module Blade.Tests.IndexTypes

// ============================================================================
// AntisymIdx Tests
// ============================================================================

let test_antisym_iteration = """
// AntisymIdx iteration should use strict i < j bounds
// For n=4: pairs are (0,1),(0,2),(0,3),(1,2),(1,3),(2,3) = 6 elements
let A = [1.0, 2.0, 3.0, 4.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x - y
let result = L <@> f
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

// ============================================================================
// HermitianIdx Tests
// ============================================================================

let test_hermitian_parse_type = """
// Parse HermitianIdx in a function signature  
function trace(H: Array<Complex128 like HermitianIdx<n>>) -> Complex128 = {
    0.0
}
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
// Block expression return value tests
// ============================================================================

let test_block_return_simple = """
// Block should return its final expression
function f(x: Float64) -> Float64 = {
    let y = x * 2.0
    y + 1.0
}
"""

let test_block_return_with_loop = """
// Block with method_for should still return final expression
function g(A: Array<Float64 like Idx<n>>) -> Float64 = {
    let L = method_for(A, A)
    0.0
}
"""

// ============================================================================
// Test Collections
// ============================================================================

/// Index type tests (AntisymIdx, HermitianIdx)
let indexTypeTests = [
    ("AntisymIdx Iteration", test_antisym_iteration)
    ("AntisymIdx Kernel", test_antisym_type_in_kernel)
    ("Block Return Simple", test_block_return_simple)
    ("Block Return With Loop", test_block_return_with_loop)
    ("AntisymIdx Parse Type", test_antisym_parse_type)
    ("HermitianIdx Parse Type", test_hermitian_parse_type)
    ("HermitianIdx Rectangular", test_hermitian_rectangular)
]
