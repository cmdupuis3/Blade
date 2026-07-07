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

let test_antisym_decl_param = """
// Declare an antisymmetric array as a function parameter:
// Array<T like AntisymIdx<2, n>>. Confirms the `like AntisymIdx<arity, extent>`
// declaration form parses and type-checks in a binding position. (Usage that
// POPULATES an antisymmetric array goes through the Reynolds/method_for path —
// the Reynolds-antisymmetric tests above — which is currently the only
// producer of antisymmetric storage.)
function trace_skew(M: Array<Float64 like AntisymIdx<2, n>>) -> Float64 = {
    0.0
}
"""

// ----------------------------------------------------------------------------
// Reynolds antisymmetric STORAGE tests.
//
// These exercise the path where same-array method_for + comm (triangular
// iteration) + reynolds(_, Antisymmetric) deduces an AntisymIdx OUTPUT index
// type. The discriminator is CARDINALITY: an antisymmetric rank-2 output over
// n stores C(n,2) strict-upper-triangle elements (no diagonal), versus
// C(n+1,2) for symmetric (with diagonal) or n*n for rectangular. The strict
// left-justified DFS storage order is (0,1),(0,2),...,(0,n-1),(1,2),...,(n-2,n-1).
//
// Antisymmetrized value at (i,j) for g(x,y)=2x+y is g(A[i],A[j])-g(A[j],A[i])
//   = (2A[i]+A[j]) - (2A[j]+A[i]) = A[i]-A[j].
//
// NOTE on comm semantics (formalism 2705/3514): `comm` declares the arguments
// INTERCHANGEABLE FOR ITERATION (enables triangular bounds); it is NOT an
// assertion that g(x,y)=g(y,x). So a comm-declared kernel may be Reynolds-
// ANTIsymmetrized to a nonzero result. (A comm kernel that genuinely satisfied
// g(x,y)=g(y,x) would antisymmetrize to zero — see the cancellation case.)

let test_antisym_reynolds_n4 = """
// Same array, comm + reynolds Antisymmetric -> AntisymIdx<2,4> output.
// Strict pairs (i<j) for n=4: (0,1)(0,2)(0,3)(1,2)(1,3)(2,3) = C(4,2)=6 elements.
// value(i,j) = A[i]-A[j] with A=[1,2,3,4]:
//   (0,1)=-1 (0,2)=-2 (0,3)=-3 (1,2)=-1 (1,3)=-2 (2,3)=-1
// Cardinality 6 (not 10 symmetric, not 16 rectangular) confirms antisym storage.
let A = [1.0, 2.0, 3.0, 4.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let result = L <@> reynolds(g, Antisymmetric) |> compute
// EXPECT: result = [-1.0, -2.0, -3.0, -1.0, -2.0, -1.0]
"""

let test_antisym_reynolds_n3 = """
// Smaller case n=3: strict pairs (0,1)(0,2)(1,2) = C(3,2)=3 elements.
// value(i,j)=A[i]-A[j], A=[1,2,3]: (0,1)=-1 (0,2)=-2 (1,2)=-1
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let result = L <@> reynolds(g, Antisymmetric) |> compute
// EXPECT: result = [-1.0, -2.0, -1.0]
"""

let test_antisym_reynolds_cancellation = """
// A genuinely symmetric kernel (x*y) antisymmetrized cancels to zero on every
// strict pair: x*y - y*x = 0. Confirms the strict layout still has C(3,2)=3
// slots (all zero), distinguishing "antisym storage of zeros" from a bug that
// drops the output entirely.
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> reynolds(g, Antisymmetric) |> compute
// EXPECT: result = [0.0, 0.0, 0.0]
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

// ----------------------------------------------------------------------------
// conj — built-in unary complex-conjugate op.
//   conj((a, b) : Complex128) = (a, -b);  conj(real) = real (identity).
// Lowers to IRUnaryOp(IRConj, _); codegen emits std::conj for complex
// operands and the bare operand for reals.

let test_conj_scalar = """
// Conjugate a complex scalar: negates the imaginary part.
let z = (3.0, 4.0) : Complex128
let zc = conj(z)
// EXPECT: zc = (3,-4)
"""

let test_conj_real_identity = """
// conj on a real value is the identity (conj(x)=x for real x). Confirms the
// permissive typing: conj : T -> T, with reals passing through unchanged.
let r = 5.0
let rc = conj(r)
// EXPECT: rc = 5
"""

let test_conj_array = """
// conj applied elementwise inside a kernel over a complex array. Each element
// (a, b) becomes (a, -b).
let A: Array<Complex128 like Idx<3>> = [
    (1.0, 2.0) : Complex128,
    (3.0, -4.0) : Complex128,
    (0.0, 5.0) : Complex128
]
let L = method_for(A)
let f = lambda(z) -> conj(z)
let result = L <@> f |> compute
// EXPECT: result = [(1, -2), (3, 4), (0, -5)]
"""

// hermitian(A) = conj(transpose(A, [0,1])) — the conjugate-transpose (adjoint)
// A^H of a complex 2x3 -> 3x2. Element (a,b) at [i][j] becomes (a,-b) at [j][i].
// Result is a plain dense array (the adjoint operation), not SymHermitian-typed.
let test_hermitian_adjoint = """
let A: Array<Complex128 like Idx<2>, Idx<3>> = [
    [(1.0, 2.0) : Complex128, (3.0, -1.0) : Complex128, (0.0, 5.0) : Complex128],
    [(2.0, 1.0) : Complex128, (-1.0, 4.0) : Complex128, (6.0, 0.0) : Complex128]
]
let result = hermitian(A)
// EXPECT: result = [(1, -2), (2, -1), (3, 1), (-1, -4), (0, -5), (6, 0)]
"""

// gram(A, A) complex -> Hermitian 2x2. result[i][j] = sum_k A[i][k]*conj(A[j][k]).
// Upper-triangle canonical print (left-justified [i][jr]): (0,0),(0,1),(1,1).
let test_gram_hermitian = """
let A: Array<Complex128 like Idx<2>, Idx<3>> = [
    [(1.0, 1.0) : Complex128, (2.0, 0.0) : Complex128, (0.0, 1.0) : Complex128],
    [(3.0, -1.0) : Complex128, (1.0, 2.0) : Complex128, (2.0, 0.0) : Complex128]
]
let result = gram(A, A)
// EXPECT: result = [(7, 0), (4, 2), (19, 0)]
"""

// gram(A, A) real -> symmetric 2x2 (conj is identity on reals).
let test_gram_symmetric = """
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let result = gram(A, A)
// EXPECT: result = [14, 32, 77]
"""

// gram(A, B) distinct arrays -> general dense 2x2 (no symmetry).
let test_gram_dense = """
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let B: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 0.0, 1.0], [0.0, 1.0, 0.0]]
let result = gram(A, B)
// EXPECT: result = [4, 2, 10, 5]
"""

// decompact(gram(A,A), d) on a rank-2 Hermitian -> dense n×n with the lower
// triangle conjugated (diagonal kept, real). H = [[7, 4+2i],[4-2i, 19]].
let test_gram_decompact_hermitian = """
let A: Array<Complex128 like Idx<2>, Idx<3>> = [
    [(1.0, 1.0) : Complex128, (2.0, 0.0) : Complex128, (0.0, 1.0) : Complex128],
    [(3.0, -1.0) : Complex128, (1.0, 2.0) : Complex128, (2.0, 0.0) : Complex128]
]
let H = gram(A, A)
let result = decompact(H, 0)
// EXPECT: result = [(7, 0), (4, 2), (4, -2), (19, 0)]
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

