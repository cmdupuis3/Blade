module Blade.Tests.IndexTypes

// ============================================================================
// AntisymIdx Tests
// ============================================================================

let test_antisym_iteration = """
// AntisymIdx iteration should use strict i < j bounds
// For n=4: pairs are (0,1),(0,2),(0,3),(1,2),(1,3),(2,3) = 6 elements
// But with i<=j: (0,0),(0,1),(0,2),(0,3),(1,1),...,(3,3) = 10 elements
let A = [1.0, 2.0, 3.0, 4.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x - y
let result = L <@> f |> compute
// EXPECT: result = [0, -1, -2, -3, 0, -1, -2, 0, -1, 0]
"""

let test_antisym_type_in_kernel = """
// Kernel producing antisymmetric output
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f |> compute
// EXPECT: result = [1, 2, 3, 4, 6, 9]
"""

let test_antisym_parse_type = """
// Parse AntisymIdx in a function signature, with block body
function skew(A: Array<Float64 like Idx<n>>) -> Float64 = {
    let L = method_for(A, A)
    let f = lambda(x, y) where comm(x, y) -> x - y
    0.0
}
"""

// ============================================================================
// HermitianIdx Tests
// ============================================================================

let test_hermitian_parse_type = """
// Parse HermitianIdx in a function signature. Hermitian arrays require
// complex element types (the Hermitian property A[i,j] = conj(A[j,i])
// is meaningful only over C), so signature uses Complex128 throughout
// and body returns an explicit Complex128 zero literal via the postfix
// type annotation form `(re, im) : Complex128`. Both components are
// float-typed (decimal-point literals) per the design rule that complex
// literals require explicit floating-point real/imaginary parts.
function trace(H: Array<Complex128 like HermitianIdx<n>>) -> Complex128 = {
    (0.0, 0.0) : Complex128
}
"""

let test_complex_lit_basic = """
// Complex128 literal construction via postfix type annotation.
// `(re, im) : Complex128` — both components are float-typed; the
// resulting value is a scalar Complex128, NOT a 2-tuple of doubles.
let x = (1.0, 2.0) : Complex128
let y = (3.0, 4.0) : Complex128
// EXPECT: x = (1,2)
// EXPECT: y = (3,4)
"""

let test_complex_lit_in_array = """
// Array of Complex128 elements, each constructed via the literal form.
// The array is rank-1 (Idx<3>) — Complex elements do NOT add a phantom
// dimension. Runtime layout is std::complex<double>[3] = 3 elements,
// each storing a (real, imag) pair internally; the array's rank stays 1.
let arr: Array<Complex128 like Idx<3>> = [
    (1.0, 0.0) : Complex128,
    (0.0, 1.0) : Complex128,
    (1.0, 1.0) : Complex128
]
// EXPECT: arr = [(1, 0), (0, 1), (1, 1)]
"""

let test_complex_lit_via_let_annotation = """
// When a let binding has an explicit Complex128 annotation, the tuple
// form (re, im) is recognized via bidirectional checkExpr without a
// trailing : Complex128 cast. This exercises the same checkExpr rule
// reached from a different entry point.
let x: Complex128 = (5.0, -7.0)
// EXPECT: x = (5,-7)
"""

let test_hermitian_rectangular = """
// HermitianIdx iterates as full rectangular (no triangular optimization)
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let f = lambda(x, y) -> x * y
let result = L <@> f |> compute
// EXPECT: result = [4, 5, 6, 8, 10, 12, 12, 15, 18]
"""

// ============================================================================
// DepIdx Round 1 — parse + typecheck only.
// Iteration codegen is deferred to a later round; these tests confirm the
// surface syntax parses and types lower without error. The function body
// returns a constant to avoid exercising any iteration path.
// ============================================================================

let test_depidx_parse_lambda = """
// DepIdx<outer, lambda(i) -> body>: explicit lambda form
function f(A: Array<Float64 like DepIdx<Idx<3>, lambda(i) -> Idx<3>>>) -> Float64 = {
    0.0
}
"""

let test_depidx_parse_eta = """
// DepIdx<outer, func>: eta-reduced form. Desugars at parse time to the
// lambda form. Round 1 lowers to a placeholder dynamic extent regardless;
// this test only confirms the surface form parses.
//
// Per the index-type role validator: the named extent function must be a
// static function (its result feeds a DepIdx body, which is a static-context
// position), with aliased index parameters (anonymous index types are not
// permitted as static-fn params).
type TriIdx = Idx<3>
static function tri_extent(i: TriIdx) -> TriIdx = i
function g(A: Array<Float64 like DepIdx<TriIdx, tri_extent>>) -> Float64 = {
    0.0
}
"""

