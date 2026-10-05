# Elmish, Bolero, and RazorConsole: Architecture and Connector Design

## 1. Executive Summary

This document provides a comprehensive architectural investigation into:
1. **RazorConsole** (`/home/shanoaice/build/RazorConsole`): How it hosts Razor components, runs its custom Blazor `Renderer`, translates virtual DOM diffs to Spectre.Console renderables, manages terminal focus and input, and triggers re-renders.
2. **Bolero** (`/home/shanoaice/build/Bolero`): How it bridges Elmish (`Program<'arg, 'model, 'msg, 'view>`) with Microsoft Blazor's `ComponentBase`, `RenderHandle`, and `RenderTreeBuilder`, marshaling state changes via `InvokeAsync(this.StateHasChanged)`.
3. **Elmish Model Translation**: A definitive analysis of Elmish's core design proving that Elmish core is entirely UI-agnostic and produces no intermediate representation (IR) or virtual DOM, leaving `'view` unconstrained. We compare how major connectors (Fable.React, Bolero, Elmish.WPF, Avalonia.FuncUI) materialize UI.
4. **Architecture & Concrete Code Design for `RazorConsole.Elmish`**: A complete specification and implementation design for an Elmish connector targeting RazorConsole, including component lifecycle, dispatch marshaling, event integration, a type-safe F# DSL, and generic host integration.

---

## 2. In-Depth Analysis of RazorConsole

RazorConsole enables rendering Blazor / Razor components directly into terminal windows using [Spectre.Console](https://spectreconsole.net/).

```
+---------------------------------------------------------------------------------------+
|                                    RazorConsole Host                                  |
|                                                                                       |
|   +-----------------------+           +-------------------------------------------+   |
|   | Microsoft Generic Host| --------> | ComponentService<TComponent>              |   |
|   +-----------------------+           | (IHostedService / BackgroundService)      |   |
|                                       +---------------------+---------------------+   |
|                                                             |                         |
|                                                             v                         |
|   +-------------------------------------------------------------------------------+   |
|   |                        ConsoleRenderer : Renderer                             |   |
|   |                                                                               |   |
|   |   Dispatcher: Dispatcher.CreateDefault()                                      |   |
|   |   Component Roots: Dictionary<int, VNode>                                     |   |
|   |                                                                               |   |
|   |   MountComponentAsync() --------> RenderRootComponentAsync(componentId)      |   |
|   |                                               |                               |   |
|   |                                               v                               |   |
|   |   UpdateDisplayAsync(in RenderBatch batch)    |                               |   |
|   |     +-- ApplyComponentEdits() <---------------+                               |   |
|   |     +-- Updates in-memory VNode tree                                          |   |
|   |     +-- CreateSnapshot()                                                      |   |
|   |           |                                                                   |   |
|   |           v                                                                   |   |
|   |     TranslationContext / WidgetTranslationContext                             |   |
|   |           |                                                                   |   |
|   |           v                                                                   |   |
|   |     Spectre.Console IRenderable tree                                          |   |
|   |           |                                                                   |   |
|   |           +---------> EnqueueObserverNotification(snapshot)                   |   |
|   +-----------------------------------------------+-------------------------------+   |
|                                                   |                                   |
|                                                   v                                   |
|   +-------------------------------------------------------------------------------+   |
|   |                 ConsoleLiveDisplayContext : IObserver<RenderSnapshot>         |   |
|   |                                                                               |   |
|   |   OnNext(snapshot) -> UpdateView()                                            |   |
|   |     +-- VdomDiffService.Diff(previousRoot, currentRoot)                       |   |
|   |     +-- TryApplyMutations() (incremental updates on canvas)                   |   |
|   |     +-- Fallback: LiveDisplayCanvas.UpdateTarget(IRenderable)                 |   |
|   +-------------------------------------------------------------------------------+   |
|                               |                                                       |
|                               v                                                       |
|   +-------------------------------------------------------------------------------+   |
|   |                     Spectre.Console LiveDisplay Canvas                        |   |
|   |                        (Terminal ANSI Output)                                 |   |
|   +-------------------------------------------------------------------------------+   |
|           ^                                                       |                   |
|           | Terminal Key / Mouse Input                            |                   |
|           |                                                       |                   |
|   +-------+--------------------+                                  |                   |
|   | KeyboardEventManager       | <--------------------------------+                   |
|   | MouseEventManager          |                                                      |
|   | FocusManager               |                                                      |
|   +-------------+--------------+                                                      |
|                 |                                                                     |
|                 +---> ConsoleRenderer.DispatchEventAsync(handlerId, eventArgs)        |
|                         |                                                             |
|                         v                                                             |
|                       base.DispatchEventAsync(...) -> Blazor Component EventCallback  |
+---------------------------------------------------------------------------------------+
```

### 2.1 Application Hosting and Entry Point
The hosting entry point is defined in `/home/shanoaice/build/RazorConsole/src/RazorConsole.Core/AppHost.cs`:
- **`UseRazorConsole<TComponent>`** (`AppHost.cs:29-42`):
  Extension method on `IHostBuilder` (and `IHostApplicationBuilder`, lines 51-65) with constraint `where TComponent : IComponent`.
- **`RegisterDefaults<TComponent>`** (`AppHost.cs:67-78`):
  Calls `services.AddRazorConsoleServices()` and registers `ComponentService<TComponent>` as an `IHostedService` (`AppHost.cs:71`).
- **`ComponentService<TComponent>`** (`AppHost.cs:81-161`):
  A `BackgroundService` that coordinates the lifecycle:
  1. Calls `RenderComponentInternalAsync` (`AppHost.cs:140-158`), which executes:
     ```csharp
     var snapshot = await consoleRenderer.MountComponentAsync<TComponent>(parameterView, cancellationToken).ConfigureAwait(false);
     return ConsoleViewResult.FromSnapshot(snapshot);
     ```
  2. Instantiates `ConsoleLiveDisplayContext` (`AppHost.cs:125`):
     ```csharp
     using var liveContext = new ConsoleLiveDisplayContext(
         new LiveDisplayCanvas(options.ConsoleLiveDisplayOptions, AnsiConsole.Console),
         consoleRenderer, terminalMonitor, null);
     ```
  3. Subscribes `FocusManager` to the renderer (`AppHost.cs:126`):
     ```csharp
     using var _ = consoleRenderer.Subscribe(focusManager);
     using var focusSession = focusManager.BeginSession(liveContext, initialView, token);
     ```
  4. Starts background input loops:
     ```csharp
     var keyListenerTask = keyboardEventManager.RunAsync(token); // AppHost.cs:129
     if (options.EnableTerminalResizing) { terminalMonitor.Start(token); } // AppHost.cs:132
     ```
  5. Waits for cancellation while keeping the terminal alive (`AppHost.cs:137`).

### 2.2 `ConsoleRenderer`: Blazor Renderer Specialization
The core rendering engine lives in `/home/shanoaice/build/RazorConsole/src/RazorConsole.Core/Rendering/ConsoleRenderer.cs`:
- **Inheritance & Interfaces** (`ConsoleRenderer.cs:20-32`):
  ```csharp
  internal sealed class ConsoleRenderer(...)
      : Renderer(services, loggerFactory),
        IObservable<ConsoleRenderer.RenderSnapshot>
  ```
  `ConsoleRenderer` inherits directly from `Microsoft.AspNetCore.Components.RenderTree.Renderer`.
- **Dispatcher** (`ConsoleRenderer.cs:35, 56`):
  ```csharp
  private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
  public override Dispatcher Dispatcher => _dispatcher;
  ```
  All component lifecycle calls, parameter setting, and event dispatches are routed through this `Dispatcher`.
- **Mounting Root Components** (`ConsoleRenderer.cs:83-117`):
  ```csharp
  var componentId = AssignRootComponentId(component);
  _rootComponentId = componentId;
  await Dispatcher.InvokeAsync(async () =>
  {
      await RenderRootComponentAsync(componentId, parameters).ConfigureAwait(false);
  }).ConfigureAwait(false);
  ```
  `AssignRootComponentId` and `RenderRootComponentAsync` are protected methods on `Microsoft.AspNetCore.Components.RenderTree.Renderer`.

### 2.3 Render Loop & Batch Diff Processing
When any component triggers a state update, Blazor executes `RenderQueue` and invokes `UpdateDisplayAsync`:
- **`UpdateDisplayAsync(in RenderBatch batch)`** (`ConsoleRenderer.cs:150-198`):
  1. Iterates through `batch.UpdatedComponents` (`ConsoleRenderer.cs:155-170`):
     ```csharp
     for (var i = 0; i < updatedComponentsCount; i++)
     {
         var diff = updatedComponentsArray[i];
         ApplyComponentEdits(batch, diff);
     }
     ```
  2. Removes disposed components from `_componentRoots` (`ConsoleRenderer.cs:173-180`).
  3. Creates an updated snapshot: `var snapshot = CreateSnapshot();` (`ConsoleRenderer.cs:182`).
  4. Notifies subscribers: `EnqueueObserverNotification(snapshot);` (`ConsoleRenderer.cs:187`).
- **`ApplyComponentEdits`** (`ConsoleRenderer.cs:207-241`):
  Maintains an in-memory Virtual DOM tree composed of `VNode` instances (`ConsoleRenderer.cs:33`: `Dictionary<int, VNode> _componentRoots`).
  It decodes each `RenderTreeEdit` from Blazor:
  - `RenderTreeEditType.PrependFrame` -> `ApplyPrependFrameEdit` (inserts child element/text into `VNode` parent)
  - `RenderTreeEditType.RemoveFrame` -> `ApplyRemoveFrameEdit` (removes child `VNode`)
  - `RenderTreeEditType.SetAttribute` -> `ApplySetAttributeEdit` (sets attribute or records event callback ID)
  - `RenderTreeEditType.RemoveAttribute` -> `ApplyRemoveAttributeEdit`
  - `RenderTreeEditType.UpdateText` -> `ApplyUpdateTextEdit`
  - `RenderTreeEditType.StepIn` / `StepOut` -> navigates the `_cursor: Stack<VNode>`

### 2.4 Translation to Spectre.Console Renderables
In `CreateSnapshot()` (`ConsoleRenderer.cs:480-520`):
1. **Root VNode**: Finds the `VNode` for `_rootComponentId`.
2. **Translation Pipeline**:
   - In default mode: calls `_translationContext.Translate(rootNode)` (`ConsoleRenderer.cs:505`).
     The `TranslationContext` contains ordered middlewares (`ITranslationMiddleware`, registered in `RazorConsoleServiceCollectionExtensions.cs:73-141`):
     - `PanelElementTranslator`: converts `<panel>` / `<Box Class="panel">` to `Spectre.Console.Panel`.
     - `HtmlTableElementTranslator`: converts `<table>` to `Spectre.Console.Table`.
     - `ButtonElementTranslator`: builds focusable interactive Spectre renderables.
     - `TextNodeTranslator` & `HtmlInlineTextElementTranslator`: formats text and inline markup.
     - `ModalTranslator` & `AbsolutePositionMiddleware`: collects overlays into `CollectedOverlays`.
   - In widget layout mode (`_options.RenderingPipeline == RazorConsoleRenderingPipeline.WidgetLayout`, `ConsoleRenderer.cs:497-500`):
     Calls `_widgetTranslationContext.Translate(rootNode)`, executes `_layoutEngine.Layout(widgetRoot, constraints)`, and paints via `layoutResult.PaintToRenderable()` (`ConsoleRenderer.cs:527-529`).
3. Returns `RenderSnapshot(rootNode, finalRenderable, animatedRenderables)`.

### 2.5 Live Display & Screen Updates
`ConsoleLiveDisplayContext` (`ConsoleLiveDisplayContext.cs:17-104`) implements `IObserver<ConsoleRenderer.RenderSnapshot>`:
- `OnNext(RenderSnapshot value)` calls `UpdateView(ConsoleViewResult.FromSnapshot(value))` (`ConsoleLiveDisplayContext.cs:186-187`).
- `UpdateView` checks `VdomDiffService.Diff(previousRoot, currentRoot)` (`ConsoleLiveDisplayContext.cs:78`).
  - If changes can be applied as surgical terminal canvas text/attribute mutations, it calls `TryApplyMutations(diff)` (`ConsoleLiveDisplayContext.cs:85, 195-219`).
  - Otherwise, it updates the live display target: `_canvas.UpdateTarget(view.Renderable)` (`ConsoleLiveDisplayContext.cs:88, 96`).
  - `_canvas` (`LiveDisplayCanvas.cs`) wraps Spectre.Console's `LiveDisplay` or standard console streams.

### 2.6 Event Dispatching & State Change Triggers
- **Keyboard & Mouse Events**:
  - `KeyboardEventManager` (`KeyboardEventManager.cs:83-144`) reads terminal keys from `IConsoleInput` in a loop.
  - Looks up the focused `VNode`'s event handler ID (`ulong AttributeEventHandlerId`).
  - Dispatches via `ConsoleRenderer.DispatchEventAsync` (`ConsoleRenderer.cs:838-861`):
    ```csharp
    internal Task DispatchEventAsync(ulong handlerId, EventArgs eventArgs)
    {
        return Dispatcher.InvokeAsync(() => base.DispatchEventAsync(handlerId, default, eventArgs));
    }
    ```
- **Component Base Class**:
  - Components implement `Microsoft.AspNetCore.Components.IComponent`.
  - Most components inherit from `Microsoft.AspNetCore.Components.ComponentBase` (e.g., `Panel.razor`, `TextInput.razor`, `Box.razor`, `Markup.razor`).
  - Re-rendering is triggered when:
    1. A component invokes `StateHasChanged()` (protected method on `ComponentBase`).
    2. An event handler (`EventCallback` / `EventCallback<T>`) completes.
    3. New parameters are supplied by a parent component via `SetParametersAsync`.
  - `StateHasChanged()` queues a render request to the renderer's `Dispatcher`, which calls `BuildRenderTree(builder)` and passes the resulting diffs to `ConsoleRenderer.UpdateDisplayAsync`.

---

## 3. In-Depth Analysis of Bolero

Bolero brings the Elmish (MVU) architecture to Blazor. The primary sources reside in `/home/shanoaice/build/Bolero/src/Bolero`.

```
+-----------------------------------------------------------------------------------+
|                                Bolero Architecture                                |
|                                                                                   |
|   +---------------------------------------------------------------------------+   |
|   |                    Program<'model, 'msg> Type Alias                       |   |
|   |                                                                           |   |
|   |   Program<ProgramComponent<'model, 'msg>, 'model, 'msg, Node>             |   |
|   |                                                                           |   |
|   |   'arg  = ProgramComponent<'model, 'msg>                                 |   |
|   |   'view = Node  (= delegate of obj * RenderTreeBuilder * int -> int)      |   |
|   +-------------------------------------+-------------------------------------+   |
|                                         |                                         |
|                                         v                                         |
|   +---------------------------------------------------------------------------+   |
|   |        ProgramComponent<'model, 'msg> : Component<'model> : Component     |   |
|   |                            : ComponentBase                                |   |
|   |                                                                           |   |
|   |   [OnInitializedAsync]                                                    |   |
|   |     1. let initModel, initCmd = Program.init program this                 |   |
|   |     2. view <- Program.view program initModel dispatch                    |   |
|   |     3. Intercept setState via Program.map:                                |   |
|   |          setState <- fun model dispatch ->                                |   |
|   |             if ShouldRender(oldModel, model) then                         |   |
|   |                 this.ForceSetState(model, dispatch)                       |   |
|   |                                                                           |   |
|   |   [OnAfterRenderAsync(firstRender)]                                       |   |
|   |     Program.runWith this program   (starts Elmish loop)                   |   |
|   |                                                                           |   |
|   |   [ForceSetState(model, dispatch)]                                        |   |
|   |     1. view <- Program.view program model dispatch                        |   |
|   |     2. oldModel <- Some model                                             |   |
|   |     3. this.InvokeAsync(this.StateHasChanged)  <--- BLASOR DISPATCHER!    |   |
|   |                                                                           |   |
|   |   [BuildRenderTree(builder)]                                              |   |
|   |     this.Render().Invoke(this, builder, 0) |> ignore                      |   |
|   |     (where this.Render() returns `view : Node`)                           |   |
|   +-------------------------------------+-------------------------------------+   |
|                                         |                                         |
|                                         v                                         |
|   +---------------------------------------------------------------------------+   |
|   |                     Bolero.Node / RenderTreeBuilder                       |   |
|   |                                                                           |   |
|   |   Node = delegate of (comp: obj) * (tb: RenderTreeBuilder) * (seq: int)   |   |
|   |          -> nextSeq: int                                                  |   |
|   |                                                                           |   |
|   |   Node.Elt "div" [ attrs ] [ children ]                                   |   |
|   |     -> tb.OpenElement(seq, "div")                                         |   |
|   |     -> invoke attrs & children                                            |   |
|   |     -> tb.CloseElement()                                                  |   |
|   |                                                                           |   |
|   |   comp<TComponent> { "Param" => val }                                     |   |
|   |     -> tb.OpenComponent<TComponent>(seq)                                  |   |
|   |     -> tb.AddAttribute(seq+1, "Param", box val)                           |   |
|   |     -> tb.CloseComponent()                                                |   |
|   |                                                                           |   |
|   |   Event Callbacks (attr.callback<'T>):                                    |   |
|   |     -> EventCallback.Factory.Create(receiver, Action<'T>(fun e ->         |   |
|   |            dispatch (Msg e)))                                             |   |
|   +---------------------------------------------------------------------------+   |
+-----------------------------------------------------------------------------------+
```

### 3.1 The Representation of `Node` and `Attr`
In `/home/shanoaice/build/Bolero/src/Bolero/NodeTypes.fs`:
```fsharp
// NodeTypes.fs:10-14
type Attr = delegate of obj * RenderTreeBuilder * int -> int
type Node = delegate of obj * RenderTreeBuilder * int -> int
```
Both `Attr` and `Node` are F# delegates accepting:
1. `obj`: The owning / receiving Blazor component instance.
2. `RenderTreeBuilder`: Blazor's low-level mutable builder.
3. `int`: The sequence number index.
They return `int`: the next available sequence number.

In `/home/shanoaice/build/Bolero/src/Bolero/Node.fs`:
- `Node.Empty()` (`Node.fs:58`): `Node(fun _ _ i -> i)`
- `Node.Elt` (`Node.fs:66-82`):
  ```fsharp
  let inline Elt name (attrs: seq<Attr>) (children: seq<Node>) = Node(fun comp tb i ->
      tb.OpenElement(i, name)
      // apply CSS scope if applicable...
      let mutable i = i + 2
      for attr in attrs do i <- attr.Invoke(comp, tb, i)
      for node in children do i <- node.Invoke(comp, tb, i)
      tb.CloseElement()
      i)
  ```
- `Node.Text` (`Node.fs:88-90`): `tb.AddContent(i, text); i + 1`
- `Node.RawHtml` (`Node.fs:96-98`): `tb.AddMarkupContent(i, html); i + 1`
- `Node.Fragment` (`Node.fs:161-165`):
  ```fsharp
  let inline Fragment (fragment: RenderFragment) = Node(fun _ tb i ->
      tb.OpenRegion(i)
      fragment.Invoke(tb)
      tb.CloseRegion()
      i + 1)
  ```

### 3.2 The `ProgramComponent` Base Class
In `/home/shanoaice/build/Bolero/src/Bolero/Components.fs`:
- **Hierarchy**:
  - `Component()` (`Components.fs:34-49`): Inherits `Microsoft.AspNetCore.Components.ComponentBase()`.
    Overrides `BuildRenderTree`:
    ```fsharp
    override this.BuildRenderTree(builder) =
        base.BuildRenderTree(builder)
        this.Render().Invoke(this, builder, 0) |> ignore
    ```
  - `Component<'model>()` (`Components.fs:54-71`): Adds `ShouldRender(oldModel, newModel)` with customizable `[<Parameter>] Equal`.
  - `ProgramComponent<'model, 'msg>()` (`Components.fs:123-343`): Inherits `Component<'model>()`.
