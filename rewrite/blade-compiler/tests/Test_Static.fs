module Blade.Tests.Static

let test57_staticValue = """
// Basic static value evaluated at compile time
let static n = 100
let x = n
// EXPECT: x = 100
"""

let test58_staticArithmetic = """
// Static expressions with arithmetic
let static a = 10
let static b = 20
let static c = a + b
let static d = a * b + 5
let x = c
let y = d
// EXPECT: x = 30
// EXPECT: y = 205
"""

let test59_staticFunction = """
// Static function evaluated at compile time
static function triangle(k) = k * (k + 1) / 2
let static n = 10
let static t = triangle(n)
let x = t
// EXPECT: x = 55
"""

let test60_staticDependencyOrder = """
// Static values with dependencies resolved in correct order
let static base_dim = 5
static function sym_count(d) = d * (d + 1) / 2
let static total = sym_count(base_dim)
let static doubled = total * 2
let x = doubled
// EXPECT: x = 30
"""

let test61_staticConditional = """
// Static function with conditional
static function clamp(x, lo, hi) = if x < lo then lo else if x > hi then hi else x
let static a = clamp(15, 0, 10)
let static b = clamp(-5, 0, 10)
let static c = clamp(7, 0, 10)
let x = a
let y = b
let z = c
// EXPECT: x = 10
// EXPECT: y = 0
// EXPECT: z = 7
"""

let test62_staticRecursiveFunction = """
// Static function with recursion (fibonacci)
static function fib(n) = if n <= 1 then n else fib(n - 1) + fib(n - 2)
let static f10 = fib(10)
let x = f10
// EXPECT: x = 55
"""

let test63_staticMutualRecursion = """
// Static functions calling each other
static function is_even(n) = if n == 0 then 1 else is_odd(n - 1)
static function is_odd(n) = if n == 0 then 0 else is_even(n - 1)
let static a = is_even(10)
let static b = is_odd(7)
let x = a
let y = b
// EXPECT: x = 1
// EXPECT: y = 1
"""

let test64_staticInTypeExtent = """
// Static value used in type position (index extent)
let static n = 5
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
"""

let test65_staticForwardRef = """
// Static values can be used before their textual definition
// because evaluation uses topological order, not source order
let static result = base + offset
let static base = 100
let static offset = 42
let x = result
// EXPECT: x = 142
"""

let test66_staticMatch = """
// Static function with match expression
static function dim(l) = match l with
    | 0 -> 1
    | 1 -> 3
    | 2 -> 5
    | _ -> 2 * l + 1
let static d0 = dim(0)
let static d1 = dim(1)
let static d2 = dim(2)
let static d3 = dim(3)
let a = d0
let b = d1
let c = d2
let d = d3
// EXPECT: a = 1
// EXPECT: b = 3
// EXPECT: c = 5
// EXPECT: d = 7
"""

let test67_staticFunctionDualUse = """
// Same static function used both at compile time and at runtime
static function twice(x) = x * 2
let static ct_result = twice(21)
let rt_input = 5
let rt_result = twice(rt_input)
let a = ct_result
let b = rt_result
// EXPECT: a = 42
// EXPECT: b = 10
"""

/// Static evaluation tests
let staticTests = [
    ("Static Value", test57_staticValue)
    ("Static Arithmetic", test58_staticArithmetic)
    ("Static Function", test59_staticFunction)
    ("Static Dependency Order", test60_staticDependencyOrder)
    ("Static Conditional", test61_staticConditional)
    ("Static Recursive Function", test62_staticRecursiveFunction)
    ("Static Mutual Recursion", test63_staticMutualRecursion)
    ("Static In Type Extent", test64_staticInTypeExtent)
    ("Static Forward Reference", test65_staticForwardRef)
    ("Static Match", test66_staticMatch)
    ("Static Function Dual Use", test67_staticFunctionDualUse)
]
