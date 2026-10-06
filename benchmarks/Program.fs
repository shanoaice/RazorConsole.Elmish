namespace RazorConsole.Elmish.Benchmarks

#nowarn "0044" "0057"

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open BenchmarkDotNet.Attributes
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open BenchmarkDotNet.Toolchains.InProcess.NoEmit
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Rendering
open Microsoft.AspNetCore.Components.RenderTree
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open RazorConsole.Core
open Spectre.Console

type PipelineMode =
    | PureBlazor = 0
    | ConsoleRenderer = 1

[<Struct>]
type ItemModel = { Id: int; Version: int }

type PureBlazorRenderer(services: IServiceProvider, loggerFactory: ILoggerFactory) =
    inherit Renderer(services, loggerFactory)
    let dispatcher = Dispatcher.CreateDefault()

    override _.Dispatcher = dispatcher
    override _.UpdateDisplayAsync(renderBatch: inref<RenderBatch>) = Task.CompletedTask
    override _.HandleException(ex: exn) =
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()

    member private this.AssignId(comp: IComponent) = this.AssignRootComponentId(comp)
    member private this.RenderRoot(id: int, p: ParameterView) = this.RenderRootComponentAsync(id, p)

    member this.MountComponentAsync(comp: IComponent) : Task<int> =
        let id = this.AssignId(comp)
        this.Dispatcher.InvokeAsync(fun () -> this.RenderRoot(id, ParameterView.Empty)).ContinueWith(fun (t: Task) ->
            if t.IsFaulted then System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(t.Exception.InnerException).Throw()
            id
        )

[<AbstractClass>]
type RootTestBase() =
    inherit ComponentBase()
    let baseItemCount = 50
    let mutable items: ItemModel[] = [||]

    member this.Items
        with get () = items
        and set value = items <- value

    override this.OnInitialized() =
        base.OnInitialized()
        items <- Array.init baseItemCount (fun i -> { Id = i; Version = 0 })

    member this.TriggerFullUpdate(tick: int) =
        items <- Array.init baseItemCount (fun i -> { Id = i; Version = tick })
        this.StateHasChanged()

    member this.TriggerPartialUpdate(tick: int) =
        let updated = Array.copy items
        updated.[25] <- { Id = 25; Version = tick }
        items <- updated
        this.StateHasChanged()

    member this.TriggerStructuralToggle(tick: int) =
        if (tick &&& 1) = 1 then
            let expanded = Array.zeroCreate (baseItemCount + 1)
            expanded.[0] <- { Id = 999; Version = tick }
            Array.Copy(items, 0, expanded, 1, Math.Min(items.Length, baseItemCount))
            items <- expanded
        else
            items <- Array.init baseItemCount (fun i -> { Id = i; Version = tick })
        this.StateHasChanged()

type ComponentRootComponent() =
    inherit RootTestBase()
    override this.BuildRenderTree(builder: RenderTreeBuilder) =
        base.BuildRenderTree(builder)
        RazorConsoleBenchmark.EmitComponentTree(builder, this.Items)

and DirectElementRootComponent() =
    inherit RootTestBase()
    override this.BuildRenderTree(builder: RenderTreeBuilder) =
        base.BuildRenderTree(builder)
        RazorConsoleBenchmark.EmitDirectElementTree(builder, this.Items)

