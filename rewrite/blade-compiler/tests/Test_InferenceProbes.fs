module Blade.Tests.InferenceProbes

// ============================================================================
// Type-checking probes
// ============================================================================
//
// This file holds two related groups of tests, both exercising Blade's
// type-checking pipeline rather than any particular language feature:
//
//   1. Inference probes (display name "Inference: ..."). Originally written
//      to surface gaps in how the typechecker propagated constraints into
//      special forms (extents, mask, sort, reduce) when the kernel arg was
//      unannotated. Most now pass — they document working behavior; the
//      one rejection-style probe (Annotation Mismatch) is a regression
//      guard that confirms a deliberate type clash still errors.
//
//   2. Validator probes (display name "Validator: ... (rejects)"). Each
//      asserts that the IndexTypeValidator rejects a specific forbidden
//      placement of an index type — as a regular function parameter, struct
//      field, let-binding annotation, etc. All seven are expected to fail
//      IR lowering with a specific validator error message; that's the
//      pass criterion.
// ============================================================================

let probe_a_extents_unannotated = """
// Probe A: extents() on an unannotated kernel param.
// Expected to fail at typecheck with "extents requires array" or similar
// — same pattern reduce had before the recent fix.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let sizes = method_for(r) <@> lambda(g) -> extents(g) |> compute
// If this ever passes: EXPECT: sizes = [3, 2, 1]
"""

let probe_b_mask_unannotated = """
// Probe B: mask() on an unannotated kernel param. Result is reduced
// to a scalar so the test has a concrete check at the end.
// Expected to fail at typecheck — mask inspects the array arg's type.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let high_sum = method_for(r) <@> lambda(g) -> reduce(mask(g, lambda(x) -> x > 2.0), (+)) |> compute
// Filtered values per row: [3.0], [4.0, 5.0], [6.0]
// Sums: 3, 9, 6
// If this ever passes: EXPECT: high_sum = [3, 9, 6]
"""

let probe_c_sort_unannotated = """
// Probe C: sort() on an unannotated kernel param. Reduced to first-element
// for a concrete check.
// Expected to fail at typecheck — sort inspects the array arg's type.
let r = [[3.0, 1.0, 2.0], [5.0, 4.0], [6.0]]
let mins = method_for(r) <@> lambda(g) -> reduce(sort(g, lambda(x) -> x), (+)) |> compute
// Sort each row ascending, sum (sort doesn't change sum, just order)
// Sums: 6, 9, 6
// If this ever passes: EXPECT: mins = [6, 9, 6]
"""

let probe_d_reduce_int_source = """
// Probe D: unannotated reduce on an Int64 source. The current reduce fix
// defaults to Float64 when the kernel arg is unbound — does the resulting
// kernel param's Float64 type actually reconcile with the Int64 source at
// <@> time, or does buildApplyInfo skip that unification?
//
// If silent miscompile: produces wrong values or odd C++.
// If clean error: the inference correctly catches the elem-type mismatch.
// If correct: the kernel-param Float64 default works through implicit
// numeric promotion at codegen.
type StationIdx = Idx<6>
type RegionIdx = Idx<3>
let region: Array<RegionIdx like StationIdx> = [0, 1, 2, 0, 1, 2]
let temps_int: Array<Int64 like StationIdx> = [20, 25, 30, 22, 27, 32]
let gk = group_keys(region)
let grouped = group_by(temps_int, gk)
let result = method_for(grouped) <@> lambda(g) -> reduce(g, (+)) |> compute
// EXPECT: result = [42, 52, 62]
"""

let probe_e_annotated_mismatch = """
// Probe E: annotated kernel param with WRONG elem type relative to source.
// Tests Gap 2.5: does buildApplyInfo (or unify) catch the mismatch
// between an annotated Int64 kernel param and a Float64 source array?
//
// If error: unification correctly enforces elem-type compatibility.
// If silent: confirmed silent miscompile — kernel param's elem type and
//   source's elem type are decoupled at typecheck/codegen.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let sums = method_for(r) <@> lambda(g: Array<Int64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// If error (good): test fails at typecheck with a clear message.
// If silent (bad): result is whatever the implicit conversion produces.
// EXPECT: typecheck failure with elem-type mismatch
"""

let probe_f_extents_annotated = """
// Probe F: control — annotated extents on a kernel param. Should pass.
// If it doesn't, extents has more issues than the Gap-1 pattern alone.
let r = [[1.0, 2.0, 3.0], [4.0, 5.0], [6.0]]
let sizes = method_for(r) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> extents(g) |> compute
// If this passes: EXPECT: sizes = [3, 2, 1]
"""

// ============================================================================
// Validator rejection probes (IndexTypeValidator.fs).
//
// Each probe documents a forbidden position for index types. Expected to
// fail at IR (typecheck) with a validator error message. They serve as
// regression tests that the validator catches these patterns.
// ============================================================================

