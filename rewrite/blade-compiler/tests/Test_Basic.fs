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
"""

let test44_multilineBlock = """
// Multi-line block with pipelines
let A = [1.0, 2.0, 3.0]
let result = {
    let L = method_for(A, A)
    let f = lambda(x, y) where comm(x, y) -> x * y
    L <@> f
}
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
]
