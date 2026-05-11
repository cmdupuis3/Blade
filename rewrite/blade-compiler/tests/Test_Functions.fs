module Blade.Tests.Functions

let test13_functionDecl = """
function add(a: Float64, b: Float64) -> Float64 = a + b
let r = add(3.0, 4.0)
// EXPECT: r = 7
"""

let test14_functionWithWhere = """
// Function with array parameters and a where clause. The body produces
// a rank-2 tensor M[i][j] = A[i] * B[j], so the declared return type
// matches that shape (Idx<n>, Idx<n>). The where clause asserts
// `comm(A, B)`, but since A and B are distinct arrays the comm doesn't
// fold into output symmetry — the output is full Idx, Idx, not SymIdx
// (per Test_Symmetry test37). The point of this test is exercising
// where-clause parsing alongside array-typed params.
function covariance(A: Array<Float64 like Idx<n>>, B: Array<Float64 like Idx<n>>) 
  where comm(A, B) -> Array<Float64 like Idx<n>, Idx<n>> = 
  method_for(A, B) <@> lambda(x, y) -> x * y |> compute
"""

let test23_kernelWithTypes = """
// Kernel with explicit param types — exercises inline annotation syntax
// on lambda params. Body produces rank-2 outer product M[i][j] = a[i] * b[j],
// hence the rank-2 return type.
function vectorDot(a: Array<Float64 like Idx<n>>, b: Array<Float64 like Idx<n>>)
  -> Array<Float64 like Idx<n>, Idx<n>> = 
  method_for(a, b) <@> lambda(x: Float64, y: Float64) -> x * y |> compute
"""

let test24_nonScalarKernel = """
// Kernel that operates on 1D slices (irank = 1 for each input)
let matrix = [[1.0, 2.0], [3.0, 4.0]]
let O = object_for(lambda(row: Array<Float64 like Idx<2>>) -> row)
"""

let test10_scalarCaptureInKernel = """
// Scalars and arrays can be captured by lambdas
let scale = 2.5
let offset = 1.0
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let f = lambda(x, y) -> (x + y) * scale + offset
let result = L <@> f |> compute
// result[i][j] = (A[i]+B[j])*2.5 + 1
// EXPECT: result = [13.5, 16, 18.5, 16, 18.5, 21, 18.5, 21, 23.5]
"""

let test25_functionCapture = """
// Lambdas CAN capture functions from environment
// Scalar params inferred, return type inferred
function square(x) = x * x
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> square(x) + square(y)
let result = L <@> f |> compute
// EXPECT: result = [2, 5, 10, 8, 13, 18]
"""

let test26_arrayCaptureRejected = """
// Array captures in lambdas are now allowed (restriction removed for SQL-like features)
// This test is retained as a historical note; see Test_Sqlish.fs for capture tests
let A = [1.0, 2.0, 3.0]
let bad = lambda(x) -> x + A
"""

let test51_functionCallResult = """
// Function declaration + call with verified result
function add(a: Float64, b: Float64) -> Float64 = a + b
function square(x: Float64) -> Float64 = x * x
let r1 = add(3.0, 4.0)
let r2 = square(5.0)
let r3 = add(square(2.0), square(3.0))
// EXPECT: r1 = 7
// EXPECT: r2 = 25
// EXPECT: r3 = 13
"""

// ============================================================================
// HM polymorphism × arity-scheme interaction probes
// ============================================================================
//
// Blade uses F#/OCaml-style implicit Hindley-Milner polymorphism: type
// variables (uppercase identifiers like `T`, `U`) and extent variables
// (lowercase like `n`, `m`) appear in signatures without explicit binding,
// and are unified across positions. These probes exercise the specific
// interactions between HM type-var unification and Blade's arity machinery
// (Poly packs, like-clauses, Reynolds combinators) that the language's
// design separates into independent universes per Moggi 2000.
// ============================================================================

let test_hm_cross_position = """
// HM unification: T in param and T in return refer to the same type
// variable (cross-position unification, F#-style implicit polymorphism).
function arr_id(a: Array<T like Idx<n>>) -> Array<T like Idx<n>> = a
let xs: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]
let result = arr_id(xs)
let r = result(0)
// EXPECT: r = 1
"""

let test_hm_two_type_vars = """
// HM independence: distinct type variables T and U in the same signature
// are independent — they don't get unified just because both appear as
// element types. Calling with Float64 and Int64 arrays exercises this.
function takeFirst(a: Array<T like Idx<n>>, b: Array<U like Idx<n>>) -> T = a(0)
let xs: Array<Float64 like Idx<2>> = [10.0, 20.0]
let ys: Array<Int64 like Idx<2>> = [3, 7]
let r = takeFirst(xs, ys)
// EXPECT: r = 10
"""

let test_hm_lambda_capture = """
// HM type-var capture: a lambda inside a polymorphic function captures
// a value of type T from the enclosing scope and uses it in the kernel
// body. Tests that the type variable threads through method_for and the
// lambda's parameter environment correctly.
function applyVal(arr: Array<T like Idx<n>>, v: T) -> Array<T like Idx<n>>
= method_for(arr) <@> lambda(x) -> v |> compute
let arr: Array<Float64 like Idx<3>> = [1.0, 2.0, 3.0]
let result = applyVal(arr, 42.0)
let r = result(0)
// EXPECT: r = 42
"""

