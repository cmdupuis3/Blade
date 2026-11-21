// THE PRINT SELECTION (BLADE_PRINT / --print) -- one parser and one resolver
// shared by every reader (gap G200, round 4 wave 7).
//
// The selection names TOP-LEVEL bindings by their FINAL spelling: the main
// module's own names, and an imported module's names QUALIFIED (`Lib.y`, the
// print label Lowering.qualifyModuleNames gives them after every optimizer
// pass). Codegen's print pass, the LLVM lane's and the interpreter's compare
// the selection against those final names (`names`, exact).
//
// The OPTIMIZER plans that remove or fuse a binding only when the selection
// EXCLUDES it run per module BEFORE qualification, on the unqualified name.
// They ask `mayName` / `excludes`, which are CONSERVATIVE: an entry selects a
// binding when it is the binding's name or when its last dotted segment is
// (`Lib.y` selects every module's `y`; fail closed -- a plan declines a little
// more often in a multi-module program, it never elides a binding the
// selection names). Each plan that admits only single-module programs still
// proves that through its own flag; this module supplies print DEMAND only.
//
// A REPL / notebook supplies its own print demand AFTER lowering (an
// explicit `printOnly`): it lowers under `withoutSelection`, so no plan is
// licensed by an ambient BLADE_PRINT the session then overrides.
module Blade.PrintSelection

let private suppressed = System.Threading.AsyncLocal<bool>()

/// Run `f` with the ambient selection hidden from every reader (the caller
/// supplies its own print demand later). Restores the previous state.
let withoutSelection (f: unit -> 'a) : 'a =
    let prior = suppressed.Value
    suppressed.Value <- true
    try f () finally suppressed.Value <- prior

/// The selected names, as written (None = no selection: every top-level
/// binding prints). Read per call, like every env gate. Names are separated
/// by commas, spaces or semicolons.
let names () : Set<string> option =
    if suppressed.Value then None else
    match System.Environment.GetEnvironmentVariable "BLADE_PRINT" with
    | null -> None
    | v ->
        let ns =
            v.Split([| ','; ' '; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s <> "")
        if ns.Length = 0 then None else Some (Set.ofArray ns)

/// May the selection `sel` name a binding whose UNQUALIFIED name is `name`
/// (in any module)? The entry itself, or an entry whose last dotted segment
/// is `name`. A tuple carrier `_(a,b)` is never qualified, so the plain test
/// covers it.
let mayName (sel: Set<string>) (name: string) : bool =
    sel.Contains name
    || sel |> Set.exists (fun n ->
        match n.LastIndexOf '.' with
        | -1 -> false
        | i -> n.Substring(i + 1) = name)

/// An internal binding (a leading `__` on the last dotted segment, so a
/// qualified `Lib.__sfj18` counts) prints only when a selection names it --
/// except a bare top-level expression's `__expr<N>`, which prints by default.
/// The one predicate every printer and every plan reads.
let isInternal (name: string) : bool =
    let seg = match name.LastIndexOf '.' with -1 -> name | i -> name.Substring(i + 1)
    seg.StartsWith "__" && not (System.Text.RegularExpressions.Regex.IsMatch(seg, @"^__expr\d+$"))

/// Is the binding `name` PRINTED under the current selection, as far as a
/// pre-qualification plan can tell? True without a selection (every binding
/// prints, an internal one excepted) and whenever the selection may name it.
let mayPrint (name: string) : bool =
    match names () with
    | None -> not (isInternal name)
    | Some sel -> mayName sel name

/// Does a selection exist AND certainly exclude `name`? The admission test of
/// every print-licensed plan.
let excludes (name: string) : bool =
    match names () with
    | None -> false
    | Some sel -> not (mayName sel name)
