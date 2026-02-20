// Blade-DSL C++ Code Generation
// Transforms IR structures into C++ source code
// Generates complete, compilable C++ programs

module Blade.CodeGen

open Blade.IR

// ============================================================================
// Code Generation Context
// ============================================================================

/// Tracks information needed during code generation
type CodeGenContext = {
    /// Map from IR variable IDs to C++ variable names
    VarNames: Map<IRId, string>
    /// Current indentation level
    Indent: int
    /// Generated static declarations (symmetry vectors, extents)
    StaticDecls: string list
    /// Counter for generating unique names
    mutable NextTempId: int
    /// Set of struct type names that have where constraints (need validate() calls)
    ConstrainedStructs: Set<string>
}

let emptyContext () = {
    VarNames = Map.empty
    Indent = 0
    StaticDecls = []
    NextTempId = 0
    ConstrainedStructs = Set.empty
}

let indent ctx = { ctx with Indent = ctx.Indent + 1 }
let dedent ctx = { ctx with Indent = max 0 (ctx.Indent - 1) }
let indentStr ctx = String.replicate ctx.Indent "    "

/// C++ reserved words and built-in type names that cannot be used as identifiers
let cppReservedWords = Set.ofList [
    // Types that conflict with Blade names
    "double"; "float"; "int"; "long"; "short"; "char"; "bool"; "void"; "auto"
    "signed"; "unsigned"; "const"; "volatile"; "static"; "extern"; "register"
    // Keywords
    "class"; "struct"; "enum"; "union"; "namespace"; "template"; "typename"
    "virtual"; "override"; "final"; "public"; "private"; "protected"
    "new"; "delete"; "this"; "return"; "if"; "else"; "for"; "while"; "do"
    "switch"; "case"; "break"; "continue"; "goto"; "default"; "try"; "catch"; "throw"
    "sizeof"; "alignof"; "decltype"; "typedef"; "using"; "operator"
    "true"; "false"; "nullptr"; "inline"; "constexpr"; "mutable"
]

/// Sanitize a name to avoid C++ reserved word conflicts
let sanitizeCppName (name: string) : string =
    if Set.contains name cppReservedWords then name + "_"
    else name

let addVarName id name ctx = 
    { ctx with VarNames = Map.add id (sanitizeCppName name) ctx.VarNames }

let freshTemp ctx prefix =
    let id = ctx.NextTempId
    ctx.NextTempId <- ctx.NextTempId + 1
    sprintf "%s_%d" prefix id

// ============================================================================
// Type Inference Helper (for code generation)
// ============================================================================

/// Collect all variable IDs referenced in an expression (for computing captures)
let rec collectVarRefs (expr: IRExpr) : Set<IRId> =
    match expr with
    | IRVar (id, _) -> Set.singleton id
    | IRLit _ -> Set.empty
    | IRParam _ -> Set.empty
    | IRBinOp (_, _, left, right) -> Set.union (collectVarRefs left) (collectVarRefs right)
    | IRUnaryOp (_, operand) -> collectVarRefs operand
    | IRIf (cond, thenBr, elseBr) -> 
        Set.unionMany [collectVarRefs cond; collectVarRefs thenBr; collectVarRefs elseBr]
    | IRLet (_, value, body) -> Set.union (collectVarRefs value) (collectVarRefs body)
    | IRLambda info -> 
        let paramIds = info.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        Set.difference (collectVarRefs info.Body) paramIds
    | IRApp (func, args, _) -> 
        Set.unionMany (collectVarRefs func :: List.map collectVarRefs args)
    | IRTuple exprs -> Set.unionMany (List.map collectVarRefs exprs)
    | IRTupleProj (e, _) -> collectVarRefs e
    | IRArrayLit (exprs, _) -> Set.unionMany (List.map collectVarRefs exprs)
    | IRIndex (arr, indices, _) -> 
        Set.unionMany (collectVarRefs arr :: List.map collectVarRefs indices)
    | IRFieldAccess (obj, _) -> collectVarRefs obj
    | IRStructLit (_, fields) -> Set.unionMany (fields |> List.map (snd >> collectVarRefs))
    | IRCompute inner -> collectVarRefs inner
    | IRMethodFor info -> Set.unionMany (List.map collectVarRefs info.Arrays)
    | IRObjectFor info -> 
        // InputRanks is int list, not IRExpr list - just collect from kernel
        collectVarRefs info.Kernel
    | IRApplyCombinator info -> 
        Set.union (collectVarRefs info.Loop) (collectVarRefs info.Kernel)
    | IRArity _ -> Set.empty
    | IRReynolds (inner, _) -> collectVarRefs inner
    | IRMatch (scrutinee, cases) ->
        let scrutineeRefs = collectVarRefs scrutinee
        let caseRefs = cases |> List.map (fun c -> collectVarRefs c.Body) |> Set.unionMany
        Set.union scrutineeRefs caseRefs
    | _ -> Set.empty

/// Infer type from an IR expression (simplified version for codegen)
let rec inferExprType (expr: IRExpr) : IRType =
    match expr with
    | IRArrayLit (_, arrTy) -> IRTArray arrTy
    | IRLit (IRLitInt _) -> IRTScalar ETInt64
    | IRLit (IRLitFloat _) -> IRTScalar ETFloat64
    | IRLit (IRLitBool _) -> IRTScalar ETBool
    | IRLit IRLitUnit -> IRTUnit
    | IRBinOp (_, op, left, _) ->
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> IRTScalar ETBool
        | _ -> inferExprType left
    | IRUnaryOp (op, operand) ->
        match op with
        | IRNot -> IRTScalar ETBool
        | IRNeg -> inferExprType operand
    | IRIf (_, thenBr, _) -> inferExprType thenBr
    | IRCompute inner -> inferExprType inner
    | IRApplyCombinator info -> info.OutputType
    | IRLambda info -> 
        let argTypes = info.Params |> List.map (fun p -> p.Type)
        let retType = inferExprType info.Body
        IRTFunc (argTypes, retType)
    | IRTuple exprs -> IRTTuple (exprs |> List.map inferExprType)
    | IRTupleProj (e, i) ->
        match inferExprType e with
        | IRTTuple ts when i < ts.Length -> ts.[i]
        | _ -> IRTUnit  // Tuple projection out of range — should not happen with typed IR
    | IRStructLit (typeName, _) -> IRTNamed typeName
    | IRApp (_, _, retType) -> retType
    | IRVar (_, ty) -> ty
    | IRParam (_, _, ty) -> ty
    | IRLet (_, _, body) -> inferExprType body
    | IRMatch (_, cases) ->
        match cases with
        | c :: _ -> inferExprType c.Body
        | [] -> IRTUnit
    | IRIndex (arr, indices, _) ->
        // Indexing peels dimensions; full indexing yields scalar
        match inferExprType arr with
        | IRTArray arrTy when indices.Length >= arrTy.IndexTypes.Length -> IRTScalar arrTy.ElemType
        | IRTArray arrTy -> IRTArray { arrTy with IndexTypes = arrTy.IndexTypes |> List.skip indices.Length }
        | t -> t
    | IRSequence exprs ->
        match exprs with
        | [] -> IRTUnit
        | _ -> inferExprType (List.last exprs)
    | IRAssign _ -> IRTUnit
    | IRArity _ -> IRTScalar ETInt64
    | IRNth -> IRTScalar ETInt64
    | IRRank _ -> IRTScalar ETInt64
    | IRExtent _ -> IRTScalar ETInt64
    | IRRange _ -> IRTScalar ETInt64
    | IRFieldAccess (obj, field) ->
        // Would need struct type info to resolve; return unit as placeholder
        IRTUnit
    | _ -> IRTUnit  // Remaining cases: loop objects, combinators — not runtime values

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
    | ETString -> "std::string"

/// Get rank (total dimensions) from array type
let arrayRank (arr: IRArrayType) = 
    arr.IndexTypes |> List.sumBy (fun i -> i.Arity)

