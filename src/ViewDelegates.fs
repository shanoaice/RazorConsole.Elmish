namespace RazorConsole.Elmish

open Microsoft.AspNetCore.Components.Rendering

type Attr = delegate of receiver: obj * builder: RenderTreeBuilder * sequence: int -> int

type Node = delegate of receiver: obj * builder: RenderTreeBuilder * sequence: int -> int