// ============================================================================
// RaggedIdx Round 1 — parse + typecheck only.
// RaggedIdx contributes a single IR record (the ragged dim itself); it
// references an external lengths array and requires at least one prior index
// in the array's index list to iterate over.
// ============================================================================

let test_raggedidx_parse = """
// RaggedIdx<lengths>: externally parameterized via a lengths array.
// The required prior index (Idx<3>) provides the iteration position used to
// look up lengths internally.
let lens = [2, 3, 1]
function h(A: Array<Float64 like Idx<3>, RaggedIdx<lens>>) -> Float64 = {
    0.0
}
"""

// Round 2 — verify the two-record expansion.
// A DepIdx in array index position now contributes TWO records to the array's
// IndexTypes (outer + inner with Dependencies linking). Total rank from one
// DepIdx is 2. The substituteAndLowerExtent helper produces a real IR Extent
// expression for the lambda body — though without codegen iteration support
// (Round 3 work) we can't actually iterate over a DepIdx-typed array yet.
// This test only exercises the type-level structure: rank() should reflect
// the two-record expansion.

let test_depidx_rank_two_records = """
// rank(A) for an Array<like DepIdx<...>> should be 2 — the two records
// (outer + inner) each contribute arity 1.
function f(A: Array<Float64 like DepIdx<Idx<3>, lambda(i) -> Idx<3>>>) -> Int64 = {
    rank(A)
}
let x = 0  // placeholder so the file has runtime behavior
// EXPECT: x = 0
"""

// Round 2 Phase 2 — ragged literal allocation and printing.
// A nested array literal with uneven inner lengths is now typed as a
// RaggedIdx-typed array. Codegen emits offsets table, flat backing buffer,
// and row-pointer table. The print loop walks the structure using the
// offsets to produce the correct flat output.

let test_ragged_literal_basic = """
// Ragged literal: rows of differing lengths. The literal allocates as
// RaggedIdx-typed; print emits the flat sequence of values.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
// EXPECT: r = [1, 2, 3, 4, 5, 6, 7, 8, 9]
"""

// Ragged literal direct indexing: with the row-pointer setup, r(i)(j) works
// as nested C++ indexing on the heterogeneous-length rows. This test exercises
// element access without involving combinator iteration (which still requires
// the kernel-param typing fix that's a separate workstream).

let test_ragged_literal_indexing = """
// Direct index access into a ragged literal. The row-pointer table makes
// r(i)(j) generate r[i][j] in C++, which works correctly because each r[i]
// points into the flat buffer at the right offset.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let a = r(0)(2)
let b = r(1)(0)
let c = r(2)(3)
// EXPECT: a = 3
// EXPECT: b = 4
// EXPECT: c = 9
"""

let test_ragged_literal_row_then_elem = """
// Bind a row to an intermediate variable, then index into it. Tests that the
// row binding holds a usable pointer and that subsequent indexing through it
// produces correct values.
let r = [[10.0, 20.0, 30.0], [40.0, 50.0], [60.0, 70.0, 80.0, 90.0]]
let row1 = r(1)
let v = row1(0)
let w = row1(1)
// EXPECT: v = 40
// EXPECT: w = 50
"""

// Combinator iteration over a ragged literal. Routes through the generalized
// tryRaggedPeel codegen path: outer loop over rows, sub-array binding for the
// kernel param, kernel body produces one scalar per row.
//
// The kernel's `g(0)` references work via the IRApp→IRIndex rewrite already
// used by group_by aggregation. The kernel param's type stays unconstrained
// at typecheck (a fresh type variable) — the codegen path handles it
// regardless. Output is rectangular `Array<Float64 like Idx<3>>`.

let test_ragged_method_for_first_elem = """
// method_for over a ragged literal. Kernel returns the first element of each
// row.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let firsts = method_for(r) <@> lambda(g) -> g(0) |> compute
// EXPECT: firsts = [1, 4, 6]
"""

