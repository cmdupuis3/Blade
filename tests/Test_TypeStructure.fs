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
    // A wreath record's extent lives inside the IROrbitClass marker, so read it
    // through orbitBaseExtent (the identity on every other record).
    let n = formatExtent (Blade.IR.orbitBaseExtent ix)
    match ix.Rank, ix.Symmetry with
    | 0, _ -> "_"                                            // rank hole in a pattern
    | 1, SymNone -> sprintf "Idx<%s>" n
    | r, SymSymmetric -> sprintf "SymIdx<%d, %s>" r n
    | r, SymAntisymmetric -> sprintf "AntisymIdx<%d, %s>" r n
    | 2, SymHermitian -> sprintf "HermitianIdx<%s>" n
    // The LEVEL LIST is the type here — a rank-only rendering would name a
    // different class — so it is what gets printed. (The SparseIdx addition
    // skipped this function and the compiler's own printers; not repeated.)
    | _, SymWreath ->
        sprintf "OrbIdx<%s, %s>" (Blade.IR.ppOrbitLevels (Blade.IR.orbitLevelsOf ix)) n
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

// ---- Elementwise over a NON-DENSE inner dim: assert the IxKind ------------
// A depidx / ragged array contributes TWO index records (outer + a dependent or
// ragged inner). An elementwise (rank-0) kernel consumes nothing, so BOTH must
// survive AS THEMSELVES: deduceOutputType's consumed-dim filter (IR.fs) drops
// `IxKDepInner`/ragged-family dims only `when kernelConsumesInner`, and its
// size-1-group path copies the source record verbatim (Id refreshed only), so
// Tag and IxKind reach the output unchanged.
//
// These two DELIBERATELY bypass matchesTypePattern. That relation compares
// Rank, Symmetry, DimensionKind (S/T) and the Tag — but NOT `IxKind`, and a
// `__`-prefixed Tag in the pattern is treated as don't-care anyway (IR.fs
// matchesIndexPattern). So the pattern `[idx; idx]` these tests used to assert
// is satisfied *identically* by a densified output: the exact bug the tests
// exist to catch would pass. IxKind is the discriminator, so assert it.

/// Lower `src` and return the index records of `bindingName`'s array type.
let private indexRecordsOf (src: string) (bindingName: string) : Result<IRIndexType list, string> =
    match lower src with
    | Error e -> Error (sprintf "lower failed: %s" e)
    | Ok prog ->
        match bindingTypeByName prog bindingName with
        | None -> Error (sprintf "no binding named '%s' in lowered program" bindingName)
        | Some (ArrayElem a) -> Ok a.IndexTypes
        | Some other -> Error (sprintf "binding '%s' is not an array: %s" bindingName (formatBladeType other))

/// Assert the per-slot (IxKind, Tag) of `bindingName`'s index records — the
/// two fields matchesTypePattern cannot see.
let private assertIndexKinds (testName: string) (src: string) (bindingName: string)
                             (expected: (IxKind * string option) list) : string * bool * string =
    match indexRecordsOf src bindingName with
    | Error e -> (testName, false, e)
    | Ok ixs ->
        let actual = ixs |> List.map (fun ix -> (ix.IxKind, ix.Tag))
        let shapeOk =
            ixs |> List.forall (fun ix ->
                ix.Rank = 1 && ix.Symmetry = SymNone && ix.Kind = SDimension)
        if actual = expected && shapeOk then
            (testName, true, sprintf "%A" actual)
        elif actual = expected then
            (testName, false, sprintf "kinds/tags match %A but a slot is not rank-1/SymNone/S-dim: %A"
                                      actual (ixs |> List.map (fun ix -> ix.Rank, ix.Symmetry, ix.Kind)))
        else
            (testName, false, sprintf "expected %A, got %A" expected actual)

// Elementwise propagation, DEPENDENT index (DepIdx). `Array<_ like Tri3>` lowers
// to [outer tagged __depidx_outer / IxKDepOuter; inner tagged __depidx_inner /
// IxKDepInner] (TypeCheck.fs lowerIndexTypeList's TyDepIdx arm, reached through
// the TyNamed alias recursion). A rank-0 kernel must hand both back unchanged.
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
    assertIndexKinds "elementwise over depidx preserves IxKDepInner" src "result"
        [ (IxKDepOuter, Some "__depidx_outer"); (IxKDepInner, Some "__depidx_inner") ]

