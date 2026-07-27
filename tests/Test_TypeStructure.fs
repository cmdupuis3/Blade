module Blade.Tests.TypeStructure

open Blade.IR
open Blade.Types
open Blade.Tests.TestHarness
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

let private freshIx (rank: int) (sym: SymmetryClass) : IRIndexType =
    // Extent is never compared by matchesTypePattern (it is a runtime value, not
    // type identity), so any placeholder is fine here.
    { Id = -1; Rank = rank; Extent = IRLit (IRLitInt 0L)
      Symmetry = sym; Tag = None; IxKind = IxKPlain; Kind = SDimension; Dependencies = [] }

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

// ---- Blade-syntax type rendering ------------------------------------------
// The failure detail (and, optionally, the pass detail) renders types in Blade
// SURFACE syntax rather than dumping the IR record with %A. The rendering
// mirrors exactly the fields matchesTypePattern treats as type identity
// (element type, per-index arity + symmetry, rank, virtual character); runtime
// detail (extent, dependencies, ids) is intentionally not shown, since it is
// not part of the type. Pattern holes render as `_`:
//   - IRTInfer (anyElem)        -> `_`
//   - an index with Rank = 0   -> `_`  (the "any rank/symmetry" slot hole)

/// Render an element type in Blade surface syntax.
let rec formatBladeElem (ty: IRType) : string =
    match ty with
    | IRTInfer _ -> "_"
    | IRTScalar ETInt32 -> "Int32"
    | IRTScalar ETInt64 -> "Int64"
    | IRTScalar ETFloat32 -> "Float32"
    | IRTScalar ETFloat64 -> "Float64"
    | IRTScalar ETComplex64 -> "Complex64"
    | IRTScalar ETComplex128 -> "Complex128"
    | IRTScalar ETBool -> "Bool"
    | IRTScalar ETUnit -> "Unit"
    | IRTScalar ETString -> "String"
    | IRTNamed n -> n
    // A nominal index-tagged element type (e.g. an EnumIdx alias on an Int64
    // axis) renders as its surface name. The raw form is
    // `IRTIdxTagged (inner, IRefNamed name)`; show `name` (the alias the user
    // wrote), falling back to the inner element type for an anonymous tag.
    | IRTIdxTagged (_, IRefNamed name) -> name
    | IRTIdxTagged (inner, (IRefAnon _ | IRefAny)) -> formatBladeElem inner
    | _ -> formatBladeType ty   // nested arrays / arrows fall through to the full printer

/// Render an index type's extent. A concrete integer literal is shown as-is
/// (the actual deduced types carry real extents from lowering); a genuinely
/// abstract extent — the extent-agnostic pattern placeholder, or a symbolic /
/// dependent extent — renders as the `_` wildcard. This mirrors
/// matchesTypePattern's rule that extent is a runtime value and never type
/// identity, so where a type is abstract in its extent, it prints abstractly.
and formatExtent (e: IRExpr) : string =
    match e with
    | IRLit (IRLitInt n) when n > 0L -> string n
    | _ -> "_"

/// Render a single index type (one Array<...> slot) in Blade surface syntax,
/// following the canonical forms from the formalism:
///   Idx<N>, SymIdx<r, N>, AntisymIdx<r, N>, HermitianIdx<N>.
/// An arity-0 slot is a pattern hole and renders as `_`.
and formatBladeIndex (ix: IRIndexType) : string =
    match ix with
    // An irreps record's identity is its SPEC, not its extent — defer to the
    // compiler's own printer, which renders `IrrepsIdx<[(l,p,m), ...]>` and
    // wraps it in `SymIdx<k, ...>` when the record is a symmetric power of it.
    | IrrepsIdxLike _ -> Blade.IR.ppIndexType ix
    | _ ->
    let n = formatExtent ix.Extent
    match ix.Rank, ix.Symmetry with
    | 0, _ -> "_"                                            // rank hole in a pattern
    | 1, SymNone -> sprintf "Idx<%s>" n
    | r, SymSymmetric -> sprintf "SymIdx<%d, %s>" r n
    | r, SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" r n
    | 2, SymHermitian -> sprintf "HermitianIdx<%s>" n
    // Defensive fallbacks: shapes the canonical syntax doesn't define (e.g. a
    // non-symmetric group of arity > 1, or a non-rank-2 Hermitian). Surface the
    // anomaly rather than mis-rendering it as a well-formed type.
    | r, SymNone -> sprintf "Idx<%s, rank=%d?>" n r
    | r, SymHermitian -> sprintf "HermitianIdx<%s, rank=%d?>" n r

