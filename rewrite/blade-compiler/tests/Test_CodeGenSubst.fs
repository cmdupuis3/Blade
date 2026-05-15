module Blade.Tests.CodeGenSubst

// ============================================================================
// Phase C Step 2 smoke tests for the codegen substitution mechanism.
//
// These verify that exprToCppWithSubst correctly substitutes
// set-probe calls for registered IRContains nodes (by reference
// equality), and that the substitution propagates through compositional
// constructs (binops, if-branches). They do NOT exercise the mask
// renderer — that's Step 3. They test the threading mechanism in
// isolation by constructing IR fragments and calling the rendering
// function directly.
// ============================================================================

open Blade.IR
open Blade.CodeGen

type SubstTest = {
    Name: string
    Run: unit -> bool * string   // (passed, message-if-failed)
}

let private intTy = IRTScalar ETInt64
let private boolTy = IRTScalar ETBool

/// Helper: assert that `output` contains all of `expected` and none of `forbidden`.
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

// ----------------------------------------------------------------------------
// 1. Empty substitution: behavior unchanged from current IIFE.
// ----------------------------------------------------------------------------

let test_empty_subst_falls_through_to_IIFE = {
    Name = "Empty SubstMap: IRContains falls back to linear-scan IIFE"
    Run = fun () ->
        // contains(B_1, x_2) with no substitution should emit the IIFE.
        let cont = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let names =
            Map.empty
            |> Map.add 1 "B"
            |> Map.add 2 "x"
        let output = exprToCppWithSubst [] names cont
        // The IIFE form contains "[&]()" and the loop signature.
        checkOutput output
            ["[&]()"; "B.extents[0]"; "B["; "return true"; "return false"]
            ["B.count("]
}

// ----------------------------------------------------------------------------
// 2. Top-level IRContains: substitution emits set.count(value).
// ----------------------------------------------------------------------------

let test_top_level_subst = {
    Name = "Subst at top-level IRContains emits set.count(value)"
    Run = fun () ->
        let cont = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let names =
            Map.empty
            |> Map.add 1 "B"
            |> Map.add 2 "x"
        let subst = [(cont, "B_set")]
        let output = exprToCppWithSubst subst names cont
        checkOutput output
            ["B_set.count(x)"]
            ["[&]()"; "B.extents[0]"]   // IIFE markers absent
}

// ----------------------------------------------------------------------------
// 3. Substitution propagates through a binop.
// ----------------------------------------------------------------------------

let test_subst_propagates_through_binop = {
    Name = "Subst propagates through IRBinOp: contains(B, x) && (x > 5)"
    Run = fun () ->
        let cont = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let cmp = IRBinOp (IRElementwise, IRGt, IRVar (2, intTy), IRLit (IRLitInt 5L))
        let e = IRBinOp (IRElementwise, IRAnd, cont, cmp)
        let names =
            Map.empty
            |> Map.add 1 "B"
            |> Map.add 2 "x"
        let subst = [(cont, "B_set")]
        let output = exprToCppWithSubst subst names e
        // The conjunction binds && at the outer level. The contains
        // sub-emit should be the set probe; the comparison stays.
        checkOutput output
            ["B_set.count(x)"; "x > 5"; "&&"]
            ["[&]()"]
}

// ----------------------------------------------------------------------------
// 4. Substitution propagates through an if-branch.
// ----------------------------------------------------------------------------

let test_subst_propagates_through_if = {
    Name = "Subst propagates through IRIf condition"
    Run = fun () ->
        let cont = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let e = IRIf (cont, IRLit (IRLitInt 10L), IRLit (IRLitInt 20L))
        let names =
            Map.empty
            |> Map.add 1 "B"
            |> Map.add 2 "x"
        let subst = [(cont, "B_set")]
        let output = exprToCppWithSubst subst names e
        checkOutput output
            ["B_set.count(x)"; "10"; "20"; "?"]
            ["[&]()"]
}

// ----------------------------------------------------------------------------
// 5. Selective substitution: two contains nodes, only one mapped.
// ----------------------------------------------------------------------------

let test_selective_subst = {
    Name = "Selective substitution: one contains substituted, another falls through"
    Run = fun () ->
        // Build TWO distinct IRContains objects. Both structurally look
        // the same (contains(B, x)) but reference equality distinguishes
        // them. Only `cont1` is in the map; `cont2` should fall through.
        let cont1 = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let cont2 = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        // Compose: cont1 || cont2
        let e = IRBinOp (IRElementwise, IROr, cont1, cont2)
        let names =
            Map.empty
            |> Map.add 1 "B"
            |> Map.add 2 "x"
        let subst = [(cont1, "B_set_hoisted")]
        let output = exprToCppWithSubst subst names e
        // cont1 → "B_set_hoisted.count(x)"
        // cont2 → IIFE form
        checkOutput output
            ["B_set_hoisted.count(x)"; "[&]()"; "B.extents[0]"; "||"]
            // No forbidden substrings; both forms should be present.
            []
}

// ----------------------------------------------------------------------------
// 6. Subst with no IRContains in expression: substitution is harmless.
// ----------------------------------------------------------------------------

let test_subst_harmless_when_no_contains = {
    Name = "Subst present but expression has no IRContains: no effect"
    Run = fun () ->
        // Just (x + 5) — no contains anywhere. The subst map has an
        // entry for some unrelated IRContains. Output should look like
        // a plain expression.
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
    test_empty_subst_falls_through_to_IIFE
    test_top_level_subst
    test_subst_propagates_through_binop
    test_subst_propagates_through_if
    test_selective_subst
    test_subst_harmless_when_no_contains
]

let runCodeGenSubstTests () : int * int =
    printfn ""
    printfn "=== Phase C Step 2: codegen substitution mechanism ==="
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
