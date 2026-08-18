module Blade.Tests.ValidateArrow

open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
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
//   1. Valid virtual array (rank-1, scalar elem) constructs the EXACT arrow.
//   2. Valid virtual array (rank-N, scalar elem) constructs the EXACT arrow.
//   3. Invalid virtual array (arrow-typed elem) raises with a descriptive
//      error message naming the constraint.
//   4. The other smart constructors (mkArrayArrow, mkFuncArrow), which are
//      structurally constraint-safe, still produce their exact arrows — these
//      are regression checks confirming the gate is scoped correctly.
//   5. validateArrowShape's own verdicts, message for message: constraint 2
//      (arrow result), both arms of constraint 1 (stored/value after the first
//      virtual slot), and the silent-on-well-formed control.
//
// The positive cases assert CONSTRUCTED VALUES, not just "did not raise": a
// constructor that emits the wrong slot kind, drops the identity, or reorders
// slots raises nothing at all.
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

/// Run a thunk that should produce exactly `expected`. Subsumes "did not
/// raise": a raise fails the check with its message, and a successful call is
/// compared against the constructed value rather than merely being counted.
/// (Every positive test here used to assert only the absence of an exception,
/// which is satisfied by a constructor that builds the WRONG arrow — e.g. one
/// emitting SIdx slots where SIdxVirt is required, or dropping the identity.)
let private expectValue (expected: 'a) (action: unit -> 'a) : bool * string =
    try
        let actual = action ()
        if actual = expected then (true, sprintf "= %A" actual)
        else (false, sprintf "expected %A, got %A" expected actual)
    with ex ->
        (false, sprintf "raised: %s" ex.Message)

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
    // No constraint violations expected; identity is forced to None.
    let (ok, detail) = expectValue (IRTArrow ([SIdxVirt (idxN 5)], i64, None)) (fun () ->
        mkVirtualArrayArrow [idxN 5] i64)
    ("valid rank-1 virtual array (Int64 elem) = IRTArrow ([SIdxVirt], Int64, None)",
     ok, detail)

let private test_valid_virtual_array_rank_2 () =
    // Two virtual index slots, scalar elem. Both slots SIdxVirt (in order),
    // no violations.
    let (ok, detail) =
        expectValue (IRTArrow ([SIdxVirt (idxN 3); SIdxVirt (idxN 4)], f64, None)) (fun () ->
            mkVirtualArrayArrow [idxN 3; idxN 4] f64)
    ("valid rank-2 virtual array (Float64 elem) = IRTArrow ([SIdxVirt; SIdxVirt], Float64, None)",
     ok, detail)

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
    // no gate, should always work — and must carry the identity through.
    let (ok, detail) =
        expectValue (IRTArrow ([SIdx (idxN 3); SIdx (idxN 4)], f64, Some (mkId 1))) (fun () ->
            mkArrayArrow [idxN 3; idxN 4] f64 (Some (mkId 1)))
    ("stored array (all-SIdx) = IRTArrow ([SIdx; SIdx], Float64, Some id)",
     ok, detail)

let private test_valid_stored_array_with_arrow_elem () =
    // mkArrayArrow CAN take an arrow as elemType — that's a stored
    // array of functions, which is a valid §5.3 use case. Confirm
    // the gate is scoped only to virtual arrays (and that the arrow elem
    // lands in the RESULT position verbatim, not spliced into the slots).
    let arrowElem = IRTArrow ([SVal f64], i64, None)
    let (ok, detail) =
        expectValue (IRTArrow ([SIdx (idxN 3)], arrowElem, Some (mkId 1))) (fun () ->
            mkArrayArrow [idxN 3] arrowElem (Some (mkId 1)))
    ("stored array of functions (arrow elem) = IRTArrow ([SIdx], arrow, Some id) — no constraint-2",
     ok, detail)

