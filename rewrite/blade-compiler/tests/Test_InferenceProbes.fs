module Blade.Tests.InferenceProbes

// ============================================================================
// Phase 0 — Inference probes
// ============================================================================
//
// These tests are deliberate probes of how Blade's typecheck/inference
// machinery propagates (or fails to propagate) constraints into special
// forms used in kernel-parameter contexts.
//
// They are EXPECTED TO FAIL until the broader inference refactor (Phase
// 3 onward) is complete. Each probe targets a specific gap:
//
//   - Probes A/B/C: same Gap-1 pattern as reduce (special form inspects
//     resolved type but doesn't drive unification when the arg is an
//     unannotated kernel param).
//
//   - Probe D: tests Gap 2.5 — does the elem type chosen at reduce
//     typecheck (currently defaults to Float64) actually flow correctly
//     when the source data is non-Float64? Reveals whether buildApplyInfo
//     needs to unify kernel param types with source per-row types.
//
//   - Probe E: directly tests Gap 2.5 by deliberately mismatching an
//     annotated kernel-param elem type against the source's elem type.
//     If this silently compiles, we have a silent miscompile latent in
//     the language; if it errors, the unification is at least partly
//     working somewhere.
//
//   - Probe F: control case — annotated extents on a kernel param.
//     Should pass; if it doesn't, extents has more issues than just
//     Gap 1.
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

let inferenceProbes = [
    ("Probe A: Extents Unannotated", probe_a_extents_unannotated)
    ("Probe B: Mask Unannotated", probe_b_mask_unannotated)
    ("Probe C: Sort Unannotated", probe_c_sort_unannotated)
    ("Probe D: Reduce Int Source", probe_d_reduce_int_source)
    ("Probe E: Annotated Mismatch", probe_e_annotated_mismatch)
    ("Probe F: Extents Annotated (control)", probe_f_extents_annotated)
    ("Probe V1: Anon Idx Regular Fn Param", probe_v1_anon_idx_regular_fn_param)
    ("Probe V2: Aliased Idx Regular Fn Param", probe_v2_aliased_idx_regular_fn_param)
    ("Probe V3: Anon Idx Struct Field", probe_v3_anon_idx_struct_field)
    ("Probe V4: Anon Idx Let Binding", probe_v4_anon_idx_let_binding)
    ("Probe V5: Anon Idx Static Fn Param", probe_v5_anon_idx_static_fn_param)
    ("Probe V6: Anon Idx Array Elem Type", probe_v6_anon_idx_array_elem_type)
    ("Probe V7: Runtime Idx Static Fn Return", probe_v7_runtime_idx_static_fn_return)
]