// ============================================================================
// Foreign-key dereference tests (step 0 for the SQL-like layer)
//
// These exercise the round-trip:
//   1. Declare an array whose ELEMENT type is a named index alias —
//      `Array<RegionIdx like StationIdx>`. The element type lowers to
//      IRTIdxTagged(IRTScalar ETInt64, IRefNamed "RegionIdx").
//   2. Co-iterate this foreign-key array alongside a value array sharing
//      the same outer index type (StationIdx). The kernel parameter for
//      the foreign-key array picks up the RegionIdx nominal tag.
//   3. Use that tagged value to index a sibling array whose own index
//      type is RegionIdx. Step 5's tag-matching check on the index site
//      enforces tag equality, so this typechecks; codegen emits a plain
//      integer subscript.
//
// This is the compositional analogue of SQL's inner-join-plus-projection:
// one DMWF pass, no intermediate join table, lookup fused with kernel.
// ============================================================================

let test_foreign_key_deref_kernel = """
// Basic foreign-key dereference inside a kernel. Iterate the StationIdx
// range; inside the kernel, `region(s)` produces a RegionIdx-tagged
// value, and `region_avg(region(s))` chains the dereference into a
// sibling RegionIdx-keyed array. The whole pipeline is one DMWF pass
// — SQL's inner-join-plus-projection collapsed into one indexing
// expression.
//
// Multi-arg method_for(A, B) produces the Cartesian outer product
// (CROSS JOIN). For co-iteration of same-indexed arrays, iterate the
// shared index range and dereference explicitly inside the kernel.
type StationIdx = Idx<6>
type RegionIdx = Idx<3>
let region: Array<RegionIdx like StationIdx> = [0, 1, 2, 0, 1, 2]
let region_avg: Array<Float64 like RegionIdx> = [10.0, 20.0, 30.0]
let temps: Array<Float64 like StationIdx> = [11.0, 19.0, 31.0, 12.0, 21.0, 29.0]
let deltas = method_for(range<StationIdx>) <@> lambda(s) -> temps(s) - region_avg(region(s)) |> compute
// EXPECT: deltas = [1, -1, 1, 2, 1, -1]
"""

let test_foreign_key_deref_record = """
// Record-based foreign-key transformation. The struct bundles two
// StationIdx-keyed fields (one value, one foreign key); the kernel
// projects out both via chained field-then-index access and
// dereferences the foreign key into a sibling RegionIdx-keyed array.
//
// Tests that field projection preserves the IRefNamed tag on the
// foreign-key array, so `data.region(s)` still produces a properly
// tagged RegionIdx value at the indexing site.
type StationIdx = Idx<6>
type RegionIdx = Idx<3>
struct StationData {
    temp:   Array<Float64 like StationIdx>,
    region: Array<RegionIdx like StationIdx>
}
let region_baseline: Array<Float64 like RegionIdx> = [10.0, 20.0, 30.0]
let data = StationData {
    temp   = [11.0, 19.0, 31.0, 12.0, 21.0, 29.0],
    region = [0, 1, 2, 0, 1, 2]
}
let result = method_for(range<StationIdx>) <@> lambda(s) -> data.temp(s) - region_baseline(data.region(s)) |> compute
// EXPECT: result = [1, -1, 1, 2, 1, -1]
"""


// ----------------------------------------------------------------------------
// transpose(A, [d1, d2]) — hard transpose, swap two arity-1 SymNone axes.
// Allocates a fresh pool at the swapped extents and copies axis-swapped. The
// result is an independent array (no aliasing back to the source).

let test_transpose_2d_nonsquare = """
// Non-square 2x3 -> 3x2. Non-square is the discriminating case: a square test
// would pass even if the extent-swap were forgotten. Flat print is row-major
// over the RESULT (3x2), so [[1,2,3],[4,5,6]] transposed reads column-first.
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let result = transpose(A, [0, 1])
// A row-major:        1 2 3 4 5 6
// result is 3x2: [[1,4],[2,5],[3,6]] -> row-major 1 4 2 5 3 6
// EXPECT: result = [1, 4, 2, 5, 3, 6]
"""

let test_transpose_square = """
// Square 2x2, sanity check that the swap is a genuine transpose (off-diagonal
// elements exchange, diagonal fixed).
let A: Array<Float64 like Idx<2>, Idx<2>> = [[1.0, 2.0], [3.0, 4.0]]
let result = transpose(A, [0, 1])
// [[1,2],[3,4]] -> [[1,3],[2,4]] -> row-major 1 3 2 4
// EXPECT: result = [1, 3, 2, 4]
"""

let test_transpose_rank3 = """
// General rank: swap the outer two axes of a 2x2x2, leaving the innermost
// axis in place. Exercises the N-deep copy with two swapped subscripts.
let A: Array<Float64 like Idx<2>, Idx<2>, Idx<2>> = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let result = transpose(A, [0, 1])
// A[i][j][k] -> result[j][i][k]. Row-major result:
//   r[0][0][*]=A[0][0]=1,2 ; r[0][1][*]=A[1][0]=5,6
//   r[1][0][*]=A[0][1]=3,4 ; r[1][1][*]=A[1][1]=7,8
// EXPECT: result = [1, 2, 5, 6, 3, 4, 7, 8]
"""

// Reject probes — each should fail before producing valid IR.

let test_transpose_reject_same_axis = """
// d1 == d2 is the identity; reject rather than silently no-op.
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let result = transpose(A, [1, 1])
// EXPECT: typecheck failure — the two axes must differ.
"""

