// Blade-DSL Compiler Main Entry Point
// Test driver for the compiler pipeline

module Blade.Main

open System
open Blade.Ast
open Blade.Lexer
open Blade.Parser
open Blade.IR
open Blade.TypedAst
open Blade.TypeCheck
open Blade.Lowering
open Blade.CodeGen
open Blade.NetcdfProvider

// ============================================================================
// Test Utilities
// ============================================================================

let printHeader title =
    printfn "\n%s" (String.replicate 70 "=")
    printfn "  %s" title
    printfn "%s\n" (String.replicate 70 "=")

let printSubHeader title =
    printfn "\n--- %s ---\n" title

let testParse source =
    printfn "Source:\n%s\n" source
    match parseProgram source with
    | Ok program ->
        printfn "Parse: OK (%d modules)" program.Modules.Length
        Ok program
    | Error e ->
        printfn "Parse: ERROR at %d:%d - %s" e.Line e.Col e.Message
        Error e.Message

let testLower source =
    match lower source with
    | Ok ir ->
        printfn "Lower: OK"
        for m in ir.Modules do
            printfn "  Module: %s" m.Name
            printfn "  Functions: %d" m.Functions.Length
            printfn "  Bindings: %d" m.Bindings.Length
            
            // Build name context from all bindings
            let mutable names = Map.empty
            for f in m.Functions do
                printfn "    function %s" f.Name
                names <- Map.add f.Id f.Name names
            for b in m.Bindings do
                names <- Map.add b.Id b.Name names
            
            // Print bindings with name context
            for b in m.Bindings do
                printfn "    let %s = %s" b.Name (ppIRExprWithNames names 0 b.Value)
        Ok ir
    | Error e ->
        printfn "Lower: ERROR - %s" e
        Error e

/// Test type checking phase only
let testTypeCheck source =
    printfn "Source:\n%s\n" source
    match parseProgram source with
    | Ok program ->
        printfn "Parse: OK"
        match typeCheck program with
        | Ok typedProgram ->
            printfn "TypeCheck: OK (%d modules)" typedProgram.Modules.Length
            for m in typedProgram.Modules do
                let moduleName = m.Name |> Option.map (String.concat ".") |> Option.defaultValue "<anonymous>"
                printfn "  Module: %s" moduleName
                printfn "  Declarations: %d" m.Decls.Length
                for decl in m.Decls do
                    match decl with
                    | TDeclLet binding ->
                        printfn "    let %s : %s" binding.Name (ppIRType binding.Type)
                    | _ -> ()
            Ok typedProgram
        | Error e ->
            let msg = 
                match e with
                | UnboundVariable name -> sprintf "Unbound variable: %s" name
                | TypeMismatch (exp, act) -> sprintf "Type mismatch: expected %A, got %A" exp act
                | ArityMismatch (exp, act) -> sprintf "Arity mismatch: expected %d, got %d" exp act
                | InvalidArrayCapture name -> sprintf "Lambda cannot capture array '%s'" name
                | InvalidApplication ty -> sprintf "Cannot apply non-function type: %A" ty
                | PatternTypeMismatch (pat, ty) -> sprintf "Pattern %s doesn't match type %A" pat ty
                | Other msg -> msg
            printfn "TypeCheck: ERROR - %s" msg
            Error msg
    | Error e ->
        printfn "Parse: ERROR at %d:%d - %s" e.Line e.Col e.Message
        Error e.Message

/// Test new pipeline: Parse -> TypeCheck -> Lower
let testLowerWithTypeCheck source =
    printfn "Source:\n%s\n" source
    match lowerWithTypeCheck source with
    | Ok ir ->
        printfn "Pipeline (Parse → TypeCheck → Lower): OK"
        for m in ir.Modules do
            printfn "  Module: %s" m.Name
            printfn "  Bindings: %d" m.Bindings.Length
            
            let names = m.Bindings |> List.fold (fun acc b -> Map.add b.Id b.Name acc) Map.empty
            for b in m.Bindings do
                printfn "    let %s = %s" b.Name (ppIRExprWithNames names 0 b.Value)
        Ok ir
    | Error e ->
        printfn "Pipeline: ERROR - %s" e
        Error e

/// Compare old and new pipelines
let testComparePipelines source =
    printfn "Source:\n%s\n" source
    printfn "--- Old Pipeline (Parse → Lower) ---"
    let oldResult = lower source
    printfn "--- New Pipeline (Parse → TypeCheck → Lower) ---"
    let newResult = lowerWithTypeCheck source
    
    match oldResult, newResult with
    | Ok oldIR, Ok newIR ->
        printfn "\nBoth pipelines succeeded."
        printfn "Old: %d bindings" (oldIR.Modules |> List.sumBy (fun m -> m.Bindings.Length))
        printfn "New: %d bindings" (newIR.Modules |> List.sumBy (fun m -> m.Bindings.Length))
        Ok (oldIR, newIR)
    | Error oldErr, Ok _ ->
        printfn "\nOld pipeline failed, new succeeded."
        printfn "Old error: %s" oldErr
        Error oldErr
    | Ok _, Error newErr ->
        printfn "\nOld pipeline succeeded, new failed."
        printfn "New error: %s" newErr
        Error newErr
    | Error oldErr, Error newErr ->
        printfn "\nBoth pipelines failed."
        printfn "Old error: %s" oldErr
        printfn "New error: %s" newErr
        Error oldErr

// ============================================================================
// Test Cases
// ============================================================================

let test1_basicExpr = """
let x = 1 + 2 * 3
"""

let test2_lambda = """
let f = lambda(a, b) -> a + b
"""

let test3_ifThenElse = """
let result = if true then 42 else 0
"""

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

