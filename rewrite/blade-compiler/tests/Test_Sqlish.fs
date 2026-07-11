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
// mask constructs the Bool PRESENCE array over A's own index space;
// compound(A, m) compactifies. Reads are COORDINATE-based (positions in
// the original space), so the selection stays composable with companion
// columns over the same index space.
let A = [3.0, -1.0, 4.0, -2.0, 5.0]
let m = mask(A, lambda(x) -> x > 0.0)
let pos = compound(A, m)
let n = extents(pos)
let x0 = pos(0)
let x2 = pos(2)
let x4 = pos(4)
// EXPECT: n = 3
// EXPECT: x0 = 3
// EXPECT: x2 = 4
// EXPECT: x4 = 5
"""

let test_mask_all_pass = """
// All elements pass: cardinality equals the source extent.
let A = [1.0, 2.0, 3.0]
let all = compound(A, mask(A, lambda(x) -> x > 0.0))
let n = extents(all)
let s = reduce(all, (+))
// EXPECT: n = 3
// EXPECT: s = 6
"""

let test_mask_none_pass = """
// No elements pass — cardinality 0 (empty filtered set).
let A = [1.0, 2.0, 3.0]
let none = compound(A, mask(A, lambda(x) -> x > 100.0))
let n = extents(none)
// EXPECT: n = 0
"""

let test_mask_with_compute = """
// Mask over a COMPUTED array: the mask reuses the computed array's own
// index records, so compound() matches by identity even though the
// computed output's indices are anonymous.
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let doubled = method_for(A) <@> lambda(a) -> a * 2.0 |> compute
let big = compound(doubled, mask(doubled, lambda(x) -> x > 5.0))
// doubled = [2, 4, 6, 8, 10]; > 5 at coordinates {2, 3, 4}
let n = extents(big)
let x2 = big(2)
let x4 = big(4)
// EXPECT: n = 3
// EXPECT: x2 = 6
// EXPECT: x4 = 10
"""

let test_mask_composition = """
// Predicate conjunction inside one mask (WHERE p AND q as one pass). The
// POSITIONAL two-mask composition (elementwise Bool AND of two mask
// arrays) is exercised separately in SQL WHERE AND.
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]
let band = compound(A, mask(A, lambda(x) -> x > 3.0 && x < 8.0))
let n = extents(band)
let x3 = band(3)
let x6 = band(6)
// EXPECT: n = 4
// EXPECT: x3 = 4
// EXPECT: x6 = 7
"""

// ============================================================================
// Phase 2b: SQL WHERE equivalent
// ============================================================================

let test_sql_where = """
// SQL: SELECT temp FROM stations WHERE temp > 25.0
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]

