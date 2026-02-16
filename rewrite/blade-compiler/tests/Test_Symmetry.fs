module Blade.Tests.Symmetry

let test8_triangularIteration = """
// Same array used twice with commutative kernel - enables triangular iteration
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f |> compute
// With comm(x,y), symmetric 5x5 matrix stored as left-justified triangular
// Row-major order: row0=[1,2,3,4,5], row1=[4,6,8,10], row2=[9,12,15], row3=[16,20], row4=[25]
// EXPECT: result = [1.0, 2.0, 3.0, 4.0, 5.0, 4.0, 6.0, 8.0, 10.0, 9.0, 12.0, 15.0, 16.0, 20.0, 25.0]
"""

let test17_symmetryDemonstration = """
// Case 1: Different arrays, no symmetry -> speedup = 1
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L1 = method_for(A, B)
let f1 = lambda(x, y) -> x * y
let r1 = L1 <@> f1

// Case 2: Same array twice WITH comm -> triangular iteration, speedup = 2
let C = [1.0, 2.0, 3.0]
let L2 = method_for(C, C)
let f2 = lambda(x, y) where comm(x, y) -> x * y
let r2 = L2 <@> f2

// Case 3: Same array three times with comm (gives 6x speedup)
let D = [1.0, 2.0, 3.0]
let L3 = method_for(D, D, D)
let f3 = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let r3 = L3 <@> f3
"""

let test36_outputTypeDeduction = """
// Output type should be SymIdx when comm + same array
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
// result should have type Array<Float like SymIdx<2, 3>>
"""

let test37_outputTypeDifferentArrays = """
// Output type should be Idx, Idx when different arrays (no symmetry)
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
// result should have type Array<Float like Idx<3>, Idx<3>>
"""

let test38_outputTypeThreeWay = """
// Output type for 3-way same array: SymIdx<3, n>
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A, A)
let f = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let result = L <@> f
// result should have type Array<Float like SymIdx<3, 3>>
"""

let test39_outputTypeMixed = """
// Mixed: (A, A, B) -> SymIdx<2, n>, Idx<n>
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, A, B)
let f = lambda(x, y, z) where comm(x, y, z) -> x * y * z
let result = L <@> f
// result should have type Array<Float like SymIdx<2, 3>, Idx<3>>
"""

let test40_outputTypeNoComm = """
// Without comm: Idx, Idx even with same array
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) -> x * y
let result = L <@> f
// result should have type Array<Float like Idx<3>, Idx<3>>
"""

let test41_partialComm = """
// Partial comm: only (x,y) are commutative, not z
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, A, B)
let f = lambda(x, y, z) where comm(x, y) -> x * y * z
let result = L <@> f
// result: SymIdx<2, 3> for A,A; Idx<3> for B (z not in comm group)
"""

let test42_distinctCommGroups = """
// Two distinct comm groups
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, A, B, B)
let f = lambda(x, y, z, w) where comm(x, y), comm(z, w) -> x * y * z * w
let result = L <@> f
// result: SymIdx<2, 3> for A,A; SymIdx<2, 3> for B,B
"""

/// Symmetry and triangular iteration
let symmetryTests = [
    ("Triangular Iteration", test8_triangularIteration)
    ("Symmetry Demonstration", test17_symmetryDemonstration)
    ("Output Type: Same Array + Comm", test36_outputTypeDeduction)
    ("Output Type: Different Arrays", test37_outputTypeDifferentArrays)
    ("Output Type: Three-Way Same", test38_outputTypeThreeWay)
    ("Output Type: Mixed (A,A,B)", test39_outputTypeMixed)
    ("Output Type: No Comm", test40_outputTypeNoComm)
    ("Output Type: Partial Comm", test41_partialComm)
    ("Output Type: Distinct Comm Groups", test42_distinctCommGroups)
]