let test7_arrayLit = """
let arr1d = [1.0, 2.0, 3.0]
let arr2d = [[1.0, 2.0], [3.0, 4.0]]
"""

let test8_triangularIteration = """
// Same array used twice with commutative kernel - enables triangular iteration
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
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

let test10_scalarCaptureInKernel = """
// Scalars CAN be captured by lambdas (only arrays are forbidden)
let scale = 2.5
let offset = 1.0
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let L = method_for(A, B)
let f = lambda(x, y) -> (x + y) * scale + offset
let result = L <@> f
"""

let test11_objectForWithArrays = """
let kernel = lambda(a, b) -> a * b + 1.0
let O = object_for(kernel)
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
"""

let test12_nestedArray = """
let matrix = [[1.0, 2.0, 3.0], [4.0, 5.0, 6.0], [7.0, 8.0, 9.0]]
"""

let test13_functionDecl = """
function add(a: Float64, b: Float64) -> Float64 = a + b
"""

let test14_functionWithWhere = """
function covariance(A: Array<Float64 like Idx<n>>, B: Array<Float64 like Idx<n>>) 
  where comm(A, B) -> Float64 = {
  let L = method_for(A, B)
  let f = lambda(x, y) -> x * y
  L <@> f
}
"""

let test15_precedenceTest = """
// Test that * binds tighter than +
let x = 1 + 2 * 3
let y = (1 + 2) * 3
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

let test18_matchExpr = """
let x = 5
let result = match x with
  | 0 -> 0
  | 1 -> 1
  | n -> n * 2
"""

let test19_matchWithGuard = """
let x = 10
let result = match x with
  | n if n > 5 -> n * 2
  | n if n > 0 -> n
  | _ -> 0
"""

let test20_tupleDestructure = """
let pair = (1, 2)
let (a, b) = pair
let sum = a + b
"""

let test21_compute = """
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) -> x * y
let result = L <@> f |> compute
"""

let test22_pureAndBind = """
let x = pure(42)
let f = lambda(n) -> n * 2
let result = x >>= f
"""

let test23_kernelWithTypes = """
// Kernel with explicit types to test irank inference
function vectorDot(a: Array<Float64 like Idx<n>>, b: Array<Float64 like Idx<n>>) -> Float64 = {
  let L = method_for(a, b)
  let mult = lambda(x: Float64, y: Float64) -> x * y
  L <@> mult |> compute
}
"""

let test24_nonScalarKernel = """
// Kernel that operates on 1D slices (irank = 1 for each input)
let matrix = [[1.0, 2.0], [3.0, 4.0]]
let O = object_for(lambda(row: Array<Float64 like Idx<2>>) -> row)
"""

let test25_functionCapture = """
// Lambdas CAN capture functions from environment
// Scalar params inferred, return type inferred
function square(x) = x * x
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> square(x) + square(y)
let result = L <@> f
"""

let test26_arrayCaptureRejected = """
// Lambdas CANNOT capture arrays - this should fail
let A = [1.0, 2.0, 3.0]
let bad = lambda(x) -> x + A
"""

let test27_reynoldsSymmetric = """
// Reynolds combinator wraps a non-commutative kernel
// g(x,y) = x/y is NOT commutative, but reynolds(g) computes g(x,y) + g(y,x)
// Provides 2× speedup from triangular iteration on output
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
// Reynolds + identical arrays gives (n!)² speedup
// g(x,y) = x^y is NOT commutative, reynolds(g) computes x^y + y^x
// Same array twice: 2× from identity × 2× from Reynolds = 4×
let A = [1.0, 2.0, 3.0]
let L = method_for(A, A)
let g = lambda(x, y) where comm(x, y) -> x^y
let result = L <@> reynolds(g)
"""

let test30_reynoldsThreeWay = """
// Reynolds with 3 parameters: 3! = 6× speedup
// g(x,y,z) = x²yz is NOT commutative
// reynolds(g) sums over all 6 permutations of S₃
let A = [1.0, 2.0, 3.0]
let B = [4.0, 5.0, 6.0]
let C = [7.0, 8.0, 9.0]
let L = method_for(A, B, C)
let g = lambda(x, y, z) -> x * x * y * z
let result = L <@> reynolds(g)
"""

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

let test33_consDestructure = """
// :: destructuring pattern
let t = (1, 2, 3)
let head :: tail = t
"""

let test34_arityKeyword = """
// arity keyword inside Poly function
function sumPoly(args: Poly<T^0>) -> T^0
= if arity == 0 then 0 else args[0]
"""

let test35_arityReturnType = """
// T^arity in return type
function identity(args: Poly<T^1>) -> T^arity
= args[0]
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

let test26_arrayCaptureRejected_orig = """
// Lambdas CANNOT capture arrays - this should fail
let A = [1.0, 2.0, 3.0]
let bad = lambda(x) -> x + A
"""

// ============================================================================
// Struct Tests
// ============================================================================

let test45_structDecl = """
// Basic struct declaration and construction
struct Point {
    x: Float64,
    y: Float64
}
let p = Point { x = 1.0, y = 2.0 }
"""

let test46_structFieldAccess = """
// Struct field access
struct Vector3 {
    x: Float64,
    y: Float64,
    z: Float64
}
let v = Vector3 { x = 1.0, y = 2.0, z = 3.0 }
let sum = v.x + v.y + v.z
"""

let test47_structPattern = """
// Struct destructuring in pattern
struct Pair {
    first: Int,
    second: Int
}
let p = Pair { first = 10, second = 20 }
let Pair { first, second } = p
let total = first + second
"""

// ============================================================================
// Sum Type Tests
// ============================================================================

let test48_sumTypeSimple = """
// Simple sum type (enum-like)
type Direction = North | South | East | West
let d = North
"""

let test49_sumTypeWithData = """
// Sum type with payload
type Option = Some : Int | None
let x = Some(42)
let y = None
"""

let test50_sumTypeMatch = """
// Pattern matching on sum type
type Result = Ok : Int | Err : String
let r = Ok(100)
let value = match r with
    | Ok(n) -> n
    | Err(msg) -> 0