let m = mask(temps, lambda(t) -> t > 25.0)
let hot = compound(temps, m)
// present coordinates {2, 4, 5} = 30, 27, 32
let n = extents(hot)
let total = reduce(hot, (+))
let t2 = hot(2)
// EXPECT: n = 3
// EXPECT: total = 89
// EXPECT: t2 = 30
"""

// ============================================================================
// Phase 3: intersect / union — Mask Combinators
// ============================================================================

let test_intersect_basic = """
// Positional two-mask AND, post-redesign: mask now returns a Bool presence
// array (not filtered values), so "intersection of two filters over the same
// source" is the elementwise Bool AND of the two masks, then compactify. This
// mirrors SQL WHERE AND; value-based intersect() is covered by the dedup tests.
type AB = Idx<5>
let A: Array<Float64 like AB> = [1.0, 2.0, 3.0, 4.0, 5.0]
let above2 = mask(A, lambda(x) -> x > 2.0)
let below5 = mask(A, lambda(x) -> x < 5.0)
let both = method_for(zip(above2, below5)) <@> lambda(a, b) -> a && b |> compute
let f = compound(A, both)
// AND = [F,F,T,T,F] -> coordinates {2, 3} = 3.0, 4.0
let n = extents(f)
let x2 = f(2)
let x3 = f(3)
// EXPECT: n = 2
// EXPECT: x2 = 3.0
// EXPECT: x3 = 4.0
"""

let test_union_basic = """
// Positional two-mask OR: elementwise Bool OR of two presence masks over the
// same named index space, then compactify (union of two filters over one source).
type AB = Idx<5>
let A: Array<Float64 like AB> = [1.0, 2.0, 3.0, 4.0, 5.0]
let low = mask(A, lambda(x) -> x < 2.0)
let high = mask(A, lambda(x) -> x > 4.0)
let either = method_for(zip(low, high)) <@> lambda(a, b) -> a || b |> compute
let f = compound(A, either)
// OR = [T,F,F,F,T] -> coordinates {0, 4} = 1.0, 5.0
let n = extents(f)
let x0 = f(0)
let x4 = f(4)
// EXPECT: n = 2
// EXPECT: x0 = 1.0
// EXPECT: x4 = 5.0
"""

let test_intersect_disjoint = """
// Disjoint filters: elementwise AND of two masks that never overlap -> empty
// presence array -> zero-cardinality compound.
type AB = Idx<5>
let A: Array<Float64 like AB> = [1.0, 2.0, 3.0, 4.0, 5.0]
let low = mask(A, lambda(x) -> x < 3.0)
let high = mask(A, lambda(x) -> x > 3.0)
let none = method_for(zip(low, high)) <@> lambda(a, b) -> a && b |> compute
let f = compound(A, none)
// AND = [F,F,F,F,F] -> empty
let n = extents(f)
// EXPECT: n = 0
"""

let test_intersect_dedups_a = """
// SQL set semantics: duplicates in A are dropped; first occurrence
// wins for ordering. A has 3 twice and 5 once; B has 3 and 5.
// Result: [3, 5] — not [3, 3, 5].
let A = [3, 3, 5, 3]
let B = [3, 5, 7]
let r = intersect(A, B)
let result = method_for(r) <@> lambda(x) -> x |> compute
// EXPECT: result = [3, 5]
"""

let test_intersect_dedups_b_irrelevant = """
// Duplicates in B don't affect the result. B's role is membership only;
// B = [5, 5, 7, 7, 5] tests the same set as B = [5, 7]. A is already
// unique so output order is direct.
let A = [3, 5, 7]
let B = [5, 5, 7, 7, 5]
let r = intersect(A, B)
let result = method_for(r) <@> lambda(x) -> x |> compute
// EXPECT: result = [5, 7]
"""

let test_union_dedups_both = """
// SQL set semantics: duplicates within A AND within B are dropped.
// Ordering: first occurrences in A precede first occurrences in B.
// A = [1, 2, 1, 3] has unique values {1, 2, 3} in order [1, 2, 3].
// B = [3, 4, 4] adds {4} (3 is already seen).
// Result: [1, 2, 3, 4].
let A = [1, 2, 1, 3]
let B = [3, 4, 4]
let r = union(A, B)
let result = method_for(r) <@> lambda(x) -> x |> compute
// EXPECT: result = [1, 2, 3, 4]
"""

let test_union_a_subsumes_b = """
// When all of B is already in A, union equals dedup(A). Tests that the
// B-loop's seen-check correctly suppresses everything.
let A = [1, 2, 3, 2, 1]
let B = [1, 3]
let r = union(A, B)
let result = method_for(r) <@> lambda(x) -> x |> compute
// EXPECT: result = [1, 2, 3]
"""

let test_sql_where_and = """
// SQL: SELECT SUM(temp) FROM stations WHERE temp > 20.0 AND temp < 30.0
// POSITIONAL composition: two mask arrays over the same (named) index
// space, combined by elementwise Bool AND, then compactified. This is
// what the value-based intersect could not express (it operates on
// element VALUES, which coincide across positions).
type SIdx = Idx<6>
let temps: Array<Float64 like SIdx> = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]