let private test_valid_func_arrow_constructs () =
    // mkFuncArrow with all-SVal slots. Structurally constraint-safe;
    // no gate, should always work.
    let (ok, detail) = expectValue (IRTArrow ([SVal i64; SVal f64], f64, None)) (fun () ->
        mkFuncArrow [i64; f64] f64)
    ("function arrow (all-SVal) = IRTArrow ([SVal Int64; SVal Float64], Float64, None)",
     ok, detail)

let private test_nullary_func_arrow_constructs () =
    // mkFuncArrow [] is the canonical nullary-function form. Must
    // remain valid AND stay the empty-slot shape (ArrayElem deliberately
    // refuses to match it — that reservation is the whole point).
    let (ok, detail) = expectValue (IRTArrow ([], f64, None)) (fun () ->
        mkFuncArrow [] f64)
    ("nullary function = IRTArrow ([], Float64, None)",
     ok, detail)

let private test_validate_arrow_shape_directly () =
    // validateArrowShape itself on the constraint-2 case, independent of the
    // gate. The exact message is pinned: `not errs.IsEmpty` is satisfied by
    // ANY error, including a constraint-1 message misfiring on a shape whose
    // only defect is the arrow result.
    let virtSlots = [SIdxVirt (idxN 3)]
    let arrowResult = IRTArrow ([SIdx (idxN 4)], f64, Some (mkId 1))
    let errs = validateArrowShape virtSlots arrowResult
    let expected =
        [ "Virtual arrow has IRTArrow result (virtual arrays cannot contain arrays/functions)" ]
    let pass = (errs = expected)
    ("validateArrowShape: virtual-with-arrow-result reports exactly the constraint-2 message",
     pass,
     if pass then sprintf "got %d error(s)" errs.Length else sprintf "expected %A, got %A" expected errs)

// ---- Constraint 1: no stored/value slot after the first SIdxVirt ----------
// Constraint 1 had ZERO tests: every case above and below exercises constraint
// 2 (arrow result) or a shape with a single virtual slot, where "all slots
// after k" is empty and the arm cannot fire. Both of its arms are covered here.

let private test_validate_arrow_shape_stored_after_virtual () =
    // [SIdxVirt; SIdx] violates constraint 1: stored cannot follow virtual.
    // The result is a scalar, so constraint 2 stays silent — this isolates
    // constraint 1.
    let errs = validateArrowShape [SIdxVirt (idxN 3); SIdx (idxN 4)] f64
    let expected =
        [ "Slot 1 is SIdx but appears after first SIdxVirt at 0 (stored cannot follow virtual)" ]
    let pass = (errs = expected)
    ("validateArrowShape: SIdx after SIdxVirt reports the constraint-1 stored-after-virtual message",
     pass,
     if pass then sprintf "got %d error(s)" errs.Length else sprintf "expected %A, got %A" expected errs)

let private test_validate_arrow_shape_value_after_virtual () =
    // [SIdxVirt; SVal] is constraint 1's other arm, with its own wording.
    let errs = validateArrowShape [SIdxVirt (idxN 3); SVal f64] f64
    let expected =
        [ "Slot 1 is SVal but appears after first SIdxVirt at 0 (virtual arrays cannot contain functions)" ]
    let pass = (errs = expected)
    ("validateArrowShape: SVal after SIdxVirt reports the constraint-1 value-after-virtual message",
     pass,
     if pass then sprintf "got %d error(s)" errs.Length else sprintf "expected %A, got %A" expected errs)

let private test_validate_arrow_shape_accepts_wellformed () =
    // The control: an all-virtual arrow with a scalar result violates neither
    // constraint, so the validator must stay silent. Without this, the three
    // message pins above are all satisfiable by a validator that reports
    // errors indiscriminately.
    let errs = validateArrowShape [SIdxVirt (idxN 3); SIdxVirt (idxN 4)] f64
    let pass = List.isEmpty errs
    ("validateArrowShape: well-formed all-virtual arrow reports no errors (control)",
     pass,
     if pass then "no errors" else sprintf "got %A" errs)

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
        test_validate_arrow_shape_stored_after_virtual
        test_validate_arrow_shape_value_after_virtual
        test_validate_arrow_shape_accepts_wellformed
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