- **Specializing the Elmish Program**:
  ```fsharp
  // Components.fs:118
  type Program<'model, 'msg> = Program<ProgramComponent<'model, 'msg>, 'model, 'msg, Node>
  ```
  Elmish's `'arg` is bound to the component instance (`ProgramComponent<'model, 'msg>`), and `'view` is bound to Bolero's `Node`.

### 3.3 State Synchronization: `dispatch` to `StateHasChanged()`
The critical link connecting Elmish's update cycle to Blazor's render loop is `ForceSetState`:
```fsharp
// Components.fs:200-210
member private this.ForceSetState(model, dispatch) =
    view <- Program.view program model dispatch
    oldModel <- Some model
    this.InvokeAsync(this.StateHasChanged) |> ignore
    router |> Option.iter (fun router ->
        let newUri = router.GetRoute(model).TrimStart('/')
        let oldUri = this.GetCurrentUri()
        if newUri <> oldUri then
            try this.NavigationManager.NavigateTo(newUri)
            with _ -> ()
    )
```
Notice:
1. `view <- Program.view program model dispatch`: Bolero computes the new `Node` delegate immediately when the model updates.
2. `this.InvokeAsync(this.StateHasChanged) |> ignore`: Bolero calls `ComponentBase.InvokeAsync(Action)` to marshal onto Blazor's renderer dispatcher, and calls `StateHasChanged()`.
3. Blazor's dispatcher queues the component for rendering, which calls `Component.BuildRenderTree(builder)`, which in turn executes `this.Render().Invoke(this, builder, 0)`.
4. In `ProgramComponent.Render()` (`Components.fs:329-330`):
   ```fsharp
   override this.Render() = view
   ```
   It returns the newly computed `view: Node`, which writes the updated elements and components directly into Blazor's `RenderTreeBuilder`.

