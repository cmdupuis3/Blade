module Blade.Tests.CodeGenSubst

// ============================================================================
// Codegen smoke tests for the IRContains fallback path.
//
// History: this file once tested an active codegen-level substitution
// mechanism that mapped specific IRContains nodes (by reference) to
// precomputed-set names. The mask renderer populated that map; the
// IRContains arm of exprToCppCore consulted it.
//
// As of M1, the optimization moved to the IR level (rewriteMaskContains
// + IRMaskWithSet + IRSetMember). The substitution machinery exists as
// a vestigial parameter but is never populated. The IRContains arm now
// always renders the IIFE linear scan; mask+contains fusion happens
// before codegen runs.
//
// What remains here: two smoke tests verifying (a) that the IIFE form
// is what IRContains renders, and (b) that the leftover SubstMap
// parameter is harmless when populated with unrelated entries.
// ============================================================================

open Blade.IR
open Blade.CodeGen

type SubstTest = {
    Name: string
    Run: unit -> bool * string
}

let private intTy = IRTScalar ETInt64

let private checkOutput (output: string) (expected: string list) (forbidden: string list) : bool * string =
    let missing = expected |> List.filter (fun s -> not (output.Contains s))
    let present = forbidden |> List.filter (fun s -> output.Contains s)
    if List.isEmpty missing && List.isEmpty present then (true, "")
    else
        let msg =
            sprintf "output: %s\n   missing expected substrings: [%s]\n   contains forbidden substrings: [%s]"
                output
                (String.concat "; " missing)
                (String.concat "; " present)
        (false, msg)

let test_irContains_renders_as_iife = {
    Name = "IRContains renders as IIFE linear scan"
    Run = fun () ->
        let cont = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let names =
            Map.empty
            |> Map.add 1 "B"
            |> Map.add 2 "x"
        let output = exprToCppWithSubst [] names cont
        checkOutput output
            ["[&]()"; "B.extents[0]"; "B["; "return true"; "return false"]
            ["B.count("]
}

let test_subst_param_harmless_when_no_contains = {
    Name = "SubstMap is vestigial: unrelated entries don't affect rendering"
    Run = fun () ->
        // The subst parameter still exists in the codegen API but is no
        // longer consulted. Pass a map with entries; verify they have
        // no effect on an expression with no IRContains.
        let unrelated = IRContains (IRVar (99, intTy), IRVar (98, intTy))
        let e = IRBinOp (IRElementwise, IRAdd, IRVar (2, intTy), IRLit (IRLitInt 5L))
        let names = Map.empty |> Map.add 2 "x"
        let subst = [(unrelated, "ghost_set")]
        let output = exprToCppWithSubst subst names e
        checkOutput output
            ["x + 5"]
            ["ghost_set"; "[&]()"; "count("]
}

let allSubstTests : SubstTest list = [
    test_irContains_renders_as_iife
    test_subst_param_harmless_when_no_contains
]

let runCodeGenSubstTests () : int * int =
    printfn ""
    printfn "=== Codegen IRContains smoke tests ==="
    let mutable passed = 0
    let mutable failed = 0
    for t in allSubstTests do
        let (ok, msg) = t.Run ()
        if ok then
            passed <- passed + 1
            printfn "  [OK] %s" t.Name
        else
            failed <- failed + 1
            printfn "  [FAIL] %s" t.Name
            printfn "    %s" msg
    printfn "Subst Tests: %d passed, %d failed" passed failed
    (passed, failed)