/// Render a full type in Blade surface syntax. Arrays become
/// `Array<Elem like Ix, Ix, ...>` (or `VirtualArray<...>` for virtual arrays);
/// non-array types use a compact Blade-ish form.
and formatBladeType (ty: IRType) : string =
    match ty with
    | IRTInfer _ -> "_"
    | ArrayElem a ->
        let kw = if a.IsVirtual then "VirtualArray" else "Array"
        let elem = formatBladeElem a.ElemType
        let ixs = a.IndexTypes |> List.map formatBladeIndex |> String.concat ", "
        sprintf "%s<%s like %s>" kw elem ixs
    | IRTScalar _ | IRTNamed _ -> formatBladeElem ty
    | IRTTuple ts -> sprintf "(%s)" (ts |> List.map formatBladeType |> String.concat ", ")
    | IRTComputation t -> sprintf "Computation<%s>" (formatBladeType t)
    | IRTUnit -> "Unit"
    // Anything else: fall back to the structural dump so no information is lost
    // (and so an unexpected shape is visible rather than silently mis-rendered).
    | other -> sprintf "%A" other

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
            if matchesTypePattern expected actual then
                (testName, true, formatBladeType actual)
            else (testName, false,
                  sprintf "type mismatch for '%s': expected %s, got %s"
                      bindingName (formatBladeType expected) (formatBladeType actual))

// ---- Test cases ------------------------------------------------------------
// Each returns (name, passed, detail). The cases assert STRUCTURE — the thing
// that is hard to verify through values (especially the decompact residuals).

// gram(A, A) on a complex matrix -> square Hermitian (one arity-2 SymHermitian
// group). Abstract in extent (matches any n) and we pin element type to complex.
let private test_gram_hermitian_type () =
    let src =
        "let A: Array<Complex128 like Idx<2>, Idx<3>> = [\n" +
        "    [complex(1.0, 1.0), complex(2.0, 0.0), complex(0.0, 1.0)],\n" +
        "    [complex(3.0, -1.0), complex(1.0, 2.0), complex(2.0, 0.0)]\n" +
        "]\n" +
        "let result = gram(A, A)\n"
    assertBindingType "gram(A,A) complex" src "result"
        (arrOf c128 [herm])

// gram(A, A) on a real matrix -> square symmetric (one arity-2 SymSymmetric group).
let private test_gram_symmetric_type () =
    let src =
        "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
        "let result = gram(A, A)\n"
    assertBindingType "gram(A,A) real" src "result"
        (arrOf f64 [sym 2])

// gram(A, B) distinct -> general dense (two plain arity-1 axes, no symmetry).
let private test_gram_dense_type () =
    let src =
        "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
        "let B: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 0.0, 1.0], [0.0, 1.0, 0.0]]\n" +
        "let result = gram(A, B)\n"
    assertBindingType "gram(A,B) distinct" src "result"
        (arrOf f64 [idx; idx])

// hermitian(A) -> dense conjugate-transpose (two plain axes, no symmetry).
let private test_hermitian_adjoint_type () =
    let src =
        "let A: Array<Complex128 like Idx<2>, Idx<3>> = [\n" +
        "    [complex(1.0, 2.0), complex(3.0, -1.0), complex(0.0, 5.0)],\n" +
        "    [complex(2.0, 1.0), complex(-1.0, 4.0), complex(6.0, 0.0)]\n" +
        "]\n" +
        "let result = hermitian(A)\n"
    assertBindingType "hermitian(A)" src "result"
        (arrOf c128 [idx; idx])

// Rank-2 symmetric decompact -> fully dense [Idx; Idx].
let private test_decompact_sym_type () =
    let src =
        "let A = [1.0, 2.0, 3.0]\n" +
        "let L = method_for(A, A)\n" +
        "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
        "let sym = L <@> reynolds(g) |> compute\n" +
        "let result = decompact(sym, 0)\n"
    assertBindingType "decompact(sym rank-2)" src "result"
        (arrOf anyElem [idx; idx])

