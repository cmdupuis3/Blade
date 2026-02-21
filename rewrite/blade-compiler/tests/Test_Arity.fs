module Blade.Tests.Arity

let test31_polyType = """
// Poly<T^r> type parsing
function comoment(args: Poly<T^0>)
where comm(args)
-> T^0
= args[0]
"""

let test32_rankIntrinsic = """
// rank() intrinsic on 1D array
let A = [1.0, 2.0, 3.0]
let r = rank(A)
// EXPECT: r = 1
"""

let test32b_rankScalar = """
// rank() on a scalar value is 0
let x = 42.0
let r = rank(x)
// EXPECT: r = 0
"""

let test32c_rankArithmetic = """
// rank() used in arithmetic
let A = [1.0, 2.0, 3.0]
let r = rank(A)
let doubled = r * 2
// EXPECT: doubled = 2
"""

let test32d_rankComparison = """
// rank() in conditional
let A = [1.0, 2.0, 3.0]
let isVector = rank(A) == 1
// EXPECT: isVector = 1
"""

let test34_arityKeyword = """
// arity(param) - get arity of a Poly<> parameter
// Returns the number of elements in the poly pack at call site
function firstOrDefault(args: Poly<T^0>, fallback: T^0) -> T^0
= if arity(args) == 1 then args[0] else fallback
"""

let test34b_multiPolyArity = """
// Multiple Poly<> parameters - each has its own arity
// Useful for zip-like operations that need to know both sizes
function selectLarger(xs: Poly<T^0>, ys: Poly<T^0>) -> T^0
= if arity(xs) >= arity(ys) then xs[0] else ys[0]
"""

let test35_arityReturnType = """
// T^arity(param) in return type - rank depends on poly pack size
function identity(args: Poly<T^1>) -> T^arity(args)
= args[0]
"""

let test36_polyCallSite = """
// Poly function with actual call site — triggers monomorphization
function polySum(args: Poly<T^0>) -> T^0
= args[0] + args[1]

let result = polySum(3.0, 4.0)
// EXPECT: result = 7.0
"""

let test36b_arityInPoly = """
// arity(param) inside poly function, monomorphized at call site
function polyArityCheck(args: Poly<T^0>) -> T^0
= arity(args)

let result = polyArityCheck(10, 20, 30)
// EXPECT: result = 3
"""

/// Arity polymorphism tests
let arityTests = [
    ("Poly Type", test31_polyType)
    ("Rank Intrinsic", test32_rankIntrinsic)
    ("Rank Scalar", test32b_rankScalar)
    ("Rank Arithmetic", test32c_rankArithmetic)
    ("Rank Comparison", test32d_rankComparison)
    ("Arity Keyword", test34_arityKeyword)
    ("Multi Poly Arity", test34b_multiPolyArity)
    ("Arity Return Type", test35_arityReturnType)
    ("Poly Call Site", test36_polyCallSite)
    ("Arity In Poly", test36b_arityInPoly)
]
