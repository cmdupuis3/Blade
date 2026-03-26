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
    /// Map from C++ variable name to its tuple children names (for extents provenance)
    /// e.g., "_0" → ["_0_0", "_0_1"] means _0 is a pair whose components are _0_0 and _0_1
    TupleChildren: Map<string, string list>
    /// Map from IRId to deferred computation expressions (not yet materialized).
    /// Computations (L <@> f) are only materialized when |> compute forces them.
    /// Combinators like <&!> look through variables to find the original ApplyInfo for fusion.
    DeferredComputations: Map<IRId, IRExpr>
    /// Accumulated code generation warnings (unsupported IR nodes, fallbacks, etc.)
    Warnings: string list ref
}

/// Module-level expression warnings collector.
/// exprToCpp is pure (returns string) so it can't use CodeGenContext directly.
/// This collects warnings from exprToCpp calls and is synced into CodeGenContext.
let exprWarnings : string list ref = ref []

/// Record an expression-level warning and return a C++ expression that causes a compile error.
let exprError (msg: string) : string =
    exprWarnings.Value <- exprWarnings.Value @ [msg]
    sprintf "BLADE_CODEGEN_ERROR_%s" (msg.Replace(" ", "_").Replace("'", "").Replace("(", "").Replace(")", "").Replace(",", "").Replace(":", "").Replace("\"", "").ToUpper())

/// Check if an expression is unit/void-typed (should not generate a value)
let isUnitExpr (expr: IRExpr) : bool =
    match expr with
    | IRLit IRLitUnit -> true
    | IRAssign _ -> true
    | IRForRange _ -> true
    | _ -> false

let emptyContext () = {
    VarNames = Map.empty
    Indent = 0
    StaticDecls = []
    NextTempId = 0
    ConstrainedStructs = Set.empty
    TupleChildren = Map.empty
    DeferredComputations = Map.empty
    Warnings = ref []
}

let indent ctx = { ctx with Indent = ctx.Indent + 1 }
let indentStr ctx = String.replicate ctx.Indent "    "

/// Record a codegen warning and return a C++ #error directive.
/// This ensures the generated C++ will not compile silently.
let codegenError (ctx: CodeGenContext) (ind: string) (msg: string) : string list =
    ctx.Warnings.Value <- ctx.Warnings.Value @ [msg]
    [sprintf "%s#error \"Blade codegen: %s\"" ind (msg.Replace("\"", "'"))]

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
    | IRTupleProj (e, _, _) -> collectVarRefs e
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
        Set.unionMany [collectVarRefs info.Loop; collectVarRefs info.Kernel; Set.unionMany (List.map collectVarRefs info.Arrays)]
    | IRArity _ -> Set.empty
    | IRReynolds (inner, _) -> collectVarRefs inner
    | IRMatch (scrutinee, cases) ->
        let scrutineeRefs = collectVarRefs scrutinee
        let caseRefs = cases |> List.collect (fun c ->
            [collectVarRefs c.Body]
            @ (c.Guard |> Option.map collectVarRefs |> Option.toList)) |> Set.unionMany
        Set.union scrutineeRefs caseRefs
    | IRAssign (target, value) ->
        Set.union (collectVarRefs target) (collectVarRefs value)
    | IRForRange (vid, lo, hi, body) ->
        Set.unionMany [collectVarRefs lo; collectVarRefs hi; Set.remove vid (collectVarRefs body)]
    | IRGuard (cond, body) ->
        Set.union (collectVarRefs cond) (collectVarRefs body)
    | IRMask (arr, pred) ->
        Set.union (collectVarRefs arr) (collectVarRefs pred)
    | IRIntersect (a, b) | IRUnion (a, b) ->
        Set.union (collectVarRefs a) (collectVarRefs b)
    | IRSequence exprs ->
        Set.unionMany (List.map collectVarRefs exprs)
    | _ -> Set.empty

