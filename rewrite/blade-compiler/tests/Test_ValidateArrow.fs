module Blade.Tests.ValidateArrow

open Blade.IR
open Blade.Types
open Blade.Tests.TestHarness

// ============================================================================
// Tests for the validateArrowShape gate at mkVirtualArrayArrow entry
// (Segment 6 follow-on: validator-as-gate).
// ============================================================================
//
// validateArrowShape was previously defined but never invoked — its doc
// comment described constraints as "enforced", but no gate actually
// enforced them. These tests verify that mkVirtualArrayArrow now refuses
// to construct invalid shapes, while still permitting valid ones.
//
// What we're verifying:
//   1. Valid virtual array (rank-1, scalar elem) constructs without raising.
//   2. Valid virtual array (rank-N, scalar elem) constructs without raising.
//   3. Invalid virtual array (arrow-typed elem) raises with a descriptive
//      error message naming the constraint.
//   4. The other smart constructors (mkArrayArrow, mkFuncArrow), which are
//      structurally constraint-safe, still work without raising — these
//      are regression checks confirming the gate is scoped correctly.
//
// Note: these are compiler-invariant checks, not user-facing diagnostics.
// User-facing rejection of weird source-level inputs (e.g., reverse of
// a 2D array, if such a path could reach mkVirtualArrayArrow) is a
// separate TypeCheck-level concern.
//
// Runner: `blade test validate-arrow`.

// ---- Helpers --------------------------------------------------------------

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

/// Run a thunk that should not raise. Returns (true, "") on success,
/// (false, error message) if it raised.
let private expectNoRaise (action: unit -> 'a) : bool * string =
    try
        let _ = action ()
        (true, "")
    with ex ->
        (false, ex.Message)

/// Run a thunk that should raise. Returns (true, "") on raise,
/// (false, "did not raise") if it didn't.
let private expectRaise (action: unit -> 'a) : bool * string =
    try
        let _ = action ()
        (false, "expected exception, got success")
    with ex ->
        (true, ex.Message)

// ---- Test cases -----------------------------------------------------------

let private test_valid_virtual_array_scalar_elem () =
    // Standard virtual array: range<Idx<5>> with Int64 elements.
    // No constraint violations expected.
    let (ok, detail) = expectNoRaise (fun () ->
        mkVirtualArrayArrow [idxN 5] i64)
    ("valid rank-1 virtual array (Int64 elem) constructs without raising",
     ok,
     if ok then "constructed" else sprintf "raised: %s" detail)

let private test_valid_virtual_array_rank_2 () =
    // Two virtual index slots, scalar elem. Both slots SIdxVirt, no
    // violations.
    let (ok, detail) = expectNoRaise (fun () ->
        mkVirtualArrayArrow [idxN 3; idxN 4] f64)
    ("valid rank-2 virtual array (Float64 elem) constructs without raising",
     ok,
     if ok then "constructed" else sprintf "raised: %s" detail)

let private test_invalid_virtual_array_arrow_elem () =
    // The constraint-2 violation: virtual array with an arrow as
    // elem type. Gate must raise.
    let arrowElem = IRTArrow ([SIdx (idxN 3)], f64, Some (mkId 1))
    let (raised, msg) = expectRaise (fun () ->
        mkVirtualArrayArrow [idxN 5] arrowElem)
    let mentionsConstraint = msg.Contains("Virtual arrow") || msg.Contains("IRTArrow result")
    let pass = raised && mentionsConstraint
    ("invalid virtual array (arrow elem) raises with descriptive message",
     pass,
     if pass then "raised with expected message"
     elif raised then sprintf "raised but message unclear: %s" msg
     else "did not raise — gate is not firing")

let private test_valid_stored_array_constructs () =
    // mkArrayArrow with all-SIdx slots. Structurally constraint-safe;
    // no gate, should always work.
    let (ok, detail) = expectNoRaise (fun () ->
        mkArrayArrow [idxN 3; idxN 4] f64 (Some (mkId 1)))
    ("stored array (all-SIdx) constructs without raising",
     ok,
     if ok then "constructed" else sprintf "raised: %s" detail)

let private test_valid_stored_array_with_arrow_elem () =
    // mkArrayArrow CAN take an arrow as elemType — that's a stored
    // array of functions, which is a valid §5.3 use case. Confirm
    // the gate is scoped only to virtual arrays.
    let arrowElem = IRTArrow ([SVal f64], i64, None)
    let (ok, detail) = expectNoRaise (fun () ->
        mkArrayArrow [idxN 3] arrowElem (Some (mkId 1)))
    ("stored array of functions (arrow elem) constructs — stored has no constraint-2",
     ok,
     if ok then "constructed (correctly: §5.3 array-of-functions case)"
     else sprintf "raised: %s — stored array should not be gated" detail)

let private test_valid_func_arrow_constructs () =
    // mkFuncArrow with all-SVal slots. Structurally constraint-safe;
    // no gate, should always work.
    let (ok, detail) = expectNoRaise (fun () ->
        mkFuncArrow [i64; f64] f64)
    ("function arrow (all-SVal) constructs without raising",
     ok,
     if ok then "constructed" else sprintf "raised: %s" detail)

let private test_nullary_func_arrow_constructs () =
    // mkFuncArrow [] is the canonical nullary-function form. Must
    // remain valid (the empty-slot form is explicitly reserved).
    let (ok, detail) = expectNoRaise (fun () ->
        mkFuncArrow [] f64)
    ("nullary function (empty SVal slots) constructs",
     ok,
     if ok then "constructed" else sprintf "raised: %s" detail)

let private test_validate_arrow_shape_directly () =
    // Sanity check on validateArrowShape itself: it should return a
    // non-empty error list for the constraint-2 case. Tests the
    // underlying validator function independent of the gate.
    let virtSlots = [SIdxVirt (idxN 3)]
    let arrowResult = IRTArrow ([SIdx (idxN 4)], f64, Some (mkId 1))
    let errs = validateArrowShape virtSlots arrowResult
    let pass = not errs.IsEmpty
    ("validateArrowShape directly flags virtual-with-arrow-result",
     pass,
     if pass then sprintf "got %d error(s): %s" errs.Length (List.head errs)
     else "validateArrowShape returned empty list for invalid shape — bug in validator itself")

// ---- Runner ---------------------------------------------------------------

let runValidateArrowTests () : Blade.Tests.TestHarness.BlockResult =
    let tests = [
        test_valid_virtual_array_scalar_elem
        test_valid_virtual_array_rank_2
        test_invalid_virtual_array_arrow_elem
        test_valid_stored_array_constructs
        test_valid_stored_array_with_arrow_elem
        test_valid_func_arrow_constructs
        test_nullary_func_arrow_constructs
        test_validate_arrow_shape_directly
    ]
    Blade.Tests.TestHarness.printHeader "Validate Arrow Gate"
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
    Blade.Tests.TestHarness.printFooter "Validate Arrow" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Validate Arrow"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
