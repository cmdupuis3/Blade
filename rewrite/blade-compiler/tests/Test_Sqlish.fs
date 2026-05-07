module Blade.Tests.Sqlish

// ============================================================================
// Phase 1: Index Types as Element Types (Foreign Keys)
// ============================================================================

let test_foreignKey_basic = """
// An integer array representing foreign key values (positions in another index space)
let region = [0, 1, 2, 0, 1, 2]
// EXPECT: region = [0, 1, 2, 0, 1, 2]
"""

let test_foreignKey_iterate = """
// Foreign key values can be iterated and used in kernels as integers
let region = [0, 1, 2, 0, 1, 2]
let L = method_for(region)
let result = L <@> lambda(r) -> r * 10 |> compute
// EXPECT: result = [0, 10, 20, 0, 10, 20]
"""

let test_foreignKey_crossRef = """
// Use a foreign key to index into another array (lambda captures the lookup array)
let region = [0, 1, 2, 0, 1, 2]
let weights = [5.0, 6.0, 4.0]

// Look up each station's region weight via foreign key indexing
let L = method_for(region)
let result = L <@> lambda(r) -> weights(r) |> compute
// EXPECT: result = [5.0, 6.0, 4.0, 5.0, 6.0, 4.0]
"""

let test_foreignKey_outerProduct = """
// Foreign keys participate in outer products with data arrays
let temps = [20.0, 25.0, 30.0]
let region = [0, 1, 2]

// Outer product: each (temp, region) pair
let L = method_for(temps, region)
let result = L <@> lambda(t, r) -> t + r |> compute
// 20+0=20, 20+1=21, 20+2=22, 25+0=25, 25+1=26, 25+2=27, 30+0=30, 30+1=31, 30+2=32
// EXPECT: result = [20, 21, 22, 25, 26, 27, 30, 31, 32]
"""

let test_foreignKey_coiteration = """
// Zip temps and region for element-wise pairing, use region values to look up weights
let temps = [20.0, 25.0, 30.0]
let region = [0, 1, 2]
let weights = [1.0, 2.0, 3.0]

// Co-iterate: each (temp, region) pair, look up weight by region index
let L = method_for(zip(temps, region))
let result = L <@> lambda(t, r) -> t * weights(r) |> compute
// 20*1=20, 25*2=50, 30*3=90
// EXPECT: result = [20.0, 50.0, 90.0]
"""

let test_foreignKey_struct = """
// Struct with integer field used as a foreign key
// ETIndexRef infrastructure exists but is exercised in Phase 2
// when Array<RegionIdx like StationIdx> type annotations are enforced
struct Station {
    temp: Float64,
    region: Int64
}

let s = Station { temp = 25.0, region = 2 }
let t = s.temp
let r = s.region
// EXPECT: t = 25
// EXPECT: r = 2
"""

let test_lambda_array_capture = """
// Lambda captures an array from outer scope and indexes into it
let lookup = [10.0, 20.0, 30.0]
let indices = [2, 0, 1]
let L = method_for(indices)
let result = L <@> lambda(i) -> lookup(i) |> compute
// EXPECT: result = [30.0, 10.0, 20.0]
"""

let test_function_array_capture = """
// Named function references a module-level array (generated as C++ lambda with [&] capture)
let weights = [10.0, 20.0, 30.0]

function weighted(idx: Int64) -> Float64 = weights(idx)

let r1 = weighted(0)
let r2 = weighted(2)
// EXPECT: r1 = 10
// EXPECT: r2 = 30
"""

let test_enumidx_basic = """
// EnumIdx: index type with known key values
type LandType = EnumIdx<[101, 205, 307]>

// Array indexed by LandType has 3 elements (one per enum value)
let names = [10.0, 20.0, 30.0]
let result = method_for(names) <@> lambda(x) -> x + 1.0 |> compute
// EXPECT: result = [11.0, 21.0, 31.0]
"""