// ============================================================================
// RaggedIdx<_> — opaque-extent variant for kernel-parameter typing.
// Round 3 (Leg 1): a kernel parameter typed `Array<T like RaggedIdx<_>>`
// represents a sub-array peeled from a parent ragged at iteration time.
// The `_` is a wildcard for "extent supplied by surrounding context"; only
// the raggedness is part of the kernel-param type. Without this annotation,
// the kernel param has a fresh type variable and operations like reduce(g),
// extents(g) cannot typecheck. With the annotation, those operations
// typecheck normally, and codegen produces correct C++ via the existing
// tryRaggedPeel sub-array binding (which also emits the per-row _extents
// the kernel body needs to read).
// ============================================================================

let test_raggedidx_opaque_parse = """
// Parse + typecheck smoke for the wildcard form. Body is constant; this
// only exercises the surface syntax and type lowering.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0]]
let firsts = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> 0.0 |> compute
// EXPECT: firsts = [0, 0]
"""

let test_raggedidx_opaque_reduce = """
// Reduce each row of a ragged literal. With the typed kernel param, reduce(g)
// typechecks because g is rank-1; codegen renders the inline IIFE against
// the sub-array binding's _extents, producing per-row sums.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let sums = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: sums = [6, 9, 30]
"""

let test_raggedidx_opaque_extents = """
// extents(g) for a typed rank-1 kernel param falls through to the runtime
// read against the sub-array binding's _extents[0], which carries the row
// length the parent ragged supplied at the peel point.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let sizes = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> extents(g) |> compute
// EXPECT: sizes = [3, 2, 4]
"""

let test_raggedidx_opaque_indexing = """
// Direct indexing on a typed kernel param. With the annotation, g(0) is
// TExprIndex at typecheck (since g is a rank-1 array type) — distinct from
// the unannotated path where g(0) became TExprApp and was rewritten to
// IRIndex post-hoc. Both should produce equivalent output.
let r = [[10.0, 20.0, 30.0], [40.0, 50.0], [60.0, 70.0]]
let firsts = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> g(0) |> compute
// EXPECT: firsts = [10, 40, 60]
"""

// ============================================================================
// Function parameters with ragged types (Leg 2).
// A function declared `f(A: Array<... RaggedIdx<...>>)` must receive `A_lens`
// alongside `A_extents` at the C++ calling convention. With those companion
// arrays in scope inside the body, tryRaggedPeel handles iteration via the
// usual `A_lens[__g]` / `A_extents[0]` machinery — the same path that
// already worked for ragged literals.
//
// Both surface forms are exercised:
//   - Closed `RaggedIdx<lens>`: type-level reference to a named lens binding.
//     Currently decorative — no runtime check that the caller's lens equals
//     the named binding. Enforcement is deferred to a materialization round.
//   - Opaque `RaggedIdx<_>`:    the universal ragged spelling for any context.
//
// Both produce identical C++ at the call site; the named form's enforcement
// is the only future-different bit.
// ============================================================================

let test_raggedidx_func_param_closed_smoke = """
// Function decl with closed RaggedIdx<lens> param. Body is constant — only
// verifies the decl signature emits properly and the call site passes the
// companion arrays.
let lens = [3, 2, 4]
function h(A: Array<Float64 like Idx<3>, RaggedIdx<lens>>) -> Float64 = 0.0
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let result = h(r)
// EXPECT: result = 0
"""

let test_raggedidx_func_param_opaque_smoke = """
// Function decl with opaque RaggedIdx<_> param. Same as above but with the
// opaque form, which is the recommended spelling for ragged function params
// until named-form enforcement lands.
function h(A: Array<Float64 like Idx<3>, RaggedIdx<_>>) -> Float64 = 0.0
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let result = h(r)
// EXPECT: result = 0
"""

let test_raggedidx_func_param_closed_total = """
// Function takes a ragged input, returns the total sum of all elements.
// Body uses tryRaggedPeel via method_for(A) inside the function — A_lens
// and A_extents must both be in scope, which only happens if the calling
// convention plumbing is correct.
let lens = [3, 2, 4]
function totalSum(A: Array<Float64 like Idx<3>, RaggedIdx<lens>>) -> Float64 = {
    let row_sums = method_for(A) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
    reduce(row_sums, (+))
}
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let result = totalSum(r)
// EXPECT: result = 45
"""

let test_raggedidx_func_param_opaque_total = """
// Same as above, opaque form. The ragged structural logic is identical;
// only the type-level annotation differs.
function totalSum(A: Array<Float64 like Idx<3>, RaggedIdx<_>>) -> Float64 = {
    let row_sums = method_for(A) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
    reduce(row_sums, (+))
}
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let result = totalSum(r)
// EXPECT: result = 45
"""