// Elementwise propagation, RAGGED index. A jagged literal lowers to [plain outer
// (extent = row count); inner tagged __raggedidx_inline / IxKRaggedInline]
// (TypeCheck.fs's isRaggedAtSecondLevel branch). The elementwise map maps each
// scalar, so the row structure — and therefore the ragged kind — is unchanged.
let private test_elementwise_over_ragged_type () =
    let src =
        "let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]\n" +
        "let h = lambda(e) -> e * 2.0\n" +
        "let result = method_for(r) <@> h |> compute\n"
    assertIndexKinds "elementwise over ragged preserves IxKRaggedInline" src "result"
        [ (IxKPlain, None); (IxKRaggedInline, Some "__raggedidx_inline") ]

// Elementwise propagation, ENUM index (EnumIdx). An EnumIdx alias stands alone
// (the array axis is a plain Idx; the ENUM is the element type). An elementwise
// map preserves the plain Idx axis AND the nominal enum tag on the element.
//
// The element-type half needs its own assertion: with `anyElem` in the pattern
// the claim "enum is an element-type concern" is exactly what goes unchecked —
// `arrOf anyElem [idx]` is satisfied by a plain `Array<Float64 like Idx<3>>`,
// so a dropped alias would pass. matchesTypePattern can't express "an
// IRTIdxTagged with THIS name but any inner" either (IRTIdxTagged falls to its
// exact-equality fallback, where a hole in the inner position is not a hole),
// so the element type is destructured directly. The inner scalar is asserted
// only to be an integer: Int32-vs-Int64 for enum backing is a literal-typing
// decision, not part of the alias-survival claim.
let private test_elementwise_over_enumidx_type () =
    let name = "elementwise over enumidx (axis stays Idx, elem keeps Nat<LandType>)"
    let src =
        "type LandType = EnumIdx<[101, 205, 307]>\n" +
        "let codes: Array<LandType like Idx<3>> = [101, 205, 307]\n" +
        "let h = lambda(e) -> e\n" +
        "let result = method_for(codes) <@> h |> compute\n"
    match lower src with
    | Error e -> (name, false, sprintf "lower failed: %s" e)
    | Ok prog ->
        match bindingTypeByName prog "result" with
        | None -> (name, false, "no 'result' binding")
        | Some actual ->
            if not (matchesTypePattern (arrOf anyElem [idx]) actual) then
                (name, false, sprintf "axis shape: expected Array<_ like Idx<_>>, got %s" (formatBladeType actual))
            else
                match actual with
                | ArrayElem a ->
                    match a.ElemType with
                    | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), IRefNamed "LandType") ->
                        (name, true, formatBladeType actual)
                    | other ->
                        (name, false,
                         sprintf "element type lost the enum alias: expected Nat<LandType>, got %A" other)
                | _ -> (name, false, "not an array after the pattern matched (unreachable)")

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
// The retired transforms-as-types plan §2.7 asserted — "read, not yet executed" —
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

// ---- Stage 4: multi-axis irreps under joint fusion -------------------------
// `fuseJointSLevels` now admits IrrepsIdx S-dims beside plain ones, so a
// comm-covered identity group over a batch x irreps (or irreps x irreps) array
// fuses into ONE compound level and the output is SymIdx<r, prod(n_j)>.
//
// Two things need pinning at the FIELD level, neither visible through
// matchesTypePattern (which ignores extent, and treats `__`-prefixed tags as
// don't-care — the irreps tag is exactly such a tag):
//   (1) the fused LOOP LEVEL: SourceRank = #factors, FusedFactors = the source
//       records in order, IndexSpace.Extent = the literal product, Tag = None;
//   (2) the fused OUTPUT record: Extent = the literal product, Rank = group
//       size, Symmetry = SymSymmetric, and — the consistency hazard — BOTH
//       Tag = None AND IxKind = IxKPlain. Inheriting IxKind from the template
//       factor would produce Tag = None + IxKIrreps, which the IR validator
//       rejects (ixKindOfTag None = IxKPlain) and which would falsely advertise
//       a spec the compound axis does not have (§6.3(iii): a batch x irreps
//       product is NOT an irreps space).

/// A rank-1 dense S-dim record with the given kind/tag and literal extent.
let private sRec (id: int) (extent: int64) (kind: IxKind) (tag: string option) : IRIndexType =
    { Id = id; Rank = 1; Extent = IRLit (IRLitInt extent)
      Symmetry = SymNone; Tag = tag; IxKind = kind; Kind = SDimension; Dependencies = [] }