### 3.4 Lifecycle Orchestration & Interactive Modes
In `Components.fs:213-287` and `src/Bolero/ProgramRun.fs`:
- During `OnInitializedAsync()`:
  1. Bolero runs `let initModel, initCmd = Program.init theProgram this` (`Components.fs:215`).
  2. Computes the initial view: `view <- Program.view theProgram initModel (fun cmd -> dispatch cmd)`.
  3. Intercepts Elmish's `setState`:
     ```fsharp
     setState <- fun model dispatch ->
         match oldModel with
         | Some oldModel when this.ShouldRender(oldModel, model) -> this.ForceSetState(model, dispatch)
         | _ -> ()
     ```
  4. Wraps `theProgram` using `Program.map` (`Components.fs:231-237` or `Components.fs:264-271`) to inject `setState` and capture the real `dispatch` function.
- During `OnAfterRenderAsync(firstRender)` (`Components.fs:308-327`):
  On the first render, it calls `Program.runWith this program` (in .NET 9+, line 312) or `runProgramLoop()` (in .NET 8, line 314, implemented in `ProgramRun.fs:61-121`).
  This starts the Elmish event loop, subscriptions, and initial commands only after the component is rendered.

### 3.5 Embedding Blazor Components in Bolero Views
Bolero allows embedding any Blazor component via `comp<'T>` (`src/Bolero.Html/Html.fs:179` and `src/Bolero.Html/Builders.fs:832-837`):
```fsharp
let inline comp<'T when 'T :> IComponent> = ComponentBuilder<'T>()
```
In `Builders.fs`:
```fsharp
b.OpenComponent<'T>(i)
let i = x.Invoke(c, b, i + 1)
b.CloseComponent()
```
And event callbacks are created in `src/Bolero.Html/Html.fs:915-918`:
```fsharp
let inline callback<'T> (name: string) ([<InlineIfLambda>] value: 'T -> unit) =
    Attr(fun receiver builder sequence ->
        builder.AddAttribute<'T>(sequence, name, EventCallback.Factory.Create(receiver, Action<'T>(value)))
        sequence + 1)
```
This produces a Blazor `EventCallback` that invokes the F# lambda, which in turn calls Elmish's `dispatch msg`.