// Elementwise (rank-0) map over a symmetric array must PRESERVE symmetry:
// method_for(sym) <@> (e -> ...) where `sym` is rank-2 symmetric should deduce
// a rank-2 SYMMETRIC result (same index types as the input), NOT collapse to a
// scalar or to dense [Idx; Idx]. This is the type-deduction half of the
// elementwise-over-symmetric feature (deduceOutputType copies the input S-dims
// verbatim for a rank-0 kernel). Codegen/runtime for this path is exercised
// separately by a value-checked test.
let private test_elementwise_over_symmetric_type () =
    let src =
        "let A = [1.0, 2.0, 3.0]\n" +
        "let L = method_for(A, A)\n" +
        "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
        "let sym = L <@> reynolds(g) |> compute\n" +
        "let h = lambda(e) -> e * 2.0\n" +
        "let result = method_for(sym) <@> h |> compute\n"
    assertBindingType "elementwise over symmetric" src "result"
        (arrOf anyElem [sym 2])

// Elementwise propagation, PLAIN Idx baseline: method_for(A) <@> (x -> ...)
// over a plain rank-1 vector preserves the plain Idx axis. This is the
// control case — if even a dense axis didn't propagate, the others couldn't.
let private test_elementwise_over_idx_type () =
    let src =
        "let A = [1.0, 2.0, 3.0, 4.0]\n" +
        "let h = lambda(x) -> x * 2.0\n" +
        "let result = method_for(A) <@> h |> compute\n"
    assertBindingType "elementwise over Idx" src "result"
        (arrOf anyElem [idx])

// Elementwise propagation, ANTISYMMETRIC: map over a compact antisymmetric
// array (produced by an antisym Reynolds over a repeated array) must preserve
// the AntisymIdx index type (same compact storage, sign-on-read semantics
// unchanged by a rank-0 elementwise kernel).
let private test_elementwise_over_antisym_type () =
    let src =
        "let A = [1.0, 2.0, 3.0]\n" +
        "let L = method_for(A, A)\n" +
        "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
        "let anti = L <@> reynolds(g, Antisymmetric) |> compute\n" +
        "let h = lambda(e) -> e * 2.0\n" +
        "let result = method_for(anti) <@> h |> compute\n"
    assertBindingType "elementwise over antisym" src "result"
        (arrOf anyElem [anti 2])

// Elementwise propagation, HERMITIAN: map over a Hermitian array (gram of a
// complex matrix) must preserve the HermitianIdx index type. The elementwise
// kernel is rank-0, so it does not disturb the conjugate-on-read symmetry.
let private test_elementwise_over_hermitian_type () =
    let src =
        "let A: Array<Complex128 like Idx<2>, Idx<3>> = [\n" +
        "    [complex(1.0, 2.0), complex(3.0, -1.0), complex(0.0, 5.0)],\n" +
        "    [complex(2.0, 1.0), complex(-1.0, 4.0), complex(6.0, 0.0)]\n" +
        "]\n" +
        "let herm = gram(A, A)\n" +
        "let h = lambda(e) -> e + e\n" +
        "let result = method_for(herm) <@> h |> compute\n"
    assertBindingType "elementwise over hermitian" src "result"
        (arrOf anyElem [herm])

// Elementwise propagation, DEPENDENT index (DepIdx). A DepIdx array contributes
// TWO index records (outer Idx + dependent inner). An elementwise (rank-0) map
// should PRESERVE both — the per-element kernel doesn't consume the inner dim.
// NOTE: this asserts the DESIRED behavior. deduceOutputType currently filters
// out `__depidx_inner`-tagged dims unconditionally (correct for a CONSUMING
// kernel like reduce, wrong for elementwise), so this test characterizes whether
// the elementwise-vs-consuming distinction is yet made for dependent dims.
let private test_elementwise_over_depidx_type () =
    let src =
        "type Tri3 = DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>\n" +
        "let r: Array<Float64 like Tri3> = [\n" +
        "    [1.0, 2.0, 3.0],\n" +
        "    [4.0, 5.0],\n" +
        "    [6.0]\n" +
        "]\n" +
        "let h = lambda(e) -> e * 2.0\n" +
        "let result = method_for(r) <@> h |> compute\n"
    // Desired: two records preserved (outer + dependent inner).
    assertBindingType "elementwise over depidx" src "result"
        (arrOf anyElem [idx; idx])