/// An irreps S-dim record for `spec` (extent = total_dim, tag = mkIrrepsTag).
let private irrepsRec (id: int) (spec: (int * int * int) list) : IRIndexType =
    let totalDim = spec |> List.sumBy (fun (l, _, mult) -> int64 (mult * (2 * l + 1)))
    sRec id totalDim IxKIrreps (Some (mkIrrepsTag None spec))

/// Run `fuseJointSLevels` on TWO copies of one array (identity `A`, one comm
/// group covering both positions) whose S-block is `recs`.
let private fuseTwoCopies (recs: IRIndexType list) : LoopLevelInfo list =
    let arr = { ElemType = f64; IndexTypes = recs; IsVirtual = false
                Identity = Some (AIDVariable "A") }
    let arrayTypes = [arr; arr]
    let identities = [AIDVariable "A"; AIDVariable "A"]
    let raw = buildRawLoopLevels arrayTypes (computeSDimsPerArray arrayTypes)
    fuseJointSLevels identities [[0; 1]] arrayTypes raw

/// Shared checker for a fused level pair: two levels (one per copy), each
/// carrying the whole S-block as FusedFactors over the product extent.
let private checkFusedLevels (name: string) (recs: IRIndexType list) (wantExtent: int64) =
    let levels = fuseTwoCopies recs
    let wantExt = IRLit (IRLitInt wantExtent)
    let checks =
        [ (levels.Length = 2), sprintf "expected 2 fused levels (one per copy), got %d" levels.Length
          (levels |> List.forall (fun l -> l.FusedFactors = Some recs)),
            sprintf "FusedFactors = %A, want the source records verbatim" (levels |> List.map (fun l -> l.FusedFactors))
          (levels |> List.forall (fun l -> l.IndexSpace.Extent = wantExt)),
            sprintf "Extent = %A, want IRLit %d (the literal product)" (levels |> List.map (fun l -> l.IndexSpace.Extent)) wantExtent
          (levels |> List.forall (fun l -> l.IndexSpace.Tag = None)),
            sprintf "Tag = %A, want None (the fused axis is anonymous)" (levels |> List.map (fun l -> l.IndexSpace.Tag))
          (levels |> List.forall (fun l -> l.IndexSpace.SourceRank = recs.Length)),
            sprintf "SourceRank = %A, want %d" (levels |> List.map (fun l -> l.IndexSpace.SourceRank)) recs.Length
          (levels |> List.forall (fun l -> l.IndexSpace.Symmetry = SymNone)),
            sprintf "Symmetry = %A, want SymNone (the level is the ITERATION axis)" (levels |> List.map (fun l -> l.IndexSpace.Symmetry)) ]
    match checks |> List.tryFind (fst >> not) with
    | Some (_, why) -> (name, false, why)
    | None -> (name, true, sprintf "2 levels, SourceRank %d, extent %d" recs.Length wantExtent)

// Stage 4 (a): the fused LEVEL for a mixed plain x irreps S-block. Idx<2> x
// IrrepsIdx<[(1,1,1)]> (total_dim 3) -> one compound axis of extent 6.
let private test_fuse_level_plain_x_irreps () =
    checkFusedLevels
        "stage4 fuseJointSLevels fuses Idx<2> x IrrepsIdx<3> -> compound 6"
        [ sRec 1 2L IxKPlain None; irrepsRec 2 [(1, 1, 1)] ]
        6L

// Stage 4 (b): the fused LEVEL for irreps x irreps — two DIFFERENT irreps
// spaces (total_dim 2 and 3) -> one compound axis of extent 6. This is the case
// where the LEADING factor is the irreps record.
let private test_fuse_level_irreps_x_irreps () =
    checkFusedLevels
        "stage4 fuseJointSLevels fuses IrrepsIdx<2> x IrrepsIdx<3> -> compound 6"
        [ irrepsRec 1 [(0, 0, 2)]; irrepsRec 2 [(1, 1, 1)] ]
        6L