---

## 4. Synthesis: How Elmish Translates Models & Ecosystem Comparison

### 4.1 Does Elmish Core Translate Models to an Intermediate Representation?
**No.** Elmish core is entirely UI-agnostic and performs no model translation, virtual DOM diffing, or layout computation.

#### Direct Code Evidence from Elmish Core (`/home/shanoaice/build/elmish/src/program.fs`):
```fsharp
// src/program.fs:12-20
type Program<'arg, 'model, 'msg, 'view> = private {
    init : 'arg -> 'model * Cmd<'msg>
    update : 'msg -> 'model -> 'model * Cmd<'msg>
    subscribe : 'model -> Sub<'msg>
    view : 'model -> Dispatch<'msg> -> 'view
    setState : 'model -> Dispatch<'msg> -> unit
    onError : (string*exn) -> unit
    termination : ('msg -> bool) * ('model -> unit)
}
```
In `Program.runWithDispatch` (`src/program.fs:160-203`):
```fsharp
// src/program.fs:187-192
let (model',cmd') = program.update msg state
let sub' = program.subscribe model'
program.setState model' dispatch'
activeSubs <- Subs.diff activeSubs sub' |> Subs.Fx.change program.onError dispatch'
cmd' |> Cmd.exec (fun ex -> program.onError (sprintf "Error handling the message: %A" msg, ex)) dispatch'
state <- model'
```
Key observations:
1. `'view` is an unrestricted generic type parameter. Elmish core never inspects, casts, or stores `'view`.
2. In default `Program.mkProgram` (`src/program.fs:34`), `setState` is simply:
   ```fsharp
   setState = fun model -> view model >> ignore
   ```
3. Elmish's only responsibility is managing the message queue (`RingBuffer 10`, line 164), invoking `update`, managing active `Sub<'msg>`, and notifying `setState model dispatch`.
4. All UI generation, intermediate representations, reconciliation, and rendering are left entirely to the connector.

### 4.2 Cross-Connector Comparison Table

| Feature / Dimension | Fable.React / Feliz | Bolero | Elmish.WPF | Avalonia.FuncUI | RazorConsole.Elmish (Proposed) |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`'view` Type Parameter** | `ReactElement` | `Bolero.Node` (`delegate of obj * RenderTreeBuilder * int -> int`) | `Bindings<'model, 'msg>` (View-Model binding spec) | `IView` (Virtual control tree) | `RenderFragment` (or `View` delegate writing to `RenderTreeBuilder`) |
| **Host Environment** | Browser DOM via Webpack/Vite | Microsoft Blazor (`ComponentBase`) | WPF (`Window` / `FrameworkElement`) | Avalonia UI Control (`ElmishHostControl`) | RazorConsole (`ConsoleRenderer` : `Renderer`) |
| **Intermediate Representation (IR)** | React Virtual DOM (`ReactElement`) | Blazor `RenderTreeFrame` array in `RenderTreeBuilder` | None (Direct WPF DependencyProperty bindings) | FuncUI Virtual Control Tree (`IView`) | Blazor `RenderTreeFrame` -> RazorConsole `VNode` tree |
| **Diffing & Reconciliation** | React reconciliation engine (fiber diffing) | Blazor `Renderer` (`RenderBatch` / `RenderTreeDiff`) | WPF native layout engine & `INotifyPropertyChanged` | Custom FuncUI diff/patch engine on Avalonia controls | Blazor diff -> `ConsoleRenderer.UpdateDisplayAsync` -> `VdomDiffService` |
| **Screen Materialization** | Browser DOM mutations (`document.createElement`) | Browser DOM via WebAssembly/SignalR | DirectX / MilCore native rendering | Skia / Direct2D via Avalonia visual tree | Spectre.Console `IRenderable` -> ANSI sequences on terminal canvas |
| **Thread Marshaling** | Single-threaded JS event loop | `ComponentBase.InvokeAsync` onto Blazor `Dispatcher` | `Dispatcher.Invoke` onto WPF UI Dispatcher | `Dispatcher.UIThread.InvokeAsync` | `ComponentBase.InvokeAsync` onto `ConsoleRenderer.Dispatcher` |
| **Event Routing** | Synthetic React events | Blazor `EventCallback` -> `dispatch` | WPF `ICommand` & event triggers -> `dispatch` | Native Avalonia events -> `dispatch` | Terminal input -> `KeyboardEventManager` -> `DispatchEventAsync` -> `EventCallback` -> `dispatch` |