"""

// ============================================================================
// Interface and Impl Tests
// ============================================================================

let test51_interfaceDecl = """
// Interface declaration
interface Measurable {
    function area(self) -> Float64
}
struct Circle {
    radius: Float64
}
"""

let test52_implDecl = """
// Interface implementation
interface Scalable {
    function scale(self, factor: Float64) -> Float64
}
struct Box {
    width: Float64,
    height: Float64
}
impl Scalable for Box {
    function scale(self, factor: Float64) -> Float64 = self.width * self.height * factor
}
"""

// ============================================================================
// Module Tests
// ============================================================================

let test53_moduleDecl = """
module Math.Geometry

let pi = 3.14159
function circleArea(r: Float64) -> Float64 = pi * r * r
"""

let test54_moduleWithImport = """
module Main

let x = 42
let y = x * 2
"""

// ============================================================================
// Extended Guard Tests
// ============================================================================

let test55_guardWithAnd = """
// Guard with && operator
let x = 15
let result = match x with
    | n if n > 10 && n < 20 -> n
    | _ -> 0
"""

let test56_guardWithOr = """
// Guard with || operator
let x = 5
let result = match x with
    | n if n < 0 || n > 100 -> 0
    | n -> n * 2
"""

let test57_guardComplex = """
// Complex guard with multiple conditions
let x = 50
let y = 30
let result = match (x, y) with
    | (a, b) if a > 0 && b > 0 && a + b < 100 -> a + b
    | (a, b) if a < 0 || b < 0 -> 0
    | _ -> 999
"""

let test58_guardNested = """
// Nested match with guards
let x = 10
let y = 20
let outer = match x with
    | n if n > 5 -> match y with
        | m if m > 15 -> n + m
        | _ -> n
    | _ -> 0