let test_enumidx_as_element = """
// EnumIdx values used as foreign keys
type RegionCode = EnumIdx<[100, 200, 300]>

// Key array stores actual values (100, 200, 300), not positions
let codes = [100, 300, 200, 100, 300, 200]
let L = method_for(codes)
let result = L <@> lambda(c) -> c |> compute
// EXPECT: result = [100, 300, 200, 100, 300, 200]
"""

// ============================================================================
// Phase 2: mask(array, pred) — Filtered Iteration
// ============================================================================

let test_mask_basic = """
// Filter an array to only positive values
let A = [3.0, -1.0, 4.0, -2.0, 5.0]
let pos = mask(A, lambda(x) -> x > 0.0)

let result = method_for(pos) <@> lambda(x) -> x * 2.0 |> compute
// EXPECT: result = [6.0, 8.0, 10.0]
"""

let test_mask_all_pass = """
// All elements pass the predicate
let A = [1.0, 2.0, 3.0]
let all = mask(A, lambda(x) -> x > 0.0)

let result = method_for(all) <@> lambda(x) -> x |> compute
// EXPECT: result = [1.0, 2.0, 3.0]
"""

let test_mask_none_pass = """
// No elements pass — empty result (0-extent array)
let A = [1.0, 2.0, 3.0]
let none = mask(A, lambda(x) -> x > 100.0)

let result = method_for(none) <@> lambda(x) -> x |> compute
// EXPECT: result = []
"""

let test_mask_with_compute = """
// Filter the result of a 1D computation
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let doubled = method_for(A) <@> lambda(a) -> a * 2.0 |> compute

// Only keep doubled values > 5
let big = mask(doubled, lambda(x) -> x > 5.0)
let result = method_for(big) <@> lambda(x) -> x |> compute
// doubled = [2, 4, 6, 8, 10], filter >5: [6, 8, 10]
// EXPECT: result = [6.0, 8.0, 10.0]
"""

let test_mask_composition = """
// Two successive masks
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]
let above3 = mask(A, lambda(x) -> x > 3.0)
let below8 = mask(above3, lambda(x) -> x < 8.0)

let result = method_for(below8) <@> lambda(x) -> x |> compute
// EXPECT: result = [4.0, 5.0, 6.0, 7.0]
"""

// ============================================================================
// Phase 2b: SQL WHERE equivalent
// ============================================================================

let test_sql_where = """
// SQL: SELECT temp FROM stations WHERE temp > 25.0
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]

let hot = mask(temps, lambda(t) -> t > 25.0)
let result = method_for(hot) <@> lambda(t) -> t |> compute
// EXPECT: result = [30.0, 27.0, 32.0]
"""

// ============================================================================
// Phase 3: intersect / union — Mask Combinators
// ============================================================================

let test_intersect_basic = """
// Intersection of two masks: positions valid in both
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let above2 = mask(A, lambda(x) -> x > 2.0)
let below5 = mask(A, lambda(x) -> x < 5.0)

let both = intersect(above2, below5)
let result = method_for(both) <@> lambda(x) -> x |> compute
// EXPECT: result = [3.0, 4.0]
"""

let test_union_basic = """
// Union of two masks: positions valid in either
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let low = mask(A, lambda(x) -> x < 2.0)
let high = mask(A, lambda(x) -> x > 4.0)

let either = union(low, high)
let result = method_for(either) <@> lambda(x) -> x |> compute
// EXPECT: result = [1.0, 5.0]
"""

let test_intersect_disjoint = """
// Disjoint masks — intersection is empty
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let low = mask(A, lambda(x) -> x < 3.0)
let high = mask(A, lambda(x) -> x > 3.0)

let none = intersect(low, high)
let result = method_for(none) <@> lambda(x) -> x |> compute
// EXPECT: result = []
"""