---

## 5. Architecture & Code Design for a RazorConsole Elmish Connector

### 5.1 Architectural Evaluation: Bolero vs. Native `RazorConsole.Elmish`

#### Can Bolero run on RazorConsole as-is?
**Yes, in theory**, because Bolero produces a standard Blazor `ComponentBase` (`ProgramComponent`) that outputs frames to `RenderTreeBuilder`. RazorConsole's `ConsoleRenderer` is a full Blazor `Renderer` that executes `BuildRenderTree` and parses component frames.
However, Bolero has significant drawbacks for terminal development:
1. **Web Baggage**: Bolero unconditionally injects web services into `ProgramComponent` (`[<Inject>] IJSRuntime`, `[<Inject>] INavigationInterception`). These are designed for browser DOM / JS interop.
2. **HTML Bias**: Bolero's core DSL (`Bolero.Html`) is centered around HTML5 tags (`<div>`, `<p>`, `<table>`, `<input>`). While RazorConsole translates some HTML tags, native terminal features like borders (`BoxBorder.Rounded`), padding, colors, figlets, spinners, charts, and focus keys require RazorConsole-specific components (`<Panel>`, `<Box>`, `<TextInput>`, `<Select>`, `<SpectreTable>`). In Bolero, you would have to write awkward `comp<Panel> { ... }` wrappers for everything.

#### Conclusion: A Native `RazorConsole.Elmish` Connector
A clean, lightweight, native `RazorConsole.Elmish` library provides:
- Zero web or JS dependencies.
- Direct alignment with RazorConsole's terminal rendering pipeline (`TranslationContext` & `WidgetLayout`).
- First-class support for terminal keyboard focus (`FocusManager`).
- An idiomatic, type-safe F# DSL for Spectre.Console / RazorConsole widgets.

---

### 5.2 Concrete Implementation Design

Here is the complete production-grade architectural design for `RazorConsole.Elmish`.

#### Module 1: The Core Program Component (`ElmishProgramComponent.fs`)
```fsharp
namespace RazorConsole.Elmish

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Rendering
open Elmish

/// <summary>
/// A Blazor component that hosts an Elmish (MVU) program inside RazorConsole.
/// </summary>
[<AbstractClass>]
type ElmishProgramComponent<'model, 'msg>() as this =
    inherit ComponentBase()

    let mutable currentModel: 'model option = None
    let mutable currentView: RenderFragment = RenderFragment(fun _ -> ())
    let mutable dispatch: Dispatch<'msg> = ignore
    let mutable program: Program<ElmishProgramComponent<'model, 'msg>, 'model, 'msg, RenderFragment> option = None
    let mutable started = false

    /// <summary>The Elmish program definition.</summary>
    abstract Program: Program<ElmishProgramComponent<'model, 'msg>, 'model, 'msg, RenderFragment>

    /// <summary>Dispatches a message to the Elmish loop safely on the Blazor dispatcher.</summary>
    member this.Dispatch(msg: 'msg) =
        this.InvokeAsync(fun () -> dispatch msg) |> ignore

    /// <summary>Compares old and new models to avoid redundant renders.</summary>
    abstract ShouldRender: oldModel: 'model * newModel: 'model -> bool
    default _.ShouldRender(oldModel, newModel) =
        not (obj.ReferenceEquals(oldModel, newModel))

    override this.OnInitialized() =
        base.OnInitialized()
        let theProgram = this.Program

        // Initialize state
        let initModel, initCmd = Program.init theProgram this
        currentModel <- Some initModel
        currentView <- Program.view theProgram initModel this.Dispatch

        // Set up the state change hook
        let setState (model: 'model) (d: Dispatch<'msg>) =
            let shouldUpdate =
                match currentModel with
                | Some oldModel -> this.ShouldRender(oldModel, model)
                | None -> true

            if shouldUpdate then
                currentModel <- Some model
                currentView <- Program.view theProgram model d
                this.InvokeAsync(this.StateHasChanged) |> ignore

        // Map program to capture dispatch and wire setState
        let mappedProgram =
            theProgram
            |> Program.map
                (fun init arg ->
                    let m, c = init arg
                    m, [ fun d -> dispatch <- d ] @ c)
                id
                id
                (fun _ model d -> setState model d)
                id
                id

        program <- Some mappedProgram

    override this.OnAfterRenderAsync(firstRender: bool) =
        task {
            if firstRender && not started then
                started <- true
                match program with
                | Some p -> Program.runWith this p
                | None -> ()
        }

    override this.BuildRenderTree(builder: RenderTreeBuilder) =
        base.BuildRenderTree(builder)
        currentView.Invoke(builder)

    interface IDisposable with
        member _.Dispose() =
            // Clean up subscriptions or program resources
            ()
```

---