// Stage 4 negative: a SYMMETRIC factor must still NOT fuse. Only the IxKind
// predicate was relaxed; SymNone/Rank-1/no-deps are untouched, because a
// symmetric record stores only canonical cells (extent != cardinality) so the
// compound index is not a dense row-major product — its sound joint form is the
// wreath product, deferred (docs/plan-orbit-index-types.md). Two levels per copy, no
// fusion, means four levels total and no FusedFactors anywhere.
let private test_symidx_factor_still_does_not_fuse () =
    let name = "stage4 SymIdx factor still excluded from fusion (wreath deferral)"
    let symFactor = { sRec 2 3L IxKPlain None with Rank = 2; Symmetry = SymSymmetric }
    let levels = fuseTwoCopies [ sRec 1 2L IxKPlain None; symFactor ]
    // Rank-2 symmetric record spans 2 levels, so each copy has 3 raw levels.
    if levels |> List.exists (fun l -> l.FusedFactors.IsSome) then
        (name, false, "a SymIdx-bearing S-block was fused")
    elif levels.Length <> 6 then
        (name, false, sprintf "expected 6 unfused levels (3 per copy), got %d" levels.Length)
    else (name, true, "6 unfused levels, no FusedFactors")

/// Lower `src` and return the SOLE index record of the fused output binding,
/// asserting the stage 4 field stamping on it.
let private checkFusedOutput (name: string) (src: string) (wantExtent: int64) =
    match soleIndexOf src "result" with
    | Error e -> (name, false, e)
    | Ok ix ->
        let checks =
            [ (ix.Rank = 2), sprintf "Rank = %d, want 2" ix.Rank
              (ix.Symmetry = SymSymmetric), sprintf "Symmetry = %A, want SymSymmetric" ix.Symmetry
              (ix.Extent = IRLit (IRLitInt wantExtent)),
                sprintf "Extent = %A, want IRLit %d (the literal product)" ix.Extent wantExtent
              (ix.Tag = None), sprintf "Tag = %A, want None (the compound axis is anonymous)" ix.Tag
              (ix.IxKind = IxKPlain),
                sprintf "IxKind = %A, want IxKPlain — a Tag=None/IxKIrreps record violates Tag<->IxKind agreement" ix.IxKind
              (ix.Dependencies = []), sprintf "Dependencies = %A, want []" ix.Dependencies ]
        match checks |> List.tryFind (fst >> not) with
        | Some (_, why) -> (name, false, why)
        | None -> (name, true, formatBladeIndex ix)

// Stage 4 (c): the OUTPUT record for a mixed plain x irreps array under comm.
let private test_fused_output_plain_x_irreps () =
    checkFusedOutput
        "stage4 batch x IrrepsIdx output = SymIdx<2, 6>, Tag None, IxKPlain"
        ("let static spec = [(1, 1, 1)]\n" +
         "let A: Array<Float64 like Idx<2>, IrrepsIdx<spec>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
         "let L = method_for(A, A)\n" +
         "let f = lambda(x, y) where comm(x, y) -> x * y\n" +
         "let result = L <@> f |> compute\n")
        6L

// Stage 4 (d): the OUTPUT record for an irreps x irreps array — the case whose
// leading factor is irreps, i.e. the one that actually trips the hazard.
let private test_fused_output_irreps_x_irreps () =
    checkFusedOutput
        "stage4 IrrepsIdx x IrrepsIdx output = SymIdx<2, 6>, Tag None, IxKPlain"
        ("let static sA = [(0, 0, 2)]\n" +
         "let static sB = [(1, 1, 1)]\n" +
         "let A: Array<Float64 like IrrepsIdx<sA>, IrrepsIdx<sB>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]\n" +
         "let L = method_for(A, A)\n" +
         "let f = lambda(x, y) where comm(x, y) -> x * y\n" +
         "let result = L <@> f |> compute\n")
        6L

// ---- Outer-product identity and composed-apply typing ----------------------

// `[*]` synthesizes an anonymous array (mkOuterResult: Identity = None), but a
// LET-BINDING mints the identity from the pattern name, not the RHS type — so
// a let-bound outer product participates in comm fusion like any named array.
// This is the positive control pinning that behavior.
let private test_outer_let_identity_fuses () =
    checkFusedOutput
        "outer product through let: identity fuses to SymIdx<2, 4>"
        ("let A = [1.0, 2.0]\n" +
         "let B = [3.0, 4.0]\n" +
         "let Q = A [*] B\n" +
         "let L = method_for(Q, Q)\n" +
         "let f = lambda(x, y) where comm(x, y) -> x * y\n" +
         "let result = L <@> f |> compute\n")
        4L

