module Blade.Tests.CodeGenSubst

// ============================================================================
// Codegen smoke tests for the IRContains fallback path.
//
// History: this file once tested an active codegen-level substitution
// mechanism that mapped specific IRContains nodes (by reference) to
// precomputed-set names. The mask renderer populated that map; the
// IRContains arm of exprToCppCore consulted it.
//
// As of M1, that optimization moved to the IR level (rewriteMaskContains
// + IRMaskWithSet + IRSetMember). The IRContains arm now ALWAYS renders the
// IIFE linear scan and never consults the substitution; mask+contains fusion
// happens before codegen runs.
//
// The SubstMap itself is NOT dead, though: exprToCppCore still consults it,
// at the IRIndex arm, for the halo-carousel rewrite (a hoisted dense window
// read renders as its rotating local). And it is keyed by REFERENCE
// (System.Object.ReferenceEquals in trySubst), not by structural equality —
// the same contains/index shape can occur at several positions, only some of
// which are substitutable.
//
// What this file pins:
//   (a) IRContains renders as the IIFE linear scan;
//   (b) the IRContains arm does not substitute even when the map holds an
//       entry structurally EQUAL to the very node being rendered;
//   (c) the SubstMap is nonetheless LIVE: a reference hit at the arm that does
//       consult it (IRIndex) renders the substituted name.
// (b) used to pass an expression with NO IRContains at all against a
// contains-keyed map — vacuous by construction: there was nothing in the tree
// for the entry to match, structurally or otherwise, so no implementation
// could have failed it. (c) is what stops (b) from being satisfiable by a
// renderer that ignores the SubstMap altogether.
// ============================================================================

open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.Tests.TestHarness
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

let test_contains_arm_never_substitutes = {
    Name = "IRContains does not substitute, even for a map entry equal to the node rendered"
    Run = fun () ->
        // The map DOES carry an IRContains entry, and the expression under
        // test IS an IRContains — a separately built twin with the same
        // arguments, so the two are structurally equal (F# DU equality) but
        // distinct objects. The contains must still render as the ordinary
        // IIFE scan with no "ghost_set" anywhere.
        //
        // This is what makes the test discriminate. Two ways for it to go red,
        // both real regressions: re-wiring a contains-substitution arm (the
        // pre-M1 hoist-set fusion returning to codegen after the optimization
        // moved to the IR level), or switching trySubst from ReferenceEquals to
        // structural equality — under which the twin WOULD match here and at
        // every other coincidentally-identical contains in a program.
        let inTree = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        let twin = IRContains (IRVar (1, intTy), IRVar (2, intTy))
        // Guard the premise: structurally equal, referentially distinct. If
        // either half ever stops holding, the test has silently degraded back
        // into the vacuous "unrelated entry" shape.
        if not (inTree = twin) then
            (false, "premise broken: the two IRContains nodes are not structurally equal")
        elif System.Object.ReferenceEquals(inTree, twin) then
            (false, "premise broken: the two IRContains nodes are the same object")
        else
        let names = Map.empty |> Map.add 1 "B" |> Map.add 2 "x"
        let output = exprToCppWithSubst [(twin, "ghost_set")] names inTree
        checkOutput output
            ["[&]()"; "B.extents[0]"; "return true"; "return false"]
            ["ghost_set"]
}

let test_subst_is_consulted_on_reference_hit = {
    Name = "SubstMap is live: a reference hit at the IRIndex arm renders the substituted local"
    Run = fun () ->
        // The positive half. Without it, the negative test above is satisfied
        // by an implementation that ignores the SubstMap entirely — which is
        // what this file's header used to (wrongly) claim it does. The carousel
        // rewrite consults trySubst at the IRIndex arm, so a hit on the node's
        // OWN reference renders as the substituted name and nothing else.
        let node = IRIndex (IRVar (1, intTy), [IRVar (2, intTy)], None)
        let names = Map.empty |> Map.add 1 "B" |> Map.add 2 "i"
        let output = exprToCppWithSubst [(node, "__w_center")] names node
        if output.Trim() = "__w_center" then (true, "")
        else (false, $"expected exactly \"__w_center\", got: {output}")
}

let allSubstTests : SubstTest list = [
    test_irContains_renders_as_iife
    test_contains_arm_never_substitutes
    test_subst_is_consulted_on_reference_hit
]

let runCodeGenSubstTests () : Blade.Tests.TestHarness.BlockResult =
    Blade.Tests.TestHarness.printHeader "Codegen IRContains Smoke"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    for t in allSubstTests do
        let (ok, msg) = t.Run ()
        if ok then
            passed <- passed + 1
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass t.Name ""
        else
            failed <- failed + 1
            failedNames <- failedNames @ [t.Name]
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail t.Name msg
    Blade.Tests.TestHarness.printFooter "Subst" [$"{passed} passed"; $"{failed} failed"]
    { Block = "Subst"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
