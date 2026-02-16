module Blade.Tests.Loops

let test4_methodFor = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
"""

let test5_objectFor = """
let kernel = lambda(x: Float64, y: Float64) -> x * y
let O = object_for(kernel)
"""

let test6_apply = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) -> x * y
let result = L <@> f
"""

let test9_loopObjectReuse = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let sum = lambda(x, y) -> x + y
let prod = lambda(x, y) -> x * y
let sumResult = L <@> sum
let prodResult = L <@> prod
"""

let test11_objectForWithArrays = """
let kernel = lambda(a, b) -> a * b + 1.0
let O = object_for(kernel)
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
"""

let test23_objectForApply = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let f = lambda(x, y) -> x + y
let O = object_for(f)
let result = O <@> (A, B)
"""

let test24_objectForComm = """
let A = [1.0, 2.0, 3.0]
let f = lambda(x, y) where comm(x, y) -> x * y
let O = object_for(f)
let result = O <@> (A, A)
"""

let test16_combinators = """
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let L1 = method_for(A, A)
let L2 = method_for(B, B)
let f = lambda(x, y) -> x + y
// Parallel composition
let parallel = (L1 <@> f) <&> (L2 <@> f)
"""

let test21_compute = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) -> x * y
let result = L <@> f |> compute
// result is 3x3 matrix: [[1*1, 1*2, 1*3], [2*1, 2*2, 2*3], [3*1, 3*2, 3*3]]
// EXPECT: result = [1, 2, 3, 2, 4, 6, 3, 6, 9]
"""

let test22_pureAndBind = """
let x = pure(42)
let f = lambda(n) -> n * 2
let result = x >>= f
"""

let test77_rangeBasic = """
// range<Idx<5>> produces virtual array [0, 1, 2, 3, 4]
let R = method_for(range<Idx<5>>)
let f = lambda(i) -> i * 2.0
let result = R <@> f
// EXPECT: result = [0, 2, 4, 6, 8]
"""

let test78_reverseBasic = """
// reverse<Idx<5>> produces virtual array [4, 3, 2, 1, 0]
let R = method_for(reverse<Idx<5>>)
let f = lambda(i) -> i * 1.0
let result = R <@> f
// EXPECT: result = [4, 3, 2, 1, 0]
"""

let test79_rangeWithArray = """
// Mix range with a real array
let A = [10.0, 20.0, 30.0]
let L = method_for(A, range<Idx<3>>)
let f = lambda(a, i) -> a + i
let result = L <@> f
"""

/// Loop objects and application
let loopTests = [
    ("Method For", test4_methodFor)
    ("Object For", test5_objectFor)
    ("Apply Combinator", test6_apply)
    ("Loop Object Reuse", test9_loopObjectReuse)
    ("Object For With Arrays", test11_objectForWithArrays)
    ("Combinators", test16_combinators)
    ("Compute", test21_compute)
    ("Pure and Bind", test22_pureAndBind)
    ("Object For Apply", test23_objectForApply)
    ("Object For Commutative", test24_objectForComm)
    ("Range Basic", test77_rangeBasic)
    ("Reverse Basic", test78_reverseBasic)
    ("Range With Array", test79_rangeWithArray)
]