"""

// ============================================================================
// Bracketed (Outer Product) Operator Tests
// ============================================================================

let test59_bracketedArithmetic = """
// Bracketed arithmetic operators for outer product
let A = [1.0, 2.0, 3.0]
let B = [10.0, 20.0]
let added = A [+] B
let multiplied = A [*] B
let powered = A [^] B
"""

let test60_bracketedComparison = """
// Bracketed comparison operators
let A = [1, 2, 3, 4, 5]
let B = [3, 3, 3]
let less_than = A [<] B
let equal = A [==] B
let greater_eq = A [>=] B
"""

let test61_bracketedLogical = """
// Bracketed logical operators
let P = [true, false, true]
let Q = [true, true, false]
let and_result = P [&&] Q
let or_result = P [||] Q
"""

let test62_bracketedMixed = """
// Mixed bracketed and elementwise in same expression
let A = [1.0, 2.0]
let B = [3.0, 4.0]
let C = [5.0, 6.0]
// (A [*] B) gives outer product, then + C would need broadcasting
let outer = A [*] B
let elem = A + B
"""

// ============================================================================
// Test Categories
// ============================================================================

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
]

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

/// Reynolds operator tests
let reynoldsTests = [
    ("Reynolds Symmetric", test27_reynoldsSymmetric)
    ("Reynolds Antisymmetric", test28_reynoldsAntisymmetric)
    ("Reynolds Plus Identity", test29_reynoldsPlusIdentity)
    ("Reynolds Three-Way", test30_reynoldsThreeWay)
]

/// Arity polymorphism tests
let arityTests = [
    ("Poly Type", test31_polyType)
    ("Rank Intrinsic", test32_rankIntrinsic)
    ("Arity Keyword", test34_arityKeyword)
    ("Arity Return Type", test35_arityReturnType)
]

/// Functions and captures
let functionTests = [
    ("Function Declaration", test13_functionDecl)
    ("Function With Where Clause", test14_functionWithWhere)
    ("Kernel With Types", test23_kernelWithTypes)
    ("Non-Scalar Kernel", test24_nonScalarKernel)
    ("Scalar Capture In Kernel", test10_scalarCaptureInKernel)
    ("Function Capture", test25_functionCapture)
]

/// Struct tests
let structTests = [
    ("Struct Declaration", test45_structDecl)
    ("Struct Field Access", test46_structFieldAccess)
    ("Struct Pattern", test47_structPattern)
]

/// Sum type tests
let sumTypeTests = [
    ("Sum Type Simple", test48_sumTypeSimple)
    ("Sum Type With Data", test49_sumTypeWithData)
    ("Sum Type Match", test50_sumTypeMatch)
]

/// Interface and impl tests
let interfaceTests = [
    ("Interface Declaration", test51_interfaceDecl)
    ("Impl Declaration", test52_implDecl)
]

/// Module tests
let moduleTests = [
    ("Module Declaration", test53_moduleDecl)
    ("Module With Declarations", test54_moduleWithImport)
]

/// Extended guard tests
let guardTests = [
    ("Guard With &&", test55_guardWithAnd)
    ("Guard With ||", test56_guardWithOr)
    ("Guard Complex", test57_guardComplex)
    ("Guard Nested", test58_guardNested)
]

/// Bracketed (outer product) operator tests
let bracketedTests = [
    ("Bracketed Arithmetic", test59_bracketedArithmetic)
    ("Bracketed Comparison", test60_bracketedComparison)
    ("Bracketed Logical", test61_bracketedLogical)
    ("Bracketed Mixed", test62_bracketedMixed)
]

/// All tests combined
let allTests = 
    basicTests @ loopTests @ symmetryTests @ reynoldsTests @ arityTests @ functionTests 
    @ structTests @ sumTypeTests @ interfaceTests @ moduleTests @ guardTests @ bracketedTests

// ============================================================================
// Test Runner
// ============================================================================

let runTestCategory name tests =
    printHeader (sprintf "Blade-DSL: %s Tests" name)
    printfn "Running %d tests...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testLower source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run tests using the new type checking pipeline
let runTestCategoryWithTypeCheck name tests =
    printHeader (sprintf "Blade-DSL: %s Tests (TypeCheck Pipeline)" name)
    printfn "Running %d tests with Parse → TypeCheck → Lower pipeline...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testLowerWithTypeCheck source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Run type checking only (no lowering)
let runTypeCheckOnly name tests =
    printHeader (sprintf "Blade-DSL: %s Tests (TypeCheck Only)" name)
    printfn "Running %d tests through type checker only...\n" (List.length tests)
    
    let mutable passed = 0
    let mutable failed = 0
    
    for (testName, source) in tests do
        printSubHeader testName
        match testTypeCheck source with
        | Ok _ ->
            printfn "PASSED"
            passed <- passed + 1
        | Error _ ->
            printfn "FAILED"
            failed <- failed + 1
    
    printHeader "Test Summary"
    printfn "Passed: %d" passed
    printfn "Failed: %d" failed
    printfn "Total:  %d" (passed + failed)
    
    if failed > 0 then
        printfn "\nSome tests failed."
        1
    else
        printfn "\nAll tests passed!"
        0

/// Compare both pipelines on all tests
let runPipelineComparison () =
    printHeader "Pipeline Comparison: Old vs New"
    printfn "Comparing Parse→Lower vs Parse→TypeCheck→Lower...\n"
    
    let mutable bothPassed = 0
    let mutable oldOnly = 0
    let mutable newOnly = 0
    let mutable bothFailed = 0
    
    for (testName, source) in allTests do
        printSubHeader testName
        let oldResult = lower source
        let newResult = lowerWithTypeCheck source
        
        match oldResult, newResult with
        | Ok _, Ok _ ->
            printfn "BOTH PASSED"
            bothPassed <- bothPassed + 1
        | Ok _, Error e ->
            printfn "OLD PASSED, NEW FAILED: %s" e
            oldOnly <- oldOnly + 1
        | Error e, Ok _ ->
            printfn "OLD FAILED, NEW PASSED: %s" e
            newOnly <- newOnly + 1
        | Error _, Error _ ->
            printfn "BOTH FAILED"
            bothFailed <- bothFailed + 1
    
    printHeader "Comparison Summary"
    printfn "Both passed:      %d" bothPassed
    printfn "Old only passed:  %d" oldOnly
    printfn "New only passed:  %d" newOnly
    printfn "Both failed:      %d" bothFailed
    printfn "Total:            %d" (bothPassed + oldOnly + newOnly + bothFailed)
    
    if oldOnly > 0 then
        printfn "\nWARNING: New pipeline has regressions!"
        1
    else
        printfn "\nNew pipeline is compatible with old pipeline."
        0

let runAllTests () =
    runTestCategory "All" allTests

let runAllTestsWithTypeCheck () =
    runTestCategoryWithTypeCheck "All" allTests

// ============================================================================
// Specific Test for Symmetry Analysis
// ============================================================================

let runSymmetryTest () =
    printHeader "Symmetry Analysis Test"
    
    let source = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
"""
    
    printfn "Source:\n%s" source
    
    match lower source with
    | Ok ir ->
        for m in ir.Modules do
            // Build name context
            let names = m.Bindings |> List.fold (fun acc b -> Map.add b.Id b.Name acc) Map.empty
            
            for b in m.Bindings do
                printfn "\n%s =" b.Name
                printfn "  %s" (ppIRExprWithNames names 2 b.Value)
                
                // Extra debug for apply combinator
                match b.Value with
                | IRApplyCombinator info ->
                    printfn "\n  [DEBUG] Apply Combinator Details:"
                    printfn "    SDimsPerArray: %A" info.SDimsPerArray
                    printfn "    KernelInputRanks: %A" info.KernelInputRanks
                    printfn "    SymcomStates: %A" info.SymcomStates
                    printfn "    TriangularLevels: %A" info.TriangularLevels
                    printfn "    SpeedupFactor: %d" info.SpeedupFactor
                    
                    // Check kernel comm groups
                    match info.Kernel with
                    | IRLambda linfo ->
                        printfn "    Kernel CommGroups: %A" linfo.CommGroups
                        printfn "    Kernel IsCommutative: %b" linfo.IsCommutative
                    | _ -> ()
                    
                    // Check loop identities
                    match info.Loop with
                    | IRMethodFor mfInfo ->
                        printfn "    Loop Identities: %A" mfInfo.Identities
                        printfn "    Loop ArrayTypes count: %d" mfInfo.ArrayTypes.Length
                    | _ -> ()
                | _ -> ()
    | Error e ->
        printfn "Error: %s" e

// ============================================================================
// Test for C++ Code Generation
// ============================================================================

