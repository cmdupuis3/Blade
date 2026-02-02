// Blade-DSL C++ Code Generation
// Transforms IR structures into C++ source code

module Blade.CodeGen

open Blade.IR

// ============================================================================
// C++ Type Mapping
// ============================================================================

/// Convert element type to C++ type string
let elemTypeToCpp = function
    | ETInt32 -> "int32_t"
    | ETInt64 -> "int64_t"
    | ETFloat32 -> "float"
    | ETFloat64 -> "double"
    | ETComplex64 -> "std::complex<float>"
    | ETComplex128 -> "std::complex<double>"
    | ETBool -> "bool"
    | ETUnit -> "void"

/// Convert IR type to C++ type string
let rec irTypeToCpp = function
    | IRTScalar et -> elemTypeToCpp et
    | IRTArray arr -> sprintf "promote<%s, %d>::type" (elemTypeToCpp arr.ElemType) (arr.IndexTypes |> List.sumBy (fun i -> i.Arity))
    | IRTTuple ts -> sprintf "std::tuple<%s>" (ts |> List.map irTypeToCpp |> String.concat ", ")
    | IRTFunc _ -> "std::function<...>"  // Would need full signature
    | IRTUnit -> "void"
    | _ -> "/* unknown type */"

// ============================================================================
// C++ Expression Generation
// ============================================================================

/// Convert binary operator to C++ string
let binOpToCpp = function
    | IRAdd -> "+" | IRSub -> "-" | IRMul -> "*" | IRDiv -> "/"
    | IRMod -> "%" | IRCaret -> "pow"  // Special handling needed
    | IREq -> "==" | IRNeq -> "!=" 
    | IRLt -> "<" | IRLe -> "<=" | IRGt -> ">" | IRGe -> ">="
    | IRAnd -> "&&" | IROr -> "||"

/// Convert unary operator to C++ string
let unaryOpToCpp = function
    | IRNeg -> "-"
    | IRNot -> "!"

