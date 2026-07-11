module Blade.Tests.Bracketed

let test59_bracketedArithmetic = """
// Bracketed arithmetic operators for outer product
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let added = A [+] B
let multiplied = A [*] B
let powered = A [^] B
// added is 3x2: [[11,21], [12,22], [13,23]]
// EXPECT: added = [11, 21, 12, 22, 13, 23]
// multiplied is 3x2: [[10,20], [20,40], [30,60]]
// EXPECT: multiplied = [10, 20, 20, 40, 30, 60]
"""

let test60_bracketedComparison = """
// Bracketed comparison operators
let A = [1, 2, 3, 4, 5]
let B = [3, 3, 3]
let less_than = A [<] B
let equal = A [==] B
let greater_eq = A [>=] B
"""

let test61_bracketedLogical = """
// Bracketed logical operators
let P = [true, false, true]
let Q = [true, true, false]
let and_result = P [&&] Q
let or_result = P [||] Q
"""

let test62_bracketedMixed = """
// Mixed bracketed operators in same expression
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let C = [5.0, 6.0]
// Outer products with different operators
let outer_mul = A [*] B
let outer_add = A [+] C
// EXPECT: outer_mul = [3, 4, 6, 8]
// EXPECT: outer_add = [6, 7, 7, 8]
"""

let test63_elementwiseArrayOps = """
// Elementwise operations on arrays (co-iteration)
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0, 30.0]
let sum = A + B           // elementwise: [11.0, 22.0, 33.0]
let diff = A - B          // elementwise: [-9.0, -18.0, -27.0]
let prod = A * B          // elementwise: [10.0, 40.0, 90.0]
let quot = B / A          // elementwise: [10.0, 10.0, 10.0]
// EXPECT: sum = [11, 22, 33]
// EXPECT: diff = [-9, -18, -27]
// EXPECT: prod = [10, 40, 90]
// EXPECT: quot = [10, 10, 10]
"""

let test64_openmpParallel = """
// OpenMP correctness: rectangular outer product gets #pragma omp parallel for.
// Verify results match hand-computed values — if OMP has a race condition, values will be wrong.
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let B = [10.0, 20.0, 30.0, 40.0, 50.0]
let L = method_for(A, B)
let f = lambda(x, y) -> x * y
let result = L <@> f |> compute
// 5×5 rectangular = 25 elements, outermost loop gets OMP
// EXPECT: result = [10, 20, 30, 40, 50, 20, 40, 60, 80, 100, 30, 60, 90, 120, 150, 40, 80, 120, 160, 200, 50, 100, 150, 200, 250]
"""

let test65_openmpSymmetric = """
// Test OpenMP with symmetric/triangular iteration.
// Triangular loops parallelize the OUTERMOST loop with schedule(dynamic):
// each outer index owns a disjoint triangular sub-slab, and dynamic scheduling
// balances the (unequal) per-slab work. Inner dependent loops stay sequential
// (collapse is unsafe on a non-rectangular space). Values are independent of
// the parallelization strategy — a race would corrupt them.
let A = [1.0, 2.0, 3.0, 4.0, 5.0]

// Symmetric kernel on same array - triangular iteration
let loop = method_for(A, A)
let kernel = lambda(x, y) where comm(x, y) -> x * y
let result = loop <@> kernel |> compute

// With comm, iteration is triangular (i <= j); outer loop parallel, inner serial.
// Triangular: [1*1,1*2,1*3,1*4,1*5, 2*2,2*3,2*4,2*5, 3*3,3*4,3*5, 4*4,4*5, 5*5]
// EXPECT: result = [1, 2, 3, 4, 5, 4, 6, 8, 10, 9, 12, 15, 16, 20, 25]
"""

let test66_openmpNested = """
// 3-way outer product: outermost loop gets OMP
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0, 300.0]
let loop = method_for(A, B, C)
let kernel = lambda(a, b, c) -> a + b + c
let result = loop <@> kernel |> compute
// 3×2×3 = 18 elements
// EXPECT: result = [111, 211, 311, 121, 221, 321, 112, 212, 312, 122, 222, 322, 113, 213, 313, 123, 223, 323]
"""

