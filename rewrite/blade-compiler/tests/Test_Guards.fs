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
// Nested match with guards
let x = 10
let y = 20
let outer = match x with
    | n if n > 5 -> match y with
        | m if m > 15 -> n + m
        | _ -> n
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