/// Infer type from an IR expression (simplified version for codegen)
let rec inferExprType (expr: IRExpr) : IRType =
    match expr with
    | IRArrayLit (_, arrTy) -> IRTArray arrTy
    | IRLit (IRLitInt _) -> IRTScalar ETInt64
    | IRLit (IRLitFloat _) -> IRTScalar ETFloat64
    | IRLit (IRLitBool _) -> IRTScalar ETBool
    | IRLit IRLitUnit -> IRTUnit
    | IRBinOp (_, op, left, right) ->
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> IRTScalar ETBool
        | _ ->
            match inferExprType left, inferExprType right with
            | IRTScalar e1, IRTScalar e2 ->
                IRTScalar (IR.promoteElemType e1 e2 |> Option.defaultValue e1)
            | lt, _ -> lt
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
    | IRTupleProj (e, i, isFlat) ->
        let parentTy = inferExprType e
        if isFlat then
            let leaves = IR.flattenTupleLeaves parentTy
            if i < leaves.Length then leaves.[i] else IRTUnit
        else
            match parentTy with
            | IRTTuple ts when i < ts.Length -> ts.[i]
            | _ -> IRTUnit
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
        | _ ->
            // Sequence produces array with Idx<N> over element type
            let elemType = inferExprType (List.head exprs)
            match elemType with
            | IRTArray arr ->
                // Array elements: prepend sequence dimension
                let seqIdx = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt (int64 exprs.Length)); Symmetry = SymNone; Tag = Some "__seq"; Kind = SDimension; Dependencies = [] }
                IRTArray { arr with IndexTypes = seqIdx :: arr.IndexTypes }
            | IRTScalar et ->
                // Scalar elements: simple array
                let seqIdx = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt (int64 exprs.Length)); Symmetry = SymNone; Tag = Some "__seq"; Kind = SDimension; Dependencies = [] }
                IRTArray { ElemType = et; IndexTypes = [seqIdx]; IsVirtual = false; Identity = None }
            | _ -> elemType
    | IRAssign _ -> IRTUnit
    | IRForRange _ -> IRTUnit
    | IRArity _ -> IRTScalar ETInt64
    | IRNth -> IRTScalar ETInt64
    | IRRank _ -> IRTScalar ETInt64
    | IRExtent _ -> IRTScalar ETInt64
    | IRRange _ -> IRTScalar ETInt64
    | IRFieldAccess (obj, field) ->
        // Would need struct type info to resolve; return unit as placeholder
        IRTUnit
    | IRFunctorMap (f, c) ->
        // f <$> c: return type is f's return type
        match inferExprType f with
        | IRTFunc (_, retTy) -> retTy
        | _ -> inferExprType c  // fallback: preserve computation type
    | IRBind (_, cont) ->
        // >>= : result type is continuation's return type
        match inferExprType cont with
        | IRTFunc (_, retTy) -> retTy
        | t -> t
    | IRComposeMeth (_, right) ->
        // @>> : result type is right's type
        inferExprType right
    | IRPure e -> inferExprType e
    | IRParallel (l, r, _) -> IRTTuple [inferExprType l; inferExprType r]
    | IRFusion (l, r) -> IRTTuple [inferExprType l; inferExprType r]
    | IRChoice (l, _) -> inferExprType l
    | IRGuard (_, body) -> inferExprType body
    | IRMask (arr, _) -> inferExprType arr  // Same elem type, different extent
    | IRIntersect (a, _) | IRUnion (a, _) -> inferExprType a  // Same elem type, different extent
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
    | ETIndexRef _ -> "int64_t"

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
        | LKMethod -> "BLADE_ERROR_METHOD_LOOP_TYPE"
        | LKObject -> "BLADE_ERROR_OBJECT_LOOP_TYPE"
    | IRTComputation t -> irTypeToCpp t  // Computation<T> erases to T at runtime
    | IRTPoly (base', arityVar) -> 
        // After monomorphization, IRTPoly should not reach codegen.
        // If it does, fall back to the base type.
        irTypeToCpp base'
    | IRTNat _ -> "size_t"
    | IRTNamed "String" -> "std::string"  // Blade String → C++ std::string
    | IRTNamed name -> name  // Named types (structs, etc.) use their name directly
    | IRTInfer n ->
        exprWarnings.Value <- exprWarnings.Value @ [sprintf "unresolved type variable _%d reached codegen" n]
        sprintf "BLADE_UNRESOLVED_TYPE_%d" n
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
    | IRLit IRLitUnit -> "((void)0)"
    | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
    | IRParam (name, _, _) -> name
    | IRBinOp (_, op, l, r) ->
        let lStr = exprToCppSimple names l
        let rStr = exprToCppSimple names r
        if op = IRCaret then sprintf "pow(%s, %s)" lStr rStr
        else sprintf "(%s %s %s)" lStr (binOpToCpp op) rStr
    | IRUnaryOp (op, e) -> sprintf "%s(%s)" (unaryOpToCpp op) (exprToCppSimple names e)
    | IRGuard (cond, body) ->
        sprintf "(%s ? %s : 0.0)" (exprToCppSimple names cond) (exprToCppSimple names body)
    | other -> sprintf "BLADE_UNSUPPORTED_EXPR_%s" (other.GetType().Name.ToUpper())

/// Convert IRLit to C++ literal string
let litToCpp (lit: IRLit) : string =
    match lit with
    | IRLitInt n -> sprintf "%dL" n
    | IRLitFloat f -> sprintf "%g" f
    | IRLitBool b -> if b then "true" else "false"
    | IRLitUnit -> "((void)0)"  // Valid C++ no-op; should be elided by callers

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
    | IRTupleProj (e, i, isFlat) ->
        if not isFlat then
            sprintf "std::get<%d>(%s)" i (exprToCpp names e)
        else
            // Flat projection into potentially nested tuple — compute navigation path
            let parentTy = inferExprType e
            let rec findPath (ty: IRType) (targetFlat: int) : int list =
                match ty with
                | IRTTuple ts ->
                    let mutable offset = 0
                    let mutable found = None
                    for idx in 0 .. ts.Length - 1 do
                        if found.IsNone then
                            let count = IR.flattenTupleLeaves ts.[idx] |> List.length
                            if targetFlat < offset + count then
                                match ts.[idx] with
                                | IRTTuple _ -> found <- Some (idx :: findPath ts.[idx] (targetFlat - offset))
                                | _ -> found <- Some [idx]
                            offset <- offset + count
                    found |> Option.defaultValue [i]
                | _ -> [i]
            let path = findPath parentTy i
            path |> List.fold (fun acc idx -> sprintf "std::get<%d>(%s)" idx acc) (exprToCpp names e)
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
        let names' = Map.add id (sprintf "__v%d" id) names
        if isUnitExpr value then
            // Unit-valued binding: skip the auto declaration
            if isUnitExpr body then
                "((void)0)"
            else
                exprToCpp names' body
        else
            let valStr = exprToCpp names value
            match body with
            | IRLit IRLitUnit ->
                sprintf "([&]() { auto __v%d = %s; }())" id valStr
            | _ ->
                let bodyStr = exprToCpp names' body
                sprintf "([&]() { auto __v%d = %s; return %s; }())" id valStr bodyStr
    | IRLambda info ->
        // Generate C++ lambda
        let paramList = info.Params |> List.map (fun p -> sprintf "%s %s" (irTypeToCpp p.Type) p.Name) |> String.concat ", "
        let names' = info.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
        if isUnitExpr info.Body then
            sprintf "[&](%s) { }" paramList
        else
            let bodyStr = exprToCpp names' info.Body
            sprintf "[&](%s) { return %s; }" paramList bodyStr
    | IRMethodFor _ -> exprError "loop object used as value"
    | IRObjectFor _ -> exprError "loop object used as value"
    | IRApplyCombinator _ -> 
        exprError "unevaluated computation used as value - use |> compute"
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
        exprError "reynolds wrapper in expression position"
    | IRZip arrs ->
        // In expression context (e.g. inside a kernel body), zip produces a tuple
        sprintf "std::make_tuple(%s)" (arrs |> List.map (exprToCpp names) |> String.concat ", ")
    | IRStack arrs ->
        exprError "stack not yet implemented in codegen"
    | IRSlice (arr, dim, start, stop) ->
        exprError "slice not yet implemented in codegen"
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
            | [] -> "([&]() -> double { std::cerr << \"Blade: non-exhaustive match\" << std::endl; std::abort(); return 0; }())"
            | [case] ->
                // Last case - assume it matches (wildcard or variable)
                // But if there's a guard, we must still check it.
                let abortExpr = "([&]() -> double { std::cerr << \"Blade: non-exhaustive match\" << std::endl; std::abort(); return 0; }())"
                let wrapGuard (bodyStr: string) (names': Map<IRId, string>) : string =
                    match case.Guard with
                    | Some guard ->
                        let guardStr = exprToCpp names' guard
                        sprintf "(%s ? %s : %s)" guardStr bodyStr abortExpr
                    | None -> bodyStr
                match case.Pattern with
                | IRPatVar varId ->
                    // Bind variable and evaluate body (only if variable is used)
                    let varUsed =
                        (collectVarRefs case.Body).Contains varId ||
                        (case.Guard |> Option.map (fun g -> (collectVarRefs g).Contains varId) |> Option.defaultValue false)
                    if varUsed then
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let bodyStr = exprToCpp names' case.Body
                        let guardedBody = wrapGuard bodyStr names'
                        sprintf "[&]() { auto %s = %s; return %s; }()" varName scrut guardedBody
                    else
                        wrapGuard (exprToCpp names case.Body) names
                | IRPatWild ->
                    wrapGuard (exprToCpp names case.Body) names
                | IRPatLit lit ->
                    let litStr = litToCpp lit
                    let bodyStr = wrapGuard (exprToCpp names case.Body) names
                    sprintf "(%s == %s ? %s : %s)" scrut litStr bodyStr abortExpr
                | IRPatVariant (ctorName, tag, innerOpt, isEnum) ->
                    // Last variant case — extract payload and evaluate body
                    match innerOpt with
                    | Some (IRPatVar varId) ->
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let extractExpr = sprintf "std::get<%s_T>(%s).value" ctorName scrut
                        let bodyStr = exprToCpp names' case.Body
                        let guardedBody = wrapGuard bodyStr names'
                        sprintf "[&]() { auto %s = %s; return %s; }()" varName extractExpr guardedBody
                    | _ ->
                        wrapGuard (exprToCpp names case.Body) names
                | IRPatTuple innerPats ->
                    // Last tuple case — bind each element
                    let bindings =
                        innerPats |> List.mapi (fun idx pat ->
                            match pat with
                            | IRPatVar varId -> Some (varId, sprintf "__match_%d" varId, idx)
                            | _ -> None)
                        |> List.choose id
                    let bindingDecls = bindings |> List.map (fun (_, name, idx) ->
                        sprintf "auto %s = std::get<%d>(%s)" name idx scrut) |> String.concat "; "
                    let names' = bindings |> List.fold (fun acc (id, name, _) -> Map.add id name acc) names
                    let bodyStr = exprToCpp names' case.Body
                    let guardedBody = wrapGuard bodyStr names'
                    sprintf "[&]() { %s; return %s; }()" bindingDecls guardedBody
                | _ ->
                    wrapGuard (exprToCpp names case.Body) names
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
                | IRPatVariant (ctorName, tag, innerOpt, isEnum) ->
                    // Variant pattern - check variant type and optionally bind inner value
                    let checkExpr =
                        if isEnum then sprintf "%s == %s" scrut ctorName
                        else sprintf "std::holds_alternative<%s_T>(%s)" ctorName scrut
                    
                    match innerOpt with
                    | Some (IRPatVar varId) ->
                        // Variant with inner value binding
                        let varName = sprintf "__match_%d" varId
                        let names' = Map.add varId varName names
                        let extractExpr = sprintf "std::get<%s_T>(%s).value" ctorName scrut
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
    | IRNth -> exprError "nth keyword not supported in expression position"
    | IRZero -> "0"
    | IRPolyIndex (pack, idx) ->
        // For static index, use std::get; otherwise runtime indexing
        match idx with
        | IRLit (IRLitInt n) -> sprintf "std::get<%d>(%s)" n (exprToCpp names pack)
        | _ -> sprintf "%s[%s]" (exprToCpp names pack) (exprToCpp names idx)
    | IRParallel (a, b, _) ->
        exprError "parallel combinator in expression position"
    | IRFusion (a, b) ->
        exprError "fusion combinator in expression position"
    | IRChoice (a, b) ->
        let aStr = exprToCpp names a
        let bStr = exprToCpp names b
        sprintf "(%s != 0 ? %s : %s)" aStr aStr bStr
    | IRGuard (cond, body) ->
        // guard(p, c) → p ? c : 0 (type-appropriate zero)
        let condStr = exprToCpp names cond
        let bodyStr = exprToCpp names body
        let zeroStr =
            match inferExprType body with
            | IRTScalar ETBool -> "false"
            | IRTScalar ETInt64 | IRTScalar ETInt32 -> "0L"
            | _ -> "0.0"
        sprintf "(%s ? %s : %s)" condStr bodyStr zeroStr
    | IRCompose (f, g) ->
        // f >> g = [&](auto... args) { return g(f(args...)); }
        let fStr = exprToCpp names f
        let gStr = exprToCpp names g
        sprintf "[&](auto... __args) { return %s(%s(__args...)); }" gStr fStr
    | IRComposeObj (f, g) ->
        exprError "compose_obj in expression position"
    | IRComposeMeth (f, g) ->
        exprError "compose_meth in expression position"
    | IRArrayProduct (a, b) ->
        exprError "array_product in expression position"
    | IRFunctorMap (f, c) ->
        exprError "functor_map in expression position"
    | IRAssign (target, value) ->
        let targetStr =
            match target with
            | LVVar id -> Map.tryFind id names |> Option.defaultValue (sprintf "__v%d" id)
            | LVIndex (arr, idxs) ->
                let arrStr = exprToCpp names arr
                let idxStr = idxs |> List.map (fun i -> sprintf "[%s]" (exprToCpp names i)) |> String.concat ""
                sprintf "%s%s" arrStr idxStr
            | LVField (obj, f) -> sprintf "%s.%s" (exprToCpp names obj) f
            | LVOther e -> exprError "invalid assignment target"
        sprintf "%s = %s" targetStr (exprToCpp names value)
    | IRForRange (vid, lo, hi, body) ->
        exprError "for-range loop in expression position"
    | other -> exprError (sprintf "unsupported IR node: %s" (other.GetType().Name))

/// Generate inline combinator application as an IIFE expression
/// This is used when L <@> f appears in expression context (not as a top-level binding)
and genApplyCombinatorExpr (names: Map<IRId, string>) (info: ApplyInfo) : string =
    // Extract array info
    let arrayNames = 
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> Map.tryFind id names |> Option.defaultValue (sprintf "arr%d" i)
            | IRParam (name, _, _) -> name
            | _ -> sprintf "arr%d" i)
    
    // Extract kernel
    let kernelExpr = 
        match info.Kernel with
        | IRLambda lInfo -> 
            let paramNames = lInfo.Params |> List.map (fun p -> p.Name)
            let bodyStr = 
                let names' = lInfo.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
                exprToCpp names' lInfo.Body
            (paramNames, bodyStr)
        | IRReynolds (IRLambda lInfo, _) ->
            let paramNames = lInfo.Params |> List.map (fun p -> p.Name)
            let bodyStr = 
                let names' = lInfo.Params |> List.fold (fun m p -> Map.add p.VarId p.Name m) names
                exprToCpp names' lInfo.Body
            (paramNames, bodyStr)
        | _ -> ([], exprError "kernel is not a lambda in inline combinator expression")
    
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
        exprError (sprintf "inline combinator not supported for %d arrays" arrayNames.Length)

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
    | VirtualRange offset ->
        // range<I>: kernel param gets the loop index, plus offset if present
        let valueExpr =
            match offset with
            | None -> level.IndexName
            | Some (IRLit (IRLitInt n)) -> sprintf "(%s + %dL)" level.IndexName n
            | Some off -> sprintf "(%s + %s)" level.IndexName (exprToCpp Map.empty off)
        let code = sprintf "size_t %s = %s;" elem.ParamName valueExpr
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
/// Generate all permutations of a list of integers
let rec permutations (items: int list) : int list list =
    match items with
    | [] -> [[]]
    | _ ->
        items |> List.collect (fun x ->
            let rest = items |> List.filter (fun i -> i <> x)
            permutations rest |> List.map (fun p -> x :: p))

/// Count inversions to get permutation sign (+1 for even, -1 for odd)

/// Is this binary operation commutative? (a op b) = (b op a)
let isCommutativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IREq | IRNeq | IRAnd | IROr -> true
    | _ -> false

/// Is this binary operation associative? (a op b) op c = a op (b op c)
/// Only ops that are BOTH commutative and associative get flattened.
let isAssociativeOp (op: IRBinOp) : bool =
    match op with
    | IRAdd | IRMul | IRAnd | IROr -> true
    | _ -> false

/// Flatten nested applications of the same commutative+associative op into a list of operands.
/// E.g. (a * b) * c → [a; b; c]
let rec flattenAssocOp (mode: IRBinOpMode) (op: IRBinOp) (expr: IRExpr) : IRExpr list =
    match expr with
    | IRBinOp (m, o, l, r) when o = op && m = mode ->
        flattenAssocOp mode op l @ flattenAssocOp mode op r
    | _ -> [expr]

/// Generate a canonical string key for an IR expression under a given name mapping.
/// Commutative binary operations have their children sorted by canonical key,
/// and associative+commutative chains are flattened and sorted, so that e.g.
/// (a * b) * c and c * (b * a) produce the same key.
/// Used for Reynolds permutation deduplication.
let rec canonicalKey (nameMap: Map<int, string>) (expr: IRExpr) : string =
    match expr with
    | IRVar (id, _) ->
        Map.tryFind id nameMap |> Option.defaultValue (sprintf "v%d" id)
    | IRParam (name, _, _) ->
        sprintf "p:%s" name
    | IRLit lit ->
        match lit with
        | IRLitInt n -> string n
        | IRLitFloat f -> sprintf "%g" f
        | IRLitBool b -> if b then "true" else "false"
        | IRLitUnit -> "()"
    | IRBinOp (mode, op, l, r) when isCommutativeOp op && isAssociativeOp op ->
        let operands = flattenAssocOp mode op expr
        let keys = operands |> List.map (canonicalKey nameMap) |> List.sort
        sprintf "(%A/%A %s)" mode op (keys |> String.concat " ")
    | IRBinOp (mode, op, l, r) when isCommutativeOp op ->
        let lk = canonicalKey nameMap l
        let rk = canonicalKey nameMap r
        let children = [lk; rk] |> List.sort
        sprintf "(%A/%A %s %s)" mode op children.[0] children.[1]
    | IRBinOp (mode, op, l, r) ->
        sprintf "(%A/%A %s %s)" mode op (canonicalKey nameMap l) (canonicalKey nameMap r)
    | IRUnaryOp (op, inner) ->
        sprintf "(u%A %s)" op (canonicalKey nameMap inner)
    | IRApp (func, args, _) ->
        let fk = canonicalKey nameMap func
        let ak = args |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(call %s [%s])" fk ak
    | IRIf (cond, thn, els) ->
        sprintf "(if %s %s %s)" (canonicalKey nameMap cond) (canonicalKey nameMap thn) (canonicalKey nameMap els)
    | IRLet (id, value, body) ->
        sprintf "(let v%d=%s in %s)" id (canonicalKey nameMap value) (canonicalKey nameMap body)
    | IRTupleProj (tup, idx, _) ->
        sprintf "(proj %d %s)" idx (canonicalKey nameMap tup)
    | IRTuple elems ->
        let ek = elems |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(tuple %s)" ek
    | IRFieldAccess (obj, field) ->
        sprintf "(field %s %s)" (canonicalKey nameMap obj) field
    | IRStructLit (name, fields) ->
        let fk = fields |> List.map (fun (f, e) -> sprintf "%s=%s" f (canonicalKey nameMap e)) |> String.concat ","
        sprintf "(struct %s {%s})" name fk
    | IRMatch (scrutinee, cases) ->
        let sk = canonicalKey nameMap scrutinee
        let ck = cases |> List.map (fun c -> sprintf "%A->%s" c.Pattern (canonicalKey nameMap c.Body)) |> String.concat "|"
        sprintf "(match %s [%s])" sk ck
    | IRLambda info ->
        let pk = info.Params |> List.map (fun p -> sprintf "%s:%d" p.Name p.VarId) |> String.concat ","
        sprintf "(fn [%s] %s)" pk (canonicalKey nameMap info.Body)
    | IRIndex (arr, indices, _) ->
        let ak = canonicalKey nameMap arr
        let ik = indices |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(idx %s [%s])" ak ik
    | IRArrayLit (elems, _) ->
        let ek = elems |> List.map (canonicalKey nameMap) |> String.concat ","
        sprintf "(arrlit [%s])" ek
    | IRExtent (arr, dim) ->
        sprintf "(extent %s %d)" (canonicalKey nameMap arr) dim
    | IRRank arr ->
        sprintf "(rank %s)" (canonicalKey nameMap arr)
    | IRPolyIndex (pack, idx) ->
        sprintf "(polyidx %s %s)" (canonicalKey nameMap pack) (canonicalKey nameMap idx)
    | IRNth -> "nth"
    | IRZero -> "zero"
    | IRSlice (arr, dim, start, stop) ->
        sprintf "(slice %s %d %s %s)" (canonicalKey nameMap arr) dim (canonicalKey nameMap start) (canonicalKey nameMap stop)
    | IRCurry (arr, idx, rank) ->
        sprintf "(curry %s %s %d)" (canonicalKey nameMap arr) (canonicalKey nameMap idx) rank
    | IRTranspose (arr, perm) ->
        sprintf "(transpose %s %A)" (canonicalKey nameMap arr) perm
    | IRAssign (lhs, rhs) ->
        sprintf "(assign %s %s)" (canonicalKey nameMap lhs) (canonicalKey nameMap rhs)
    | IRForRange (vid, lo, hi, body) ->
        sprintf "(for v%d %s %s %s)" vid (canonicalKey nameMap lo) (canonicalKey nameMap hi) (canonicalKey nameMap body)
    | _ ->
        // Combinators, compute, reynolds, etc. — won't appear in kernel bodies.
        // Use unique repr to prevent false dedup.
        sprintf "(opaque %d %A)" (expr.GetHashCode()) (expr.GetType().Name)
let permSign (perm: int list) : int =
    let mutable inv = 0
    for i in 0 .. perm.Length - 2 do
        for j in i + 1 .. perm.Length - 1 do
            if perm.[i] > perm.[j] then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

/// Reynolds kernel codegen result: C++ expression + dedup statistics.
type ReynoldsResult = {
    CppExpr: string
    TotalPerms: int
    UniqueTerms: int
}

/// Generate the kernel expression string, applying Reynolds permutation sum if needed.
/// For non-Reynolds kernels, just returns `exprToCpp nameMap kernelExpr`.
/// For Reynolds kernels, generates the sum over all permutations of the kernel parameters,
/// deduplicating structurally equivalent permutations via canonical keys.

let genKernelExprWithReynolds
    (kernelExpr: IRExpr)
    (kernelParams: IRParam list)
    (hasReynolds: bool)
    (isAntisymmetric: bool)
    (nameMap: Map<int, string>)
    (paramFinalNames: Map<int, string>) : ReynoldsResult =
    if hasReynolds && kernelParams.Length >= 2 then
        let n = kernelParams.Length
        let paramCppNames =
            kernelParams |> List.map (fun p ->
                Map.tryFind p.VarId paramFinalNames
                |> Option.defaultValue (sprintf "__p%d" p.VarId))
        let allPerms = permutations [0 .. n - 1]
        let totalPerms = allPerms.Length
        // For each permutation, generate:
        //   - canonical key (for grouping — commutative ops normalized)
        //   - C++ expression (for actual emission)
        //   - sign
        let permData =
            allPerms |> List.map (fun perm ->
                let permNameMap =
                    kernelParams |> List.mapi (fun i p ->
                        (p.VarId, paramCppNames.[perm.[i]]))
                    |> List.fold (fun acc (vid, name) -> Map.add vid name acc) nameMap
                let sign = permSign perm
                let key = canonicalKey permNameMap kernelExpr
                let cppExpr = exprToCpp permNameMap kernelExpr
                (key, sign, cppExpr))
        // Group by canonical key to deduplicate equivalent permutations.
        // For symmetric Reynolds: identical keys accumulate multiplicity.
        // For antisymmetric Reynolds: identical keys accumulate net sign (may cancel to 0).
        let grouped =
            permData
            |> List.groupBy (fun (key, _, _) -> key)
            |> List.choose (fun (_key, group) ->
                let representativeCpp = let (_, _, cpp) = group.Head in cpp
                if isAntisymmetric then
                    let netSign = group |> List.sumBy (fun (_, s, _) -> s)
                    if netSign = 0 then None
                    else Some (netSign, representativeCpp)
                else
                    Some (group.Length, representativeCpp))
        let uniqueTerms = grouped.Length
        // Build the sum expression with multiplicity coefficients
        let formatTerm coeff expr =
            match isAntisymmetric with
            | true ->
                if abs coeff = 1 then
                    if coeff > 0 then expr else sprintf "(-%s)" expr
                else sprintf "(%d * %s)" coeff expr
            | false ->
                if coeff = 1 then expr else sprintf "(%d * %s)" coeff expr
        let sumExpr =
            grouped |> List.mapi (fun i (coeff, expr) ->
                let term = formatTerm coeff expr
                if i = 0 then term
                elif isAntisymmetric && coeff < 0 then
                    sprintf " - %s" (formatTerm (abs coeff) expr)
                else sprintf " + %s" term)
            |> String.concat ""
        let cppExpr =
            if grouped.IsEmpty then
                "0.0"  // Complete cancellation (e.g. antisymmetrization of symmetric kernel)
            else
                sprintf "(%s)" sumExpr
        { CppExpr = cppExpr; TotalPerms = totalPerms; UniqueTerms = uniqueTerms }
    else
        { CppExpr = exprToCpp nameMap kernelExpr; TotalPerms = 1; UniqueTerms = 1 }

let genLoopNest (codeGen: LoopNestCodeGen) (outerNames: Map<int, string>) (indent: int) : string list =
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
            | VirtualRange _ | VirtualReverse ->
                paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
            | RealArray ->
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Build name map for kernel body from final peeled names
    // Start from outer scope, then overlay kernel params (kernel params take priority)
    let nameMap = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    let nameMap =
        codeGen.Captures
        |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
    
    // Generate kernel assignment (with Reynolds permutation sum if applicable)
    let outputIdx = 
        codeGen.Bindings 
        |> List.map (fun b -> sprintf "[%s]" b.IndexName)
        |> String.concat ""
    
    let reynoldsResult = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap paramFinalNames
    if codeGen.HasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
    lines <- lines @ [ind depth + sprintf "%s%s = %s;" codeGen.OutputName outputIdx reynoldsResult.CppExpr]
    
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


// ============================================================================
// Array Allocation Generation
// ============================================================================


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
    let body = genLoopNest codeGen Map.empty 1
    
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
    | IRLit (IRLitBool b) -> [if b then 1.0 else 0.0]
    | IRUnaryOp (IRNeg, IRLit (IRLitFloat f)) -> [-f]
    | IRUnaryOp (IRNeg, IRLit (IRLitInt n)) -> [float -n]
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
        // Unit-typed binding: only emit if value has side effects
        if isUnitExpr value then []
        else
            let valueStr = exprToCppCtx ctx value
            [sprintf "%s%s;" ind valueStr]
    | _ ->
        let cppType = irTypeToCpp resolvedTy
        let valueStr = exprToCppCtx ctx value
        [sprintf "%s%s %s = %s;" ind cppType name valueStr]

// ============================================================================
// Loop Application Code Generation
// ============================================================================

/// Build a simple (no symmetry) ApplyInfo for applying a unary kernel to arrays.
/// Used by >>@ and @>> to construct stage-2 pipeline applications.
let defaultIndexType () = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt 0); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
let defaultArrayType et = { ElemType = et; IndexTypes = [defaultIndexType ()]; IsVirtual = false; Identity = None }

