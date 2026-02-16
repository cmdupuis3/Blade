module Blade.Tests.Structs

let test45_structDecl = """
// Basic struct declaration and construction
struct Point {
    x: Float64,
    y: Float64
}
let p = Point { x = 1.0, y = 2.0 }
"""

let test46_structFieldAccess = """
// Struct field access
struct Vector3 {
    x: Float64,
    y: Float64,
    z: Float64
}
let v = Vector3 { x = 1.0, y = 2.0, z = 3.0 }
let sum = v.x + v.y + v.z
// EXPECT: sum = 6
"""

let test47_structPattern = """
// Struct destructuring in pattern
struct Pair {
    first: Int,
    second: Int
}
let p = Pair { first = 10, second = 20 }
let Pair { first, second } = p
let total = first + second
// EXPECT: total = 30
"""

let test48_structConstraintValid = """
// Struct with where constraint - valid construction
struct Balanced {
    a: Int,
    b: Int,
    total: Int
} where a + b == total
let x = Balanced { a = 3, b = 7, total = 10 }
let result = x.total
"""

let test49_structConstraintArith = """
// Struct with arithmetic constraint - valid
struct Ratio {
    num: Float64,
    den: Float64
} where den != 0.0
let r = Ratio { num = 3.14, den = 2.0 }
let half = r.num / r.den
"""

let test50_structConstraintInvalid = """
// Struct with where constraint - INVALID construction (should abort)
struct Balanced {
    a: Int,
    b: Int,
    total: Int
} where a + b == total
let x = Balanced { a = 3, b = 7, total = 99 }
"""

/// Struct tests
let structTests = [
    ("Struct Declaration", test45_structDecl)
    ("Struct Field Access", test46_structFieldAccess)
    ("Struct Pattern", test47_structPattern)
    ("Struct Constraint Valid", test48_structConstraintValid)
    ("Struct Constraint Arithmetic", test49_structConstraintArith)
]

/// Tests that should abort at runtime (constraint violation)
let structAbortTests = [
    ("Struct Constraint Invalid", test50_structConstraintInvalid)
]
