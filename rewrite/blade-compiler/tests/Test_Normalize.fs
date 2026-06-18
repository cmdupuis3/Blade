module Blade.Tests.Normalize

open Blade.IR
open Blade.Tests.TestHarness

// ============================================================================
// IR-level unit tests for the type normalizer (Segment 6, Path B-nested).
// ============================================================================
//
// These tests construct IRType values directly and assert on the output of
// `normalize ToNested` and `irTypeEquiv`. They live at the IR level rather
// than going through Blade source because the normalizer is a pure IR-to-IR
// transformation; testing through source would couple normalizer behavior to
// producer/lowering quirks.
//
// Runner: `blade test normalize`. Each test returns (name, passed, detail).

// ---- Helpers for terse IRType construction --------------------------------

let private f64 = IRTScalar ETFloat64
let private i64 = IRTScalar ETInt64

/// Build an IRIndexType for `Idx<N>`. Just enough fields for normalize to
/// treat them opaquely. The normalizer doesn't inspect index type internals.
let private idxN (n: int) : IRIndexType =
    {
        Id = n  // ad-hoc handle, only used for equality in the tests
        Arity = 1
        Extent = IRLit (IRLitInt (int64 n))
        Symmetry = SymNone
        Tag = None
        Kind = SDimension
        Dependencies = []
    }

/// Build a fresh AIDLiteral identity with the given numeric handle.
let private mkId (handle: int) : ArrayIdentity = AIDLiteral handle

// ---- Test cases -----------------------------------------------------------

let private test_noop_on_uniform () =
    // Uniform-slot arrows should normalize to themselves (structural eq).
    let storedArr =
        IRTArrow (
            [SIdx (idxN 3); SIdx (idxN 4)],
            f64,
            Some (mkId 1))
    let result = normalize ToNested storedArr
    let pass = result = storedArr
    ("noop on uniform stored array",
     pass,
     if pass then "preserved as-is"
     else sprintf "expected %A, got %A" storedArr result)

let private test_noop_on_pure_function () =
    // Pure function (all SVal) — uniform, should not change.
    let func = IRTArrow ([SVal f64; SVal f64], f64, None)
    let result = normalize ToNested func
    let pass = result = func
    ("noop on pure function",
     pass,
     if pass then "preserved as-is"
     else sprintf "expected %A, got %A" func result)

let private test_split_array_of_functions () =
    // [SIdx; SVal] -> R with identity becomes:
    //   [SIdx] -> ([SVal] -> R, None) with identity
    // The outer SIdx group inherits the identity; the inner SVal group gets None.
    let id1 = mkId 1
    let flat = IRTArrow ([SIdx (idxN 3); SVal f64], f64, Some id1)
    let expected =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow ([SVal f64], f64, None),
            Some id1)
    let result = normalize ToNested flat
    let pass = result = expected
    ("split [SIdx; SVal] into nested with identity on outer",
     pass,
     if pass then "split correctly"
     else sprintf "expected %A, got %A" expected result)

let private test_split_function_returning_array () =
    // [SVal; SIdx] -> R with no identity becomes:
    //   [SVal] -> ([SIdx] -> R, None), None
    // Both groups get None — function side has no identity; inner array
    // has no source identity (it's a returned value).
    let flat = IRTArrow ([SVal f64; SIdx (idxN 5)], f64, None)
    let expected =
        IRTArrow (
            [SVal f64],
            IRTArrow ([SIdx (idxN 5)], f64, None),
            None)
    let result = normalize ToNested flat
    let pass = result = expected
    ("split [SVal; SIdx] into nested",
     pass,
     if pass then "split correctly"
     else sprintf "expected %A, got %A" expected result)

let private test_split_three_kind_groups () =
    // [SIdx; SIdx; SVal; SIdx] groups as [SIdx;SIdx] | [SVal] | [SIdx]
    // Outer keeps identity, middle and inner get None.
    let id1 = mkId 7
    let flat =
        IRTArrow (
            [SIdx (idxN 2); SIdx (idxN 3); SVal i64; SIdx (idxN 4)],
            f64,
            Some id1)
    let expected =
        IRTArrow (
            [SIdx (idxN 2); SIdx (idxN 3)],
            IRTArrow (
                [SVal i64],
                IRTArrow ([SIdx (idxN 4)], f64, None),
                None),
            Some id1)
    let result = normalize ToNested flat
    let pass = result = expected
    ("split three slot-kind groups",
     pass,
     if pass then "split correctly"
     else sprintf "expected %A, got %A" expected result)