let runCodeGenTest () =
    printHeader "C++ Code Generation Test"
    
    let source = """
let A = [1.0, 2.0, 3.0, 4.0, 5.0]
let L = method_for(A, A)
let f = lambda(x, y) where comm(x, y) -> x * y
let result = L <@> f
"""
    
    printfn "Source:\n%s" source
    
    match lower source with
    | Ok ir ->
        let builder = IRBuilder()
        for m in ir.Modules do
            for b in m.Bindings do
                match b.Value with
                | IRApplyCombinator info ->
                    printfn "\n=== Generating C++ for '%s' ===" b.Name
                    
                    // Build code gen info
                    let arrayNames = ["A"; "A"]  // Both are same array
                    let codeGen = buildLoopNestCodeGen info arrayNames b.Name builder
                    
                    printfn "\nLoop Bindings:"
                    for binding in codeGen.Bindings do
                        printfn "  Level %d: %s iterates %s[%d] (param %s)" 
                            binding.Level binding.IndexName binding.ArrayName 
                            binding.DimIndex binding.ParamName
                        let extentStr = ppIRExprWithNames Map.empty 0 binding.Extent
                        let lowerStr = ppIRExprWithNames Map.empty 0 binding.LowerBound
                        printfn "    Extent: %s, LowerBound: %s, Parallel: %b, State: %A"
                            extentStr lowerStr
                            binding.IsParallel binding.State
                    
                    printfn "\nOutput: %s (type: %s)" 
                        codeGen.OutputName (ppIRType codeGen.OutputType)
                    printfn "Output Symm Vec: %A" codeGen.OutputSymmVec
                    printfn "Speedup: %dx" codeGen.SpeedupFactor
                    
                    printfn "\n=== Generated C++ Code ==="
                    let cppLines = genLoopNest codeGen 0
                    for line in cppLines do
                        printfn "%s" line
                    
                | _ -> ()
        0
    | Error e ->
        printfn "Error: %s" e
        1

// ============================================================================
// Test for Array Capture Rejection
// ============================================================================

let runArrayCaptureTest () =
    printHeader "Array Capture Rejection Test"
    
    printfn "This test verifies that lambdas cannot capture arrays.\n"
    
    let source = test26_arrayCaptureRejected
    printfn "Source:\n%s" source
    
    try
        match lower source with
        | Ok _ ->
            printfn "FAILED: Should have rejected array capture"
            1
        | Error e ->
            printfn "Got expected error: %s" e
            0
    with
    | ex ->
        if ex.Message.Contains("cannot capture array") then
            printfn "PASSED: Correctly rejected array capture"
            printfn "Error message: %s" ex.Message
            0
        else
            printfn "FAILED: Unexpected error: %s" ex.Message
            1

// ============================================================================
// NetCDF Provider Tests
// ============================================================================

