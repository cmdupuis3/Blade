module Blade.Tests.Unify

open Blade.IR
open Blade.Types
open Blade.Tests.TestHarness
open Blade.TypeCheck
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk

// ============================================================================
// TypeCheck-level tests for the §5.3 fast path in unify (Segment 6).
// ============================================================================
//
// These tests construct IRType values, instantiate a Subst, and call unify
// directly. They live one layer above Test_Normalize: that file tests the
// pure-IR normalizer; this one tests the integration into the type system.
//
// Runner: `blade test unify`. Each test returns (name, passed, detail).

// ---- Helpers (parallel to Test_Normalize) ---------------------------------

let private f64 = IRTScalar ETFloat64
let private i64 = IRTScalar ETInt64

let private idxN (n: int) : IRIndexType =
    {
        Id = n
        Rank = 1
        Extent = IRLit (IRLitInt (int64 n))
        Symmetry = SymNone
        Tag = None; IxKind = IxKPlain
        Kind = SDimension
        Dependencies = []
    }

let private mkId (handle: int) : ArrayIdentity = AIDLiteral handle

let private isOk = function Ok _ -> true | Error _ -> false

let private describeResult = function
    | Ok _ -> "Ok"
    | Error e -> sprintf "Error: %A" e

// ---- Test cases -----------------------------------------------------------

let private test_identical_concrete () =
    // Baseline: identical concrete types must unify (sanity check that the
    // fast path doesn't reject anything previously accepted).
    let subst = Subst()
    let t = IRTArrow ([SIdx (idxN 3)], f64, Some (mkId 1))
    let result = unify subst t t
    let pass = isOk result
    ("identical concrete arrow unifies",
     pass,
     describeResult result)

let private test_mixed_flat_vs_split_nested () =
    // The motivating §5.3 case: flat [SIdx; SVal] mixed-slot arrow
    // should unify with its split nested form.
    let subst = Subst()
    let flat =
        IRTArrow ([SIdx (idxN 3); SVal f64], f64, Some (mkId 1))
    let nested =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow ([SVal f64], f64, None),
            Some (mkId 1))
    let result = unify subst flat nested
    let pass = isOk result
    ("flat mixed-slot unifies with split nested form (§5.3)",
     pass,
     describeResult result)

let private test_reverse_order_flat_vs_split_nested () =
    // Symmetric direction: nested form on left, flat on right.
    let subst = Subst()
    let flat =
        IRTArrow ([SIdx (idxN 3); SVal f64], f64, Some (mkId 1))
    let nested =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow ([SVal f64], f64, None),
            Some (mkId 1))
    let result = unify subst nested flat
    let pass = isOk result
    ("split nested unifies with flat (symmetric)",
     pass,
     describeResult result)

let private test_differing_element_types_still_fail () =
    // Negative case: the fast path must NOT accept structurally
    // different types just because both are valid arrows.
    let subst = Subst()
    let flatF64 =
        IRTArrow ([SIdx (idxN 3); SVal f64], f64, Some (mkId 1))
    let nestedI64 =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow ([SVal f64], i64, None),
            Some (mkId 1))
    let result = unify subst flatF64 nestedI64
    let pass = not (isOk result)
    ("flat F64 result vs split I64 result fails (negative)",
     pass,
     if pass then "correctly rejected" else "incorrectly accepted")

let private test_uniform_flat_vs_nested_still_fails () =
    // Documents the §5.2 limitation: uniform-kind flat vs nested arrays
    // remain non-equivalent under ToNested. This test documents that
    // current behavior at the unify level — confirming the
    // limitation comment in unify's docstring.
    let subst = Subst()
    let flat =
        IRTArrow ([SIdx (idxN 2); SIdx (idxN 3)], f64, Some (mkId 1))
    let nested =
        IRTArrow (
            [SIdx (idxN 2)],
            IRTArrow ([SIdx (idxN 3)], f64, None),
            Some (mkId 1))
    let result = unify subst flat nested
    // Today, this should fail (uniform-kind §5.2 collapse not implemented).
    // If a future B-flat lands and this starts passing, the test will fail
    // as a signal to update both this test and the unify docstring.
    let pass = not (isOk result)
    ("uniform flat-vs-nested still fails under ToNested (documents §5.2 gap)",
     pass,
     if pass then "rejected as expected; flip this test when B-flat lands"
     else "unexpectedly accepted — B-flat may have landed; update docs")

let private test_three_kind_split_arrow () =
    // [SIdx; SVal; SIdxVirt] (three groups) vs nested form
    // [SIdx] -> [SVal] -> [SIdxVirt] should unify.
    let subst = Subst()
    let flat =
        IRTArrow (
            [SIdx (idxN 3); SVal f64; SIdxVirt (idxN 5)],
            f64,
            Some (mkId 1))
    let nested =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow (
                [SVal f64],
                IRTArrow ([SIdxVirt (idxN 5)], f64, None),
                None),
            Some (mkId 1))
    let result = unify subst flat nested
    let pass = isOk result
    ("three-kind split unifies with three-level nesting",
     pass,
     describeResult result)

