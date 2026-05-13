module Blade.Tests.FuncArrays

// ============================================================================
// Arrays of functions (formalism §5.3 "models" case)
// ============================================================================
//
// The formalism establishes that array element types can be arbitrary, including
// function types. The canonical example from §5.3:
//
//     let models: Array<(Params → TimeSeries) like LatIdx, LonIdx>
//     models(lat, lon)(land_model_params)(time)
//
// This file tests increasingly complex versions of this pattern, starting from
// the simplest "function as scalar value" case and building up to the nested-
// arrow case from the formalism. Each test stands alone; later tests assume
// the language features exercised by earlier tests work.
//
// Expected status (Segment 6 starting position): some of these likely fail.
// The failure pattern reveals which language features need work to fully
// support the formalism's array-of-functions semantics.

// ----------------------------------------------------------------------------
// T1: Function as scalar value (baseline — should already work)
// ----------------------------------------------------------------------------
// Bind a named function to a variable, call through the variable.
// If this fails, the rest of the file will too — it tests the most basic
// "functions are first-class values" capability.

let test_func_as_scalar = """
function add1(x: Float64) -> Float64 = x + 1.0
let f: Float64 -> Float64 = add1
let r = f(5.0)
// EXPECT: r = 6
"""

// ----------------------------------------------------------------------------
// T2: Function value via lambda (no name)
// ----------------------------------------------------------------------------

let test_func_lambda = """
let f: Float64 -> Float64 = lambda(x: Float64) -> x + 1.0
let r = f(5.0)
// EXPECT: r = 6
"""

// ----------------------------------------------------------------------------
// T3: Array of named functions (literal construction)
// ----------------------------------------------------------------------------
// Stores function values in an array literal. Indexing returns one function;
// calling it produces a scalar. This is the minimum case for "array of
// functions" — rank 1, uniform element type.

let test_array_of_named_funcs = """
function add1(x: Float64) -> Float64 = x + 1.0
function mul2(x: Float64) -> Float64 = x * 2.0
function sub1(x: Float64) -> Float64 = x - 1.0
let funcs: Array<(Float64 -> Float64) like Idx<3>> = [add1, mul2, sub1]
let r = funcs(1)(5.0)
// EXPECT: r = 10
"""

// ----------------------------------------------------------------------------
// T4: Function returning array
// ----------------------------------------------------------------------------
// The dual shape: a function whose return type is an array. This exercises
// the IRTArrow form `[SVal _] -> IRTArrow ([SIdx _], elem, identity)`.

let test_func_returning_array = """
function range3() -> Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]
let arr = range3()
let r = arr(1)
// EXPECT: r = 2
"""

// ----------------------------------------------------------------------------
// T5: Array of array-returning functions
// ----------------------------------------------------------------------------
// Combines T3 and T4: each element is a function returning an array.
// Type shape: Array<(Float64 -> Array<Float64 like Idx<3>>) like Idx<2>>

let test_array_of_arr_returning_funcs = """
function shift_up(x: Float64) -> Array<Float64 like Idx<3>> = [x + 1.0, x + 2.0, x + 3.0]
function shift_dn(x: Float64) -> Array<Float64 like Idx<3>> = [x - 1.0, x - 2.0, x - 3.0]
let funcs: Array<(Float64 -> Array<Float64 like Idx<3>>) like Idx<2>> = [shift_up, shift_dn]
let r = funcs(0)(10.0)(1)
// EXPECT: r = 12
"""

// ----------------------------------------------------------------------------
// T6: 2D array of functions (the formalism's models case, simplified)
// ----------------------------------------------------------------------------
// Array<(Float64 -> Float64) like Idx<2>, Idx<2>>: a 2x2 grid of scalar
// functions. This is the §5.3 models case with simpler types (scalars rather
// than parameterized types and time series).

let test_2d_array_of_funcs = """
function f00(x: Float64) -> Float64 = x + 0.0
function f01(x: Float64) -> Float64 = x + 1.0
function f10(x: Float64) -> Float64 = x + 10.0
function f11(x: Float64) -> Float64 = x + 11.0
let grid: Array<(Float64 -> Float64) like Idx<2>, Idx<2>> = [[f00, f01], [f10, f11]]
let r = grid(1, 0)(5.0)
// EXPECT: r = 15
"""

// ----------------------------------------------------------------------------
// Test list registration
// ----------------------------------------------------------------------------

let funcArrayTests = [
    ("FA T1: Func as scalar", test_func_as_scalar)
    ("FA T2: Func via lambda", test_func_lambda)
    ("FA T3: Array of named funcs", test_array_of_named_funcs)
    ("FA T4: Func returning array", test_func_returning_array)
    ("FA T5: Array of array-returning funcs", test_array_of_arr_returning_funcs)
    ("FA T6: 2D array of funcs", test_2d_array_of_funcs)
]