let runNetcdfTests () =
    printHeader "NetCDF Provider Tests"
    let mutable passed = 0
    let mutable failed = 0
    
    let check (name: string) (condition: bool) (detail: string) =
        if condition then
            printfn "  PASS: %s" name
            passed <- passed + 1
        else
            printfn "  FAIL: %s — %s" name detail
            failed <- failed + 1

    // ---------------------------------------------------------------
    // Test 1: ncTypeToElemType mapping
    // ---------------------------------------------------------------
    printfn "\n--- Type Code Mapping ---"
    
    check "NC_FLOAT (5) -> ETFloat32"
        (NetcdfProvider.ncTypeToElemType 5 = ETFloat32) ""
    check "NC_DOUBLE (6) -> ETFloat64"
        (NetcdfProvider.ncTypeToElemType 6 = ETFloat64) ""
    check "NC_INT (4) -> ETInt64"
        (NetcdfProvider.ncTypeToElemType 4 = ETInt64) ""
    check "NC_SHORT (3) -> ETInt64"
        (NetcdfProvider.ncTypeToElemType 3 = ETInt64) ""
    check "NC_UBYTE (7) -> ETInt64"
        (NetcdfProvider.ncTypeToElemType 7 = ETInt64) ""
    check "NC_CHAR (2) -> ETInt32"
        (NetcdfProvider.ncTypeToElemType 2 = ETInt32) ""
    
    let unsupportedThrows =
        try NetcdfProvider.ncTypeToElemType 99 |> ignore; false
        with _ -> true
    check "Unsupported type code throws" unsupportedThrows ""

    // ---------------------------------------------------------------
    // Test 2: Module construction from mock NcFile
    // ---------------------------------------------------------------
    printfn "\n--- Module Construction (mock data) ---"

    let mockFile : NetcdfProvider.NcFile = {
        Path = "sample.nc"
        Dims = [
            { Name = "lat"; Length = 180L }
            { Name = "lon"; Length = 360L }
            { Name = "time"; Length = 12L }
        ]
        Vars = [
            { Name = "A"; Dims = [
                { Name = "lat"; Length = 180L }
                { Name = "lon"; Length = 360L }
                { Name = "time"; Length = 12L }
              ]; TypeCode = 6 }  // NC_DOUBLE
        ]
    }

    let builder = IRBuilder()
    let modul = NetcdfProvider.ncFileToModule builder "sample" mockFile None

    // Helper to find structs by name
    let findStruct name (m: IRModule) =
        m.Types |> List.tryPick (function
            | IRTDStruct (n, fields) when n = name -> Some fields
            | _ -> None)

    check "Module name is 'sample'"
        (modul.Name = "sample") (sprintf "got '%s'" modul.Name)

    // 3 index types + dims struct + vars struct = 5 type defs
    check "Module has 5 type defs (3 idx + 2 structs)"
        (modul.Types.Length = 5) (sprintf "got %d" modul.Types.Length)

    let idxTypeNames =
        modul.Types |> List.choose (function
            | IRTDIndexType (name, _) -> Some name
            | _ -> None)
    
    check "Index type names are lat, lon, time"
        (idxTypeNames = ["lat"; "lon"; "time"])
        (sprintf "got %A" idxTypeNames)

    let latExtent =
        modul.Types |> List.tryPick (function
            | IRTDIndexType ("lat", idx) ->
                match idx.Extent with IRLit (IRLitInt n) -> Some n | _ -> None
            | _ -> None)
    check "lat extent is 180"
        (latExtent = Some 180L) (sprintf "got %A" latExtent)

    let timeExtent =
        modul.Types |> List.tryPick (function
            | IRTDIndexType ("time", idx) ->
                match idx.Extent with IRLit (IRLitInt n) -> Some n | _ -> None
            | _ -> None)
    check "time extent is 12"
        (timeExtent = Some 12L) (sprintf "got %A" timeExtent)

    // ---------------------------------------------------------------
    // Test 3: Struct structure
    // ---------------------------------------------------------------
    printfn "\n--- Struct Structure ---"

    let dimsFields = findStruct "dims" modul
    check "dims struct exists"
        (dimsFields.IsSome) ""
    check "dims has 3 fields (lat, lon, time)"
        (dimsFields.Value.Length = 3)
        (sprintf "got %d" (match dimsFields with Some f -> f.Length | None -> 0))
    check "dims field names"
        (dimsFields.Value |> List.map fst = ["lat"; "lon"; "time"])
        (sprintf "got %A" (dimsFields.Value |> List.map fst))

    let varsFields = findStruct "vars" modul
    check "vars struct exists"
        (varsFields.IsSome) ""
    check "vars has 1 field (A)"
        (varsFields.Value.Length = 1)
        (sprintf "got %d" (match varsFields with Some f -> f.Length | None -> 0))

    let varAType = varsFields.Value |> List.tryPick (fun (n, t) -> if n = "A" then Some t else None)
    check "vars.A exists" (varAType.IsSome) ""

    match varAType with
    | Some (IRTArray arr) ->
        check "A element type is Float64"
            (arr.ElemType = ETFloat64) (sprintf "got %A" arr.ElemType)
        check "A has 3 index types"
            (arr.IndexTypes.Length = 3) (sprintf "got %d" arr.IndexTypes.Length)
        check "A index types have no tags"
            (arr.IndexTypes |> List.forall (fun i -> i.Tag = None)) ""
        check "A identity is AIDVariable 'A'"
            (arr.Identity = Some (AIDVariable "A")) (sprintf "got %A" arr.Identity)
    | _ ->
        check "A is an array type" false ""

    // ---------------------------------------------------------------
    // Test 4: Index type sharing within a module
    // ---------------------------------------------------------------
    printfn "\n--- Index Type Sharing ---"
    
    let mockFile2 : NetcdfProvider.NcFile = {
        Path = "multi.nc"
        Dims = [
            { Name = "lat"; Length = 180L }
            { Name = "lon"; Length = 360L }
        ]
        Vars = [
            { Name = "temperature"; Dims = [
                { Name = "lat"; Length = 180L }
                { Name = "lon"; Length = 360L }
              ]; TypeCode = 6 }
            { Name = "pressure"; Dims = [
                { Name = "lat"; Length = 180L }
                { Name = "lon"; Length = 360L }
              ]; TypeCode = 5 }  // NC_FLOAT
        ]
    }
    
    let builder2 = IRBuilder()
    let modul2 = NetcdfProvider.ncFileToModule builder2 "climate" mockFile2 None
    let vars2 = findStruct "vars" modul2
    
    check "vars has 2 fields" (vars2.Value.Length = 2) ""
    
    // Both variables should reference the same IRIndexType (same Id)
    let tempIdxIds =
        match vars2.Value |> List.tryPick (fun (n,t) -> if n = "temperature" then Some t else None) with
        | Some (IRTArray a) -> a.IndexTypes |> List.map (fun i -> i.Id)
        | _ -> []
    let pressIdxIds =
        match vars2.Value |> List.tryPick (fun (n,t) -> if n = "pressure" then Some t else None) with
        | Some (IRTArray a) -> a.IndexTypes |> List.map (fun i -> i.Id)
        | _ -> []
    
    check "temperature and pressure share same lat index Id"
        (tempIdxIds.Length >= 1 && pressIdxIds.Length >= 1
         && tempIdxIds.[0] = pressIdxIds.[0]) ""
    
    check "temperature and pressure share same lon index Id"
        (tempIdxIds.Length >= 2 && pressIdxIds.Length >= 2
         && tempIdxIds.[1] = pressIdxIds.[1]) ""

    check "temperature is Float64, pressure is Float32"
        (match vars2.Value.[0] |> snd, vars2.Value.[1] |> snd with
         | IRTArray a1, IRTArray a2 ->
             a1.ElemType = ETFloat64 && a2.ElemType = ETFloat32
         | _ -> false) ""

    // ---------------------------------------------------------------
    // Test 5: External dim map (schema extensibility)
    // ---------------------------------------------------------------
    printfn "\n--- External Dim Map (schema hook) ---"
    
    let schemaBuilder = IRBuilder()
    let sharedLat = {
        Id = schemaBuilder.FreshId()
        Arity = 1
        Extent = IRLit (IRLitInt 180L)
        Symmetry = SymNone
        Tag = None
        Kind = SDimension
        Dependencies = []
    }
    let sharedLon = {
        Id = schemaBuilder.FreshId()
        Arity = 1
        Extent = IRLit (IRLitInt 360L)
        Symmetry = SymNone
        Tag = None
        Kind = SDimension
        Dependencies = []
    }
    let externalMap = Map.ofList [("lat", sharedLat); ("lon", sharedLon)]
    
    let modul3 = NetcdfProvider.ncFileToModule schemaBuilder "file1" mockFile2 (Some externalMap)
    let modul4 = NetcdfProvider.ncFileToModule schemaBuilder "file2" mockFile2 (Some externalMap)
    
    // With external map, no IRTDIndexType defs are generated
    let idx3 = modul3.Types |> List.choose (function IRTDIndexType _ -> Some () | _ -> None)
    check "External map: no IRTDIndexType defs generated"
        (idx3.IsEmpty) (sprintf "got %d" idx3.Length)
    
    // Both modules' vars should reference the shared lat/lon Ids
    let vars3 = findStruct "vars" modul3
    let vars4 = findStruct "vars" modul4
    check "External map: both modules share same lat Id"
        (match vars3, vars4 with
         | Some f3, Some f4 ->
             match f3.[0] |> snd, f4.[0] |> snd with
             | IRTArray a1, IRTArray a2 ->
                 a1.IndexTypes.[0].Id = sharedLat.Id
                 && a2.IndexTypes.[0].Id = sharedLat.Id
             | _ -> false
         | _ -> false) ""

    // ---------------------------------------------------------------
    // Test 6: C++ codegen helpers
    // ---------------------------------------------------------------
    printfn "\n--- C++ Code Generation ---"
    
    let dimNames = NetcdfProvider.CppNetcdf.dimNamesFromModule modul
    check "dimNamesFromModule returns [lat; lon; time]"
        (dimNames = ["lat"; "lon"; "time"]) (sprintf "got %A" dimNames)
    
    match varAType with
    | Some (IRTArray arrType) ->
        let readCode = NetcdfProvider.CppNetcdf.genReadVar "sample.nc" "A" "A" arrType
        check "genReadVar produces nc_open call"
            (readCode |> List.exists (fun s -> s.Contains "nc_open")) ""
        check "genReadVar produces nc_get_var_double"
            (readCode |> List.exists (fun s -> s.Contains "nc_get_var_double")) ""
        check "genReadVar produces nc_close"
            (readCode |> List.exists (fun s -> s.Contains "nc_close")) ""
        
        let writeCode = NetcdfProvider.CppNetcdf.genWriteVar "out.nc" "A" "A" arrType dimNames
        check "genWriteVar produces nc_create call"
            (writeCode |> List.exists (fun s -> s.Contains "nc_create")) ""
        check "genWriteVar uses dimension names from module"
            (writeCode |> List.exists (fun s -> s.Contains "\"lat\"")
             && writeCode |> List.exists (fun s -> s.Contains "\"lon\"")
             && writeCode |> List.exists (fun s -> s.Contains "\"time\"")) ""
    | _ -> ()

    // ---------------------------------------------------------------
    // Test 7: Live load (requires libnetcdf + sample.nc)
    // ---------------------------------------------------------------
    printfn "\n--- Live Load (sample.nc) ---"
    
    try
        let liveFile = NetcdfProvider.load "sample.nc"
        printfn "  Loaded '%s': %d dims, %d vars" liveFile.Path liveFile.Dims.Length liveFile.Vars.Length
        
        for dim in liveFile.Dims do
            printfn "    dim %-12s length=%d" dim.Name dim.Length
        
        let hasA = liveFile.Vars |> List.exists (fun v -> v.Name = "A")
        check "sample.nc contains variable A" hasA ""
        
        if hasA then
            let liveBuilder = IRBuilder()
            let liveModule = NetcdfProvider.ncFileToModule liveBuilder "sample" liveFile None
            
            let liveDimsFields = findStruct "dims" liveModule
            let liveVarsFields = findStruct "vars" liveModule
            
            check "Live dims struct exists"
                (liveDimsFields.IsSome) ""
            check "Live vars struct exists"
                (liveVarsFields.IsSome) ""
            check "Live vars has field for A"
                (liveVarsFields.Value |> List.exists (fun (n, _) -> n = "A")) ""
            
            printfn "\n  Module IR:"
            printfn "    module %s" liveModule.Name
            let names = indexNameMap liveModule
            for td in liveModule.Types do
                match td with
                | IRTDIndexType (name, idx) ->
                    let ext = match idx.Extent with IRLit (IRLitInt n) -> sprintf "%d" n | _ -> "?"
                    printfn "      type %s = Idx<%s>" name ext
                | IRTDStruct (name, fields) ->
                    printfn "      struct %s = {" name
                    for (fname, ftype) in fields do
                        printfn "        %s: %s" fname (ppIRTypeIn names ftype)
                    printfn "      }"
                | _ -> ()
    with
    | :? System.DllNotFoundException ->
        printfn "  SKIP: libnetcdf not available"
    | :? System.IO.FileNotFoundException ->
        printfn "  SKIP: sample.nc not found"
    | ex ->
        printfn "  SKIP: %s" ex.Message

    // ---------------------------------------------------------------
    // Test 8: Blade program with import and provider load
    // ---------------------------------------------------------------
    printfn "\n--- Blade Program Import (sample.nc) ---"

    let bladeSource = """
import Providers.NetCDF as NetCDF

let sample = NetCDF.load("sample.nc")
"""
    
    // Test parse
    match parseProgram bladeSource with
    | Ok program ->
        check "Parse succeeds" true ""
        let decls = program.Modules.[0].Decls |> List.map (fun d -> d.Value)
        
        check "First decl is DeclImport"
            (match decls.[0] with DeclImport _ -> true | _ -> false)
            (sprintf "got %A" decls.[0])
        
        check "Import has correct qualified name"
            (match decls.[0] with 
             | DeclImport (["Providers"; "NetCDF"], Some "NetCDF") -> true 
             | _ -> false)
            (sprintf "got %A" decls.[0])

        check "Second decl is DeclLet"
            (match decls.[1] with DeclLet _ -> true | _ -> false)
            (sprintf "got %A" decls.[1])

        // Test lowering (requires sample.nc + libnetcdf)
        try
            match lower bladeSource with
            | Ok ir ->
                check "Lower succeeds" true ""
                let modul = ir.Modules.[0]
                let names = indexNameMap modul
                
                printfn "\n  Lowered module: %s" modul.Name
                printfn "  Types: %d" modul.Types.Length
                for td in modul.Types do
                    match td with
                    | IRTDIndexType (name, idx) ->
                        let ext = match idx.Extent with IRLit (IRLitInt n) -> sprintf "%d" n | _ -> "?"
                        printfn "    type %s = Idx<%s>" name ext
                    | IRTDStruct (name, fields) ->
                        printfn "    struct %s = {" name
                        for (fname, ftype) in fields do
                            printfn "      %s: %s" fname (ppIRTypeIn names ftype)
                        printfn "    }"
                    | _ -> ()

                // Verify types were produced
                let idxTypes = modul.Types |> List.choose (function IRTDIndexType (n, _) -> Some n | _ -> None)
                check "Provider produced index types"
                    (idxTypes.Length >= 3) (sprintf "got %A" idxTypes)

                let hasVarsStruct = modul.Types |> List.exists (function IRTDStruct ("vars", _) -> true | _ -> false)
                check "Provider produced vars struct" hasVarsStruct ""

                let hasDimsStruct = modul.Types |> List.exists (function IRTDStruct ("dims", _) -> true | _ -> false)
                check "Provider produced dims struct" hasDimsStruct ""

                // Verify vars struct has field A
                let varAExists =
                    modul.Types |> List.exists (function
                        | IRTDStruct ("vars", fields) ->
                            fields |> List.exists (fun (n, _) -> n = "A")
                        | _ -> false)
                check "vars struct has field A" varAExists ""

            | Error e ->
                printfn "  Lower error: %s" e
                check "Lower succeeds" false e
        with
        | :? System.DllNotFoundException ->
            printfn "  SKIP lower: libnetcdf not available"
        | :? System.IO.FileNotFoundException ->
            printfn "  SKIP lower: sample.nc not found"
        | ex ->
            printfn "  SKIP lower: %s" ex.Message

    | Error e ->
        check "Parse succeeds" false (sprintf "%d:%d %s" e.Line e.Col e.Message)

    // ---------------------------------------------------------------
    // Summary
    // ---------------------------------------------------------------
    printfn "\n========================================="
    printfn "NetCDF Provider: %d passed, %d failed" passed failed
    if failed > 0 then 1 else 0