and [<MemoryDiagnoser>] RazorConsoleBenchmark() =
    let mutable compRenderer: Renderer = Unchecked.defaultof<_>
    let mutable directRenderer: Renderer = Unchecked.defaultof<_>
    let mutable compRoot: ComponentRootComponent = Unchecked.defaultof<_>
    let mutable directRoot: DirectElementRootComponent = Unchecked.defaultof<_>

    let mutable compFullTick = 0
    let mutable directFullTick = 0
    let mutable compPartialTick = 0
    let mutable directPartialTick = 0
    let mutable compStructTick = 0
    let mutable directStructTick = 0

    [<Params(PipelineMode.PureBlazor, PipelineMode.ConsoleRenderer)>]
    member val Mode: PipelineMode = PipelineMode.PureBlazor with get, set

    static member CreatePureBlazorRenderer() =
        let services = ServiceCollection()
        let sp = services.BuildServiceProvider()
        new PureBlazorRenderer(sp, NullLoggerFactory.Instance)

    static member CreateConsoleRenderer() =
        let services = ServiceCollection()
        services.AddRazorConsoleServices() |> ignore
        let sp = services.BuildServiceProvider()
        let consoleRendererType =
            typeof<RazorConsoleServiceCollectionExtensions>.Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer")
        sp.GetRequiredService(consoleRendererType) :?> Renderer

    static member MountRazorConsoleComponentAsync(renderer: Renderer, comp: IComponent) : Task =
        let method =
            renderer.GetType().GetMethods(BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
            |> Array.find (fun m -> m.Name = "MountComponentAsync" && m.IsGenericMethod && m.GetParameters().Length = 3)
        let genericMethod = method.MakeGenericMethod(comp.GetType())
        genericMethod.Invoke(renderer, [| box comp; box ParameterView.Empty; box CancellationToken.None |]) :?> Task

    [<GlobalSetup>]
    member this.Setup() =
        compRoot <- ComponentRootComponent()
        directRoot <- DirectElementRootComponent()

        if this.Mode = PipelineMode.ConsoleRenderer then
            let comp = RazorConsoleBenchmark.CreateConsoleRenderer()
            let direct = RazorConsoleBenchmark.CreateConsoleRenderer()
            compRenderer <- comp
            directRenderer <- direct

            comp.Dispatcher.InvokeAsync(fun () ->
                RazorConsoleBenchmark.MountRazorConsoleComponentAsync(comp, compRoot)
            ).GetAwaiter().GetResult()

            direct.Dispatcher.InvokeAsync(fun () ->
                RazorConsoleBenchmark.MountRazorConsoleComponentAsync(direct, directRoot)
            ).GetAwaiter().GetResult()
        else
            let comp = RazorConsoleBenchmark.CreatePureBlazorRenderer()
            let direct = RazorConsoleBenchmark.CreatePureBlazorRenderer()
            compRenderer <- comp
            directRenderer <- direct

            comp.Dispatcher.InvokeAsync(fun () ->
                task { let! _ = comp.MountComponentAsync(compRoot) in () } :> Task
            ).GetAwaiter().GetResult()

            direct.Dispatcher.InvokeAsync(fun () ->
                task { let! _ = direct.MountComponentAsync(directRoot) in () } :> Task
            ).GetAwaiter().GetResult()

    member this.GetCompRoot() : obj =
        let method = compRenderer.GetType().GetMethod("RefreshSnapshot", BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
        let snapshot = method.Invoke(compRenderer, null)
        snapshot.GetType().GetProperty("Root").GetValue(snapshot)

    member this.GetDirectRoot() : obj =
        let method = directRenderer.GetType().GetMethod("RefreshSnapshot", BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
        let snapshot = method.Invoke(directRenderer, null)
        snapshot.GetType().GetProperty("Root").GetValue(snapshot)

    // -------------------------------------------------------------
    // Scenario 1: Full Update (All 50 items change version)
    // -------------------------------------------------------------
    [<Benchmark(Baseline = true, Description = "Component Tree (Full Update: 50 items)")>]
    member this.ComponentPathFullUpdate() : Task =
        compFullTick <- compFullTick + 1
        compRenderer.Dispatcher.InvokeAsync(fun () -> compRoot.TriggerFullUpdate(compFullTick))

    [<Benchmark(Description = "Direct Builder (Full Update: 50 items)")>]
    member this.DirectElementPathFullUpdate() : Task =
        directFullTick <- directFullTick + 1
        directRenderer.Dispatcher.InvokeAsync(fun () -> directRoot.TriggerFullUpdate(directFullTick))

    // -------------------------------------------------------------
    // Scenario 2: Partial Update (Only 1 item out of 50 changes)
    // -------------------------------------------------------------
    [<Benchmark(Description = "Component Tree (Partial Update: 1 of 50 items)")>]
    member this.ComponentPathPartialUpdate() : Task =
        compPartialTick <- compPartialTick + 1
        compRenderer.Dispatcher.InvokeAsync(fun () -> compRoot.TriggerPartialUpdate(compPartialTick))

    [<Benchmark(Description = "Direct Builder (Partial Update: 1 of 50 items)")>]
    member this.DirectElementPathPartialUpdate() : Task =
        directPartialTick <- directPartialTick + 1
        directRenderer.Dispatcher.InvokeAsync(fun () -> directRoot.TriggerPartialUpdate(directPartialTick))

    // -------------------------------------------------------------
    // Scenario 3: Structural Mutation (Toggle Insert/Remove 50 <-> 51 items)
    // -------------------------------------------------------------
    [<Benchmark(Description = "Component Tree (Structural Mutation: 50 <-> 51 items)")>]
    member this.ComponentPathInsertRemove() : Task =
        compStructTick <- compStructTick + 1
        compRenderer.Dispatcher.InvokeAsync(fun () -> compRoot.TriggerStructuralToggle(compStructTick))

    [<Benchmark(Description = "Direct Builder (Structural Mutation: 50 <-> 51 items)")>]
    member this.DirectElementPathInsertRemove() : Task =
        directStructTick <- directStructTick + 1
        directRenderer.Dispatcher.InvokeAsync(fun () -> directRoot.TriggerStructuralToggle(directStructTick))

    // -------------------------------------------------------------
    // Tree Emitters: Guaranteed Identical VDOM for Any items List
    // -------------------------------------------------------------
    static member EmitComponentTree(b: RenderTreeBuilder, items: ItemModel[]) =
        b.OpenComponent<RazorConsole.Components.Rows>(0)
        b.AddAttribute(1, "ChildContent", RenderFragment(fun bRows ->
            for i = 0 to items.Length - 1 do
                let item = items.[i]
                bRows.OpenComponent<RazorConsole.Components.Panel>(0)
                bRows.SetKey(box item.Id)
                bRows.AddAttribute(1, "Title", $"Item #{item.Id} (v{item.Version})")
                bRows.AddAttribute(2, "Border", box BoxBorder.Rounded)
                bRows.AddAttribute(3, "ChildContent", RenderFragment(fun bPanel ->
                    bPanel.OpenComponent<RazorConsole.Components.Markup>(0)
                    bPanel.AddAttribute(1, "Content", $"[green]Status: Active {item.Version}[/]")
                    bPanel.CloseComponent()
                ))
                bRows.CloseComponent()
        ))
        b.CloseComponent()

    static member EmitDirectElementTree(b: RenderTreeBuilder, items: ItemModel[]) =
        b.OpenElement(0, "div")
        b.AddAttribute(1, "class", "rows")
        b.AddAttribute(2, "data-layout", "flex")
        b.AddAttribute(3, "data-direction", "column")
        b.AddAttribute(4, "data-justify", "start")
        b.AddAttribute(5, "data-align", "start")
        b.AddAttribute(6, "data-wrap", "nowrap")
        b.AddAttribute(7, "data-gap", "0")
        b.AddAttribute(8, "data-expand", "false")
        b.AddAttribute(9, "data-fill-width", "false")
        b.AddAttribute(10, "data-fill-height", "false")

        for i = 0 to items.Length - 1 do
            let item = items.[i]
            b.OpenElement(11, "div")
            b.SetKey(box item.Id)
            b.AddAttribute(12, "class", "panel")
            b.AddAttribute(13, "data-layout", "box")
            b.AddAttribute(14, "data-expand", "false")
            b.AddAttribute(15, "data-fill-width", "false")
            b.AddAttribute(16, "data-fill-height", "false")
            b.AddAttribute(17, "data-header", $"Item #{item.Id} (v{item.Version})")
            b.AddAttribute(18, "data-border", "rounded")

            b.OpenElement(19, "span")
            b.AddAttribute(20, "data-text", "true")
            b.AddAttribute(21, "data-style", "")
            b.AddAttribute(22, "data-content", $"[[green]]Status: Active {item.Version}[[/]]")
            b.CloseElement()

            b.CloseElement()

        b.CloseElement()

module Program =
    let serializeVNode (root: obj) : string =
        if isNull root then ""
        else
            let serializerType = typeof<RazorConsoleServiceCollectionExtensions>.Assembly.GetType("RazorConsole.Core.Vdom.VdomHtmlSerializer")
            let method = serializerType.GetMethod("Serialize", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            method.Invoke(null, [| root |]) :?> string

    let runVerification () =
        printfn "=== Verifying VDOM Equivalence Across All Scenarios (F# Engine) ==="
        let bench = RazorConsoleBenchmark()
        bench.Mode <- PipelineMode.ConsoleRenderer
        bench.Setup()

        let assertMatch scenarioName =
            let compHtml = serializeVNode (bench.GetCompRoot())
            let directHtml = serializeVNode (bench.GetDirectRoot())
            let match' = compHtml = directHtml
            printfn "[%s] Exact Match: %b (Length: %d chars)" scenarioName match' compHtml.Length
            if not match' then
                let limit = Math.Min(compHtml.Length, directHtml.Length)
                for i = 0 to limit - 1 do
                    if compHtml.[i] <> directHtml.[i] then
                        printfn "Diff at char %d:" i
                        printfn "COMP:   %s" (compHtml.Substring(Math.Max(0, i - 20), Math.Min(compHtml.Length - Math.Max(0, i - 20), 50)))
                        printfn "DIRECT: %s" (directHtml.Substring(Math.Max(0, i - 20), Math.Min(directHtml.Length - Math.Max(0, i - 20), 50)))
                        failwithf "Mismatch in scenario %s!" scenarioName
                failwithf "Mismatch in scenario %s!" scenarioName

        assertMatch "Initial (50 items)"

        bench.ComponentPathFullUpdate().GetAwaiter().GetResult()
        bench.DirectElementPathFullUpdate().GetAwaiter().GetResult()
        assertMatch "After Full Update (50 items dirty)"

        bench.ComponentPathPartialUpdate().GetAwaiter().GetResult()
        bench.DirectElementPathPartialUpdate().GetAwaiter().GetResult()
        assertMatch "After Partial Update (1 of 50 items dirty)"

        bench.ComponentPathInsertRemove().GetAwaiter().GetResult()
        bench.DirectElementPathInsertRemove().GetAwaiter().GetResult()
        assertMatch "After Insert (51 items)"

        bench.ComponentPathInsertRemove().GetAwaiter().GetResult()
        bench.DirectElementPathInsertRemove().GetAwaiter().GetResult()
        assertMatch "After Remove back to 50 items"

        printfn "\nAll scenarios produced 100%% IDENTICAL VDOM trees in F#!"

    [<EntryPoint>]
    let main args =
        if args.Length > 0 && args.[0] = "--verify" then
            runVerification()
            0
        else
            let config =
                ManualConfig.Create(DefaultConfig.Instance)
                    .AddJob(
                        Job.ShortRun
                            .WithToolchain(InProcessNoEmitToolchain.Instance)
                            .WithWarmupCount(3)
                            .WithIterationCount(10)
                    )
            BenchmarkRunner.Run<RazorConsoleBenchmark>(config, args) |> ignore
            0