/// Convert IRExpr to C++ expression string
let rec exprToCpp (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%dL" n
    | IRLit (IRLitFloat f) -> sprintf "%g" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit IRLitUnit -> "()"
    | IRVar id -> 
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "__v%d" id
    | IRParam (name, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCpp names l
        let rStr = exprToCpp names r
        if op = IRCaret then
            sprintf "pow(%s, %s)" lStr rStr
        else
            sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
    | IRUnaryOp (op, e) ->
        sprintf "%s(%s)" (unaryOpToCpp op) (exprToCpp names e)
    | IRIf (cond, thenBr, elseBr) ->
        sprintf "(%s ? %s : %s)" 
            (exprToCpp names cond) 
            (exprToCpp names thenBr) 
            (exprToCpp names elseBr)
    | IRTuple exprs ->
        sprintf "std::make_tuple(%s)" (exprs |> List.map (exprToCpp names) |> String.concat ", ")
    | IRTupleProj (e, i) ->
        sprintf "std::get<%d>(%s)" i (exprToCpp names e)
    | IRIndex (arr, indices, _) ->
        let arrStr = exprToCpp names arr
        let idxStr = indices |> List.map (fun i -> sprintf "[%s]" (exprToCpp names i)) |> String.concat ""
        sprintf "%s%s" arrStr idxStr
    | IRApp (func, args) ->
        sprintf "%s(%s)" (exprToCpp names func) (args |> List.map (exprToCpp names) |> String.concat ", ")
    | _ -> "/* unsupported expr */"

// ============================================================================
// Loop Nest Code Generation
// ============================================================================

/// Convert extent/bound IRExpr to C++ string (simplified for loop bounds)
let boundExprToCpp (binding: LoopIndexBinding) (expr: IRExpr) : string =
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%d" n
    | IRVar id -> sprintf "__i%d" id  // Reference to prior loop index
    | IRParam (name, _) -> sprintf "%s_extents[%d]" binding.ArrayName binding.DimIndex
    | _ -> sprintf "%s_extents[%d]" binding.ArrayName binding.DimIndex

/// Generate the element binding expression: "auto arr__idx = arr[idx];"
let genElementBinding (binding: LoopIndexBinding) : string =
    sprintf "auto %s__%s = %s[%s];" 
        binding.ArrayName binding.IndexName binding.ArrayName binding.IndexName

/// Generate a for-loop header with optional OpenMP pragma
let genForLoopHeader (binding: LoopIndexBinding) : string =
    let pragma = if binding.IsParallel then "#pragma omp parallel for\n" else ""
    let lowerStr = boundExprToCpp binding binding.LowerBound
    let extentStr = boundExprToCpp binding binding.Extent
    sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" 
        pragma
        binding.IndexName 
        lowerStr
        binding.IndexName 
        extentStr
        binding.IndexName

/// Generate triangular loop header (upper bound depends on prior index)
let genTriangularForHeader (binding: LoopIndexBinding) (priorIndex: string) : string =
    let lowerStr = boundExprToCpp binding binding.LowerBound
    let extentStr = boundExprToCpp binding binding.Extent
    sprintf "for (size_t %s = %s; %s < %s - %s; %s++) {" 
        binding.IndexName 
        lowerStr
        binding.IndexName 
        extentStr
        priorIndex
        binding.IndexName

/// Generate the kernel body with parameter substitutions
let genKernelBody (codeGen: LoopNestCodeGen) : string =
    // Build name map: param VarId -> element binding name
    let nameMap = 
        codeGen.Bindings 
        |> List.fold (fun acc b -> 
            Map.add b.ParamVarId (sprintf "%s__%s" b.ArrayName b.IndexName) acc
        ) Map.empty
    
    exprToCpp nameMap codeGen.KernelExpr

/// Generate output index expression for nested pointer arrays
let genOutputIndexNested (codeGen: LoopNestCodeGen) : string =
    codeGen.Bindings 
    |> List.map (fun b -> sprintf "[%s]" b.IndexName)
    |> String.concat ""

/// Check if a binding has triangular lower bound (references prior index)
let isTriangularBound (binding: LoopIndexBinding) : bool =
    match binding.LowerBound with
    | IRVar _ -> true
    | _ -> false

/// Generate output index for triangular packed storage
/// Formula: idx = i * (2*n - i + 1) / 2 + (j - i)
let genOutputIndexTriangular (codeGen: LoopNestCodeGen) : string =
    match codeGen.Bindings with
    | [b0; b1] when isTriangularBound b1 ->
        // 2D triangular case
        let extentStr = boundExprToCpp b0 b0.Extent
        sprintf "[%s * (2 * %s - %s + 1) / 2 + (%s - %s)]"
            b0.IndexName extentStr b0.IndexName b1.IndexName b0.IndexName
    | _ ->
        // Fallback to nested
        genOutputIndexNested codeGen

/// Generate complete loop nest as C++ code
let genLoopNest (codeGen: LoopNestCodeGen) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Generate nested loops with element bindings
    for binding in codeGen.Bindings do
        lines <- lines @ [ind depth + genForLoopHeader binding]
        depth <- depth + 1
        lines <- lines @ [ind depth + genElementBinding binding]
    
    // Generate kernel assignment
    let outputIdx = genOutputIndexNested codeGen
    let kernelBody = genKernelBody codeGen
    lines <- lines @ [ind depth + sprintf "%s%s = %s;" codeGen.OutputName outputIdx kernelBody]
    
    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]
    
    lines

// ============================================================================
// Symmetry Vector Generation
// ============================================================================

/// Generate C++ static constexpr array for symmetry vector
let genSymmVecDecl (name: string) (symmVec: int list) : string =
    if symmVec.IsEmpty then
        sprintf "static constexpr const size_t* %s = nullptr;" name
    else
        let values = symmVec |> List.map string |> String.concat ", "
        sprintf "static constexpr const size_t %s[%d] = {%s};" name symmVec.Length values

/// Generate C++ static constexpr array for extents
let genExtentsDecl (name: string) (extents: int list) : string =
    if extents.IsEmpty then
        sprintf "static constexpr const size_t %s[0] = {};" name
    else
        let values = extents |> List.map string |> String.concat ", "
        sprintf "static constexpr const size_t %s[%d] = {%s};" name extents.Length values

// ============================================================================
// Array Allocation Generation
// ============================================================================

/// Generate allocation call using promote<T, rank>::type pattern
let genAllocate (varName: string) (elemType: string) (rank: int) (symmVecName: string) (extentsName: string) : string list =
    [
        sprintf "promote<%s, %d>::type %s;" elemType rank varName
        sprintf "%s = allocate<typename promote<%s, %d>::type, %s>(%s);" 
            varName elemType rank symmVecName extentsName
    ]

// ============================================================================
// Function Template Generation
// ============================================================================

/// Generate template parameter list for a combinator function
let genTemplateParams (inputCount: int) (hasOutput: bool) : string =
    let inputs = 
        [0 .. inputCount - 1] 
        |> List.collect (fun i -> 
            [sprintf "typename ITYPE%d" (i+1)
             sprintf "const size_t IRANK%d" (i+1)
             sprintf "const size_t* ISYM%d" (i+1)])
    let output =
        if hasOutput then
            ["typename OTYPE"; "const size_t ORANK"; "const size_t* OSYM"]
        else []
    inputs @ output |> String.concat ", "

/// Generate function parameter list
let genFunctionParams (inputNames: string list) (outputName: string) : string =
    let inputs =
        inputNames |> List.mapi (fun i name ->
            [sprintf "typename promote<ITYPE%d, IRANK%d>::type %s" (i+1) (i+1) name
             sprintf "const size_t %s_extents[IRANK%d]" name (i+1)])
        |> List.concat
    let output =
        [sprintf "typename promote<OTYPE, ORANK>::type %s" outputName
         sprintf "const size_t %s_extents[ORANK]" outputName]
    inputs @ output |> String.concat ",\n    "

// ============================================================================
// Complete Function Generation
// ============================================================================

/// Generate a complete C++ function from LoopNestCodeGen
let genFunction (codeGen: LoopNestCodeGen) (funcName: string) : string list =
    let inputCount = codeGen.InputArrayNames.Length
    
    // Template declaration
    let templateParams = genTemplateParams inputCount true
    let funcParams = genFunctionParams codeGen.InputArrayNames codeGen.OutputName
    
    // Function signature
    let signature = 
        [sprintf "template<%s>" templateParams
         sprintf "void %s(" funcName
         sprintf "    %s) {" funcParams]
    
    // Body with loop nest
    let body = genLoopNest codeGen 1
    
    // Close
    let close = ["}"]
    
    signature @ body @ close

/// Generate header includes
let genIncludes () : string list =
    ["#include <cstdint>"
     "#include <cmath>"
     "#include <complex>"
     "#include <omp.h>"
     "#include \"nested_array_utilities.cpp\""
     "using namespace nested_array_utilities;"
     ""]

// ============================================================================
// Full Program Generation
// ============================================================================

/// Generate a complete C++ program from multiple LoopNestCodeGen
let genProgram (functions: (string * LoopNestCodeGen) list) : string =
    let includes = genIncludes ()
    
    let funcCode = 
        functions 
        |> List.collect (fun (name, cg) -> genFunction cg name @ [""])
    
    (includes @ funcCode) |> String.concat "\n"