/// Convert IR type to C++ type string
let rec irTypeToCpp = function
    | IRTScalar et -> elemTypeToCpp et
    | IRTArray arr -> sprintf "promote<%s, %d>::type" (elemTypeToCpp arr.ElemType) (arrayRank arr)
    | IRTTuple ts -> sprintf "std::tuple<%s>" (ts |> List.map irTypeToCpp |> String.concat ", ")
    | IRTFunc _ -> "std::function</* func */>"
    | IRTUnit -> "void"
    | IRTLoop lt ->
        match lt.Kind with
        | LKMethod -> "/* MethodLoop */"
        | LKObject -> "/* ObjectLoop */"
    | IRTComputation t -> irTypeToCpp t  // Computation<T> erases to T at runtime
    | IRTPoly (base', arityVar) -> 
        // After monomorphization, IRTPoly should not reach codegen.
        // If it does, fall back to the base type.
        irTypeToCpp base'
    | IRTNat _ -> "size_t"
    | IRTNamed "String" -> "std::string"  // Blade String → C++ std::string
    | IRTNamed name -> name  // Named types (structs, etc.) use their name directly
    | IRTInfer n -> sprintf "/* UNRESOLVED_TYPE_%d */" n  // Bug: should not reach codegen
    | IRTUnitAnnotated (inner, _) -> irTypeToCpp inner  // Units erase at codegen


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

/// Simplified exprToCpp that doesn't recurse into complex IR nodes
/// Used for kernel bodies in inline generation
let rec exprToCppSimple (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit (IRLitInt n) -> sprintf "%dL" n
    | IRLit (IRLitFloat f) -> sprintf "%g" f
    | IRLit (IRLitBool b) -> if b then "true" else "false"
    | IRLit IRLitUnit -> "()"
    | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppSimple names l
        let rStr = exprToCppSimple names r
        if op = IRCaret then sprintf "pow(%s, %s)" lStr rStr
        else sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
    | IRUnaryOp (op, e) -> sprintf "%s(%s)" (unaryOpToCpp op) (exprToCppSimple names e)
    | _ -> "/* unsupported expr */"

/// Generate inline combinator application as an IIFE expression
/// This is used when L <@> f appears in expression context (not as a top-level binding)
and genApplyCombinatorExpr (names: Map<IRId, string>) (info: ApplyInfo) : string =
    // Extract array info
    let arrayNames = 
        match info.Loop with
        | IRMethodFor mfInfo ->
            mfInfo.Arrays |> List.mapi (fun i arr ->
                match arr with
                | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "arr%d" i)
                | IRParam (name, _, _) -> name
                | _ -> sprintf "arr%d" i)
        | _ -> []
    
    // Extract kernel
    let kernelExpr = 
        match info.Kernel with
        | IRLambda lInfo -> 
            let paramNames = lInfo.Params |> List.map (fun p -> p.Name)
            let bodyStr = 
                let names' = lInfo.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
                exprToCppSimple names' lInfo.Body
            (paramNames, bodyStr)
        | IRReynolds (IRLambda lInfo, _) ->
            let paramNames = lInfo.Params |> List.map (fun p -> p.Name)
            let bodyStr = 
                let names' = lInfo.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
                exprToCppSimple names' lInfo.Body
            (paramNames, bodyStr)
        | _ -> ([], "0")
    
    let (paramNames, kernelBody) = kernelExpr
    
    // Infer output element type from ApplyInfo
    let elemTypeStr =
        match info.OutputType with
        | IRTScalar et -> elemTypeToCpp et
        | IRTArray arr -> elemTypeToCpp arr.ElemType
        | t -> irTypeToCpp t
    
    // For scalar output (simple accumulation), generate inline loop
    // This is a simplified version - full version would use LoopNestCodeGen
    if arrayNames.Length = 2 && paramNames.Length = 2 then
        let arr1 = arrayNames.[0]
        let arr2 = arrayNames.[1]
        let p1 = paramNames.[0]
        let p2 = paramNames.[1]
        // Generate as IIFE with nested loops
        // Use _extents arrays for size (works with both top-level arrays and function params)
        sprintf "([&]() { %s __result = 0; for (size_t __i0 = 0; __i0 < %s_extents[0]; __i0++) { %s %s = %s[__i0]; for (size_t __i1 = 0; __i1 < %s_extents[0]; __i1++) { %s %s = %s[__i1]; __result += %s; } } return __result; }())"
            elemTypeStr arr1 elemTypeStr p1 arr1 arr2 elemTypeStr p2 arr2 kernelBody
    else
        // Fallback - can't generate inline code
        sprintf "/* inline combinator not supported for %d arrays */" arrayNames.Length

/// Convert IRLit to C++ literal string
let litToCpp (lit: IRLit) : string =
    match lit with
    | IRLitInt n -> sprintf "%dL" n
    | IRLitFloat f -> sprintf "%g" f
    | IRLitBool b -> if b then "true" else "false"
    | IRLitUnit -> "()"

/// Convert IRExpr to C++ expression string
let rec exprToCpp (names: Map<IRId, string>) (expr: IRExpr) : string =
    match expr with
    | IRLit lit -> litToCpp lit
    | IRVar (id, _) -> 
        match Map.tryFind id names with
        | Some name -> name
        | None -> sprintf "__v%d" id
    | IRParam (name, _, _) -> name
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
    | IRFieldAccess (obj, field) ->
        sprintf "%s.%s" (exprToCpp names obj) field
    | IRStructLit (typeName, fields) ->
        let fieldInits = fields |> List.map (fun (fname, e) -> 
            sprintf ".%s = %s" fname (exprToCpp names e)) |> String.concat ", "
        sprintf "%s { %s }" typeName fieldInits
    | IRIndex (arr, indices, _) ->
        let arrStr = exprToCpp names arr
        let idxStr = indices |> List.map (fun i -> sprintf "[%s]" (exprToCpp names i)) |> String.concat ""
        sprintf "%s%s" arrStr idxStr
    | IRApp (func, args, _) ->
        sprintf "%s(%s)" (exprToCpp names func) (args |> List.map (exprToCpp names) |> String.concat ", ")
    | IRLet (id, value, body) ->
        // For inline let expressions, we need statement context
        // This is a simplified version - complex lets need statement generation
        let valStr = exprToCpp names value
        let names' = Map.add id (sprintf "__v%d" id) names
        let bodyStr = exprToCpp names' body
        sprintf "([&]() { auto __v%d = %s; return %s; }())" id valStr bodyStr
    | IRLambda info ->
        // Generate C++ lambda
        let paramList = info.Params |> List.map (fun p -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name) |> String.concat ", "
        let names' = info.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
        let bodyStr = exprToCpp names' info.Body
        sprintf "[&](%s) { return %s; }" paramList bodyStr
    | IRMethodFor _ -> "0 /* loop object */"  // Loop objects are compile-time only
    | IRObjectFor _ -> "0 /* loop object */"  // Loop objects are compile-time only
    | IRApplyCombinator _ -> 
        // Combinator applications are lazy - they don't generate code until compute is called
        // Return a placeholder that will cause a compile error if used directly
        "/* unevaluated computation - use |> compute to evaluate */"
    | IRCompute inner -> 
        // compute forces evaluation of a lazy computation
        match inner with
        | IRApplyCombinator info -> genApplyCombinatorExpr names info
        | _ -> exprToCpp names inner  // For non-combinator compute, just evaluate
    | IRPure e -> exprToCpp names e     // pure wraps value
    | IRRank arr -> 
        // Rank is known statically from the type
        let rank = match inferExprType arr with
                   | IRTArray at -> arrayRank at
                   | _ -> 0
        sprintf "%dL" rank
    | IRArity (Some n, _) -> sprintf "%d" n
    | IRArity (None, paramName) -> 
        // Arity of poly pack - use tuple_size on the named parameter
        sprintf "std::tuple_size_v<std::decay_t<decltype(%s)>>" paramName
    | IRBind (comp, cont) ->
        // Monadic bind - comp >>= cont
        sprintf "%s(%s)" (exprToCpp names cont) (exprToCpp names comp)
    | IRReynolds (kernel, isAntisym) ->
        // Reynolds operator wraps kernel
        sprintf "/* reynolds(%s, antisym=%b) */" (exprToCpp names kernel) isAntisym
    | IRZip arrs ->
        sprintf "/* zip(%s) */" (arrs |> List.map (exprToCpp names) |> String.concat ", ")
    | IRStack arrs ->
        sprintf "/* stack(%s) */" (arrs |> List.map (exprToCpp names) |> String.concat ", ")
    | IRSlice (arr, dim, start, stop) ->
        sprintf "/* slice(%s, dim=%d, %s:%s) */" (exprToCpp names arr) dim (exprToCpp names start) (exprToCpp names stop)
    | IRCurry (arr, idx, resultRank) ->
        sprintf "%s[%s]" (exprToCpp names arr) (exprToCpp names idx)
    | IRTupleCons (head, tail) ->
        sprintf "std::tuple_cat(std::make_tuple(%s), %s)" (exprToCpp names head) (exprToCpp names tail)
    | IRTupleDecons tuple ->
        exprToCpp names tuple  // Decons is handled by projection
    | IRMatch (scrutinee, cases) ->
        // Generate nested ternary for match expressions
        let scrut = exprToCpp names scrutinee
        let rec genCase (cases: IRMatchCase list) : string =
            match cases with
            | [] -> "0 /* no match */"
            | [case] ->
                // Last case - assume it matches (wildcard or variable)
                match case.Pattern with
                | IRPatVar varId ->
                    // Bind variable and evaluate body (only if variable is used)
                    if (collectVarRefs case.Body).Contains varId then
                        let varName = sprintf "__match_%d" varId
                        sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut (exprToCppWithVar names varId varName case.Body)
                    else
                        exprToCpp names case.Body
                | IRPatWild ->
                    exprToCpp names case.Body
                | IRPatLit lit ->
                    let litStr = litToCpp lit
                    let bodyStr = exprToCpp names case.Body
                    sprintf "(%s == %s ? %s : 0 /* no match */)" scrut litStr bodyStr
                | _ ->
                    exprToCpp names case.Body
            | case :: rest ->
                let restStr = genCase rest
                match case.Pattern with
                | IRPatLit lit ->
                    let litStr = litToCpp lit
                    let bodyStr = 
                        match case.Guard with
                        | Some guard -> 
                            let guardStr = exprToCpp names guard
                            sprintf "(%s ? %s : %s)" guardStr (exprToCpp names case.Body) restStr
                        | None -> exprToCpp names case.Body
                    sprintf "(%s == %s ? %s : %s)" scrut litStr bodyStr restStr
                | IRPatVar varId ->
                    let varUsed =
                        (collectVarRefs case.Body).Contains varId ||
                        (case.Guard |> Option.map (fun g -> (collectVarRefs g).Contains varId) |> Option.defaultValue false)
                    if varUsed then
                        let varName = sprintf "__match_%d" varId
                        match case.Guard with
                        | Some guard ->
                            // Variable pattern with guard, variable used
                            let guardStr = exprToCppWithVar names varId varName guard
                            let bodyStr = exprToCppWithVar names varId varName case.Body
                            sprintf "[&]() { auto %s = %s; return %s ? %s : %s; }()" varName scrut guardStr bodyStr restStr
                        | None ->
                            // Variable pattern without guard - always matches, variable used
                            let bodyStr = exprToCppWithVar names varId varName case.Body
                            sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut bodyStr
                    else
                        match case.Guard with
                        | Some guard ->
                            // Variable unused, but has guard
                            let guardStr = exprToCpp names guard
                            let bodyStr = exprToCpp names case.Body
                            sprintf "(%s ? %s : %s)" guardStr bodyStr restStr
                        | None ->
                            // Variable unused, no guard - always matches (like wildcard)
                            exprToCpp names case.Body
                | IRPatWild ->
                    match case.Guard with
                    | Some guard ->
                        let guardStr = exprToCpp names guard
                        let bodyStr = exprToCpp names case.Body
                        sprintf "(%s ? %s : %s)" guardStr bodyStr restStr
                    | None ->
                        // Wildcard without guard - always matches
                        exprToCpp names case.Body
                | IRPatTuple innerPats ->
                    // Tuple pattern - bind each element
                    let rec collectVarBindings (pats: IRPattern list) (idx: int) : (IRId * string) list =
                        match pats with
                        | [] -> []
                        | IRPatVar varId :: rest ->
                            let varName = sprintf "__match_%d" varId
                            (varId, varName) :: collectVarBindings rest (idx + 1)
                        | _ :: rest -> collectVarBindings rest (idx + 1)
                    
                    let bindings = collectVarBindings innerPats 0
                    let bindingDecls = bindings |> List.mapi (fun idx (_, name) ->
                        sprintf "auto %s = std::get<%d>(%s)" name idx scrut) |> String.concat "; "
                    
                    // Extend names map with bindings
                    let names' = bindings |> List.fold (fun acc (id, name) -> Map.add id name acc) names
                    
                    match case.Guard with
                    | Some guard ->
                        let guardStr = exprToCpp names' guard
                        let bodyStr = exprToCpp names' case.Body
                        sprintf "[&]() { %s; return %s ? %s : %s; }()" bindingDecls guardStr bodyStr restStr
                    | None ->
                        let bodyStr = exprToCpp names' case.Body
                        sprintf "[&]() { %s; return %s; }()" bindingDecls bodyStr
                | IRPatVariant (ctorName, tag, innerOpt) ->
                    // Variant pattern - check variant type and optionally bind inner value
                    let variantTypeName = sprintf "%s_T" ctorName
                    let checkExpr = sprintf "std::holds_alternative<%s>(%s)" variantTypeName scrut
                    
                    match innerOpt with
                    | Some (IRPatVar varId) ->
                        // Variant with inner value binding
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let extractExpr = sprintf "std::get<%s>(%s).value" variantTypeName scrut
                        let bodyStr = exprToCpp names' case.Body
                        sprintf "(%s ? [&]() { auto %s = %s; return %s; }() : %s)" checkExpr varName extractExpr bodyStr restStr
                    | Some _ ->
                        // Other inner patterns - fallback
                        let bodyStr = exprToCpp names case.Body
                        sprintf "(%s ? %s : %s)" checkExpr bodyStr restStr
                    | None ->
                        // Variant without inner value
                        let bodyStr = exprToCpp names case.Body
                        sprintf "(%s ? %s : %s)" checkExpr bodyStr restStr
                | _ ->
                    // Unsupported pattern - fallback
                    sprintf "(true ? %s : %s)" (exprToCpp names case.Body) restStr
        genCase cases
    | IRNth -> "/* nth */"
    | IRZero -> "0"
    | IRPolyIndex (pack, idx) ->
        // For static index, use std::get; otherwise runtime indexing
        match idx with
        | IRLit (IRLitInt n) -> sprintf "std::get<%d>(%s)" n (exprToCpp names pack)
        | _ -> sprintf "%s[%s]" (exprToCpp names pack) (exprToCpp names idx)
    | IRParallel (a, b, _) ->
        sprintf "/* parallel(%s, %s) */" (exprToCpp names a) (exprToCpp names b)
    | IRFusion (a, b) ->
        sprintf "/* fusion(%s, %s) */" (exprToCpp names a) (exprToCpp names b)
    | IRChoice (a, b) ->
        sprintf "/* choice(%s, %s) */" (exprToCpp names a) (exprToCpp names b)
    | IRCompose (f, g) ->
        sprintf "/* compose(%s, %s) */" (exprToCpp names f) (exprToCpp names g)
    | IRComposeObj (f, g) ->
        sprintf "/* compose_obj(%s, %s) */" (exprToCpp names f) (exprToCpp names g)
    | IRComposeMeth (f, g) ->
        sprintf "/* compose_meth(%s, %s) */" (exprToCpp names f) (exprToCpp names g)
    | IRArrayProduct (a, b) ->
        sprintf "/* array_product(%s, %s) */" (exprToCpp names a) (exprToCpp names b)
    | IRFunctorMap (f, c) ->
        sprintf "/* fmap(%s, %s) */" (exprToCpp names f) (exprToCpp names c)
    | IRAssign (id, value) ->
        let name = Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
        sprintf "%s = %s" name (exprToCpp names value)
    | _ -> "/* unsupported expr */"

/// Convert IRExpr to C++ with an additional variable binding
and exprToCppWithVar (names: Map<IRId, string>) (varId: IRId) (varName: string) (expr: IRExpr) : string =
    let names' = Map.add varId varName names
    exprToCpp names' expr

/// Convert IRExpr to C++ using context
let exprToCppCtx (ctx: CodeGenContext) (expr: IRExpr) : string =
    exprToCpp ctx.VarNames expr

// ============================================================================
// Loop Nest Code Generation
// ============================================================================

/// Generate the element binding expression for a single array at a loop level
/// Returns (cppCode, newPeeledName) where newPeeledName is used for subsequent levels
let genElementBindingNew (level: LoopIndexBinding) (elem: ElementBinding) (currentName: string) 
    : string * string =
    match elem.Virtual with
    | VirtualRange ->
        // range<I>: kernel param gets the loop index directly (index types are size_t)
        let code = sprintf "size_t %s = %s;" elem.ParamName level.IndexName
        (code, elem.ParamName)
    | VirtualReverse ->
        // reverse<I>: kernel param gets (extent - 1 - i)
        let extentStr =
            match level.Extent with
            | IRLit (IRLitInt n) -> sprintf "%d" n
            | _ -> sprintf "%s_extents[%d]" elem.ArrayName elem.DimIndex
        let code = sprintf "size_t %s = (%s - 1 - %s);" elem.ParamName extentStr level.IndexName
        (code, elem.ParamName)
    | RealArray ->
        // After indexing once, remaining rank decreases
        let levelsConsumed = elem.ArityComponent + 1  // How many levels of this array consumed so far
        let resultRank = elem.ArrayRank - levelsConsumed
        let elemTypeStr = elemTypeToCpp elem.ArrayElemType
        
        // For left-justified iteration, array index = current + sum of dependencies
        let arrayIndex = 
            if level.BoundDependencies.IsEmpty then
                level.IndexName
            else
                let deps = level.BoundDependencies |> List.map (sprintf "__i%d") |> String.concat " + "
                sprintf "%s + %s" level.IndexName deps
        
        let newName = sprintf "%s__%s" currentName level.IndexName
        let code =
            if resultRank <= 0 then
                sprintf "%s %s = %s[%s];" elemTypeStr newName currentName arrayIndex
            else
                sprintf "promote<%s, %d>::type %s = %s[%s];" elemTypeStr resultRank newName currentName arrayIndex
        (code, newName)

/// Generate a for-loop header with optional OpenMP pragma
/// Bounds are computed as: extent - sum of all dependency indices
let genForLoopHeader (binding: LoopIndexBinding) : string =
    let pragma = if binding.IsParallel then "#pragma omp parallel for\n    " else ""
    let extentStr = 
        match binding.Extent with
        | IRLit (IRLitInt n) -> sprintf "%d" n
        | _ -> sprintf "%s_extents[%d]" binding.ExtentArrayRef binding.ExtentDimRef
    
    // Compute bound subtraction from dependencies
    let subtraction =
        if binding.BoundDependencies.IsEmpty && binding.StrictOffset = 0 then ""
        else 
            let depParts = binding.BoundDependencies |> List.map (sprintf "__i%d")
            let offsetParts = if binding.StrictOffset > 0 then [sprintf "%d" binding.StrictOffset] else []
            depParts @ offsetParts |> String.concat " - " |> sprintf " - %s"
    
    sprintf "%sfor (size_t %s = 0; %s < %s%s; %s++) {" 
        pragma
        binding.IndexName 
        binding.IndexName 
        extentStr
        subtraction
        binding.IndexName

/// Generate complete loop nest as C++ code
/// Tracks peeled names across levels and generates element bindings for all arrays at each level
let genLoopNest (codeGen: LoopNestCodeGen) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> = 
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    
    // Track final peeled name for each param VarId (for kernel body substitution)
    let mutable paramFinalNames : Map<int, string> = Map.empty
    
    // Generate nested loops with element bindings
    for binding in codeGen.Bindings do
        // Generate the loop header
        lines <- lines @ [ind depth + genForLoopHeader binding]
        depth <- depth + 1
        
        // Generate element bindings for all arrays at this level
        for elem in binding.Elements do
            let currentName = 
                Map.tryFind elem.ArrayPosition currentNames 
                |> Option.defaultValue elem.ArrayName
            let (code, newName) = genElementBindingNew binding elem currentName
            lines <- lines @ [ind depth + code]
            currentNames <- Map.add elem.ArrayPosition newName currentNames
            // Record mapping for kernel body
            match elem.Virtual with
            | VirtualRange | VirtualReverse ->
                paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
            | RealArray ->
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Build name map for kernel body from final peeled names
    let nameMap = paramFinalNames
    let nameMap =
        codeGen.Captures
        |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
    let kernelStr = exprToCpp nameMap codeGen.KernelExpr
    
    // Generate kernel assignment
    let outputIdx = 
        codeGen.Bindings 
        |> List.map (fun b -> sprintf "[%s]" b.IndexName)
        |> String.concat ""
    lines <- lines @ [ind depth + sprintf "%s%s = %s;" codeGen.OutputName outputIdx kernelStr]
    
    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]
    
    lines