let above20 = mask(temps, lambda(t) -> t > 20.0)
let below30 = mask(temps, lambda(t) -> t < 30.0)
let both = method_for(zip(above20, below30)) <@> lambda(a, b) -> a && b |> compute
let f = compound(temps, both)
// AND = [F,T,F,T,T,F] -> coordinates {1, 3, 4} = 25, 22, 27
let n = extents(f)
let total = reduce(f, (+))
// EXPECT: n = 3
// EXPECT: total = 74
"""

// ============================================================================
// Phase 3.5: unique — first-occurrence deduplication
// ============================================================================

let test_unique_basic = """
// unique drops repeated elements, keeping the first occurrence of each.
let A = [3, 1, 4, 1, 5, 9, 2, 6, 5, 3, 5]
let u = unique(A)
let result = method_for(u) <@> lambda(x) -> x |> compute
// EXPECT: result = [3, 1, 4, 5, 9, 2, 6]
"""

let test_unique_no_dupes = """
// Already-unique input passes through unchanged.
let A = [10, 20, 30, 40, 50]
let u = unique(A)
let result = method_for(u) <@> lambda(x) -> x |> compute
// EXPECT: result = [10, 20, 30, 40, 50]
"""

let test_unique_float = """
// unique on Float64 — same machinery, std::unordered_set handles the hash.
let A = [1.5, 2.5, 1.5, 3.5, 2.5]
let u = unique(A)
let result = method_for(u) <@> lambda(x) -> x |> compute
// EXPECT: result = [1.5, 2.5, 3.5]
"""

// ============================================================================
// Phase 3.5: contains — membership test (linear scan)
// ============================================================================

let test_contains_present = """
// Element present in array — returns true.
let A = [10, 20, 30, 40, 50]
let result = contains(A, 30)
// EXPECT: result = true
"""

let test_contains_absent = """
// Element not in array — returns false.
let A = [10, 20, 30, 40, 50]
let result = contains(A, 99)
// EXPECT: result = false
"""

let test_contains_empty_after_mask = """
// contains on an EMPTY filtered set (cardinality-0 compound) — returns
// false without aborting.
let A = [1, 2, 3]
let empty_set = compound(A, mask(A, lambda(x) -> x > 100))
let result = contains(empty_set, 5)
// EXPECT: result = false
"""

let test_contains_in_mask_predicate = """
// contains used inside a mask predicate — the canonical semijoin idiom.
// After the IR pattern matcher runs, this lowers to IRSemijoin(A, B):
// codegen pre-builds an unordered_set from B and probes A once, dropping
// the inner cost from O(|A|·|B|) to O(|A|+|B|). The result is identical
// to the unfused form — this test guards correctness across the rewrite.
let A = [1, 2, 3, 4, 5, 6]
let B = [2, 4, 6, 8]
let in_B = compound(A, mask(A, lambda(x) -> contains(B, x)))
// coordinates {1, 3, 5} = 2, 4, 6
let n = extents(in_B)
let x1 = in_B(1)
let x5 = in_B(5)
// EXPECT: n = 3
// EXPECT: x1 = 2
// EXPECT: x5 = 6
"""

// ============================================================================
// Phase 3.6: semijoin / antijoin pattern matcher tests
// ============================================================================
// These exercise the IR rewrite for mask(A, contains(B, x)) → IRSemijoin
// and mask(A, !contains(B, x)) → IRAntijoin. Correctness is checked by
// value comparison; the algorithmic improvement (O(|A|·|B|) → O(|A|+|B|))
// is implicit but not measured here.

let test_semijoin_preserves_multiplicity = """
// Semijoin is NOT a set operation: it preserves A's multiplicity.
// A has 3 twice and 1 twice; both copies of each should appear in the
// output as long as they're members of B. This distinguishes semijoin
// from intersect (which would dedup to [3, 1]).
let A = [3, 1, 3, 2, 1, 4]
let B = [1, 3]
let r = compound(A, mask(A, lambda(x) -> contains(B, x)))
// present coordinates {0, 1, 2, 4}: both 3s and both 1s survive
let n = extents(r)
let x0 = r(0)
let x2 = r(2)
let x4 = r(4)
// EXPECT: n = 4
// EXPECT: x0 = 3
// EXPECT: x2 = 3
// EXPECT: x4 = 1
"""

let test_antijoin_basic = """
// Antijoin: elements of A NOT in B. Pattern is mask(A, !contains(B, x)).
let A = [1, 2, 3, 4, 5, 6, 7]
let B = [2, 4, 6]
let r = compound(A, mask(A, lambda(x) -> !contains(B, x)))
// present coordinates {0, 2, 4, 6} = 1, 3, 5, 7
let n = extents(r)
let x0 = r(0)
let x6 = r(6)
// EXPECT: n = 4
// EXPECT: x0 = 1
// EXPECT: x6 = 7
"""

let test_antijoin_preserves_multiplicity = """
// Antijoin also preserves A's multiplicity. A has duplicates; the
// duplicates that aren't in B all appear in the output.
let A = [3, 1, 3, 2, 1, 4, 2]
let B = [1, 3]
let r = compound(A, mask(A, lambda(x) -> !contains(B, x)))
// present coordinates {3, 5, 6} = 2, 4, 2 (multiplicity preserved)
let n = extents(r)
let x3 = r(3)
let x6 = r(6)
// EXPECT: n = 3
// EXPECT: x3 = 2
// EXPECT: x6 = 2
"""

let test_pattern_does_not_fire_on_conjunction = """
// Conjunction predicate (contains AND a scalar condition). The LOCAL
// set-hoist substitutes the contains node in place, so conjunctions,
// negations, and multi-contains predicates all fuse now — the old
// global rewriter had to decline these. Correctness is what this
// test guards; the hoist firing more often must not change values.
let A = [1, 2, 3, 4, 5, 6, 7, 8]
let B = [2, 4, 6, 8]
let r = compound(A, mask(A, lambda(x) -> contains(B, x) && x > 3))
// coordinates {3, 5, 7} = 4, 6, 8
let n = extents(r)
let x3 = r(3)
let x7 = r(7)
// EXPECT: n = 3
// EXPECT: x3 = 4
// EXPECT: x7 = 8
"""

let test_semijoin_float64 = """
// Semijoin on Float64 — exercises std::hash<double> through the
// pattern-matched path (mirrors Unique Float on the unique() side).
let A = [1.5, 2.5, 3.5, 4.5]
let B = [2.5, 4.5, 6.5]
let r = compound(A, mask(A, lambda(x) -> contains(B, x)))
// coordinates {1, 3} = 2.5, 4.5
let n = extents(r)
let x1 = r(1)
let x3 = r(3)
// EXPECT: n = 2
// EXPECT: x1 = 2.5
// EXPECT: x3 = 4.5
"""

let test_unique_of_semijoin = """
// Distinct members of A that are in B, post-redesign. mask now returns a Bool
// presence array (not packed values), so "unique of the semijoin" is expressed
// as: take the distinct values of A, then keep those present in B. For the set
// of distinct passing values, unique-then-filter == filter-then-unique, and
// both preserve A's first-occurrence order.
let A = [3, 1, 3, 2, 1, 4]
let B = [1, 3]
let uniqA = unique(A)
let inB = mask(uniqA, lambda(x) -> contains(B, x))
let f = compound(uniqA, inB)
// unique(A) = [3, 1, 2, 4]; in B = [T, T, F, F] -> coordinates {0, 1} = 3, 1
let n = extents(f)
let x0 = f(0)
let x1 = f(1)
// EXPECT: n = 2
// EXPECT: x0 = 3
// EXPECT: x1 = 1
"""

let test_semijoin_then_mask = """
// Composition, post-redesign: "members of A in B, then those > 2". mask now
// returns Bool presence arrays, so this is the elementwise AND of the semijoin
// mask and the comparison mask over A's shared named index, then compactify.
type AB = Idx<8>
let A: Array<Int64 like AB> = [1, 2, 3, 4, 5, 6, 7, 8]
let B = [2, 3, 5, 7]
let inB = mask(A, lambda(x) -> contains(B, x))
let gt2 = mask(A, lambda(x) -> x > 2)
let both = inB && gt2
let f = compound(A, both)
// inB = [F,T,T,F,T,F,T,F], gt2 = [F,F,T,T,T,T,T,T], AND = [F,F,T,F,T,F,T,F]
// -> coordinates {2, 4, 6} = 3, 5, 7
let n = extents(f)
let x2 = f(2)
let x4 = f(4)
let x6 = f(6)
// EXPECT: n = 3
// EXPECT: x2 = 3
// EXPECT: x4 = 5
// EXPECT: x6 = 7
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

