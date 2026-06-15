module Blade.Tests.TypeStructure

open Blade.IR
open Blade.Lowering

// ============================================================================
// Type-structure test harness.
//
// Asserts the fully-deduced IR TYPE of a named binding in a Blade source
// snippet, using Blade's own type-pattern relation (matchesTypePattern). This
// is the structural counterpart to the value-level differential harness: it
// checks WHAT SHAPE an expression resolves to (rank, per-group arity +
// symmetry, SYMM/STRICT character, element type) without generating code or
// running anything.
//
// The expected type is written as a PATTERN that may be CONCRETE (every field
// specified -> strict structural assertion) or ABSTRACT (holes that match any
// concrete filling). The same relation will back surface type-ascription and
// the language server's "type of expression" queries, so the harness exercises
// the real machinery rather than a test-only comparator.
//
// Runner: `blade test type-structure`. Each test returns (name, passed, detail).
// ============================================================================

// ---- Spec DSL: build an expected-type PATTERN ergonomically ---------------
// A group spec is (arity, symmetry); arity 0 means "any arity" (a hole).
// Helpers name the common index forms.

let private freshIx (arity: int) (sym: SymmetryClass) : IRIndexType =
    // Extent is never compared by matchesTypePattern (it is a runtime value, not
    // type identity), so any placeholder is fine here.
    { Id = -1; Arity = arity; Extent = IRLit (IRLitInt 0L)
      Symmetry = sym; Tag = None; Kind = SDimension; Dependencies = [] }

/// Plain free axis (Idx).
let idx = freshIx 1 SymNone
/// Symmetric group of given rank.
let sym (r: int) = freshIx r SymSymmetric
/// Antisymmetric group of given rank.
let anti (r: int) = freshIx r SymAntisymmetric
/// Hermitian group (rank 2).
let herm = freshIx 2 SymHermitian
/// "Any arity, any-symmetry" hole for a single index slot.
let anyIx = freshIx 0 SymNone

/// Build an ARRAY type pattern from an element type and a list of index specs.
let arrOf (elem: IRType) (ixs: IRIndexType list) : IRType =
    mkArrayLike { ElemType = elem; IndexTypes = ixs; IsVirtual = false; Identity = None }

let f64 = IRTScalar ETFloat64
let i64 = IRTScalar ETInt64
let c128 = IRTScalar ETComplex128
/// Whole-type hole (matches any element type).
let anyElem = IRTInfer -1

// ---- Core assertion --------------------------------------------------------

/// Lower `src`, find the named binding, and assert its type matches `expected`
/// (as a pattern). Returns (passed, detail).
let private assertBindingType (testName: string) (src: string) (bindingName: string) (expected: IRType) : string * bool * string =
    match lower src with
    | Error e -> (testName, false, sprintf "lower failed: %s" e)
    | Ok prog ->
        match bindingTypeByName prog bindingName with
        | None -> (testName, false, sprintf "no binding named '%s' in lowered program" bindingName)
        | Some actual ->
            if matchesTypePattern expected actual then (testName, true, "")
            else (testName, false, sprintf "type mismatch for '%s':\n        expected (pattern): %A\n        actual:             %A" bindingName expected actual)

// ---- Test cases ------------------------------------------------------------
// Each returns (name, passed, detail). The cases assert STRUCTURE — the thing
// that is hard to verify through values (especially the decompact residuals).

// gram(A, A) on a complex matrix -> square Hermitian (one arity-2 SymHermitian
// group). Abstract in extent (matches any n) and we pin element type to complex.
let private test_gram_hermitian_type () =
    let src =
        "let A: Array<Complex128 like Idx<2>, Idx<3>> = [\n" +
        "    [(1.0, 1.0) : Complex128, (2.0, 0.0) : Complex128, (0.0, 1.0) : Complex128],\n" +
        "    [(3.0, -1.0) : Complex128, (1.0, 2.0) : Complex128, (2.0, 0.0) : Complex128]\n" +
        "]\n" +
        "let result = gram(A, A)\n"
    assertBindingType "gram(A,A) complex -> Hermitian arity-2" src "result"
        (arrOf c128 [herm])

// gram(A, A) on a real matrix -> square symmetric (one arity-2 SymSymmetric group).
let private test_gram_symmetric_type () =
    let src =
        "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
        "let result = gram(A, A)\n"
    assertBindingType "gram(A,A) real -> Symmetric arity-2" src "result"
        (arrOf f64 [sym 2])

// gram(A, B) distinct -> general dense (two plain arity-1 axes, no symmetry).
let private test_gram_dense_type () =
    let src =
        "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
        "let B: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 0.0, 1.0], [0.0, 1.0, 0.0]]\n" +
        "let result = gram(A, B)\n"
    assertBindingType "gram(A,B) distinct -> dense [Idx; Idx]" src "result"
        (arrOf f64 [idx; idx])

// hermitian(A) -> dense conjugate-transpose (two plain axes, no symmetry).
let private test_hermitian_adjoint_type () =
    let src =
        "let A: Array<Complex128 like Idx<2>, Idx<3>> = [\n" +
        "    [(1.0, 2.0) : Complex128, (3.0, -1.0) : Complex128, (0.0, 5.0) : Complex128],\n" +
        "    [(2.0, 1.0) : Complex128, (-1.0, 4.0) : Complex128, (6.0, 0.0) : Complex128]\n" +
        "]\n" +
        "let result = hermitian(A)\n"
    assertBindingType "hermitian(A) -> dense [Idx; Idx]" src "result"
        (arrOf c128 [idx; idx])