// ============================================================================
// Main Entry Point
// ============================================================================

let printUsage () =
    printfn "Blade-DSL Compiler Test Suite"
    printfn ""
    printfn "Usage: dotnet run [option]"
    printfn ""
    printfn "Options:"
    printfn "  (none)        Run all tests (old pipeline)"
    printfn "  --basic       Basic language constructs"
    printfn "  --loops       Loop objects and application"
    printfn "  --symmetry    Symmetry and triangular iteration"
    printfn "  --reynolds    Reynolds operator tests"
    printfn "  --arity       Arity polymorphism tests"
    printfn "  --functions   Functions and captures"
    printfn "  --structs     Struct tests"
    printfn "  --sumtypes    Sum type tests"
    printfn "  --interfaces  Interface and impl tests"
    printfn "  --modules     Module tests"
    printfn "  --guards      Guard expression tests"
    printfn "  --bracketed   Bracketed (outer product) operator tests"
    printfn "  --capture     Array capture rejection test"
    printfn "  --codegen     C++ code generation test"
    printfn "  --netcdf      NetCDF provider tests"
    printfn ""
    printfn "Type Checking Pipeline Options:"
    printfn "  --typecheck   Run all tests with new TypeCheck pipeline"
    printfn "  --tc-only     Run type checking only (no lowering)"
    printfn "  --compare     Compare old vs new pipeline on all tests"
    printfn ""
    printfn "  --help        Show this help"

