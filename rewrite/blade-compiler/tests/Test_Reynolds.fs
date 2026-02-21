module Blade.Tests.Reynolds

// ============================================================================
// Reynolds Symmetric 2D — Two different arrays
// ============================================================================
// g(x,y) = 2*x + y is NOT commutative
// reynolds(g)(x,y) = g(x,y) + g(y,x) = (2x+y) + (2y+x) = 3x + 3y
// A=[1,2], B=[3,4]: output[i][j] = 3*A[i] + 3*B[j]
// [0][0]=3+9=12, [0][1]=3+12=15, [1][0]=6+9=15, [1][1]=6+12=18
let test27_reynolds2dSym = """
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let L = method_for(A, B)
let g = lambda(x, y) -> 2.0 * x + y
let result = L <@> reynolds(g) |> compute
// EXPECT: result = [12.0, 15.0, 15.0, 18.0]
"""

// ============================================================================
// Reynolds Antisymmetric 2D — Two different arrays
// ============================================================================
// reynolds(g, Anti)(x,y) = g(x,y) - g(y,x) = (2x+y) - (2y+x) = x - y
// [0][0]=1-3=-2, [0][1]=1-4=-3, [1][0]=2-3=-1, [1][1]=2-4=-2
let test28_reynolds2dAnti = """
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let L = method_for(A, B)
let g = lambda(x, y) -> 2.0 * x + y
let result = L <@> reynolds(g, Antisymmetric) |> compute
// EXPECT: result = [-2.0, -3.0, -1.0, -2.0]
"""

// ============================================================================
// Reynolds Symmetric 3D — Three different arrays
// ============================================================================
// g(x,y,z) = x + 2*y + 3*z
// S_3 has 6 permutations. Sum of coefficients for each variable:
//   x gets: 1+1+2+3+2+3 = 12    (appears as x,x,y,z,y,z across permutations)
//   y gets: 2+3+1+1+3+2 = 12
//   z gets: 3+2+3+2+1+1 = 12
// reynolds(g)(x,y,z) = 12x + 12y + 12z = 12(x+y+z)
// A=[1,2], B=[3,4], C=[5,6]
// [0][0][0]=12*(1+3+5)=108, [0][0][1]=12*(1+3+6)=120
// [0][1][0]=12*(1+4+5)=120, [0][1][1]=12*(1+4+6)=132
// [1][0][0]=12*(2+3+5)=120, [1][0][1]=12*(2+3+6)=132
// [1][1][0]=12*(2+4+5)=132, [1][1][1]=12*(2+4+6)=144
let test29_reynolds3dSym = """
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let C = [5.0, 6.0]
let L = method_for(A, B, C)
let g = lambda(x, y, z) -> x + 2.0 * y + 3.0 * z
let result = L <@> reynolds(g) |> compute
// EXPECT: result = [108.0, 120.0, 120.0, 132.0, 120.0, 132.0, 132.0, 144.0]
"""

// ============================================================================
// Reynolds Symmetric 2D — Same array (identity + Reynolds)
// ============================================================================
// g(x,y) = 2*x + y, comm(x,y) for identity optimization
// reynolds(g)(x,y) = (2x+y) + (2y+x) = 3(x+y)
// A=[1,2,3], stored triangularly: (0,0)=6, (0,1)=9, (0,2)=12, (1,1)=12, (1,2)=15, (2,2)=18
let test30_reynolds2dSymIdentity = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> 2.0 * x + y
let result = L <@> reynolds(g) |> compute
// EXPECT: result = [6.0, 9.0, 12.0, 12.0, 15.0, 18.0]
"""

// ============================================================================
// Reynolds with commutative kernel (sanity check)
// ============================================================================
// g(x,y) = x + y is already commutative
// reynolds(g)(x,y) = g(x,y) + g(y,x) = (x+y) + (y+x) = 2*(x+y)
// So Reynolds doubles the result of a commutative kernel
// A=[1,2], B=[3,4]: out[i][j] = 2*(A[i]+B[j])
// [0][0]=8, [0][1]=10, [1][0]=10, [1][1]=12
let test31_reynoldsCommutative = """
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let L = method_for(A, B)
let g = lambda(x, y) -> x + y
let result = L <@> reynolds(g) |> compute
// EXPECT: result = [8.0, 10.0, 10.0, 12.0]
"""

/// Reynolds operator tests
let reynoldsTests = [
    ("Reynolds 2D Symmetric", test27_reynolds2dSym)
    ("Reynolds 2D Antisymmetric", test28_reynolds2dAnti)
    ("Reynolds 3D Symmetric", test29_reynolds3dSym)
    ("Reynolds 2D Sym Identity", test30_reynolds2dSymIdentity)
    ("Reynolds Commutative", test31_reynoldsCommutative)
]