let test_sql_where_and = """
// SQL: SELECT temp FROM stations WHERE temp > 20.0 AND temp < 30.0
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]

let above20 = mask(temps, lambda(t) -> t > 20.0)
let below30 = mask(temps, lambda(t) -> t < 30.0)
let result = method_for(intersect(above20, below30)) <@> lambda(t) -> t |> compute
// EXPECT: result = [25.0, 22.0, 27.0]
"""

// ============================================================================
// Phase 4: group_by — Two-op flat API (group_keys + group_by)
// ============================================================================
// Tests use simple kernel patterns (g(0), g(0) + g(1)) instead of reduce/extent,
// since reduce is convenience-not-primitive and extent is deferred.
// With let-binding annotation enforcement (bidirectional checking), tests
// can opt into Case 1 (positional Idx<N>) or Case 2 (EnumIdx reverse lookup)
// by annotating the keys array; otherwise Case 3 (dynamic max+1) fires.

let test_groupby_idx_first = """
// First element of each group; no annotation -> Case 3 dynamic.
let region = [0, 1, 2, 0, 1, 2]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) |> compute
// EXPECT: result = [20.0, 25.0, 30.0]
"""

let test_groupby_idx_sum_two = """
// Sum first two members of each group (each group has exactly 2 here)
let region = [0, 1, 2, 0, 1, 2]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) + g(1) |> compute
// EXPECT: result = [42.0, 52.0, 62.0]
"""

let test_groupby_idx_annotated = """
// Annotated keys -> Case 1 static (Idx<N> bucket count known at compile time).
type RegionIdx = Idx<3>
type StationIdx = Idx<6>
let region: Array<RegionIdx like StationIdx> = [0, 1, 2, 0, 1, 2]
let temps: Array<Float64 like StationIdx> = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) |> compute
// EXPECT: result = [20.0, 25.0, 30.0]
"""

let test_groupby_enum_first = """
// Annotated EnumIdx keys -> Case 2 static (reverse lookup, sparse key space).
type LandType = EnumIdx<[101, 205, 307]>
type StationIdx = Idx<6>
let codes: Array<LandType like StationIdx> = [101, 205, 307, 101, 205, 307]
let temps: Array<Float64 like StationIdx> = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(codes)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) |> compute
// EXPECT: result = [20.0, 25.0, 30.0]
"""

// Phase 4 completion: per-group reduction kernels, exercising the ragged peel
// path that Leg 1 generalized. The peel emits sub-array `_extents` for the
// kernel param, so any combinator that consults extents (reduce, etc.) finds
// the right metadata at runtime.
//
// Note on kernel param annotation: kernels that pass `g` as an array argument
// (rather than just calling `g(args)`) require an explicit `RaggedIdx<_>`
// annotation. The reason is that operations like `reduce(g, ...)` inspect
// the argument's resolved type but don't drive type inference, so an
// unannotated `g` remains a type variable and the operation rejects it.
// `g(0)` works without annotation because the calling syntax structurally
// forces `g` to be an array. The opaque `RaggedIdx<_>` annotation is the
// canonical way to type a kernel parameter that receives a runtime-shape
// sub-array — which was its design purpose in Leg 1.

let test_groupby_reduce_per_group = """
// Per-group sum via reduce. The peel binds g to a sub-array sized by the
// group; reduce iterates that sub-array using its emitted _extents.
let region = [0, 1, 2, 0, 1, 2]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: result = [42, 52, 62]
"""

let test_groupby_reduce_unannotated = """
// Same per-group sum without an explicit kernel-param annotation. Works
// because reduce's typecheck drives inference for unconstrained kernel
// parameters: it deduces an element type from the kernel's first arg
// (concrete IRTScalar wins; otherwise defaults to Float64), then unifies
// the array arg with a fresh rank-1 array of that elem type. For section
// kernels like (+) the args are fresh type variables, so this falls
// through to the Float64 default — matching what section lowering already
// emits in the IR. For non-Float64 data with a section kernel, an
// explicit annotation on the kernel param is still required.
let region = [0, 1, 2, 0, 1, 2]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> reduce(g, (+)) |> compute
// EXPECT: result = [42, 52, 62]
"""