let test_transpose_reject_out_of_range = """
// Axis 5 does not exist in a rank-2 array.
let A: Array<Float64 like Idx<2>, Idx<3>> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let result = transpose(A, [0, 5])
// EXPECT: typecheck failure — axis out of range.
"""

// ----------------------------------------------------------------------------
// Intra-group transpose: swapping two dimensions inside one index type is a
// storage-preserving, per-class transform (symmetric -> identity, antisym ->
// whole-array negation). No decompaction, no dense blow-up.

let test_transpose_symmetric_identity = """
// Symmetric intra-group transpose is the IDENTITY: A(i,j) = A(j,i), so storage
// is unchanged and the result equals the source. Producer: reynolds(g), g=2x+y
// -> 3(x+y), triangular canonicals (0,0)=6 (0,1)=9 (0,2)=12 (1,1)=12 (1,2)=15
// (2,2)=18. transpose(sym,[0,1]) prints identically to sym.
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let sym = L <@> reynolds(g) |> compute
let result = transpose(sym, [0, 1])
// EXPECT: result = [6, 9, 12, 12, 15, 18]
"""

let test_transpose_antisym_negate = """
// Antisymmetric intra-group transpose NEGATES the whole array (any transposition
// is odd parity). Producer: reynolds(g, Antisymmetric), g=2x+y, A=[1,2,3] ->
// strict canonicals (0,1)=-1 (0,2)=-2 (1,2)=-1. transpose negates each stored
// element -> (0,1)=1 (0,2)=2 (1,2)=1; the storage shape (strict triangular) is
// unchanged, so the print walks the same canonical layout.
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let anti = L <@> reynolds(g, Antisymmetric) |> compute
let result = transpose(anti, [0, 1])
// EXPECT: result = [1, 2, 1]
"""

// ----------------------------------------------------------------------------
// decompact(A, d) — pull the compact component at dim d out as a free Idx,
// materializing the full dense tensor. v1: rank-2 sole compact slot
// (SymIdx<2,n> / AntisymIdx<2,n>) → dense n×n.

let test_decompact_symmetric = """
// Symmetric source: reynolds(g) with g=2x+y gives 3(x+y), stored triangularly
// as (0,0)=6 (0,1)=9 (0,2)=12 (1,1)=12 (1,2)=15 (2,2)=18. Decompacting the
// symmetric group yields a dense 3×3 symmetric matrix (each canonical written
// to both (i,j) and (j,i); diagonal once).
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let sym = L <@> reynolds(g) |> compute
let result = decompact(sym, 0)
// M = [[6,9,12],[9,12,15],[12,15,18]] -> row-major
// EXPECT: result = [6, 9, 12, 9, 12, 15, 12, 15, 18]
"""

let test_decompact_antisymmetric = """
// Antisymmetric source n=3: reynolds(g, Antisymmetric) with g=2x+y, A=[1,2,3]
// gives strict canonicals (0,1)=-1 (0,2)=-2 (1,2)=-1. Decompacting yields a
// dense 3×3 antisymmetric matrix: M[i][j]=canon for i<j, M[j][i]=-canon, and
// a literal-zero diagonal (the diagonal is never stored).
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let anti = L <@> reynolds(g, Antisymmetric) |> compute
let result = decompact(anti, 0)
// M = [[0,-1,-2],[1,0,-1],[2,1,0]] -> row-major
// EXPECT: result = [0, -1, -2, 1, 0, -1, 2, 1, 0]
"""

// Reject probes.

let test_decompact_reject_plain = """
// Decompacting a plain Idx axis — nothing to decompact.
let A: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]
let result = decompact(A, 0)
// EXPECT: typecheck failure — plain axis, nothing to decompact.
"""

let test_decompact_reject_out_of_range = """
// Dimension 5 does not exist.
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> x + y
let sym = L <@> g |> compute
let result = decompact(sym, 5)
// EXPECT: typecheck failure — dimension out of range.
"""

// ---- General symmetric fission (rank >= 3) ----
let test_decompact_sym3_peel_first = """
// d=0: freed axis = dim 0, kept pair = (j,k) symmetric. Output Idx<3> ->
// SymIdx<2,3>, mask {1,2,2}, storage out[i][a][b] with (j,k)=(a,a+b).
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let sym = L <@> f |> compute
let result = decompact(sym, 0)
// EXPECT: result = [1, 2, 3, 4, 6, 9, 2, 4, 6, 8, 12, 18, 3, 6, 9, 12, 18, 27]
"""