// Elementwise propagation, RAGGED index (RaggedIdx). A ragged dim should
// propagate through an elementwise map (the kernel maps each scalar; the row
// structure is unchanged). NOTE: deduceOutputType currently drops `__raggedidx`
// tags unconditionally; this characterizes whether elementwise preserves the
// ragged dim as #6 expects.
let private test_elementwise_over_ragged_type () =
    let src =
        "let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]\n" +
        "let h = lambda(e) -> e * 2.0\n" +
        "let result = method_for(r) <@> h |> compute\n"
    // Desired: outer Idx + ragged inner both present (2 records).
    assertBindingType "elementwise over ragged" src "result"
        (arrOf anyElem [idx; idx])

// Elementwise propagation, ENUM index (EnumIdx). An EnumIdx alias stands alone
// (the array axis is a plain Idx; the ENUM is the element type). An elementwise
// map should preserve the plain Idx axis. Expected to PASS (enum is an element-
// type concern, not a filtered inner dim).
let private test_elementwise_over_enumidx_type () =
    let src =
        "type LandType = EnumIdx<[101, 205, 307]>\n" +
        "let codes: Array<LandType like Idx<3>> = [101, 205, 307]\n" +
        "let h = lambda(e) -> e\n" +
        "let result = method_for(codes) <@> h |> compute\n"
    assertBindingType "elementwise over enumidx" src "result"
        (arrOf anyElem [idx])

// JOINT PRODUCT SYMMETRY (d=2) type deduction — CORRECTED (arc 1). One
// identity group over a multi-dim array licenses only the JOINT (diagonal)
// symmetry: whole argument index tuples are interchangeable, never each data
// dimension independently (docs/formalism.md §8.4/§12.4; proofs.md
// per_dim_swap_not_symmetry refutes the old per-dim SymIdx<2,2>, SymIdx<2,3>
// prediction, and counting_general_C shows that shape cannot even hold the
// result). For A: Array<.., Idx<2>, Idx<3>> with comm(x,y), the argument's
// dense S-block fuses into one compound axis of extent 6, and the output is
// the single joint record SymIdx<2, 6> — speedup 2!, not (2!)^2.
let private test_product_symmetry_2d_type () =
    let src =
        "let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
        "let L = method_for(A, A)\n" +
        "let f = lambda(x, y) where comm(x, y) -> x * y\n" +
        "let result = L <@> f |> compute\n"
    // Corrected: ONE joint symmetric record over the compound spatial space.
    assertBindingType "joint product symmetry 2D (A,A)" src "result"
        (arrOf anyElem [sym 2])

// JOINT PRODUCT SYMMETRY via FIBER KERNEL — CORRECTED (arc 1). A comm kernel
// consuming a TimeIdx fiber from each copy of A: Array<.., LatIdx, LonIdx,
// TimeIdx> leaves (Lat, Lon) as each argument's S-block. The old prediction
// symmetrized each outer dim independently (SymIdx<2,Lat>, SymIdx<2,Lon> —
// the (2!)^2 basis); that is refuted (per_dim_swap_not_symmetry): swapping
// only the Lat coordinates across the two fiber arguments is NOT an output
// symmetry. Corrected: the (Lat, Lon) block fuses into one compound axis
// (extent Lat*Lon = 6) and the output is the single joint SymIdx<2, 6>;
// Time is consumed by the reduce and absent, as before.
let private test_product_symmetry_fiber_type () =
    let src =
        "type LatIdx = Idx<2>\n" +
        "type LonIdx = Idx<3>\n" +
        "type TimeIdx = Idx<4>\n" +
        "let A: Array<Float64 like LatIdx, LonIdx, TimeIdx> = " +
        "[[[1.0,2.0,3.0,4.0],[5.0,6.0,7.0,8.0],[9.0,10.0,11.0,12.0]]," +
        "[[13.0,14.0,15.0,16.0],[17.0,18.0,19.0,20.0],[21.0,22.0,23.0,24.0]]]\n" +
        "let L = method_for(A, A)\n" +
        "let k = lambda(a: Array<Float64 like TimeIdx>, b: Array<Float64 like TimeIdx>) where comm(a, b) -> reduce(a, (+))\n" +
        "let result = L <@> k |> compute\n"
    // Corrected: one joint symmetric record over (Lat x Lon); Time consumed.
    assertBindingType "joint product symmetry fiber (A,A over Time)" src "result"
        (arrOf anyElem [sym 2])