let test_groupby_mixed_kernel = """
// Kernel mixing direct indexing with a reduce call. Confirms the peel's
// kernel rewriter (g(args) -> IRIndex) doesn't break the reduce(g, ...)
// path: only g(args) shapes get rewritten, not g passed as an array.
let region = [0, 1, 2, 0, 1, 2]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> g(0) + reduce(g, (+)) |> compute
// First elements [20, 25, 30] + sums [42, 52, 62] = [62, 77, 92]
// EXPECT: result = [62, 77, 92]
"""

// ============================================================================
// Phase 5: sort — Sorted Iteration (NOT YET IMPLEMENTED)
// ============================================================================

let test_sort_ascending = """
// Sort array by value ascending
let A = [30.0, 10.0, 50.0, 20.0, 40.0]
let sorted = sort(A, lambda(x) -> x)
let result = method_for(sorted) <@> lambda(x) -> x |> compute
// EXPECT: result = [10.0, 20.0, 30.0, 40.0, 50.0]
"""

let test_sort_descending = """
// Sort array by value descending
let A = [30.0, 10.0, 50.0, 20.0, 40.0]
let sorted = sort(A, lambda(x) -> -x)
let result = method_for(sorted) <@> lambda(x) -> x |> compute
// EXPECT: result = [50.0, 40.0, 30.0, 20.0, 10.0]
"""

// ============================================================================
// Combined: SQL-like queries using multiple features (depends on sort)
// ============================================================================

let test_sql_select_where_orderby = """
// SQL: SELECT temp FROM stations WHERE temp > 25.0 ORDER BY temp DESC
type StationIdx = Idx<6>
let temps: Array<Float like StationIdx> = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]

let hot = mask(temps, lambda(t) -> t > 25.0)
let sorted = sort(hot, lambda(t) -> -t)
let result = method_for(sorted) <@> lambda(t) -> t |> compute
// EXPECT: result = [32.0, 30.0, 27.0]
"""

// ============================================================================
// Test Lists
// ============================================================================

/// Phase 1: Foreign key arrays, ETIndexRef, array captures
let foreignKeyTests = [
    ("Foreign Key Basic", test_foreignKey_basic)
    ("Foreign Key Iterate", test_foreignKey_iterate)
    ("Foreign Key Cross-Reference", test_foreignKey_crossRef)
    ("Foreign Key Outer Product", test_foreignKey_outerProduct)
    ("Foreign Key Co-iteration", test_foreignKey_coiteration)
    ("Foreign Key Struct", test_foreignKey_struct)
    ("Lambda Array Capture", test_lambda_array_capture)
    ("Function Array Capture", test_function_array_capture)
    ("EnumIdx Basic", test_enumidx_basic)
    ("EnumIdx As Element", test_enumidx_as_element)
]

/// Phase 2: mask
let maskTests = [
    ("Mask Basic", test_mask_basic)
    ("Mask All Pass", test_mask_all_pass)
    ("Mask None Pass", test_mask_none_pass)
    ("Mask With Compute", test_mask_with_compute)
    ("Mask Composition", test_mask_composition)
    ("SQL WHERE", test_sql_where)
]

/// Phase 3: intersect / union
let setOpTests = [
    ("Intersect Basic", test_intersect_basic)
    ("Union Basic", test_union_basic)
    ("Intersect Disjoint", test_intersect_disjoint)
    ("SQL WHERE AND", test_sql_where_and)
]