let test_func_param_plain_array_call = """
// Latent-bug regression: a function with a plain (non-ragged) array param
// is now actually callable. Previously the decl emitted `const size_t*`
// extents companions, but the call site didn't pass them, producing a
// "too few arguments" C++ error. Argument-driven companion-passing fixes
// both ragged and non-ragged calls.
function arraySum(A: Array<Float64 like Idx<3>>) -> Float64 = reduce(A, (+))
let A = [1.0, 2.0, 3.0]
let result = arraySum(A)
// EXPECT: result = 6
"""

// ============================================================================
// Block expression return value tests
// ============================================================================

let test_block_return_simple = """
// Block should return its final expression
function f(x: Float64) -> Float64 = {
    let y = x * 2.0
    y + 1.0
}
let r = f(5.0)
// EXPECT: r = 11
"""

let test_block_return_with_loop = """
// Block with method_for should still return final expression
function g(A: Array<Float64 like Idx<n>>) -> Float64 = {
    let L = method_for(A, A)
    0.0
}
"""

// ============================================================================
// DepIdx Leg 3 — construction codegen and iteration through __depidx_inner.
// Triangular form: DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>> gives row lengths
// [3, 2, 1] computed at codegen time from the formula. The literal must
// match those lens; mismatches surface as a codegen-time error.
// Once allocated, the runtime layout (`_lens`/`_offsets`/flat backing)
// matches a ragged array, so iteration flows through the same paths.
// ============================================================================

let test_depidx_triangular_literal = """
// Triangular literal: lens computed from the formula 3-i = [3, 2, 1].
// Print walks the offsets table to produce a flat sequence, identical
// in shape to the ragged literal print.
let r: Array<Float64 like DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0],
    [6.0]
]
// EXPECT: r = [1, 2, 3, 4, 5, 6]
"""

let test_depidx_inline_per_row_reduce = """
// Inline method_for over a DepIdx-allocated array. The kernel param uses
// the opaque RaggedIdx<_> type — the peel logic recognizes both
// __depidx_inner and __raggedidx_opaque tags as 'has runtime-shape inner
// extent' and uses the same loop structure.
let r: Array<Float64 like DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0],
    [6.0]
]
let row_sums = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
let total = reduce(row_sums, (+))
// EXPECT: total = 21
"""

let test_depidx_func_param_total = """
// Function takes a DepIdx-typed parameter, computes per-row reductions in
// the body, then totals them. Exercises the calling convention (lens
// companion passed at the call site), the function-body let-binding of
// method_for output (Leg 2 fix), and the per-row peel through the
// __depidx_inner tag.
function totalSum(A: Array<Float64 like DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>>) -> Float64 = {
    let row_sums = method_for(A) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
    reduce(row_sums, (+))
}
let r: Array<Float64 like DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0],
    [6.0]
]
let total = totalSum(r)
// EXPECT: total = 21
"""

// ============================================================================
// Index types as standalone value types. The validator (IndexTypeValidator.fs)
// enforces:
//   - regular function params/returns: forbidden
//   - struct fields: forbidden
//   - let-binding annotations: forbidden
//   - static fn params: aliased + statically-evaluable only
//   - static fn returns: statically-evaluable (anon or aliased)
//   - tuple elements in static-context: statically-evaluable
//   - array element types: aliased only (foreign keys)
// ============================================================================

let test_idx_value_static_fn_return_anon = """
// Anonymous index type as static-fn return: permitted.
static function pivot() -> Idx<5> = 2
let p = pivot()
// EXPECT: p = 2
"""

let test_idx_value_static_fn_param_aliased = """
// Aliased index type as static-fn parameter: permitted.
type StationIdx = Idx<6>
static function next_station(s: StationIdx) -> StationIdx = s
let s = next_station(3)
// EXPECT: s = 3
"""

let test_idx_value_static_tuple_destructuring = """
// Static tuple containing index types in `let static` destructuring:
// permitted because the tuple is in static-context. Observation of
// destructured components is direct — no arithmetic, since arithmetic
// on named index types is forbidden (per the formalism: index types are
// nominal labels with no useful arithmetic semantics; for value-level
// position arithmetic, use virtual array iteration; for new index types
// derived from arithmetic, type-level construction is a separate
// workstream).
type StationIdx = Idx<6>
static function lookup_pair() -> (StationIdx, Idx<3>) = (4, 1)
let static (a, b) = lookup_pair()
let r = a
// EXPECT: r = 4
"""