// Rank-2 antisym decompact -> fully dense [Idx; Idx].
let private test_decompact_anti_type () =
    let src =
        "let A = [1.0, 2.0, 3.0]\n" +
        "let L = method_for(A, A)\n" +
        "let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y\n" +
        "let anti = L <@> reynolds(g, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 0)\n"
    assertBindingType "decompact(anti rank-2)" src "result"
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
    assertBindingType "decompact(anti rank-3, d=0)" src "result"
        (arrOf anyElem [idx; anti 2])

// Rank-3 antisym decompact, peel-LAST (d=2): residual [AntisymIdx<2>; Idx(freed)].
let private test_decompact_anti3_peel_last_type () =
    let src =
        "let A = [1.0, 2.0, 3.0, 4.0]\n" +
        "let L = method_for(A, A, A)\n" +
        "let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z\n" +
        "let anti = L <@> reynolds(f, Antisymmetric) |> compute\n" +
        "let result = decompact(anti, 2)\n"
    assertBindingType "decompact(anti rank-3, d=2)" src "result"
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
    assertBindingType "decompact(anti rank-5, d=2 interior)" src "result"
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
    let wrong = arrOf anyElem [idx; idx; idx]
    let testName = sprintf "negative control: anti3 d=0 is NOT %s" (formatBladeType wrong)
    match lower src with
    | Error e -> (testName, false, sprintf "lower failed: %s" e)
    | Ok prog ->
        match bindingTypeByName prog "result" with
        | None -> (testName, false, "no 'result' binding")
        | Some actual ->
            // The residual is rank-2 (Array<_ like Idx, AntisymIdx<2>>), so a
            // rank-3 all-dense pattern must NOT match — on rank and on symmetry.
            if not (matchesTypePattern wrong actual) then
                (testName, true, formatBladeType actual)
            else
                (testName, false,
                 sprintf "relation wrongly matched a dense rank-3 pattern against %s" (formatBladeType actual))

// ---- F10: §2.7's IR-reachability claim, EXECUTED ---------------------------
// docs/plan-transforms-as-types.md §2.7 asserted — "read, not yet executed" —
// that the IR target record for `SymIdx<k, IrrepsIdx<spec>>` is ALREADY what
// inference produces: deduceOutputType's size-≥2 group path (IR.fs:2126) builds
// `{ rep with Rank = groupRank; Symmetry = groupSymmetry }`, so the irreps
// `Tag` and `IxKind` survive verbatim, and the symmetry-vs-kind classification
// tests Symmetry first. These two tests run that claim.
//
// They assert the index record's identity FIELDS directly rather than going
// through matchesTypePattern, which deliberately treats a `__`-prefixed tag in
// the pattern as "don't care" (IR.fs:906-910) — and the irreps tag is exactly
// such a tag. The spec payload IS the identity here, so it has to be compared.

/// Shared prologue: an irreps-typed rank-1 array and a comm kernel over two
/// copies of it. spec = [(0,0,2), (1,1,1)] -> total_dim = 2*1 + 1*3 = 5.
let private irrepsCommPrologue =
    "let static spec = [(0, 0, 2), (1, 1, 1)]\n" +
    "let x: Array<Float like IrrepsIdx<spec>> = [1.0, 2.0, 3.0, 4.0, 5.0]\n" +
    "let L = method_for(x, x)\n" +
    "let f = lambda(a, b) where comm(a, b) -> a * b\n"

/// Lower `src` and return the SOLE index record of `bindingName`'s array type.
let private soleIndexOf (src: string) (bindingName: string) : Result<IRIndexType, string> =
    match lower src with
    | Error e -> Error (sprintf "lower failed: %s" e)
    | Ok prog ->
        match bindingTypeByName prog bindingName with
        | None -> Error (sprintf "no binding named '%s' in lowered program" bindingName)
        | Some (ArrayElem a) ->
            match a.IndexTypes with
            | [ix] -> Ok ix
            | ixs -> Error (sprintf "expected exactly one index record, got %d" ixs.Length)
        | Some other -> Error (sprintf "binding '%s' is not an array: %s" bindingName (formatBladeType other))