let probe_v1_anon_idx_regular_fn_param = """
// Anonymous Idx<n> as a regular function parameter: rejected.
// Validator should report: "Index types cannot appear as regular function parameters."
function bump(i: Idx<5>) -> Int64 = i + 1
let r = bump(2)
"""

let probe_v2_aliased_idx_regular_fn_param = """
// Aliased index type as a regular function parameter: also rejected
// (regular fns reject all index types; aliased ones are allowed only in
// static fn params and foreign-key element types).
type StationIdx = Idx<6>
function get_temp(temps: Array<Float64 like StationIdx>, s: StationIdx) -> Float64 = temps(s)
let temps: Array<Float64 like StationIdx> = [20.0, 21.5, 22.0, 19.0, 18.5, 23.0]
let r = get_temp(temps, 3)
"""

let probe_v3_anon_idx_struct_field = """
// Anonymous index type as a struct field: rejected.
struct Pair { i: Idx<3>, j: Idx<3> }
let p = Pair { i = 1, j = 2 }
"""

let probe_v4_anon_idx_let_binding = """
// Index type annotation on a regular let binding: rejected.
// To declare a new index type, use `type X = ...`.
let i: Idx<3> = 2
"""

let probe_v5_anon_idx_static_fn_param = """
// Anonymous index type as a static function parameter: rejected.
// Static fn params require nominative identity (aliased only).
static function f(i: Idx<3>) -> Idx<3> = i
"""

let probe_v6_anon_idx_array_elem_type = """
// Anonymous index type as an array element type (foreign-key position):
// rejected. Foreign keys require an alias for nominative identity.
let r: Array<Idx<3> like Idx<6>> = [0, 1, 2, 0, 1, 2]
"""

let probe_v7_runtime_idx_static_fn_return = """
// Runtime-evaluable index type as a static function return: rejected.
let lens: Array<Int64 like Idx<3>> = [3, 2, 1]
static function f() -> RaggedIdx<lens> = lens(0)
"""

let probe_v8_enumidx_mixed_values = """
// EnumIdx with both integer and string literals in the same values list.
// Surface intent is ambiguous (different runtime backings: int64_t vs
// std::string), so it must be a type error at the alias declaration.
type LandType = EnumIdx<[101, "urban", 307]>
"""

let probe_v9_enumidx_mixed_inline = """
// Same kind of mixed-values rejection, but inline (no `type X = ...`).
// Uses `Array<T like EnumIdx<[...]>>` — index domain position, which the
// IndexTypeValidator permits. The mixed-values pre-pass then catches the
// mixing. (Element position `Array<EnumIdx<[...]> like ...>` would trip
// the validator first with "Anonymous index types cannot be used as an
// array element type" — that path forces an alias which is already
// covered by probe_v8.)
let codes: Array<Float64 like EnumIdx<[1, "two"]>> = [1.0, 2.0]
"""

let probe_v10_cross_tag_index_mismatch = """
// Step 5 (Option C, conservative) tag check: indexing an array whose slot
// has a user-named tag with a value bearing a *different* named tag is a
// type error. Tests the "real teeth" of §4.18.2's nominal-typing discipline.
//
// Lat and Lon are two distinct named index types both backed by Idx<3>.
// Structural extent equality is irrelevant — nominal tags differ, so the
// indexing is rejected. The (1 : Lon) cast produces a Lon-tagged value
// via the literal-coercion rule; A's slot expects Lat; mismatch.
type Lat = Idx<3>
type Lon = Idx<3>
let A: Array<Float64 like Lat> = [10.0, 20.0, 30.0]
let v = A((1 : Lon))
// EXPECT: typecheck failure — "Array index tag mismatch: slot expects 'Lat'..."
"""

let probe_v11_foreign_key_cross_tag = """
// Foreign-key dereference variant of probe_v10. A foreign-key array
// produces tagged values via co-iteration in a kernel; using one of those
// values to index a sibling array with a DIFFERENT nominal tag must be
// rejected. This exercises the tag-matching path through a kernel
// parameter rather than through an explicit (expr : Tag) cast.
//
// region's elements are RegionIdx-tagged. by_country expects a CountryIdx
// index. Structural extents match (both Idx<3>) but nominal tags differ;
// the indexing site must reject.
type StationIdx = Idx<6>
type RegionIdx = Idx<3>
type CountryIdx = Idx<3>
let region: Array<RegionIdx like StationIdx> = [0, 1, 2, 0, 1, 2]
let by_country: Array<Float64 like CountryIdx> = [10.0, 20.0, 30.0]
let bad = method_for(region) <@> lambda(r) -> by_country(r) |> compute
// EXPECT: typecheck failure — tag mismatch between RegionIdx and CountryIdx
"""