/// Phase 4: group_by
let groupByTests = [
    ("GroupBy Idx First", test_groupby_idx_first)
    ("GroupBy Idx Sum Two", test_groupby_idx_sum_two)
    ("GroupBy Idx Annotated", test_groupby_idx_annotated)
    ("GroupBy Enum First", test_groupby_enum_first)
    ("GroupBy Reduce Per Group", test_groupby_reduce_per_group)
    ("GroupBy Reduce Unannotated", test_groupby_reduce_unannotated)
    ("GroupBy Mixed Kernel", test_groupby_mixed_kernel)
]

/// Phase 5: sort
let sortTests = [
    ("Sort Ascending", test_sort_ascending)
    ("Sort Descending", test_sort_descending)
]

/// Phase 6: reduce
let test_reduce_sum_section = """
// reduce with operator section: sum a 1D array
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let total = reduce(A, (+))
// EXPECT: total = 15
"""

let test_reduce_product_section = """
// reduce with operator section: product
let A = [1.0, 2.0, 3.0, 4.0]
let prod = reduce(A, (*))
// EXPECT: prod = 24
"""

let test_reduce_lambda = """
// reduce with explicit lambda kernel
let A = [3.0, 1.0, 4.0, 1.0, 5.0, 9.0, 2.0]
let max_val = reduce(A, lambda(a, b) -> if a > b then a else b)
// EXPECT: max_val = 9
"""

let test_reduce_default_kernel = """
// reduce with no kernel argument: defaults to (+).
// reduce(arr) ≡ reduce(arr, (+)) — sum is the most common reduction.
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let total = reduce(A)
// EXPECT: total = 15
"""

let test_reduce_inline_arithmetic = """
// Inline reduce inside arithmetic — exercises the IIFE path in exprToCpp.
// The reduce expression appears as a sub-expression of a binary op.
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let result = 100.0 + reduce(A)
// EXPECT: result = 115
"""

let test_reduce_inline_let_chain = """
// Inline reduce as part of a let-binding expression with following arithmetic.
// Tests that the IIFE result composes with later operations cleanly.
let A = [2.0, 3.0, 4.0]
let doubled = reduce(A) * 2.0
// EXPECT: doubled = 18
"""

let test_reduce_captured_in_kernel = """
// Reduce a CAPTURED outer array from inside a method_for kernel.
// This exercises the IIFE path with capture-by-reference: each iteration
// of the kernel computes (x + total) where total is recomputed by reducing
// the captured array. (Real code would let-bind total once, but this is a
// minimal test of the inline path.)
let weights = [1.0, 2.0, 3.0]
let xs = [10.0, 20.0]
let result = method_for(xs) <@> lambda(x) -> x + reduce(weights) |> compute
// EXPECT: result = [16, 26]
"""

let reduceTests = [
    ("Reduce Sum Section", test_reduce_sum_section)
    ("Reduce Product Section", test_reduce_product_section)
    ("Reduce Lambda Max", test_reduce_lambda)
    ("Reduce Default Kernel", test_reduce_default_kernel)
    ("Reduce Inline Arithmetic", test_reduce_inline_arithmetic)
    ("Reduce Inline Let Chain", test_reduce_inline_let_chain)
    ("Reduce Captured In Kernel", test_reduce_captured_in_kernel)
]

/// Phase 7: extents
let test_extents_static = """
// extents() on a literal-typed array should resolve to a compile-time literal.
// The codegen emits 5L (not a runtime read), parallel to rank()'s behavior.
let A = [10.0, 20.0, 30.0, 40.0, 50.0]
let n = extents(A)
// EXPECT: n = 5
"""

let test_extents_dynamic = """
// extents() on a mask result reads the runtime extent.
// The masked array's length is data-dependent, so the codegen falls back
// to <name>_extents[0].
let xs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
let evens = mask(xs, lambda(x) -> x % 2 == 0)
let n = extents(evens)
// EXPECT: n = 5
"""