#### Module 2: The Type-Safe F# Terminal View DSL (`View.fs`)
```fsharp
namespace RazorConsole.Elmish

open System
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Rendering
open Spectre.Console
open RazorConsole.Components

/// <summary>
/// Functions for constructing declarative RazorConsole render trees.
/// </summary>
module View =

    type View = RenderFragment

    /// <summary>Empty view node.</summary>
    let empty : View = RenderFragment(fun _ -> ())

    /// <summary>Renders plain text content.</summary>
    let text (content: string) : View =
        RenderFragment(fun b -> b.AddContent(0, content))

    /// <summary>Renders Spectre.Console markup text.</summary>
    let markup (content: string) : View =
        RenderFragment(fun b ->
            b.OpenComponent<Markup>(0)
            b.AddAttribute(1, "Content", content)
            b.CloseComponent()
        )

    /// <summary>Renders a Panel component wrapping child content.</summary>
    let panel
        (title: string option)
        (border: BoxBorder option)
        (borderColor: Color option)
        (child: View) : View =
        RenderFragment(fun b ->
            b.OpenComponent<Panel>(0)
            title |> Option.iter (fun t -> b.AddAttribute(1, "Title", t))
            border |> Option.iter (fun brd -> b.AddAttribute(2, "Border", brd))
            borderColor |> Option.iter (fun c -> b.AddAttribute(3, "BorderColor", Nullable c))
            b.AddAttribute(4, "ChildContent", child)
            b.CloseComponent()
        )

    /// <summary>Renders an interactive TextInput component with two-way binding mapped to Elmish messages.</summary>
    let textInput
        (value: string)
        (placeholder: string option)
        (onChanged: string -> 'msg)
        (dispatch: Dispatch<'msg>) : View =
        RenderFragment(fun b ->
            b.OpenComponent<TextInput>(0)
            b.AddAttribute(1, "Value", value)
            placeholder |> Option.iter (fun p -> b.AddAttribute(2, "Placeholder", p))
            let callback = EventCallback.Factory.Create<string>(
                b,
                Action<string>(fun v -> dispatch (onChanged v))
            )
            b.AddAttribute(3, "ValueChanged", callback)
            b.CloseComponent()
        )

    /// <summary>Renders vertical rows of child views.</summary>
    let rows (children: View list) : View =
        RenderFragment(fun b ->
            b.OpenComponent<Rows>(0)
            let childContent = RenderFragment(fun cb ->
                let mutable seq = 0
                for child in children do
                    cb.AddContent(seq, child)
                    seq <- seq + 1
            )
            b.AddAttribute(1, "ChildContent", childContent)
            b.CloseComponent()
        )

    /// <summary>Renders horizontal columns of child views.</summary>
    let columns (children: View list) : View =
        RenderFragment(fun b ->
            b.OpenComponent<Columns>(0)
            let childContent = RenderFragment(fun cb ->
                let mutable seq = 0
                for child in children do
                    cb.AddContent(seq, child)
                    seq <- seq + 1
            )
            b.AddAttribute(1, "ChildContent", childContent)
            b.CloseComponent()
        )

    /// <summary>Renders a Figlet banner.</summary>
    let figlet (text: string) (color: Color option) : View =
        RenderFragment(fun b ->
            b.OpenComponent<Figlet>(0)
            b.AddAttribute(1, "Text", text)
            color |> Option.iter (fun c -> b.AddAttribute(2, "Color", Nullable c))
            b.CloseComponent()
        )

    /// <summary>Combines multiple views sequentially.</summary>
    let concat (views: View list) : View =
        RenderFragment(fun b ->
            let mutable seq = 0
            for v in views do
                b.AddContent(seq, v)
                seq <- seq + 1
        )
```

---

#### Module 3: Generic Host Integration (`HostExtensions.fs`)
```fsharp
namespace RazorConsole.Elmish

open System.Runtime.CompilerServices
open Microsoft.Extensions.Hosting
open RazorConsole.Core

[<Extension>]
type HostBuilderExtensions =

    /// <summary>
    /// Configures the HostBuilder to run a RazorConsole Elmish application.
    /// </summary>
    [<Extension>]
    static member UseRazorConsoleElmish<'TComponent, 'model, 'msg
        when 'TComponent :> ElmishProgramComponent<'model, 'msg> and 'TComponent : (new : unit -> 'TComponent)>
        (builder: IHostBuilder) =
        builder.UseRazorConsole<'TComponent>()

    /// <summary>
    /// Configures the HostApplicationBuilder to run a RazorConsole Elmish application.
    /// </summary>
    [<Extension>]
    static member UseRazorConsoleElmish<'TComponent, 'model, 'msg
        when 'TComponent :> ElmishProgramComponent<'model, 'msg> and 'TComponent : (new : unit -> 'TComponent)>
        (builder: IHostApplicationBuilder) =
        builder.UseRazorConsole<'TComponent>()
```

---

#### Module 4: Complete Working Example Application
Here is what a developer writes to build a full terminal app:
```fsharp
namespace MyApp

open System
open Spectre.Console
open Elmish
open RazorConsole.Elmish
open RazorConsole.Elmish.View

// 1. Model
type Model = {
    UserName: string
    Counter: int
    Status: string
}

// 2. Messages
type Msg =
    | NameChanged of string
    | Increment
    | Decrement
    | Reset

// 3. Init & Update
module App =
    let init () : Model * Cmd<Msg> =
        { UserName = "Terminal Explorer"; Counter = 0; Status = "Ready" }, Cmd.none

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | NameChanged name ->
            { model with UserName = name; Status = sprintf "Name set to %s" name }, Cmd.none
        | Increment ->
            { model with Counter = model.Counter + 1; Status = "Incremented" }, Cmd.none
        | Decrement ->
            { model with Counter = model.Counter - 1; Status = "Decremented" }, Cmd.none
        | Reset ->
            { model with Counter = 0; Status = "Reset to 0" }, Cmd.none

    // 4. View function producing RazorConsole View (RenderFragment)
    let view (model: Model) (dispatch: Dispatch<Msg>) : View =
        rows [
            figlet "ELMISH TUI" (Some Color.Cyan1)

            panel (Some "User Settings") (Some BoxBorder.Rounded) (Some Color.Green) (
                rows [
                    markup (sprintf "[bold]Current User:[/][yellow] %s[/]" model.UserName)
                    textInput model.UserName (Some "Enter your name...") NameChanged dispatch
                ]
            )

            panel (Some "Counter Dashboard") (Some BoxBorder.Double) (Some Color.Blue) (
                rows [
                    markup (sprintf "[bold]Count:[/] [bold green]%d[/]" model.Counter)
                    markup (sprintf "[italic]Status:[/] [grey]%s[/]" model.Status)
                    columns [
                        markup "[dim](Press Up/Down to navigate inputs)[/]"
                    ]
                ]
            )
        ]

// 5. Root Component
type TerminalAppRoot() =
    inherit ElmishProgramComponent<Model, Msg>()

    override _.Program =
        Program.mkProgram App.init App.update App.view

// 6. Program Entry Point
module Program =
    open Microsoft.Extensions.Hosting

    [<EntryPoint>]
    let main args =
        Host.CreateDefaultBuilder(args)
            .UseRazorConsoleElmish<TerminalAppRoot, _, _>()
            .Build()
            .Run()
        0
```
---

## 6. HTML Support Matrix & Computation Expression (CE) DSL Analysis

### 6.1 Exhaustive HTML Support Matrix in RazorConsole
RazorConsole translates a strictly bounded subset of HTML elements into Spectre.Console renderables via dedicated translation middlewares.

