# Elmish, Bolero, and RazorConsole: Architecture and Connector Design

> 📘 **Looking for the active developer handbook, contracts, and TDD guide?**  
> See the companion document: [implementation_guide.md](implementation_guide.md).

---

## 1. Executive Summary

This document provides a comprehensive architectural investigation into:
1. **Foundational Microsoft Learn References**: Core Blazor architectural concepts (lifecycle, render tree building, synchronization context, event callbacks, custom renderers) for understanding how components work under the hood.
2. **RazorConsole** (`RazorConsole.Core`): How it hosts Razor components, runs its custom Blazor `Renderer`, translates virtual DOM diffs to Spectre.Console renderables, manages terminal focus and input, and triggers re-renders.
3. **Bolero** (`Bolero`): How it bridges Elmish (`Program<'arg, 'model, 'msg, 'view>`) with Microsoft Blazor's `ComponentBase`, `RenderHandle`, and `RenderTreeBuilder`, marshaling state changes via `InvokeAsync(this.StateHasChanged)`.
4. **Elmish Model Translation**: A definitive analysis of Elmish's core design proving that Elmish core is entirely UI-agnostic and produces no intermediate representation (IR) or virtual DOM, leaving `'view` unconstrained. We compare how major connectors (Fable.React, Bolero, Elmish.WPF, Avalonia.FuncUI) materialize UI.
5. **Architectural Evaluation: Bolero vs. Native `RazorConsole.Elmish`**: Why a native connector tailored for terminal TUIs is superior to hosting Bolero directly.
6. **Exhaustive HTML Support Matrix in RazorConsole**: Exactly which tags are translated by RazorConsole's middleware pipeline and how unsupported elements are handled.
7. **Empirical Performance Benchmarks**: Direct `RenderTreeBuilder` emission vs. Component `RenderFragment` composition, analyzing tree generation speed, partial update penalties, and Amdahl's Law in terminal pipelines.
8. **Primary Source Citation Index**: Grounded references citing files, symbols, and interfaces across all reviewed repositories without fragile line numbers.

---

## 2. Foundational Microsoft Learn References (Razor/Blazor Architecture)

To understand how RazorConsole renders Razor components to a terminal and how Elmish integrates with it, familiarity with Microsoft Blazor's architectural building blocks is essential. The following first-party guides and API references on Microsoft Learn explain these core concepts:

### 2.1 The Component Lifecycle & Rendering Model
* **[ASP.NET Core Razor component lifecycle](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/lifecycle)**
  * **Core Concept:** Explains the order of execution for `SetParametersAsync`, `OnInitialized`, `OnInitializedAsync`, and `OnAfterRender(bool firstRender)`.
  * **Why it matters for Elmish:** Explains why `init` model must be produced in `OnInitialized` to avoid an empty initial render tree, and why the asynchronous Elmish loop (`Program.runWith`) must be deferred to `OnAfterRender(firstRender = true)`.
* **[ASP.NET Core Razor component rendering](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/rendering)**
  * **Core Concept:** How Blazor decides when to re-render, how `ComponentBase` queues render batches, and how calling `StateHasChanged()` signals that component state has been updated.
  * **Why it matters for Elmish:** This is the exact mechanism Elmish uses in `setState` to request a visual refresh when the model changes.

### 2.2 Low-Level Render Tree Construction & Linear Diffing
* **[ASP.NET Core Blazor advanced scenarios (render tree construction)](https://learn.microsoft.com/en-us/aspnet/core/blazor/advanced-scenarios)**
  * **Core Concept:** Explains how the Razor compiler transforms `.razor` tags into C# calls on `RenderTreeBuilder`, and how Blazor's diffing engine achieves $O(N)$ linear-time performance.
  * **Critical Concept — Sequence Numbers:** Sequence numbers indicate source code call sites, not runtime execution counters (`seq++`). Generating runtime sequence numbers corrupts the diff algorithm.
  * **Critical Concept — `OpenRegion`:** Explains how `builder.OpenRegion` isolates dynamic collections and subtrees so sequence shifts inside loops do not desynchronize sibling elements.
* **[`RenderTreeBuilder` Class API Reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.rendering.rendertreebuilder)**
  * **Core Concept:** The low-level API for emitting frame entries: `OpenElement`, `CloseElement`, `AddAttribute`, `AddContent`, `OpenComponent<T>`, `SetKey`, and `OpenRegion`.
* **[`RenderFragment` Delegate Reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.renderfragment)**
  * **Core Concept:** The foundational delegate `delegate void RenderFragment(RenderTreeBuilder builder)` representing arbitrary UI segments.

### 2.3 Threading, Dispatchers, and Concurrency
* **[ASP.NET Core Blazor synchronization context](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context)**
  * **Core Concept:** Blazor enforces a single-threaded execution context per renderer. Components run within a specialized `SynchronizationContext` managed by a `Dispatcher`.
  * **Why it matters for Elmish:** Elmish commands (`Cmd.OfAsync`, `Cmd.OfTask`, timers, subscriptions) run on background thread pool threads. Attempting to call `StateHasChanged()` directly from a background thread triggers concurrency exceptions. All UI notifications must be marshaled via `ComponentBase.InvokeAsync(this.StateHasChanged)` onto the `Dispatcher`.
* **[`Dispatcher` Class API Reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.dispatcher)**
  * **Core Concept:** Provides methods (`InvokeAsync`) to execute work on the component renderer's logical thread.

### 2.4 Event Callbacks & Inter-Component Communication
* **[ASP.NET Core Blazor event handling](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/event-handling)**
  * **Core Concept:** Explains how event arguments (`ChangeEventArgs`, `MouseEventArgs`, `KeyboardEventArgs`) are dispatched and how parent components bind to child events without manual event wire-up or memory leak risks.
* **[`EventCallback` Struct](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.eventcallback) & [`EventCallbackFactory` Class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.eventcallbackfactory)**
  * **Core Concept:** `EventCallback.Factory.Create` binds an event receiver component to a delegate action. RazorConsole's terminal input manager triggers these callbacks automatically when keystrokes hit focused elements.

### 2.5 The Custom Renderer Extensibility Model
* **[`Renderer` Class API Reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.rendertree.renderer)**
  * **Core Concept:** The abstract base class in `Microsoft.AspNetCore.Components.RenderTree` that manages component lifecycles, assigns integer component IDs, runs diffing passes, and yields `RenderBatch` structs.
  * **How RazorConsole fits in:** RazorConsole's `ConsoleRenderer` inherits directly from `Renderer`. It overrides `UpdateDisplayAsync(in RenderBatch batch)` to capture the diff stream and translate it into a terminal Virtual DOM (`VNode`) and Spectre.Console renderables rather than browser DOM mutations.

---

## 3. In-Depth Analysis of RazorConsole

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
|   |   MountComponentAsync() --------> RenderRootComponentAsync(componentId)       |   |
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

### 3.1 Application Hosting and Entry Point
The hosting entry point is defined in `RazorConsole.Core/AppHost.cs`:
- **`UseRazorConsole<TComponent>`**:
  Extension method on `IHostBuilder` and `IHostApplicationBuilder` with constraint `where TComponent : IComponent`.
- **`RegisterDefaults<TComponent>`**:
  Calls `services.AddRazorConsoleServices()` and registers `ComponentService<TComponent>` as an `IHostedService`.
- **`ComponentService<TComponent>`**:
  A `BackgroundService` that coordinates the lifecycle:
  1. Calls `RenderComponentInternalAsync`, which executes `consoleRenderer.MountComponentAsync<TComponent>`.
  2. Instantiates `ConsoleLiveDisplayContext` wrapping `LiveDisplayCanvas`, `consoleRenderer`, and `terminalMonitor`.
  3. Subscribes `FocusManager` to the renderer snapshot notifications.
  4. Starts background input loops (`keyboardEventManager.RunAsync` and `terminalMonitor.Start`).
  5. Waits for cancellation while keeping the terminal alive.

### 3.2 `ConsoleRenderer`: Blazor Renderer Specialization
The core rendering engine lives in `RazorConsole.Core/Rendering/ConsoleRenderer.cs`:
- **Inheritance & Interfaces**:
  Inherits directly from `Microsoft.AspNetCore.Components.RenderTree.Renderer` and implements `IObservable<ConsoleRenderer.RenderSnapshot>`.
- **Dispatcher**:
  Instantiates `Dispatcher.CreateDefault()`. All component lifecycle calls, parameter setting, and event dispatches are routed through this `Dispatcher`.
- **Component Roots Tracking**:
  Maintains `Dictionary<int, VNode> _componentRoots`, mapping Blazor component IDs to Virtual DOM node trees.
- **`UpdateDisplayAsync(in RenderBatch batch)`**:
  Overrides Blazor's abstract display update:
  1. Iterates `batch.UpdatedComponents`, invoking `ApplyComponentEdits(batch, diff)` to mutate the in-memory `VNode` tree.
  2. Removes disposed component trees from `_componentRoots`.
  3. Invokes `CreateSnapshot()` to translate the `VNode` hierarchy into a Spectre.Console `IRenderable` tree.
  4. Signals observers via `EnqueueObserverNotification(snapshot)`.
- **Event Dispatching**:
  `DispatchEventAsync(ulong eventHandlerId, EventFieldInfo? fieldInfo, EventArgs eventArgs)` routes incoming terminal events from `KeyboardEventManager` or mouse monitors back to Blazor's base event dispatcher, triggering component `EventCallback`s.

### 3.3 `ConsoleLiveDisplayContext` and Terminal Screen Updates
Defined in `RazorConsole.Core/Rendering/ConsoleLiveDisplayContext.cs`:
- Implements `IObserver<ConsoleRenderer.RenderSnapshot>`.
- In `OnNext(snapshot)`, converts the snapshot to a `ConsoleViewResult` and calls `UpdateView(view)`.
- Uses `VdomDiffService.Diff(previousRoot, currentRoot)` to compute mutations.
- If mutations can be applied incrementally (e.g. `UpdateText`, `UpdateAttributes`), it updates `_canvas` in-place. Otherwise, it executes a full redraw fallback via `_canvas.UpdateTarget(view.Renderable)`.

---

## 4. In-Depth Analysis of Bolero

Bolero brings the Elmish (MVU) architecture to Blazor. The primary sources reside in `Bolero/src/Bolero`.

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
|   |     3. this.InvokeAsync(this.StateHasChanged)  <--- BLAZOR DISPATCHER!    |   |
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

### 4.1 The Representation of `Node` and `Attr`
In `Bolero/src/Bolero/NodeTypes.fs`:
```fsharp
type Attr = delegate of obj * RenderTreeBuilder * int -> int
type Node = delegate of obj * RenderTreeBuilder * int -> int
```
Both `Attr` and `Node` are F# delegates accepting:
1. `obj`: The owning / receiving Blazor component instance.
2. `RenderTreeBuilder`: Blazor's mutable builder.
3. `int`: The sequence number index.
They return `int`: the next available sequence number.

In `Bolero/src/Bolero/Node.fs`:
- `Node.Empty()`: `Node(fun _ _ i -> i)`
- `Node.Elt`: Calls `tb.OpenElement`, iterates attributes and children, and calls `tb.CloseElement`.
- `Node.Text`: Calls `tb.AddContent(i, text); i + 1`.
- `Node.ForEach`: Wraps dynamic iteration in `tb.OpenRegion(i)` ... `tb.CloseRegion()` to prevent sequence desynchronization.
- `Node.Fragment`: Wraps a standard Blazor `RenderFragment` in `tb.OpenRegion(i)`.

### 4.2 The `ProgramComponent` Base Class
In `Bolero/src/Bolero/Components.fs`:
- Inherits `ComponentBase`.
- Overrides `BuildRenderTree(builder)` to call `this.Render().Invoke(this, builder, 0) |> ignore`.
- Specializes Elmish’s program:
  ```fsharp
  type Program<'model, 'msg> = Program<ProgramComponent<'model, 'msg>, 'model, 'msg, Node>
  ```
- Bridges Elmish state updates to Blazor via `ForceSetState`:
  ```fsharp
  view <- Program.view program model dispatch
  oldModel <- Some model
  this.InvokeAsync(this.StateHasChanged) |> ignore
  ```

---

## 5. Synthesis: How Elmish Translates Models & Ecosystem Comparison

### 5.1 Does Elmish Core Translate Models to an Intermediate Representation?
**No.** Elmish core (`elmish/src/program.fs`) is entirely UI-agnostic and performs no model translation, virtual DOM diffing, or layout computation.

```fsharp
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