// Negative control: the same outer product written INLINE twice has two fresh
// identities (no CSE — the documented limitation), so no fusion happens and
// the output stays four dense axes.
let private test_outer_inline_stays_dense () =
    assertBindingType
        "outer product inline twice: no identity, stays dense"
        ("let A = [1.0, 2.0]\n" +
         "let B = [3.0, 4.0]\n" +
         "let f = lambda(x, y) where comm(x, y) -> x * y\n" +
         "let result = method_for(A [*] B, A [*] B) <@> f |> compute\n")
        "result"
        (arrOf f64 [idx; idx; idx; idx])

// `(o1 >>@ o2) <@> A` used to parrot the FIRST input array's type (empty
// SymcomStates, no deduction). The chained per-stage typing must carry the
// LAST stage's element type out the end.
let private test_compose_apply_output_type () =
    assertBindingType
        "composed apply carries stage-2 element type (Bool)"
        ("let A = [1.0, 2.0, 3.0]\n" +
         "let o1 = object_for(lambda(v) -> v + 1.0)\n" +
         "let o2 = object_for(lambda(v) -> v > 2.0)\n" +
         "let result = (o1 >>@ o2) <@> A |> compute\n")
        "result"
        (arrOf (IRTScalar ETBool) [idx])

// ---- OrbIdx lowering: the RECORD, not a pattern ----------------------------
//
// These five deliberately bypass `assertBindingType`. matchesTypePattern is a
// permissive relation — it ignores extent, and it would ignore a level list
// living in the extent slot — while the entire claim v1 rests on is that a
// depth-1 OrbIdx lowers to the SAME RECORD the legacy spelling produces, field
// for field. That is an equality question, not a matching question, so these
// compare records directly.
//
// Id is excluded from every comparison: every occurrence of an index type gets
// a fresh Id (env.Builder.FreshId()), so two spellings of the same type can
// never share one, and comparing it would make every test here vacuously fail.

// (The array-binding accessor these use is `indexRecordsOf`, already defined
// above for the IxKind assertions.)

/// The index record a `type X = <index type>` alias registers. The route a
/// DEPTH >= 2 class has to take: it is refused the moment it names storage, so
/// there is no array binding to read it off.
let private aliasIndexRecord (src: string) (aliasName: string) : Result<IRIndexType, string> =
    match lower src with
    | Error e -> Error (sprintf "lower failed: %s" e)
    | Ok prog ->
        match prog.Modules
              |> List.collect (fun m -> m.Types)
              |> List.tryPick (function
                               | IRTDIndexType (n, ix) when n = aliasName -> Some ix
                               | _ -> None) with
        | Some ix -> Ok ix
        | None -> Error (sprintf "no index-type alias named '%s' in lowered program" aliasName)

let private withoutId (ix: IRIndexType) = { ix with Id = 0 }

/// Depth-1 '+': `OrbIdx<[(2,+)], 3>` must lower to the record `SymIdx<2, 3>`
/// lowers to — the same Rank, Symmetry, Extent, Tag, IxKind, Kind and
/// Dependencies. Not "a symmetric record of rank 2": THAT record.
let private test_orbidx_depth1_plus_is_symidx_record () =
    let name = "OrbIdx [(2,+)] lowers to the SymIdx<2,n> record"
    let src =
        "let s1: Array<Int64 like SymIdx<2, 3>> = fill_random(10)\n" +
        "let s2: Array<Int64 like OrbIdx<[(2,+)], 3>> = fill_random(10)\n"
    match indexRecordsOf src "s1", indexRecordsOf src "s2" with
    | Error e, _ | _, Error e -> (name, false, e)
    | Ok [a], Ok [b] ->
        if withoutId a = withoutId b then (name, true, formatBladeIndex b)
        else (name, false, sprintf "records differ:\n  SymIdx: %A\n  OrbIdx: %A" (withoutId a) (withoutId b))
    | Ok a, Ok b ->
        (name, false, sprintf "expected one index record each, got %d and %d" a.Length b.Length)