// Rank-2 symmetric decompact -> fully dense [Idx; Idx].
let private test_decompact_sym_type () =
    let src =
        "let A = [1.0, 2.0, 3.0]\n" +
        "let L = method_for(A, A)\n" +
        "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
        "let sym = L <@> reynolds(g) |> compute\n" +
        "let result = decompact(sym, 0)\n"
    assertBindingType "decompact(sym rank-2) -> dense [Idx; Idx]" src "result"
        (arrOf anyElem [idx; idx])

// Rank-2 antisym decompact -> fully dense [Idx; Idx].
let private test_decompact_anti_type () =
    let src =
        "let A = [1.0, 2.0, 3.0]\n" +
        "let L = method_for(A, A)\n" +
        "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
        "let anti = L <@> reynolds(g, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 0)\n"
    assertBindingType "decompact(anti rank-2) -> dense [Idx; Idx]" src "result"
        (arrOf anyElem [idx; idx])

// Rank-3 antisym decompact, peel-FIRST (d=0): residual [Idx(freed); AntisymIdx<2>].
// THIS is the structural assertion that was painful to verify through values:
// the residual shape is checked directly.
let private test_decompact_anti3_peel_first_type () =
    let src =
        "let A = [1.0, 2.0, 3.0, 4.0]\n" +
        "let L = method_for(A, A, A)\n" +
        "let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z\n" +
        "let anti = L <@> reynolds(f, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 0)\n"
    assertBindingType "decompact(anti rank-3, d=0) -> [Idx; AntisymIdx<2>]" src "result"
        (arrOf anyElem [idx; anti 2])

// Rank-3 antisym decompact, peel-LAST (d=2): residual [AntisymIdx<2>; Idx(freed)].
let private test_decompact_anti3_peel_last_type () =
    let src =
        "let A = [1.0, 2.0, 3.0, 4.0]\n" +
        "let L = method_for(A, A, A)\n" +
        "let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z\n" +
        "let anti = L <@> reynolds(f, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 2)\n"
    assertBindingType "decompact(anti rank-3, d=2) -> [AntisymIdx<2>; Idx]" src "result"
        (arrOf anyElem [anti 2; idx])

// Rank-5 antisym interior decompact (d=2): TWO residual antisym groups flanking
// the freed axis: [AntisymIdx<2>; Idx(freed); AntisymIdx<2>].
let private test_decompact_anti5_interior_type () =
    let src =
        "let A = [1.0, 2.0, 3.0, 4.0, 5.0]\n" +
        "let L = method_for(A, A, A, A, A)\n" +
        "let f = lambda(a, b, c, d, e) where comm(a, b, c, d, e) -> a * a * a * a * b * b * b * c * c * d\n" +
        "let anti = L <@> reynolds(f, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 2)\n"
    assertBindingType "decompact(anti rank-5, d=2 interior) -> [AntisymIdx<2>; Idx; AntisymIdx<2>]" src "result"
        (arrOf anyElem [anti 2; idx; anti 2])

// A deliberate NEGATIVE control: assert that the rank-3 peel-first residual is
// NOT plain dense [Idx; Idx; Idx]. This confirms the relation actually
// discriminates symmetry (it must FAIL to match the wrong pattern). Implemented
// by checking that matchesTypePattern returns false for the wrong pattern.
let private test_negative_control () =
    let src =
        "let A = [1.0, 2.0, 3.0, 4.0]\n" +
        "let L = method_for(A, A, A)\n" +
        "let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z\n" +
        "let anti = L <@> reynolds(f, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 0)\n"
    match lower src with
    | Error e -> ("negative control: anti3 d=0 is NOT [Idx;Idx;Idx]", false, sprintf "lower failed: %s" e)
    | Ok prog ->
        match bindingTypeByName prog "result" with
        | None -> ("negative control: anti3 d=0 is NOT [Idx;Idx;Idx]", false, "no 'result' binding")
        | Some actual ->
            // The residual is rank-2 ([Idx; AntisymIdx<2>]), so a rank-3 all-dense
            // pattern must NOT match — both on rank and on symmetry.
            let wrong = arrOf anyElem [idx; idx; idx]
            if not (matchesTypePattern wrong actual) then
                ("negative control: anti3 d=0 is NOT [Idx;Idx;Idx]", true, "")
            else
                ("negative control: anti3 d=0 is NOT [Idx;Idx;Idx]", false,
                 sprintf "relation wrongly matched a dense rank-3 pattern against %A" actual)

// ---- Runner ----------------------------------------------------------------

let runTypeStructureTests () : int * int =
    let tests =
        [ test_gram_hermitian_type
          test_gram_symmetric_type
          test_gram_dense_type
          test_hermitian_adjoint_type
          test_decompact_sym_type
          test_decompact_anti_type
          test_decompact_anti3_peel_first_type
          test_decompact_anti3_peel_last_type
          test_decompact_anti5_interior_type
          test_negative_control ]
    printfn ""
    printfn "=== Type-structure tests ==="
    let mutable passed = 0
    let mutable failed = 0
    for testFn in tests do
        let (name, ok, detail) = testFn ()
        if ok then
            passed <- passed + 1
            printfn "  [PASS] %s" name
        else
            failed <- failed + 1
            printfn "  [FAIL] %s" name
            if detail <> "" then printfn "      %s" detail
    printfn "Type-structure: %d passed, %d failed" passed failed
    (passed, failed)
