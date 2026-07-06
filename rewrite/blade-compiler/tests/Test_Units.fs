module Blade.Tests.Units

let test68_unitBaseDecl = """
// Base unit declarations
Unit meters
Unit seconds
Unit kg
let x = 1.0
// EXPECT: x = 1
"""

let test69_unitDerived = """
// Derived unit declarations
Unit meters
Unit seconds
Unit velocity = meters / seconds
Unit acceleration = meters / seconds^2
let x = 2.0
// EXPECT: x = 2
"""

let test70_unitAnnotatedType = """
// Unit-annotated scalar types
Unit meters
Unit seconds
let dist: Float<meters> = 100.0
let time: Float<seconds> = 9.58
// EXPECT: dist = 100
// EXPECT: time = 9.58
"""

let test71_unitArithmetic = """
// Arithmetic with unit-annotated values
Unit meters
let a: Float<meters> = 10.0
let b: Float<meters> = 20.0
let c = a + b
// EXPECT: c = 30
"""

let test72_unitMultiply = """
// Multiplication produces derived units
Unit meters
let width: Float<meters> = 3.0
let height: Float<meters> = 4.0
let area = width * height
// EXPECT: area = 12
"""

let test73_unitDivision = """
// Division of units
Unit meters
Unit seconds
let dist: Float<meters> = 100.0
let time: Float<seconds> = 10.0
let speed = dist / time
// EXPECT: speed = 10
"""

let test74_unitWithStaticFunction = """
// Static function with unit-annotated args
Unit meters
static function double_dist(d) = d * 2.0
let static corridor = double_dist(50.0)
let x: Float<meters> = corridor
// EXPECT: x = 100
"""

let test75_unitComplex = """
// Multiple derived units
Unit kg
Unit meters
Unit seconds
Unit newtons = kg * meters / seconds^2
let mass: Float<kg> = 10.0
let accel = 9.8
let force = mass * accel
// EXPECT: force = 98
"""

let test76_unitMismatchAdd = """
// Adding incompatible units is a type error
Unit meters
Unit seconds
let dist: Float<meters> = 10.0
let time: Float<seconds> = 5.0
let bad = dist + time
"""

/// Unit of measure tests
let unitTests = [
    ("Unit Base Declaration", test68_unitBaseDecl)
    ("Unit Derived", test69_unitDerived)
    ("Unit Annotated Type", test70_unitAnnotatedType)
    ("Unit Arithmetic", test71_unitArithmetic)
    ("Unit Multiply", test72_unitMultiply)
    ("Unit Division", test73_unitDivision)
    ("Unit With Static Function", test74_unitWithStaticFunction)
    ("Unit Complex", test75_unitComplex)
]

/// Negative tests: should fail type checking
let unitErrorTests = [
    ("Unit Mismatch Add", test76_unitMismatchAdd)
]