// F10 (a): the INFERENCE side. `comm` over two identical rank-1 IrrepsIdx-typed
// arrays must deduce a rank-2 SYMMETRIC index that still carries the spec —
// Tag = the same mkIrrepsTag payload, IxKind = IxKIrreps, extent = total_dim.
let private test_comm_over_irreps_infers_sym_irreps () =
    let name = "F10 comm over IrrepsIdx infers SymIdx<2, IrrepsIdx<spec>>"
    let src = irrepsCommPrologue + "let result = L <@> f |> compute\n"
    match soleIndexOf src "result" with
    | Error e -> (name, false, e)
    | Ok ix ->
        let wantTag = Some (mkIrrepsTag None [(0, 0, 2); (1, 1, 1)])
        let checks =
            [ (ix.Rank = 2), sprintf "Rank = %d, want 2" ix.Rank
              (ix.Symmetry = SymSymmetric), sprintf "Symmetry = %A, want SymSymmetric" ix.Symmetry
              (ix.IxKind = IxKIrreps), sprintf "IxKind = %A, want IxKIrreps" ix.IxKind
              (ix.Tag = wantTag), sprintf "Tag = %A, want %A (spec payload lost)" ix.Tag wantTag
              (ix.Extent = IRLit (IRLitInt 5L)), sprintf "Extent = %A, want IRLit 5 (= total_dim)" ix.Extent ]
        match checks |> List.tryFind (fst >> not) with
        | Some (_, why) -> (name, false, why)
        | None -> (name, true, Blade.IR.ppIndexType ix)

// F10 (b): the ROUND TRIP. Writing the (newly writable) annotation on that same
// output must produce the SAME index record the inference produced — same rank,
// symmetry, spec tag, kind, and extent. Ids are allocation counters and are
// never type identity, so they are excluded.
let private test_sym_irreps_annotation_matches_inference () =
    let name = "F10 written SymIdx<2, IrrepsIdx<spec>> = inferred record"
    let inferred = irrepsCommPrologue + "let result = L <@> f |> compute\n"
    let annotated =
        irrepsCommPrologue
        + "let result: Array<Float like SymIdx<2, IrrepsIdx<spec>>> = L <@> f |> compute\n"
    match soleIndexOf inferred "result", soleIndexOf annotated "result" with
    | Error e, _ -> (name, false, sprintf "inferred side: %s" e)
    | _, Error e -> (name, false, sprintf "annotated side: %s" e)
    | Ok inf, Ok ann ->
        let identity (ix: IRIndexType) =
            (ix.Rank, ix.Symmetry, ix.Tag, ix.IxKind, ix.Kind, ix.Extent, ix.Dependencies)
        if identity inf = identity ann then (name, true, Blade.IR.ppIndexType ann)
        else
            (name, false,
             sprintf "inferred %s vs annotated %s (identity fields differ)"
                 (Blade.IR.ppIndexType inf) (Blade.IR.ppIndexType ann))

// ---- Runner ----------------------------------------------------------------

let runTypeStructureTests () : Blade.Tests.TestHarness.BlockResult =
    let tests =
        [ test_gram_hermitian_type
          test_gram_symmetric_type
          test_gram_dense_type
          test_hermitian_adjoint_type
          test_decompact_sym_type
          test_elementwise_over_symmetric_type
          test_elementwise_over_idx_type
          test_elementwise_over_antisym_type
          test_elementwise_over_hermitian_type
          test_elementwise_over_depidx_type
          test_elementwise_over_ragged_type
          test_elementwise_over_enumidx_type
          test_product_symmetry_2d_type
          test_product_symmetry_fiber_type
          test_decompact_anti_type
          test_decompact_anti3_peel_first_type
          test_decompact_anti3_peel_last_type
          test_decompact_anti5_interior_type
          test_negative_control
          test_comm_over_irreps_infers_sym_irreps
          test_sym_irreps_annotation_matches_inference ]
    Blade.Tests.TestHarness.printHeader "Type-Structure"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames = []
    for testFn in tests do
        let (name, ok, detail) = testFn ()
        if ok then
            passed <- passed + 1
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            Blade.Tests.TestHarness.resultLine Blade.Tests.TestHarness.Fail name detail
    Blade.Tests.TestHarness.printFooter "Type-Structure" [sprintf "%d passed" passed; sprintf "%d failed" failed]
    { Block = "Type-Structure"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
