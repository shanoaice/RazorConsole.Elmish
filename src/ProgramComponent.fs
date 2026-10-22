namespace RazorConsole.Elmish

open Microsoft.AspNetCore.Components
open Elmish

[<AbstractClass>]
type ProgramComponent<'model, 'msg>() =
    inherit ComponentBase()

    let mutable _currentView: Node option = None

    abstract Program: Program<ProgramComponent<'model, 'msg>, 'model, 'msg, Node>

    override this.OnInitialized() =
        let programInit = Program.init this.Program
        let initModel, initCmd = programInit this

        ()