| Category | Supported HTML Tags | Primary Source Middleware | Translation Target / Behavior |
| :--- | :--- | :--- | :--- |
| **Containers / Blocks** | `<div>` | `HtmlDivElementTranslator.cs:34-68` | Translated to `BlockInlineRenderable`. Divs with special classes (`panel`, `rows`, `columns`, `grid`) or data attributes (`data-button`, `data-spacer`, `data-newline`) are delegated to specialized layout translators. |
| | `<p>` | `HtmlParagraphElementTranslator.cs:46-49` | Child nodes converted to renderables; multiple children wrapped in `Columns(Expand=false, Padding=0)`. |
| | `<blockquote>` | `HtmlBlockquoteElementTranslator.cs:44-46` | Quoted block with indent/border. |
| | `<hr>` | `HtmlHrElementTranslator.cs:28-30` | Rendered as a horizontal rule via Spectre `Rule`. |
| | `<pre>` with `<code>` | `HtmlCodeBlockElementTranslator.cs:76-78` | Syntax-highlighted code block using `SyntaxHighlightingService`. |
| **Headings** | `<h1>` through `<h6>` | `HtmlHeadingElementTranslator.cs:41-67` | Prefixed with `# `, `## `, `### ` etc. and styled with Spectre headings. |
| **Lists** | `<ul>`, `<ol>`, `<li>` | `HtmlListElementTranslator.cs:22-92` | Rendered as bulleted (`* `) or numbered (`1. `) lines. |
| **Tables** | `<table>`, `<thead>`, `<tbody>`, `<tfoot>`, `<tr>`, `<th>`, `<td>` | `HtmlTableElementTranslator.cs:163-254` | Translated to a full Spectre.Console `Table` with border styles and cells. |
| **Buttons** | `<button>` | `HtmlButtonElementTranslator.cs:44-48` | Focusable button renderable with click/key handlers. |
| **Inline Formatting** | `<strong>`, `<b>` | `HtmlInlineTextElementTranslator.cs:18-19` | `[bold]` Spectre markup |
| | `<em>`, `<i>`, `<cite>` | `HtmlInlineTextElementTranslator.cs:20-27` | `[italic]` Spectre markup |
| | `<mark>` | `HtmlInlineTextElementTranslator.cs:22` | `[black on yellow]` Spectre markup |
| | `<del>` | `HtmlInlineTextElementTranslator.cs:23` | `[strikethrough]` Spectre markup |
| | `<ins>`, `<abbr>` | `HtmlInlineTextElementTranslator.cs:24-26` | `[underline]` Spectre markup |
| | `<code>` | `HtmlInlineTextElementTranslator.cs:25` | `[indianred1 on #1f1f1f]` Spectre markup |
| | `<small>`, `<sub>`, `<sup>` | `HtmlInlineTextElementTranslator.cs:28-30` | `[dim]` Spectre markup |
| | `<q>` | `HtmlInlineTextElementTranslator.cs:33, 101` | Unicode quotes (`“...”`) |
| | `<a>` | `HtmlInlineTextElementTranslator.cs:35, 113` | Underlined link markup |

#### Behavior on Unsupported HTML Tags
If an application outputs an HTML tag not listed above (such as `<section>`, `<article>`, `<header>`, `<footer>`, `<input>`, `<select>`), RazorConsole does **not** silently drop it. Instead, **`FallbackTranslator.cs:33-55`** intercepts it and renders a red diagnostic panel directly on the terminal:
```
+-----------------------------------------------+
| [red]Untranslated VDOM[/] • <section>         |
+-----------------------------------------------+
```

---

### 6.2 The Value Proposition: Why a Computation Expression (CE) DSL for Layout?