// Chained decompact: fully densify a rank-3 symmetric array by decompacting
// twice. The FIRST decompact (sym, 0) yields [Idx ; SymIdx<2>] (one free leading
// dim + a symmetric pair). The SECOND decompact (., 1) then targets that pair
// while a free Idx PRECEDES it — exercising the surrounding-free-dimension
// codegen path (leading free loop wrapping the symmetric fission scatter). The
// result is a fully dense 3x3x3 array: dense[p][q][r] = product over sorted
// (p,q,r) = A[p]*A[q]*A[r] (order-independent by symmetry). Confirms that
// composed decompaction densifies, and that surrounding dims map identically.
let test_decompact_chained_full_dense = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let sym = L <@> f |> compute
let step1 = decompact(sym, 0)
let result = decompact(step1, 1)
// EXPECT: result = [1, 2, 3, 2, 4, 6, 3, 6, 9, 2, 4, 6, 4, 8, 12, 6, 12, 18, 3, 6, 9, 6, 12, 18, 9, 18, 27]
"""

let test_decompact_sym3_peel_last = """
// d=2: freed axis = dim 2 (last), kept pair = (i,j) symmetric. Output
// SymIdx<2,3> -> Idx<3>, mask {1,1,2}, storage out[a][b][k] with (i,j)=(a,a+b).
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let sym = L <@> f |> compute
let result = decompact(sym, 2)
// EXPECT: result = [1, 2, 3, 2, 4, 6, 3, 6, 9, 4, 8, 12, 6, 12, 18, 9, 18, 27]
"""

let test_decompact_sym3_peel_mid = """
// d=1: freed axis = middle. Both remainders are arity-1 (= plain Idx), so the
// group fully dissolves to a dense 3x3x3. mask {1,2,3}, storage out[i][j][k].
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let sym = L <@> f |> compute
let result = decompact(sym, 1)
// EXPECT: result = [1, 2, 3, 2, 4, 6, 3, 6, 9, 2, 4, 6, 4, 8, 12, 6, 12, 18, 3, 6, 9, 6, 12, 18, 9, 18, 27]
"""

// ---- Antisym rank-3 compact-residual decompact (boundary cuts) ----
// Source: reynolds(f, Antisymmetric) with f = x*x*y + z, A=[1,2,3,4]. This is
// the standard signed-permutation antisymmetrization; the strict canonical
// rank-3 source (i<j<k), C(4,3)=4 elements, is:
//   (0,1,2)=-2 (0,1,3)=-6 (0,2,3)=-6 (1,2,3)=-2
// (Verified against the hand-computed antisymmetrization. These differ from an
// earlier buggy build whose rank>=3 strict loop offset was flat-1 instead of
// cumulative, collapsing the source to [0,-2,0,0]; the fix made the iteration
// visit the true canonical tuples.)

// Intermediate: assert the rank-3 antisym SOURCE pool directly (no decompact),
// pinning the reynolds-source values the decompact EXPECTs are derived from.
let test_reynolds_anti3_source = """
let A = [1.0, 2.0, 3.0, 4.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let result = L <@> reynolds(f, Antisymmetric) |> compute
// Strict canonical pool (i<j<k), DFS order.
// EXPECT: result = [-2.0, -6.0, -6.0, -2.0]
"""

let test_decompact_anti3_peel_first = """
let A = [1.0, 2.0, 3.0, 4.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let anti = L <@> reynolds(f, Antisymmetric) |> compute
let result = decompact(anti, 0)
// EXPECT: result = [0, 0, 0, -2, -6, -6, 0, 2, 6, 0, 0, -2, -2, 0, 6, 0, 2, 0, -6, -6, 0, -2, 0, 0]
"""

// d=2 boundary cut (last): residual AntisymIdx<2> + freed axis dim2.
// Storage SYMM {1,1,2} STRICT {1,1,0}, card = C(n,2)*n = 24, DFS order
// (strict residual pair outer, freed F inner).
let test_decompact_anti3_peel_last = """
let A = [1.0, 2.0, 3.0, 4.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let anti = L <@> reynolds(f, Antisymmetric) |> compute
let result = decompact(anti, 2)
// EXPECT: result = [0, 0, -2, -6, 0, 2, 0, -6, 0, 6, 6, 0, -2, 0, 0, -2, -6, 0, 2, 0, -6, -2, 0, 0]
"""

// ---- n=5 cases (C(5,3)=10 canonical source values, 4 distinct magnitudes
// 2/6/12/16 — exercises distinct-value sign placement across larger pools).
// Source x*x*y+z, A=[1..5]; canonical source (i<j<k) =
//   [-2, -6, -12, -6, -16, -12, -2, -6, -6, -2]  (verified antisymmetrization).
let test_reynolds_anti3_source_n5 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let result = L <@> reynolds(f, Antisymmetric) |> compute
// EXPECT: result = [-2.0, -6.0, -12.0, -6.0, -16.0, -12.0, -2.0, -6.0, -6.0, -2.0]
"""

// d=0 boundary cut, n=5: SYMM {1,2,2} STRICT {0,1,1}, card = 5*C(5,2) = 50.
let test_decompact_anti3_peel_first_n5 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let anti = L <@> reynolds(f, Antisymmetric) |> compute
let result = decompact(anti, 0)
// EXPECT: result = [0, 0, 0, 0, -2, -6, -12, -6, -16, -12, 0, 2, 6, 12, 0, 0, 0, -2, -6, -6, -2, 0, 6, 16, 0, 2, 6, 0, 0, -2, -6, -6, 0, 12, -2, 0, 6, 0, 2, 0, -12, -16, -12, 0, -6, -6, 0, -2, 0, 0]
"""

// d=2 boundary cut (last), n=5: SYMM {1,1,2} STRICT {1,1,0}, card = C(5,2)*5 = 50.
let test_decompact_anti3_peel_last_n5 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * x * y + z
let anti = L <@> reynolds(f, Antisymmetric) |> compute
let result = decompact(anti, 2)
// EXPECT: result = [0, 0, -2, -6, -12, 0, 2, 0, -6, -16, 0, 6, 6, 0, -12, 0, 12, 16, 12, 0, -2, 0, 0, -2, -6, -6, 0, 2, 0, -6, -12, 0, 6, 6, 0, -6, -2, 0, 0, -2, -16, -6, 0, 2, 0, -12, -6, -2, 0, 0]
"""

// Interior antisym cut, rank 4 d=1: result Idx -> Idx(freed) -> AntisymIdx<2>
// (one surviving residual group + a degenerate arity-1 left side). Kernel uses
// distinct exponents (x0^3 x1^2 x2) so the antisymmetrization does not vanish.
let test_decompact_anti_interior_r4 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A, A, A)
let f = lambda(w, x, y, z) where comm(w, x, y, z) -> w * w * w * x * x * y
let anti = L <@> reynolds(f, Antisymmetric) |> compute
let result = decompact(anti, 1)
// EXPECT: result = [[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 12, 48, 72, 0, 0, 0, 0, 0, -12, -48, 0, 0, 48, 0, 0, 0, 0, 12, 0, -72, 0, -48, 0, 0, 0, 0, 0, 48, 72, 0, 48, 0, 0, 0, 0, 0, 0, 0, 0, 0, -12, -48, -72, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 12, 48, 0, 0, 0, 0, 0, 12, 0, -12, 0, 72, 0, 0, 0, 0, -12, 0, 0, -48, -72, 0, 0, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0, 12, 48, 0, 0, -48, 0, 0, -12, -48, 0, 0, 0, 0, 0, -12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 12, 0, 0, 48, 0, 0, 12, 0, 0, 0, 48, 0, -48, 0, 0, -12, 0, 0, 0, 0, 0, 0, 0, 0, -12, 0, 72, 0, 48, 0, 0, 12, 0, -72, 0, 0, 0, 0, 12, 0, -12, 0, 0, -48, 0, 0, -12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 72, 48, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, -48, -72, 0, -48, 0, 0, 0, 48, 72, 0, 0, 0, 0, -12, 0, 0, -48, 0, 48, 0, 0, 12, 0, 0, 0, 0, -72, -48, 0, 0, -12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]]
"""

// Interior antisym cut, rank 5 d=2: result AntisymIdx<2> -> Idx(freed) ->
// AntisymIdx<2> (TWO independent surviving residual groups). Kernel
// x0^4 x1^3 x2^2 x3 (distinct exponents) -> non-vanishing antisymmetrization.
let test_decompact_anti_interior_r5 = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A, A, A, A)
let f = lambda(a, b, c, d, e) where comm(a, b, c, d, e) -> a * a * a * a * b * b * b * c * c * d
let anti = L <@> reynolds(f, Antisymmetric) |> compute
let result = decompact(anti, 2)
// EXPECT: result = [[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, -288, 0, 0, 0, 0, 0, 0, 0, 0, 288, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]]
"""


// compound(dense, mask): source-level compound construction (P5) + phase-1
// full-tuple indexing, validated end-to-end against the dense source. The
// dense array and the bool mask share NAMED index types (Lat, Lon), so
// compoundViewType's identity check matches them by tag even though each
// annotation reference gets a fresh Id (the fix aligning compound construction
// with 14.6 shared-index-space identity). All dims are masked (no trailing),
// so indexing a PRESENT cell returns a scalar equal to the dense value there.
// Mask pattern: (0,0)=T (1,0)=T (1,1)=T, (0,1)=F -> cardinality 3.
// B((0,0)) is present, so it must equal dense[0][0] = 10.0.
let test_compound_construct_index = """
type Lat = Idx<2>
type Lon = Idx<2>
let dense: Array<Float64 like Lat, Lon> = [[10.0, 20.0], [30.0, 40.0]]
let m: Array<Bool like Lat, Lon> = [[true, false], [true, true]]
let B = compound(dense, m)
let x = B((0, 0))
// EXPECT: x = 10
"""


// ---------------------------------------------------------------------------
// Partial compound indexing (formalism 4.5, phase 2). Shared fixture shapes:
//
// Rank-2: Lat = Idx<2>, Lon = Idx<3>, dense values 10..60 row-major,
//   mask [[T,F,T],[T,T,F]] -> present lex order:
//   (0,0)=10, (0,2)=30, (1,0)=40, (1,1)=50   (cardinality 4)
//
// Rank-3: X = Y = Z = Idx<2>, dense value at (x,y,z) = 4x + 2y + z + 1,
//   mask [[[T,F],[T,T]],[[F,T],[T,F]]] -> present lex order:
//   (0,0,0)=1, (0,1,0)=3, (0,1,1)=4, (1,0,1)=6, (1,1,0)=7   (cardinality 5)
//
// The four reconstitution shapes are each hit at least once: prefix window
// (dense rank-1 residual, shared data), prefix residual compound (shared
// window, no copy), scattered dense gather, and (via the chained test)
// gather on a residual produced by a prior partial.
// ---------------------------------------------------------------------------

// Trailing wildcard, rank-2: B((0, _)) -> the dense window of valid lons at
// lat 0. Prefix pin -> make_partial_window (shared parent data, heap extent).
// Also exercises scalar indexing into the residual and reduce over it.
let test_compound_partial_prefix_window = """
type Lat = Idx<2>
type Lon = Idx<3>
let dense: Array<Float64 like Lat, Lon> = [[10.0, 20.0, 30.0], [40.0, 50.0, 60.0]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let r = B((0, _))
let x = r(1)
let t = reduce(r, (+))
// EXPECT: r = [10, 30]
// EXPECT: x = 30
// EXPECT: t = 40
"""

// Leading wildcard, rank-2: B((_, 0)) -> valid lats at lon 0. The pinned axis
// is NOT a leading prefix, so the surviving cells are scattered in the parent
// buffer -> make_partial_gather_dense (deep-copy gather).
let test_compound_partial_scattered_dense = """
type Lat = Idx<2>
type Lon = Idx<3>
let dense: Array<Float64 like Lat, Lon> = [[10.0, 20.0, 30.0], [40.0, 50.0, 60.0]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let r = B((_, 0))
// EXPECT: r = [10, 40]
"""

// Short tuple, k-j = 1: a 2-tuple on a rank-3 compound pins the leading
// prefix (x,y) = (0,1); the residual is the dense window over z. This is the
// most reachable partial form via plain tuples (previously gated).
let test_compound_partial_short_tuple_r3 = """
type X = Idx<2>
type Y = Idx<2>
type Z = Idx<2>
let dense: Array<Float64 like X, Y, Z> = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let m: Array<Bool like X, Y, Z> = [[[true, false], [true, true]], [[false, true], [true, false]]]
let B = compound(dense, m)
let r = B((0, 1))
// EXPECT: r = [3, 4]
"""

// Trailing double wildcard, rank-3: B((0, _, _)) pins x = 0; the residual is
// a rank-2 CompoundIdx over (y, z) at that prefix -- shared-window
// make_partial_compound, no data copy. Present residual cells at x = 0:
// (0,0)=1, (1,0)=3, (1,1)=4. Validated by full-tuple reads on the residual.
let test_compound_partial_residual_compound = """
type X = Idx<2>
type Y = Idx<2>
type Z = Idx<2>
let dense: Array<Float64 like X, Y, Z> = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let m: Array<Bool like X, Y, Z> = [[[true, false], [true, true]], [[false, true], [true, false]]]
let B = compound(dense, m)
let r = B((0, _, _))
let x1 = r((0, 0))
let x2 = r((1, 1))
// EXPECT: x1 = 1
// EXPECT: x2 = 4
"""

// Interior wildcard, rank-3: B((0, _, 0)) frees the MIDDLE axis. The pinned
// axes {x, z} are not a prefix -> scattered gather over y at (x,z) = (0,0):
// (0,0,0)=1 present, (0,1,0)=3 present.
let test_compound_partial_interior_wildcard = """
type X = Idx<2>
type Y = Idx<2>
type Z = Idx<2>
let dense: Array<Float64 like X, Y, Z> = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let m: Array<Bool like X, Y, Z> = [[[true, false], [true, true]], [[false, true], [true, false]]]
let B = compound(dense, m)
let r = B((0, _, 0))
// EXPECT: r = [1, 3]
"""

// Chained partial indexing: the rank-2 residual of B((0,_,_)) is itself a
// first-class compound, further partial-indexable. s = r((_, 0)) gathers the
// valid y values at z = 0 within the x = 0 window: residual cells (0,0)=1
// and (1,0)=3.
let test_compound_partial_chained = """
type X = Idx<2>
type Y = Idx<2>
type Z = Idx<2>
let dense: Array<Float64 like X, Y, Z> = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let m: Array<Bool like X, Y, Z> = [[[true, false], [true, true]], [[false, true], [true, false]]]
let B = compound(dense, m)
let r = B((0, _, _))
let s = r((_, 0))
// EXPECT: s = [1, 3]
"""

// All coordinates free pins nothing -- the "residual" would be the array
// itself. Rejected at typecheck with a drop-the-index message.
let test_compound_partial_reject_all_free = """
type Lat = Idx<2>
type Lon = Idx<3>
let dense: Array<Float64 like Lat, Lon> = [[10.0, 20.0, 30.0], [40.0, 50.0, 60.0]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let r = B((_, _))
// EXPECT: typecheck failure — all coordinates free pins nothing.
"""

// A wildcard tuple must be FULL arity: on a rank-3 compound, a 2-tuple with a
// wildcard is ambiguous (which axes?) and rejected; short tuples pin a
// leading prefix only in their wildcard-free form.
let test_compound_partial_reject_short_wildcard = """
type X = Idx<2>
type Y = Idx<2>
type Z = Idx<2>
let dense: Array<Float64 like X, Y, Z> = [[[1.0, 2.0], [3.0, 4.0]], [[5.0, 6.0], [7.0, 8.0]]]
let m: Array<Bool like X, Y, Z> = [[[true, false], [true, true]], [[false, true], [true, false]]]
let B = compound(dense, m)
let r = B((0, _))
// EXPECT: typecheck failure — wildcard tuple must have full arity 3.
"""

// ---------------------------------------------------------------------------
// Compound with a trailing regular dim (mask over a leading prefix), and
// range<CompoundIdx<m>> drivers. Trailing fixture (rank-2 mask + T):
//   Lat = Idx<2>, Lon = Idx<3>, T = Idx<2>, mask [[T,F,T],[T,T,F]],
//   dense[i][j][t] = (i*3 + j)*10 + t. Present cells (lex) with their blocks:
//   (0,0)=[0,1], (0,2)=[20,21], (1,0)=[30,31], (1,1)=[40,41].
// ---------------------------------------------------------------------------

// Full compound tuple + trailing wildcard: B((0,2), _) leaves the trailing
// dim free -- identical to B((0,2)) -- yielding the contiguous trailing-row
// view (raw T*, zero copy; the lex-sorted compact layout keeps each cell's
// block contiguous). Scalar reads off the row and the full scalar form
// validate the values.
let test_compound_trailing_wildcard_row = """
type Lat = Idx<2>
type Lon = Idx<3>
type T = Idx<2>
let dense: Array<Float64 like Lat, Lon, T> = [
    [[0.0, 1.0], [10.0, 11.0], [20.0, 21.0]],
    [[30.0, 31.0], [40.0, 41.0], [50.0, 51.0]]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let r = B((0, 2), _)
let x = r(1)
let y = B((1, 1), 0)
// EXPECT: x = 21
// EXPECT: y = 40
"""

// Partial prefix pin + one trailing dim: B((0, _)) frees Lon AND leaves T
// free. The window of present (0,*) cells is contiguous INCLUDING their
// trailing blocks, so the result is a rank-2 dense view {cells, T} sharing
// the parent data (make_partial_window_trail: fresh row table only).
let test_compound_trailing_partial_window = """
type Lat = Idx<2>
type Lon = Idx<3>
type T = Idx<2>
let dense: Array<Float64 like Lat, Lon, T> = [
    [[0.0, 1.0], [10.0, 11.0], [20.0, 21.0]],
    [[30.0, 31.0], [40.0, 41.0], [50.0, 51.0]]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let w = B((0, _))
// EXPECT: w = [[0, 1], [20, 21]]
"""

// Scattered pin + one trailing dim: B((_, 0)) frees Lat at Lon = 0; the
// surviving cells are non-contiguous, so their trailing BLOCKS are gathered
// (make_partial_gather_dense_trail) into a fresh rank-2 dense array.
let test_compound_trailing_partial_gather = """
type Lat = Idx<2>
type Lon = Idx<3>
type T = Idx<2>
let dense: Array<Float64 like Lat, Lon, T> = [
    [[0.0, 1.0], [10.0, 11.0], [20.0, 21.0]],
    [[30.0, 31.0], [40.0, 41.0], [50.0, 51.0]]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let g = B((_, 0))
// EXPECT: g = [[0, 1], [30, 31]]
"""

// Residual COMPOUND + trailing dim: a rank-3 mask over (X,Y,Z) with trailing
// T; B((0, _, _)) pins x = 0, leaving a rank-2 residual compound over (Y,Z)
// that SHARES the parent window and keeps the trailing stride (same
// make_partial_compound helper, no data copy). Present at x = 0 (with
// blocks): (0,0)=[0,1], (1,0)=[20,21], (1,1)=[30,31].
let test_compound_trailing_residual_compound = """
type X = Idx<2>
type Y = Idx<2>
type Z = Idx<2>
type T = Idx<2>
let dense: Array<Float64 like X, Y, Z, T> = [
    [[[0.0, 1.0], [10.0, 11.0]], [[20.0, 21.0], [30.0, 31.0]]],
    [[[40.0, 41.0], [50.0, 51.0]], [[60.0, 61.0], [70.0, 71.0]]]]
let m: Array<Bool like X, Y, Z> = [[[true, false], [true, true]], [[false, true], [true, false]]]
let B = compound(dense, m)
let r = B((0, _, _))
let x1 = r((1, 1), 1)
let x2 = r((1, 0), 0)
// EXPECT: x1 = 31
// EXPECT: x2 = 20
"""

// A wildcard BEFORE a supplied trailing index would free an interior
// trailing dim (a restructure of the trailing blocks) -- rejected.
let test_compound_trailing_interior_hole_reject = """
type Lat = Idx<2>
type Lon = Idx<3>
type T = Idx<2>
let dense: Array<Float64 like Lat, Lon, T> = [
    [[0.0, 1.0], [10.0, 11.0], [20.0, 21.0]],
    [[30.0, 31.0], [40.0, 41.0], [50.0, 51.0]]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let x = B((0, 0), _, 1)
// EXPECT: typecheck failure — a trailing wildcard must come after all supplied trailing indices.
"""

// range<CompoundIdx<m>>: a virtual driver over the PRESENT cells of a mask
// (formalism 4.5 x range semantics: one loop level over the cardinality;
// the kernel receives one coordinate param per mask dimension, in lex
// order). The output is a compound array sharing the materialized index.
// Kernel i*10+j makes each cell's value its own coordinates, so full-tuple
// reads pin down exactly which cells were visited and in what coordinates.
let test_range_compound_map = """
type Lat = Idx<2>
type Lon = Idx<3>
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let R = method_for(range<CompoundIdx<m>>) <@> lambda(i, j) -> i * 10 + j |> compute
let y0 = R((0, 0))
let y1 = R((0, 2))
let y2 = R((1, 0))
let y3 = R((1, 1))
// EXPECT: y0 = 0
// EXPECT: y1 = 2
// EXPECT: y2 = 10
// EXPECT: y3 = 11
"""

// range<CompoundIdx<m>> driving reads of a SAME-mask compound value: the
// canonical "iterate the present cells, transform the stored values"
// pattern. (The kernel captures B; coordinates arrive as size_t and flow
// into the full-tuple read.)
let test_range_compound_reads_compound = """
type Lat = Idx<2>
type Lon = Idx<3>
let dense: Array<Float64 like Lat, Lon> = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let B = compound(dense, m)
let R = method_for(range<CompoundIdx<m>>) <@> lambda(i, j) -> B((i, j)) * 2.0 |> compute
let y1 = R((0, 2))
let y2 = R((1, 1))
// EXPECT: y1 = 6
// EXPECT: y2 = 10
"""

// A compound range slot cannot (yet) be combined with other index types in
// one range<>; the gate is a codegen-stage #error, so the probe passes by
// failing at compile.
let test_range_compound_mixed_reject = """
type Lat = Idx<2>
type Lon = Idx<3>
type K = Idx<4>
let m: Array<Bool like Lat, Lon> = [[true, false, true], [true, true, false]]
let R = method_for(range<CompoundIdx<m>, K>) <@> lambda(i, j, k) -> i * 100 + j * 10 + k |> compute
// EXPECT: typecheck failure — compound range slot cannot mix with other index types.
"""

// ---------------------------------------------------------------------------
// RaggedIdx coverage round: paths implemented end-to-end but previously
// exercised only in one spelling. Tuple-form reads (existing tests use only
// the curried r(i)(j)), the closed RaggedIdx<lens> annotation driving a
// TOP-LEVEL peel (previously only inside function bodies), and the
// ragged-first-slot surface rule.
// ---------------------------------------------------------------------------

// Tuple-form indexing on a ragged literal: r(i, j) consumes both slots in
// one application, mirroring the dense multi-arg form (the curried r(i)(j)
// spelling is covered elsewhere). Also pins rank(r) = 2: RaggedIdx
// contributes ONE record (unlike DepIdx's two-record expansion), so outer +
// ragged inner = 2.
let test_ragged_tuple_form_read = """
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let v = r(1, 0)
let w = r(2, 3)
let k = rank(r)
// EXPECT: v = 4
// EXPECT: w = 9
// EXPECT: k = 2
"""

// Closed RaggedIdx<lens> annotation on a top-level let, driving the peel
// directly (the closed form was previously peeled only via function
// parameters; the alias test covers annotation + direct reads but not
// iteration). The annotated literal emits the same Ragged<T> wrapper with
// .lens/.offsets companions, and tryRaggedPeel recognizes the closed
// __raggedidx tag alongside the inline/opaque forms.
let test_raggedidx_closed_toplevel_peel = """
let lens: Array<Int64 like Idx<3>> = [3, 2, 1]
let r: Array<Float64 like Idx<3>, RaggedIdx<lens>> = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let row_sums = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
let total = reduce(row_sums, (+))
// EXPECT: row_sums = [6, 9, 6]
// EXPECT: total = 21
"""

// RaggedIdx cannot be the FIRST index slot: with no prior index there is no
// iteration position to drive the lengths lookup (formalism 4.4 -- the
// ragged extent is a function of the outer index). The rank-1 annotation
// also cannot describe the rank-2 nested literal.
let test_raggedidx_first_slot_reject = """
let lens: Array<Int64 like Idx<2>> = [2, 1]
let r: Array<Float64 like RaggedIdx<lens>> = [[1.0, 2.0], [3.0]]
// EXPECT: typecheck failure — RaggedIdx requires at least one prior index.
"""

// ---------------------------------------------------------------------------
// Ragged round 2: shape-preserving elementwise maps, sub-views that carry
// their length, and the surface rules that used to fail silently.
// ---------------------------------------------------------------------------

// Elementwise map over a ragged array: same-shaped ragged output. The kernel
// param is a scalar ELEMENT (contrast the consuming lambda(g) -> reduce(g,+)
// form); TypeCheck keeps the ragged inner dim in the output type, and codegen
// allocates a fresh pool over shared extents/lens/offsets metadata. Printed
// as the flat value sequence, row structure preserved underneath.
let test_ragged_elementwise_map = """
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0, 7.0, 8.0, 9.0]]
let d = method_for(r) <@> lambda(e) -> e * 2.0 |> compute
// EXPECT: d = [2, 4, 6, 8, 10, 12, 14, 16, 18]
"""

// Elementwise output is itself a first-class ragged value: feed it straight
// into the consuming (row-reduce) form. The map's output shares the parent's
// lens metadata, which is exactly what the downstream peel reads.
let test_ragged_elementwise_then_reduce = """
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let d = method_for(r) <@> lambda(e) -> e * 10.0 |> compute
let sums = method_for(d) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: sums = [60, 90, 60]
"""

// A let-bound ragged row now carries its length: `let row = r(i)` binds a
// RaggedRow (pointer + len) rather than decaying to a bare pointer, so
// every length-dependent op downstream works -- print, extents, reduce,
// and element reads.
let test_ragged_subview_metadata = """
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let row = r(1)
let n = extents(row)
let s = reduce(row, (+))
let v = row(0)
// EXPECT: row = [4, 5]
// EXPECT: n = 2
// EXPECT: s = 9
// EXPECT: v = 4
"""

// extents() on a MULTI-RANK ragged array is statically ill-posed: the
// ragged dimension's extent varies per row, so there is no scalar answer
// for the tuple form to contain. extents answers from the argument type
// where it can, and rejects where the type says the question has no
// answer. (Per-row lengths: extents on a peeled or indexed row.)
let test_ragged_extents_rank2_reject = """
let r = [[1.0, 2.0], [3.0]]
let e = extents(r)
// EXPECT: typecheck failure — extents on a ragged array is per-row for the ragged dimension.
"""

// A ragged operand slipping past the peel (here: multi-array method_for
// mixing ragged with dense) is gated rather than silently miscompiled --
// the standard loop nest knows nothing about per-row lengths. Co-iteration
// semantics for ragged operands are a rewrite-spec question.
let test_ragged_multi_array_reject = """
let r = [[1.0, 2.0], [3.0]]
let d = [10.0, 20.0]
let x = method_for(r, d) <@> lambda(g: Array<Float64 like RaggedIdx<_>>, y) -> reduce(g, (+)) + y |> compute
// EXPECT: typecheck failure — ragged operands support only the single-array peel forms.
"""

// A wildcard `_` is a hole, not a value: it is only meaningful as a compound-
// index coordinate (B((a, _, c))), where the index dispatch consumes it. Bound
// into a let as part of a tuple value, it has escaped and must be rejected at
// typecheck. `a` and `c` are defined so the ONLY failure reason is the escaped
// hole (not an undefined name). This currently rejects at typecheck via the
// value-forming-boundary guard in inferLetBindingValue; before that guard it
// slipped to a lowering failwith, which is the wrong (unlocated, exception-
// based) mechanism.
let test_wildcard_escape_reject = """
let a = 1
let c = 3
let t = (a, _, c)
// EXPECT: typecheck failure — a wildcard hole cannot be bound as a value.
"""


/// Index type tests (AntisymIdx, HermitianIdx)
let indexTypeTests = [
    ("Compound Construct + Index", test_compound_construct_index)
    ("Compound Partial Prefix Window", test_compound_partial_prefix_window)
    ("Compound Partial Scattered Dense", test_compound_partial_scattered_dense)
    ("Compound Partial Short Tuple R3", test_compound_partial_short_tuple_r3)
    ("Compound Partial Residual Compound", test_compound_partial_residual_compound)
    ("Compound Partial Interior Wildcard", test_compound_partial_interior_wildcard)
    ("Compound Partial Chained", test_compound_partial_chained)
    ("Compound Partial All Free (rejects)", test_compound_partial_reject_all_free)
    ("Compound Partial Short Wildcard (rejects)", test_compound_partial_reject_short_wildcard)
    ("Compound Trailing Wildcard Row", test_compound_trailing_wildcard_row)
    ("Compound Trailing Partial Window", test_compound_trailing_partial_window)
    ("Compound Trailing Partial Gather", test_compound_trailing_partial_gather)
    ("Compound Trailing Residual Compound", test_compound_trailing_residual_compound)
    ("Compound Trailing Interior Hole (rejects)", test_compound_trailing_interior_hole_reject)
    ("Range Compound Map", test_range_compound_map)
    ("Range Compound Reads Compound", test_range_compound_reads_compound)
    ("Range Compound Mixed (rejects)", test_range_compound_mixed_reject)
    ("Ragged Tuple Form Read", test_ragged_tuple_form_read)
    ("RaggedIdx Closed Toplevel Peel", test_raggedidx_closed_toplevel_peel)
    ("RaggedIdx First Slot (rejects)", test_raggedidx_first_slot_reject)
    ("Ragged Elementwise Map", test_ragged_elementwise_map)
    ("Ragged Elementwise Then Reduce", test_ragged_elementwise_then_reduce)
    ("Ragged Subview Metadata", test_ragged_subview_metadata)
    ("Ragged Extents Rank2 (rejects)", test_ragged_extents_rank2_reject)
    ("Ragged Multi Array (rejects)", test_ragged_multi_array_reject)
    ("Wildcard Escape In Let (rejects)", test_wildcard_escape_reject)
    ("Transpose 2D Nonsquare", test_transpose_2d_nonsquare)
    ("Transpose Square", test_transpose_square)
    ("Transpose Rank3", test_transpose_rank3)
    ("Transpose Same Axis (rejects)", test_transpose_reject_same_axis)
    ("Transpose Out Of Range (rejects)", test_transpose_reject_out_of_range)
    ("Transpose Symmetric Identity", test_transpose_symmetric_identity)
    ("Transpose Antisym Negate", test_transpose_antisym_negate)
    ("Decompact Symmetric", test_decompact_symmetric)
    ("Decompact Antisymmetric", test_decompact_antisymmetric)
    ("Decompact Sym3 Peel First", test_decompact_sym3_peel_first)
    ("Decompact Chained Full Dense", test_decompact_chained_full_dense)
    ("Decompact Sym3 Peel Last", test_decompact_sym3_peel_last)
    ("Decompact Sym3 Peel Mid (dense)", test_decompact_sym3_peel_mid)
    ("Decompact Anti3 Source", test_reynolds_anti3_source)
    ("Decompact Anti3 Peel First", test_decompact_anti3_peel_first)
    ("Decompact Anti3 Peel Last", test_decompact_anti3_peel_last)
    ("Decompact Anti3 Source n5", test_reynolds_anti3_source_n5)
    ("Decompact Anti3 Peel First n5", test_decompact_anti3_peel_first_n5)
    ("Decompact Anti3 Peel Last n5", test_decompact_anti3_peel_last_n5)
    ("Decompact Plain Axis (rejects)", test_decompact_reject_plain)
    ("Decompact Out Of Range (rejects)", test_decompact_reject_out_of_range)
    ("Decompact Anti Interior R4", test_decompact_anti_interior_r4)
    ("Decompact Anti Interior R5", test_decompact_anti_interior_r5)
    ("AntisymIdx Iteration", test_antisym_iteration)
    ("AntisymIdx Kernel", test_antisym_type_in_kernel)
    ("AntisymIdx Reynolds n4", test_antisym_reynolds_n4)
    ("AntisymIdx Reynolds n3", test_antisym_reynolds_n3)
    ("AntisymIdx Reynolds Cancellation", test_antisym_reynolds_cancellation)
    ("Block Return Simple", test_block_return_simple)
    ("Block Return With Loop", test_block_return_with_loop)
    ("AntisymIdx Parse Type", test_antisym_parse_type)
    ("AntisymIdx Decl Param", test_antisym_decl_param)
    ("HermitianIdx Parse Type", test_hermitian_parse_type)
    ("Complex Lit Basic", test_complex_lit_basic)
    ("Complex Lit In Array", test_complex_lit_in_array)
    ("Complex Lit Via Let Annotation", test_complex_lit_via_let_annotation)
    ("Conj Scalar", test_conj_scalar)
    ("Conj Real Identity", test_conj_real_identity)
    ("Conj Array", test_conj_array)
    ("Gram Decompact Hermitian", test_gram_decompact_hermitian)
    ("Gram Hermitian", test_gram_hermitian)
    ("Gram Symmetric", test_gram_symmetric)
    ("Gram Dense", test_gram_dense)
    ("Hermitian Adjoint", test_hermitian_adjoint)
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
    ("Foreign Key Deref Kernel", test_foreign_key_deref_kernel)
    ("Foreign Key Deref Record", test_foreign_key_deref_record)
]