let buildSimpleApplyInfo (arrays: IRExpr list) (kernel: IRExpr) (outputType: IRType) : ApplyInfo =
    let arrayTypes = arrays |> List.map (fun a -> 
        match inferExprType a with 
        | IRTArray arr -> arr 
        | _ -> defaultArrayType ETFloat64)
    let identities = arrays |> List.mapi (fun i _ -> AIDLiteral i)
    let sDims = arrayTypes |> List.map arrayRank
    let totalSDims = List.sum sDims
    {
        Loop = IRMethodFor {
            Arrays = arrays
            Identities = identities
            ArrayTypes = arrayTypes
            SDimsPerArray = sDims
            TotalSDims = totalSDims
            SharedIndexType = None
        }
        Kernel = kernel
        Arrays = arrays
        Identities = identities
        ArrayTypes = arrayTypes
        SharedIndexType = None
        SymcomStates = List.replicate totalSDims SCNeither
        TriangularLevels = List.replicate totalSDims false
        SDimsPerArray = sDims
        KernelInputRanks = List.replicate (List.length arrays) 0
        KernelOutputRank = 0
        KernelTDims = []
        SpeedupFactor = 1L
        ReynoldsSpeedup = 1L
        HasReynolds = false
        OutputType = outputType
        IsCoIteration = false
    }

/// Generate the complete code for a combinator application (L <@> f)
let genApplyCombinator (ctx: CodeGenContext) (name: string) (info: ApplyInfo) (builder: IRBuilder) : string list =
    let ind = indentStr ctx
    
    // Pre-materialize any inline array expressions (mask, intersect, union, etc.)
    // These need to be bound to temporary variables before the loop nest can reference them.
    let mutable preCode = []
    let mutable tempCtx = ctx
    let materializedArrays =
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) -> 
                let name = Map.tryFind id tempCtx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                (name, arr)
            | IRRange _ -> (sprintf "__range%d" i, arr)
            | IRVirtualReverse _ -> (sprintf "__rev%d" i, arr)
            | IRBlocked _ -> (sprintf "__blk%d" i, arr)
            | IRMask _ | IRIntersect _ | IRUnion _ ->
                // Generate a temporary binding for inline materialized expressions
                let tmpName = sprintf "%s__tmp%d" name i
                let tmpId = builder.FreshId()
                let tmpType = inferExprType arr
                // Use exprToCppCtx for the source array reference inside the expression
                let arrName =
                    match arr with
                    | IRMask (srcArr, _) | IRIntersect (srcArr, _) | IRUnion (srcArr, _) ->
                        exprToCppCtx tempCtx srcArr
                    | _ -> sprintf "arr%d" i
                let elemType =
                    match tmpType with
                    | IRTArray a -> elemTypeToCpp a.ElemType
                    | IRTScalar et -> elemTypeToCpp et
                    | _ -> "double"
                let code =
                    match arr with
                    | IRMask (srcArr, predExpr) ->
                        let (predParamId, predBody) =
                            match predExpr with
                            | IRLambda lInfo when lInfo.Params.Length = 1 ->
                                let pName = sprintf "__%s_x" tmpName
                                let predNames = Map.add lInfo.Params.[0].VarId pName tempCtx.VarNames
                                let predNames = lInfo.Captures |> List.fold (fun m c -> Map.add c.Id c.Name m) predNames
                                (pName, exprToCpp predNames lInfo.Body)
                            | _ -> (sprintf "__%s_x" tmpName, "true")
                        [
                            sprintf "%ssize_t %s__count = 0;" ind tmpName
                            sprintf "%sfor (size_t __mi = 0; __mi < %s_extents[0]; __mi++) {" ind arrName
                            sprintf "%s    %s %s = %s[__mi];" ind elemType (fst (predParamId, predBody)) arrName
                            sprintf "%s    if (%s) %s__count++;" ind (snd (predParamId, predBody)) tmpName
                            sprintf "%s}" ind
                            sprintf "%ssize_t %s_extents[1] = {%s__count};" ind tmpName tmpName
                            sprintf "%s%s* %s = new %s[%s__count];" ind elemType tmpName elemType tmpName
                            sprintf "%ssize_t %s__fill = 0;" ind tmpName
                            sprintf "%sfor (size_t __mi = 0; __mi < %s_extents[0]; __mi++) {" ind arrName
                            sprintf "%s    %s %s = %s[__mi];" ind elemType (fst (predParamId, predBody)) arrName
                            sprintf "%s    if (%s) { %s[%s__fill++] = %s; }" ind (snd (predParamId, predBody)) tmpName tmpName (fst (predParamId, predBody))
                            sprintf "%s}" ind
                        ]
                    | IRIntersect (aExpr, bExpr) ->
                        let aName = exprToCppCtx tempCtx aExpr
                        let bName = exprToCppCtx tempCtx bExpr
                        [
                            sprintf "%sstd::set<%s> %s__set;" ind elemType tmpName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) %s__set.insert(%s[__si]);" ind bName tmpName bName
                            sprintf "%ssize_t %s__count = 0;" ind tmpName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind aName
                            sprintf "%s    if (%s__set.count(%s[__si])) %s__count++;" ind tmpName aName tmpName
                            sprintf "%s}" ind
                            sprintf "%ssize_t %s_extents[1] = {%s__count};" ind tmpName tmpName
                            sprintf "%s%s* %s = new %s[%s__count];" ind elemType tmpName elemType tmpName
                            sprintf "%ssize_t %s__fill = 0;" ind tmpName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind aName
                            sprintf "%s    if (%s__set.count(%s[__si])) %s[%s__fill++] = %s[__si];" ind tmpName aName tmpName tmpName aName
                            sprintf "%s}" ind
                        ]
                    | IRUnion (aExpr, bExpr) ->
                        let aName = exprToCppCtx tempCtx aExpr
                        let bName = exprToCppCtx tempCtx bExpr
                        [
                            sprintf "%sstd::set<%s> %s__set;" ind elemType tmpName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) %s__set.insert(%s[__si]);" ind aName tmpName aName
                            sprintf "%ssize_t %s__extra = 0;" ind tmpName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind bName
                            sprintf "%s    if (!%s__set.count(%s[__si])) %s__extra++;" ind tmpName bName tmpName
                            sprintf "%s}" ind
                            sprintf "%ssize_t %s__total = %s_extents[0] + %s__extra;" ind tmpName aName tmpName
                            sprintf "%ssize_t %s_extents[1] = {%s__total};" ind tmpName tmpName
                            sprintf "%s%s* %s = new %s[%s__total];" ind elemType tmpName elemType tmpName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) %s[__si] = %s[__si];" ind aName tmpName aName
                            sprintf "%ssize_t %s__fill = %s_extents[0];" ind tmpName aName
                            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind bName
                            sprintf "%s    if (!%s__set.count(%s[__si])) %s[%s__fill++] = %s[__si];" ind tmpName bName tmpName tmpName bName
                            sprintf "%s}" ind
                        ]
                    | _ -> []
                preCode <- preCode @ code
                tempCtx <- addVarName tmpId tmpName tempCtx
                (tmpName, IRVar (tmpId, tmpType))
            | _ -> (sprintf "arr%d" i, arr))
    
    let arrayNames = materializedArrays |> List.map fst
    let updatedArrays = materializedArrays |> List.map snd
    let info = { info with Arrays = updatedArrays }
    
    if arrayNames.IsEmpty then
        codegenError ctx ind (sprintf "no arrays in method_for for '%s' — kernel cannot be applied" name)
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
        let loopCode = genLoopNest codeGen tempCtx.VarNames tempCtx.Indent
        
        // Combine all (prepend any pre-materialized temporaries)
        preCode @ [symmVecDecl; ""; extentsDecl] @ extentsFill @ [""; allocDecl; allocInit; ""] @ loopCode

