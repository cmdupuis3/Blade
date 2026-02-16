module Blade.Tests.Modules

let test53_moduleDecl = """
module Math.Geometry

let pi = 3.14159
function circleArea(r: Float64) -> Float64 = pi * r * r
"""

let test54_moduleWithImport = """
module Main

let x = 42
let y = x * 2
// EXPECT: y = 84
"""

/// Multi-file test: (testName, [(moduleName, source), ...])
/// Modules are listed in dependency order (imported modules first)
let test80_crossModuleValue : string * (string * string) list =
    ("Cross-module value import",
    [
        ("Math", """
module Math

let pi = 3.14159
let e = 2.71828
""");
        ("Main", """
module Main
import Math

let tau = Math.pi * 2.0
// EXPECT: tau = 6.28318
""")
    ])

let test81_crossModuleFunction : string * (string * string) list =
    ("Cross-module function import",
    [
        ("MathLib", """
module MathLib

function double(x: Float64) -> Float64 = x * 2.0
function square(x: Float64) -> Float64 = x * x
""");
        ("Main", """
module Main
import MathLib

let a = MathLib.double(5.0)
let b = MathLib.square(3.0)
// EXPECT: a = 10
// EXPECT: b = 9
""")
    ])

let test82_crossModuleAlias : string * (string * string) list =
    ("Cross-module import with alias",
    [
        ("Utilities", """
module Utilities

let scale = 100.0
function convert(x: Float64) -> Float64 = x * scale
""");
        ("Main", """
module Main
import Utilities as U

let result = U.convert(3.5)
// EXPECT: result = 350
""")
    ])

let test83_multipleImports : string * (string * string) list =
    ("Multiple module imports",
    [
        ("Constants", """
module Constants

let pi = 3.14159
let gravity = 9.81
""");
        ("Formulas", """
module Formulas

function area(r: Float64) -> Float64 = r * r
""");
        ("Main", """
module Main
import Constants
import Formulas

let circleArea = Constants.pi * Formulas.area(5.0)
// EXPECT: circleArea = 78.53975
""")
    ])

let test84_chainedImports : string * (string * string) list =
    ("Chained module imports (A imports B, C imports A)",
    [
        ("Base", """
module Base

let factor = 10.0
""");
        ("Middle", """
module Middle
import Base

let scaled = Base.factor * 2.0
""");
        ("Main", """
module Main
import Middle

let result = Middle.scaled + 5.0
// EXPECT: result = 25
""")
    ])

let test85_selectiveImport : string * (string * string) list =
    ("Selective import (from...import)",
    [
        ("Math", """
module Math

let pi = 3.14159
let e = 2.71828
let phi = 1.61803
""");
        ("Main", """
module Main
from Math import pi, e

let tau = pi * 2.0
let sum = pi + e
// EXPECT: tau = 6.28318
// EXPECT: sum = 5.85987
""")
    ])

let test86_selectiveImportFunction : string * (string * string) list =
    ("Selective import of functions",
    [
        ("Helpers", """
module Helpers

function double(x: Float64) -> Float64 = x * 2.0
function triple(x: Float64) -> Float64 = x * 3.0
function negate(x: Float64) -> Float64 = 0.0 - x
""");
        ("Main", """
module Main
from Helpers import double, negate

let a = double(5.0)
let b = negate(3.0)
// EXPECT: a = 10
// EXPECT: b = -3
""")
    ])

let test87_mixedImportStyles : string * (string * string) list =
    ("Mixed qualified and selective imports",
    [
        ("Constants", """
module Constants

let pi = 3.14159
let gravity = 9.81
""");
        ("Formulas", """
module Formulas

function square(x: Float64) -> Float64 = x * x
function cube(x: Float64) -> Float64 = x * x * x
""");
        ("Main", """
module Main
import Constants as C
from Formulas import square

let area = C.pi * square(5.0)
// EXPECT: area = 78.53975
""")
    ])

/// Single-file module tests
let moduleTests = [
    ("Module Declaration", test53_moduleDecl)
    ("Module With Declarations", test54_moduleWithImport)
]

/// Multi-file module tests
let multiFileTests = [
    test80_crossModuleValue
    test81_crossModuleFunction
    test82_crossModuleAlias
    test83_multipleImports
    test84_chainedImports
    test85_selectiveImport
    test86_selectiveImportFunction
    test87_mixedImportStyles
]