/// Check if a binding has triangular bounds (has dependencies to subtract)
let isTriangularBound (binding: LoopIndexBinding) : bool =
    not binding.BoundDependencies.IsEmpty

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
     "#include <cstdlib>"  // for rand()
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "// Note: OpenMP disabled for portability"
     "// #include <omp.h>"
     "#include \"nested_array_utilities.cpp\""
     "using namespace nested_array_utilities;"
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""]

/// Generate the runtime library as an embedded string (for self-contained output)
let genEmbeddedRuntime () : string =
    """
// ============================================================================
// Embedded Runtime: nested_array_utilities
// ============================================================================

#include <algorithm>
#include <cstddef>
#include <type_traits>

namespace nested_array_utilities {

    template<typename TYPE>
    constexpr const size_t get_rank() {
        if constexpr (std::is_pointer<TYPE>::value) {
            return 1 + get_rank<typename std::remove_pointer<TYPE>::type>();
        } else {
            return 0;
        }
    }

    template<typename TYPE, const size_t rank, const size_t depth = 0>
    constexpr auto promote_impl() {
        if constexpr (depth < rank) {
            return promote_impl<typename std::add_pointer<TYPE>::type, rank, depth + 1>();
        } else if constexpr (depth == rank) {
            TYPE dummy = {0};
            return dummy;
        } else {
            return;
        }
    }

    template<typename TYPE, const size_t rank, const size_t depth = 0>
    class promote {
    public:
        typedef decltype(promote_impl<TYPE, rank>()) type;
    };

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr TYPE allocate(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        TYPE array;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            array = new DTYPE[extents[DEPTH] - lastIndex];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents, i + lastIndex);
                    } else {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents);
                    }
                }
            }
        } else {
            array = new DTYPE[extents[DEPTH]];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents, i);
                    } else {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents);
                    }
                }
            }
        }
        return array;
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_random(TYPE array_in, const size_t extents[], int mod_in, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in, i + lastIndex);
                    } else {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    array_in[i] = rand() % mod_in;
                }
            }
        } else {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in, i);
                    } else {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array_in[i] = rand() % mod_in;
                }
            }
        }
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_value(TYPE array_in, const size_t extents[], 
                              typename std::remove_pointer<TYPE>::type value, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value, i + lastIndex);
                    } else {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    array_in[i] = value;
                }
            }
        } else {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value, i);
                    } else {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array_in[i] = value;
                }
            }
        }
    }


    // =========================================================================
    // Antisymmetric array support
    // =========================================================================

    // Allocate antisymmetric array: strict i < j, so n-1 elements in first row,
    // n-2 in second, etc. Total: n(n-1)/2
    // Same nested pointer structure as symmetric but with -1 offset at each level.
    template<typename TYPE, const size_t DEPTH = 0>
    constexpr TYPE allocate_antisym(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        TYPE array;

        if constexpr (DEPTH == 0) {
            // Outermost: full extent (rows)
            array = new DTYPE[extents[DEPTH]];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array[i] = allocate_antisym<DTYPE, DEPTH + 1>(extents, i + 1);
                }
            }
        } else {
            // Inner: strict bound, extent - lastIndex elements
            size_t len = (extents[DEPTH] > lastIndex) ? extents[DEPTH] - lastIndex : 0;
            array = new DTYPE[len];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < len; i++) {
                    array[i] = allocate_antisym<DTYPE, DEPTH + 1>(extents, i + lastIndex);
                }
            }
        }
        return array;
    }

    // =========================================================================
    // Index canonicalization wrappers
    // =========================================================================

    // Symmetric canonicalization: (i,j) -> (min(i,j), max(i,j))
    inline void sym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        ci = (i <= j) ? i : j;
        cj = (i <= j) ? j : i;
    }

    // Antisymmetric canonicalization: (i,j) -> (min(i,j), max(i,j)), sign = +1 or -1
    // Returns -1 if swapped (odd permutation), +1 if not
    inline int antisym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i < j) { ci = i; cj = j; return 1; }
        else if (i > j) { ci = j; cj = i; return -1; }
        else { ci = i; cj = j; return 0; }  // diagonal: value is zero
    }

    // Hermitian canonicalization: (i,j) -> (min(i,j), max(i,j)), needs_conj flag
    // For Hermitian: A(i,j) = conj(A(j,i)), so access with j<i needs conjugation
    inline bool hermitian_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i <= j) { ci = i; cj = j; return false; }  // no conjugation needed
        else { ci = j; cj = i; return true; }           // needs conjugation
    }

} // namespace nested_array_utilities

using namespace nested_array_utilities;
"""