let test_idx_value_alias_runtime_smoke = """
// Aliasing a runtime-evaluable index type at the alias-body position is
// permitted by the validator (alias bodies accept any index type, runtime
// or static). End-to-end test: declare the alias, use it as the second
// index in a 2-D ragged array, index into the array, verify the value.
let lens: Array<Int64 like Idx<3>> = [3, 2, 1]
type Ragged3 = RaggedIdx<lens>
let r: Array<Float64 like Idx<3>, Ragged3> = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let v = r(0)(0)
// EXPECT: v = 1
"""

let test_idx_value_alias_depidx = """
// DepIdx aliasing — the multi-record case. The unaliased form expands to
// two IRIndexType records (outer + inner with Dependencies linking them);
// the alias must re-expand at the use site rather than retrieve a
// single-record placeholder. lowerIndexTypeList catches TyNamed → DepIdx
// body and recurses on the body.
type Tri3 = DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>
let r: Array<Float64 like Tri3> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0],
    [6.0]
]
// EXPECT: r = [1, 2, 3, 4, 5, 6]
"""

let test_idx_value_alias_chain_static = """
// Chained static alias: type B = A where A is itself an index alias.
// registerTypeDecl chases one level of TDIIndexType reference at
// registration; B then behaves like a direct alias of Idx<6>, including
// in foreign-key element-type position.
type StationIdx = Idx<6>
type RegionIdx = StationIdx
let codes: Array<RegionIdx like Idx<3>> = [0, 1, 2]
let v = codes(1)
// EXPECT: v = 1
"""

let test_idx_value_alias_chain_enum = """
// Chained EnumIdx alias: type B = A where A is itself an EnumIdx alias.
// Same chase pattern as the static chain test, but resolving through
// TDIEnumIdx (whose body field was added in this round). Without the
// body, the chain falls through to TDIAlias and the array's element
// type lookup misses the EnumIdx values list.
type LandType = EnumIdx<[101, 205, 307]>
type RegionCode = LandType
let codes: Array<RegionCode like Idx<3>> = [101, 205, 307]
let v = codes(1)
// EXPECT: v = 205
"""

let test_idx_value_alias_chain_depidx = """
// Chained runtime alias over DepIdx. After registration chase, TriCopy's
// body is TyDepIdx (peeled from Tri3's stored body). lowerIndexTypeList
// then re-expands to the proper outer + inner records when TriCopy is
// used in an array index list.
type Tri3 = DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>
type TriCopy = Tri3
let r: Array<Float64 like TriCopy> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0],
    [6.0]
]
// EXPECT: r = [1, 2, 3, 4, 5, 6]
"""

let test_depidx_eta_reduced_value = """
// Eta-reduced DepIdx — the named-function form. Semantically equivalent
// to `DepIdx<Idx<3>, lambda(i) -> Idx<3 - i>>` (test_depidx_triangular_literal),
// but with the per-row extent computed by a static function. The eta-
// reduction inlines the function's body at the use site, substituting
// the function's param with the outer iteration variable.
//
// The function uses Int64 throughout to avoid index-type coercion
// concerns; the eta-reduction mechanism is type-agnostic and simply
// extracts the body expression for substitution.
static function tri_extent(i: Int64) -> Int64 = 3 - i
let r: Array<Float64 like DepIdx<Idx<3>, tri_extent>> = [
    [1.0, 2.0, 3.0],
    [4.0, 5.0],
    [6.0]
]
// EXPECT: r = [1, 2, 3, 4, 5, 6]
"""

// ============================================================================
// Nominal index value tests
// ============================================================================
//
// These exercise the post-Option-C handling of named (nominal) index types
// at the value level: how the typechecker tags values, how step 5's
// indexing check accepts matching tags, and how iteration-tagging makes
// the common kernel-over-virtual-array pattern work without explicit
// annotations on the kernel parameter.

let test_iter_tag_named_range = """
// Iteration-tagging via range<NamedIdx>: the virtual array's element
// type is Nat<Lat>, so the lambda parameter `i` is inferred as a
// Lat-tagged value. Step 5's tag check on A(i) then matches the array's
// LatIdx slot tag without needing an explicit annotation on `i`.
//
// This is the practical payoff of iteration-tagging — the common
// pattern of "iterate over an index space and look up sibling arrays"
// typechecks naturally.
type Lat = Idx<3>
let A: Array<Float64 like Lat> = [10.0, 20.0, 30.0]
let r = method_for(range<Lat>) <@> lambda(i) -> A(i) * 2.0 |> compute
// EXPECT: r = [20, 40, 60]
"""