let probe_v12_struct_field_foreign_key_cross_tag = """
// Cross-tag rejection through a struct-field foreign-key dereference.
//
// `data.region(s)` lowers through the dispatchAppOrIndex helper's
// method-call entry: array-typed struct fields route to TExprIndex,
// producing a value whose type carries the array's element type — which
// for a foreign-key field is `IRTIdxTagged(_, IRefNamed "RegionIdx")`.
// That tagged value is then passed to `by_country(...)`, which enters
// dispatchAppOrIndex via the general ExprApp branch and runs the tag
// check against by_country's CountryIdx slot. The two named tags don't
// match — rejection.
//
// Distinct from probe_v11: that one tests tag flow through
// iteration-tagging on the kernel parameter `r`, which is currently
// permissive (the kernel parameter's type doesn't pick up the iterated
// array's element tag). This probe tests tag flow through explicit
// field indexing, which DOES preserve the tag via TExprIndex's elem-type
// propagation. Both should reject; only this one currently does.
type StationIdx = Idx<6>
type RegionIdx = Idx<3>
type CountryIdx = Idx<3>
struct StationData {
    temp:   Array<Float64 like StationIdx>,
    region: Array<RegionIdx like StationIdx>
}
let data = StationData {
    temp   = [11.0, 19.0, 31.0, 12.0, 21.0, 29.0],
    region = [0, 1, 2, 0, 1, 2]
}
let by_country: Array<Float64 like CountryIdx> = [10.0, 20.0, 30.0]
let bad = method_for(range<StationIdx>) <@> lambda(s) -> by_country(data.region(s)) |> compute
// EXPECT: typecheck failure — tag mismatch between RegionIdx and CountryIdx
"""

let probe_v13_contains_type_mismatch = """
// `contains(A, x)` requires x's type to unify with A's element type.
// Passing a Float64 to an Int64-array contains should reject in typecheck,
// not silently coerce or pick a default. The unify in the ExprContains
// rule (TypeCheck.fs) is the rejection point.
let ints = [1, 2, 3]
let bad = contains(ints, 5.0)
// EXPECT: typecheck failure — Int64 vs Float64 in contains value
"""

let probe_v14_omp_cuda_mutual_exclusion = """
// omp and cuda are mutually exclusive parallelization strategies: a single
// where-clause may not specify both. The parser rejects this at parse time.
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y), omp(x: 1), cuda(block: 64) -> x * y
let result = L <@> f |> compute
// EXPECT: parse failure — only one parallelization strategy per where-clause
"""

let inferenceProbes = [
    ("Inference: Unannotated Extents", probe_a_extents_unannotated)
    ("Inference: Unannotated Mask", probe_b_mask_unannotated)
    ("Inference: Unannotated Sort", probe_c_sort_unannotated)
    ("Inference: Reduce Int Source", probe_d_reduce_int_source)
    ("Inference: Annotation Mismatch (rejects)", probe_e_annotated_mismatch)
    ("Inference: Annotated Extents", probe_f_extents_annotated)
    ("Validator: Anon Idx As Fn Param (rejects)", probe_v1_anon_idx_regular_fn_param)
    ("Validator: Aliased Idx As Fn Param (rejects)", probe_v2_aliased_idx_regular_fn_param)
    ("Validator: Anon Idx As Struct Field (rejects)", probe_v3_anon_idx_struct_field)
    ("Validator: Anon Idx As Let Annotation (rejects)", probe_v4_anon_idx_let_binding)
    ("Validator: Anon Idx As Static Fn Param (rejects)", probe_v5_anon_idx_static_fn_param)
    ("Validator: Anon Idx As Array Elem (rejects)", probe_v6_anon_idx_array_elem_type)
    ("Validator: Runtime Idx As Static Return (rejects)", probe_v7_runtime_idx_static_fn_return)
    ("Validator: EnumIdx Mixed Values (rejects)", probe_v8_enumidx_mixed_values)
    ("Validator: EnumIdx Mixed Values Inline (rejects)", probe_v9_enumidx_mixed_inline)
    ("Nominal: Cross-Tag Index Mismatch (rejects)", probe_v10_cross_tag_index_mismatch)
    ("Nominal: Foreign Key Cross-Tag (rejects)", probe_v11_foreign_key_cross_tag)
    ("Nominal: Struct Field Foreign Key Cross-Tag (rejects)", probe_v12_struct_field_foreign_key_cross_tag)
    ("Nominal: Contains Type Mismatch (rejects)", probe_v13_contains_type_mismatch)
    ("Parallel: omp+cuda Mutual Exclusion (rejects)", probe_v14_omp_cuda_mutual_exclusion)
]
