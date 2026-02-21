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
let result = R <@> f |> compute
// EXPECT: result = [0, 2, 4, 6, 8]
"""

let test78_reverseBasic = """
// reverse<Idx<5>> produces virtual array [4, 3, 2, 1, 0]
let R = method_for(reverse<Idx<5>>)
let f = lambda(i) -> i * 1.0
let result = R <@> f |> compute
// EXPECT: result = [4, 3, 2, 1, 0]
"""

let test79_rangeWithArray = """
// Mix range with a real array
let A = [10.0, 20.0, 30.0]
let L = method_for(A, range<Idx<3>>)
let f = lambda(a, i) -> a + i
let result = L <@> f
"""

// Co-iteration: elementwise product via shared index space
let test80_coIterBasic = """
// Co-iteration: for (A, B) in range<Idx<N>> produces elementwise result
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let result = for (A, B) in range<Idx<3>> <@> lambda(a, b) -> a * b
"""

// Co-iteration: three-way elementwise operation
let test81_coIter3Way = """
// Three-way co-iteration
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let C = [7.0, 8.0, 9.0]
let result = for (A, B, C) in range<Idx<3>> <@> lambda(a, b, c) -> a * b + c
"""

let test82_composeBasic = """
function double(x) -> Int = x * 2
function add_one(x) -> Int = x + 1
let f = double >> add_one
let result = f(5)
// EXPECT: result = 11
"""

let test83_composeChain = """
function inc(x) -> Float64 = x + 1.0
function triple(x) -> Float64 = x * 3.0
function negate(x) -> Float64 = 0.0 - x
let g = inc >> triple >> negate
let result = g(2.0)
// EXPECT: result = -9.0
"""

let test84_parallelBasic = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let (sums, prods) = (L <@> f) <&> (L <@> g) |> compute
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
"""

let test85_fusionBasic = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let (sums, prods) = (L <@> f) <&!> (L <@> g) |> compute
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
"""

let test86_parallel3Way = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let (inner, squares) = (L <@> f) <&> (L <@> g) <&> (L <@> h) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test87_fusion3Way = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let (inner, squares) = (L <@> f) <&!> (L <@> g) <&!> (L <@> h) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test88_arrayProductBasic = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A) <*> method_for(B)
let result = L <@> lambda(x, y) -> x + y |> compute
// EXPECT: result = [5, 6, 7, 6, 7, 8, 7, 8, 9]
"""

let test89_arrayProductSymmetric = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A) <*> method_for(A)
let result = L <@> lambda(x, y) where comm(x, y) -> x + y |> compute
// EXPECT: result = [2, 3, 4, 4, 5, 6]
"""

let test90_arrayProduct3Way = """
let A = [1.0, 2.0]
let B = [10.0, 20.0]
let C = [100.0, 200.0]
let L = method_for(A) <*> method_for(B) <*> method_for(C)
let result = L <@> lambda(x, y, z) -> x + y + z |> compute
// EXPECT: result = [111, 211, 121, 221, 112, 212, 122, 222]
"""

let test91_objectForParallel = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let c1 = L <@> f
let c2 = L <@> g
let (sums, prods) = c1 <&> c2 |> compute
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
"""

let test92_objectForFusion = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let c1 = L <@> f
let c2 = L <@> g
let (sums, prods) = c1 <&!> c2 |> compute
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
"""

let test93_objectForArrayProduct = """
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0]
let L = method_for(A) <*> method_for(B)
let result = L <@> lambda(x, y) -> x * y |> compute
// EXPECT: result = [4, 5, 8, 10, 12, 15]
"""

let test94_objectForCombParallel = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let c1 = L <@> lambda(x) -> x + 10.0
let c2 = L <@> lambda(x) -> x * 2.0
let c3 = L <@> lambda(x) -> x * x
let (inner, squares) = object_for(<&>) <@> (c1, c2, c3) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test95_objectForCombFusion = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let c1 = L <@> lambda(x) -> x + 10.0
let c2 = L <@> lambda(x) -> x * 2.0
let c3 = L <@> lambda(x) -> x * x
let (inner, squares) = object_for(<&!>) <@> (c1, c2, c3) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test96_objectForCombApply = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let (c1, c2) = object_for(<@>) <@> ((L, f), (L, g))
let (sums, prods) = c1 <&> c2 |> compute
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
"""

let test97_objectForCombApply3Way = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let (c1, c2, c3) = object_for(<@>) <@> ((L, f), (L, g), (L, h))
let (inner, squares) = object_for(<&>) <@> (c1, c2, c3) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
"""

let test98_pipeApplyBasic = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let result = f |@> L |> compute
// EXPECT: result = [11.0, 12.0, 13.0]
"""

let test99_pipeApplyChain = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A)
let f = lambda(x) -> x + 10.0
let g = lambda(x) -> x * 2.0
let h = lambda(x) -> x * x
let (inner, squares) = ((L, f), (L, g), (L, h)) |@> object_for(<@>) |@> object_for(<&>) |> compute
let (sums, prods) = inner
// EXPECT: sums = [11.0, 12.0, 13.0]
// EXPECT: prods = [2.0, 4.0, 6.0]
// EXPECT: squares = [1.0, 4.0, 9.0]
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
    ("CoIter Basic", test80_coIterBasic)
    ("CoIter 3-Way", test81_coIter3Way)
    ("Compose Basic", test82_composeBasic)
    ("Compose Chain", test83_composeChain)
    ("Parallel Basic", test84_parallelBasic)
    ("Fusion Basic", test85_fusionBasic)
    ("Parallel 3-Way", test86_parallel3Way)
    ("Fusion 3-Way", test87_fusion3Way)
    ("Array Product Basic", test88_arrayProductBasic)
    ("Array Product Symmetric", test89_arrayProductSymmetric)
    ("Array Product 3-Way", test90_arrayProduct3Way)
    ("Object For Parallel", test91_objectForParallel)
    ("Object For Fusion", test92_objectForFusion)
    ("Object For Array Product", test93_objectForArrayProduct)
    ("Object For Comb Parallel", test94_objectForCombParallel)
    ("Object For Comb Fusion", test95_objectForCombFusion)
    ("Object For Comb Apply", test96_objectForCombApply)
    ("Object For Comb Apply 3-Way", test97_objectForCombApply3Way)
    ("Pipe Apply Basic", test98_pipeApplyBasic)
    ("Pipe Apply Chain", test99_pipeApplyChain)
]
