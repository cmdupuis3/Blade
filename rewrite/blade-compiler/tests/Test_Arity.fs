module Blade.Tests.Arity

let test31_polyType = """
// Poly<T^r> type parsing
function comoment(args: Poly<T^0>)
where comm(args)
-> T^0
= args[0]
"""

let test32_rankIntrinsic = """
// rank() intrinsic
let A = [[1.0, 2.0], [3.0, 4.0]]
let r = rank(A)
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

/// Arity polymorphism tests
let arityTests = [
    ("Poly Type", test31_polyType)
    ("Rank Intrinsic", test32_rankIntrinsic)
    ("Arity Keyword", test34_arityKeyword)
    ("Multi Poly Arity", test34b_multiPolyArity)
    ("Arity Return Type", test35_arityReturnType)
]