let test_extents_after_reduce_for_count = """
// extents() composes with the reduce machinery: counting elements via
// extents is the static-friendly counterpart to summing 1s.
let xs = [3.0, 1.0, 4.0, 1.0, 5.0, 9.0, 2.0, 6.0]
let count = extents(xs)
let sum = reduce(xs, (+))
// EXPECT: count = 8
// EXPECT: sum = 31
"""

let extentsTests = [
    ("Extents Static", test_extents_static)
    ("Extents Dynamic Mask", test_extents_dynamic)
    ("Extents With Reduce", test_extents_after_reduce_for_count)
]

/// Phase 7b: extents on multi-rank arrays — returns a tuple
let test_extents_rank2 = """
// Multi-rank extents returns a tuple (Int64, Int64) — outermost dim first.
// The 2x3 nested array literal produces a typed array with IndexTypes
// [Idx<2>, Idx<3>].
let matrix = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0]]
let dims = extents(matrix)
let (outer, inner) = dims
// EXPECT: outer = 2
// EXPECT: inner = 3
"""

let extentsMultiRankTests = [
    ("Extents Rank 2 Tuple", test_extents_rank2)
]

/// Phase 7c: regression tests for the concerns raised when extents() landed.
/// These exercise specific situations to confirm or refute speculation about
/// edge cases.

// rank() inside a lambda body that captures an array — verifies the existing
// default fall-through in collectVarRefsIR doesn't break capture analysis for
// rank. (Spoiler: rank's argument doesn't actually need capture because it
// resolves statically from the type, but the test pins down that behavior.)
let test_rank_captured = """
// rank(captured) inside a lambda — should be fine because rank resolves
// statically from the type, no runtime capture needed.
let A = [1.0, 2.0, 3.0]
let f = lambda(x: Int64) -> rank(A) + x
let result = f(10)
// EXPECT: result = 11
"""

// Static arithmetic in extent expressions: tests whether tryEvalIntIR
// successfully evaluates literal arithmetic in index types. If the static
// path fires correctly, the C++ output uses a literal; if not, it falls back
// to a runtime read. Either way the test value should be correct — this is
// a *correctness* test, not a performance one. The static path is verified
// indirectly by inspecting the generated C++ for `4L` vs `_extents[0]`.
let test_extents_static_arithmetic = """
// Index extent involving literal arithmetic — should still resolve statically
// when tryEvalIntIR can evaluate the expression.
let A = [1.0, 2.0, 3.0, 4.0]
let n = extents(A)
// EXPECT: n = 4
"""

// reduce() on a statically-extent-non-empty array — codegen should NOT emit
// a runtime guard. Functionally produces same answer regardless.
let test_reduce_static_nonempty = """
// reduce on a statically non-empty array: typecheck proves non-emptiness,
// codegen omits the runtime guard, loop produces the answer directly.
let A = [10.0, 20.0, 30.0]
let total = reduce(A, (+))
// EXPECT: total = 60
"""

// reduce() on a dynamic-extent array (mask result) — codegen MUST emit the
// runtime guard. We test the non-empty path here; the empty path would abort
// the program, which the test framework can't easily verify.
let test_reduce_dynamic_nonempty = """
// reduce on a mask result: extent is dynamic. Codegen emits a runtime guard
// that would abort on empty input. With a non-empty mask result, the guard
// passes and we get the correct sum.
let xs = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let evens = mask(xs, lambda(x) -> x > 2.0)
let total = reduce(evens, (+))
// EXPECT: total = 18
"""

let regressionTests = [
    ("Rank In Captured Lambda", test_rank_captured)
    ("Extents Static Arithmetic", test_extents_static_arithmetic)
    ("Reduce Static NonEmpty", test_reduce_static_nonempty)
    ("Reduce Dynamic NonEmpty", test_reduce_dynamic_nonempty)
]

/// Combined
let sqlCombinedTests = [
    ("SQL Select-Where-OrderBy", test_sql_select_where_orderby)
]

/// All SQL-ish tests
let sqlishTests =
    foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests
