module Blade.Tests.Guards

let test55_guardWithAnd = """
// Guard with && operator
let x = 15
let result = match x with
    | n if n > 10 && n < 20 -> n
    | _ -> 0
// EXPECT: result = 15
"""

let test56_guardWithOr = """
// Guard with || operator
let x = 5
let result = match x with
    | n if n < 0 || n > 100 -> 0
    | n -> n * 2
// EXPECT: result = 10
"""

let test57_guardComplex = """
// Complex guard with multiple conditions
let x = 50
let y = 30
let result = match (x, y) with
    | (a, b) if a > 0 && b > 0 && a + b < 100 -> a + b
    | (a, b) if a < 0 || b < 0 -> 0
    | _ -> 999
// EXPECT: result = 80
"""

let test58_guardNested = """
// Nested match with guards (multi-line match body requires braces)
let x = 10
let y = 20
let outer = match x with
    | n if n > 5 -> {
        match y with
        | m if m > 15 -> n + m
        | _ -> n
    }
    | _ -> 0
// EXPECT: outer = 30
"""

/// Extended guard tests
let guardTests = [
    ("Guard With &&", test55_guardWithAnd)
    ("Guard With ||", test56_guardWithOr)
    ("Guard Complex", test57_guardComplex)
    ("Guard Nested", test58_guardNested)
]

// ============================================================================
// Guard combinator tests: guard(cond, body)
// ============================================================================

let test120_guardScalarTrue = """
// guard(true, expr) = expr
let result = guard(true, 42)
// EXPECT: result = 42
"""

let test121_guardScalarFalse = """
// guard(false, expr) = 0
let result = guard(false, 42)
// EXPECT: result = 0
"""

let test122_guardVariable = """
// guard with variable condition
let x = 10
let cond = x > 5
let result = guard(cond, x * 2)
// EXPECT: result = 20
"""

let test123_guardVariableFalse = """
// guard with false variable condition
let x = 3
let cond = x > 5
let result = guard(cond, x * 2)
// EXPECT: result = 0
"""

let test124_guardComputation = """
// guard wrapping a computation — true case executes
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let cond = true
let result = guard(cond, L <@> f) |> compute
// EXPECT: result = [11.0, 12.0, 13.0]
"""

let test125_guardExpressionFalse = """
// guard(false, expr) in arithmetic context
let x = 10
let y = guard(x > 20, x * 3) + 5
// EXPECT: y = 5
"""

let test126_guardNested = """
// Nested guard: guard(p, guard(q, c)) = guard(p && q, c)
let p = true
let q = true
let result = guard(p, guard(q, 99))
// EXPECT: result = 99
"""

let test127_guardNestedFalse = """
// Nested guard with outer false
let p = false
let q = true
let result = guard(p, guard(q, 99))
// EXPECT: result = 0
"""

let test155_guardFalseComputation = """
// guard(false, L <@> f) |> compute — output should be zeroed.
// Baseline for test156: establishes that the IRGuard codegen wraps a
// non-Reynolds kernel with the predicate correctly.
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let cond = false
let result = guard(cond, L <@> f) |> compute
// EXPECT: result = [0.0, 0.0, 0.0]
"""

let test156_guardFalseReynolds = """
// guard(false, L <@> reynolds(g)) |> compute — output should be zeroed.
// Regression test for the IRGuard codegen path silently dropping the
// guard predicate when the kernel is Reynolds-wrapped (CodeGen.fs site
// resolveCallable-without-peelReynolds, fixed via mapKernelInner).
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> x * y
let cond = false
let result = guard(cond, L <@> reynolds(g)) |> compute
// Without the fix: triangular [2.0, 4.0, 6.0, 8.0, 12.0, 18.0]
//   (= 2 * x * y at each unordered (i, j) pair in {1, 2, 3}^2,
//    Reynolds doubles a commutative kernel).
// With the fix: guard fires, output zeroed.
// EXPECT: result = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0]
"""

let guardCombinatorTests = [
    ("Guard Scalar True", test120_guardScalarTrue)
    ("Guard Scalar False", test121_guardScalarFalse)
    ("Guard Variable", test122_guardVariable)
    ("Guard Variable False", test123_guardVariableFalse)
    ("Guard Computation", test124_guardComputation)
    ("Guard Expression False", test125_guardExpressionFalse)
    // "Guard Nested" → "Guard Nested True" to disambiguate from the match-form
    // "Guard Nested" in guardTests. The previous shared name caused both tests
    // to write to the same Guard_Nested.cpp file under parallel execution; the
    // winner was non-deterministic and one validation would fail.
    ("Guard Nested True", test126_guardNested)
    ("Guard Nested False", test127_guardNestedFalse)
    ("Guard False Computation", test155_guardFalseComputation)
    ("Guard False Reynolds", test156_guardFalseReynolds)
]

