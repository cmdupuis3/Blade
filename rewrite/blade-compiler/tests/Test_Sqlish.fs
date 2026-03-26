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
// Phase 4: group_by — Record Grouping
// ============================================================================

let test_groupby_sum = """
// SQL: SELECT region, SUM(temp) FROM stations GROUP BY region
type RegionIdx = Idx<3>
type StationIdx = Idx<6>

struct StationData {
    temp: Array<Float like StationIdx>,
    region: Array<RegionIdx like StationIdx>
}

let data = StationData {
    temp = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0],
    region = [0, 1, 2, 0, 1, 2]
}

let grouped = group_by(data, data.region)

let result = method_for(grouped) <@> lambda(group: StationData) -> {
    reduce((+), group.temp)
}
|> compute
// Region 0: 20+22=42, Region 1: 25+27=52, Region 2: 30+32=62
// EXPECT: result = [42.0, 52.0, 62.0]
"""

let test_groupby_count = """
// SQL: SELECT region, COUNT(*) FROM stations GROUP BY region
type RegionIdx = Idx<3>
type StationIdx = Idx<6>

struct StationData {
    temp: Array<Float like StationIdx>,
    region: Array<RegionIdx like StationIdx>
}

let data = StationData {
    temp = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0],
    region = [0, 1, 2, 0, 1, 2]
}

let grouped = group_by(data, data.region)

let result = method_for(grouped) <@> lambda(group: StationData) -> {
    extent(group.temp)
}
|> compute
// Each region has 2 stations
// EXPECT: result = [2, 2, 2]
"""

let test_groupby_mean = """
// SQL: SELECT region, AVG(temp) FROM stations GROUP BY region
type RegionIdx = Idx<3>
type StationIdx = Idx<6>

struct StationData {
    temp: Array<Float like StationIdx>,
    region: Array<RegionIdx like StationIdx>
}

let data = StationData {
    temp = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0],
    region = [0, 1, 2, 0, 1, 2]
}

let grouped = group_by(data, data.region)

let result = method_for(grouped) <@> lambda(group: StationData) -> {
    reduce((+), group.temp) / extent(group.temp)
}
|> compute
// Region 0: 42/2=21, Region 1: 52/2=26, Region 2: 62/2=31
// EXPECT: result = [21.0, 26.0, 31.0]
"""

let test_groupby_having = """
// SQL: SELECT region, AVG(temp) FROM stations GROUP BY region HAVING AVG(temp) > 25
type RegionIdx = Idx<3>
type StationIdx = Idx<6>

struct StationData {
    temp: Array<Float like StationIdx>,
    region: Array<RegionIdx like StationIdx>
}

let data = StationData {
    temp = [20.0, 25.0, 30.0, 22.0, 27.0, 32.0],
    region = [0, 1, 2, 0, 1, 2]
}

let grouped = group_by(data, data.region)

let avgs = method_for(grouped) <@> lambda(group: StationData) -> {
    reduce((+), group.temp) / extent(group.temp)
}
|> compute

let hot_regions = mask(avgs, lambda(avg) -> avg > 25.0)
let result = method_for(hot_regions) <@> lambda(avg) -> avg |> compute
// Region 1: 26, Region 2: 31 (Region 0: 21 filtered out)
// EXPECT: result = [26.0, 31.0]
"""

// ============================================================================
// Phase 5: sort — Sorted Iteration
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
// Combined: SQL-like queries using multiple features
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
    ("GroupBy Sum", test_groupby_sum)
    ("GroupBy Count", test_groupby_count)
    ("GroupBy Mean", test_groupby_mean)
    ("GroupBy Having", test_groupby_having)
]

/// Phase 5: sort
let sortTests = [
    ("Sort Ascending", test_sort_ascending)
    ("Sort Descending", test_sort_descending)
]

/// Combined
let sqlCombinedTests = [
    ("SQL Select-Where-OrderBy", test_sql_select_where_orderby)
]

/// All SQL-ish tests
let sqlishTests =
    foreignKeyTests @ maskTests @ setOpTests @ groupByTests @ sortTests @ sqlCombinedTests