/// Generate a fused loop nest with multiple kernel assignments.
/// All kernels share the same loop structure (bindings), only differ in kernel expr and output.
/// Each extra kernel tuple: (outName, kernelExpr, params, captures, hasReynolds, isAntisymmetric)
let genFusedLoopNest (codeGen: LoopNestCodeGen) (extraKernels: (string * IRExpr * IRParam list * CaptureInfo list * bool * bool) list) (outerNames: Map<int, string>) (indent: int) : string list =
    let ind n = String.replicate n "    "
    let mutable lines = []
    let mutable depth = indent
    
    // Track current peeled name for each array position
    let mutable currentNames : Map<int, string> = 
        codeGen.InputArrayNames |> List.mapi (fun i n -> (i, n)) |> Map.ofList
    let mutable paramFinalNames : Map<int, string> = Map.empty
    
    // Generate nested loops with element bindings (same as genLoopNest)
    for binding in codeGen.Bindings do
        lines <- lines @ [ind depth + genForLoopHeader binding]
        depth <- depth + 1
        for elem in binding.Elements do
            let currentName = 
                Map.tryFind elem.ArrayPosition currentNames 
                |> Option.defaultValue elem.ArrayName
            let (code, newName) = genElementBindingNew binding elem currentName
            lines <- lines @ [ind depth + code]
            currentNames <- Map.add elem.ArrayPosition newName currentNames
            match elem.Virtual with
            | VirtualRange _ | VirtualReverse ->
                paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
            | RealArray ->
                paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
    
    // Output index expression (shared by all kernels)
    let outputIdx = 
        codeGen.Bindings 
        |> List.map (fun b -> sprintf "[%s]" b.IndexName)
        |> String.concat ""
    
    // First kernel assignment (uses primary LoopNestCodeGen's Reynolds info)
    let nameMap0 = paramFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
    let nameMap0 = codeGen.Captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap0
    let reynoldsResult0 = genKernelExprWithReynolds codeGen.KernelExpr codeGen.KernelParams codeGen.HasReynolds codeGen.IsAntisymmetric nameMap0 paramFinalNames
    if codeGen.HasReynolds && reynoldsResult0.UniqueTerms < reynoldsResult0.TotalPerms then
        lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult0.UniqueTerms reynoldsResult0.TotalPerms (reynoldsResult0.TotalPerms / max 1 reynoldsResult0.UniqueTerms)]
    lines <- lines @ [ind depth + sprintf "%s%s = %s;" codeGen.OutputName outputIdx reynoldsResult0.CppExpr]
    
    // Additional kernel assignments
    // Each extra kernel has its own param VarIds that need mapping to the same peeled names.
    // We bridge via position: primary param[i].VarId → peeledName, extra param[i].VarId → same peeledName.
    for (outName, kernelExpr, extraParams, captures, hasReynolds, isAntisym) in extraKernels do
        let extraParamFinalNames =
            extraParams |> List.mapi (fun i p ->
                match codeGen.KernelParams |> List.tryItem i with
                | Some primaryParam ->
                    match Map.tryFind primaryParam.VarId paramFinalNames with
                    | Some peeledName -> Some (p.VarId, peeledName)
                    | None -> None
                | None -> None)
            |> List.choose id
            |> Map.ofList
        let nameMap = extraParamFinalNames |> Map.fold (fun acc k v -> Map.add k v acc) outerNames
        let nameMap = captures |> List.fold (fun acc c -> Map.add c.Id c.Name acc) nameMap
        let reynoldsResult = genKernelExprWithReynolds kernelExpr extraParams hasReynolds isAntisym nameMap extraParamFinalNames
        if hasReynolds && reynoldsResult.UniqueTerms < reynoldsResult.TotalPerms then
            lines <- lines @ [ind depth + sprintf "// Reynolds: %d/%d perms unique (dedup %dx)" reynoldsResult.UniqueTerms reynoldsResult.TotalPerms (reynoldsResult.TotalPerms / max 1 reynoldsResult.UniqueTerms)]
        lines <- lines @ [ind depth + sprintf "%s%s = %s;" outName outputIdx reynoldsResult.CppExpr]
    
    // Close all loops
    for _ in codeGen.Bindings do
        depth <- depth - 1
        lines <- lines @ [ind depth + "}"]
    
    lines


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
        codegenError ctx ind (sprintf "unsupported object_for configuration for '%s'" name)

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

// ============================================================================
// Recursive Parallel/Fusion Tree Helpers
// ============================================================================

/// Recursively generate code for a parallel composition tree (<&>).
/// Each leaf IRApplyCombinator gets its own independent loop nest.
/// Returns (code_lines, result_variable_name, tupleChildrenMap).
/// tupleChildrenMap tracks pair structure for nested tuple destructuring.
let rec genParallelTree (ctx: CodeGenContext) (name: string) (expr: IRExpr) (builder: IRBuilder) : string list * string * Map<string, string list> =
    let ind = indentStr ctx
    // Collect all leaf expressions from the parallel/fusion tree
    let rec collectLeaves (e: IRExpr) : IRExpr list =
        match e with
        | IRParallel (left, right, _) | IRFusion (left, right) ->
            collectLeaves left @ collectLeaves right
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> collectLeaves deferred
            | None -> [e]
        | _ -> [e]
    let leaves = collectLeaves expr
    match leaves with
    | [single] ->
        // Single leaf — generate directly, no tuple wrapping
        match single with
        | IRApplyCombinator info ->
            let code = genApplyCombinator ctx name info builder
            (code, name, Map.empty)
        | IRVar (id, _) ->
            let existingName = Map.tryFind id ctx.VarNames |> Option.defaultValue name
            ([], existingName, Map.empty)
        | _ ->
            let code = genScalarBinding ctx name single (inferExprType single)
            (code, name, Map.empty)
    | _ ->
        // Multiple leaves — generate each, assemble flat tuple
        let leafNames = leaves |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        let allCode =
            (leaves, leafNames) ||> List.map2 (fun leaf leafName ->
                match leaf with
                | IRApplyCombinator info ->
                    genApplyCombinator ctx leafName info builder @ [""]
                | IRVar (id, _) ->
                    let existingName = Map.tryFind id ctx.VarNames |> Option.defaultValue leafName
                    if existingName <> leafName then
                        [sprintf "%sauto& %s = %s;" ind leafName existingName; ""]
                    else []
                | _ ->
                    genScalarBinding ctx leafName leaf (inferExprType leaf) @ [""])
            |> List.concat
        let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
        let childMap = Map.ofList [name, leafNames]
        (allCode @ [tupleLine], name, childMap)

/// Collect all leaf expressions from a fusion tree in left-to-right order.
let rec collectFusionLeaves (expr: IRExpr) : IRExpr list =
    match expr with
    | IRFusion (left, right) -> collectFusionLeaves left @ collectFusionLeaves right
    | _ -> [expr]