Key observations from Elmish core:
1. `'view` is an unrestricted generic type parameter. Elmish core never inspects, casts, or stores `'view`.
2. In default `Program.mkProgram`, `setState` is simply `fun model -> view model >> ignore`.
3. Elmish's sole responsibility is managing the message queue ring buffer, invoking `update`, managing active `Sub<'msg>`, and notifying `setState model dispatch`.
4. All UI generation, intermediate representations, reconciliation, and rendering are left entirely to connector libraries.

### 5.2 Cross-Connector Comparison Table

| Feature / Dimension | Fable.React / Feliz | Bolero | Elmish.WPF | Avalonia.FuncUI | RazorConsole.Elmish |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **`'view` Type Parameter** | `ReactElement` | `Bolero.Node` | `Bindings<'model, 'msg>` | `IView` | `RazorConsole.Elmish.Node` |
| **Host Environment** | Browser DOM via Webpack/Vite | Microsoft Blazor (`ComponentBase`) | WPF (`Window` / `FrameworkElement`) | Avalonia UI Control (`ElmishHostControl`) | RazorConsole (`ConsoleRenderer`) |
| **Intermediate Representation** | React Virtual DOM | Blazor `RenderTreeFrame` array | None (Direct DependencyProperty bindings) | FuncUI Virtual Control Tree | Blazor frames -> RazorConsole `VNode` tree |
| **Diffing & Reconciliation** | React fiber diffing | Blazor `Renderer` (`RenderBatch`) | WPF native layout engine | Custom FuncUI diff/patch engine | Blazor diff -> `ConsoleRenderer` -> `VdomDiffService` |
| **Screen Materialization** | Browser DOM elements | Browser DOM via WASM/SignalR | DirectX / MilCore native rendering | Skia / Direct2D | Spectre.Console `IRenderable` -> ANSI canvas |
| **Thread Marshaling** | Single-threaded JS event loop | `ComponentBase.InvokeAsync` | `Dispatcher.Invoke` onto WPF Dispatcher | `Dispatcher.UIThread.InvokeAsync` | `ComponentBase.InvokeAsync` onto Blazor Dispatcher |
| **Event Routing** | Synthetic React events | Blazor `EventCallback` -> `dispatch` | WPF `ICommand` -> `dispatch` | Native Avalonia events -> `dispatch` | Terminal input -> `KeyboardEventManager` -> `EventCallback` -> `dispatch` |