let private test_inference_var_at_concrete_position_binds () =
    // Previously this case was rejected (documented as a limitation of
    // irTypeEquiv-as-fast-path). Now that unify normalizes both sides at
    // entry, the recursive case sees matching nested shapes and the
    // inference variable in the SVal slot binds to the concrete type
    // on the other side.
    let subst = Subst()
    let infTy = subst.Fresh()
    let infId =
        match infTy with
        | IRTInfer id -> id
        | _ -> failwith "expected IRTInfer"
    let flatWithInfer =
        IRTArrow ([SIdx (idxN 3); SVal infTy], f64, Some (mkId 1))
    let nestedConcrete =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow ([SVal f64], f64, None),
            Some (mkId 1))
    let result = unify subst flatWithInfer nestedConcrete
    let bound = subst.TryFind(infId)
    let pass =
        match result, bound with
        | Ok (), Some t when t = f64 -> true
        | _ -> false
    ("inference var at SVal slot across flat/nested binds correctly (normalize-aware)",
     pass,
     match result, bound with
     | Ok (), Some t when t = f64 -> "Ok, inference var bound to Float64"
     | Ok (), other -> sprintf "Ok but binding wrong: %A (expected Float64)" other
     | Error e, _ -> sprintf "expected Ok, got Error %A" e)

let private test_kind_mismatch_after_normalize_rejects () =
    // Negative case for the recursive normalized path: when two arrows
    // have the same slot count but different kinds at the same position,
    // normalization splits them differently. The resulting shapes are
    // structurally distinct and unify must reject — confirming normalize
    // doesn't conflate genuinely different slot patterns.
    //
    // t1 = [SIdx, SVal] -> R         normalizes to: [SIdx] -> [SVal] -> R
    // t2 = [SVal, SIdx] -> R         normalizes to: [SVal] -> [SIdx] -> R
    // Outer slot kinds differ → no unification.
    let t1 =
        IRTArrow ([SIdx (idxN 3); SVal f64], f64, Some (mkId 1))
    let t2 =
        IRTArrow ([SVal f64; SIdx (idxN 3)], f64, Some (mkId 1))
    let subst = Subst()
    let result = unify subst t1 t2
    let pass = not (isOk result)
    ("kind-swapped mixed-slot arrows ([SIdx,SVal] vs [SVal,SIdx]) reject after normalize",
     pass,
     if pass then "rejected as expected"
     else describeResult result)

let private test_uniform_shape_with_infer_elem_binds () =
    // Regression check: when both sides have identical uniform-kind
    // arrow shapes and one has an IRTInfer ElemType, the unification
    // descends through ArrayElem and binds the var. This case worked
    // before and must still work — normalizing a uniform arrow is a
    // no-op, so the ArrayElem pattern matches and recursion handles
    // the inner type.
    let subst = Subst()
    let infTy = subst.Fresh()
    let infId =
        match infTy with
        | IRTInfer id -> id
        | _ -> failwith "expected IRTInfer"
    let t1 =
        IRTArrow ([SIdx (idxN 3)], infTy, Some (mkId 1))
    let t2 =
        IRTArrow ([SIdx (idxN 3)], f64, Some (mkId 1))
    let result = unify subst t1 t2
    let bound = subst.TryFind(infId)
    let pass =
        match result, bound with
        | Ok (), Some t when t = f64 -> true
        | _ -> false
    ("regression check: uniform-shape ElemType IRTInfer still binds",
     pass,
     match result, bound with
     | Ok (), Some t when t = f64 -> "Ok, var bound"
     | _ -> sprintf "expected Ok with binding, got %A / %A" result bound)

let private test_unrelated_types_fail () =
    // Sanity: completely unrelated types still fail.
    let subst = Subst()
    let result = unify subst f64 (IRTNamed "Trace")
    let pass = not (isOk result)
    ("scalar vs named type rejected (sanity)",
     pass,
     if pass then "correctly rejected" else describeResult result)

// ---- Dist<r, τ> unification (typed-Dist arc, phase 1) ----------------------

let private idxTagged (n: int) (tag: string) : IRIndexType =
    { idxN n with Tag = Some tag }

let private test_dist_same_order_unifies () =
    // Two Dists of equal order, unifiable elem, same axis tag → Ok.
    let subst = Subst()
    let d1 = IRTDist (2, f64, [idxTagged 3 "I3"])
    let d2 = IRTDist (2, f64, [idxTagged 3 "I3"])
    let result = unify subst d1 d2
    ("Dist<2> unifies with Dist<2> (same elem, same axes)",
     isOk result,
     describeResult result)