/// Depth-1 '-': the AntisymIdx twin of the above. Independently asserted
/// because a lowering that ignored the sign would pass the '+' test.
let private test_orbidx_depth1_minus_is_antisym_record () =
    let name = "OrbIdx [(2,-)] lowers to the AntisymIdx<2,n> record"
    let src =
        "let a1: Array<Int64 like AntisymIdx<2, 3>> = fill_random(10)\n" +
        "let a2: Array<Int64 like OrbIdx<[(2,-)], 3>> = fill_random(10)\n"
    match indexRecordsOf src "a1", indexRecordsOf src "a2" with
    | Error e, _ | _, Error e -> (name, false, e)
    | Ok [a], Ok [b] ->
        if withoutId a = withoutId b then (name, true, formatBladeIndex b)
        else (name, false, sprintf "records differ:\n  AntisymIdx: %A\n  OrbIdx: %A" (withoutId a) (withoutId b))
    | Ok a, Ok b ->
        (name, false, sprintf "expected one index record each, got %d and %d" a.Length b.Length)

/// §7.2's normalization: a rank-1 level is the trivial group at EITHER sign and
/// is dropped, so `[(2,+), (1,-)]` is `[(2,+)]` — the SymIdx<2,3> record — and
/// NOT a depth-2 wreath. This is the safeguard that makes the depth-1/depth-2
/// case split at lowering exhaustive; without it an AST could append trivial
/// levels forever at fixed rank.
let private test_orbidx_rank1_level_drops () =
    let name = "OrbIdx [(2,+),(1,-)] normalizes to the SymIdx<2,n> record"
    let src =
        "let s1: Array<Int64 like SymIdx<2, 3>> = fill_random(10)\n" +
        "let s2: Array<Int64 like OrbIdx<[(2,+), (1,-)], 3>> = fill_random(10)\n"
    match indexRecordsOf src "s1", indexRecordsOf src "s2" with
    | Error e, _ | _, Error e -> (name, false, e)
    | Ok [a], Ok [b] ->
        if withoutId a = withoutId b && b.Symmetry = SymSymmetric && b.Rank = 2 then
            (name, true, formatBladeIndex b)
        else (name, false, sprintf "records differ:\n  SymIdx: %A\n  OrbIdx: %A" (withoutId a) (withoutId b))
    | Ok a, Ok b ->
        (name, false, sprintf "expected one index record each, got %d and %d" a.Length b.Length)

/// The empty class: `OrbIdx<[], 3>` is the plain `Idx<3>` record (§3's normal
/// form), including Tag = None and IxKind = IxKPlain — a wreath marker left on
/// a trivial class would route it into the compact machinery for nothing.
let private test_orbidx_empty_is_plain_idx_record () =
    let name = "OrbIdx [] lowers to the plain Idx<n> record"
    let src =
        "let d1: Array<Int64 like Idx<3>> = [1, 2, 3]\n" +
        "let d2: Array<Int64 like OrbIdx<[], 3>> = [4, 5, 6]\n"
    match indexRecordsOf src "d1", indexRecordsOf src "d2" with
    | Error e, _ | _, Error e -> (name, false, e)
    | Ok [a], Ok [b] ->
        if withoutId a = withoutId b && b.Symmetry = SymNone && b.IxKind = IxKPlain && b.Tag = None then
            (name, true, formatBladeIndex b)
        else (name, false, sprintf "records differ:\n  Idx: %A\n  OrbIdx: %A" (withoutId a) (withoutId b))
    | Ok a, Ok b ->
        (name, false, sprintf "expected one index record each, got %d and %d" a.Length b.Length)