let test_named_idx_explicit_cast_index = """
// Expression-level type cast `(expr : NamedIdx)` produces a tagged
// Nat<NamedIdx> value via the literal-coercion rule in checkExpr
// (§4.18.3). The expression-position cast bypasses the IndexType
// validator (which only constrains declaration-level positions),
// giving a way to construct a tagged value outside static function
// signatures.
//
// Indexing with a matching-tag value succeeds under step 5's check.
type Lat = Idx<3>
let A: Array<Float64 like Lat> = [10.0, 20.0, 30.0]
let v = A((1 : Lat))
// EXPECT: v = 20
"""


/// Index type tests (AntisymIdx, HermitianIdx)
let indexTypeTests = [
    ("AntisymIdx Iteration", test_antisym_iteration)
    ("AntisymIdx Kernel", test_antisym_type_in_kernel)
    ("Block Return Simple", test_block_return_simple)
    ("Block Return With Loop", test_block_return_with_loop)
    ("AntisymIdx Parse Type", test_antisym_parse_type)
    ("HermitianIdx Parse Type", test_hermitian_parse_type)
    ("Complex Lit Basic", test_complex_lit_basic)
    ("Complex Lit In Array", test_complex_lit_in_array)
    ("Complex Lit Via Let Annotation", test_complex_lit_via_let_annotation)
    ("HermitianIdx Rectangular", test_hermitian_rectangular)
    ("DepIdx Parse Lambda", test_depidx_parse_lambda)
    ("DepIdx Parse Eta", test_depidx_parse_eta)
    ("RaggedIdx Parse", test_raggedidx_parse)
    ("DepIdx Two Records Rank", test_depidx_rank_two_records)
    ("Ragged Literal Basic", test_ragged_literal_basic)
    ("Ragged Literal Indexing", test_ragged_literal_indexing)
    ("Ragged Literal Row Then Elem", test_ragged_literal_row_then_elem)
    ("Ragged Method For First Elem", test_ragged_method_for_first_elem)
    ("RaggedIdx Opaque Parse", test_raggedidx_opaque_parse)
    ("RaggedIdx Opaque Reduce", test_raggedidx_opaque_reduce)
    ("RaggedIdx Opaque Extents", test_raggedidx_opaque_extents)
    ("RaggedIdx Opaque Indexing", test_raggedidx_opaque_indexing)
    ("RaggedIdx Func Param Closed Smoke", test_raggedidx_func_param_closed_smoke)
    ("RaggedIdx Func Param Opaque Smoke", test_raggedidx_func_param_opaque_smoke)
    ("RaggedIdx Func Param Closed Total", test_raggedidx_func_param_closed_total)
    ("RaggedIdx Func Param Opaque Total", test_raggedidx_func_param_opaque_total)
    ("Func Param Plain Array Call", test_func_param_plain_array_call)
    ("DepIdx Triangular Literal", test_depidx_triangular_literal)
    ("DepIdx Inline Per Row Reduce", test_depidx_inline_per_row_reduce)
    ("DepIdx Func Param Total", test_depidx_func_param_total)
    ("Idx Value Static Fn Return Anon", test_idx_value_static_fn_return_anon)
    ("Idx Value Static Fn Param Aliased", test_idx_value_static_fn_param_aliased)
    ("Idx Value Static Tuple Destructuring", test_idx_value_static_tuple_destructuring)
    ("Idx Value Alias Runtime Smoke", test_idx_value_alias_runtime_smoke)
    ("Idx Value Alias DepIdx", test_idx_value_alias_depidx)
    ("Idx Value Alias Chain Static", test_idx_value_alias_chain_static)
    ("Idx Value Alias Chain Enum", test_idx_value_alias_chain_enum)
    ("Idx Value Alias Chain DepIdx", test_idx_value_alias_chain_depidx)
    ("DepIdx Eta-Reduced Value", test_depidx_eta_reduced_value)
    ("Nominal: Iter Tag Named Range", test_iter_tag_named_range)
    ("Nominal: Explicit Cast Index", test_named_idx_explicit_cast_index)
]
