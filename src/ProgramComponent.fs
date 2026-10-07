namespace RazorConsole.Elmish

open Microsoft.AspNetCore.Components

[<AbstractClass>]
type ProgramComponent<'model, 'msg>() =
    inherit ComponentBase()