/// Depth 2 — the only case that is new machinery. Asserts the whole record
/// shape (Rank = the PRODUCT of the level ranks, Symmetry = SymWreath, the
/// level list on the Extent marker in outermost-last order, IxKOrbit + its
/// "__orbidx" sentinel so the IR validator's Tag<->IxKind agreement holds) AND
/// the §4 cardinality: the Riemann shape at n = 4 folds 4 -> C(4,2) = 6 ->
/// C(7,2) = 21, the 21 formalism §3.4 states. That last number is the one a
/// wrong `PlaceCombinatorial _` fallthrough would silently get wrong (it would
/// compute C(4+16-1, 16) over the raw axis count instead).
let private test_orbidx_depth2_record_and_cardinality () =
    let name = "OrbIdx [(2,-),(2,+)] depth-2 record: rank 4, SymWreath, 21 cells at n=4"
    let src = "type Riemann = OrbIdx<[(2,-), (2,+)], 4>\nlet x = 1\n"
    match aliasIndexRecord src "Riemann" with
    | Error e -> (name, false, e)
    | Ok ix ->
        let levels = orbitLevelsOf ix
        let card =
            try
                match bufferGroupCardinality { Rank = ix.Rank; Extent = ix.Extent
                                               Symmetry = ix.Symmetry; Kind = ix.Kind
                                               Dependencies = ix.Dependencies } with
                | IRLit (IRLitInt n) -> Ok n
                | other -> Error (sprintf "cardinality did not fold to a literal: %A" other)
            with e -> Error e.Message
        let problems =
            [ if ix.Rank <> 4 then yield sprintf "Rank = %d, expected 4 (= 2 * 2)" ix.Rank
              if ix.Symmetry <> SymWreath then yield sprintf "Symmetry = %A, expected SymWreath" ix.Symmetry
              if ix.IxKind <> IxKOrbit then yield sprintf "IxKind = %A, expected IxKOrbit" ix.IxKind
              if ix.Tag <> Some "__orbidx" then yield sprintf "Tag = %A, expected Some \"__orbidx\"" ix.Tag
              if ixKindOfTag ix.Tag <> ix.IxKind then yield "Tag/IxKind disagree (the IR validator would reject)"
              if levels <> [ (2, false); (2, true) ] then
                  yield sprintf "levels = %s, expected [(2,-), (2,+)] outermost-last" (ppOrbitLevels levels)
              match ix.Extent with
              | IROrbitClass (_, IRLit (IRLitInt 4L)) -> ()
              | other -> yield sprintf "base extent = %A, expected IROrbitClass (_, 4)" other
              match card with
              | Ok 21L -> ()
              | Ok n -> yield sprintf "cell count = %d, expected 21 (4 -> C(4,2)=6 -> C(7,2)=21)" n
              | Error e -> yield sprintf "cell count failed: %s" e ]
        if List.isEmpty problems then (name, true, sprintf "%s, 21 cells" (formatBladeIndex ix))
        else (name, false, String.concat "; " problems)

/// The level list is TYPE IDENTITY, not decoration: `[(2,+),(2,+)]` and
/// `[(2,-),(2,-)]` are both Rank 4, both SymWreath, and both carry the same
/// synthetic "__orbidx" Tag (which indexPairIncompatible's tag arm exempts by
/// design), so every pre-existing test in that function calls them compatible —
/// while their cell counts at n = 4 are 55 and 15. This pins both halves.
let private test_orbidx_level_lists_are_identity () =
    let name = "OrbIdx level lists are type identity (same rank, different class)"
    let src =
        "type Tied = OrbIdx<[(2,+), (2,+)], 4>\n" +
        "type Anti = OrbIdx<[(2,-), (2,-)], 4>\n" +
        "let x = 1\n"
    match aliasIndexRecord src "Tied", aliasIndexRecord src "Anti" with
    | Error e, _ | _, Error e -> (name, false, e)
    | Ok t, Ok a ->
        let cardOf (ix: IRIndexType) =
            match bufferGroupCardinality { Rank = ix.Rank; Extent = ix.Extent
                                           Symmetry = ix.Symmetry; Kind = ix.Kind
                                           Dependencies = ix.Dependencies } with
            | IRLit (IRLitInt n) -> Some n
            | _ -> None
        let problems =
            [ if t.Rank <> a.Rank then yield "the two classes should share Rank 4 (that is the point)"
              if not (Blade.Unify.indexPairIncompatible t a) then
                  yield "unification treats [(2,+),(2,+)] and [(2,-),(2,-)] as COMPATIBLE"
              if Blade.Unify.indexPairIncompatible t t then
                  yield "unification treats a class as incompatible with itself"
              match cardOf t, cardOf a with
              | Some 55L, Some 15L -> ()
              | ct, ca -> yield sprintf "cell counts %A / %A, expected 55 / 15 at n = 4" ct ca ]
        if List.isEmpty problems then (name, true, "55 vs 15 cells, unification separates them")
        else (name, false, String.concat "; " problems)

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
          test_sym_irreps_annotation_matches_inference
          test_fuse_level_plain_x_irreps
          test_fuse_level_irreps_x_irreps
          test_symidx_factor_still_does_not_fuse
          test_fused_output_plain_x_irreps
          test_fused_output_irreps_x_irreps
          test_outer_let_identity_fuses
          test_outer_inline_stays_dense
          test_compose_apply_output_type
          test_orbidx_depth1_plus_is_symidx_record
          test_orbidx_depth1_minus_is_antisym_record
          test_orbidx_rank1_level_drops
          test_orbidx_empty_is_plain_idx_record
          test_orbidx_depth2_record_and_cardinality
          test_orbidx_level_lists_are_identity ]
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