/// Generate the runtime header file content (for separate file)
let genRuntimeHeader () : string =
    """#pragma once
// nested_array_utilities.hpp
// Blade DSL Runtime Support Library

#include <algorithm>
#include <cstddef>
#include <type_traits>

namespace nested_array_utilities {

    template<typename TYPE>
    constexpr const size_t get_rank() {
        if constexpr (std::is_pointer<TYPE>::value) {
            return 1 + get_rank<typename std::remove_pointer<TYPE>::type>();
        } else {
            return 0;
        }
    }

    template<typename TYPE, const size_t rank, const size_t depth = 0>
    constexpr auto promote_impl() {
        if constexpr (depth < rank) {
            return promote_impl<typename std::add_pointer<TYPE>::type, rank, depth + 1>();
        } else if constexpr (depth == rank) {
            TYPE dummy = {0};
            return dummy;
        } else {
            return;
        }
    }

    template<typename TYPE, const size_t rank, const size_t depth = 0>
    class promote {
    public:
        typedef decltype(promote_impl<TYPE, rank>()) type;
    };

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr TYPE allocate(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        TYPE array;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH-1] == SYMM[DEPTH]) {
            array = new DTYPE[extents[DEPTH] - lastIndex];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents, i + lastIndex);
                    } else {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents);
                    }
                }
            }
        } else {
            array = new DTYPE[extents[DEPTH]];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents, i);
                    } else {
                        array[i] = allocate<DTYPE, SYMM, DEPTH + 1>(extents);
                    }
                }
            }
        }
        return array;
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_random(TYPE array_in, const size_t extents[], int mod_in, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in, i + lastIndex);
                    } else {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    array_in[i] = rand() % mod_in;
                }
            }
        } else {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in, i);
                    } else {
                        fill_random<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, mod_in);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array_in[i] = rand() % mod_in;
                }
            }
        }
    }

    template<typename TYPE, const size_t SYMM[] = nullptr, const size_t DEPTH = 0>
    constexpr void fill_value(TYPE array_in, const size_t extents[], 
                              typename std::remove_pointer<TYPE>::type value, size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;

        if constexpr ((bool)SYMM && DEPTH > 0 && SYMM[DEPTH - 1] == SYMM[DEPTH]) {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value, i + lastIndex);
                    } else {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH] - lastIndex; i++) {
                    array_in[i] = value;
                }
            }
        } else {
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    if constexpr ((bool)SYMM && SYMM[DEPTH] == SYMM[DEPTH + 1]) {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value, i);
                    } else {
                        fill_value<DTYPE, SYMM, DEPTH + 1>(array_in[i], extents, value);
                    }
                }
            } else {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array_in[i] = value;
                }
            }
        }
    }


    // =========================================================================
    // Antisymmetric array support
    // =========================================================================

    // Allocate antisymmetric array: strict i < j, so n-1 elements in first row,
    // n-2 in second, etc. Total: n(n-1)/2
    // Same nested pointer structure as symmetric but with -1 offset at each level.
    template<typename TYPE, const size_t DEPTH = 0>
    constexpr TYPE allocate_antisym(const size_t extents[], const size_t lastIndex = 0) {
        typedef typename std::remove_pointer<TYPE>::type DTYPE;
        TYPE array;

        if constexpr (DEPTH == 0) {
            // Outermost: full extent (rows)
            array = new DTYPE[extents[DEPTH]];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < extents[DEPTH]; i++) {
                    array[i] = allocate_antisym<DTYPE, DEPTH + 1>(extents, i + 1);
                }
            }
        } else {
            // Inner: strict bound, extent - lastIndex elements
            size_t len = (extents[DEPTH] > lastIndex) ? extents[DEPTH] - lastIndex : 0;
            array = new DTYPE[len];
            if constexpr (std::is_pointer<DTYPE>::value) {
                for (size_t i = 0; i < len; i++) {
                    array[i] = allocate_antisym<DTYPE, DEPTH + 1>(extents, i + lastIndex);
                }
            }
        }
        return array;
    }

    // =========================================================================
    // Index canonicalization wrappers
    // =========================================================================

    // Symmetric canonicalization: (i,j) -> (min(i,j), max(i,j))
    inline void sym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        ci = (i <= j) ? i : j;
        cj = (i <= j) ? j : i;
    }

    // Antisymmetric canonicalization: (i,j) -> (min(i,j), max(i,j)), sign = +1 or -1
    // Returns -1 if swapped (odd permutation), +1 if not
    inline int antisym_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i < j) { ci = i; cj = j; return 1; }
        else if (i > j) { ci = j; cj = i; return -1; }
        else { ci = i; cj = j; return 0; }  // diagonal: value is zero
    }

    // Hermitian canonicalization: (i,j) -> (min(i,j), max(i,j)), needs_conj flag
    // For Hermitian: A(i,j) = conj(A(j,i)), so access with j<i needs conjugation
    inline bool hermitian_canonical(size_t i, size_t j, size_t& ci, size_t& cj) {
        if (i <= j) { ci = i; cj = j; return false; }  // no conjugation needed
        else { ci = j; cj = i; return true; }           // needs conjugation
    }

} // namespace nested_array_utilities

using namespace nested_array_utilities;
"""