// ============================================================================
// Zero combinator tests
// ============================================================================

let test128_zeroScalar = """
// zero as a standalone scalar value
let result = zero
// EXPECT: result = 0
"""

let test129_zeroInArithmetic = """
// zero + value = value (additive identity)
let x = zero + 5
// EXPECT: x = 5
"""

let test130_zeroFloat = """
// zero in float context
let x = zero + 3.14
// EXPECT: x = 3.14
"""

let test131_zeroKernel = """
// M <@> zero produces array of zeros
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let result = L <@> zero |> compute
// EXPECT: result = [0.0, 0.0, 0.0]
"""

let test132_zeroKernelSymmetric = """
// method_for(A, A) <@> zero with symmetric arrays
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let result = L <@> zero |> compute
// EXPECT: result [symmetric]
"""

let test133_zeroObjectFor = """
// object_for(zero) <@> arrays
let A = [1.0, 2.0, 3.0]
let result = object_for(zero) <@> (A, A) |> compute
// EXPECT: result [symmetric]
"""

let test134_guardZero = """
// guard(false, c) should behave like zero
let x = guard(false, 42)
let y = zero
// EXPECT: x = 0
// EXPECT: y = 0
"""

let zeroCombinatorTests = [
    ("Zero Scalar", test128_zeroScalar)
    ("Zero Arithmetic", test129_zeroInArithmetic)
    ("Zero Float", test130_zeroFloat)
    ("Zero Kernel", test131_zeroKernel)
    ("Zero Kernel Symmetric", test132_zeroKernelSymmetric)
    ("Zero ObjectFor", test133_zeroObjectFor)
    ("Guard-Zero Equivalence", test134_guardZero)
]

// ============================================================================
// Sequence combinator tests
// ============================================================================

let test135_sequenceTwoComputations = """
// sequence(c1, c2) produces array indexed by Idx<2>
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let result = sequence(L <@> f, L <@> g) |> compute
// EXPECT: result = [11.0, 12.0, 13.0, 2.0, 4.0, 6.0]
"""

let test136_sequenceThreeComputations = """
// sequence(c1, c2, c3) — 3 homogeneous computations
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let result = sequence(L <@> f, L <@> g, L <@> h) |> compute
// EXPECT: result = [11.0, 12.0, 13.0, 2.0, 4.0, 6.0, 1.0, 4.0, 9.0]
"""

let test137_sequenceSingleton = """
// sequence(c) collapses to c (identity)
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 100.0
let result = sequence(L <@> f) |> compute
// EXPECT: result = [101.0, 102.0, 103.0]
"""

let test138_sequenceDeferred = """
// sequence bound to variable, then computed
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let s = sequence(L <@> f, L <@> g)
let result = s |> compute
// EXPECT: result = [11.0, 12.0, 13.0, 2.0, 4.0, 6.0]
"""

let sequenceCombinatorTests = [
    ("Sequence Two", test135_sequenceTwoComputations)
    ("Sequence Three", test136_sequenceThreeComputations)
    ("Sequence Singleton", test137_sequenceSingleton)
    ("Sequence Deferred", test138_sequenceDeferred)
]

// ============================================================================
// Tuple View Tests
// ============================================================================

let test139_tupleViewFlat3Way = """
// Flat view of 3-way parallel: (a, b, c) against ((α, β), γ)
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let (sums, prods, squares) = (L <@> f) <&> (L <@> g) <&> (L <@> h) |> compute
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test140_tupleViewStructural3Way = """
// Structural view of 3-way parallel: (inner, c) against ((α, β), γ)
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let (inner, squares) = (L <@> f) <&> (L <@> g) <&> (L <@> h) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test141_tupleViewFlat4Way = """
// Flat view of 4-way parallel: (a, b, c, d) against (((α, β), γ), δ)
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let r = (L <@> lambda(x) -> x + 1.0) <&> (L <@> lambda(x) -> x + 2.0) <&> (L <@> lambda(x) -> x + 3.0) <&> (L <@> lambda(x) -> x + 4.0) |> compute
let (a, b, c, d) = r
// EXPECT: a = [2.0, 3.0, 4.0]
// EXPECT: b = [3.0, 4.0, 5.0]
// EXPECT: c = [4.0, 5.0, 6.0]
// EXPECT: d = [5.0, 6.0, 7.0]
"""

let test142_tupleViewScalarFlat = """
// Flat view of scalar tuples
let x = (1.0, 2.0)
let y = (x, 3.0)
let (a, b, c) = y
// EXPECT: a = 1
// EXPECT: b = 2
// EXPECT: c = 3
"""