let test_groupby_enum_string = """
// String-valued EnumIdx — same shape as test_groupby_enum_first but the keys
// are strings. Exercises Layer 2 of string support: the `using LandType`
// alias resolves to std::string, the keys array stores std::string elements,
// and the Case 2 reverse-lookup dispatch generates `__v == "forest"`-style
// comparisons (efficient because std::string::operator== accepts const char*
// without allocating a temporary).
type LandType = EnumIdx<["forest", "urban", "farmland"]>
type StationIdx = Idx<6>
let codes: Array<LandType like StationIdx> = ["forest", "urban", "farmland", "forest", "urban", "farmland"]
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

let test_groupby_sparse_keys_dynamic = """
// Sparse keys without annotation — Case 3 hash-based dispatch. The keys
// 101, 205, 307 occupy a sparse range; an earlier max-scan implementation
// would have allocated 308 buckets (almost all empty). Hash discovery
// produces 3 buckets in first-occurrence order.
let codes = [101, 205, 307, 101, 205, 307]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(codes)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) |> compute
// EXPECT: result = [20.0, 25.0, 30.0]
"""

let test_groupby_sparse_keys_reduce = """
// Per-group sum on sparse keys without annotation. Verifies the hash-
// based ngroups, offsets, and permutation correctly partition values
// for reduction.
let codes = [101, 205, 307, 101, 205, 307]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(codes)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: result = [42, 52, 62]
"""

// ============================================================================
// Phase 4 follow-on: additional coverage for gaps surfaced during recon
// ============================================================================

let test_groupby_enum_string_reduce = """
// String-EnumIdx + reduce per group. Exercises Case 2 (EnumIdx reverse
// lookup) with std::string keys, then peels each group and reduces.
// The reduce kernel sees a Ragged sub-array; the per-group lengths come
// from the gk's offsets.
type LandType = EnumIdx<["forest", "urban", "farmland"]>
type StationIdx = Idx<6>
let codes: Array<LandType like StationIdx> = ["forest", "urban", "farmland", "forest", "urban", "farmland"]
let temps: Array<Float64 like StationIdx> = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let gk = group_keys(codes)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: result = [42, 52, 62]
"""

let test_groupby_single_group = """
// All keys map to a single bucket. Exercises the degenerate-ngroups
// case: one group containing all elements. ngroups discovered dynamically
// as 1; the result is a rank-2 ragged array with outer extent 1 and
// inner extent equal to the input length.
let region = [0, 0, 0, 0]
let temps = [1.0, 2.0, 3.0, 4.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: result = [10]
"""

let test_groupby_after_method_for = """
// group_by consuming the output of another combinator. method_for + compute
// materializes a derived array; that array then flows into group_by as the
// values input. Exercises the chain
//   method_for(temps) |> compute  →  group_by(_, gk)  →  method_for(grouped)
// confirming that group_by accepts the materialized output of an upstream
// combinator as cleanly as a literal input array would.
let region = [0, 1, 2, 0, 1, 2]
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let doubled = method_for(temps) <@> lambda(t) -> t * 2.0 |> compute
let gk = group_keys(region)
let grouped = group_by(doubled, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) |> compute
// EXPECT: result = [40, 50, 60]
"""

let test_groupby_compound_two_keys_first = """
// Compound (multi-key) group_keys: distinct (region, year) tuples each
// become their own bucket. First-occurrence ordering: bucket index = the
// order in which a unique tuple first appears walking left-to-right.
//
// region+year first-occurrence in this input:
//   (0, 2020) at i=0 → bucket 0, repeated at i=4
//   (1, 2020) at i=1 → bucket 1, repeated at i=5
//   (0, 2021) at i=2 → bucket 2
//   (1, 2021) at i=3 → bucket 3
// Each bucket's first element comes from the first input index assigned to it.
let region = [0, 1, 0, 1, 0, 1]
let year =   [2020, 2020, 2021, 2021, 2020, 2020]
let temps =  [10.0, 11.0, 20.0, 21.0, 12.0, 13.0]
let gk = group_keys(region, year)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g) -> g(0) |> compute
// EXPECT: result = [10, 11, 20, 21]
"""

let test_groupby_compound_two_keys_reduce = """
// Compound group_keys + reduce per group. Same tuple structure as the
// _first variant, but each bucket sums its members.
//   bucket 0: indices [0, 4] → temps [10.0, 12.0] → sum 22.0
//   bucket 1: indices [1, 5] → temps [11.0, 13.0] → sum 24.0
//   bucket 2: indices [2]    → temps [20.0]       → sum 20.0
//   bucket 3: indices [3]    → temps [21.0]       → sum 21.0
let region = [0, 1, 0, 1, 0, 1]
let year =   [2020, 2020, 2021, 2021, 2020, 2020]
let temps =  [10.0, 11.0, 20.0, 21.0, 12.0, 13.0]
let gk = group_keys(region, year)
let grouped = group_by(temps, gk)
let result = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
// EXPECT: result = [22, 24, 20, 21]
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