/// Generate includes that reference external header
let genIncludesExternal () : string list =
    ["#include <cstdint>"
     "#include <cstdlib>"
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <set>"
     "#include <omp.h>"
     "#include \"nested_array_utilities.hpp\""
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""]

/// Generate includes with embedded runtime (self-contained)
let genIncludesEmbedded () : string list =
    ["#include <cstdint>"
     "#include <cstdlib>"
     "#include <cmath>"
     "#include <complex>"
     "#include <functional>"
     "#include <tuple>"
     "#include <variant>"
     "#include <string>"
     "#include <iostream>"
     "#include <iomanip>"
     "#include <chrono>"
     "#include <set>"
     "#include <omp.h>"
     "using std::cout;"
     "using std::endl;"
     ""
     "#define TIME std::chrono::high_resolution_clock::now()"
     "#define TIME_DIFF std::chrono::duration_cast<std::chrono::nanoseconds>(end - start).count()"
     ""
     genEmbeddedRuntime ()
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

// ============================================================================
// Array Literal Generation
// ============================================================================

/// Extract float values from array literal for initialization
let rec extractLiteralValues (expr: IRExpr) : float list =
    match expr with
    | IRLit (IRLitFloat f) -> [f]
    | IRLit (IRLitInt n) -> [float n]
    | IRArrayLit (elements, _) -> elements |> List.collect extractLiteralValues
    | _ -> []

/// Compute dimensions of an array literal
let rec computeArrayDims (expr: IRExpr) : int list =
    match expr with
    | IRArrayLit (elements, _) ->
        let thisLen = elements.Length
        match elements with
        | first :: _ -> thisLen :: computeArrayDims first
        | [] -> [0]
    | _ -> []

/// Generate code to allocate and initialize an array from literal values
let genArrayLiteral (ctx: CodeGenContext) (varName: string) (elements: IRExpr list) (arrType: IRArrayType) : string list =
    let ind = indentStr ctx
    let elemType = elemTypeToCpp arrType.ElemType
    let rank = arrayRank arrType
    
    // Get dimensions
    let dims = computeArrayDims (IRArrayLit (elements, arrType))
    
    if dims.IsEmpty then
        [sprintf "%s// Empty array literal" ind]
    else
        // Generate extents declaration
        let extentsValues = dims |> List.map string |> String.concat ", "
        let extentsDecl = sprintf "%sstatic constexpr const size_t %s_extents[%d] = {%s};" 
                            ind varName rank extentsValues
        
        // Generate allocation
        let allocDecl = sprintf "%spromote<%s, %d>::type %s;" ind elemType rank varName
        let allocInit = sprintf "%s%s = allocate<typename promote<%s, %d>::type, nullptr>(%s_extents);" 
                            ind varName elemType rank varName
        
        // Generate initialization
        let values = extractLiteralValues (IRArrayLit (elements, arrType))
        
        if rank = 1 then
            // 1D: direct loop initialization
            let initCode = values |> List.mapi (fun i v -> 
                sprintf "%s%s[%d] = %g;" ind varName i v)
            [extentsDecl; allocDecl; allocInit] @ initCode
        elif rank = 2 && dims.Length >= 2 then
            // 2D: nested loop initialization
            let rows = dims.[0]
            let cols = dims.[1]
            let mutable initCode = []
            for i in 0 .. rows - 1 do
                for j in 0 .. cols - 1 do
                    let idx = i * cols + j
                    if idx < values.Length then
                        initCode <- initCode @ [sprintf "%s%s[%d][%d] = %g;" ind varName i j values.[idx]]
            [extentsDecl; allocDecl; allocInit] @ initCode
        else
            // Higher dimensions: use a flat initialization array and nested loops
            let flatValues = values |> List.map (sprintf "%g") |> String.concat ", "
            [
                extentsDecl
                allocDecl
                allocInit
                sprintf "%s// Initialize from literal values" ind
                sprintf "%s{" ind
                sprintf "%s    %s __init_values[] = {%s};" ind elemType flatValues
                sprintf "%s    size_t __idx = 0;" ind
                sprintf "%s    // TODO: Generate nested initialization loops for rank %d" ind rank
                sprintf "%s}" ind
            ]

/// Generate code for a scalar binding
let genScalarBinding (ctx: CodeGenContext) (name: string) (value: IRExpr) (ty: IRType) : string list =
    let ind = indentStr ctx
    let resolvedTy = match ty with IRTInfer _ -> inferExprType value | t -> t
    match resolvedTy with
    | IRTUnit ->
        // Unit-typed binding: emit expression as statement, no variable declaration
        let valueStr = exprToCppCtx ctx value
        [sprintf "%s%s;" ind valueStr]
    | _ ->
        let cppType = irTypeToCpp resolvedTy
        let valueStr = exprToCppCtx ctx value
        [sprintf "%s%s %s = %s;" ind cppType name valueStr]

// ============================================================================
// Loop Application Code Generation
// ============================================================================

/// Generate the complete code for a combinator application (L <@> f)
let genApplyCombinator (ctx: CodeGenContext) (name: string) (info: ApplyInfo) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // Extract array names from the method_for (now properly resolved)
    let arrayNames = 
        match info.Loop with
        | IRMethodFor mfInfo ->
            mfInfo.Arrays |> List.mapi (fun i arr ->
                match arr with
                | IRVar (id, _) -> 
                    Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                | IRRange _ -> sprintf "__range%d" i
                | IRVirtualReverse _ -> sprintf "__rev%d" i
                | IRBlocked _ -> sprintf "__blk%d" i
                | _ -> sprintf "arr%d" i)
        | _ -> []
    
    if arrayNames.IsEmpty then
        [sprintf "%s// Cannot generate code: no arrays in method_for" ind]
    else
        // Build LoopNestCodeGen (handles both outer product and co-iteration)
        let codeGen = buildLoopNestCodeGen info arrayNames name builder
        
        // Generate static declarations for symmetry vector
        let symmVecName = sprintf "%s_symm" name
        let symmVecDecl = 
            if codeGen.OutputSymmVec.IsEmpty then
                sprintf "%sstatic constexpr const size_t* %s = nullptr;" ind symmVecName
            else
                let values = codeGen.OutputSymmVec |> List.map string |> String.concat ", "
                sprintf "%sstatic constexpr const size_t %s[%d] = {%s};" ind symmVecName codeGen.OutputSymmVec.Length values
        
        // Get output rank and type info
        let outputRank = 
            match codeGen.OutputType with
            | IRTArray arr -> arrayRank arr
            | IRTScalar _ -> 0
            | _ -> 0
        
        let outputElemType =
            match codeGen.OutputType with
            | IRTArray arr -> elemTypeToCpp arr.ElemType
            | IRTScalar et -> elemTypeToCpp et
            | t -> irTypeToCpp t
        
        // Generate extent computation
        let extentsName = sprintf "%s_extents" name
        let extentsDecl = sprintf "%ssize_t* %s = new size_t[%d];" ind extentsName outputRank
        
        // Fill extents from loop level info
        let extentsFill = 
            codeGen.Bindings |> List.mapi (fun i b ->
                match b.Extent with
                | IRLit (IRLitInt n) ->
                    sprintf "%s%s[%d] = %s;" ind extentsName i (sprintf "%d" n)
                | _ ->
                    sprintf "%s%s[%d] = %s_extents[%d];" ind extentsName i b.ExtentArrayRef b.ExtentDimRef)
        
        // Generate allocation
        let allocDecl = sprintf "%spromote<%s, %d>::type %s;" ind outputElemType outputRank name
        let allocInit = sprintf "%s%s = allocate<typename promote<%s, %d>::type, %s>(%s);" 
                            ind name outputElemType outputRank symmVecName extentsName
        
        // Generate loop nest
        let loopCode = genLoopNest codeGen ctx.Indent
        
        // Combine all
        [symmVecDecl; ""; extentsDecl] @ extentsFill @ [""; allocDecl; allocInit; ""] @ loopCode

/// Generate C++ code for inline object_for application (e.g., A [+] B)
let genObjectForApplication (ctx: CodeGenContext) (name: string) (objInfo: ObjectForInfo) (arrays: IRExpr list) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // Get array names
    let arrayNames = arrays |> List.mapi (fun i arr ->
        match arr with
        | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
        | _ -> sprintf "arr%d" i)
    
    // Get kernel info
    let (kernelParams, kernelBody) =
        match objInfo.Kernel with
        | IRLambda lInfo -> (lInfo.Params, lInfo.Body)
        | _ -> ([], IRLit IRLitUnit)
    
    // Infer element type from kernel params
    let elemTypeStr =
        kernelParams |> List.tryPick (fun p ->
            match p.Type with IRTScalar et -> Some (elemTypeToCpp et) | IRTArray arr -> Some (elemTypeToCpp arr.ElemType) | _ -> None)
        |> Option.defaultValue (match kernelParams with p :: _ -> irTypeToCpp p.Type | [] -> "void")
    
    // For outer product (InputRanks = [1; 1], OutputRank = 2), generate nested loops
    // For elementwise (InputRanks = [0; 0], OutputRank = 1), generate single loop
    
    match objInfo.InputRanks, arrayNames with
    | [1; 1], [arrA; arrB] ->
        // Outer product: result[i][j] = kernel(A[i], B[j])
        let extentsDecl = sprintf "%ssize_t %s_extents[2] = {%s_extents[0], %s_extents[0]};" ind name arrA arrB
        let allocDecl = sprintf "%spromote<%s, 2>::type %s = allocate<promote<%s, 2>::type>(%s_extents);" ind elemTypeStr name elemTypeStr name
        
        // Build name map for kernel body
        let bodyNames = 
            kernelParams 
            |> List.mapi (fun i p -> (p.VarId, sprintf "%s_%s" (if i = 0 then arrA else arrB) "__i"))
            |> Map.ofList
            |> Map.fold (fun acc k v -> Map.add k v acc) ctx.VarNames
        
        let kernelStr = exprToCpp bodyNames kernelBody
        
        let loopCode = [
            sprintf "%sfor (size_t __i0 = 0; __i0 < %s_extents[0]; __i0++) {" ind arrA
            sprintf "%s    %s %s___i = %s[__i0];" ind elemTypeStr arrA arrA
            sprintf "%s    for (size_t __i1 = 0; __i1 < %s_extents[0]; __i1++) {" ind arrB
            sprintf "%s        %s %s___i = %s[__i1];" ind elemTypeStr arrB arrB
            sprintf "%s        %s[__i0][__i1] = %s;" ind name kernelStr
            sprintf "%s    }" ind
            sprintf "%s}" ind
        ]
        
        [extentsDecl; allocDecl; ""] @ loopCode
        
    | [0; 0], [arrA; arrB] ->
        // Elementwise: result[i] = kernel(A[i], B[i])
        let extentsDecl = sprintf "%ssize_t %s_extents[1] = {%s_extents[0]};" ind name arrA
        let allocDecl = sprintf "%spromote<%s, 1>::type %s = allocate<promote<%s, 1>::type>(%s_extents);" ind elemTypeStr name elemTypeStr name
        
        // Build name map for kernel body
        let bodyNames = 
            kernelParams 
            |> List.mapi (fun i p -> (p.VarId, sprintf "%s___i" (if i = 0 then arrA else arrB)))
            |> Map.ofList
            |> Map.fold (fun acc k v -> Map.add k v acc) ctx.VarNames
        
        let kernelStr = exprToCpp bodyNames kernelBody
        
        let loopCode = [
            sprintf "%sfor (size_t __i0 = 0; __i0 < %s_extents[0]; __i0++) {" ind arrA
            sprintf "%s    %s %s___i = %s[__i0];" ind elemTypeStr arrA arrA
            sprintf "%s    %s %s___i = %s[__i0];" ind elemTypeStr arrB arrB
            sprintf "%s    %s[__i0] = %s;" ind name kernelStr
            sprintf "%s}" ind
        ]
        
        [extentsDecl; allocDecl; ""] @ loopCode
        
    | _ ->
        // Unsupported configuration
        [sprintf "%s// Unsupported object_for configuration for %s" ind name]

// ============================================================================
// Binding Generation
// ============================================================================

/// Unroll an IRLet chain into a list of (varId, valueExpr) statements and a final return expression.
/// e.g., IRLet(id1, v1, IRLet(id2, v2, body)) → statements=[(id1,v1), (id2,v2)], return=body
let rec unrollLetChain (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
    match expr with
    | IRLet (id, value, body) ->
        let (rest, final) = unrollLetChain body
        ((id, value) :: rest, final)
    | _ -> ([], expr)

/// Generate C++ code for an IR binding
let rec genBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = binding.Name
    
    match binding.Value with
    | IRArrayLit (elements, arrType) ->
        let code = genArrayLiteral ctx name elements arrType
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRApplyCombinator info ->
        let code = genApplyCombinator ctx name info builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRCompute inner ->
        // Compute unwraps - handle the inner expression
        match inner with
        | IRApplyCombinator info ->
            let code = genApplyCombinator ctx name info builder
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
        | _ ->
            // Other compute expressions - treat as scalar
            let code = genScalarBinding ctx name inner binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRMethodFor _ ->
        // method_for creates a loop object - no runtime code needed
        // Just track the variable name for later use
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = method_for(...) [loop object]" ind name], ctx')
    
    | IRObjectFor _ ->
        // object_for creates a loop object - no runtime code needed
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = object_for(...) [loop object]" ind name], ctx')
    
    | IRApp (IRObjectFor objInfo, args, _) ->
        // Inline application of object_for - need to expand to loop nest
        // This handles cases like: let added = A [+] B
        // Convert to an ApplyCombinator-like structure and generate
        let arrays = 
            match args with
            | [IRTuple elems] -> elems
            | _ -> args
        let code = genObjectForApplication ctx name objInfo arrays builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRLambda info ->
        // Generate C++ lambda for named functions
        let paramList = info.Params |> List.map (fun p -> 
            sprintf "%s %s" (irTypeToCpp p.Type) p.Name) |> String.concat ", "
        // Build name map with params and captures
        let bodyNames = info.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) Map.empty
        let bodyNames = info.Captures |> List.fold (fun m c -> Map.add c.Id c.Name m) bodyNames
        let bodyStr = exprToCpp bodyNames info.Body
        // Return type from binding annotation or inferred from body
        let retType = 
            match binding.Type with
            | IRTFunc (_, ret) -> irTypeToCpp ret
            | _ -> irTypeToCpp (inferExprType info.Body)
        // Build std::function type: std::function<RetType(P1Type, P2Type, ...)>
        let paramTypeList = info.Params |> List.map (fun p -> irTypeToCpp p.Type) |> String.concat ", "
        let funcType = sprintf "std::function<%s(%s)>" retType paramTypeList
        let code = [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType name paramList retType bodyStr]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRStructLit (typeName, _) ->
        // Struct construction - emit binding then validate() if constrained
        let code = genScalarBinding ctx name binding.Value binding.Type
        let validateCode =
            if ctx.ConstrainedStructs.Contains typeName then
                [sprintf "%s%s.validate();" ind name]
            else []
        let ctx' = addVarName binding.Id name ctx
        (code @ validateCode, ctx')
    
    | IRTuple _ | IRTupleProj _ | IRFieldAccess _ | IRLit _ | IRBinOp _ | IRUnaryOp _ | IRIf _ | IRVar _ | IRApp _ | IRParam _ | IRMatch _
    | IRBind _ | IRPure _ | IRIndex _ | IRSequence _ | IRGuard _ ->
        // Scalar expressions including tuples, field access, match, bind, pure
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRLet _ ->
        // Block expression: unroll the IRLet chain into sequential bindings
        let (lets, finalExpr) = unrollLetChain binding.Value
        let mutable currentCtx = ctx
        let mutable allCode = []
        for (id, value) in lets do
            let tempName = sprintf "__v%d" id
            let tempBinding = {
                Id = id; Name = tempName; Type = inferExprType value
                Value = value; IsConst = true; IsMutable = false
            }
            let (code, ctx') = genBinding currentCtx tempBinding builder
            allCode <- allCode @ code
            currentCtx <- ctx'
        // Generate the final named binding
        let finalBinding = {
            Id = binding.Id; Name = name; Type = binding.Type
            Value = finalExpr; IsConst = binding.IsConst; IsMutable = binding.IsMutable
        }
        let (finalCode, finalCtx) = genBinding currentCtx finalBinding builder
        (allCode @ finalCode, finalCtx)

    | IRAssign _ ->
        // Assignment expression: generate as statement
        let code = [sprintf "%s%s;" ind (exprToCppCtx ctx binding.Value)]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | _ ->
        let ctx' = addVarName binding.Id name ctx
        ([sprintf "%s// %s = <unsupported expression>" ind name], ctx')

// ============================================================================
// Module Generation
// ============================================================================

/// Generate a function body as a list of C++ statements.
/// Unrolls IRLet chains into sequential variable declarations with a final return.
let genFuncBody (names: Map<IRId, string>) (indent: string) (body: IRExpr) : string list =
    let (lets, retExpr) = unrollLetChain body
    let mutable currentNames = names
    let stmts = lets |> List.collect (fun (id, value) ->
        let varName = sprintf "__v%d" id
        let valStr = exprToCpp currentNames value
        currentNames <- Map.add id varName currentNames
        [sprintf "%sauto %s = %s;" indent varName valStr])
    let retStr = exprToCpp currentNames retExpr
    stmts @ [sprintf "%sreturn %s;" indent retStr]

let genFuncDef (ctx: CodeGenContext) (funcDef: IRFuncDef) : string list * CodeGenContext =
    let ind = indentStr ctx
    let bodyInd = ind + "    "
    
    // For each array parameter, also generate an extents parameter
    let paramList = 
        funcDef.Params 
        |> List.collect (fun p -> 
            match p.Type with
            | IRTArray arr ->
                let rank = arrayRank arr
                // Array param + its extents
                [sprintf "%s %s" (irTypeToCpp p.Type) p.Name
                 sprintf "const size_t* %s_extents" p.Name]
            | _ ->
                [sprintf "%s %s" (irTypeToCpp p.Type) p.Name])
        |> String.concat ", "
    
    // Use declared return type, or infer from body as fallback
    let retType = 
        match funcDef.RetType with
        | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)  // Should not happen with typed IR
        | t -> irTypeToCpp t
    
    // Build name map with params on top of module-level names
    let bodyNames = funcDef.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) ctx.VarNames
    
    // Generate proper C++ function
    let safeName = sanitizeCppName funcDef.Name
    let bodyStmts = genFuncBody bodyNames bodyInd funcDef.Body
    let code =
        [sprintf "%s%s %s(%s) {" ind retType safeName paramList]
        @ bodyStmts
        @ [sprintf "%s}" ind]
    
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate a function as a C++ lambda (for functions that capture module-level bindings)
let genFuncDefAsLambda (ctx: CodeGenContext) (funcDef: IRFuncDef) : string list * CodeGenContext =
    let ind = indentStr ctx
    
    let paramList = 
        funcDef.Params 
        |> List.collect (fun p -> 
            match p.Type with
            | IRTArray arr ->
                [sprintf "%s %s" (irTypeToCpp p.Type) p.Name
                 sprintf "const size_t* %s_extents" p.Name]
            | _ ->
                [sprintf "%s %s" (irTypeToCpp p.Type) p.Name])
        |> String.concat ", "
    
    let retType = 
        match funcDef.RetType with
        | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)
        | t -> irTypeToCpp t
    
    let bodyNames = funcDef.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) ctx.VarNames
    let bodyStr = exprToCpp bodyNames funcDef.Body
    let safeName = sanitizeCppName funcDef.Name
    // Build std::function type with all parameter types (including extent params)
    let paramTypeList =
        funcDef.Params
        |> List.collect (fun p ->
            match p.Type with
            | IRTArray _ -> [irTypeToCpp p.Type; "const size_t*"]
            | _ -> [irTypeToCpp p.Type])
        |> String.concat ", "
    let funcType = sprintf "std::function<%s(%s)>" retType paramTypeList
    let code = [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType safeName paramList retType bodyStr]
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate C++ code for an entire IR module.
/// Returns (functionDefs, bindingCode) — functions go outside main(), bindings inside.
let genModule (modul: IRModule) (builder: IRBuilder) : string list * string list =
    let mutable ctx = emptyContext ()
    let mutable funcCode = []
    let mutable bindCode = []
    
    // Collect constrained struct names from type definitions
    let constrainedNames = 
        modul.Types |> List.choose (function
            | IRTDStruct (name, _, Some _) -> Some name
            | _ -> None)
        |> Set.ofList
    ctx <- { ctx with ConstrainedStructs = constrainedNames }
    
    // First pass: register ALL names (both bindings and functions) in context
    for binding in modul.Bindings do
        ctx <- addVarName binding.Id binding.Name ctx
    for funcDef in modul.Functions do
        ctx <- addVarName funcDef.Id funcDef.Name ctx
    
    // Build a combined list of items with their "order" based on ID
    // Lower IDs were created earlier in the source
    let bindingItems = modul.Bindings |> List.map (fun b -> (b.Id, Choice1Of2 b))
    let funcItems = modul.Functions |> List.map (fun f -> (f.Id, Choice2Of2 f))
    let allItems = bindingItems @ funcItems |> List.sortBy fst
    
    // Build set of all function IDs (to exclude from free var checks)
    let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    
    // Generate in ID order (approximates source order)
    // First, collect file-scope functions to generate forward declarations
    let fileScopeFuncs = ResizeArray<IRFuncDef>()
    for (_, item) in allItems do
        match item with
        | Choice2Of2 funcDef ->
            let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
            // Exclude self-reference AND other function IDs from free var check
            let freeVars = Set.difference (collectVarRefs funcDef.Body) (Set.union paramIds funcIds)
            let hasFreeVars = freeVars |> Set.exists (fun id -> Map.containsKey id ctx.VarNames)
            if not hasFreeVars then fileScopeFuncs.Add(funcDef)
        | _ -> ()
    
    // Generate forward declarations for all file-scope functions
    for funcDef in fileScopeFuncs do
        let paramList = 
            funcDef.Params 
            |> List.collect (fun p -> 
                match p.Type with
                | IRTArray _ ->
                    [sprintf "%s %s" (irTypeToCpp p.Type) p.Name
                     sprintf "const size_t* %s_extents" p.Name]
                | _ ->
                    [sprintf "%s %s" (irTypeToCpp p.Type) p.Name])
            |> String.concat ", "
        let retType = 
            match funcDef.RetType with
            | IRTInfer _ -> irTypeToCpp (inferExprType funcDef.Body)
            | t -> irTypeToCpp t
        let safeName = sanitizeCppName funcDef.Name
        funcCode <- funcCode @ [sprintf "%s %s(%s);" retType safeName paramList]
    if fileScopeFuncs.Count > 0 then funcCode <- funcCode @ [""]
    
    for (_, item) in allItems do
        match item with
        | Choice1Of2 binding ->
            let (code, ctx') = genBinding ctx binding builder
            bindCode <- bindCode @ code @ [""]
            ctx <- ctx'
        | Choice2Of2 funcDef ->
            // Check if function has free variables (references to module bindings)
            let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
            // Exclude self-reference AND other function IDs from free var check
            let freeVars = Set.difference (collectVarRefs funcDef.Body) (Set.union paramIds funcIds)
            let hasFreeVars = freeVars |> Set.exists (fun id -> Map.containsKey id ctx.VarNames)
            
            if hasFreeVars then
                // Generate as C++ lambda inside main() (captures module bindings)
                let (code, ctx') = genFuncDefAsLambda ctx funcDef
                bindCode <- bindCode @ code @ [""]
                ctx <- ctx'
            else
                // Pure function — generate at file scope
                let (code, ctx') = genFuncDef ctx funcDef
                funcCode <- funcCode @ code @ [""]
                ctx <- ctx'
    
    (funcCode, bindCode)

/// Generate a complete C++ program with main() from an IR module
let genMainProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    
    let includes = genIncludes ()
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    
    let mainFunc = 
        ["int main() {"
         "    cout << std::setprecision(15);"
         "    auto start = TIME;"
         ""]
        @ bodyIndented
        @ [""
           "    auto end = TIME;"
           "    double elapsed = 1e-9 * TIME_DIFF;"
           sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
           "    return 0;"
           "}"]
    
    (includes @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// Generate a complete C++ program from an IR program (all modules)
let genProgramFromIR (program: IRProgram) (testName: string) : string =
    match program.Modules with
    | [] -> "// Empty program\nint main() { return 0; }\n"
    | [modul] -> genMainProgram modul testName
    | modules ->
        let merged = {
            Name = "merged"
            Types = modules |> List.collect (fun m -> m.Types)
            Functions = modules |> List.collect (fun m -> m.Functions)
            Bindings = modules |> List.collect (fun m -> m.Bindings)
            StaticFunctionUsage = Map.empty
        }
        genMainProgram merged testName

/// Generate C++ struct definition from IRTDStruct
let genStructDef (name: string) (fields: (string * IRType) list) (invariant: StructConstraintInfo option) : string list =
    let fieldLines = fields |> List.map (fun (fname, fty) ->
        sprintf "    %s %s;" (irTypeToCpp fty) fname)
    let validateLines =
        match invariant with
        | Some info ->
            // Build name map: IRVar IDs in invariant -> field names
            let nameMap = info.FieldBindings |> List.fold (fun m (fname, fid) -> Map.add fid fname m) Map.empty
            let constraintStr = exprToCpp nameMap info.Expr
            ["    void validate() const {"
             sprintf "        if (!(%s)) {" constraintStr
             sprintf "            std::cerr << \"Constraint violation in %s\" << std::endl;" name
             "            std::abort();"
             "        }"
             "    }"]
        | None -> []
    [sprintf "struct %s {" name]
    @ fieldLines
    @ validateLines
    @ ["};"
       ""]

/// Generate type definitions for a module
let genTypeDefs (modul: IRModule) : string list =
    modul.Types |> List.collect (function
        | IRTDStruct (name, fields, invariant) -> genStructDef name fields invariant
        | IRTDVariant (name, variants) ->
            // Check if any variant has data
            let hasData = variants |> List.exists (fun (_, d) -> d.IsSome)
            if hasData then
                // Tagged union using std::variant
                // Generate wrapper structs for each variant (with _T suffix to avoid name clash)
                let variantStructs = variants |> List.collect (fun (vname, data) ->
                    match data with
                    | Some ty -> 
                        [sprintf "struct %s_T { %s value; };" vname (irTypeToCpp ty)]
                    | None -> 
                        [sprintf "struct %s_T {};" vname]
                )
                // Generate the variant type alias
                let variantTypes = variants |> List.map (fun (v, _) -> v + "_T") |> String.concat ", "
                let variantAlias = sprintf "using %s = std::variant<%s>;" name variantTypes
                // Generate constructor functions for variants with data
                let ctorFuncs = variants |> List.collect (fun (vname, data) ->
                    match data with
                    | Some ty ->
                        [sprintf "inline %s %s(%s v) { return %s_T{v}; }" name vname (irTypeToCpp ty) vname]
                    | None ->
                        [sprintf "const %s %s = %s_T{};" name vname vname]
                )
                variantStructs @ [variantAlias] @ ctorFuncs @ [""]
            else
                // Simple enum - use plain enum for unscoped names
                [sprintf "enum %s { %s };" name 
                    (variants |> List.map fst |> String.concat ", ")
                 ""]
        | IRTDAlias (name, ty) ->
            [sprintf "using %s = %s;" name (irTypeToCpp ty); ""]
        | IRTDIndexType _ -> 
            [] // Index types don't need C++ definitions
    )

/// Generate code to print a scalar value
let genPrintScalar (name: string) : string list =
    [sprintf "    cout << \"%s = \" << %s << endl;" name name]

/// Generate code to print an array value (flattened for easy parsing)
let genPrintArrayFlat (name: string) (rank: int) : string list =
    let firstVar = sprintf "%s__first" name
    if rank = 1 then
        [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    for (size_t i = 0; i < %s_extents[0]; i++) {" name
            sprintf "        if (i > 0) cout << \", \";"
            sprintf "        cout << %s[i];" name
            "    }"
            sprintf "    cout << \"]\" << endl;"
        ]
    elif rank = 2 then
        [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar
            sprintf "    for (size_t i = 0; i < %s_extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s_extents[1]; j++) {" name
            sprintf "            if (!%s) cout << \", \";" firstVar
            sprintf "            %s = false;" firstVar
            sprintf "            cout << %s[i][j];" name
            "        }"
            "    }"
            sprintf "    cout << \"]\" << endl;"
        ]
    elif rank = 3 then
        [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar
            sprintf "    for (size_t i = 0; i < %s_extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s_extents[1]; j++) {" name
            sprintf "            for (size_t k = 0; k < %s_extents[2]; k++) {" name
            sprintf "                if (!%s) cout << \", \";" firstVar
            sprintf "                %s = false;" firstVar
            sprintf "                cout << %s[i][j][k];" name
            "            }"
            "        }"
            "    }"
            sprintf "    cout << \"]\" << endl;"
        ]
    else
        [sprintf "    // Print not implemented for rank %d arrays" rank]

/// Generate print loop for triangular arrays (left-justified)
let genPrintArrayTriangular (name: string) (rank: int) : string list =
    let firstVar = sprintf "%s__first" name
    if rank = 2 then
        [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar
            sprintf "    for (size_t i = 0; i < %s_extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s_extents[1] - i; j++) {" name  // Triangular bound
            sprintf "            if (!%s) cout << \", \";" firstVar
            sprintf "            %s = false;" firstVar
            sprintf "            cout << %s[i][j];" name
            "        }"
            "    }"
            sprintf "    cout << \"]\" << endl;"
        ]
    elif rank = 3 then
        [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar
            sprintf "    for (size_t i = 0; i < %s_extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s_extents[1] - i; j++) {" name  // Triangular bound
            sprintf "            for (size_t k = 0; k < %s_extents[2] - i - j; k++) {" name  // Triangular bound
            sprintf "                if (!%s) cout << \", \";" firstVar
            sprintf "                %s = false;" firstVar
            sprintf "                cout << %s[i][j][k];" name
            "            }"
            "        }"
            "    }"
            sprintf "    cout << \"]\" << endl;"
        ]
    else
        genPrintArrayFlat name rank  // Fall back for other ranks

/// Generate a C++ program (uses external runtime header)
let genSelfContainedProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    
    let includes = genIncludesExternal ()
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    
    // Generate print statements for all bindings
    // Only print bindings that generate actual printable C++ variables
    let printCode = 
        modul.Bindings |> List.collect (fun b ->
            // Determine if this binding generates a printable variable
            let isPrintable = 
                match b.Value with
                // These generate actual arrays via genApplyCombinator
                | IRApplyCombinator _ -> true
                | IRCompute (IRApplyCombinator _) -> true
                // These don't generate runtime variables
                | IRCompute _ | IRMethodFor _ | IRObjectFor _ | IRLambda _ -> false
                // Simple values are printable
                | _ -> true
            
            // Check if result has symmetry (triangular storage)
            let hasSymmetry =
                match b.Value with
                | IRApplyCombinator info -> 
                    info.SymcomStates |> List.exists (fun s -> s <> SCNeither)
                | IRCompute (IRApplyCombinator info) ->
                    info.SymcomStates |> List.exists (fun s -> s <> SCNeither)
                | _ -> false
            
            if isPrintable then
                match IR.stripUnits b.Type with
                | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool) ->
                    genPrintScalar b.Name
                | IRTArray arrType ->
                    let rank = arrayRank arrType
                    if hasSymmetry && (rank = 2 || rank = 3) then
                        genPrintArrayTriangular b.Name rank
                    else
                        genPrintArrayFlat b.Name rank
                | IRTTuple _ ->
                    // For tuples, skip for now
                    []
                | IRTNamed _ ->
                    // For structs, skip for now
                    []
                | IRTUnit ->
                    []
                | _ -> []
            else []
        )
    
    let mainFunc = 
        ["int main() {"
         "    cout << std::setprecision(15);"
         "    auto start = TIME;"
         ""]
        @ bodyIndented
        @ [""
           "    auto end = TIME;"
           "    double elapsed = 1e-9 * TIME_DIFF;"
           sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
           ""
           "    // Print results for verification"]
        @ printCode
        @ [""
           "    return 0;"
           "}"]
    
    (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// Generate a C++ program with external runtime header
/// Returns (mainFileContent, headerFileContent)
let genProgramWithExternalRuntime (modul: IRModule) (testName: string) : string * string =
    let builder = IRBuilder()
    
    let includes = genIncludesExternal ()
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    
    // Generate print statements for all bindings
    let printCode = 
        modul.Bindings |> List.collect (fun b ->
            let isPrintable = 
                match b.Value with
                | IRApplyCombinator _ -> true
                | IRCompute (IRApplyCombinator _) -> true
                | IRCompute _ | IRMethodFor _ | IRObjectFor _ | IRLambda _ -> false
                | _ -> true
            
            // Check if result has symmetry (triangular storage)
            let hasSymmetry =
                match b.Value with
                | IRApplyCombinator info -> 
                    info.SymcomStates |> List.exists (fun s -> s <> SCNeither)
                | IRCompute (IRApplyCombinator info) ->
                    info.SymcomStates |> List.exists (fun s -> s <> SCNeither)
                | _ -> false
            
            if isPrintable then
                match IR.stripUnits b.Type with
                | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool) ->
                    genPrintScalar b.Name
                | IRTArray arrType ->
                    let rank = arrayRank arrType
                    if hasSymmetry && (rank = 2 || rank = 3) then
                        genPrintArrayTriangular b.Name rank
                    else
                        genPrintArrayFlat b.Name rank
                | IRTTuple _ ->
                    // For tuples, skip for now
                    []
                | IRTNamed _ ->
                    // For structs, skip for now
                    []
                | IRTUnit ->
                    []
                | _ -> []
            else []
        )
    
    let mainFunc = 
        ["int main() {"
         "    cout << std::setprecision(15);"
         "    auto start = TIME;"
         ""]
        @ bodyIndented
        @ [""
           "    auto end = TIME;"
           "    double elapsed = 1e-9 * TIME_DIFF;"
           sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
           ""
           "    // Print results for verification"]
        @ printCode
        @ [""
           "    return 0;"
           "}"]
    
    let mainFile = (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"
    let headerFile = genRuntimeHeader ()
    (mainFile, headerFile)

/// Generate a self-contained C++ program from an IR program
let genSelfContainedProgramFromIR (program: IRProgram) (testName: string) : string =
    match program.Modules with
    | [] -> "// Empty program\nint main() { return 0; }\n"
    | [modul] -> genSelfContainedProgram modul testName
    | modules ->
        // Multi-module: merge all modules into one for code generation
        // Functions and bindings from earlier modules come first
        let merged = {
            Name = "merged"
            Types = modules |> List.collect (fun m -> m.Types)
            Functions = modules |> List.collect (fun m -> m.Functions)
            Bindings = modules |> List.collect (fun m -> m.Bindings)
            StaticFunctionUsage = modules |> List.fold (fun acc m -> 
                Map.fold (fun a k v -> Map.add k v a) acc m.StaticFunctionUsage) Map.empty
        }
        genSelfContainedProgram merged testName

// ============================================================================
// Verification Helpers
// ============================================================================

/// Generate C++ code that prints array values for verification
let genPrintArray (ctx: CodeGenContext) (name: string) (arrType: IRArrayType) : string list =
    let ind = indentStr ctx
    let rank = arrayRank arrType
    
    if rank = 1 then
        [
            sprintf "%scout << \"%s = [\";" ind name
            sprintf "%sfor (size_t i = 0; i < %s_extents[0]; i++) {" ind name
            sprintf "%s    if (i > 0) cout << \", \";" ind
            sprintf "%s    cout << %s[i];" ind name
            sprintf "%s}" ind
            sprintf "%scout << \"]\" << endl;" ind
        ]
    elif rank = 2 then
        [
            sprintf "%scout << \"%s = [\" << endl;" ind name
            sprintf "%sfor (size_t i = 0; i < %s_extents[0]; i++) {" ind name
            sprintf "%s    cout << \"  [\";" ind
            sprintf "%s    for (size_t j = 0; j < %s_extents[1]; j++) {" ind name
            sprintf "%s        if (j > 0) cout << \", \";" ind
            sprintf "%s        cout << %s[i][j];" ind name
            sprintf "%s    }" ind
            sprintf "%s    cout << \"]\" << endl;" ind
            sprintf "%s}" ind
            sprintf "%scout << \"]\" << endl;" ind
        ]
    else
        [sprintf "%s// Print not implemented for rank %d arrays" ind rank]

/// Generate a test program that prints results
let genTestProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    let mutable ctx = emptyContext ()
    
    let includes = genIncludes ()
    
    // Collect array bindings to print at the end
    let mutable arrayBindings = []
    let mutable allCode = []
    
    for binding in modul.Bindings do
        let (code, ctx') = genBinding ctx binding builder
        allCode <- allCode @ code @ [""]
        ctx <- ctx'
        
        // Track array bindings for printing
        match binding.Type with
        | IRTArray arrType -> arrayBindings <- arrayBindings @ [(binding.Name, arrType)]
        | _ -> ()
    
    // Generate print code for arrays
    let printCode = 
        arrayBindings |> List.collect (fun (name, arrType) ->
            genPrintArray ctx name arrType @ [""])
    
    let codeIndented = allCode |> List.map (fun s -> "    " + s)
    let printIndented = printCode |> List.map (fun s -> "    " + s)
    
    let mainFunc = 
        ["int main() {"
         "    cout << std::setprecision(15);"
         "    auto start = TIME;"
         ""]
        @ codeIndented
        @ [""
           "    auto end = TIME;"
           "    double elapsed = 1e-9 * TIME_DIFF;"
           sprintf "    cout << \"%s completed in \" << elapsed << \"s\" << endl;" testName
           ""
           "    // Print results"]
        @ printIndented
        @ [""
           "    return 0;"
           "}"]
    
    (includes @ mainFunc) |> String.concat "\n"