let test_hm_reynolds_kernel = """
// HM through Reynolds: a polymorphic kernel's type information must
// flow through the reynolds combinator to the loop machinery. The
// kernel `lambda(x, y) -> x - y` is antisymmetric in its arguments, so
// reynolds(kern) computes kern(x,y) + kern(y,x) = 0 for all (x,y).
// Every output entry is zero.
let A: Array<Float64 like Idx<2>> = [3.0, 5.0]
let B: Array<Float64 like Idx<2>> = [1.0, 2.0]
let kern = lambda(x, y) -> x - y
let result = method_for(A, B) <@> reynolds(kern) |> compute
let r = result(0, 0)
// EXPECT: r = 0
"""

// ---- Scalar HM tests: the simplest possible polymorphism. These exercise
// implicit type-variable introduction (bare `T` in type position) without
// involving arrays, kernels, or capture — useful as baseline checks that
// the F#/OCaml-style implicit polymorphism is recognized by the parser
// and propagated through unification at scalar level.

let test_hm_scalar_id = """
// Scalar polymorphic identity: the simplest HM test. `T` appears bare
// in param and return; should be inferred as a type variable.
function id(x: T) -> T = x
let r = id(42)
// EXPECT: r = 42
"""

let test_hm_scalar_two_vars = """
// Two distinct scalar type vars: confirms `T` and `U` are independent
// type variables (not unified to a single type) when given different
// concrete arguments at the call site.
function constFirst(x: T, y: U) -> T = x
let r = constFirst(99, 1.5)
// EXPECT: r = 99
"""

let test_hm_scalar_conditional = """
// Polymorphic conditional return: tests that if-then-else preserves the
// type variable through both branches, and that the branches unify to
// a single T at the return site.
function pick(x: T, y: T, b: Bool) -> T = if b then x else y
let r = pick(7, 13, True)
// EXPECT: r = 7
"""

let test_hm_scalar_compose = """
// Polymorphic composition: two polymorphic functions composed. The outer
// function's T flows through both inner calls; each call instantiates
// the inner T independently (let-polymorphism), but in this case all
// instantiations resolve to the same concrete type.
function id(x: T) -> T = x
function twiceId(x: T) -> T = id(id(x))
let r = twiceId(100)
// EXPECT: r = 100
"""

// ---- Mixed Poly + HM: Poly<T^N> arity-polymorphism interacting with free
// type variables. These exercise the unified specialization story — the
// architecture must produce one concrete C++ function per distinct
// (arity × type-binding) pattern at call sites.
//
// KNOWN-FAILING (as of this writing): both tests below currently fail
// because the Poly stage's call-site arity detection (monomorphizeModule
// in IR.fs, `let arity = args.Length`) treats every positional arg as a
// pack element, not just the args destined for the Poly param. So
// `f((10, 20, 30), 5)` arrives at Poly with arity=2 (total args), the
// pack expands to 2 sub-params, and the resulting f_arity2 has 3 params
// (pack_0, pack_1, scalar) while the call has 2 args — a shape mismatch
// that HM downstream can't repair. Fixing this requires teaching Poly to
// (a) recognize which positional args correspond to Poly params vs free
// params, and (b) accept a tuple expression in a Poly position and
// flatten it into individual pack elements at the call site. Until that
// design lands, these tests document the intended Poly+HM semantics but
// don't pass; treat them as forward-looking specs.

let test_hm_poly_same_type = """
// Same-T mix: the type variable inside the Poly pack and the free scalar
// share the same name T, so they unify to a single concrete type at the
// call site. The function picks the first element of the pack (typed T)
// and adds the scalar (also T). The pack is passed as a tuple literal;
// the scalar follows as a separate argument.
function f(pack: Poly<T^0>, scalar: T) -> T = pack[0] + scalar
let r = f((10, 20, 30), 5)
// EXPECT: r = 15
"""

let test_hm_poly_mixed_types = """
// Mixed-type mix: Poly pack typed T, free scalar typed U. T and U are
// distinct type variables that must specialize independently — T to the
// pack element type (Float64 here from the literals), U to the scalar's
// concrete type (Int64). The function returns the scalar (typed U), so
// the result type comes from U alone. The pack is passed as a tuple
// literal; the scalar follows as a separate argument.
function g(pack: Poly<T^0>, scalar: U) -> U = scalar
let r = g((1.0, 2.0, 3.0), 42)
// EXPECT: r = 42
"""

/// Functions and captures
let functionTests = [
    ("Function Declaration", test13_functionDecl)
    ("Function With Where Clause", test14_functionWithWhere)
    ("Kernel With Types", test23_kernelWithTypes)
    ("Non-Scalar Kernel", test24_nonScalarKernel)
    ("Scalar Capture In Kernel", test10_scalarCaptureInKernel)
    ("Function Capture", test25_functionCapture)
    ("Function Call Result", test51_functionCallResult)
    ("HM Cross-Position Unification", test_hm_cross_position)
    ("HM Two Independent Type Vars", test_hm_two_type_vars)
    ("HM Type Var Capture In Lambda", test_hm_lambda_capture)
    ("HM Polymorphic Kernel Through Reynolds", test_hm_reynolds_kernel)
    ("HM Scalar Identity", test_hm_scalar_id)
    ("HM Scalar Two Type Vars", test_hm_scalar_two_vars)
    ("HM Scalar Conditional Return", test_hm_scalar_conditional)
    ("HM Scalar Compose Polymorphic", test_hm_scalar_compose)
    ("HM Poly+HM Same Type Var", test_hm_poly_same_type)
    ("HM Poly+HM Mixed Type Vars", test_hm_poly_mixed_types)
]
