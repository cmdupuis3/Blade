module Blade.Tests.Functions

let test13_functionDecl = """
function add(a: Float64, b: Float64) -> Float64 = a + b
"""

let test14_functionWithWhere = """
// Function with array parameters - extents passed alongside arrays
function covariance(A: Array<Float64 like Idx<n>>, B: Array<Float64 like Idx<n>>) 
  where comm(A, B) -> Float64 = 
  method_for(A, B) <@> lambda(x, y) -> x * y |> compute
"""

let test23_kernelWithTypes = """
// Kernel with explicit types - inline syntax
function vectorDot(a: Array<Float64 like Idx<n>>, b: Array<Float64 like Idx<n>>) -> Float64 = 
  method_for(a, b) <@> lambda(x: Float64, y: Float64) -> x * y |> compute
"""

let test24_nonScalarKernel = """
// Kernel that operates on 1D slices (irank = 1 for each input)
let matrix = [[1.0, 2.0], [3.0, 4.0]]
let O = object_for(lambda(row: Array<Float64 like Idx<2>>) -> row)
"""

let test10_scalarCaptureInKernel = """
// Scalars CAN be captured by lambdas (only arrays are forbidden)
let scale = 2.5
let offset = 1.0
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let f = lambda(x, y) -> (x + y) * scale + offset
let result = L <@> f |> compute
// result[i][j] = (A[i]+B[j])*2.5 + 1
// EXPECT: result = [13.5, 16, 18.5, 16, 18.5, 21, 18.5, 21, 23.5]
"""

let test25_functionCapture = """
// Lambdas CAN capture functions from environment
// Scalar params inferred, return type inferred
function square(x) = x * x
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> square(x) + square(y)
let result = L <@> f |> compute
// EXPECT: result = [2, 5, 10, 8, 13, 18]
"""

let test26_arrayCaptureRejected = """
// Lambdas CANNOT capture arrays - this should fail
let A = [1.0, 2.0, 3.0]
let bad = lambda(x) -> x + A
"""

let test51_functionCallResult = """
// Function declaration + call with verified result
function add(a: Float64, b: Float64) -> Float64 = a + b
function square(x: Float64) -> Float64 = x * x
let r1 = add(3.0, 4.0)
let r2 = square(5.0)
let r3 = add(square(2.0), square(3.0))
// EXPECT: r1 = 7
// EXPECT: r2 = 25
// EXPECT: r3 = 13
"""

/// Functions and captures
let functionTests = [
    ("Function Declaration", test13_functionDecl)
    ("Function With Where Clause", test14_functionWithWhere)
    ("Kernel With Types", test23_kernelWithTypes)
    ("Non-Scalar Kernel", test24_nonScalarKernel)
    ("Scalar Capture In Kernel", test10_scalarCaptureInKernel)
    ("Function Capture", test25_functionCapture)
    ("Function Call Result", test51_functionCallResult)
]