let private test_dist_order_mismatch_rejects () =
    // The order guard's foundation: carried orders are part of the type.
    let subst = Subst()
    let d1 = IRTDist (2, f64, [idxTagged 3 "I3"])
    let d2 = IRTDist (4, f64, [idxTagged 3 "I3"])
    let result = unify subst d1 d2
    let pass = not (isOk result)
    ("Dist<2> vs Dist<4> rejected (order is nominal)",
     pass,
     if pass then "correctly rejected" else describeResult result)

let private test_dist_elem_infer_binds () =
    // An inference variable in elem position binds through the Dist wrapper
    // (the HM path a Dist-typed function parameter will exercise).
    let subst = Subst()
    let infTy = subst.Fresh()
    let infId = match infTy with IRTInfer id -> id | _ -> failwith "expected IRTInfer"
    let d1 = IRTDist (3, infTy, [idxTagged 3 "I3"])
    let d2 = IRTDist (3, f64, [idxTagged 3 "I3"])
    let result = unify subst d1 d2
    let bound = subst.TryFind(infId)
    let pass =
        match result, bound with
        | Ok (), Some t when t = f64 -> true
        | _ -> false
    ("Dist elem inference var binds through the wrapper",
     pass,
     sprintf "%s / bound=%A" (describeResult result) bound)

let private test_dist_vs_tuple_rejects () =
    // Strictness (the IRTIdxTagged discipline): a bare tuple of arrays never
    // implicitly becomes a Dist — only the construction intrinsic and
    // dist-typed operators produce one.
    let subst = Subst()
    let d = IRTDist (2, f64, [idxTagged 3 "I3"])
    let tup = IRTTuple [ IRTArrow ([SIdx (idxTagged 3 "I3")], f64, None)
                         IRTArrow ([SIdx (idxTagged 3 "I3")], f64, None) ]
    let r1 = unify subst d tup
    let r2 = unify subst tup d
    let pass = not (isOk r1) && not (isOk r2)
    ("Dist vs bare tuple rejected in both directions (strict, no coercion)",
     pass,
     if pass then "correctly rejected" else sprintf "%s / %s" (describeResult r1) (describeResult r2))

let private test_dist_axis_tag_mismatch_rejects () =
    // Axes are nominative like ArrayElem index types: lat-Dist ≠ lon-Dist
    // even at equal extents; synthetic (__) tags stay structural.
    let subst = Subst()
    let dLat = IRTDist (2, f64, [idxTagged 180 "lat"])
    let dLon = IRTDist (2, f64, [idxTagged 180 "lon"])
    let rNamed = unify subst dLat dLon
    let dSyn1 = IRTDist (2, f64, [idxTagged 180 "__compoundidx"])
    let dSyn2 = IRTDist (2, f64, [idxTagged 180 "__seq"])
    let rSyn = unify subst dSyn1 dSyn2
    let pass = not (isOk rNamed) && isOk rSyn
    ("Dist axis tags are nominative (user names gate, synthetic tags don't)",
     pass,
     sprintf "named: %s / synthetic: %s" (describeResult rNamed) (describeResult rSyn))

let private test_dist_zonk_erases_to_component_tuple () =
    // Zonking is the ERASURE point: a Dist<2, τ like D> leaves the checker
    // as the tuple (κ_1 : Array<τ like D>, κ_2 : Array<τ like SymIdx<2, D>>),
    // with the elem resolved through the substitution first.
    let subst = Subst()
    let infTy = subst.Fresh()
    let d = IRTDist (2, infTy, [idxTagged 3 "I3"])
    match unify subst infTy f64 with
    | Error e -> ("Dist zonk erases to component tuple", false, sprintf "setup unify failed: %A" e)
    | Ok () ->
        let zonked = zonkType subst d
        let expected = IRTTuple (Blade.IR.distComponentTypes 2 f64 [idxTagged 3 "I3"])
        let pass = (zonked = expected)
        ("Dist zonk erases to the κ_1..κ_r component tuple (elem resolved)",
         pass,
         sprintf "%A" zonked)

// ---- Runner ---------------------------------------------------------------

let runUnifyTests () : Blade.Tests.TestHarness.BlockResult =
    let tests = [
        test_identical_concrete
        test_mixed_flat_vs_split_nested
        test_reverse_order_flat_vs_split_nested
        test_differing_element_types_still_fail
        test_uniform_flat_vs_nested_still_fails
        test_three_kind_split_arrow
        test_inference_var_at_concrete_position_binds
        test_kind_mismatch_after_normalize_rejects
        test_uniform_shape_with_infer_elem_binds
        test_unrelated_types_fail
        test_dist_same_order_unifies
        test_dist_order_mismatch_rejects
        test_dist_elem_infer_binds
        test_dist_vs_tuple_rejects
        test_dist_axis_tag_mismatch_rejects
        test_dist_zonk_erases_to_component_tuple
    ]
    Blade.Tests.TestHarness.printHeader "Unify Integration"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    for testFn in tests do
        let (name, ok, detail) = testFn ()
        if ok then
            passed <- passed + 1
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name ""
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
    Blade.Tests.TestHarness.printFooter "Unify" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Unify"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