---

## 6. Architectural Evaluation: Bolero vs. Native `RazorConsole.Elmish`

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

> 📘 **For the full implementation guide, architectural contracts, 5-phase roadmap, and TDD progression, see [implementation_guide.md](implementation_guide.md).**

---

## 7. Exhaustive HTML Support Matrix in RazorConsole

RazorConsole translates a strictly bounded subset of HTML elements into Spectre.Console renderables via dedicated translation middlewares.

| Category | Supported HTML Tags | Primary Source Middleware | Translation Target / Behavior |
| :--- | :--- | :--- | :--- |
| **Containers / Blocks** | `<div>` | `HtmlDivElementTranslator` | Translated to `BlockInlineRenderable`. Divs with special classes (`panel`, `rows`, `columns`, `grid`) or data attributes (`data-button`, `data-spacer`, `data-newline`) are delegated to specialized layout translators. |
| | `<p>` | `HtmlParagraphElementTranslator` | Child nodes converted to renderables; multiple children wrapped in `Columns(Expand=false, Padding=0)`. |
| | `<blockquote>` | `HtmlBlockquoteElementTranslator` | Quoted block with indent/border. |
| | `<hr>` | `HtmlHrElementTranslator` | Rendered as a horizontal rule via Spectre `Rule`. |
| | `<pre>` with `<code>` | `HtmlCodeBlockElementTranslator` | Syntax-highlighted code block using `SyntaxHighlightingService`. |
| **Headings** | `<h1>` through `<h6>` | `HtmlHeadingElementTranslator` | Prefixed with `# `, `## `, `### ` etc. and styled with Spectre headings. |
| **Lists** | `<ul>`, `<ol>`, `<li>` | `HtmlListElementTranslator` | Rendered as bulleted (`* `) or numbered (`1. `) lines. |
| **Tables** | `<table>`, `<thead>`, `<tbody>`, `<tfoot>`, `<tr>`, `<th>`, `<td>` | `HtmlTableElementTranslator` | Translated to a full Spectre.Console `Table` with border styles and cells. |
| **Buttons** | `<button>` | `HtmlButtonElementTranslator` | Focusable button renderable with click/key handlers. |
| **Inline Formatting** | `<strong>`, `<b>` | `HtmlInlineTextElementTranslator` | `[bold]` Spectre markup |
| | `<em>`, `<i>`, `<cite>` | `HtmlInlineTextElementTranslator` | `[italic]` Spectre markup |
| | `<mark>` | `HtmlInlineTextElementTranslator` | `[black on yellow]` Spectre markup |
| | `<del>` | `HtmlInlineTextElementTranslator` | `[strikethrough]` Spectre markup |
| | `<ins>`, `<abbr>` | `HtmlInlineTextElementTranslator` | `[underline]` Spectre markup |
| | `<code>` | `HtmlInlineTextElementTranslator` | `[indianred1 on #1f1f1f]` Spectre markup |
| | `<small>`, `<sub>`, `<sup>` | `HtmlInlineTextElementTranslator` | `[dim]` Spectre markup |
| | `<q>` | `HtmlInlineTextElementTranslator` | Unicode quotes (`“...”`) |
| | `<a>` | `HtmlInlineTextElementTranslator` | Underlined link markup |