let hot = compound(temps, mask(temps, lambda(t) -> t > 25.0))
let sorted = sort(hot, lambda(t) -> -t)
// sort over a rank-1 compound walks the compact buffer; the DENSE output
// is semantically honest (sorting discards coordinate meaning).
// EXPECT: sorted = [32, 30, 27]
"""

// ============================================================================
// v24d-1 fallback regression guards. These four cases originally exercised
// the type-recovery pathways that two CodeGen fallbacks defended against:
//   - The IRFieldAccess scan fallback (CodeGen.fs:260) that searched all
//     structs by field name when obj's type wasn't IRTNamed
//   - The auto-fallback (CodeGen.fs:2208) that emitted `auto x = expr` for
//     shape-bearing IRTUnit bindings
//
// Both fallbacks were removed after instrumentation showed neither fired
// across the full test corpus including these four probes. They remain
// here as regression guards — if a future change to typechecking or
// lowering ever leaks IRTUnit / non-IRTNamed types through to codegen for
// these patterns, these probes will fail and surface the regression
// instead of the codegen silently recovering.
// ============================================================================

let test_v24d_probe_1_poly_struct_field = """
// Probe 1: field access on a struct returned from a function call. Tests
// that the function-return path preserves struct identity (IRTNamed)
// through to the field access. Originally framed around polymorphism, but
// Blade's Poly<T^N> system is for arity polymorphism, not type generics —
// the function-call indirection alone is the relevant test surface here.
struct PointType {
    x: Float64,
    y: Float64
}
function ident(p: PointType) -> PointType = p
let p = ident(PointType { x = 3.0, y = 4.0 })
let r = p.x
// EXPECT: r = 3
"""

let test_v24d_probe_2_mask_then_field = """
// Probe 2: chained mask -> indexing -> field access on a struct array. The
// mask operation might lose struct identity in its result type; subsequent
// field access on the indexed element could land in the IRFieldAccess
// scan fallback (now removed).
struct Pair {
    a: Float64,
    b: Float64
}
let pairs: Array<Pair like Idx<3>> = [
    Pair { a = 1.0, b = 10.0 },
    Pair { a = 2.0, b = 20.0 },
    Pair { a = 3.0, b = 30.0 }
]
let f = compound(pairs, mask(pairs, lambda(p) -> p.a > 1.5))
let elem = f(1)
let r = elem.a
// EXPECT: r = 2
"""

let test_v24d_probe_3_unannotated_mask = """
// Probe 3: unannotated binding whose RHS is a mask result. If the
// mask-returning expression's type doesn't flow back to the binding but
// the codegen sees a wrapper-shaped RHS, the auto-fallback (now removed)
// would have fired.
let arr: Array<Float64 like Idx<4>> = [1.0, 2.0, 3.0, 4.0]
let f = compound(arr, mask(arr, lambda(x) -> x > 1.5))
let r = f(1)
// EXPECT: r = 2
"""

let test_v24d_probe_4_unannotated_sort = """
// Probe 4: unannotated binding whose RHS is a sort result. Same shape as
// probe 3 but exercising the IRSort path through producesWrapperShape.
// `sort` takes a key-extractor lambda (returns the sort key for each
// element), not a comparator.
let arr: Array<Float64 like Idx<4>> = [3.0, 1.0, 4.0, 2.0]
let s = sort(arr, lambda(x) -> x)
let r = s(0)
// EXPECT: r = 1
"""

// ============================================================================
// Test Lists
// ============================================================================

/// Phase 1: Foreign key arrays, ETIndexRef, array captures
// ---------------------------------------------------------------------------
// group_by x standard-array intersection coverage: the grouped result is a
// first-class ragged value, so plain array reads and downstream reductions
// should work on it without going through a kernel. Fixture (uneven groups,
// first-seen key order): region [0,1,0,0,1], temps [10,20,30,40,50] ->
// group 0 = [10,30,40], group 1 = [20,50].
// ---------------------------------------------------------------------------

// Direct element reads off the grouped value at binding level -- both the
// curried grouped(i)(j) and tuple-arg grouped(i, j) spellings. Existing
// group_by tests consume groups only through method_for kernels; this pins
// the grouped result as an ordinarily-indexable rank-2 value.
let test_groupby_direct_reads = """
let region = [0, 1, 0, 0, 1]
let temps = [10.0, 20.0, 30.0, 40.0, 50.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let a = grouped(0)(1)
let b = grouped(1, 0)
let c = grouped(0)(2)
// EXPECT: a = 30
// EXPECT: b = 20
// EXPECT: c = 40
"""

// Group SIZES via extents(g) with UNEVEN groups. The existing extents test
// runs over a ragged literal (lens from the literal's own companions); here
// the row length comes from the group_keys offsets table at the peel point,
// and unequal groups distinguish the per-group length from any constant.
let test_groupby_sizes_uneven = """
let region = [0, 1, 0, 0, 1]
let temps = [10.0, 20.0, 30.0, 40.0, 50.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let sizes = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> extents(g) |> compute
// EXPECT: sizes = [3, 2]
"""

// The canonical SQL aggregate composed end-to-end in Blade terms: per-group
// reduction (a ragged peel producing a dense rank-1 of group sums), then a
// plain dense reduction over that for the grand total -- SUM(x) GROUP BY k
// followed by SUM over groups. Exercises the dense-array downstream of the
// SQL-side output with a dynamic (ngroups) extent.
let test_groupby_grand_total = """
let region = [0, 1, 0, 0, 1]
let temps = [10.0, 20.0, 30.0, 40.0, 50.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let sums = method_for(grouped) <@> lambda(g: Array<Float64 like RaggedIdx<_>>) -> reduce(g, (+)) |> compute
let grand = reduce(sums, (+))
// EXPECT: sums = [80, 70]
// EXPECT: grand = 150
"""

// A let-bound GROUP row carries its length: `let g0 = grouped(i)` binds a
// RaggedRow whose len comes from the group_keys offsets table (the grouped
// value itself has no lens member). Print, reduce, and element reads all
// work on the binding.
let test_groupby_row_subview = """
let region = [0, 1, 0, 0, 1]
let temps = [10.0, 20.0, 30.0, 40.0, 50.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let g0 = grouped(0)
let s0 = reduce(g0, (+))
let v = g0(2)
// EXPECT: g0 = [10, 30, 40]
// EXPECT: s0 = 80
// EXPECT: v = 40
"""

// Elementwise map over a group_by result is gated (the grouped value lacks
// the wrapper metadata the shape-preserving map shares, and group-shaped
// outputs have no downstream consumers yet). Mapping BEFORE grouping is
// semantically equivalent and supported.
let test_groupby_elementwise_reject = """
let region = [0, 1, 0]
let temps = [10.0, 20.0, 30.0]
let gk = group_keys(region)
let grouped = group_by(temps, gk)
let d = method_for(grouped) <@> lambda(e) -> e * 2.0 |> compute
// EXPECT: typecheck failure — elementwise map over a group_by result is gated; map before grouping.
"""


let test_sql_sum_where = """
// SQL: SELECT SUM(temp), COUNT(*) FROM stations WHERE temp > 25.0
// The whole aggregate-over-filtered pattern in two moves: mask constructs
// the selection, compound applies it, reduce consumes the compact buffer.
let temps = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0]
let hot = compound(temps, mask(temps, lambda(t) -> t > 25.0))
let total = reduce(hot, (+))
let n = extents(hot)
// EXPECT: total = 89
// EXPECT: n = 3
"""

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
    ("Intersect Dedups A", test_intersect_dedups_a)
    ("Intersect Dedups B Irrelevant", test_intersect_dedups_b_irrelevant)
    ("Union Dedups Both", test_union_dedups_both)
    ("Union A Subsumes B", test_union_a_subsumes_b)
]

/// Phase 3.5: unique / contains — value-set primitives
let uniqueContainsTests = [
    ("Unique Basic", test_unique_basic)
    ("Unique No Duplicates", test_unique_no_dupes)
    ("Unique Float", test_unique_float)
    ("Contains Present", test_contains_present)
    ("Contains Absent", test_contains_absent)
    ("Contains Empty After Mask", test_contains_empty_after_mask)
    ("Contains In Mask Predicate", test_contains_in_mask_predicate)
]

/// Phase 3.6: semijoin / antijoin pattern matcher
let semijoinTests = [
    ("Semijoin Preserves Multiplicity", test_semijoin_preserves_multiplicity)
    ("Antijoin Basic", test_antijoin_basic)
    ("Antijoin Preserves Multiplicity", test_antijoin_preserves_multiplicity)
    ("Pattern Does Not Fire On Conjunction", test_pattern_does_not_fire_on_conjunction)
    ("Semijoin Float64", test_semijoin_float64)
    ("Unique Of Semijoin", test_unique_of_semijoin)
    ("Semijoin Then Mask", test_semijoin_then_mask)
]

/// Phase 4: group_by
let groupByTests = [
    ("GroupBy Idx First", test_groupby_idx_first)
    ("GroupBy Idx Sum Two", test_groupby_idx_sum_two)
    ("GroupBy Idx Annotated", test_groupby_idx_annotated)
    ("GroupBy Enum First", test_groupby_enum_first)
    ("GroupBy Enum String", test_groupby_enum_string)
    ("GroupBy Reduce Per Group", test_groupby_reduce_per_group)
    ("GroupBy Reduce Unannotated", test_groupby_reduce_unannotated)
    ("GroupBy Mixed Kernel", test_groupby_mixed_kernel)
    ("GroupBy Sparse Keys Dynamic", test_groupby_sparse_keys_dynamic)
    ("GroupBy Sparse Keys Reduce", test_groupby_sparse_keys_reduce)
    ("GroupBy Enum String Reduce", test_groupby_enum_string_reduce)
    ("GroupBy Single Group", test_groupby_single_group)
    ("GroupBy After Method For", test_groupby_after_method_for)
    ("GroupBy Compound Two Keys First", test_groupby_compound_two_keys_first)
    ("GroupBy Compound Two Keys Reduce", test_groupby_compound_two_keys_reduce)
    ("GroupBy Direct Reads", test_groupby_direct_reads)
    ("GroupBy Sizes Uneven", test_groupby_sizes_uneven)
    ("GroupBy Grand Total", test_groupby_grand_total)
    ("GroupBy Row Subview", test_groupby_row_subview)
    ("GroupBy Elementwise (rejects)", test_groupby_elementwise_reject)
    ("SQL Sum Where", test_sql_sum_where)
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
// extents() on a compacted mask (compound). mask now returns a Bool presence
// array over xs's own index; compound() gathers the passing cells, and
// extents() of a rank-1 compound is its cardinality (the count that passed).
let xs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
let evens = mask(xs, lambda(x) -> x % 2 == 0)
let ev = compound(xs, evens)
let n = extents(ev)
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
// reduce over a compacted mask (compound). mask returns a Bool presence array;
// compound() gathers the passing VALUES, and reduce sums them. The dynamic
// extent still exercises the runtime non-emptiness guard.
let xs = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
let evens = mask(xs, lambda(x) -> x > 2.0)
let ev = compound(xs, evens)
let total = reduce(ev, (+))
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

/// Type-recovery regression guards — exercise pathways that two removed
/// CodeGen fallbacks once defended (IRFieldAccess scan + auto-fallback for
/// shape-bearing IRTUnit bindings). Should continue to typecheck and produce
/// verifiable values. If any start failing, the type pipeline has regressed.
let v24dProbes = [
    ("Type Recovery: Poly Struct Field", test_v24d_probe_1_poly_struct_field)
    ("Type Recovery: Mask Then Field", test_v24d_probe_2_mask_then_field)
    ("Type Recovery: Unannotated Mask", test_v24d_probe_3_unannotated_mask)
    ("Type Recovery: Unannotated Sort", test_v24d_probe_4_unannotated_sort)
]

/// All SQL-ish tests
let sqlishTests =
    foreignKeyTests @ maskTests @ setOpTests @ uniqueContainsTests @ semijoinTests @ groupByTests @ sortTests @ reduceTests @ extentsTests @ extentsMultiRankTests @ regressionTests @ sqlCombinedTests @ v24dProbes
