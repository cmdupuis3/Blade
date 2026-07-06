module Blade.Tests.Basic

let test1_basicExpr = """
let x = 1 + 2 * 3
// EXPECT: x = 7
"""

let test2_lambda = """
let f = lambda(a, b) -> a + b
"""

let test3_ifThenElse = """
let result = if true then 42 else 0
// EXPECT: result = 42
"""

let test7_arrayLit = """
let arr1d = [1.0, 2.0, 3.0]
let arr2d = [[1.0, 2.0], [3.0, 4.0]]
// EXPECT: arr1d = [1, 2, 3]
"""

let test12_nestedArray = """
let matrix = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0], [7.0, 8.0, 9.0]]
// EXPECT: matrix = [1, 2, 3, 4, 5, 6, 7, 8, 9]
"""

let test15_precedenceTest = """
// Test that * binds tighter than +
let x = 1 + 2 * 3
let y = (1 + 2) * 3
// EXPECT: x = 7
// EXPECT: y = 9
"""

let test18_matchExpr = """
let x = 5
let result = match x with
  | 0 -> 0
  | 1 -> 1
  | n -> n * 2
// EXPECT: result = 10
"""

let test19_matchWithGuard = """
let x = 10
let result = match x with
  | n if n > 5 -> n * 2
  | n if n > 0 -> n
  | _ -> 0
// EXPECT: result = 20
"""

let test20_tupleDestructure = """
let pair = (1, 2)
let (a, b) = pair
let sum = a + b
// EXPECT: sum = 3
"""

let test33_consDestructure = """
// :: destructuring pattern
let t = (1, 2, 3)
let head :: tail = t
"""

let test43_nestedFunction = """
// Nested function in block
let result = {
    function helper(x) -> Int = x * 2
    let y = helper(5)
    y + 1
}
// EXPECT: result = 11
"""

let test44_multilineBlock = """
// Multi-line block with pipelines
let A = [1.0, 2.0, 3.0]
let result = {
    let L = method_for(A, A)
    let f = lambda(x, y) where comm(x, y) -> x * y
    L <@> f |> compute
}
// Triangular: [1*1, 1*2, 1*3, 2*2, 2*3, 3*3]
// EXPECT: result = [1, 2, 3, 4, 6, 9]
"""

let test120_booleanBasic = """
// Boolean literals: True/False (capitalized) and true/false (lowercase)
let a = True
let b = False
let c = true
let d = false
let e = a && c
let f = b || d
// EXPECT: a = true
// EXPECT: b = false
// EXPECT: c = true
// EXPECT: d = false
// EXPECT: e = true
// EXPECT: f = false
"""

let test121_booleanCompare = """
// Boolean from comparison and if/else
let x = 3 > 2
let y = 1 == 2
let z = if x then 42 else 0
// EXPECT: x = true
// EXPECT: y = false
// EXPECT: z = 42
"""

let test122_stringScalar = """
// String scalar literals + equality. Exercises IRLitString lowering,
// litToCpp emission as std::string("..."), and string == comparison
// (which lowers to C++ std::string operator==).
let s = "hello"
let same = if s == "hello" then 42 else 0
let diff = if s == "world" then 1 else 0
// EXPECT: same = 42
// EXPECT: diff = 0
"""

let test123_stringArray = """
// String array literal — exercises the per-element genArrayLiteral
// path (extractLiteralValues silently returns [] for strings, so the
// fast scalar path falls through). Each element renders via litToCpp.
let words = ["forest", "urban", "farmland"]
let count = if words(0) == "forest" then 1 else 0
let count2 = count + (if words(1) == "urban" then 1 else 0)
let count3 = count2 + (if words(2) == "farmland" then 1 else 0)
// EXPECT: count3 = 3
"""

let test124_stringMatch = """
// Match expression on string scrutinee. Each IRPatLit with an
// IRLitString lowers via litToCpp to a `scrut == std::string("...")`
// guard, chained as nested ternaries by the match codegen.
let s = "urban"
let code = match s with
  | "forest" -> 1
  | "urban" -> 2
  | "farmland" -> 3
  | _ -> 0
let s2 = "ocean"
let code2 = match s2 with
  | "forest" -> 1
  | "urban" -> 2
  | _ -> 99
// EXPECT: code = 2
// EXPECT: code2 = 99
"""

let test125_stringDirectExpect = """
// Exercises the harness's ExpectedString and ExpectedArray1DString variants
// directly. cout emits strings without quotes (`s = hello`, not `s = "hello"`),
// so the harness strips the quotes from the EXPECT line before comparing.
// Array elements are split on `, ` from the printed form `[a, b, c]`.
let greeting = "hello"
let words = ["forest", "urban", "farmland"]
// EXPECT: greeting = "hello"
// EXPECT: words = ["forest", "urban", "farmland"]
"""

/// Basic language constructs
let basicTests = [
    ("Basic Expression", test1_basicExpr)
    ("Lambda", test2_lambda)
    ("If-Then-Else", test3_ifThenElse)
    ("Array Literals", test7_arrayLit)
    ("Nested Array", test12_nestedArray)
    ("Precedence Test", test15_precedenceTest)
    ("Match Expression", test18_matchExpr)
    ("Match With Guard", test19_matchWithGuard)
    ("Tuple Destructure", test20_tupleDestructure)
    ("Cons Destructure", test33_consDestructure)
    ("Nested Function", test43_nestedFunction)
    ("Multi-line Block", test44_multilineBlock)
    ("Boolean Basic", test120_booleanBasic)
    ("Boolean Compare", test121_booleanCompare)
    ("String Scalar", test122_stringScalar)
    ("String Array", test123_stringArray)
    ("String Match", test124_stringMatch)
    ("String Direct Expect", test125_stringDirectExpect)
]