While a pure HTML CE DSL (like Bolero's `div { ... }`) is mismatched with terminal UI development, extending a Computation Expression DSL to cover **RazorConsole layout widgets** (`panel`, `rows`, `columns`, `box`, `grid`) provides distinct architectural gains:

#### 1. Elimination of "Parameter Explosion" / Positional Argument Clutter
Terminal layout components in RazorConsole have many optional layout and styling attributes. For example, `<Box>` and `<Panel>` (`Box.razor:50-135`) support:
- `Title`, `TitleColor`, `Border`, `BorderColor`, `Padding`, `Margin`, `Width`, `Height`, `Expand`, `FillWidth`, `FillHeight`, `Horizontal`, `Vertical`.

With standard functions, you face a dilemma:
- Massive positional signatures with `option` values:
  ```fsharp
  panel (Some "Title") (Some BoxBorder.Rounded) (Some Color.Cyan) None None None (rows [ ... ])
  ```
- Or record configuration objects:
  ```fsharp
  panel { PanelConfig.Default with Title = Some "Title"; Border = BoxBorder.Rounded } [ ... ]
  ```
With a CE DSL utilizing F# `[<CustomOperation>]` attributes, configuration becomes clean, declarative, and order-independent:
```fsharp
panel {
    title "User Dashboard"
    border BoxBorder.Rounded
    borderColor Color.Cyan1
    padding (1, 0, 1, 0)
    expand

    // Children yielded directly below
    markup "[bold]Welcome back![/]"
}
```

#### 2. Syntactic Elegance Without Punctuation Clutter
Combinator trees require nested brackets and commas on every layer (`rows [ panel ... [ columns [ ... ] ] ]`).
Computation expressions replace bracket noise with clean indentation and curly braces (`rows { panel { columns { ... } } }`), mirroring the visual structure of the terminal layout.

#### 3. Native Mixing of Attributes, Children, and Control Flow
Inside a CE block, attributes (`title`, `border`), children (`markup`, `textInput`), and F# language constructs (`if ... then ... else`, `for item in items do`) live seamlessly together without wrapping sub-trees in list comprehensions.

---

### 6.3 What a TUI Application Looks Like With a Unified Layout CE DSL

Below is how an Elmish terminal application looks when authored with a unified CE DSL covering both native layout containers and supported HTML tags:

```fsharp
namespace MyConsoleApp

open System
open Spectre.Console
open Elmish
open RazorConsole.Elmish
open RazorConsole.Elmish.Dsl // Brings in panel, rows, columns, box, textInput, html, etc.

type TaskItem = { Id: int; Text: string; Done: bool }

type Model = {
    InputText: string
    Tasks: TaskItem list
    FilterDoneOnly: bool
}

type Msg =
    | SetInputText of string
    | AddTask
    | ToggleTask of int
    | ToggleFilter

let view (model: Model) (dispatch: Dispatch<Msg>) : View =
    rows {
        // Top header banner
        panel {
            title "Task Manager"
            border BoxBorder.Double
            borderColor Color.Cyan1
            padding (1, 0, 1, 0)
            expand

            markup "[bold green]Elmish + RazorConsole[/] • [dim]Press Tab to navigate focus[/]"
        }

        // Input Controls Section
        panel {
            title "New Task"
            border BoxBorder.Rounded
            borderColor Color.Yellow

            columns {
                textInput {
                    value model.InputText
                    placeholder "What needs to be done?"
                    onChange (SetInputText >> dispatch)
                    expand
                }
                button {
                    content " [bold]Add Task[/] "
                    focusedColor Color.Green
                    onClick (fun () -> dispatch AddTask)
                }
                button {
                    content (if model.FilterDoneOnly then " Show All " else " Show Completed ")
                    onClick (fun () -> dispatch ToggleFilter)
                }
            }
        }

        // Content Area: Native terminal containers mixed with supported HTML
        columns {
            // Left Column: Task List
            panel {
                title $"Tasks ({model.Tasks.Length})"
                border BoxBorder.Square
                borderColor Color.Grey
                fillWidth

                if model.Tasks.IsEmpty then
                    markup "[grey italic]No tasks found. Add one above![/]"
                else
                    rows {
                        for task in model.Tasks do
                            if not model.FilterDoneOnly || task.Done then
                                columns {
                                    let icon = if task.Done then "[green][✓][/]" else "[grey][ ][/]"
                                    let text = if task.Done then $"[strike grey]{task.Text}[/]" else $"[white]{task.Text}[/]"
                                    markup $"{icon} {text}"

                                    button {
                                        content (if task.Done then "Undo" else "Done")
                                        onClick (fun () -> dispatch (ToggleTask task.Id))
                                    }
                                }
                                html.hr
                    }
            }

            // Right Column: Summary rendered using RazorConsole's supported HTML Table translator
            panel {
                title "Summary Statistics"
                border BoxBorder.Rounded
                borderColor Color.Magenta1
                width 35

                html.table {
                    html.thead {
                        html.tr {
                            html.th "Metric"
                            html.th "Value"
                        }
                    }
                    html.tbody {
                        html.tr {
                            html.td "Total Tasks"
                            html.td (string model.Tasks.Length)
                        }
                        html.tr {
                            html.td "Completed"
                            html.td (string (model.Tasks |> List.filter (fun t -> t.Done) |> List.length))
                        }
                        html.tr {
                            html.td "Pending"
                            html.td (string (model.Tasks |> List.filter (fun t -> not t.Done) |> List.length))
                        }
                    }
                }
            }
        }
    }
```

---

## 7. Primary Source Citation Index

| Repository | File Path | Line Range | Cited Entity / Subject |
| :--- | :--- | :--- | :--- |
| **Elmish** | `src/program.fs` | 12–20 | `type Program<'arg, 'model, 'msg, 'view>` definition |
| **Elmish** | `src/program.fs` | 27–38 | `Program.mkProgram` and default `setState` definition |
| **Elmish** | `src/program.fs` | 160–203 | `Program.runWithDispatch` loop, message queue, and `setState` invocation |
| **Bolero** | `src/Bolero/NodeTypes.fs` | 10–14 | `type Attr` and `type Node` delegate definitions |
| **Bolero** | `src/Bolero/Components.fs` | 34–50 | `Component` base class and `BuildRenderTree` delegation |
| **Bolero** | `src/Bolero/Components.fs` | 54–71 | `Component<'model>` and `ShouldRender` check |
| **Bolero** | `src/Bolero/Components.fs` | 118 | `type Program<'model, 'msg> = Program<ProgramComponent<...>, 'model, 'msg, Node>` |
| **Bolero** | `src/Bolero/Components.fs` | 123–211 | `ProgramComponent` implementation and `ForceSetState` invoking `InvokeAsync(this.StateHasChanged)` |
| **Bolero** | `src/Bolero/Components.fs` | 213–287 | `OnInitializedAsync`, `setState` mapping, and interactive mode check |
| **Bolero** | `src/Bolero/Components.fs` | 308–327 | `OnAfterRenderAsync` starting `Program.runWith` |
| **Bolero** | `src/Bolero/Node.fs` | 58–98, 161–165 | `Node.Empty`, `Node.Elt`, `Node.Text`, `Node.Fragment` implementations |
| **Bolero** | `src/Bolero/ProgramRun.fs` | 50–121 | `Program'.runFirstRender` splitting init rendering from commands for Blazor lifecycle |
| **Bolero** | `src/Bolero.Html/Html.fs` | 179 | `let inline comp<'T when 'T :> IComponent>` Blazor component instantiation |
| **Bolero** | `src/Bolero.Html/Html.fs` | 915–918 | `let inline callback<'T>` wrapping `EventCallback.Factory.Create` |
| **RazorConsole** | `src/RazorConsole.Core/AppHost.cs` | 29–78 | `UseRazorConsole<TComponent>` and `RegisterDefaults` extension methods |
| **RazorConsole** | `src/RazorConsole.Core/AppHost.cs` | 81–161 | `ComponentService<TComponent>` background service and component mounting |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | 20–35 | `ConsoleRenderer` declaration, inheriting from Blazor `Renderer`, creating `_dispatcher` |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | 83–117 | `MountComponentAsync` and `RenderRootComponentAsync` dispatching |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | 150–198 | `UpdateDisplayAsync` handling `RenderBatch` diffs |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | 207–241 | `ApplyComponentEdits` transforming `RenderTreeEdit`s into `VNode` mutations |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | 480–536 | `CreateSnapshot` translating `VNode`s into Spectre `IRenderable`s |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | 838–861 | `DispatchEventAsync` routing terminal input to Blazor's base event dispatcher |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleLiveDisplayContext.cs` | 17–104 | `ConsoleLiveDisplayContext` observing snapshots, running `VdomDiffService`, and updating `LiveDisplayCanvas` |
| **RazorConsole** | `src/RazorConsole.Core/Input/KeyboardEventManager.cs` | 47–49, 83–144 | `KeyboardEventManager` input read loop and event dispatching to renderer |
| **RazorConsole** | `src/RazorConsole.Core/RazorConsoleServiceCollectionExtensions.cs` | 38–142 | Default DI service registrations for RazorConsole infrastructure |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlParagraphElementTranslator.cs` | 12–49 | HTML `<p>` translation into Spectre `Columns` |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlDivElementTranslator.cs` | 12–68 | HTML `<div>` translation and class/data-attr layout delegation |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlTableElementTranslator.cs` | 160–254 | HTML `<table>`, `thead`, `tbody`, `tr`, `th`, `td` translation into Spectre `Table` |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlHeadingElementTranslator.cs` | 12–67 | HTML `<h1>` through `<h6>` translation |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlInlineTextElementTranslator.cs` | 13–120 | Inline HTML formatting tags (`strong`, `em`, `code`, `mark`, etc.) translation |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/FallbackTranslator.cs` | 30–60 | Diagnostic red panel fallback for unsupported HTML/VDOM tags |
| **RazorConsole** | `src/RazorConsole.Core/Components/Box.razor` | 9–26, 45–198 | `<Box>` component layout attributes (`Padding`, `Margin`, `Width`, `Height`, `Expand`, `FillWidth`, `FillHeight`, `Border`) |
| **RazorConsole** | `src/RazorConsole.Core/Components/Panel.razor` | 6–19, 36–105 | `<Panel>` component wrapping `<Box>` with `Title`, `BorderColor`, etc. |
| **RazorConsole** | `src/RazorConsole.Core/Components/TextInput.razor` | 14–43, 63–70 | `<TextInput>` focusable input component with two-way binding |