let test143_tupleViewScalarStructural = """
// Structural view of scalar tuples
let x = (1.0, 2.0)
let y = (x, 3.0)
let (inner, c) = y
let (a, b) = inner
// EXPECT: a = 1
// EXPECT: b = 2
// EXPECT: c = 3
"""

let tupleViewTests = [
    ("Tuple View Flat 3-Way", test139_tupleViewFlat3Way)
    ("Tuple View Structural 3-Way", test140_tupleViewStructural3Way)
    ("Tuple View Flat 4-Way", test141_tupleViewFlat4Way)
    ("Tuple View Scalar Flat", test142_tupleViewScalarFlat)
    ("Tuple View Scalar Structural", test143_tupleViewScalarStructural)
]

// ============================================================================
// Replicate Tests
// ============================================================================

let test144_replicateBasic = """
// replicate(3, c) produces array with 3 copies
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let c = L <@> lambda(x) -> x + 10.0
let result = replicate(3, c) |> compute
// EXPECT: result = [11.0, 12.0, 13.0, 11.0, 12.0, 13.0, 11.0, 12.0, 13.0]
"""

let test145_replicateTwo = """
// replicate(2, c) — same as sequence(c, c)
let A = [1.0, 2.0]
let L = method_for(A)
let result = replicate(2, L <@> lambda(x) -> x * 3.0) |> compute
// EXPECT: result = [3.0, 6.0, 3.0, 6.0]
"""

let test146_replicateSingleton = """
// replicate(1, c) collapses to identity
let A = [10.0, 20.0]
let L = method_for(A)
let result = replicate(1, L <@> lambda(x) -> x + 1.0) |> compute
// EXPECT: result = [11.0, 21.0]
"""

let test147_replicateDeferred = """
// replicate bound to variable, then computed
let A = [1.0, 2.0]
let L = method_for(A)
let r = replicate(2, L <@> lambda(x) -> x * x)
let result = r |> compute
// EXPECT: result = [1.0, 4.0, 1.0, 4.0]
"""

let replicateTests = [
    ("Replicate Basic", test144_replicateBasic)
    ("Replicate Two", test145_replicateTwo)
    ("Replicate Singleton", test146_replicateSingleton)
    ("Replicate Deferred", test147_replicateDeferred)
]

// ============================================================================
// Anonymous Range Tests (a..b syntax)
// ============================================================================

let test148_anonRangeZeroBased = """
// 0..N desugars to range<Idx<N>>
let result = method_for(0..5) <@> lambda(i) -> i |> compute
// EXPECT: result = [0, 1, 2, 3, 4]
"""

let test149_anonRangeOffset = """
// min..max with non-zero min
let result = method_for(3..7) <@> lambda(i) -> i |> compute
// EXPECT: result = [3, 4, 5, 6]
"""

let test150_anonRangeWithArray = """
// 0..N combined with a real array
let A = [10.0, 20.0, 30.0]
let L = method_for(A)
let result = L <@> lambda(a) -> a + 1.0 |> compute
let indices = method_for(0..3) <@> lambda(i) -> i |> compute
// EXPECT: result = [11.0, 21.0, 31.0]
// EXPECT: indices = [0, 1, 2]
"""

let test151_anonRangeLiteral = """
// Literal bounds 1..4 (offset range)
let result = method_for(1..4) <@> lambda(i) -> i * 10 |> compute
// EXPECT: result = [10, 20, 30]
"""

let anonRangeTests = [
    ("Anon Range Zero-Based", test148_anonRangeZeroBased)
    ("Anon Range Offset", test149_anonRangeOffset)
    ("Anon Range With Array", test150_anonRangeWithArray)
    ("Anon Range Literal", test151_anonRangeLiteral)
]

// ============================================================================
// Imperative For-In Loop Tests
// ============================================================================

let test152_forInBasic = """
// Basic for-in loop with accumulation
let out = {
    let mut acc = 0
    for k in 0..4 {
        acc = acc + k
    }
    acc
}
// EXPECT: out = 6
"""

let test153_forInWithArray = """
// for-in loop accessing an array
let A = [10, 20, 30]
let total = {
    let mut acc = 0
    for k in 0..3 {
        acc = acc + A(k)
    }
    acc
}
// EXPECT: total = 60
"""

let test154_forInPolyPack = """
// for-in loop with arity(args) in poly function
function polySum(args: Poly<T^0>) -> T^0 = {
    let mut out = 0
    for k in 0..arity(args) {
        out = out + args[k]
    }
    out
}

let result = polySum(10, 20, 30)
// EXPECT: result = 60
"""

let forInTests = [
    ("For-In Basic", test152_forInBasic)
    ("For-In With Array", test153_forInWithArray)
    ("For-In Poly Pack", test154_forInPolyPack)
]