#### Behavior on Unsupported HTML Tags
If an application outputs an HTML tag not listed above (such as `<section>`, `<article>`, `<header>`, `<footer>`, `<input>`, `<select>`), RazorConsole does **not** silently drop it. Instead, **`FallbackTranslator`** intercepts it and renders a red diagnostic panel directly on the terminal:
```
+-----------------------------------------------+
| [red]Untranslated VDOM[/] • <section>         |
+-----------------------------------------------+
```

---

## 8. Empirical Performance Benchmark: RenderFragment vs. Direct RenderTreeBuilder

A central architectural question for `RazorConsole.Elmish` is whether the Elmish `view` function should emit a tree of Blazor components via `RenderFragment` delegates, or directly drive `RenderTreeBuilder.OpenElement` inside a single root component.

### 8.1 Resolution of the Initial Benchmark Anomaly
Early prototype benchmarks initially produced an unexpected result where the Component Path appeared faster because:
1. **Asymmetric Update Propagation:** The component path's child components were not receiving updated parameters, causing Blazor to short-circuit and skip re-rendering child components (measuring a virtually no-op parent render).
2. **VDOM Structure Divergence:** The direct element path omitted layout attributes and text spans, testing an entirely different element structure against RazorConsole's translator.

### 8.2 Normalized Empirical Findings
When the benchmark harness (`benchmarks/Program.fs`) was normalized so that both approaches generate **100% character-identical Virtual DOM trees** across all frames (verified via `VdomHtmlSerializer` string assertions), the true performance relationship emerged:

#### Benchmark Results (BenchmarkDotNet v0.15.8, .NET 10.0.12, Intel Core Ultra 9 185H)

| Scenario | Mode | Component Tree Mean | Direct Builder Mean | Speedup | Component Tree Alloc | Direct Builder Alloc | Alloc Ratio |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **Full Update (50 items)** | `PureBlazor` | 76.54 μs | **25.43 μs** | **3.01x** | 57.54 KB | **34.01 KB** | **0.59x** (-40.9%) |
| **Partial Update (1 of 50 items)** | `PureBlazor` | 61.40 μs | **24.46 μs** | **2.51x** | 42.69 KB | **31.41 KB** | **0.55x** (-45.2%) |
| **Structural Mutation (50 ↔ 51)** | `PureBlazor` | 72.91 μs | **26.53 μs** | **2.75x** | 52.76 KB | **34.35 KB** | **0.60x** (-40.2%) |
| **Full Update (50 items)** | `ConsoleRenderer` | 221.03 μs | **153.28 μs** | **1.44x** | 293.79 KB | **262.57 KB** | **0.89x** (-10.6%) |
| **Partial Update (1 of 50 items)** | `ConsoleRenderer` | 184.12 μs | **134.26 μs** | **1.37x** | 269.29 KB | **244.96 KB** | **0.83x** (-9.0%) |
| **Structural Mutation (50 ↔ 51)** | `ConsoleRenderer` | 204.26 μs | **144.16 μs** | **1.42x** | 288.55 KB | **256.90 KB** | **0.87x** (-11.0%) |