let private test_recurse_into_tuple () =
    // Mixed-slot arrow inside a tuple should still get normalized.
    let mixedInner = IRTArrow ([SIdx (idxN 3); SVal f64], f64, None)
    let expectedInner =
        IRTArrow ([SIdx (idxN 3)], IRTArrow ([SVal f64], f64, None), None)
    let tup = IRTTuple [f64; mixedInner; i64]
    let expected = IRTTuple [f64; expectedInner; i64]
    let result = normalize ToNested tup
    let pass = result = expected
    ("recurse into IRTTuple",
     pass,
     if pass then "recursed and split"
     else sprintf "expected %A, got %A" expected result)

let private test_recurse_into_sval_slot () =
    // An SVal slot whose carried type is itself a mixed-slot arrow should
    // also be normalized. Models a higher-order function whose argument is
    // an array-of-functions.
    let mixedArg = IRTArrow ([SIdx (idxN 3); SVal f64], f64, None)
    let expectedArg =
        IRTArrow ([SIdx (idxN 3)], IRTArrow ([SVal f64], f64, None), None)
    let outer = IRTArrow ([SVal mixedArg], f64, None)
    let expected = IRTArrow ([SVal expectedArg], f64, None)
    let result = normalize ToNested outer
    let pass = result = expected
    ("recurse into SVal-carried IRType",
     pass,
     if pass then "inner arrow normalized"
     else sprintf "expected %A, got %A" expected result)

let private test_idempotence () =
    // normalize(normalize(t)) = normalize(t) for any t.
    let flat =
        IRTArrow (
            [SIdx (idxN 2); SIdx (idxN 3); SVal i64; SIdx (idxN 4)],
            f64,
            Some (mkId 1))
    let once = normalize ToNested flat
    let twice = normalize ToNested once
    let pass = once = twice
    ("idempotence: normalize(normalize(t)) = normalize(t)",
     pass,
     if pass then "idempotent"
     else sprintf "first=%A second=%A" once twice)

let private test_equiv_flat_vs_nested () =
    // The §5.2 identity: flat [SIdx; SIdx] and nested [SIdx]->[SIdx] are
    // the same type modulo normalization. irTypeEquiv must agree.
    let id1 = mkId 1
    let flat =
        IRTArrow ([SIdx (idxN 2); SIdx (idxN 3)], f64, Some id1)
    let nested =
        IRTArrow (
            [SIdx (idxN 2)],
            IRTArrow ([SIdx (idxN 3)], f64, None),
            Some id1)
    // Note: under ToNested, flat uniform-kind arrows DON'T split (uniform
    // is the canonical form for uniform input). So flat and nested are
    // NOT equivalent under ToNested. This test documents that —
    // equivalence post-§5.2 is a B-flat concept, not B-nested.
    let equiv = irTypeEquiv flat nested
    let pass = not equiv  // they should NOT be equivalent under ToNested
    ("flat-vs-nested uniform are NOT equivalent under ToNested (documented gap)",
     pass,
     if pass then "ToNested keeps uniform-kind multi-slot as a single arrow; §5.2 collapse is B-flat work"
     else "unexpectedly treated flat and nested as equivalent")

let private test_equiv_mixed_split () =
    // [SIdx; SVal] (flat) and [SIdx] -> [SVal] (nested) must be equivalent
    // under ToNested, because the flat form is non-canonical (mixed-kind)
    // and normalizes to the nested form.
    let id1 = mkId 1
    let flat = IRTArrow ([SIdx (idxN 3); SVal f64], f64, Some id1)
    let nested =
        IRTArrow (
            [SIdx (idxN 3)],
            IRTArrow ([SVal f64], f64, None),
            Some id1)
    let equiv = irTypeEquiv flat nested
    ("mixed-slot flat and nested are equivalent under ToNested",
     equiv,
     if equiv then "normalized to same canonical form"
     else sprintf "normalize(flat)=%A normalize(nested)=%A"
                  (normalize ToNested flat) (normalize ToNested nested))

let private test_empty_slots_preserved () =
    // [] -> R (nullary function) is uniform-kind (vacuously). Must not be
    // mangled by normalize.
    let nullaryFn = IRTArrow ([], f64, None)
    let result = normalize ToNested nullaryFn
    let pass = result = nullaryFn
    ("empty-slot arrow preserved (nullary function)",
     pass,
     if pass then "preserved"
     else sprintf "expected %A, got %A" nullaryFn result)

// ---- Runner ---------------------------------------------------------------

/// Run all normalizer tests, return (passed, failed) counts.
let runNormalizeTests () : Blade.Tests.TestHarness.BlockResult =
    let tests = [
        test_noop_on_uniform
        test_noop_on_pure_function
        test_split_array_of_functions
        test_split_function_returning_array
        test_split_three_kind_groups
        test_recurse_into_tuple
        test_recurse_into_sval_slot
        test_idempotence
        test_equiv_flat_vs_nested
        test_equiv_mixed_split
        test_empty_slots_preserved
    ]
    Blade.Tests.TestHarness.printHeader "IR Normalize"
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
    Blade.Tests.TestHarness.printFooter "Normalize" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Normalize"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