let test67_operatorSection = """
// Test first-class operator sections: (+), (*), etc.
// Use operator section directly as a kernel
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0, 30.0]

// Apply (+) as a kernel to method_for - creates pairwise sums
let loop = method_for(A, B)
let sums = loop <@> (+) |> compute
// EXPECT: sums = [11, 21, 31, 12, 22, 32, 13, 23, 33]

// Apply (*) as a kernel - creates pairwise products  
let prods = loop <@> (*) |> compute
// EXPECT: prods = [10, 20, 30, 20, 40, 60, 30, 60, 90]

// Inline usage - pairwise differences
let result = method_for(A, B) <@> (-) |> compute
// EXPECT: result = [-9, -19, -29, -8, -18, -28, -7, -17, -27]
"""

let test68_namedInfix = """
// Test named infix operator PARSING: a :name: b -> name(a, b)
// Note: Full runtime requires lambda variable calling which is a separate feature

// Verify the lexer recognizes :name: tokens
// and the parser desugars to function application

// For now, demonstrate with scalars (no function call needed)
let a = 3.0
let b = 4.0
let c = a + b

// The :name: syntax works at parse level but full test 
// needs lambda-in-variable calling support (future work)
"""

// Elementwise Bool AND of two masks via bare `&&` (mask algebra) — the clean
// replacement for the method_for(zip(...)) <@> a && b idiom. Bool inputs, Bool
// output; feeds compound directly (mask shares A's index identity).
let test69_elementwiseMaskAnd = """
type AB = Idx<5>
let A: Array<Float64 like AB> = [1.0, 2.0, 3.0, 4.0, 5.0]
let above2 = mask(A, lambda(x) -> x > 2.0)
let below5 = mask(A, lambda(x) -> x < 5.0)
let both = above2 && below5
let f = compound(A, both)
// above2 && below5 = [F,F,T,T,F] -> coordinates {2, 3} = 3.0, 4.0
let n = extents(f)
let x2 = f(2)
let x3 = f(3)
// EXPECT: n = 2
// EXPECT: x2 = 3.0
// EXPECT: x3 = 4.0
"""

// Elementwise comparison on NUMERIC arrays via bare `>=` produces a Bool array
// (exercises the object_for output-element-type = kernel-return-type path:
// double inputs, bool output). Feeds compound.
let test70_elementwiseCompare = """
type CD = Idx<5>
let A: Array<Float64 like CD> = [1.0, 2.0, 3.0, 4.0, 5.0]
let B: Array<Float64 like CD> = [3.0, 3.0, 3.0, 3.0, 3.0]
let ge = A >= B
let f = compound(A, ge)
// A >= B = [F,F,T,T,T] -> coordinates {2, 3, 4} = 3.0, 4.0, 5.0
let n = extents(f)
let x2 = f(2)
let x4 = f(4)
// EXPECT: n = 3
// EXPECT: x2 = 3.0
// EXPECT: x4 = 5.0
"""

// Array<->scalar broadcast comparison: `A > 2.0` produces a Bool mask directly
// (scalar held fixed, array iterated). Also covers scalar-on-left `2.0 < A`.
let test71_broadcastCompare = """
type EF = Idx<5>
let A: Array<Float64 like EF> = [1.0, 2.0, 3.0, 4.0, 5.0]
let hi = A > 2.0
let f = compound(A, hi)
// A > 2.0 = [F,F,T,T,T] -> coordinates {2, 3, 4} = 3.0, 4.0, 5.0
let n = extents(f)
let x2 = f(2)
let x4 = f(4)
let hi2 = 2.0 < A
let g = compound(A, hi2)
let m = extents(g)
// EXPECT: n = 3
// EXPECT: x2 = 3.0
// EXPECT: x4 = 5.0
// EXPECT: m = 3
"""

/// Bracketed (outer product) operator tests
let bracketedTests = [
    ("Bracketed Arithmetic", test59_bracketedArithmetic)
    ("Bracketed Comparison", test60_bracketedComparison)
    ("Bracketed Logical", test61_bracketedLogical)
    ("Bracketed Mixed", test62_bracketedMixed)
    ("Elementwise Array Ops", test63_elementwiseArrayOps)
    ("OpenMP Parallel", test64_openmpParallel)
    ("OpenMP Symmetric", test65_openmpSymmetric)
    ("OpenMP Nested", test66_openmpNested)
    ("Operator Section", test67_operatorSection)
    ("Named Infix", test68_namedInfix)
    ("Elementwise Mask AND", test69_elementwiseMaskAnd)
    ("Elementwise Compare", test70_elementwiseCompare)
    ("Broadcast Compare", test71_broadcastCompare)
]
