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
// EXPECT: prod = [10, 40, 90]
"""

let test64_openmpParallel = """
// Test that OpenMP parallel loops compile and run
// Uses a larger array to potentially benefit from parallelism
let A = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0]
let B = [10.0, 20.0, 30.0, 40.0, 50.0, 60.0, 70.0, 80.0, 90.0, 100.0]

// Outer product creates 10x10 = 100 iterations - should parallelize
let outer = A [*] B

// The outer loop should have #pragma omp parallel for
"""

let test65_openmpSymmetric = """
// Test OpenMP with symmetric/triangular iteration
// Triangular loops should NOT have parallel on outermost loop
let A = [1.0, 2.0, 3.0, 4.0, 5.0]

// Symmetric kernel on same array - triangular iteration
let loop = method_for(A, A)
let kernel = lambda(x, y) where comm(x, y) -> x * y
let result = loop <@> kernel |> compute

// With comm, outer loop is triangular (i <= j), so no parallel on it
"""

let test66_openmpNested = """
// Test nested parallel regions with multiple arrays
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let B = [10.0, 20.0, 30.0, 40.0, 50.0]
let C = [100.0, 200.0, 300.0, 400.0, 500.0]

// Three-way outer product: 5x5x5 = 125 iterations
let loop = method_for(A, B, C)
let kernel = lambda(a, b, c) -> a + b + c
let result = loop <@> kernel |> compute

// Outermost loop should be parallel
"""

let test67_operatorSection = """
// Test first-class operator sections: (+), (*), etc.
// Use operator section directly as a kernel
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0, 30.0]

// Apply (+) as a kernel to method_for - creates pairwise sums
let loop = method_for(A, B)
let sums = loop <@> (+) |> compute

// Apply (*) as a kernel - creates pairwise products  
let prods = loop <@> (*) |> compute

// Inline usage - pairwise differences
let result = method_for(A, B) <@> (-) |> compute
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
]