### 8.3 Key Empirical Insights
1. **Pure Tree-Building Speed:** In isolation (`PureBlazor`), direct `RenderTreeBuilder` emission is **2.5x to 3.0x faster** and allocates **~40% to 45% less memory** (0.55x–0.60x allocation ratio) than a component hierarchy.
2. **Component Boundary Penalty on Partial Updates:** Even when only 1 item out of 50 changes, the Component Tree still takes **61.40 μs** in `PureBlazor`. Because `ChildContent` delegates are re-instantiated closures, Blazor cannot prove parameter equality and forces `SetParametersAsync` traversals across all 50 components. In contrast, Direct Builder completes in **24.46 μs** because Blazor diffs the single tree in memory and generates **0 edits** for the 49 unchanged items.
3. **Amdahl's Law in RazorConsole:** Under `ConsoleRenderer`, widget translation (`CreateSnapshot`) adds a flat baseline cost of **~110–130 μs** and **~220 KB of allocations** per frame to both approaches. This fixed overhead compresses the observed speedup from **~3.0x** down to **~1.4x** (saving **~50 to ~68 μs** per frame).
4. **VDOM Equivalence:** The benchmark harness (`Program.fs`) verified that the Direct Element tree generates **100% character-identical Virtual DOM serialization** as the Component tree across all initial, full, partial, and structural update scenarios.

---

## 9. Primary Source Citation Index

References are cited by repository, file path, and public entity/symbol rather than fluid line numbers:

| Repository | File Path | Symbol / Entity / Topic |
| :--- | :--- | :--- |
| **Benchmark** | `benchmarks/Program.fs` | `RazorConsoleBenchmark`, `PureBlazorRenderer`, `runVerification` |
| **Elmish** | `src/program.fs` | `type Program<'arg, 'model, 'msg, 'view>`, `Program.mkProgram`, `Program.runWithDispatch` |
| **Elmish** | `src/cmd.fs` | `type Dispatch<'msg>`, `type Cmd<'msg>`, `Cmd.exec` |
| **Elmish** | `src/sub.fs` | `type Sub<'msg>`, subscription diffing and active subscriber tracking |
| **Bolero** | `src/Bolero/NodeTypes.fs` | `type Attr`, `type Node` delegates |
| **Bolero** | `src/Bolero/Components.fs` | `ComponentBase` subclassing, `ProgramComponent<'model, 'msg>`, `ForceSetState`, `BuildRenderTree` |
| **Bolero** | `src/Bolero/Node.fs` | `Node.Empty`, `Node.Elt`, `Node.Text`, `Node.ForEach` (with `OpenRegion`), `Node.Fragment` |
| **Bolero** | `src/Bolero/ProgramRun.fs` | `Program'.runFirstRender` splitting init rendering from commands for Blazor lifecycle |
| **Bolero** | `src/Bolero.Html/Html.fs` | `comp<'T>`, `attr.callback<'T>` event binding via `EventCallback.Factory.Create` |
| **Bolero** | `src/Bolero.Html/Builders.fs` | `ConcatBuilder`, `AttrBuilder` struct computation expressions |
| **RazorConsole** | `src/RazorConsole.Core/AppHost.cs` | `UseRazorConsole<TComponent>`, `ComponentService<TComponent>` background lifecycle |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleRenderer.cs` | `ConsoleRenderer : Renderer`, `_dispatcher`, `MountComponentAsync`, `UpdateDisplayAsync`, `CreateSnapshot` |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/ConsoleLiveDisplayContext.cs` | `ConsoleLiveDisplayContext : IObserver<RenderSnapshot>`, `UpdateView`, `VdomDiffService` |
| **RazorConsole** | `src/RazorConsole.Core/Input/KeyboardEventManager.cs` | `KeyboardEventManager.RunAsync`, terminal input decoding, `DispatchEventAsync` |
| **RazorConsole** | `src/RazorConsole.Core/Focus/FocusManager.cs` | Terminal keyboard focus tracking and focus session management |
| **RazorConsole** | `src/RazorConsole.Core/RazorConsoleServiceCollectionExtensions.cs` | `AddRazorConsoleServices`, default DI middleware registrations |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlParagraphElementTranslator.cs` | HTML `<p>` translation into Spectre `Columns` |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlDivElementTranslator.cs` | HTML `<div>` translation and class/data-attr layout delegation |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlTableElementTranslator.cs` | HTML `<table>`, `thead`, `tbody`, `tr`, `th`, `td` translation into Spectre `Table` |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlHeadingElementTranslator.cs` | HTML `<h1>` through `<h6>` translation |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/HtmlInlineTextElementTranslator.cs` | Inline HTML formatting tags (`strong`, `em`, `code`, `mark`, etc.) translation |
| **RazorConsole** | `src/RazorConsole.Core/Rendering/Translation/Translators/FallbackTranslator.cs` | Diagnostic red panel fallback for unsupported HTML/VDOM tags |
| **RazorConsole** | `src/RazorConsole.Core/Components/Box.razor` | `<Box>` layout attributes (`Padding`, `Margin`, `Width`, `Height`, `Expand`, `Border`) |
| **RazorConsole** | `src/RazorConsole.Core/Components/Panel.razor` | `<Panel>` component wrapping `<Box>` with `Title`, `BorderColor`, etc. |
| **RazorConsole** | `src/RazorConsole.Core/Components/TextInput.razor` | `<TextInput>` focusable input component with two-way binding |
| **RazorConsole** | `src/RazorConsole.Core/Components/TextButton.razor` | `<TextButton>` focusable button component with click handling |
