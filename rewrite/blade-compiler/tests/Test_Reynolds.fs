module Blade.Tests.Reynolds

let test27_reynoldsSymmetric = """
// Reynolds combinator wraps a non-commutative kernel
// g(x,y) = x/y is NOT commutative, but reynolds(g) computes g(x,y) + g(y,x)
// Provides 2x speedup from triangular iteration on output
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let g = lambda(x, y) -> x / y
let result = L <@> reynolds(g)
"""

let test28_reynoldsAntisymmetric = """
// Reynolds with Antisymmetric computes g(x,y) - g(y,x)
// Result is antisymmetric: f(x,y) = -f(y,x)
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let g = lambda(x, y) -> x / y
let result = L <@> reynolds(g, Antisymmetric)
"""

let test29_reynoldsPlusIdentity = """
// Reynolds + identical arrays gives (n!)^2 speedup
// g(x,y) = x^y is NOT commutative, reynolds(g) computes x^y + y^x
// Same array twice: 2x from identity x 2x from Reynolds = 4x
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> x^y
let result = L <@> reynolds(g)
"""

let test30_reynoldsThreeWay = """
// Reynolds with 3 parameters: 3! = 6x speedup
// g(x,y,z) = x^2*y*z is NOT commutative
// reynolds(g) sums over all 6 permutations of S_3
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let C = [7.0, 8.0, 9.0]
let L = method_for(A, B, C)
let g = lambda(x, y, z) -> x * x * y * z
let result = L <@> reynolds(g)
"""

/// Reynolds operator tests
let reynoldsTests = [
    ("Reynolds Symmetric", test27_reynoldsSymmetric)
    ("Reynolds Antisymmetric", test28_reynoldsAntisymmetric)
    ("Reynolds Plus Identity", test29_reynoldsPlusIdentity)
    ("Reynolds Three-Way", test30_reynoldsThreeWay)
]