/// Build nested std::make_pair expression matching the tree structure of an IRFusion/IRParallel.
/// Consumes names from the list in left-to-right order.
let rec buildPairTree (expr: IRExpr) (names: string list) : string * string list =
    match expr with
    | IRFusion (left, right) | IRParallel (left, right, _) ->
        let (leftStr, names') = buildPairTree left names
        let (rightStr, names'') = buildPairTree right names'
        (sprintf "std::make_pair(%s, %s)" leftStr rightStr, names'')
    | _ ->
        match names with
        | n :: rest -> (n, rest)
        | [] -> (exprError "internal: no names left in buildPairTree", [])

/// Generate code for N-way mandatory fusion (<&!>).
/// Collects all leaf ApplyCombinators, generates a single fused loop nest,
/// then builds nested pair tree for the result.
/// Build named pair tree from an IRExpr tree structure and a list of leaf names.
/// Generates named intermediate std::make_pair variables for each internal node.
/// Uses __p_ prefix for intermediates to avoid collision with leaf names.
/// Returns (code_lines, result_name, tupleChildrenMap).
let genFusionTree (ctx: CodeGenContext) (name: string) (expr: IRExpr) (builder: IRBuilder) : string list * string * Map<string, string list> =
    let ind = indentStr ctx
    let rawLeaves = collectFusionLeaves expr
    
    // Resolve IRVar leaves through DeferredComputations
    let leaves = rawLeaves |> List.map (fun leaf ->
        match leaf with
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> deferred
            | None -> leaf
        | _ -> leaf)
    
    // Extract ApplyInfo from each leaf
    let infos = leaves |> List.choose (fun e ->
        match e with
        | IRApplyCombinator info -> Some info
        | _ -> None)
    
    if infos.Length < 2 || infos.Length <> leaves.Length then
        // Not all leaves are ApplyCombinators — fall back to parallel generation
        genParallelTree ctx name expr builder
    else
        // Generate output names for each leaf
        let leafNames = infos |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
        
        // Use first info for loop structure
        let primaryInfo = infos.[0]
        let primaryName = leafNames.[0]
        
        // Extract array names from the shared loop
        let arrayNames = 
            primaryInfo.Arrays |> List.mapi (fun i arr ->
                match arr with
                | IRVar (id, _) -> 
                    Map.tryFind id ctx.VarNames |> Option.defaultValue (sprintf "arr%d" i)
                | IRRange _ -> sprintf "__range%d" i
                | IRVirtualReverse _ -> sprintf "__rev%d" i
                | IRBlocked _ -> sprintf "__blk%d" i
                | _ -> sprintf "arr%d" i)
        
        if arrayNames.IsEmpty then
            (codegenError ctx ind (sprintf "no arrays in method_for for parallel/sequence '%s'" name), name, Map.empty)
        else
            // Build primary LoopNestCodeGen
            let codeGenPrimary = buildLoopNestCodeGen primaryInfo arrayNames primaryName builder
            
            // Extract kernel info for all additional leaves
            let extraKernels = 
                (List.tail infos, List.tail leafNames) ||> List.map2 (fun info outName ->
                    let (kParams, kBody, _commGroups, captures, hasReynolds, isAntisym) =
                        match info.Kernel with
                        | IRLambda lInfo -> (lInfo.Params, lInfo.Body, lInfo.CommGroups, lInfo.Captures, false, false)
                        | IRReynolds (IRLambda lInfo, isAnti) -> (lInfo.Params, lInfo.Body, lInfo.CommGroups, lInfo.Captures, true, isAnti)
                        | _ -> ([], IRLit IRLitUnit, [], [], false, false)
                    (outName, kBody, kParams, captures, hasReynolds, isAntisym))
            
            // Generate symm vec + extents + allocation for each output
            let allCodeGens = infos |> List.mapi (fun i info ->
                buildLoopNestCodeGen info arrayNames leafNames.[i] builder)
            
            let declCode = allCodeGens |> List.mapi (fun i cg ->
                let lname = leafNames.[i]
                let symmVecName = sprintf "%s_symm" lname
                let symmVecDecl = genSymmVecDecl symmVecName cg.OutputSymmVec
                let outputRank = match cg.OutputType with IRTArray arr -> arrayRank arr | _ -> 0
                let outputElemType = match cg.OutputType with IRTArray arr -> elemTypeToCpp arr.ElemType | IRTScalar et -> elemTypeToCpp et | t -> irTypeToCpp t
                let extentsName = sprintf "%s_extents" lname
                let extentsDecl = sprintf "%ssize_t* %s = new size_t[%d];" ind extentsName outputRank
                let extentsFill = 
                    cg.Bindings |> List.mapi (fun j b ->
                        match b.Extent with
                        | IRLit (IRLitInt n) -> sprintf "%s%s[%d] = %d;" ind extentsName j n
                        | _ -> sprintf "%s%s[%d] = %s_extents[%d];" ind extentsName j b.ExtentArrayRef b.ExtentDimRef)
                let allocDecl = sprintf "%spromote<%s, %d>::type %s;" ind outputElemType outputRank lname
                let allocInit = sprintf "%s%s = allocate<typename promote<%s, %d>::type, %s>(%s);" 
                                    ind lname outputElemType outputRank symmVecName extentsName
                [symmVecDecl] @ [extentsDecl] @ extentsFill @ [allocDecl; allocInit]) |> List.concat
            
            // Generate single fused loop nest
            let loopCode = genFusedLoopNest codeGenPrimary extraKernels ctx.VarNames ctx.Indent
            
            // Build flat tuple from leaf names
            let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (leafNames |> String.concat ", ")
            let childrenMap = Map.ofList [name, leafNames]
            
            (declCode @ [""] @ loopCode @ [""] @ [tupleLine], name, childrenMap)

/// Compute the number of flat leaves for a type (recursing into nested tuples).
let rec tupleLeafCount (ty: IRType) : int =
    match ty with
    | IRTTuple ts -> ts |> List.sumBy tupleLeafCount
    | _ -> 1

/// For a tuple type, compute the flat child range [start, start+count) for each top-level element.
/// E.g. ((α,β), γ) → [(0, 2); (2, 1)] meaning element 0 spans flat indices 0..1, element 1 is flat index 2.
let tupleLeafRanges (ty: IRType) : (int * int) list =
    match ty with
    | IRTTuple ts ->
        let mutable offset = 0
        ts |> List.map (fun t ->
            let count = tupleLeafCount t
            let range = (offset, count)
            offset <- offset + count
            range)
    | _ -> [(0, 1)]

/// Generate C++ code for an IR binding
let rec genBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    // Make anonymous tuple binding names unique to avoid C++ redefinition errors
    let name = if binding.Name = "_" then sprintf "__tup_%d" binding.Id else binding.Name
    
    match binding.Value with
    | IRMask (arrExpr, predExpr) ->
        // mask(array, pred): eager compaction — scan, count, allocate, fill
        let arrName = exprToCppCtx ctx arrExpr
        let elemType =
            match inferExprType arrExpr with
            | IRTArray arr -> arr.ElemType
            | IRTScalar et -> et
            | _ -> ETFloat64
        let elemStr = elemTypeToCpp elemType
        
        // Extract predicate lambda param and body
        let (predParamId, predBody) =
            match predExpr with
            | IRLambda lInfo when lInfo.Params.Length = 1 ->
                (lInfo.Params.[0].VarId, lInfo.Body)
            | _ -> (0, IRLit (IRLitBool true))  // fallback: include everything
        let predParamName = sprintf "__%s_x" name
        let predNames = Map.add predParamId predParamName ctx.VarNames
        let predStr = exprToCpp predNames predBody
        
        let code = [
            sprintf "%s// mask: count + compact" ind
            sprintf "%ssize_t %s__count = 0;" ind name
            sprintf "%sfor (size_t __mi = 0; __mi < %s_extents[0]; __mi++) {" ind arrName
            sprintf "%s    %s %s = %s[__mi];" ind elemStr predParamName arrName
            sprintf "%s    if (%s) %s__count++;" ind predStr name
            sprintf "%s}" ind
            sprintf "%ssize_t %s_extents[1] = {%s__count};" ind name name
            sprintf "%s%s* %s = new %s[%s__count];" ind elemStr name elemStr name
            sprintf "%ssize_t %s__fill = 0;" ind name
            sprintf "%sfor (size_t __mi = 0; __mi < %s_extents[0]; __mi++) {" ind arrName
            sprintf "%s    %s %s = %s[__mi];" ind elemStr predParamName arrName
            sprintf "%s    if (%s) { %s[%s__fill++] = %s; }" ind predStr name name predParamName
            sprintf "%s}" ind
        ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRIntersect (aExpr, bExpr) ->
        // intersect(A, B): elements present in both arrays
        let ctx' = addVarName binding.Id name ctx
        let aName = exprToCppCtx ctx aExpr
        let bName = exprToCppCtx ctx bExpr
        let elemType =
            match inferExprType aExpr with
            | IRTArray arr -> elemTypeToCpp arr.ElemType
            | IRTScalar et -> elemTypeToCpp et
            | _ -> "double"
        let code = [
            sprintf "%s// intersect: build set from B, scan A" ind
            sprintf "%sstd::set<%s> %s__set;" ind elemType name
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) %s__set.insert(%s[__si]);" ind bName name bName
            sprintf "%ssize_t %s__count = 0;" ind name
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind aName
            sprintf "%s    if (%s__set.count(%s[__si])) %s__count++;" ind name aName name
            sprintf "%s}" ind
            sprintf "%ssize_t %s_extents[1] = {%s__count};" ind name name
            sprintf "%s%s* %s = new %s[%s__count];" ind elemType name elemType name
            sprintf "%ssize_t %s__fill = 0;" ind name
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind aName
            sprintf "%s    if (%s__set.count(%s[__si])) %s[%s__fill++] = %s[__si];" ind name aName name name aName
            sprintf "%s}" ind
        ]
        (code, ctx')
    
    | IRUnion (aExpr, bExpr) ->
        // union(A, B): all elements from A, plus elements from B not in A
        let ctx' = addVarName binding.Id name ctx
        let aName = exprToCppCtx ctx aExpr
        let bName = exprToCppCtx ctx bExpr
        let elemType =
            match inferExprType aExpr with
            | IRTArray arr -> elemTypeToCpp arr.ElemType
            | IRTScalar et -> elemTypeToCpp et
            | _ -> "double"
        let code = [
            sprintf "%s// union: all of A, plus elements from B not in A" ind
            sprintf "%sstd::set<%s> %s__set;" ind elemType name
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) %s__set.insert(%s[__si]);" ind aName name aName
            sprintf "%ssize_t %s__extra = 0;" ind name
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind bName
            sprintf "%s    if (!%s__set.count(%s[__si])) %s__extra++;" ind name bName name
            sprintf "%s}" ind
            sprintf "%ssize_t %s__total = %s_extents[0] + %s__extra;" ind name aName name
            sprintf "%ssize_t %s_extents[1] = {%s__total};" ind name name
            sprintf "%s%s* %s = new %s[%s__total];" ind elemType name elemType name
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) %s[__si] = %s[__si];" ind aName name aName
            sprintf "%ssize_t %s__fill = %s_extents[0];" ind name aName
            sprintf "%sfor (size_t __si = 0; __si < %s_extents[0]; __si++) {" ind bName
            sprintf "%s    if (!%s__set.count(%s[__si])) %s[%s__fill++] = %s[__si];" ind name bName name name bName
            sprintf "%s}" ind
        ]
        (code, ctx')
    
    | IRArrayLit (elements, arrType) ->
        let code = genArrayLiteral ctx name elements arrType
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRApplyCombinator info ->
        // Defer: computation not materialized until |> compute or combinator forces it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation>" ind name], ctx')
    
    | IRParallel _ | IRFusion _ ->
        // Defer: computation combinator not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred computation combinator>" ind name], ctx')
    
    | IRFunctorMap _ ->
        // Defer: functor map not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred functor map>" ind name], ctx')
    
    | IRZip _ ->
        // Defer: zip is a lazy array combinator, absorbed by method_for or materialized by |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred zip>" ind name], ctx')
    
    | IRChoice (left, right) ->
        // Only defer when children are computation-level (not scalar)
        let isCompExpr e = match e with IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
        if isCompExpr left || isCompExpr right then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred choice>" ind name], ctx')
        else
            // Scalar choice: generate directly
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRGuard (_, body) ->
        // Guard wrapping a computation: defer for later materialization via |> compute
        // Recurse through nested guards to check if the leaf body is a computation
        let rec leafIsComputation e =
            match e with
            | IRGuard (_, inner) -> leafIsComputation inner
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRSequence _ -> true
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | _ -> false
        if leafIsComputation body then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred guard>" ind name], ctx')
        else
            // Scalar guard: generate directly
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRSequence elems ->
        // Defer: sequence is a flat n-ary parallel, materialized by |> compute
        let isCompExpr e = match e with IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
        if elems |> List.exists isCompExpr then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred sequence>" ind name], ctx')
        else
            // All scalars: generate as tuple
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRCompute inner ->
        // Compute unwraps - handle the inner expression
        // Recursive resolver: peels IRFunctorMap wrappers, resolves IRVar through deferred,
        // and handles IRComposeMeth by extracting right's kernel as a functor wrapper.
        // Returns (innerExpr, wrapperFunctions) where wrappers are innermost-first
        let rec resolveComputation (expr: IRExpr) (wrappers: IRExpr list) : IRExpr * IRExpr list =
            match expr with
            | IRVar (id, _) ->
                match Map.tryFind id ctx.DeferredComputations with
                | Some deferred -> resolveComputation deferred wrappers
                | None -> (expr, wrappers)
            | IRFunctorMap (f, inner) ->
                resolveComputation inner (f :: wrappers)
            | IRGuard (cond, body) ->
                // Resolve through guard: push wrappers into the body
                let (innerResolved, innerWrappers) = resolveComputation body wrappers
                (IRGuard (cond, innerResolved), innerWrappers)
            | IRComposeMeth (left, right) ->
                // @>> : c1 @>> c2 means "at each index, apply c2's kernel to c1's result"
                // Only fold into wrappers if kernel is an inlinable IRLambda
                let rec extractInlinableKernel e =
                    match e with
                    | IRVar (id, _) ->
                        match Map.tryFind id ctx.DeferredComputations with
                        | Some d -> extractInlinableKernel d
                        | None -> None
                    | IRApplyCombinator info -> 
                        match info.Kernel with
                        | IRLambda _ -> Some info.Kernel  // Only fold if kernel is inline lambda
                        | _ -> None
                    | IRFunctorMap (f, inner) ->
                        match extractInlinableKernel inner with
                        | Some k -> Some (IRCompose (k, f))
                        | None -> None
                    | _ -> None
                match extractInlinableKernel right with
                | Some kernel -> resolveComputation left (kernel :: wrappers)
                | None -> (expr, wrappers)  // fallback: leave for IRComposeMeth handler
            | _ -> (expr, wrappers)
        
        let (resolved, functorWrappers) = resolveComputation inner []
        
        // Compose functor wrappers into ApplyInfo kernel if present
        // f <$> (L <@> g) → L <@> (f ∘ g)
        // Wraps kernel body: λparams → f(g(params))
        let applyFunctorWrappers (info: ApplyInfo) (wrappers: IRExpr list) : ApplyInfo =
            if wrappers.IsEmpty then info
            else
                // Beta-reduce: substitute wrapper's parameter with inner body
                // f <$> (L <@> g) where f = λx → h(x)
                // becomes L <@> λparams → h(g(params))
                let betaReduce (wrapper: IRExpr) (body: IRExpr) : IRExpr =
                    match wrapper with
                    | IRLambda wInfo when wInfo.Params.Length = 1 ->
                        // Substitute param VarId with body expression
                        let paramId = wInfo.Params.[0].VarId
                        let rec subst (expr: IRExpr) =
                            match expr with
                            | IRVar (id, _) when id = paramId -> body
                            | IRVar _ | IRLit _ | IRParam _ -> expr
                            | IRBinOp (m, op, l, r) -> IRBinOp (m, op, subst l, subst r)
                            | IRUnaryOp (op, e) -> IRUnaryOp (op, subst e)
                            | IRIf (c, t, e) -> IRIf (subst c, subst t, subst e)
                            | IRApp (f, args, rt) -> IRApp (subst f, args |> List.map subst, rt)
                            | IRIndex (a, idxs, ty) -> IRIndex (subst a, idxs |> List.map subst, ty)
                            | IRTuple es -> IRTuple (es |> List.map subst)
                            | IRTupleProj (e, i, flat) -> IRTupleProj (subst e, i, flat)
                            | IRFieldAccess (e, f) -> IRFieldAccess (subst e, f)
                            | IRLet (id, v, b) -> IRLet (id, subst v, subst b)
                            | _ -> expr  // For complex nodes, leave as-is
                        subst wInfo.Body
                    | _ ->
                        // Can't beta-reduce, fall back to IRApp (IIFE in C++)
                        let retTy = match wrapper with
                                    | IRLambda li -> inferExprType li.Body
                                    | _ -> IRTScalar ETFloat64
                        IRApp (wrapper, [body], retTy)
                
                let wrappedKernel =
                    match info.Kernel with
                    | IRLambda lInfo ->
                        // Apply wrappers innermost first (list is already innermost-first from peeling)
                        let wrappedBody =
                            wrappers |> List.fold (fun body wrapper ->
                                betaReduce wrapper body
                            ) lInfo.Body
                        IRLambda { lInfo with Body = wrappedBody }
                    | IRReynolds (IRLambda lInfo, isAnti) ->
                        let wrappedBody =
                            wrappers |> List.fold (fun body wrapper ->
                                betaReduce wrapper body
                            ) lInfo.Body
                        IRReynolds (IRLambda { lInfo with Body = wrappedBody }, isAnti)
                    | other -> other
                // Update output type from outermost wrapper's return type
                let newOutputType =
                    match wrappers |> List.tryHead with
                    | Some (IRLambda li) -> inferExprType li.Body
                    | _ -> info.OutputType
                // If output is an array, update the element type
                let adjustedOutputType =
                    match info.OutputType, newOutputType with
                    | IRTArray arr, IRTScalar et -> IRTArray { arr with ElemType = et }
                    | _ -> newOutputType
                { info with Kernel = wrappedKernel; OutputType = adjustedOutputType }
        
        match resolved with
        | IRApplyCombinator info ->
            // Check if Loop is a composed ObjectLoop (>>@)
            let resolvedLoop =
                match info.Loop with
                | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.defaultValue info.Loop
                | other -> other
            match resolvedLoop with
            | IRComposeObj (obj1, obj2) ->
                // >>@ applied to arrays: (object_for(f) >>@ object_for(g)) <@> A
                // Resolve obj1/obj2 through deferred variables
                let rec resolveObj e =
                    match e with
                    | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.map resolveObj |> Option.defaultValue e
                    | _ -> e
                let rObj1 = resolveObj obj1
                let rObj2 = resolveObj obj2
                let kernel1 = match rObj1 with IRObjectFor o -> o.Kernel | _ -> rObj1
                let kernel2 = match rObj2 with IRObjectFor o -> o.Kernel | _ -> rObj2
                let arrays = match info.Kernel with IRTuple elems -> elems | other -> [other]
                
                // Get C++ names for kernels (they're emitted as auto lambdas)
                let kernelName1 = match kernel1 with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
                let kernelName2 = match kernel2 with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
                
                // Get array info
                let arrName = 
                    match arrays with 
                    | [IRVar (id, _)] -> Map.tryFind id ctx.VarNames |> Option.defaultValue "arr0" 
                    | _ -> "arr0"
                let arrRank = 
                    match arrays with 
                    | [a] -> (match inferExprType a with IRTArray arr -> arrayRank arr | _ -> 1) 
                    | _ -> 1
                let elemType = "double"
                
                match kernelName1, kernelName2 with
                | Some k1, Some k2 ->
                    // Both kernels are named C++ lambdas — generate function-call loops
                    let s1Name = sprintf "%s__s1" name
                    let s1Code = [
                        sprintf "%sstatic constexpr const size_t* %s_symm = nullptr;" ind s1Name
                        sprintf "%sconst size_t* %s_extents = %s_extents;" ind s1Name arrName
                        sprintf "%spromote<%s, %d>::type %s;" ind elemType arrRank s1Name
                        sprintf "%s%s = allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents);" ind s1Name elemType arrRank s1Name s1Name
                        sprintf "%sfor (size_t __i0 = 0; __i0 < %s_extents[0]; __i0++) {" ind arrName
                        sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind s1Name k1 arrName
                        sprintf "%s}" ind
                    ]
                    let s2Code = [
                        sprintf "%sstatic constexpr const size_t* %s_symm = nullptr;" ind name
                        sprintf "%sconst size_t* %s_extents = %s_extents;" ind name s1Name
                        sprintf "%spromote<%s, %d>::type %s;" ind elemType arrRank name
                        sprintf "%s%s = allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents);" ind name elemType arrRank name name
                        sprintf "%sfor (size_t __i0 = 0; __i0 < %s_extents[0]; __i0++) {" ind s1Name
                        sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name k2 s1Name
                        sprintf "%s}" ind
                    ]
                    let ctx' = addVarName binding.Id name ctx
                    (s1Code @ [""] @ s2Code, ctx')
                | _ ->
                    // Fallback: kernels are inline lambdas, use ApplyInfo path
                    let s1Name = sprintf "%s__s1" name
                    let s1Id = builder.FreshId()
                    let s1ElemType =
                        match kernel1 with 
                        | IRLambda li -> (match inferExprType li.Body with IRTScalar et -> et | _ -> ETFloat64) 
                        | _ -> ETFloat64
                    let inputArrayTypes = arrays |> List.map (fun a -> 
                        match inferExprType a with 
                        | IRTArray arr -> arr 
                        | _ -> defaultArrayType ETFloat64)
                    let totalInputDims = inputArrayTypes |> List.sumBy arrayRank
                    let s1Type = IRTArray { ElemType = s1ElemType; IndexTypes = [for _ in 1..totalInputDims -> defaultIndexType ()]; IsVirtual = false; Identity = None }
                    let s1Info = buildSimpleApplyInfo arrays kernel1 s1Type
                    let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute (IRApplyCombinator s1Info); IsConst = true; IsMutable = false }
                    let (code1, ctx1) = genBinding ctx s1Binding builder
                    
                    let s2OutputType =
                        match binding.Type with
                        | IRTUnit -> match s1Type with IRTArray arr -> IRTArray { arr with ElemType = s1ElemType } | _ -> IRTArray (defaultArrayType s1ElemType)
                        | other -> other
                    let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] kernel2 s2OutputType
                    let code2 = genApplyCombinator ctx1 name s2Info builder
                    let ctx2 = addVarName binding.Id name ctx1
                    (code1 @ [""] @ code2, ctx2)
            
            | _ ->
                // Normal apply
                let info' = applyFunctorWrappers info functorWrappers
                let code = genApplyCombinator ctx name info' builder
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        
        | IRComposeMeth (left, right) ->
            // @>> : sequential composition — compute left, feed result to right's kernel
            // Stage 1: materialize left computation
            let s1Name = sprintf "%s__s1" name
            let s1Id = builder.FreshId()
            
            // Resolve left through deferred
            let rec resolveDeferred e =
                match e with
                | IRVar (id, _) -> 
                    match Map.tryFind id ctx.DeferredComputations with
                    | Some d -> resolveDeferred d
                    | None -> e
                | _ -> e
            let resolvedLeft = resolveDeferred left
            let resolvedRight = resolveDeferred right

            // Extract right's kernel
            let rightKernel = 
                match resolvedRight with 
                | IRApplyCombinator info -> info.Kernel 
                | _ -> resolvedRight
            let rightKernelName = 
                match rightKernel with 
                | IRVar (id, _) -> Map.tryFind id ctx.VarNames 
                | _ -> None
            
            // Materialize left as stage 1
            let s1Type = inferExprType resolvedLeft
            let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedLeft; IsConst = true; IsMutable = false }
            let (code1, ctx1) = genBinding ctx s1Binding builder
            
            match rightKernelName with
            | Some kName ->
                // Right kernel is a named function — generate element-wise function-call loop
                let arrRank = match s1Type with IRTArray arr -> arrayRank arr | _ -> 1
                let elemType = match s1Type with IRTArray arr -> elemTypeToCpp arr.ElemType | _ -> "double"
                let s2Code = [
                    sprintf "%sstatic constexpr const size_t* %s_symm = nullptr;" ind name
                    sprintf "%sconst size_t* %s_extents = %s_extents;" ind name s1Name
                    sprintf "%spromote<%s, %d>::type %s;" ind elemType arrRank name
                    sprintf "%s%s = allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents);" ind name elemType arrRank name name
                    sprintf "%sfor (size_t __i0 = 0; __i0 < %s_extents[0]; __i0++) {" ind s1Name
                    sprintf "%s    %s[__i0] = %s(%s[__i0]);" ind name kName s1Name
                    sprintf "%s}" ind
                ]
                let ctx2 = addVarName binding.Id name ctx1
                (code1 @ [""] @ s2Code, ctx2)
            | None ->
                // Right kernel is inline lambda — use buildSimpleApplyInfo path
                let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] rightKernel binding.Type
                let code2 = genApplyCombinator ctx1 name s2Info builder
                let ctx2 = addVarName binding.Id name ctx1
                (code1 @ [""] @ code2, ctx2)
        
        | IRBind (comp, cont) ->
            // Monadic bind: c >>= k
            // Stage 1: materialize comp
            let s1Name = sprintf "%s__s1" name
            let s1Id = builder.FreshId()
            
            // Resolve comp through deferred
            let rec resolveDeferred e =
                match e with
                | IRVar (id, _) -> 
                    match Map.tryFind id ctx.DeferredComputations with
                    | Some d -> resolveDeferred d
                    | None -> e
                | _ -> e
            let resolvedComp = resolveDeferred comp
            
            let s1Type = inferExprType resolvedComp
            let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedComp; IsConst = true; IsMutable = false }
            let (code1, ctx1) = genBinding ctx s1Binding builder
            
            // Resolve continuation to IRLambda
            let resolvedCont = resolveDeferred cont
            match resolvedCont with
            | IRLambda lInfo when lInfo.Params.Length >= 1 ->
                // Bind lambda parameter to stage 1 result
                let param = lInfo.Params.[0]
                let ctx2 = addVarName param.VarId s1Name ctx1
                
                // Generate code for lambda body as a computation
                let bodyBinding = { Id = binding.Id; Name = name; Type = binding.Type; Value = IRCompute lInfo.Body; IsConst = true; IsMutable = false }
                let (code2, ctx3) = genBinding ctx2 bodyBinding builder
                (code1 @ [""] @ code2, ctx3)
            | _ ->
                // Fallback: continuation not resolvable to lambda — generate function call
                let contName = match cont with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
                match contName with
                | Some kName ->
                    let code = [sprintf "%sauto %s = %s(%s);" ind name kName s1Name]
                    let ctx' = addVarName binding.Id name ctx1
                    (code1 @ [""] @ code, ctx')
                | None ->
                    let code = genScalarBinding ctx1 name (IRApp(cont, [IRVar(s1Id, s1Type)], binding.Type)) binding.Type
                    let ctx' = addVarName binding.Id name ctx1
                    (code1 @ [""] @ code, ctx')

        | IRParallel _ ->
            // Parallel composition: recursively generate independent loops, combine as nested pairs
            let (code, _, childrenMap) = genParallelTree ctx name resolved builder
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
            (code, ctx')
        
        | IRFusion _ ->
            // Mandatory fusion: single fused loop nest with all kernels
            let (code, _, childrenMap) = genFusionTree ctx name resolved builder
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
            (code, ctx')
        
        | IRChoice (left, right) ->
            // Computation-level choice: materialize both sides, element-wise combine
            // result[i] = (lhs[i] != 0) ? lhs[i] : rhs[i]
            // If functor wrappers present: f <$> (c1 <|> c2) ≡ (f <$> c1) <|> (f <$> c2)
            let wrapSide side =
                if functorWrappers.IsEmpty then side
                else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) side
            let left' = wrapSide left
            let right' = wrapSide right
            let nameL = sprintf "%s__lhs" name
            let nameR = sprintf "%s__rhs" name
            let idL = builder.FreshId()
            let idR = builder.FreshId()
            let bindingL = { Id = idL; Name = nameL; Type = binding.Type; Value = IRCompute left'; IsConst = true; IsMutable = false }
            let bindingR = { Id = idR; Name = nameR; Type = binding.Type; Value = IRCompute right'; IsConst = true; IsMutable = false }
            let (codeL, ctxL) = genBinding ctx bindingL builder
            let (codeR, ctxR) = genBinding ctxL bindingR builder
            
            let ind = indentStr ctx
            let rank = match binding.Type with IRTArray arr -> arrayRank arr | _ -> 0
            let elemType = match binding.Type with IRTArray arr -> elemTypeToCpp arr.ElemType | _ -> "double"
            
            if rank = 0 then
                // Scalar choice
                let code = [sprintf "%s%s %s = (%s != 0) ? %s : %s;" ind elemType name nameL nameL nameR]
                let ctx' = addVarName binding.Id name ctxR
                (codeL @ [""] @ codeR @ [""] @ code, ctx')
            else
                // Array choice: allocate result, element-wise combine
                let extentsName = sprintf "%s_extents" nameL  // reuse left's extents
                let symmVecName = sprintf "%s_symm" nameL     // reuse left's symmetry
                // Create extents alias (runtime) and symm alias (must be static constexpr for template)
                let extentsAlias = sprintf "%ssize_t* %s_extents = %s;" ind name extentsName
                let symmAlias = sprintf "%sstatic constexpr const size_t* %s_symm = %s;" ind name symmVecName
                let allocDecl = sprintf "%spromote<%s, %d>::type %s;" ind elemType rank name
                let allocInit = sprintf "%s%s = allocate<typename promote<%s, %d>::type, %s_symm>(%s_extents);" 
                                    ind name elemType rank name name
                
                // Generate nested loops for element-wise choice
                let mutable loopLines = []
                let mutable depth = ctx.Indent
                let indD d = String.replicate d "    "
                for i in 0 .. rank - 1 do
                    let bound = sprintf "%s_extents[%d]" name i
                    loopLines <- loopLines @ [sprintf "%sfor (size_t __i%d = 0; __i%d < %s; __i%d++) {" (indD depth) i i bound i]
                    depth <- depth + 1
                
                let idxStr = [for i in 0 .. rank - 1 -> sprintf "[__i%d]" i] |> String.concat ""
                let lhsElem = sprintf "%s%s" nameL idxStr
                let rhsElem = sprintf "%s%s" nameR idxStr
                loopLines <- loopLines @ [sprintf "%s%s%s = (%s != 0) ? %s : %s;" (indD depth) name idxStr lhsElem lhsElem rhsElem]
                
                for _ in 0 .. rank - 1 do
                    depth <- depth - 1
                    loopLines <- loopLines @ [sprintf "%s}" (indD depth)]
                
                let ctx' = addVarName binding.Id name ctxR
                (codeL @ [""] @ codeR @ [""] @ [extentsAlias; symmAlias; allocDecl; allocInit; ""] @ loopLines, ctx')
        
        | IRGuard (cond, body) ->
            // guard(p, c) |> compute: conditionally execute computation
            // Strategy: wrap the kernel body with the guard condition
            // guard(cond, L <@> f) → L <@> (λargs → cond ? f(args) : 0)
            // This allocates the array always but fills with zeros when false
            let isComputation =
                match body with
                | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ -> true
                | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
                | _ -> false
            if isComputation then
                // Resolve the inner computation
                let resolvedBody =
                    match body with
                    | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.defaultValue body
                    | _ -> body
                match resolvedBody with
                | IRApplyCombinator info ->
                    // Wrap kernel: λparams → cond ? kernel_body : 0
                    let wrappedKernel =
                        match info.Kernel with
                        | IRLambda lInfo ->
                            let zeroVal =
                                match inferExprType lInfo.Body with
                                | IRTScalar ETBool -> IRLit (IRLitBool false)
                                | IRTScalar ETInt64 | IRTScalar ETInt32 -> IRLit (IRLitInt 0L)
                                | _ -> IRLit (IRLitFloat 0.0)
                            IRLambda { lInfo with Body = IRIf (cond, lInfo.Body, zeroVal) }
                        | other -> other  // Can't wrap non-lambda kernels
                    let guardedInfo = { info with Kernel = wrappedKernel }
                    // Apply any functor wrappers
                    let finalInfo = applyFunctorWrappers guardedInfo functorWrappers
                    let code = genApplyCombinator ctx name finalInfo builder
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
                | _ ->
                    // Non-apply computation (parallel, fusion, etc.) — fall back to scalar guard
                    let guardExpr = IRGuard (cond, body)
                    let code = genScalarBinding ctx name guardExpr binding.Type
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
            else
                // Scalar guard: treat as scalar expression via exprToCpp
                let guardExpr = IRGuard (cond, body)
                let code = genScalarBinding ctx name guardExpr binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        
        | IRSequence elems ->
            // Homogeneous n-ary parallel: each child produces same type
            // Result is array indexed by Idx<N> containing the child results
            // IMPORTANT: each child generates against the original ctx, not accumulated,
            // to prevent one child's output from contaminating another's array resolution.
            let n = elems.Length
            let childNames = elems |> List.mapi (fun i _ -> sprintf "%s_%d" name i)
            let (allCode, mergedVarNames) =
                (elems, childNames) ||> List.map2 (fun elem childName ->
                    let wrappedElem =
                        if functorWrappers.IsEmpty then elem
                        else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) elem
                    let childType =
                        match wrappedElem with
                        | IRApplyCombinator info -> info.OutputType
                        | _ -> inferExprType wrappedElem
                    let childBinding = { Id = builder.FreshId(); Name = childName; Type = childType; Value = IRCompute wrappedElem; IsConst = true; IsMutable = false }
                    genBinding ctx childBinding builder)
                |> List.fold (fun (accCode, accNames) (code, newCtx) ->
                    (accCode @ code @ [""], Map.fold (fun a k v -> Map.add k v a) accNames newCtx.VarNames)
                ) ([], ctx.VarNames)
            // Determine child element type and rank
            let childType = inferExprType (List.head elems)
            let (childElemType, childRank) =
                match childType with
                | IRTArray arr -> (elemTypeToCpp arr.ElemType, arrayRank arr)
                | IRTScalar et -> (elemTypeToCpp et, 0)
                | _ -> ("double", 0)
            let outerRank = childRank + 1
            // Build extents array: [N, child_extents...]
            let extentsEntries =
                [sprintf "%d" n]
                @ [for d in 0 .. childRank - 1 -> sprintf "%s_extents[%d]" (List.head childNames) d]
            let extentsDecl = sprintf "%ssize_t %s_extents[%d] = {%s};" ind name outerRank (extentsEntries |> String.concat ", ")
            // Allocate pointer array (for array children) or value array (for scalar children)
            let allocDecl =
                if childRank > 0 then
                    sprintf "%spromote<%s, %d>::type %s = new %s*[%d];" ind childElemType outerRank name childElemType n
                else
                    sprintf "%spromote<%s, 1>::type %s = new %s[%d];" ind childElemType name childElemType n
            let assignLines =
                childNames |> List.mapi (fun i cn ->
                    sprintf "%s%s[%d] = %s;" ind name i cn)
            let ctx' = { ctx with VarNames = Map.add binding.Id name mergedVarNames }
            (allCode @ [extentsDecl; allocDecl] @ assignLines, ctx')
        
        | _ ->
            // Other compute expressions - treat as scalar
            let code = genScalarBinding ctx name resolved binding.Type
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
        // Check if body is a computation expression (e.g., method_for(arr) <@> f)
        // Such lambdas are continuation lambdas for >>= — they return computations,
        // not values, so they can't be rendered as simple C++ lambdas.
        // The bind handler inlines their body through DeferredComputations.
        let isCompBody =
            match info.Body with
            | IRApplyCombinator _ | IRBind _ | IRComposeMeth _ | IRComposeObj _
            | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ -> true
            | _ -> false
        if isCompBody then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <continuation lambda (deferred for >>=)>" ind name], ctx')
        else
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
        let code =
            if isUnitExpr info.Body then
                [sprintf "%s%s %s = [&](%s) { %s; };" ind funcType name paramList bodyStr]
            else
                [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType name paramList retType bodyStr]
        let ctx' = addVarName binding.Id name ctx
        // Also store IR for bind (>>=) continuation resolution
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
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
    
    | IRTupleProj (parentExpr, projIdx, isFlat) ->
        // Check if parent is a deferred computation tuple — if so, project and defer
        let parentDeferred =
            match parentExpr with
            | IRVar (pid, _) -> Map.tryFind pid ctx.DeferredComputations
            | _ -> None
        match parentDeferred with
        | Some (IRTuple elems) when projIdx < elems.Length ->
            // Parent is a deferred tuple — project out the element and defer it
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id elems.[projIdx] ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation (tuple proj)>" ind name], ctx')
        | Some (IRParallel _ | IRFusion _) ->
            // Parent is a deferred combinator — defer the projection too
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation (proj of combinator)>" ind name], ctx')
        | _ ->
            // Tuple projection — resolve through TupleChildren map
            let parentName =
                match parentExpr with
                | IRVar (pid, _) -> Map.tryFind pid ctx.VarNames |> Option.defaultValue "_"
                | _ -> "_"
            let parentType = inferExprType parentExpr
            let flatChildren =
                match Map.tryFind parentName ctx.TupleChildren with
                | Some children -> children
                | None -> []

            if isFlat then
                // Flat projection: projIdx is a flat leaf index
                if projIdx < flatChildren.Length then
                    let sourceName = flatChildren.[projIdx]
                    let code = [sprintf "%sauto& %s = %s;" ind name sourceName]
                    let extentsAlias =
                        match IR.stripUnits binding.Type with
                        | IRTArray _ ->
                            [sprintf "%sconst size_t* %s_extents = %s_extents;" ind name sourceName]
                        | _ -> []
                    let ctx' = addVarName binding.Id name ctx
                    let ctx' =
                        match Map.tryFind sourceName ctx'.TupleChildren with
                        | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                        | None -> ctx'
                    (code @ extentsAlias, ctx')
                else
                    let code = genScalarBinding ctx name binding.Value binding.Type
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
            else
                // Structural projection: projIdx is a type-level index
                let ranges = tupleLeafRanges parentType
                let (flatStart, leafCount) =
                    if projIdx < ranges.Length then ranges.[projIdx]
                    else (projIdx, 1)

                if leafCount > 1 && flatChildren.Length > 0 && flatStart + leafCount <= flatChildren.Length then
                    // Sub-tuple: synthesize from flat children range
                    let subChildren = flatChildren.[flatStart .. flatStart + leafCount - 1]
                    let tupleLine = sprintf "%sauto %s = std::make_tuple(%s);" ind name (subChildren |> String.concat ", ")
                    let ctx' = addVarName binding.Id name ctx
                    let ctx' = { ctx' with TupleChildren = Map.add name subChildren ctx'.TupleChildren }
                    ([tupleLine], ctx')

                elif flatStart < flatChildren.Length then
                    // Single leaf at computed position
                    let sourceName = flatChildren.[flatStart]
                    let code = [sprintf "%sauto& %s = %s;" ind name sourceName]
                    let extentsAlias =
                        match IR.stripUnits binding.Type with
                        | IRTArray _ ->
                            [sprintf "%sconst size_t* %s_extents = %s_extents;" ind name sourceName]
                        | _ -> []
                    let ctx' = addVarName binding.Id name ctx
                    let ctx' =
                        match Map.tryFind sourceName ctx'.TupleChildren with
                        | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                        | None -> ctx'
                    (code @ extentsAlias, ctx')

                else
                    // No TupleChildren — fall back to std::get
                    let code = genScalarBinding ctx name binding.Value binding.Type
                    let ctx' = addVarName binding.Id name ctx
                    (code, ctx')
    
    | IRVar (srcId, _) ->
        // Check if source is deferred — propagate deferral
        match Map.tryFind srcId ctx.DeferredComputations with
        | Some deferred ->
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id deferred ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation (alias)>" ind name], ctx')
        | None ->
            // Variable reference — may be aliasing a tuple, propagate children
            let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue ""
            let hasTupleChildren = Map.containsKey srcName ctx.TupleChildren
            // Use auto& when source has flat TupleChildren to avoid type mismatch
            let code =
                if hasTupleChildren then
                    [sprintf "%sauto& %s = %s;" ind name srcName]
                else
                    genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            let ctx' =
                match Map.tryFind srcName ctx'.TupleChildren with
                | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                | None -> ctx'
            (code, ctx')

    | IRBind (comp, cont) ->
        // Monadic bind: defer if comp is a deferred computation
        let isCompDeferred =
            match comp with
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
            | _ -> false
        if isCompDeferred then
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred bind>" ind name], ctx')
        else
            // Scalar bind: cont(comp)
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')

    | IRTuple _ | IRFieldAccess _ | IRLit _ | IRBinOp _ | IRUnaryOp _ | IRIf _ | IRApp _ | IRParam _ | IRMatch _
    | IRPure _ | IRIndex _ ->
        // Check if it's a tuple of deferred computations
        match binding.Value with
        | IRTuple elems when elems |> List.forall (fun e ->
            match e with
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | _ -> false) ->
            // All elements are computations — defer the whole tuple
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
            ([sprintf "%s// %s = <deferred computation tuple>" ind name], ctx')
        | _ ->
            // Scalar expressions including tuples, field access, match, bind, pure
            let code = genScalarBinding ctx name binding.Value binding.Type
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
    
    | IRCompose _ ->
        // Function composition: uses generic lambdas (auto... args)
        let valueStr = exprToCppCtx ctx binding.Value
        let code = [sprintf "%sauto %s = %s;" ind name valueStr]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRComposeObj _ ->
        // Defer: ObjectLoop composition, materialized when applied via <@>
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose_obj>" ind name], ctx')

    | IRComposeMeth _ ->
        // Defer: computation composition, materialized when |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([sprintf "%s// %s = <deferred compose_meth>" ind name], ctx')
    
    | IRLet _ ->
        // Block expression: unroll the IRLet chain into sequential bindings
        let (lets, finalExpr) = unrollLetChain binding.Value
        let (allCode, foldCtx) =
            lets |> List.fold (fun (accCode, accCtx) (id, value) ->
                let tempName = sprintf "__v%d" id
                let tempBinding = {
                    Id = id; Name = tempName; Type = inferExprType value
                    Value = value; IsConst = true; IsMutable = false
                }
                let (code, ctx') = genBinding accCtx tempBinding builder
                (accCode @ code, ctx')
            ) ([], ctx)
        // Generate the final named binding
        let finalBinding = {
            Id = binding.Id; Name = name; Type = binding.Type
            Value = finalExpr; IsConst = binding.IsConst; IsMutable = binding.IsMutable
        }
        let (finalCode, finalCtx) = genBinding foldCtx finalBinding builder
        (allCode @ finalCode, finalCtx)

    | IRAssign _ ->
        // Assignment expression: generate as statement
        let code = [sprintf "%s%s;" ind (exprToCppCtx ctx binding.Value)]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRForRange (vid, lo, hi, body) ->
        // Imperative for-range loop
        let loStr = exprToCppCtx ctx lo
        let hiStr = exprToCppCtx ctx hi
        let varName = sprintf "__k%d" vid
        let innerCtx = addVarName vid varName ctx
        // Unroll the body IRLet chain into statements
        let (bodyLets, _bodyFinal) = unrollLetChain body
        let (bodyCode, _) =
            bodyLets |> List.fold (fun (accCode, accCtx) (id, value) ->
                let tempName = sprintf "__v%d" id
                let tempBinding = {
                    Id = id; Name = tempName; Type = inferExprType value
                    Value = value; IsConst = true; IsMutable = false
                }
                let (code, ctx') = genBinding { accCtx with Indent = ctx.Indent + 1 } tempBinding builder
                (accCode @ code, { ctx' with Indent = ctx.Indent })
            ) ([], innerCtx)
        let code =
            [sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" ind varName loStr varName hiStr varName]
            @ bodyCode
            @ [sprintf "%s}" ind]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | other ->
        let ctx' = addVarName binding.Id name ctx
        let nodeType = other.GetType().Name
        (codegenError ctx ind (sprintf "unsupported expression for binding '%s' (IR node: %s)" name nodeType), ctx')

// ============================================================================
// Module Generation
// ============================================================================

/// Generate a function body as a list of C++ statements.
/// Unrolls IRLet chains into sequential variable declarations with a final return.
let genFuncBody (names: Map<IRId, string>) (indent: string) (body: IRExpr) : string list =
    // Deep unroll: flatten all nested IRLet chains into a flat list
    let rec deepUnroll (expr: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match expr with
        | IRLet (id, value, body) ->
            // Check if value itself contains nested IRLets
            let (innerLets, innerFinal) = deepUnroll value
            let (restLets, restFinal) = deepUnroll body
            // If value had nested lets, emit those first, then bind the final value
            match innerLets with
            | [] -> ((id, value) :: restLets, restFinal)
            | _ -> (innerLets @ [(id, innerFinal)] @ restLets, restFinal)
        | _ -> ([], expr)
    let (lets, retExpr) = deepUnroll body
    let mutable currentNames = names
    let stmts = lets |> List.collect (fun (id, value) ->
        let varName = sprintf "__v%d" id
        match value with
        | IRForRange (vid, lo, hi, forBody) ->
            let loopVar = sprintf "__k%d" vid
            let loStr = exprToCpp currentNames lo
            let hiStr = exprToCpp currentNames hi
            let innerNames = Map.add vid loopVar currentNames
            let (bodyLets, _) = deepUnroll forBody
            let mutable bodyNames = innerNames
            let bodyStmts = bodyLets |> List.collect (fun (bid, bval) ->
                let bName = sprintf "__v%d" bid
                match bval with
                | IRAssign (target, v) ->
                    let targetStr =
                        match target with
                        | LVVar tid -> Map.tryFind tid bodyNames |> Option.defaultValue (sprintf "__v%d" tid)
                        | _ -> exprToCpp bodyNames target
                    bodyNames <- Map.add bid bName bodyNames
                    [sprintf "%s    %s = %s;" indent targetStr (exprToCpp bodyNames v)]
                | _ ->
                    let valStr = exprToCpp bodyNames bval
                    bodyNames <- Map.add bid bName bodyNames
                    [sprintf "%s    auto %s = %s;" indent bName valStr])
            currentNames <- Map.add id varName currentNames
            [sprintf "%sfor (size_t %s = %s; %s < %s; %s++) {" indent loopVar loStr loopVar hiStr loopVar]
            @ bodyStmts
            @ [sprintf "%s}" indent]
        | IRAssign (target, v) ->
            let targetStr =
                match target with
                | LVVar tid -> Map.tryFind tid currentNames |> Option.defaultValue (sprintf "__v%d" tid)
                | _ -> exprToCpp currentNames target
            currentNames <- Map.add id varName currentNames
            [sprintf "%s%s = %s;" indent targetStr (exprToCpp currentNames v)]
        | IRLit IRLitUnit ->
            // Skip unit literals (side effects already emitted)
            currentNames <- Map.add id varName currentNames
            []
        | IRMethodFor _ | IRObjectFor _ ->
            // Loop objects are compile-time only — they're resolved when <@> is processed
            currentNames <- Map.add id varName currentNames
            []
        | IRApplyCombinator _ ->
            // Unevaluated computations — deferred until |> compute forces them
            currentNames <- Map.add id varName currentNames
            []
        | _ ->
            let valStr = exprToCpp currentNames value
            currentNames <- Map.add id varName currentNames
            [sprintf "%sauto %s = %s;" indent varName valStr])
    if isUnitExpr retExpr then
        stmts  // Void function: no return statement needed
    else
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
    let code =
        if isUnitExpr funcDef.Body then
            [sprintf "%s%s %s = [&](%s) { %s; };" ind funcType safeName paramList bodyStr]
        else
            [sprintf "%s%s %s = [&](%s) -> %s { return %s; };" ind funcType safeName paramList retType bodyStr]
    let ctx' = addVarName funcDef.Id funcDef.Name ctx
    (code, ctx')

/// Generate C++ code for an entire IR module.
/// Returns (functionDefs, bindingCode) — functions go outside main(), bindings inside.
let genModule (modul: IRModule) (builder: IRBuilder) : string list * string list =
    let ctx0 = emptyContext ()
    
    // Collect constrained struct names from type definitions
    let constrainedNames = 
        modul.Types |> List.choose (function
            | IRTDStruct (name, _, Some _) -> Some name
            | _ -> None)
        |> Set.ofList
    let ctx0 = { ctx0 with ConstrainedStructs = constrainedNames }
    
    // First pass: register ALL names (both bindings and functions) in context
    let ctx0 =
        modul.Bindings |> List.fold (fun c b -> addVarName b.Id b.Name c) ctx0
    let ctx0 =
        modul.Functions |> List.fold (fun c f -> addVarName f.Id f.Name c) ctx0
    
    // Build a combined list of items with their "order" based on ID
    // Lower IDs were created earlier in the source
    let bindingItems = modul.Bindings |> List.map (fun b -> (b.Id, Choice1Of2 b))
    let funcItems = modul.Functions |> List.map (fun f -> (f.Id, Choice2Of2 f))
    let allItems = bindingItems @ funcItems |> List.sortBy fst
    
    // Build set of all function IDs (to exclude from free var checks)
    let funcIds = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    
    // Generate in ID order (approximates source order)
    // First, collect file-scope functions to generate forward declarations
    let hasFreeVarsCheck (funcDef: IRFuncDef) (c: CodeGenContext) =
        let paramIds = funcDef.Params |> List.map (fun p -> p.VarId) |> Set.ofList
        let freeVars = Set.difference (collectVarRefs funcDef.Body) (Set.union paramIds funcIds)
        freeVars |> Set.exists (fun id -> Map.containsKey id c.VarNames)
    
    let fileScopeFuncs =
        allItems |> List.choose (fun (_, item) ->
            match item with
            | Choice2Of2 funcDef when not (hasFreeVarsCheck funcDef ctx0) -> Some funcDef
            | _ -> None)
    
    // Generate forward declarations for all file-scope functions
    let forwardDecls =
        fileScopeFuncs |> List.map (fun funcDef ->
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
            sprintf "%s %s(%s);" retType safeName paramList)
    let forwardDecls = if forwardDecls.IsEmpty then [] else forwardDecls @ [""]
    
    let (funcCode, bindCode, finalCtx) =
        allItems |> List.fold (fun (fc, bc, c) (_, item) ->
            match item with
            | Choice1Of2 binding ->
                let (code, c') = genBinding c binding builder
                (fc, bc @ code @ [""], c')
            | Choice2Of2 funcDef ->
                if hasFreeVarsCheck funcDef c then
                    let (code, c') = genFuncDefAsLambda c funcDef
                    (fc, bc @ code @ [""], c')
                else
                    let (code, c') = genFuncDef c funcDef
                    (fc @ code @ [""], bc, c')
        ) (forwardDecls, [], ctx0)
    
    // Merge context warnings into module-level collector
    exprWarnings.Value <- exprWarnings.Value @ finalCtx.Warnings.Value
    
    (funcCode, bindCode)

/// Generate a complete C++ program with main() from an IR module
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
    if rank < 1 then
        [sprintf "    cout << \"%s = <rank-0>\" << endl;" name]
    elif rank <= 3 then
        // Ranks 1-3: flat comma-separated output
        let loopVars = [| "i"; "j"; "k" |]
        let opens = [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar ]
        let loops =
            [ for d in 0 .. rank - 1 ->
                sprintf "    %sfor (size_t %s = 0; %s < %s_extents[%d]; %s++) {"
                    (String.replicate d "    ") loopVars.[d] loopVars.[d] name d loopVars.[d] ]
        let inner =
            let ind = "    " + String.replicate rank "    "
            let idx = loopVars.[0..rank-1] |> Array.map (sprintf "[%s]") |> String.concat ""
            [ sprintf "%sif (!%s) cout << \", \";" ind firstVar
              sprintf "%s%s = false;" ind firstVar
              sprintf "%scout << %s%s;" ind name idx ]
        let closes =
            [ for d in rank - 1 .. -1 .. 0 ->
                sprintf "    %s}" (String.replicate d "    ") ]
        let finish = [ sprintf "    cout << \"]\" << endl;" ]
        opens @ loops @ inner @ closes @ finish
    elif rank = 4 then
        // Rank 4: 2D grid of 2D blocks
        // Print as: name[i][j] = [ row0; row1; ... ] for each (i,j)
        [
            sprintf "    cout << \"%s (\" << %s_extents[0] << \"x\" << %s_extents[1] << \"x\" << %s_extents[2] << \"x\" << %s_extents[3] << \"):\" << endl;"
                name name name name name
            sprintf "    for (size_t i = 0; i < %s_extents[0]; i++) {" name
            sprintf "        for (size_t j = 0; j < %s_extents[1]; j++) {" name
            sprintf "            cout << \"  %s[\" << i << \"][\" << j << \"] = [\";" name
            sprintf "            bool %s = true;" firstVar
            sprintf "            for (size_t k = 0; k < %s_extents[2]; k++) {" name
            sprintf "                for (size_t l = 0; l < %s_extents[3]; l++) {" name
            sprintf "                    if (!%s) cout << \", \";" firstVar
            sprintf "                    %s = false;" firstVar
            sprintf "                    cout << %s[i][j][k][l];" name
            "                }"
            "            }"
            sprintf "            cout << \"]\" << endl;"
            "        }"
            "    }"
        ]
    else
        // Rank 5+: just print total size and first few elements
        [
            sprintf "    cout << \"%s = <rank-%d array>\" << endl;" name rank
        ]

/// Generate print loop for arrays with per-dimension symmetry awareness.
/// Expands IRIndexType list into per-dimension loop structure:
///   - SymIdx<k,n>: k dims, first is free range, rest subtract prior vars in group
///   - Idx<n>: 1 dim, free range
/// This correctly handles mixed symmetry (e.g. SymIdx<2> + Idx).
let genPrintArraySymAware (name: string) (indexTypes: IRIndexType list) : string list =
    // Expand index types into per-dimension info: (loopVar, dimIdx, offsetVars)
    // offsetVars = list of loop vars to subtract from extent (empty for free dims)
    let loopVarNames = [| "i"; "j"; "k"; "l"; "m"; "n_"; "p"; "q" |]
    let dims =
        indexTypes |> List.fold (fun (acc, dimIdx) idx ->
            let arity = max 1 idx.Arity
            let isSym = idx.Symmetry = SymSymmetric || idx.Symmetry = SymAntisymmetric
            let groupDims =
                [0 .. arity - 1] |> List.map (fun a ->
                    let loopVar = if dimIdx + a < loopVarNames.Length then loopVarNames.[dimIdx + a] else sprintf "d%d" (dimIdx + a)
                    let offsets =
                        if isSym && a > 0 then
                            [0 .. a - 1] |> List.map (fun prev -> loopVarNames.[dimIdx + prev])
                        else []
                    (loopVar, dimIdx + a, offsets))
            (acc @ groupDims, dimIdx + arity)
        ) ([], 0) |> fst
    let rank = dims.Length
    if rank < 1 || rank > 8 then
        [sprintf "    cout << \"%s = <rank-%d array>\" << endl;" name rank]
    else
        let firstVar = sprintf "%s__first" name
        let opens = [
            sprintf "    cout << \"%s = [\";" name
            sprintf "    bool %s = true;" firstVar ]
        let loops =
            dims |> List.map (fun (loopVar, dimIdx, offsets) ->
                let indent = "    " + String.replicate (dims |> List.findIndex (fun (v,_,_) -> v = loopVar)) "    "
                let bound =
                    match offsets with
                    | [] -> sprintf "%s_extents[%d]" name dimIdx
                    | _ ->
                        let sub = offsets |> String.concat " - "
                        sprintf "%s_extents[%d] - %s" name dimIdx sub
                sprintf "%sfor (size_t %s = 0; %s < %s; %s++) {" indent loopVar loopVar bound loopVar)
        let innerIndent = "    " + String.replicate rank "    "
        let idx = dims |> List.map (fun (v,_,_) -> sprintf "[%s]" v) |> String.concat ""
        let inner = [
            sprintf "%sif (!%s) cout << \", \";" innerIndent firstVar
            sprintf "%s%s = false;" innerIndent firstVar
            sprintf "%scout << %s%s;" innerIndent name idx ]
        let closes =
            [for d in rank - 1 .. -1 .. 0 ->
                sprintf "    %s}" (String.replicate d "    ")]
        let finish = [sprintf "    cout << \"]\" << endl;"]
        opens @ loops @ inner @ closes @ finish

/// Compute which binding IDs are deferred (no C++ code generated).
/// A binding is deferred if it's a computation that hasn't been materialized via |> compute.
let computeDeferredIds (bindings: IRBinding list) : Set<int> =
    let isDeferred (ids: Set<int>) (e: IRExpr) =
        match e with
        | IRApplyCombinator _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRZip _ | IRSequence _ -> true
        | IRVar (id, _) -> Set.contains id ids
        | _ -> false
    bindings |> List.fold (fun ids b ->
        let shouldDefer =
            match b.Value with
            | IRApplyCombinator _ | IRParallel _ | IRFusion _ -> true
            | IRZip _ -> true
            | IRComposeObj _ -> true
            | IRBind (comp, _) -> isDeferred ids comp
            | IRComposeMeth (left, right) -> isDeferred ids left || isDeferred ids right
            | IRFunctorMap (_, inner) -> isDeferred ids inner
            | IRChoice (left, right) -> isDeferred ids left || isDeferred ids right
            | IRGuard (_, body) ->
                let rec leafIsDeferred e =
                    match e with
                    | IRGuard (_, inner) -> leafIsDeferred inner
                    | _ -> isDeferred ids e
                leafIsDeferred body
            | IRSequence elems -> elems |> List.exists (isDeferred ids)
            | IRTuple elems -> elems |> List.forall (isDeferred ids)
            | IRTupleProj (IRVar (pid, _), _, _) -> Set.contains pid ids
            | IRVar (srcId, _) -> Set.contains srcId ids
            | _ -> false
        if shouldDefer then Set.add b.Id ids else ids
    ) Set.empty

let genPrintStatements (modul: IRModule) : string list =
    let deferredIds = computeDeferredIds modul.Bindings
    modul.Bindings |> List.collect (fun b ->
        let isPrintable = 
            if Set.contains b.Id deferredIds then false
            else
            match b.Value with
            | IRCompute (IRApplyCombinator _) -> true
            | IRCompute (IRParallel _) -> true
            | IRCompute (IRFusion _) -> true
            | IRCompute (IRVar _) -> true
            | IRCompute (IRFunctorMap _) -> true
            | IRCompute (IRChoice _) -> true
            | IRCompute (IRComposeMeth _) -> true
            | IRCompute (IRBind _) -> true
            | IRCompute (IRGuard _) -> true
            | IRCompute (IRSequence _) -> true
            | IRCompute _ | IRMethodFor _ | IRObjectFor _ | IRLambda _ -> false
            | _ -> true
        
        let hasSymmetry =
            match IR.stripUnits b.Type with
            | IRTArray arr ->
                arr.IndexTypes |> List.exists (fun idx ->
                    idx.Symmetry = SymSymmetric || idx.Symmetry = SymAntisymmetric)
            | _ -> false
        
        if isPrintable then
            match IR.stripUnits b.Type with
            | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool | ETIndexRef _) ->
                genPrintScalar b.Name
            | IRTArray arrType ->
                let rank = arrayRank arrType
                if hasSymmetry && rank >= 2 && rank <= 8 then
                    genPrintArraySymAware b.Name arrType.IndexTypes
                else
                    genPrintArrayFlat b.Name rank
            | IRTTuple _ -> []
            | IRTNamed _ -> []
            | IRTUnit -> []
            | _ -> []
        else []
    )

/// Assemble the main() function wrapper around binding code and print statements.
let genMainWrapper (testName: string) (bodyIndented: string list) (printCode: string list) : string list =
    ["int main() {"
     "    cout << std::setprecision(15);"
     "    cout << std::boolalpha;"
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

/// Generate a C++ program (uses external runtime header)
/// Generate print statements for all bindings in a module.
/// Shared by genSelfContainedProgram and genProgramWithExternalRuntime.
let genMainProgram (modul: IRModule) (testName: string) : string =
    exprWarnings.Value <- []
    let builder = IRBuilder()
    
    let includes = genIncludes ()
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let mainFunc = genMainWrapper testName bodyIndented []
    
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
let genSelfContainedProgram (modul: IRModule) (testName: string) : string =
    let builder = IRBuilder()
    
    let includes = genIncludesExternal ()
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let printCode = genPrintStatements modul
    let mainFunc = genMainWrapper testName bodyIndented printCode
    
    (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"

/// Generate a C++ program with external runtime header
/// Returns (mainFileContent, headerFileContent)
let genProgramWithExternalRuntime (modul: IRModule) (testName: string) : string * string =
    let builder = IRBuilder()
    
    let includes = genIncludesExternal ()
    let typeDefs = genTypeDefs modul
    let (funcDefs, bindCode) = genModule modul builder
    
    let bodyIndented = bindCode |> List.map (fun s -> "    " + s)
    let printCode = genPrintStatements modul
    let mainFunc = genMainWrapper testName bodyIndented printCode
    
    let mainFile = (includes @ typeDefs @ [""] @ funcDefs @ mainFunc) |> String.concat "\n"
    let headerFile = genRuntimeHeader ()
    (mainFile, headerFile)

/// Generate a self-contained C++ program from an IR program
let genSelfContainedProgramFromIR (program: IRProgram) (testName: string) : string * string list =
    // Reset module-level expression warnings
    exprWarnings.Value <- []
    let code =
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
    (code, exprWarnings.Value)
