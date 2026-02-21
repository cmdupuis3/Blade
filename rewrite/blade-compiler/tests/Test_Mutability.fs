module Blade.Tests.Mutability

// ============================================================================
// Positive tests: should pass type checking
// ============================================================================

let test_let_assign = """
let result = {
    let x = 1
    x = x + 1
    x
}
// EXPECT: result = 2
"""

let test_let_assign_block = """
let result = {
    let x = 10
    x = x + 5
    x
}
// EXPECT: result = 15
"""

let test_let_mut_assign = """
let result = {
    let mut x = 1
    x = x + 1
    x
}
// EXPECT: result = 2
"""

let test_let_mut_in_block = """
let result = {
    let mut x = 0
    x = 10
    x += 5
    x
}
// EXPECT: result = 15
"""

let test_static_read = """
let static PI = 3.14159
let y = PI * 2.0
// EXPECT: y = 6.28318
"""

let test_static_function = """
let static twice = lambda(x) -> x * 2.0
let y = twice(21.0)
// EXPECT: y = 42
"""

let test_mixed_bindings = """
let static N = 100
let result = {
    let x = N + 1
    let mut y = N + 2
    x = x + 1
    y = y + 1
    x + y
}
// EXPECT: result = 205
"""

let test_compound_assign = """
let result = {
    let x = 10
    x += 5
    x -= 2
    x *= 3
    x
}
// EXPECT: result = 39
"""

// ============================================================================
// Negative tests: should FAIL type checking
// ============================================================================

let test_static_assign_error = """
let static x = 1
let result = {
    x = 2
    x
}
"""

let test_static_compound_assign_error = """
let static x = 10
let result = {
    x += 1
    x
}
"""

let test_static_function_reassign_error = """
let static f = lambda(x) -> x + 1
let result = {
    f = lambda(x) -> x + 2
    f(1)
}
"""

// ============================================================================
// Array element and struct field assignment
// ============================================================================

let test_array_element_assign = """
// EXPECT: result = 10
let result = {
    let A = [1.0, 2.0, 3.0]
    A(0) = 10.0
    A(0)
}
"""

let test_struct_field_assign = """
// EXPECT: result = 3
struct Point { x: Float, y: Float }
let p = Point { x = 1.0, y = 2.0 }
let result = {
    p.x = 3.0
    p.x
}
"""

// ============================================================================
// Test collections
// ============================================================================

/// Tests that should pass
let mutabilityTests = [
    ("Let assign", test_let_assign)
    ("Let assign in block", test_let_assign_block)
    ("Let mut assign", test_let_mut_assign)
    ("Let mut in block", test_let_mut_in_block)
    ("Static read", test_static_read)
    ("Static lambda", test_static_function)
    ("Mixed bindings", test_mixed_bindings)
    ("Compound assign", test_compound_assign)
    ("Array element assign", test_array_element_assign)
    ("Struct field assign", test_struct_field_assign)
]

/// Tests that should fail with a type error
let mutabilityErrorTests = [
    ("Static assign error", test_static_assign_error)
    ("Static compound assign error", test_static_compound_assign_error)
    ("Static function reassign error", test_static_function_reassign_error)
]