[<EntryPoint>]
let main args =
    match args with
    | [||] -> runAllTests ()
    | [| "--basic" |] -> runTestCategory "Basic" basicTests
    | [| "--loops" |] -> runTestCategory "Loops" loopTests
    | [| "--symmetry" |] -> runTestCategory "Symmetry" symmetryTests
    | [| "--reynolds" |] -> runTestCategory "Reynolds" reynoldsTests
    | [| "--arity" |] -> runTestCategory "Arity Polymorphism" arityTests
    | [| "--functions" |] -> runTestCategory "Functions" functionTests
    | [| "--structs" |] -> runTestCategory "Structs" structTests
    | [| "--sumtypes" |] -> runTestCategory "Sum Types" sumTypeTests
    | [| "--interfaces" |] -> runTestCategory "Interfaces" interfaceTests
    | [| "--modules" |] -> runTestCategory "Modules" moduleTests
    | [| "--guards" |] -> runTestCategory "Guards" guardTests
    | [| "--bracketed" |] -> runTestCategory "Bracketed Ops" bracketedTests
    | [| "--capture" |] -> runArrayCaptureTest ()
    | [| "--codegen" |] -> runCodeGenTest ()
    | [| "--netcdf" |] -> runNetcdfTests ()
    // New TypeCheck pipeline options
    | [| "--typecheck" |] -> runAllTestsWithTypeCheck ()
    | [| "--tc-only" |] -> runTypeCheckOnly "All" allTests
    | [| "--compare" |] -> runPipelineComparison ()
    | [| "--help" |] -> printUsage (); 0
    | _ -> printUsage (); 1
