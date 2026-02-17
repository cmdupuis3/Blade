// Blade-DSL Lowering Pass
// Transforms AST to IR with proper support for:
// - Array identity tracking (for triangular iteration)
// - Lambda capture analysis
// - Pattern binding
// - Arity polymorphism
// - Kernel irank/orank inference

module Blade.Lowering

open System
open Blade.Ast
open Blade.IR

// ============================================================================
// Lowering Context
// ============================================================================

/// Information about a bound variable
type VarInfo = {
    Id: IRId
    Identity: ArrayIdentity option
    IsMutable: bool
    Type: IRType option
    Value: IRExpr option
}

/// What a module exports to importers
type ModuleExport = {
    Variables: Map<string, VarInfo>
    Functions: Map<string, IRId>
    Types: Map<string, IRType>
    StructDefs: Map<string, (string * IRType) list>
    UnitDefs: Map<string, IR.UnitSig>
    StaticValues: Map<string, StaticEval.StaticValue>
    StaticFunctions: Map<string, StaticEval.StaticFuncDef>
}

/// Tracks how static functions are used: compile-time, runtime, or both.
/// Useful for IDE diagnostics (e.g. "this static function is only ever called at runtime").
[<Flags>]
type StaticUsage =
    | Unused       = 0
    | CompileTime  = 1   // called with all-static args → evaluated at compile time
    | RunTime      = 2   // called with runtime args → emitted as normal function call

type LoweringEnv = {
    Variables: Map<string, VarInfo>
    Types: Map<string, IRType>
    Functions: Map<string, IRId>
    Providers: Map<string, string list>  // alias -> qualified name (e.g. "NetCDF" -> ["Providers";"NetCDF"])
    Builder: IRBuilder
    OuterScope: Map<string, VarInfo>
    InPolyContext: bool
    PolyParamNames: string list  // Names of Poly<> parameters in current context
    CurrentCommGroups: int list list
    Interfaces: Map<string, InterfaceDecl>           // interface name -> decl
    ImplMethods: Map<string * string, IRId>           // (typeName, methodName) -> funcId
    StructDefs: Map<string, (string * IRType) list>   // structName -> fields
    StaticValues: Map<string, StaticEval.StaticValue>  // name -> compile-time value
    StaticFunctions: Map<string, StaticEval.StaticFuncDef>  // name -> static function def
    StaticUsageTracker: ref<Map<string, StaticUsage>>  // accumulated usage per static function
    UnitDefs: Map<string, IR.UnitSig>  // unit name -> canonical signature
    ModuleExports: Map<string, ModuleExport>  // moduleName -> exports from already-lowered modules
    ImportedModules: Map<string, string>  // alias -> full module name (for qualified name resolution)
}

let emptyEnv () = {
    Variables = Map.empty
    Types = Map.empty
    Functions = Map.empty
    Providers = Map.empty
    Builder = IRBuilder()
    OuterScope = Map.empty
    InPolyContext = false
    PolyParamNames = []
    CurrentCommGroups = []
    Interfaces = Map.empty
    ImplMethods = Map.empty
    StructDefs = Map.empty
    StaticValues = Map.empty
    StaticFunctions = Map.empty
    StaticUsageTracker = ref Map.empty
    UnitDefs = Map.empty
    ModuleExports = Map.empty
    ImportedModules = Map.empty
}

/// Record a usage of a static function
let trackStaticUsage (env: LoweringEnv) (name: string) (usage: StaticUsage) =
    let current = 
        match Map.tryFind name env.StaticUsageTracker.Value with
        | Some u -> u
        | Option.None -> StaticUsage.Unused
    env.StaticUsageTracker.Value <- Map.add name (current ||| usage) env.StaticUsageTracker.Value

let bindVar name info (env: LoweringEnv) : LoweringEnv =
    { env with Variables = Map.add name info env.Variables }

let bindVarSimple name id env =
    bindVar name { Id = id; Identity = None; IsMutable = false; Type = None; Value = None } env

let bindVarWithIdentity name id identity env =
    bindVar name { Id = id; Identity = Some identity; IsMutable = false; Type = None; Value = None } env

let bindVarWithType name id identity ty env =
    bindVar name { Id = id; Identity = identity; IsMutable = false; Type = Some ty; Value = None } env

let bindVarWithValue name id identity ty value env =
    bindVar name { Id = id; Identity = identity; IsMutable = false; Type = ty; Value = Some value } env

let lookupVar name env =
    Map.tryFind name env.Variables

let lookupVarById id env =
    env.Variables |> Map.tryPick (fun _ info -> 
        if info.Id = id then Some info else None)

/// Resolve an IR expression to a lambda if possible (follows variable references)
let resolveToLambda env (expr: IRExpr) : IRExpr =
    match expr with
    | IRLambda _ -> expr
    | IRVar id ->
        match lookupVarById id env with
        | Some info ->
            match info.Value with
            | Some (IRLambda _ as lambda) -> lambda
            | Some other -> other
            | None -> expr
        | None -> expr
    | _ -> expr

/// Resolve an IR expression to a method_for if possible (follows variable references)
let resolveToMethodFor env (expr: IRExpr) : IRExpr =
    match expr with
    | IRMethodFor _ -> expr
    | IRVar id ->
        match lookupVarById id env with
        | Some info ->
            match info.Value with
            | Some (IRMethodFor _ as mf) -> mf
            | Some other -> other
            | None -> expr
        | None -> expr
    | _ -> expr

let bindType name ty (env: LoweringEnv) =
    { env with Types = Map.add name ty env.Types }

let lookupType name env =
    Map.tryFind name env.Types

let enterScope env =
    { env with 
        OuterScope = Map.fold (fun acc k v -> Map.add k v acc) env.OuterScope env.Variables
        Variables = Map.empty }

let isCaptured name env =
    not (Map.containsKey name env.Variables) && Map.containsKey name env.OuterScope

// ============================================================================
// Capture Analysis
// ============================================================================

let rec collectFreeVars env (expr: Expr) : Set<string> =
    match expr with
    | ExprVar name ->
        if Map.containsKey name env.Variables then Set.empty
        else Set.singleton name
    | ExprBinOp (_, _, left, right) ->
        Set.union (collectFreeVars env left) (collectFreeVars env right)
    | ExprUnaryOp (_, operand) ->
        collectFreeVars env operand
    | ExprApp (func, args) ->
        let funcFree = collectFreeVars env func
        let argsFree = args |> List.map (collectFreeVars env) |> Set.unionMany
        Set.union funcFree argsFree
    | ExprLambda (parms, _, body) ->
        let env' = parms |> List.fold (fun e p -> 
            bindVarSimple p.Name (e.Builder.FreshId()) e) env
        collectFreeVars env' body
    | ExprLet (binding, body) ->
        let valueFree = collectFreeVars env binding.Value
        let env' = 
            match binding.Pattern with
            | PatVar name -> bindVarSimple name (env.Builder.FreshId()) env
            | _ -> env
        let bodyFree = collectFreeVars env' body
        Set.union valueFree bodyFree
    | ExprIf (cond, thenBr, elseBr) ->
        Set.unionMany [collectFreeVars env cond; collectFreeVars env thenBr; collectFreeVars env elseBr]
    | ExprTuple exprs ->
        exprs |> List.map (collectFreeVars env) |> Set.unionMany
    | ExprTupleIndex (tuple, index) ->
        Set.union (collectFreeVars env tuple) (collectFreeVars env index)
    | ExprMatch (scrutinee, cases) ->
        let scrutFree = collectFreeVars env scrutinee
        let casesFree = cases |> List.map (fun c -> collectFreeVars env c.Body) |> Set.unionMany
        Set.union scrutFree casesFree
    | ExprMethodFor arrays ->
        arrays |> List.map (collectFreeVars env) |> Set.unionMany
    | ExprObjectFor kernel ->
        collectFreeVars env kernel
    | ExprRank e ->
        collectFreeVars env e
    | _ -> Set.empty

let buildCaptures env (freeVars: Set<string>) : CaptureInfo list =
    freeVars |> Set.toList |> List.choose (fun name ->
        match Map.tryFind name env.OuterScope with
        | Some info -> Some { Id = info.Id; Name = name; IsMutable = info.IsMutable }
        | None -> None)

// ============================================================================
// Pattern Binding
// ============================================================================

let rec patternBindings (pat: Pattern) : string list =
    match pat with
    | PatWildcard -> []
    | PatVar name -> [name]
    | PatLit _ -> []
    | PatTuple pats -> pats |> List.collect patternBindings
    | PatCons (head, tail) -> patternBindings head @ patternBindings tail
    | PatStruct (_, fields) -> fields |> List.collect (fun (_, p) -> patternBindings p)
    | PatVariant (_, Some p) -> patternBindings p
    | PatVariant (_, None) -> []
    | PatGuarded (p, _) -> patternBindings p
    | PatTyped (p, _) -> patternBindings p

let rec inferTypeFromExpr (expr: IRExpr) : IRType option =
    match expr with
    | IRArrayLit (_, arrTy) -> Some (IRTArray arrTy)
    | IRLit (IRLitInt _) -> Some (IRTScalar ETInt64)
    | IRLit (IRLitFloat _) -> Some (IRTScalar ETFloat64)
    | IRLit (IRLitBool _) -> Some (IRTScalar ETBool)
    | IRLit IRLitUnit -> Some IRTUnit
    | IRStructLit (typeName, _) -> Some (IRTNamed typeName)  // Struct literal has named type
    | IRBinOp (_, op, left, right) ->
        // For comparison/logical ops, result is bool
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> 
            Some (IRTScalar ETBool)
        | _ ->
            // For arithmetic, try left first, then right, default to Float64
            match inferTypeFromExpr left with
            | Some t -> Some t
            | None -> 
                match inferTypeFromExpr right with
                | Some t -> Some t
                | None -> Some (IRTScalar ETFloat64)  // Default for arithmetic
    | IRUnaryOp (op, operand) ->
        match op with
        | IRNot -> Some (IRTScalar ETBool)
        | IRNeg -> inferTypeFromExpr operand
    | IRIf (_, thenBr, _) ->
        inferTypeFromExpr thenBr
    | IRCompute inner ->
        // Compute unwraps to the inner type
        inferTypeFromExpr inner
    | IRApplyCombinator info ->
        // Apply combinator has OutputType already computed
        Some info.OutputType
    | IRMethodFor info -> 
        Some (IRTLoop { Kind = LKMethod; Arity = Some info.Arrays.Length; 
                        ArrayTypes = info.ArrayTypes |> List.map IRTArray; KernelType = None })
    | IRObjectFor info ->
        Some (IRTLoop { Kind = LKObject; Arity = Some info.InputRanks.Length;
                        ArrayTypes = []; KernelType = Some (IRTUnit) })
    | IRLambda info ->
        let argTypes = info.Params |> List.map (fun p -> p.Type)
        let retType = inferTypeFromExpr info.Body |> Option.defaultValue (IRTScalar ETFloat64)
        Some (IRTFunc (argTypes, retType))
    | IRTuple exprs ->
        let elemTypes = exprs |> List.choose inferTypeFromExpr
        if elemTypes.Length = exprs.Length then Some (IRTTuple elemTypes)
        else None
    | IRTupleProj (e, i) ->
        match inferTypeFromExpr e with
        | Some (IRTTuple ts) when i < ts.Length -> Some ts.[i]
        | _ -> None
    | IRMatch (scrutinee, cases) ->
        // Match expression type is the type of the first case body
        // If the body is just a variable (pattern binding), use scrutinee type
        match cases with
        | [] -> None
        | case :: _ -> 
            match inferTypeFromExpr case.Body with
            | Some t -> Some t
            | None -> 
                // If body type unknown (e.g., just a pattern var), try scrutinee type
                inferTypeFromExpr scrutinee
    | IRFieldAccess (obj, fieldName) ->
        // For field access, we'd need to know the struct type
        // For now, just return None and let it be inferred elsewhere
        None
    | IRVar _ -> None  // Would need type environment to resolve
    | IRApp (IRObjectFor objInfo, args) ->
        // For object_for application, compute output array type
        // OutputRank determines the result dimensions
        let rank = objInfo.OutputRank
        let idx = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt 0L); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        let indexTypes = List.replicate rank idx
        Some (IRTArray { ElemType = ETFloat64; IndexTypes = indexTypes; IsVirtual = false; Identity = None })
    | IRApp (func, _) ->
        // For function application, infer from function return type
        match inferTypeFromExpr func with
        | Some (IRTFunc (_, ret)) -> Some ret
        | _ -> None
    | _ -> None

/// Infer type from expression, with environment lookup for variables
let rec inferTypeFromExprWithEnv (env: LoweringEnv) (expr: IRExpr) : IRType option =
    match expr with
    | IRVar id ->
        // Look up variable type from environment
        match lookupVarById id env with
        | Some info -> info.Type
        | None -> None
    | IRMatch (scrutinee, cases) ->
        // Match expression type - try case body, then scrutinee, using env
        match cases with
        | [] -> None
        | case :: _ -> 
            match inferTypeFromExprWithEnv env case.Body with
            | Some t -> Some t
            | None -> inferTypeFromExprWithEnv env scrutinee
    | IRBinOp (_, op, left, right) ->
        // For arithmetic, try to infer from operands with env
        match op with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr -> 
            Some (IRTScalar ETBool)
        | _ ->
            match inferTypeFromExprWithEnv env left with
            | Some t -> Some t
            | None -> 
                match inferTypeFromExprWithEnv env right with
                | Some t -> Some t
                | None -> Some (IRTScalar ETFloat64)
    | IRFieldAccess (obj, fieldName) ->
        // Resolve field type by looking up object's struct type
        match inferTypeFromExprWithEnv env obj with
        | Some (IRTNamed typeName) ->
            match Map.tryFind typeName env.StructDefs with
            | Some fields ->
                fields |> List.tryFind (fun (n, _) -> n = fieldName) |> Option.map snd
            | None -> None
        | _ -> None
    | IRApp (func, _) ->
        // Resolve function return type via env
        match inferTypeFromExprWithEnv env func with
        | Some (IRTFunc (_, ret)) -> Some ret
        | _ -> inferTypeFromExpr expr
    | _ -> inferTypeFromExpr expr

let rec extendEnvWithPatternBindings env (pat: Pattern) (value: IRExpr) : LoweringEnv * (string * IRId) list =
    match pat with
    | PatWildcard -> env, []
    | PatVar name ->
        let id = env.Builder.FreshId()
        let identity = AIDVariable name
        let inferredType = inferTypeFromExpr value
        let env' = bindVarWithValue name id (Some identity) inferredType value env
        env', [(name, id)]
    | PatTuple pats ->
        let mutable env' = env
        let mutable allBindings = []
        for i, p in List.indexed pats do
            let (newEnv, bindings) = extendEnvWithPatternBindings env' p (IRTupleProj(value, i))
            env' <- newEnv
            allBindings <- allBindings @ bindings
        env', allBindings
    | PatCons (headPat, tailPat) ->
        let decons = IRTupleDecons value
        let (env1, headBinds) = extendEnvWithPatternBindings env headPat (IRTupleProj(decons, 0))
        let (env2, tailBinds) = extendEnvWithPatternBindings env1 tailPat (IRTupleProj(decons, 1))
        env2, headBinds @ tailBinds
    | PatStruct (_, fields) ->
        let mutable env' = env
        let mutable allBindings = []
        for i, (_, p) in List.indexed fields do
            let (newEnv, bindings) = extendEnvWithPatternBindings env' p (IRTupleProj(value, i))
            env' <- newEnv
            allBindings <- allBindings @ bindings
        env', allBindings
    | PatVariant (_, Some innerPat) ->
        extendEnvWithPatternBindings env innerPat (IRTupleProj(value, 0))
    | PatVariant (_, None) ->
        env, []
    | PatTyped (p, _) ->
        extendEnvWithPatternBindings env p value
    | PatGuarded (p, _) ->
        extendEnvWithPatternBindings env p value
    | PatLit _ ->
        env, []

// ============================================================================
// Array Type Inference
// ============================================================================

let inferElemType (exprs: IRExpr list) : ElemType =
    exprs 
    |> List.tryPick (function
        | IRLit (IRLitInt _) -> Some ETInt64
        | IRLit (IRLitFloat _) -> Some ETFloat64
        | IRLit (IRLitBool _) -> Some ETBool
        | IRArrayLit (_, arrTy) -> Some arrTy.ElemType
        | _ -> None)
    |> Option.defaultValue ETFloat64

let inferArrayLitShape (exprs: IRExpr list) : int list =
    let mutable shape = []
    let mutable current = exprs
    let mutable cont = true
    while cont do
        match current with
        | [] -> 
            shape <- shape @ [0]
            cont <- false
        | IRArrayLit (inner, _) :: _ ->
            shape <- shape @ [List.length current]
            current <- inner
        | _ ->
            shape <- shape @ [List.length current]
            cont <- false
    shape

let inferArrayLitType (exprs: IRExpr list) : IRArrayType =
    let elemType = inferElemType exprs
    let shape = inferArrayLitShape exprs
    let indexTypes = 
        shape |> List.mapi (fun i extent ->
            { Id = i; Arity = 1; Extent = IRLit (IRLitInt (int64 extent))
              Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] })
    { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

/// Get array type from environment lookup - always succeeds with proper inference
let getArrayType env (arr: Expr) : IRArrayType =
    match arr with
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | Some (IRTArray arrTy) -> arrTy
            | _ ->
                // Infer from stored value if available
                match info.Value with
                | Some (IRArrayLit (elems, ty)) -> ty
                | Some (IRMethodFor mfInfo) when mfInfo.ArrayTypes.Length > 0 ->
                    mfInfo.ArrayTypes.[0]
                | _ ->
                    // Default: rank-1 array with unknown extent
                    { ElemType = ETFloat64
                      IndexTypes = [{ Id = 0; Arity = 1; Extent = IRParam(name + "_n", 0)
                                      Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }]
                      IsVirtual = false; Identity = Some (AIDVariable name) }
        | None ->
            // Unknown variable - create placeholder type
            { ElemType = ETFloat64
              IndexTypes = [{ Id = 0; Arity = 1; Extent = IRParam(name + "_n", 0)
                              Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }]
              IsVirtual = false; Identity = Some (AIDVariable name) }
    | ExprArrayLit elems ->
        let loweredElems = elems |> List.map (fun _ -> IRLit (IRLitFloat 0.0))
        inferArrayLitType loweredElems
    | ExprRange ty | ExprReverse ty ->
        // Build index type inline (lowerIndexType not yet available here)
        let idx = match ty with
                  | TyIdx extent ->
                      let ext = match extent with
                                | ExprLit (LitInt n) -> IRLit (IRLitInt (int64 n))
                                | ExprVar name -> IRParam (name, 0)
                                | _ -> IRLit (IRLitInt 0L)
                      { Id = 0; Arity = 1; Extent = ext; Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
                  | _ -> { Id = 0; Arity = 1; Extent = IRLit (IRLitInt 0L); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        { ElemType = ETInt64; IndexTypes = [idx]; IsVirtual = true; Identity = None }
    | ExprBlocked (ty, _) ->
        let idx = match ty with
                  | TyIdx extent ->
                      let ext = match extent with
                                | ExprLit (LitInt n) -> IRLit (IRLitInt (int64 n))
                                | ExprVar name -> IRParam (name, 0)
                                | _ -> IRLit (IRLitInt 0L)
                      { Id = 0; Arity = 1; Extent = ext; Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
                  | _ -> { Id = 0; Arity = 1; Extent = IRLit (IRLitInt 0L); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        { ElemType = ETInt64; IndexTypes = [idx]; IsVirtual = true; Identity = None }
    | _ ->
        // Expression result - infer rank 0 (scalar result)
        { ElemType = ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

// ============================================================================
// Type Lowering
// ============================================================================

/// Extract the type name from a TypeExpr (for impl dispatch)
let extractTypeName (ty: TypeExpr) : string option =
    match ty with
    | TyNamed (name, _) -> Some name
    | _ -> None

/// Try to determine the named type of an expression from the environment
let tryResolveObjTypeName (env: LoweringEnv) (expr: Expr) : string option =
    match expr with
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | Some (IRTNamed typeName) -> Some typeName
            | _ -> None
        | None -> None
    | _ -> None

/// Convert a compile-time static value to an IR expression
let rec staticValueToIR (v: StaticEval.StaticValue) : IRExpr =
    match v with
    | StaticEval.SVInt n -> IRLit (IRLitInt n)
    | StaticEval.SVFloat f -> IRLit (IRLitFloat f)
    | StaticEval.SVBool b -> IRLit (IRLitBool b)
    | StaticEval.SVString _ -> IRLit IRLitUnit  // strings not in IR literals yet
    | StaticEval.SVUnit -> IRLit IRLitUnit
    | StaticEval.SVTuple vs -> IRTuple (vs |> List.map staticValueToIR)

/// Resolve a UnitExpr AST node to a canonical UnitSig using the unit environment
let rec resolveUnitExpr (units: Map<string, IR.UnitSig>) (expr: UnitExpr) : Result<IR.UnitSig, string> =
    match expr with
    | UnitNamed name ->
        match Map.tryFind name units with
        | Some sig' -> Ok sig'
        | None -> Error (sprintf "Unknown unit '%s'" name)
    | UnitMul (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            IR.unitMul sa sb))
    | UnitDiv (a, b) ->
        resolveUnitExpr units a |> Result.bind (fun sa ->
        resolveUnitExpr units b |> Result.map (fun sb ->
            IR.unitDiv sa sb))
    | UnitPow (a, n) ->
        resolveUnitExpr units a |> Result.map (fun sa ->
            IR.unitPow sa n)

let rec lowerTypeExpr env (ty: TypeExpr) : IRType =
    match ty with
    | TyInt32 -> IRTScalar ETInt32
    | TyInt64 -> IRTScalar ETInt64
    | TyFloat32 -> IRTScalar ETFloat32
    | TyFloat64 -> IRTScalar ETFloat64
    | TyComplex64 -> IRTScalar ETComplex64
    | TyComplex128 -> IRTScalar ETComplex128
    | TyBool -> IRTScalar ETBool
    | TyUnit -> IRTUnit
    | TyString -> IRTScalar ETString
    | TyChar -> IRTScalar ETInt32
    
    | TyNamed (name, args) ->
        // Helper: try to resolve a type arg as a unit annotation
        let tryResolveUnitArg baseType =
            match args with
            | [TyNamed (unitName, [])] ->
                match Map.tryFind unitName env.UnitDefs with
                | Some unitSig -> IRTUnitAnnotated (baseType, unitSig)
                | None -> baseType
            | _ -> baseType
        match name with
        | "Int" | "Int32" -> tryResolveUnitArg (IRTScalar ETInt32)
        | "Int64" -> tryResolveUnitArg (IRTScalar ETInt64)
        | "Float" | "Float64" | "Double" -> tryResolveUnitArg (IRTScalar ETFloat64)
        | "Float32" -> tryResolveUnitArg (IRTScalar ETFloat32)
        | "Complex64" -> tryResolveUnitArg (IRTScalar ETComplex64)
        | "Complex128" | "Complex" -> tryResolveUnitArg (IRTScalar ETComplex128)
        | "Bool" -> IRTScalar ETBool
        | "String" -> IRTScalar ETString
        | "T" -> IRTScalar ETFloat64
        | "Poly" ->
            match args with
            | [inner] -> IRTPoly(lowerTypeExpr env inner, "r")
            | _ -> IRTUnit
        | _ ->
            match lookupType name env with
            | Some t -> t
            | None -> IRTUnit
    
    | TyArray (elemTy, indexTys) ->
        let elem = lowerElemType env elemTy
        let indices = indexTys |> List.mapi (fun i ity -> lowerIndexType env i ity)
        IRTArray { ElemType = elem; IndexTypes = indices; IsVirtual = false; Identity = None }
    
    | TyAbstractArray (elemTy, rankExpr, symmOpt) ->
        let elem = lowerElemType env elemTy
        let symmetry = 
            match symmOpt with
            | Some vec when vec.Length > 0 && vec |> List.forall (fun x -> x = vec.[0]) -> SymSymmetric
            | _ -> SymNone
        // Evaluate rank expression - must be static
        // arity is static within a kernel scope (bound at call site)
        let rank = 
            match rankExpr with
            | ExprLit (LitInt n) -> int n
            | ExprArity _ -> 
                // At definition time, arity is unknown - use placeholder
                // At instantiation time, this will be replaced with actual value
                0  // Placeholder - will be recomputed at instantiation
            | ExprVar name ->
                match lookupVar name env with
                | Some { Value = Some (IRLit (IRLitInt n)) } -> int n
                | _ -> failwithf "Rank expression '%s' must be a compile-time constant" name
            | _ -> failwith "Rank expression must be a compile-time constant (integer, arity, or const variable)"
        let indices = 
            if rank > 0 then
                [0..rank-1] |> List.map (fun i ->
                    { Id = i; Arity = 1; Extent = IRLit (IRLitInt 0L)
                      Symmetry = symmetry; Tag = None; Kind = SDimension; Dependencies = [] })
            else []
        IRTArray { ElemType = elem; IndexTypes = indices; IsVirtual = false; Identity = None }
    
    | TyFunc (args, ret) ->
        IRTFunc (args |> List.map (lowerTypeExpr env), lowerTypeExpr env ret)
    
    | TyTuple tys ->
        IRTTuple (tys |> List.map (lowerTypeExpr env))
    
    | TyVar (_, _) -> IRTScalar ETFloat64  // Type vars default to Float64 in direct lowering path
    
    | TyIdx extent ->
        let extExpr = lowerExpr env extent
        let idx = { Id = 0; Arity = 1; Extent = extExpr; Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
    | TySymIdx (arity, extent) ->
        let extExpr = lowerExpr env extent
        let idx = { Id = 0; Arity = arity; Extent = extExpr; Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
    | TyAntisymIdx (arity, extent) ->
        let extExpr = lowerExpr env extent
        let idx = { Id = 0; Arity = arity; Extent = extExpr; Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
    | TyBoundedIdx (lower, upper) ->
        let upperExpr = lowerExpr env upper
        let idx = { Id = 0; Arity = 1; Extent = upperExpr; Symmetry = SymNone; Tag = Some "bounded"; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
    | TyHermitianIdx extent ->
        let extExpr = lowerExpr env extent
        let idx = { Id = 0; Arity = 2; Extent = extExpr; Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETComplex128; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
    | TyPoly inner ->
        IRTPoly(lowerTypeExpr env inner, "r")
    
    | _ -> IRTUnit

and lowerElemType env ty =
    match lowerTypeExpr env ty with
    | IRTScalar et -> et
    | IRTUnitAnnotated (IRTScalar et, _) -> et
    | _ -> ETFloat64

and lowerIndexType env id ty : IRIndexType =
    match ty with
    | TyIdx extent ->
        { Id = id; Arity = 1; Extent = lowerExpr env extent; Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
    | TySymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExpr env extent; Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyAntisymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExpr env extent; Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyHermitianIdx extent ->
        { Id = id; Arity = 2; Extent = lowerExpr env extent; Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyNamed (name, args) ->
        let extent = 
            match args with
            | [TyNamed (n, [])] -> IRParam (n, 0)
            | [TyVar (n, _)] -> IRParam (n, 0)
            | _ -> IRLit (IRLitInt 0L)
        { Id = id; Arity = 1; Extent = extent; Symmetry = SymNone; Tag = Some name; Kind = SDimension; Dependencies = [] }
    | _ ->
        { Id = id; Arity = 1; Extent = IRLit (IRLitInt 0L); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }

// ============================================================================
// Expression Lowering
// ============================================================================

and lowerExpr env (expr: Expr) : IRExpr =
    match expr with
    | ExprLit lit -> lowerLiteral lit
    | ExprVar name ->
        // Check static values first — compile-time constants become IR literals
        match Map.tryFind name env.StaticValues with
        | Some sv -> staticValueToIR sv
        | None ->
        match lookupVar name env with
        | Some info -> IRVar info.Id
        | None -> 
            match Map.tryFind name env.OuterScope with
            | Some info -> IRVar info.Id
            | None -> IRParam (name, 0)
    | ExprQualified names ->
        // Note: the parser produces ExprField for dot-access (A.B), not ExprQualified.
        // This path exists only as a safety net; module resolution goes through ExprField.
        IRParam (String.concat "." names, 0)
    | ExprBinOp (mode, op, left, right) ->
        let l = lowerExpr env left
        let r = lowerExpr env right
        lowerBinOp env mode op l r left right
    | ExprUnaryOp (op, operand) ->
        let e = lowerExpr env operand
        match op with
        | OpNeg -> IRUnaryOp (IRNeg, e)
        | OpNot -> IRUnaryOp (IRNot, e)
    | ExprApp (ExprVar fname, args) when Map.containsKey fname env.StaticFunctions ->
        // Try compile-time evaluation of static function call
        let staticEnv = { StaticEval.Values = env.StaticValues
                          StaticEval.Functions = env.StaticFunctions
                          StaticEval.CalledFunctions = ref Set.empty }
        match StaticEval.evalExpr staticEnv StaticEval.maxSteps expr with
        | Ok sv ->
            trackStaticUsage env fname StaticUsage.CompileTime
            staticValueToIR sv
        | Error _ ->
            // Args not all static — fall through to runtime call
            trackStaticUsage env fname StaticUsage.RunTime
            let f = lowerExpr env (ExprVar fname)
            let as' = args |> List.map (lowerExpr env)
            IRApp (f, as')
    | ExprApp (ExprField (obj, method), args) ->
        // Check if this is an impl method call: obj.method(args) → TypeName__method(obj, args)
        match tryResolveObjTypeName env obj with
        | Some typeName ->
            match Map.tryFind (typeName, method) env.ImplMethods with
            | Some funcId ->
                let o = lowerExpr env obj
                let as' = args |> List.map (lowerExpr env)
                IRApp (IRVar funcId, o :: as')
            | None ->
                // Not an impl method — fall through to field access + app
                let f = lowerExpr env (ExprField (obj, method))
                let as' = args |> List.map (lowerExpr env)
                IRApp (f, as')
        | None ->
            let f = lowerExpr env (ExprField (obj, method))
            let as' = args |> List.map (lowerExpr env)
            IRApp (f, as')
    | ExprApp (func, args) ->
        let f = lowerExpr env func
        let as' = args |> List.map (lowerExpr env)
        // Check if applying to an array - if so, this is array indexing
        let isArray = 
            match func with
            | ExprVar name ->
                match lookupVar name env with
                | Some info -> 
                    match info.Type with
                    | Some (IRTArray _) -> true
                    | _ -> false
                | None -> false
            | _ -> false
        if isArray then
            let identity = 
                match func with
                | ExprVar name -> 
                    match lookupVar name env with
                    | Some info -> info.Identity
                    | None -> None
                | _ -> None
            IRIndex (f, as', identity)
        else
            IRApp (f, as')
    | ExprTupleIndex (tuple, index) ->
        // Poly-tuple indexing: args[k]
        let tup = lowerExpr env tuple
        let idx = lowerExpr env index
        // Use dynamic poly-pack indexing
        IRPolyIndex (tup, idx)
    | ExprField (ExprVar modAlias, name) when Map.containsKey modAlias env.ImportedModules ->
        // Module-qualified access: e.g. Math.pi, MathLib.double
        let fullModName = env.ImportedModules.[modAlias]
        match Map.tryFind fullModName env.ModuleExports with
        | Some exports ->
            match Map.tryFind name exports.Variables with
            | Some varInfo -> IRVar varInfo.Id
            | None ->
                match Map.tryFind name exports.Functions with
                | Some funcId -> IRVar funcId
                | None ->
                    match Map.tryFind name exports.StaticValues with
                    | Some sv -> staticValueToIR sv
                    | None ->
                        eprintfn "Warning: '%s.%s' not found in module exports" modAlias name
                        IRParam (sprintf "%s.%s" modAlias name, 0)
        | None ->
            IRParam (sprintf "%s.%s" modAlias name, 0)
    | ExprField (obj, field) ->
        let o = lowerExpr env obj
        IRFieldAccess (o, field)
    | ExprLambda (parms, whereClause, body) ->
        lowerLambda env parms whereClause body
    | ExprLet (binding, body) ->
        lowerLetBinding env binding body
    | ExprMatch (scrutinee, cases) ->
        let scrut = lowerExpr env scrutinee
        let cases' = cases |> List.map (lowerMatchCase env)
        IRMatch (scrut, cases')
    | ExprIf (cond, thenBr, elseBr) ->
        IRIf (lowerExpr env cond, lowerExpr env thenBr, lowerExpr env elseBr)
    | ExprTuple exprs ->
        IRTuple (exprs |> List.map (lowerExpr env))
    | ExprArrayLit exprs ->
        let es = exprs |> List.map (lowerExpr env)
        let ty = inferArrayLitType es
        IRArrayLit (es, ty)
    | ExprBlock (stmts, exprOpt) ->
        lowerBlock env stmts exprOpt
    | ExprMethodFor arrays ->
        lowerMethodFor env arrays
    | ExprObjectFor kernel ->
        lowerObjectFor env kernel
    | ExprRange ty ->
        let idx = lowerIndexType env 0 ty
        IRRange idx
    | ExprReverse ty ->
        let idx = lowerIndexType env 0 ty
        IRVirtualReverse idx
    | ExprBlocked (ty, size) ->
        let idx = lowerIndexType env 0 ty
        IRBlocked (idx, lowerExpr env size)
    | ExprZip exprs ->
        IRZip (exprs |> List.map (lowerExpr env))
    | ExprAlign (exprs, specOpt) ->
        let spec = 
            match specOpt with
            | Some s -> { Offsets = s.Offsets; Boundary = lowerBoundaryMode s.Boundary }
            | None -> { Offsets = []; Boundary = BndShrink }
        IRAlign (exprs |> List.map (lowerExpr env), spec)
    | ExprStack exprs ->
        IRStack (exprs |> List.map (lowerExpr env))
    | ExprPure e ->
        IRPure (lowerExpr env e)
    | ExprCompute e ->
        IRCompute (lowerExpr env e)
    | ExprReynolds (kernel, isAntisym) ->
        IRReynolds (lowerExpr env kernel, isAntisym)
    | ExprGuard (cond, body) ->
        IRGuard (lowerExpr env cond, lowerExpr env body)
    | ExprSequence exprs ->
        IRSequence (exprs |> List.map (lowerExpr env))
    | ExprReplicate (count, body) ->
        IRReplicate (lowerExpr env count, lowerExpr env body)
    | ExprTyped (e, _) ->
        lowerExpr env e
    | ExprArity paramName -> 
        // arity(param) is only valid for Poly<> parameters
        if env.InPolyContext then 
            if List.contains paramName env.PolyParamNames then
                IRArity (None, paramName)
            else
                failwith (sprintf "arity(%s): '%s' is not a Poly<> parameter" paramName paramName)
        else 
            failwith "arity() is only valid inside functions with Poly<> parameters"
    | ExprNth -> IRNth
    | ExprZero -> IRZero
    | ExprRank e -> IRRank (lowerExpr env e)
    | ExprStruct (name, fields) ->
        // Generate struct literal with field names
        IRStructLit (name, fields |> List.map (fun (fname, e) -> fname, lowerExpr env e))
    | ExprSection op ->
        lowerSection env op
    | ExprPartialApp (op, arg, isLeft) ->
        lowerPartialApp env op arg isLeft
    | ExprAssign (lhs, rhs) ->
        match lhs with
        | ExprVar name ->
            match lookupVar name env with
            | Some info -> IRAssign (info.Id, lowerExpr env rhs)
            | None -> lowerExpr env rhs
        | _ -> lowerExpr env rhs
    | ExprFor (source, constraints, kernelOpt) ->
        lowerFor env source constraints kernelOpt

and lowerBoundaryMode mode =
    match mode with
    | Ast.BndShrink -> IR.BndShrink
    | Ast.BndPad e -> IR.BndPad (IRLit (IRLitInt 0L))
    | Ast.BndPeriodic -> IR.BndPeriodic
    | Ast.BndReflect -> IR.BndReflect

/// Lower a lambda with proper capture analysis, where clause, and type extraction for irank
and lowerLambda env parms (whereClause: WhereClause option) body =
    let scopeEnv = enterScope env
    
    // Extract commutativity groups from where clause
    let commGroups : int list list = 
        match whereClause with
        | Some wc -> 
            wc.Commutativity |> List.map (fun names ->
                names |> List.choose (fun name ->
                    parms |> List.tryFindIndex (fun p -> p.Name = name)))
        | None -> []
    
    let mutable paramEnv = scopeEnv
    let paramInfos = parms |> List.mapi (fun i p ->
        let id = env.Builder.FreshId()
        // Use explicit type if provided, otherwise create inference placeholder
        let ty = match p.Type with 
                 | Some t -> lowerTypeExpr env t 
                 | None -> env.Builder.FreshInferType()
        paramEnv <- bindVarSimple p.Name id paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = id })
    
    let freeVars = collectFreeVars paramEnv body
    let captures = buildCaptures scopeEnv freeVars
    
    // Validate: lambdas cannot capture arrays or virtual arrays
    for capture in captures do
        match Map.tryFind capture.Name env.OuterScope with
        | Some info ->
            match info.Type with
            | Some (IRTArray _) -> 
                failwith (sprintf "Lambda cannot capture array '%s'. Arrays must be passed through method_for/object_for." capture.Name)
            | _ -> ()
        | None -> 
            // Also check current scope variables
            match Map.tryFind capture.Name env.Variables with
            | Some info ->
                match info.Type with
                | Some (IRTArray _) ->
                    failwith (sprintf "Lambda cannot capture array '%s'. Arrays must be passed through method_for/object_for." capture.Name)
                | _ -> ()
            | None -> ()
    
    let isComm = not (List.isEmpty commGroups)
    let body' = lowerExpr paramEnv body
    
    IRLambda {
        Params = paramInfos
        Body = body'
        Captures = captures
        IsCommutative = isComm
        CommGroups = commGroups
    }

/// Lower let binding with full pattern support
and lowerLetBinding env binding body =
    let value = lowerExpr env binding.Value
    let id = env.Builder.FreshId()
    let (env', bindings) = extendEnvWithPatternBindings env binding.Pattern value
    
    match binding.Pattern with
    | PatVar name ->
        let body' = lowerExpr env' body
        IRLet (id, value, body')
    | PatTuple pats ->
        let rec buildNestedLets env bindings body =
            match bindings with
            | [] -> lowerExpr env body
            | (name, boundId) :: rest ->
                let idx = pats |> List.tryFindIndex (fun p -> 
                    match p with PatVar n -> n = name | _ -> false)
                match idx with
                | Some i ->
                    let proj = IRTupleProj(value, i)
                    IRLet(boundId, proj, buildNestedLets env rest body)
                | None -> buildNestedLets env rest body
        buildNestedLets env' bindings body
    | PatCons (PatVar headName, PatVar tailName) ->
        let headId = bindings |> List.tryFind (fun (n, _) -> n = headName) |> Option.map snd |> Option.defaultValue (env.Builder.FreshId())
        let tailId = bindings |> List.tryFind (fun (n, _) -> n = tailName) |> Option.map snd |> Option.defaultValue (env.Builder.FreshId())
        let body' = lowerExpr env' body
        IRLet(headId, IRTupleProj(IRTupleDecons value, 0),
            IRLet(tailId, IRTupleProj(IRTupleDecons value, 1), body'))
    | _ ->
        // General pattern: bind all extracted names
        let rec buildNestedLets bindings bodyExpr =
            match bindings with
            | [] -> bodyExpr
            | (_, boundId) :: rest ->
                IRLet(boundId, IRTupleProj(value, List.length bindings - List.length rest - 1), 
                      buildNestedLets rest bodyExpr)
        let body' = lowerExpr env' body
        if bindings.IsEmpty then IRLet (id, value, body')
        else buildNestedLets bindings body'

/// Lower method_for with identity and type tracking
and lowerMethodFor env arrays =
    let arrs = arrays |> List.map (lowerExpr env)
    let identities = arrays |> List.map (fun arr ->
        match arr with
        | ExprVar name -> AIDVariable name
        | _ -> AIDLiteral (env.Builder.FreshId()))
    let arrayTypes = arrays |> List.map (getArrayType env)
    let sDimsPerArray = computeSDimsPerArray arrayTypes
    let totalSDims = List.sum sDimsPerArray
    
    IRMethodFor {
        Arrays = arrs
        Identities = identities
        ArrayTypes = arrayTypes
        SDimsPerArray = sDimsPerArray
        TotalSDims = totalSDims
    }

/// Lower object_for with commutativity and irank/orank tracking
and lowerObjectFor env kernel =
    let k = lowerExpr env kernel
    let inputRanks = extractKernelInputRanks k
    let outputRank = extractKernelOutputRank k
    
    IRObjectFor {
        Kernel = k
        CommGroups = env.CurrentCommGroups
        InputRanks = inputRanks
        OutputRank = outputRank
    }

/// Lower binary operations with special handling for combinators
and lowerBinOp env mode op l r leftExpr rightExpr =
    // Convert AST mode to IR mode
    let irMode = match mode with Elementwise -> IRElementwise | Outer -> IROuter
    
    // Helper to check if an IR expression represents an array
    let isArrayExpr (expr: IRExpr) =
        match expr with
        | IRArrayLit _ -> true
        | IRVar id ->
            // Look up in environment to check type
            env.Variables |> Map.exists (fun _ info -> 
                info.Id = id && 
                match info.Type with 
                | Some (IRTArray _) -> true 
                | _ -> false)
        | _ -> false
    
    // Helper to desugar outer mode (cross-iteration) to object_for
    let desugarOuter irOp isCommutative =
        let xParam = { Name = "_x"; Type = IRTUnit; Index = 0; VarId = env.Builder.FreshId() }
        let yParam = { Name = "_y"; Type = IRTUnit; Index = 1; VarId = env.Builder.FreshId() }
        let kernelBody = IRBinOp (IRElementwise, irOp, IRVar xParam.VarId, IRVar yParam.VarId)
        let kernel = IRLambda {
            Params = [xParam; yParam]
            Body = kernelBody
            Captures = []
            IsCommutative = isCommutative
            CommGroups = if isCommutative then [[0; 1]] else []
        }
        let objFor = IRObjectFor {
            Kernel = kernel
            CommGroups = if isCommutative then [[0; 1]] else []
            InputRanks = [1; 1]  // Cross-iteration: each array contributes 1 loop level
            OutputRank = 2       // Result is 2D (outer product)
        }
        IRApp (objFor, [IRTuple [l; r]])
    
    // Helper to desugar elementwise array ops (co-iteration) to object_for
    let desugarElementwise irOp isCommutative =
        let xParam = { Name = "_x"; Type = IRTUnit; Index = 0; VarId = env.Builder.FreshId() }
        let yParam = { Name = "_y"; Type = IRTUnit; Index = 1; VarId = env.Builder.FreshId() }
        let kernelBody = IRBinOp (IRElementwise, irOp, IRVar xParam.VarId, IRVar yParam.VarId)
        let kernel = IRLambda {
            Params = [xParam; yParam]
            Body = kernelBody
            Captures = []
            IsCommutative = isCommutative
            CommGroups = if isCommutative then [[0; 1]] else []
        }
        let objFor = IRObjectFor {
            Kernel = kernel
            CommGroups = if isCommutative then [[0; 1]] else []
            InputRanks = [0; 0]  // Co-iteration: kernel receives scalars from same position
            OutputRank = 1       // Result preserves rank (elementwise)
        }
        IRApp (objFor, [IRTuple [l; r]])
    
    // Check if both operands are arrays for elementwise mode
    let bothArrays = isArrayExpr l && isArrayExpr r
    
    // Helper to generate the right form based on mode and operand types
    let genBinOp irOp isCommutative =
        if mode = Outer then 
            desugarOuter irOp isCommutative
        else if bothArrays then
            desugarElementwise irOp isCommutative
        else 
            IRBinOp (irMode, irOp, l, r)
    
    match op with
    // Arithmetic and comparison ops
    | OpAdd -> genBinOp IRAdd true
    | OpSub -> genBinOp IRSub false
    | OpMul -> genBinOp IRMul true
    | OpDiv -> genBinOp IRDiv false
    | OpMod -> genBinOp IRMod false
    | OpCaret -> genBinOp IRCaret false
    | OpEq -> genBinOp IREq true
    | OpNeq -> genBinOp IRNeq true
    | OpLt -> genBinOp IRLt false
    | OpLe -> genBinOp IRLe false
    | OpGt -> genBinOp IRGt false
    | OpGe -> genBinOp IRGe false
    | OpAnd -> genBinOp IRAnd true
    | OpOr -> genBinOp IROr true
    
    | OpApply ->
        // Resolve loop to get method_for info (handles variable references)
        let resolvedLeft = resolveToMethodFor env l
        
        // Check if this is an object_for application (kernel-first, arrays-right).
        // If so, normalize to method_for path: build synthetic MethodFor from arrays,
        // pull kernel from the ObjectFor. This gives both paths the same codegen.
        let (resolvedLoop, resolvedKernel, identities, arrayTypes, sDimsPerArray) =
            match resolvedLeft with
            | IRObjectFor objInfo ->
                // Arrays come from the right side
                let (ids, aTypes, sDims) = extractArrayInfoFromTuple env rightExpr
                // Extract lowered array IR expressions from right side
                let arrayExprs =
                    match r with
                    | IRTuple elems -> elems
                    | _ -> [r]  // single array
                // Build a synthetic MethodFor
                let totalSDims = List.sum sDims
                let mfInfo : MethodForInfo = {
                    Arrays = arrayExprs
                    Identities = ids
                    ArrayTypes = aTypes
                    SDimsPerArray = sDims
                    TotalSDims = totalSDims
                }
                // Kernel comes from the object_for, not from right side
                let kernel = resolveToLambda env objInfo.Kernel
                (IRMethodFor mfInfo, kernel, ids, aTypes, sDims)
            | IRMethodFor mfInfo ->
                // Normal method_for path: kernel comes from right side
                let kernel = resolveToLambda env r
                (resolvedLeft, kernel, mfInfo.Identities, mfInfo.ArrayTypes, mfInfo.SDimsPerArray)
            | _ ->
                // Fallback: try to extract from AST
                let (ids, aTypes, sDims) = extractLoopInfo env leftExpr
                let (ids, aTypes, sDims) =
                    if ids.IsEmpty then extractArrayInfoFromTuple env rightExpr
                    else (ids, aTypes, sDims)
                let kernel = resolveToLambda env r
                (resolvedLeft, kernel, ids, aTypes, sDims)
        
        // Extract kernel info for irank computation
        let kernelInputRanks = 
            match resolvedKernel with
            | IRLambda info -> info.Params |> List.map (fun p ->
                match p.Type with
                | IRTArray arr -> arr.IndexTypes.Length
                | _ -> 0)
            | _ -> extractKernelInputRanks resolvedKernel
        
        let kernelOutputRank = extractKernelOutputRank resolvedKernel
        
        // Recompute S-dims accounting for kernel input ranks
        let sDimsPerArray = 
            if kernelInputRanks.Length = arrayTypes.Length then
                computeSDimsWithKernel arrayTypes kernelInputRanks
            else sDimsPerArray
        
        // Check if kernel is wrapped in IRReynolds
        let (innerKernel, hasReynolds, isAntisym) =
            match resolvedKernel with
            | IRReynolds (k, antisym) -> (k, true, antisym)
            | _ -> (resolvedKernel, false, false)
        
        // Resolve inner kernel if it's a variable reference
        let resolvedInnerKernel = resolveToLambda env innerKernel
        
        // Get commutativity groups from the inner kernel
        let commGroups = 
            match resolvedInnerKernel with
            | IRLambda info -> info.CommGroups
            | _ -> env.CurrentCommGroups
        
        let states = computeAllSymcomStates identities arrayTypes commGroups sDimsPerArray
        let triangularLevels = computeTriangularLevels arrayTypes identities commGroups sDimsPerArray
        let identitySpeedup = computePartialProductSpeedup arrayTypes identities commGroups sDimsPerArray
        
        // Reynolds speedup: n! for n parameters
        let reynoldsSpeedup = 
            if hasReynolds then
                match resolvedInnerKernel with
                | IRLambda info -> factorial info.Params.Length
                | _ -> 1L
            else 1L
        
        // Total speedup: identity speedup × Reynolds speedup
        // When both apply, they multiply: (n!)² for n identical arrays with Reynolds
        let totalSpeedup = identitySpeedup * reynoldsSpeedup
        
        // Deduce output type from arrays, identities, comm groups
        let outputType = deduceOutputType arrayTypes identities commGroups sDimsPerArray kernelOutputRank env.Builder
        
        IRApplyCombinator {
            Loop = resolvedLoop  // Always IRMethodFor after normalization
            Kernel = if hasReynolds then IRReynolds(resolvedInnerKernel, isAntisym) else resolvedKernel
            SymcomStates = states
            TriangularLevels = triangularLevels
            SDimsPerArray = sDimsPerArray
            KernelInputRanks = kernelInputRanks
            KernelOutputRank = kernelOutputRank
            SpeedupFactor = totalSpeedup
            ReynoldsSpeedup = reynoldsSpeedup
            HasReynolds = hasReynolds
            OutputType = outputType
        }
    
    | OpBind -> IRBind (l, r)
    | OpParallel -> IRParallel (l, r, None)
    | OpFusion -> IRFusion (l, r)
    | OpArrayProd -> IRArrayProduct (l, r)
    | OpFunctor -> IRFunctorMap (l, r)
    | OpChoice -> IRChoice (l, r)
    | OpFallback -> IRChoice (l, r)
    | OpComposeObj -> IRComposeObj (l, r)
    | OpComposeMeth -> IRComposeMeth (l, r)
    | OpCompose -> IRCompose (l, r)
    | OpCons -> IRTupleCons (l, r)

/// Extract array info from a tuple expression (for object_for application)
and extractArrayInfoFromTuple env expr : ArrayIdentity list * IRArrayType list * int list =
    match expr with
    | ExprTuple arrays ->
        let identities = arrays |> List.map (fun arr ->
            match arr with
            | ExprVar name -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = arrays |> List.map (getArrayType env)
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        (identities, arrayTypes, sDimsPerArray)
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Value with
            | Some (IRTuple elems) -> ([], [], [])
            | _ -> ([], [], [])
        | None -> ([], [], [])
    | _ -> ([], [], [])

/// Extract full loop info from a loop expression
and extractLoopInfo env expr : ArrayIdentity list * IRArrayType list * int list =
    match expr with
    | ExprMethodFor arrays ->
        let identities = arrays |> List.map (fun arr ->
            match arr with
            | ExprVar name -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = arrays |> List.map (getArrayType env)
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        (identities, arrayTypes, sDimsPerArray)
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Value with
            | Some (IRMethodFor mfInfo) ->
                (mfInfo.Identities, mfInfo.ArrayTypes, mfInfo.SDimsPerArray)
            | _ -> ([], [], [])
        | None -> ([], [], [])
    | _ -> ([], [], [])


/// Lower sectioned operator to lambda
and lowerSection env op =
    let aId = env.Builder.FreshId()
    let bId = env.Builder.FreshId()
    let (irOp, isComm) = 
        match op with
        | OpAdd -> (IRAdd, true)
        | OpSub -> (IRSub, false)
        | OpMul -> (IRMul, true)
        | OpDiv -> (IRDiv, false)
        | OpMod -> (IRMod, false)
        | OpCaret -> (IRCaret, false)
        | OpEq -> (IREq, true)
        | OpNeq -> (IRNeq, true)
        | OpLt -> (IRLt, false)
        | OpLe -> (IRLe, false)
        | OpGt -> (IRGt, false)
        | OpGe -> (IRGe, false)
        | OpAnd -> (IRAnd, true)
        | OpOr -> (IROr, true)
        | _ -> (IRAdd, true)  // Default fallback
    let body = IRBinOp(IRElementwise, irOp, IRVar aId, IRVar bId)
    IRLambda {
        Params = [{ Name = "a"; Type = IRTScalar ETFloat64; Index = 0; VarId = aId }
                  { Name = "b"; Type = IRTScalar ETFloat64; Index = 1; VarId = bId }]
        Body = body
        Captures = []
        IsCommutative = isComm
        CommGroups = if isComm then [[0; 1]] else []
    }

/// Lower partial application
and lowerPartialApp env op arg isLeft =
    let argExpr = lowerExpr env arg
    let paramId = env.Builder.FreshId()
    let irOp = match op with
               | OpAdd -> IRAdd | OpSub -> IRSub
               | OpMul -> IRMul | OpDiv -> IRDiv
               | OpMod -> IRMod | OpCaret -> IRCaret
               | OpEq -> IREq | OpNeq -> IRNeq
               | OpLt -> IRLt | OpLe -> IRLe
               | OpGt -> IRGt | OpGe -> IRGe
               | OpAnd -> IRAnd | OpOr -> IROr
               | _ -> IRAdd
    let body =
        if isLeft then IRBinOp (IRElementwise, irOp, argExpr, IRVar paramId)
        else IRBinOp (IRElementwise, irOp, IRVar paramId, argExpr)
    IRLambda {
        Params = [{ Name = "x"; Type = IRTScalar ETFloat64; Index = 0; VarId = paramId }]
        Body = body
        Captures = []
        IsCommutative = false
        CommGroups = []
    }

/// Lower for expression
and lowerFor env source constraints kernelOpt =
    match source with
    | ForArrays (arrays, idxTypeOpt) ->
        let methodFor = lowerMethodFor env arrays
        match kernelOpt with
        | Some k -> 
            let kernel = lowerExpr env k
            let mfInfo = match methodFor with IRMethodFor info -> info | _ -> failwith "Expected IRMethodFor"
            
            // Check if kernel is wrapped in IRReynolds
            let (innerKernel, hasReynolds, _isAntisym) =
                match kernel with
                | IRReynolds (k, antisym) -> (k, true, antisym)
                | _ -> (kernel, false, false)
            
            let kernelInputRanks = extractKernelInputRanks innerKernel
            let kernelOutputRank = extractKernelOutputRank innerKernel
            let sDimsPerArray = 
                if kernelInputRanks.Length = mfInfo.ArrayTypes.Length then
                    computeSDimsWithKernel mfInfo.ArrayTypes kernelInputRanks
                else mfInfo.SDimsPerArray
            let commGroups = match innerKernel with IRLambda info -> info.CommGroups | _ -> []
            let states = computeAllSymcomStates mfInfo.Identities mfInfo.ArrayTypes commGroups sDimsPerArray
            let triangularLevels = computeTriangularLevels mfInfo.ArrayTypes mfInfo.Identities commGroups sDimsPerArray
            let identitySpeedup = computePartialProductSpeedup mfInfo.ArrayTypes mfInfo.Identities commGroups sDimsPerArray
            
            // Reynolds speedup: n! for n parameters
            let reynoldsSpeedup = 
                if hasReynolds then
                    match innerKernel with
                    | IRLambda info -> factorial info.Params.Length
                    | _ -> 1L
                else 1L
            
            // Deduce output type
            let outputType = deduceOutputType mfInfo.ArrayTypes mfInfo.Identities commGroups sDimsPerArray kernelOutputRank env.Builder
            
            IRApplyCombinator {
                Loop = methodFor
                Kernel = kernel
                SymcomStates = states
                TriangularLevels = triangularLevels
                SDimsPerArray = sDimsPerArray
                KernelInputRanks = kernelInputRanks
                KernelOutputRank = kernelOutputRank
                SpeedupFactor = identitySpeedup * reynoldsSpeedup
                ReynoldsSpeedup = reynoldsSpeedup
                HasReynolds = hasReynolds
                OutputType = outputType
            }
        | None -> methodFor
    | ForKernel kernel ->
        lowerObjectFor env kernel

and lowerLiteral lit =
    match lit with
    | LitInt n -> IRLit (IRLitInt n)
    | LitFloat f -> IRLit (IRLitFloat f)
    | LitBool b -> IRLit (IRLitBool b)
    | LitString _ -> IRLit (IRLitInt 0L)
    | LitChar c -> IRLit (IRLitInt (int64 c))
    | LitUnit -> IRLit IRLitUnit

and lowerMatchCase env (case: MatchCase) : IRMatchCase =
    // First, extend environment with pattern bindings
    let (pat, env') = lowerPatternWithEnv env case.Pattern
    let guard = case.Guard |> Option.map (lowerExpr env')
    let body = lowerExpr env' case.Body
    { Pattern = pat; Guard = guard; Body = body }

and lowerPatternWithEnv env (pat: Pattern) : IRPattern * LoweringEnv =
    match pat with
    | PatWildcard -> IRPatWild, env
    | PatVar name ->
        let id = env.Builder.FreshId()
        let env' = bindVarSimple name id env
        IRPatVar id, env'
    | PatLit lit ->
        let irLit = 
            match lit with
            | LitInt n -> IRLitInt n
            | LitFloat f -> IRLitFloat f
            | LitBool b -> IRLitBool b
            | _ -> IRLitUnit
        IRPatLit irLit, env
    | PatTuple pats ->
        let mutable env' = env
        let irPats = pats |> List.map (fun p ->
            let (irP, newEnv) = lowerPatternWithEnv env' p
            env' <- newEnv
            irP)
        IRPatTuple irPats, env'
    | PatCons (head, tail) ->
        let (irHead, env1) = lowerPatternWithEnv env head
        let (irTail, env2) = lowerPatternWithEnv env1 tail
        IRPatCons (irHead, irTail), env2
    | PatVariant (name, innerOpt) ->
        match innerOpt with
        | Some inner ->
            let (irInner, env') = lowerPatternWithEnv env inner
            IRPatVariant (name, 0, Some irInner), env'
        | None ->
            IRPatVariant (name, 0, None), env
    | _ -> IRPatWild, env

and lowerPattern env (pat: Pattern) : IRPattern =
    fst (lowerPatternWithEnv env pat)

and lowerBlock env stmts exprOpt =
    match stmts, exprOpt with
    | [], Some e -> lowerExpr env e
    | [], None -> IRLit IRLitUnit
    | stmt :: rest, _ ->
        match stmt with
        | StmtLet binding ->
            let value = lowerExpr env binding.Value
            let id = env.Builder.FreshId()
            let env2 = 
                match binding.Pattern with
                | PatVar name -> 
                    let identity = AIDVariable name
                    let ty = inferTypeFromExpr value
                    bindVarWithValue name id (Some identity) ty value env
                | _ -> env
            let body = lowerBlock env2 rest exprOpt
            IRLet (id, value, body)
        | StmtAssign (lhs, _, rhs) ->
            match lhs with
            | ExprVar name ->
                match lookupVar name env with
                | Some info ->
                    let value = lowerExpr env rhs
                    let rest2 = lowerBlock env rest exprOpt
                    IRLet(info.Id, value, rest2)
                | None -> lowerBlock env rest exprOpt
            | _ -> lowerBlock env rest exprOpt
        | StmtExpr e ->
            let _ = lowerExpr env e
            lowerBlock env rest exprOpt

// ============================================================================
// Declaration Lowering
// ============================================================================

let lowerFuncDecl env (decl: FunctionDecl) : IRFuncDef =
    let id = env.Builder.FreshId()
    
    // Collect all parameters with Poly type
    let polyParamNames = decl.Params |> List.choose (fun p ->
        match p.Type with
        | Some (TyPoly _) -> Some p.Name
        | _ -> None)
    
    let isArityPoly = not polyParamNames.IsEmpty
    
    // Extract commutativity from where clause
    let commGroups = 
        match decl.WhereClause with
        | Some wc -> 
            wc.Commutativity |> List.map (fun names ->
                names |> List.mapi (fun i name ->
                    decl.Params |> List.tryFindIndex (fun p -> p.Name = name)
                    |> Option.defaultValue i))
        | None -> []
    
    let parallelism =
        match decl.WhereClause with
        | Some wc -> 
            wc.Parallelism |> List.choose (fun (name, level) ->
                decl.Params |> List.tryFindIndex (fun p -> p.Name = name)
                |> Option.map (fun idx -> (idx, level)))
        | None -> []
    
    // Build parameter environment with proper types for irank inference
    // Set InPolyContext and PolyParamNames if this function has Poly parameters
    let mutable paramEnv = { env with CurrentCommGroups = commGroups; InPolyContext = isArityPoly; PolyParamNames = polyParamNames }
    let params2 = decl.Params |> List.mapi (fun i p ->
        let paramId = env.Builder.FreshId()
        // Use explicit type if provided, otherwise create inference placeholder
        let ty = match p.Type with 
                 | Some t -> lowerTypeExpr env t 
                 | None -> env.Builder.FreshInferType()
        let identity = AIDParameter (p.Name, i)
        paramEnv <- bindVarWithType p.Name paramId (Some identity) ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = paramId })
    
    let body2 = lowerExpr paramEnv decl.Body
    
    // Infer return type from body if not explicitly specified
    let retTy = 
        match decl.ReturnType with
        | Some t -> lowerTypeExpr paramEnv t
        | None -> 
            inferTypeFromExpr body2 |> Option.defaultValue (IRTScalar ETFloat64)
    
    {
        Id = id
        Name = decl.Name
        Params = params2
        RetType = retTy
        Body = body2
        IsStatic = decl.IsStatic
        Commutativity = if commGroups.IsEmpty then None else Some commGroups
        Parallelism = parallelism
        IsArityPoly = isArityPoly
        ArityParam = polyParamNames |> List.tryHead
    }

/// Lower a binding, potentially producing multiple IR bindings for pattern destructuring
/// Returns (bindings, environment updates as (name, id, type, value) list)
let lowerBindingWithEnv env (binding: Binding) : IRBinding list * (string * IRId * IRType * IRExpr) list =
    let value = lowerExpr env binding.Value
    let declaredTy = binding.Type |> Option.map (lowerTypeExpr env)
    
    match binding.Pattern with
    | PatVar name ->
        let id = env.Builder.FreshId()
        let ty = declaredTy |> Option.defaultWith (fun () -> 
            inferTypeFromExprWithEnv env value |> Option.defaultValue IRTUnit)
        let bd = {
            Id = id
            Name = name
            Type = ty
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = (binding.Mutability <> BindConst)
        }
        [bd], [(name, id, ty, value)]
    
    | PatTuple pats ->
        // First, create a binding for the whole tuple
        let tupleId = env.Builder.FreshId()
        let tupleTy = declaredTy |> Option.defaultWith (fun () ->
            inferTypeFromExprWithEnv env value |> Option.defaultValue IRTUnit)
        let tupleBd = {
            Id = tupleId
            Name = sprintf "_tuple_%d" tupleId
            Type = tupleTy
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = (binding.Mutability <> BindConst)
        }
        
        // Then create bindings for each element
        let rec extractPatBindings (pat: Pattern) (proj: IRExpr) (projTy: IRType option) : IRBinding list * (string * IRId * IRType * IRExpr) list =
            match pat with
            | PatVar name ->
                let id = env.Builder.FreshId()
                // Use projected type if available, otherwise infer from expression
                let ty = projTy |> Option.defaultWith (fun () ->
                    inferTypeFromExpr proj |> Option.defaultValue (IRTScalar ETFloat64))
                let bd = {
                    Id = id
                    Name = name
                    Type = ty
                    Value = proj
                    IsConst = binding.Mutability = BindConst
                    IsMutable = (binding.Mutability <> BindConst)
                }
                [bd], [(name, id, ty, proj)]
            | PatWildcard -> [], []
            | PatTuple innerPats ->
                let mutable bds = []
                let mutable envUpdates = []
                for i, p in List.indexed innerPats do
                    // Get element type from tuple type if available
                    let elemTy = 
                        match projTy with
                        | Some (IRTTuple ts) when i < ts.Length -> Some ts.[i]
                        | _ -> None
                    let (bs, us) = extractPatBindings p (IRTupleProj(proj, i)) elemTy
                    bds <- bds @ bs
                    envUpdates <- envUpdates @ us
                bds, envUpdates
            | _ -> [], []  // Other patterns not fully supported yet
        
        let mutable allBindings = [tupleBd]
        let mutable allEnvUpdates = []
        // Get element types from the tuple type
        let elemTypes = 
            match tupleTy with
            | IRTTuple ts -> ts
            | _ -> []
        for i, p in List.indexed pats do
            let proj = IRTupleProj(IRVar tupleId, i)
            let elemTy = if i < elemTypes.Length then Some elemTypes.[i] else None
            let (bs, us) = extractPatBindings p proj elemTy
            allBindings <- allBindings @ bs
            allEnvUpdates <- allEnvUpdates @ us
        
        allBindings, allEnvUpdates
    
    | PatCons (headPat, tailPat) ->
        // Cons pattern: (head :: tail) - treat as tuple destructure for lists/tuples
        // First, create a binding for the whole value
        let consId = env.Builder.FreshId()
        let consTy = declaredTy |> Option.defaultWith (fun () ->
            inferTypeFromExprWithEnv env value |> Option.defaultValue IRTUnit)
        let consBd = {
            Id = consId
            Name = sprintf "_cons_%d" consId
            Type = consTy
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = (binding.Mutability <> BindConst)
        }
        
        // Get element types from the tuple type
        let elemTypes = 
            match consTy with
            | IRTTuple ts -> ts
            | _ -> []
        
        // Helper to create bindings for a pattern
        let rec extractConsBindings (pat: Pattern) (proj: IRExpr) (projTy: IRType option) : IRBinding list * (string * IRId * IRType * IRExpr) list =
            match pat with
            | PatVar name ->
                let id = env.Builder.FreshId()
                let ty = projTy |> Option.defaultWith (fun () ->
                    inferTypeFromExpr proj |> Option.defaultValue (IRTScalar ETFloat64))
                let bd = {
                    Id = id
                    Name = name
                    Type = ty
                    Value = proj
                    IsConst = binding.Mutability = BindConst
                    IsMutable = (binding.Mutability <> BindConst)
                }
                [bd], [(name, id, ty, proj)]
            | PatWildcard -> [], []
            | _ -> [], []
        
        let mutable allBindings = [consBd]
        let mutable allEnvUpdates = []
        
        // Head is element 0
        let headProj = IRTupleProj(IRVar consId, 0)
        let headTy = if elemTypes.Length > 0 then Some elemTypes.[0] else None
        let (headBds, headUs) = extractConsBindings headPat headProj headTy
        allBindings <- allBindings @ headBds
        allEnvUpdates <- allEnvUpdates @ headUs
        
        // Tail is element 1
        let tailProj = IRTupleProj(IRVar consId, 1)
        let tailTy = if elemTypes.Length > 1 then Some elemTypes.[1] else None
        let (tailBds, tailUs) = extractConsBindings tailPat tailProj tailTy
        allBindings <- allBindings @ tailBds
        allEnvUpdates <- allEnvUpdates @ tailUs
        
        allBindings, allEnvUpdates
    
    | PatStruct (typeName, fields) ->
        // First, create a binding for the whole struct
        let structId = env.Builder.FreshId()
        let structTy = declaredTy |> Option.defaultWith (fun () ->
            inferTypeFromExprWithEnv env value |> Option.defaultValue IRTUnit)
        let structBd = {
            Id = structId
            Name = sprintf "_struct_%d" structId
            Type = structTy
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = (binding.Mutability <> BindConst)
        }
        
        // Then create bindings for each field
        // Field types would need struct type definitions - default to double for now
        let mutable allBindings = [structBd]
        let mutable allEnvUpdates = []
        for (fieldName, pat) in fields do
            match pat with
            | PatVar varName ->
                let id = env.Builder.FreshId()
                let ty = IRTScalar ETFloat64  // Default field type
                let proj = IRFieldAccess(IRVar structId, fieldName)
                let bd = {
                    Id = id
                    Name = varName
                    Type = ty
                    Value = proj
                    IsConst = binding.Mutability = BindConst
                    IsMutable = (binding.Mutability <> BindConst)
                }
                allBindings <- allBindings @ [bd]
                allEnvUpdates <- allEnvUpdates @ [(varName, id, ty, proj)]
            | PatWildcard -> ()
            | _ -> ()  // Nested patterns not fully supported
        
        allBindings, allEnvUpdates
    
    | _ ->
        // Fallback for other patterns
        let id = env.Builder.FreshId()
        let name = sprintf "binding_%d" id
        let ty = declaredTy |> Option.defaultWith (fun () ->
            inferTypeFromExprWithEnv env value |> Option.defaultValue (IRTScalar ETFloat64))
        let bd = {
            Id = id
            Name = name
            Type = ty
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = (binding.Mutability <> BindConst)
        }
        [bd], [(name, id, ty, value)]

/// Simple wrapper for backward compatibility
let lowerBinding env (binding: Binding) : IRBinding =
    let (bindings, _) = lowerBindingWithEnv env binding
    List.head bindings

/// Returns bindings list and environment updates for pattern destructuring
let lowerDeclWithEnv env (decl: Decl) : (Choice<IRFuncDef, IRBinding list, IRTypeDef> * (string * IRId * IRType * IRExpr) list) option =
    match decl with
    | DeclFunction fd when fd.IsStatic ->
        // Dual emit: static functions are available at compile time AND emitted as runtime functions
        Some (Choice1Of3 (lowerFuncDecl env fd), [])
    | DeclFunction fd ->
        Some (Choice1Of3 (lowerFuncDecl env fd), [])
    | DeclLet binding ->
        let (bindings, envUpdates) = lowerBindingWithEnv env binding
        Some (Choice2Of3 bindings, envUpdates)
    | DeclStatic binding ->
        // Static values are pre-evaluated — emit as constant bindings with literal values
        match binding.Pattern with
        | PatVar name ->
            match Map.tryFind name env.StaticValues with
            | Some sv ->
                let irValue = staticValueToIR sv
                let id = env.Builder.FreshId()
                let ty = match sv with
                         | StaticEval.SVInt _ -> IRTScalar ETInt64
                         | StaticEval.SVFloat _ -> IRTScalar ETFloat64
                         | StaticEval.SVBool _ -> IRTScalar ETBool
                         | _ -> IRTUnit
                let bd = { Id = id; Name = name; Type = ty; Value = irValue; IsConst = true; IsMutable = false }
                Some (Choice2Of3 [bd], [(name, id, ty, irValue)])
            | None ->
                // Fallback: lower as normal binding
                let (bindings, envUpdates) = lowerBindingWithEnv env binding
                Some (Choice2Of3 bindings, envUpdates)
        | _ ->
            let (bindings, envUpdates) = lowerBindingWithEnv env binding
            Some (Choice2Of3 bindings, envUpdates)
    | DeclType td ->
        match td with
        | TyDeclAlias (name, _, body) ->
            Some (Choice3Of3 (IRTDAlias (name, lowerTypeExpr env body)), [])
        | TyDeclStruct (name, _, fields, invariant) ->
            let fields2 = fields |> List.map (fun f -> f.Name, lowerTypeExpr env f.Type)
            let constraintInfo =
                match invariant with
                | Some constraintExpr ->
                    // Create a synthetic environment with field names bound to fresh IDs
                    let fieldBindings = fields |> List.map (fun f ->
                        let id = env.Builder.FreshId()
                        (f.Name, id))
                    let constraintEnv = 
                        fieldBindings |> List.fold (fun e (fname, fid) ->
                            bindVarSimple fname fid e) env
                    let loweredExpr = lowerExpr constraintEnv constraintExpr
                    Some { Expr = loweredExpr; FieldBindings = fieldBindings }
                | None -> None
            Some (Choice3Of3 (IRTDStruct (name, fields2, constraintInfo)), [])
        | TyDeclSum (name, _, variants) ->
            let variants2 = variants |> List.map (fun v -> 
                v.Name, v.Data |> Option.map (lowerTypeExpr env))
            Some (Choice3Of3 (IRTDVariant (name, variants2)), [])
    | DeclImport _ -> None  // Handled specially in lowerModule
    | DeclUnit _ -> None    // Handled specially in lowerModule (registers unit)
    | _ -> None

/// Check if a let binding is a provider load call: let x = Provider.load("path")
/// Returns Some (providerAlias, methodName, argString) if matched.
let tryMatchProviderCall (env: LoweringEnv) (binding: Binding) : (string * string * string * string) option =
    match binding.Pattern, binding.Value with
    | PatVar bindName, ExprApp (ExprField (ExprVar provAlias, methodName), [ExprLit (LitString arg)]) ->
        match Map.tryFind provAlias env.Providers with
        | Some _qname -> Some (bindName, provAlias, methodName, arg)
        | None -> None
    | _ -> None

/// Dispatch a provider load call.  Returns types and env updates to splice into the module.
let dispatchProviderLoad
    (env: LoweringEnv)
    (bindName: string)
    (provAlias: string)
    (methodName: string)
    (arg: string)
    : IRTypeDef list =

    match Map.tryFind provAlias env.Providers with
    | Some ["Providers"; "NetCDF"] when methodName = "load" ->
        let file = NetcdfProvider.load arg
        let modul = NetcdfProvider.ncFileToModule env.Builder bindName file None
        // Return the types from the provider module (index types + structs)
        modul.Types
    | Some qname ->
        failwithf "Unknown provider method: %s.%s (provider %s)" provAlias methodName (String.concat "." qname)
    | None ->
        failwithf "Unknown provider: %s" provAlias

let lowerModule env (modul: ModuleDecl) : IRModule * ModuleExport =
    // Phase 0: Resolve all static values and functions
    let mutable currentEnv = 
        match StaticEval.resolveStatics modul.Decls with
        | Ok staticEnv ->
            // Seed usage tracker: functions called during static evaluation are compile-time uses
            let tracker = ref Map.empty
            for fname in staticEnv.CalledFunctions.Value do
                tracker.Value <- Map.add fname StaticUsage.CompileTime tracker.Value
            { env with 
                StaticValues = staticEnv.Values
                StaticFunctions = staticEnv.Functions
                StaticUsageTracker = tracker }
        | Error msg ->
            eprintfn "Static evaluation error: %s" msg
            env
    let mutable funcs = []
    let mutable bindings = []
    let mutable types = []
    
    for locDecl in modul.Decls do
        match locDecl.Value with
        // Handle imports: resolve module or provider
        | DeclImport (qname, style) ->
            let fullName = String.concat "." qname
            
            match style with
            | ImportQualified aliasOpt ->
                let alias = aliasOpt |> Option.defaultValue (List.last qname)
                
                // Check if this is an already-lowered module
                match Map.tryFind fullName currentEnv.ModuleExports with
                | Some exports ->
                    // Register the module alias for qualified name resolution
                    currentEnv <- { currentEnv with ImportedModules = Map.add alias fullName currentEnv.ImportedModules }
                    
                    // Import all exported variables, functions, types, units, statics
                    // into the current env under qualified names (alias.name)
                    for kv in exports.Variables do
                        let qualName = sprintf "%s.%s" alias kv.Key
                        currentEnv <- { currentEnv with Variables = Map.add qualName kv.Value currentEnv.Variables }
                    for kv in exports.Functions do
                        currentEnv <- { currentEnv with Functions = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.Functions }
                    for kv in exports.Types do
                        currentEnv <- { currentEnv with Types = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.Types }
                    for kv in exports.StructDefs do
                        currentEnv <- { currentEnv with StructDefs = Map.add kv.Key kv.Value currentEnv.StructDefs }
                    for kv in exports.UnitDefs do
                        currentEnv <- { currentEnv with UnitDefs = Map.add kv.Key kv.Value currentEnv.UnitDefs }
                    for kv in exports.StaticValues do
                        currentEnv <- { currentEnv with StaticValues = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.StaticValues }
                    for kv in exports.StaticFunctions do
                        currentEnv <- { currentEnv with StaticFunctions = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.StaticFunctions }
                | None ->
                    // Fall back to provider import
                    currentEnv <- { currentEnv with Providers = Map.add alias qname currentEnv.Providers }
            
            | ImportSelective names ->
                // from Math import pi, e → bring specific names into scope unqualified
                match Map.tryFind fullName currentEnv.ModuleExports with
                | Some exports ->
                    for name in names do
                        // Import variable
                        match Map.tryFind name exports.Variables with
                        | Some varInfo ->
                            currentEnv <- { currentEnv with Variables = Map.add name varInfo currentEnv.Variables }
                        | None -> ()
                        // Import function
                        match Map.tryFind name exports.Functions with
                        | Some funcId ->
                            currentEnv <- { currentEnv with Functions = Map.add name funcId currentEnv.Functions }
                        | None -> ()
                        // Import type
                        match Map.tryFind name exports.Types with
                        | Some ty ->
                            currentEnv <- { currentEnv with Types = Map.add name ty currentEnv.Types }
                        | None -> ()
                        // Import static value
                        match Map.tryFind name exports.StaticValues with
                        | Some sv ->
                            currentEnv <- { currentEnv with StaticValues = Map.add name sv currentEnv.StaticValues }
                        | None -> ()
                        // Import static function
                        match Map.tryFind name exports.StaticFunctions with
                        | Some sf ->
                            currentEnv <- { currentEnv with StaticFunctions = Map.add name sf currentEnv.StaticFunctions }
                        | None -> ()
                        // Import struct def
                        match Map.tryFind name exports.StructDefs with
                        | Some fields ->
                            currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                        | None -> ()
                        // Import unit def
                        match Map.tryFind name exports.UnitDefs with
                        | Some unitSig ->
                            currentEnv <- { currentEnv with UnitDefs = Map.add name unitSig currentEnv.UnitDefs }
                        | None -> ()
                | None ->
                    eprintfn "Warning: module '%s' not found for selective import" fullName

        // Handle unit declarations: register unit in environment
        | DeclUnit unitDecl ->
            let sig' =
                match unitDecl.Definition with
                | None | Some UnitBase ->
                    Map.ofList [(unitDecl.Name, 1)]
                | Some (UnitDerived expr) ->
                    match resolveUnitExpr currentEnv.UnitDefs expr with
                    | Ok resolved -> resolved
                    | Error msg ->
                        eprintfn "Unit error: %s" msg
                        Map.ofList [(unitDecl.Name, 1)]
            currentEnv <- { currentEnv with UnitDefs = Map.add unitDecl.Name sig' currentEnv.UnitDefs }

        // Handle let bindings that are provider load calls
        | DeclLet binding when (tryMatchProviderCall currentEnv binding).IsSome ->
            let (bindName, provAlias, methodName, arg) = (tryMatchProviderCall currentEnv binding).Value
            let provTypes = dispatchProviderLoad currentEnv bindName provAlias methodName arg
            types <- types @ provTypes
            // Register named index types in the type environment
            for td in provTypes do
                match td with
                | IRTDIndexType (name, idx) ->
                    let idxType = IRTArray {
                        ElemType = ETInt64
                        IndexTypes = [idx]
                        IsVirtual = false
                        Identity = None
                    }
                    currentEnv <- bindType name idxType currentEnv
                | _ -> ()

        // Handle interface declarations: register in environment
        | DeclInterface ifaceDecl ->
            currentEnv <- { currentEnv with Interfaces = Map.add ifaceDecl.Name ifaceDecl currentEnv.Interfaces }

        // Handle impl blocks: monomorphize methods as top-level functions
        | DeclImpl implDecl ->
            match extractTypeName implDecl.ForType with
            | Some typeName ->
                for method in implDecl.Methods do
                    let mangledName = sprintf "%s__%s" typeName method.Name
                    // Ensure self has the concrete type
                    let typedParams = method.Params |> List.map (fun p ->
                        if p.Name = "self" && p.Type.IsNone then
                            { p with Type = Some implDecl.ForType }
                        else p)
                    let modifiedDecl = { method with Name = mangledName; Params = typedParams }
                    let fd = lowerFuncDecl currentEnv modifiedDecl
                    funcs <- funcs @ [fd]
                    let funcType = IRTFunc (fd.Params |> List.map (fun p -> p.Type), fd.RetType)
                    currentEnv <- { currentEnv with
                                        Functions = Map.add mangledName fd.Id currentEnv.Functions
                                        ImplMethods = Map.add (typeName, method.Name) fd.Id currentEnv.ImplMethods }
                    currentEnv <- bindVarWithType mangledName fd.Id None funcType currentEnv
            | None ->
                () // Non-named type in impl — skip

        // Normal declarations
        | _ ->
            match lowerDeclWithEnv currentEnv locDecl.Value with
            | Some (Choice1Of3 fd, _) ->
                funcs <- funcs @ [fd]
                // Register in both Functions map (for name resolution) and Variables (for type inference)
                let funcType = IRTFunc (fd.Params |> List.map (fun p -> p.Type), fd.RetType)
                currentEnv <- { currentEnv with Functions = Map.add fd.Name fd.Id currentEnv.Functions }
                currentEnv <- bindVarWithType fd.Name fd.Id None funcType currentEnv
            | Some (Choice2Of3 bds, envUpdates) ->
                bindings <- bindings @ bds
                for (name, id, ty, value) in envUpdates do
                    let identity = AIDVariable name
                    currentEnv <- bindVarWithValue name id (Some identity) (Some ty) value currentEnv
            | Some (Choice3Of3 td, _) ->
                types <- types @ [td]
                // Register struct in environment for type resolution and field access
                match td with
                | IRTDStruct (name, fields, _) ->
                    currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                    currentEnv <- bindType name (IRTNamed name) currentEnv
                | _ -> ()
            | None -> ()
    
    // Build static function usage report
    let usageReport =
        currentEnv.StaticFunctions |> Map.map (fun name _ ->
            match Map.tryFind name currentEnv.StaticUsageTracker.Value with
            | Some u when u = (StaticUsage.CompileTime ||| StaticUsage.RunTime) -> "both"
            | Some u when u = StaticUsage.CompileTime -> "compile-time"
            | Some u when u = StaticUsage.RunTime -> "runtime"
            | _ -> "unused")

    // Extract exports: only variables/functions/types defined in THIS module
    // (exclude anything imported from other modules)
    let exportVars =
        currentEnv.Variables
        |> Map.filter (fun name _ -> not (name.Contains(".")))  // exclude qualified imports
    let exportFuncs =
        currentEnv.Functions
        |> Map.filter (fun name _ -> not (name.Contains(".")))
    let moduleExport : ModuleExport = {
        Variables = exportVars
        Functions = exportFuncs
        Types = currentEnv.Types |> Map.filter (fun name _ -> not (name.Contains(".")))
        StructDefs = currentEnv.StructDefs
        UnitDefs = currentEnv.UnitDefs
        StaticValues = currentEnv.StaticValues |> Map.filter (fun name _ -> not (name.Contains(".")))
        StaticFunctions = currentEnv.StaticFunctions |> Map.filter (fun name _ -> not (name.Contains(".")))
    }

    let irModule = {
        Name = String.concat "." modul.Name
        Types = types
        Functions = funcs
        Bindings = bindings
        StaticFunctionUsage = usageReport
    }
    (irModule, moduleExport)

let lowerProgram (program: Program) : IRProgram =
    let env = emptyEnv()
    let mutable currentExports = Map.empty : Map<string, ModuleExport>
    let mutable modules = []
    
    for modul in program.Modules do
        let envWithExports = { env with ModuleExports = currentExports }
        let (irModule, exports) = lowerModule envWithExports modul
        let moduleName = String.concat "." modul.Name
        currentExports <- Map.add moduleName exports currentExports
        modules <- modules @ [irModule]
    
    { Modules = modules }

// ============================================================================
// TypedAST-based Lowering (New Pipeline)
// ============================================================================
//
// These functions translate from TypedAST to IR. Since type checking has
// already been done, this is a straightforward translation without inference.

open Blade.TypedAst

/// Environment for typed lowering (simplified - no type inference needed)
type TypedLowerEnv = {
    Variables: Map<string, IRId>
    Functions: Map<string, IRId>
    Builder: IRBuilder
    PolyParamNames: string list
    StaticValues: Map<string, StaticEval.StaticValue>
    StaticFunctions: Map<string, StaticEval.StaticFuncDef>
    StaticUsageTracker: ref<Map<string, StaticUsage>>
    UnitDefs: Map<string, UnitSig>
    StructDefs: Map<string, (string * IRType) list>
    ImplMethods: Map<string * string, IRId>
    Interfaces: Map<string, InterfaceDecl>
    ModuleExports: Map<string, ModuleExport>
    ImportedModules: Map<string, string>
}

let emptyTypedEnv () : TypedLowerEnv = {
    Variables = Map.empty
    Functions = Map.empty
    Builder = IRBuilder()
    PolyParamNames = []
    StaticValues = Map.empty
    StaticFunctions = Map.empty
    StaticUsageTracker = ref Map.empty
    UnitDefs = Map.empty
    StructDefs = Map.empty
    ImplMethods = Map.empty
    Interfaces = Map.empty
    ModuleExports = Map.empty
    ImportedModules = Map.empty
}

let bindTypedVar name id (env: TypedLowerEnv) : TypedLowerEnv =
    { env with Variables = Map.add name id env.Variables }

/// Lower a TypedExpr to IRExpr
let rec lowerTypedExpr (env: TypedLowerEnv) (texpr: TypedExpr) : IRExpr =
    match texpr.Kind with
    | TExprLit lit ->
        lowerLiteral lit
    
    | TExprVar (name, varId, identity) ->
        IRVar varId
    
    | TExprQualified names ->
        IRParam (String.concat "." names, 0)
    
    | TExprBinOp (mode, op, left, right) ->
        let l = lowerTypedExpr env left
        let r = lowerTypedExpr env right
        lowerTypedBinOp env mode op l r left right
    
    | TExprUnaryOp (op, operand) ->
        let e = lowerTypedExpr env operand
        match op with
        | OpNeg -> IRUnaryOp (IRNeg, e)
        | OpNot -> IRUnaryOp (IRNot, e)
    
    | TExprApp (func, args) ->
        let f = lowerTypedExpr env func
        let as' = args |> List.map (lowerTypedExpr env)
        IRApp (f, as')
    
    | TExprIndex (array, indices, identity) ->
        let arr = lowerTypedExpr env array
        let idxs = indices |> List.map (lowerTypedExpr env)
        IRIndex (arr, idxs, identity)
    
    | TExprTupleIndex (tuple, index) ->
        let tup = lowerTypedExpr env tuple
        let idx = lowerTypedExpr env index
        IRPolyIndex (tup, idx)
    
    | TExprField (obj, field, _) ->
        let o = lowerTypedExpr env obj
        IRFieldAccess (o, field)
    
    | TExprLambda info ->
        lowerTypedLambda env info
    
    | TExprLet (name, varId, value, body) ->
        let v = lowerTypedExpr env value
        let env' = bindTypedVar name varId env
        let b = lowerTypedExpr env' body
        IRLet (varId, v, b)
    
    | TExprMatch (scrutinee, cases) ->
        let scrut = lowerTypedExpr env scrutinee
        let cases' = cases |> List.map (lowerTypedMatchCase env)
        IRMatch (scrut, cases')
    
    | TExprIf (cond, thenBr, elseBr) ->
        IRIf (lowerTypedExpr env cond, lowerTypedExpr env thenBr, lowerTypedExpr env elseBr)
    
    | TExprTuple exprs ->
        IRTuple (exprs |> List.map (lowerTypedExpr env))
    
    | TExprArrayLit (elems, arrTy) ->
        let es = elems |> List.map (lowerTypedExpr env)
        IRArrayLit (es, arrTy)
    
    | TExprMethodFor info ->
        IRMethodFor {
            Arrays = info.Arrays |> List.map (lowerTypedExpr env)
            Identities = info.Identities
            ArrayTypes = info.ArrayTypes
            SDimsPerArray = info.SDimsPerArray
            TotalSDims = info.TotalSDims
        }
    
    | TExprObjectFor info ->
        IRObjectFor {
            Kernel = lowerTypedExpr env info.Kernel
            CommGroups = info.CommGroups
            InputRanks = info.InputRanks
            OutputRank = info.OutputRank
        }
    
    | TExprApply info ->
        // Symmetry info already computed during type checking
        IRApplyCombinator {
            Loop = lowerTypedExpr env info.Loop
            Kernel = lowerTypedExpr env info.Kernel
            SymcomStates = info.SymcomStates
            TriangularLevels = info.TriangularLevels
            SDimsPerArray = info.SDimsPerArray
            KernelInputRanks = info.KernelInputRanks
            KernelOutputRank = info.KernelOutputRank
            SpeedupFactor = info.SpeedupFactor
            ReynoldsSpeedup = info.ReynoldsSpeedup
            HasReynolds = info.HasReynolds
            OutputType = info.OutputType
        }
    
    | TExprBind (l, r) ->
        IRBind (lowerTypedExpr env l, lowerTypedExpr env r)
    
    | TExprParallel (l, r) ->
        IRParallel (lowerTypedExpr env l, lowerTypedExpr env r, None)
    
    | TExprChoice (l, r) ->
        IRChoice (lowerTypedExpr env l, lowerTypedExpr env r)
    
    | TExprCompose (l, r) ->
        IRCompose (lowerTypedExpr env l, lowerTypedExpr env r)
    
    | TExprRange indexType ->
        IRRange indexType
    
    | TExprReverse indexType ->
        IRVirtualReverse indexType
    
    | TExprBlocked (indexType, size) ->
        IRBlocked (indexType, lowerTypedExpr env size)
    
    | TExprZip exprs ->
        IRZip (exprs |> List.map (lowerTypedExpr env))
    
    | TExprStack exprs ->
        IRStack (exprs |> List.map (lowerTypedExpr env))
    
    | TExprPure e ->
        IRPure (lowerTypedExpr env e)
    
    | TExprCompute e ->
        IRCompute (lowerTypedExpr env e)
    
    | TExprGuard (cond, body) ->
        IRGuard (lowerTypedExpr env cond, lowerTypedExpr env body)
    
    | TExprReynolds (kernel, isAntisym) ->
        IRReynolds (lowerTypedExpr env kernel, isAntisym)
    
    | TExprArity paramName ->
        IRArity (None, paramName)
    
    | TExprRank e ->
        IRRank (lowerTypedExpr env e)
    
    | TExprStruct (typeName, fields) ->
        IRStructLit (typeName, fields |> List.map (fun (fname, e) -> fname, lowerTypedExpr env e))

/// Lower a typed lambda
and lowerTypedLambda env (info: TypedLambdaInfo) : IRExpr =
    let mutable paramEnv = env
    let paramInfos = info.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)
    
    let captures = info.Captures |> List.map (fun c ->
        { Id = c.VarId; Name = c.Name; IsMutable = c.IsMutable } : CaptureInfo)
    
    let body' = lowerTypedExpr paramEnv info.Body
    
    IRLambda {
        Params = paramInfos
        Body = body'
        Captures = captures
        IsCommutative = info.IsCommutative
        CommGroups = info.CommGroups
    }

/// Lower a typed match case
and lowerTypedMatchCase env (case: TypedMatchCase) : IRMatchCase =
    let pat = lowerTypedPattern case.Pattern
    let guard = case.Guard |> Option.map (lowerTypedExpr env)
    let body = lowerTypedExpr env case.Body
    { Pattern = pat; Guard = guard; Body = body }

/// Convert AST literal to IR literal (without wrapping in IRLit)
and lowerLiteralToIRLit lit : IRLit =
    match lit with
    | LitInt n -> IRLitInt n
    | LitFloat f -> IRLitFloat f
    | LitBool b -> IRLitBool b
    | LitString _ -> IRLitInt 0L
    | LitChar c -> IRLitInt (int64 c)
    | LitUnit -> IRLitUnit

/// Lower a typed pattern
and lowerTypedPattern (pat: TypedPattern) : IRPattern =
    match pat.Kind with
    | TPatWild -> IRPatWild
    | TPatVar (_, varId) -> IRPatVar varId
    | TPatLit lit -> IRPatLit (lowerLiteralToIRLit lit)
    | TPatTuple pats -> IRPatTuple (pats |> List.map lowerTypedPattern)
    | TPatCons (h, t) -> IRPatCons (lowerTypedPattern h, lowerTypedPattern t)
    | TPatVariant (tag, payload) -> 
        IRPatVariant (tag, hash tag, payload |> Option.map lowerTypedPattern)
    | TPatStruct (_, fields) ->
        IRPatTuple (fields |> List.map (fun (_, p) -> lowerTypedPattern p))
    | TPatGuarded (p, _) -> lowerTypedPattern p

/// Lower typed binary operations
and lowerTypedBinOp env mode op l r leftExpr rightExpr =
    let irMode = match mode with Elementwise -> IRElementwise | Outer -> IROuter
    
    match op with
    | OpAdd -> IRBinOp (irMode, IRAdd, l, r)
    | OpSub -> IRBinOp (irMode, IRSub, l, r)
    | OpMul -> IRBinOp (irMode, IRMul, l, r)
    | OpDiv -> IRBinOp (irMode, IRDiv, l, r)
    | OpMod -> IRBinOp (irMode, IRMod, l, r)
    | OpCaret -> IRBinOp (irMode, IRCaret, l, r)
    | OpEq -> IRBinOp (irMode, IREq, l, r)
    | OpNeq -> IRBinOp (irMode, IRNeq, l, r)
    | OpLt -> IRBinOp (irMode, IRLt, l, r)
    | OpLe -> IRBinOp (irMode, IRLe, l, r)
    | OpGt -> IRBinOp (irMode, IRGt, l, r)
    | OpGe -> IRBinOp (irMode, IRGe, l, r)
    | OpAnd -> IRBinOp (irMode, IRAnd, l, r)
    | OpOr -> IRBinOp (irMode, IROr, l, r)
    
    | OpApply ->
        // For <@>, symmetry info should already be in TExprApply
        // This case handles when we still have raw binop (shouldn't happen in typed AST)
        IRApplyCombinator {
            Loop = l
            Kernel = r
            SymcomStates = []
            TriangularLevels = []
            SDimsPerArray = []
            KernelInputRanks = []
            KernelOutputRank = 0
            SpeedupFactor = 1L
            ReynoldsSpeedup = 1L
            HasReynolds = false
            OutputType = IRTUnit
        }
    
    | OpBind -> IRBind (l, r)
    | OpParallel -> IRParallel (l, r, None)
    | OpFusion -> IRFusion (l, r)
    | OpArrayProd -> IRArrayProduct (l, r)
    | OpFunctor -> IRFunctorMap (l, r)
    | OpChoice -> IRChoice (l, r)
    | OpFallback -> IRChoice (l, r)
    | OpComposeObj -> IRComposeObj (l, r)
    | OpComposeMeth -> IRComposeMeth (l, r)
    | OpCompose -> IRCompose (l, r)
    | OpCons -> IRTupleCons (l, r)

/// Create a minimal LoweringEnv from a TypedLowerEnv, for reusing
/// lowerTypeExpr and lowerExpr on raw AST nodes (e.g., struct constraints, type decls)
let bridgeEnv (env: TypedLowerEnv) : LoweringEnv =
    // Convert typed variable bindings to VarInfo bindings
    let vars = env.Variables |> Map.map (fun _name id ->
        { Id = id; Identity = None; IsMutable = false; Type = None; Value = None } : VarInfo)
    { Variables = vars; Types = Map.empty; Functions = env.Functions
      Providers = Map.empty; Builder = env.Builder; OuterScope = Map.empty
      InPolyContext = not env.PolyParamNames.IsEmpty
      PolyParamNames = env.PolyParamNames; CurrentCommGroups = []
      Interfaces = env.Interfaces; ImplMethods = env.ImplMethods; StructDefs = env.StructDefs
      StaticValues = env.StaticValues; StaticFunctions = env.StaticFunctions
      StaticUsageTracker = env.StaticUsageTracker; UnitDefs = env.UnitDefs
      ModuleExports = env.ModuleExports; ImportedModules = env.ImportedModules }

/// Lower a TypedFunctionDecl to IRFuncDef
let lowerTypedFuncDecl (env: TypedLowerEnv) (decl: TypedFunctionDecl) : IRFuncDef * TypedLowerEnv =
    // Check for arity polymorphism by inspecting param types
    let polyParamNames = decl.Params |> List.choose (fun p ->
        match p.Type with
        | IRTPoly _ -> Some p.Name
        | _ -> None)
    let isArityPoly = not polyParamNames.IsEmpty

    // Extract parallelism from where clause
    let parallelism =
        match decl.WhereClause with
        | Some wc ->
            wc.Parallelism |> List.choose (fun (name, level) ->
                decl.Params |> List.tryFindIndex (fun p -> p.Name = name)
                |> Option.map (fun idx -> (idx, level)))
        | None -> []

    // Bind parameters in environment for body lowering
    let mutable paramEnv = { env with PolyParamNames = polyParamNames }
    let irParams = decl.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)

    let body = lowerTypedExpr paramEnv decl.Body

    let funcDef : IRFuncDef = {
        Id = decl.FuncId
        Name = decl.Name
        Params = irParams
        RetType = decl.ReturnType
        Body = body
        IsStatic = decl.IsStatic
        Commutativity = if decl.CommGroups.IsEmpty then None else Some decl.CommGroups
        Parallelism = parallelism
        IsArityPoly = isArityPoly
        ArityParam = polyParamNames |> List.tryHead
    }

    let env' = bindTypedVar decl.Name decl.FuncId env
    (funcDef, env')

/// Lower a raw TypeDecl (from TDeclType) using the typed environment's builder
let lowerTypeDeclFromTyped (env: TypedLowerEnv) (td: TypeDecl) : IRTypeDef option =
    let bEnv = bridgeEnv env
    match td with
    | TyDeclAlias (name, _, body) ->
        Some (IRTDAlias (name, lowerTypeExpr bEnv body))
    | TyDeclStruct (name, _, fields, invariant) ->
        let fields2 = fields |> List.map (fun f -> f.Name, lowerTypeExpr bEnv f.Type)
        let constraintInfo =
            match invariant with
            | Some constraintExpr ->
                let fieldBindings = fields |> List.map (fun f ->
                    let id = env.Builder.FreshId()
                    (f.Name, id))
                let constraintEnv =
                    fieldBindings |> List.fold (fun e (fname, fid) ->
                        bindVarSimple fname fid e) bEnv
                let loweredExpr = lowerExpr constraintEnv constraintExpr
                Some { Expr = loweredExpr; FieldBindings = fieldBindings }
            | None -> None
        Some (IRTDStruct (name, fields2, constraintInfo))
    | TyDeclSum (name, _, variants) ->
        let variants2 = variants |> List.map (fun v ->
            v.Name, v.Data |> Option.map (lowerTypeExpr bEnv))
        Some (IRTDVariant (name, variants2))

/// Lower a typed binding
let lowerTypedBinding (env: TypedLowerEnv) (binding: TypedBinding) : IRBinding * TypedLowerEnv =
    let value = lowerTypedExpr env binding.Value
    let env' = bindTypedVar binding.Name binding.VarId env
    let irBinding = {
        Id = binding.VarId
        Name = binding.Name
        Type = binding.Type
        Value = value
        IsConst = not binding.IsMutable
        IsMutable = binding.IsMutable
    }
    (irBinding, env')

/// Lower a typed declaration
let lowerTypedDecl (env: TypedLowerEnv) (decl: TypedDecl) : (Choice<IRFuncDef, IRBinding, IRTypeDef> option * TypedLowerEnv) =
    match decl with
    | TDeclLet binding ->
        let (irBinding, env') = lowerTypedBinding env binding
        (Some (Choice2Of3 irBinding), env')
    
    | TDeclFunction funcDecl ->
        let (funcDef, env') = lowerTypedFuncDecl env funcDecl
        (Some (Choice1Of3 funcDef), env')
    
    | TDeclStatic binding ->
        // Static values: use pre-evaluated value if available, else lower normally
        match Map.tryFind binding.Name env.StaticValues with
        | Some sv ->
            let irValue = staticValueToIR sv
            let id = env.Builder.FreshId()
            let ty = match sv with
                     | StaticEval.SVInt _ -> IRTScalar ETInt64
                     | StaticEval.SVFloat _ -> IRTScalar ETFloat64
                     | StaticEval.SVBool _ -> IRTScalar ETBool
                     | _ -> IRTUnit
            let bd = { Id = id; Name = binding.Name; Type = ty; Value = irValue; IsConst = true; IsMutable = false }
            let env' = bindTypedVar binding.Name id env
            (Some (Choice2Of3 bd), env')
        | None ->
            // Fallback: lower as normal binding
            let (irBinding, env') = lowerTypedBinding env binding
            (Some (Choice2Of3 irBinding), env')
    
    | TDeclType td ->
        match lowerTypeDeclFromTyped env td with
        | Some irTd -> (Some (Choice3Of3 irTd), env)
        | None -> (None, env)
    
    | TDeclUnit unitDecl ->
        // Register unit in environment (same logic as untyped pipeline)
        let sig' =
            match unitDecl.Definition with
            | None | Some UnitBase ->
                Map.ofList [(unitDecl.Name, 1)]
            | Some (UnitDerived expr) ->
                match resolveUnitExpr env.UnitDefs expr with
                | Ok resolved -> resolved
                | Error msg ->
                    eprintfn "Unit error: %s" msg
                    Map.ofList [(unitDecl.Name, 1)]
        let env' = { env with UnitDefs = Map.add unitDecl.Name sig' env.UnitDefs }
        (None, env')
    
    | TDeclImport _ ->
        // Handled specially in lowerTypedModule (needs module export threading)
        (None, env)
    
    | TDeclInterface ifaceDecl ->
        let env' = { env with Interfaces = Map.add ifaceDecl.Name ifaceDecl env.Interfaces }
        (None, env')
    
    | TDeclImpl _ ->
        // Handled specially in lowerTypedModule (needs to emit multiple functions)
        (None, env)

/// Lower a typed module
let lowerTypedModule (env: TypedLowerEnv) (modul: TypedModule) (rawDecls: Located<Decl> list option) : IRModule * ModuleExport =
    // Phase 0: Resolve static values/functions from raw declarations
    let mutable currentEnv = 
        match rawDecls with
        | Some decls ->
            match StaticEval.resolveStatics decls with
            | Ok staticEnv ->
                let tracker = ref Map.empty
                for fname in staticEnv.CalledFunctions.Value do
                    tracker.Value <- Map.add fname StaticUsage.CompileTime tracker.Value
                { env with 
                    StaticValues = staticEnv.Values
                    StaticFunctions = staticEnv.Functions
                    StaticUsageTracker = tracker }
            | Error _ -> env
        | None -> env
    
    let mutable funcs = []
    let mutable bindings = []
    let mutable types = []
    
    for decl in modul.Decls do
        match decl with
        // Handle imports: resolve module exports
        | TDeclImport (qname, style) ->
            let fullName = String.concat "." qname
            match style with
            | ImportQualified aliasOpt ->
                let alias = aliasOpt |> Option.defaultValue (List.last qname)
                match Map.tryFind fullName currentEnv.ModuleExports with
                | Some exports ->
                    currentEnv <- { currentEnv with ImportedModules = Map.add alias fullName currentEnv.ImportedModules }
                    for kv in exports.Variables do
                        let qualName = sprintf "%s.%s" alias kv.Key
                        currentEnv <- bindTypedVar qualName kv.Value.Id currentEnv
                    for kv in exports.Functions do
                        currentEnv <- { currentEnv with Functions = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.Functions }
                    for kv in exports.StructDefs do
                        currentEnv <- { currentEnv with StructDefs = Map.add kv.Key kv.Value currentEnv.StructDefs }
                    for kv in exports.UnitDefs do
                        currentEnv <- { currentEnv with UnitDefs = Map.add kv.Key kv.Value currentEnv.UnitDefs }
                    for kv in exports.StaticValues do
                        currentEnv <- { currentEnv with StaticValues = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.StaticValues }
                    for kv in exports.StaticFunctions do
                        currentEnv <- { currentEnv with StaticFunctions = Map.add (sprintf "%s.%s" alias kv.Key) kv.Value currentEnv.StaticFunctions }
                | None ->
                    eprintfn "Warning: module '%s' not found in typed pipeline" fullName
            | ImportSelective names ->
                match Map.tryFind fullName currentEnv.ModuleExports with
                | Some exports ->
                    for name in names do
                        match Map.tryFind name exports.Variables with
                        | Some varInfo -> currentEnv <- bindTypedVar name varInfo.Id currentEnv
                        | None -> ()
                        match Map.tryFind name exports.Functions with
                        | Some funcId -> currentEnv <- { currentEnv with Functions = Map.add name funcId currentEnv.Functions }
                        | None -> ()
                        match Map.tryFind name exports.StaticValues with
                        | Some sv -> currentEnv <- { currentEnv with StaticValues = Map.add name sv currentEnv.StaticValues }
                        | None -> ()
                        match Map.tryFind name exports.StaticFunctions with
                        | Some sf -> currentEnv <- { currentEnv with StaticFunctions = Map.add name sf currentEnv.StaticFunctions }
                        | None -> ()
                        match Map.tryFind name exports.StructDefs with
                        | Some fields -> currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                        | None -> ()
                        match Map.tryFind name exports.UnitDefs with
                        | Some unitSig -> currentEnv <- { currentEnv with UnitDefs = Map.add name unitSig currentEnv.UnitDefs }
                        | None -> ()
                | None ->
                    eprintfn "Warning: module '%s' not found for selective import in typed pipeline" fullName
        
        // Handle impl blocks: monomorphize methods
        | TDeclImpl implDecl ->
            match extractTypeName implDecl.ForType with
            | Some typeName ->
                for method in implDecl.Methods do
                    let mangledName = sprintf "%s__%s" typeName method.Name
                    let typedParams = method.Params |> List.map (fun p ->
                        if p.Name = "self" && p.Type.IsNone then
                            { p with Type = Some implDecl.ForType }
                        else p)
                    let modifiedDecl = { method with Name = mangledName; Params = typedParams }
                    let lEnv = bridgeEnv currentEnv
                    let fd = lowerFuncDecl lEnv modifiedDecl
                    funcs <- funcs @ [fd]
                    currentEnv <- { currentEnv with
                                        Functions = Map.add mangledName fd.Id currentEnv.Functions
                                        ImplMethods = Map.add (typeName, method.Name) fd.Id currentEnv.ImplMethods }
                    currentEnv <- bindTypedVar mangledName fd.Id currentEnv
            | None -> ()
        
        // All other declarations go through lowerTypedDecl
        | _ ->
            match lowerTypedDecl currentEnv decl with
            | (Some (Choice1Of3 fd), env') ->
                funcs <- funcs @ [fd]
                currentEnv <- env'
                // Register function in env for subsequent declarations
                currentEnv <- { currentEnv with Functions = Map.add fd.Name fd.Id currentEnv.Functions }
                currentEnv <- bindTypedVar fd.Name fd.Id currentEnv
            | (Some (Choice2Of3 bd), env') ->
                bindings <- bindings @ [bd]
                currentEnv <- env'
            | (Some (Choice3Of3 td), env') ->
                types <- types @ [td]
                currentEnv <- env'
                // Register structs in environment
                match td with
                | IRTDStruct (name, fields, _) ->
                    currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                | _ -> ()
            | (None, env') ->
                currentEnv <- env'
    
    // Build static function usage report
    let usageReport =
        currentEnv.StaticFunctions |> Map.map (fun name _ ->
            match Map.tryFind name currentEnv.StaticUsageTracker.Value with
            | Some u when u = (StaticUsage.CompileTime ||| StaticUsage.RunTime) -> "both"
            | Some u when u = StaticUsage.CompileTime -> "compile-time"
            | Some u when u = StaticUsage.RunTime -> "runtime"
            | _ -> "unused")
    
    // Build exports
    let exportVars =
        currentEnv.Variables
        |> Map.filter (fun name _ -> not (name.Contains(".")))
        |> Map.map (fun name id -> { Id = id; Identity = None; IsMutable = false; Type = None; Value = None } : VarInfo)
    let exportFuncs =
        currentEnv.Functions
        |> Map.filter (fun name _ -> not (name.Contains(".")))
    let moduleExport : ModuleExport = {
        Variables = exportVars
        Functions = exportFuncs
        Types = Map.empty
        StructDefs = currentEnv.StructDefs
        UnitDefs = currentEnv.UnitDefs
        StaticValues = currentEnv.StaticValues |> Map.filter (fun name _ -> not (name.Contains(".")))
        StaticFunctions = currentEnv.StaticFunctions |> Map.filter (fun name _ -> not (name.Contains(".")))
    }
    
    let irModule = {
        Name = modul.Name |> Option.map (String.concat ".") |> Option.defaultValue ""
        Types = types
        Functions = funcs
        Bindings = bindings
        StaticFunctionUsage = usageReport
    }
    (irModule, moduleExport)

/// Lower a typed program (with optional raw program for static evaluation)
let lowerTypedProgram (program: TypedProgram) (rawProgram: Program option) : IRProgram =
    let env = emptyTypedEnv()
    let mutable currentExports = Map.empty<string, ModuleExport>
    let mutable irModules = []
    
    let rawModules = 
        match rawProgram with
        | Some p -> p.Modules |> List.map (fun m -> Some m.Decls)
        | None -> program.Modules |> List.map (fun _ -> None)
    
    for (tmod, rawDecls) in List.zip program.Modules rawModules do
        let moduleName = tmod.Name |> Option.map (String.concat ".") |> Option.defaultValue ""
        let envWithExports = { env with ModuleExports = currentExports }
        let (irModule, exports) = lowerTypedModule envWithExports tmod rawDecls
        currentExports <- Map.add moduleName exports currentExports
        irModules <- irModules @ [irModule]
    
    { Modules = irModules }

// ============================================================================
// Convenience function for testing
// ============================================================================

let lower (source: string) : Result<IRProgram, string> =
    match Blade.Parser.parseProgram source with
    | Ok program -> Ok (lowerProgram program)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// Lower multiple source files into a single IR program with cross-module imports
let lowerMultiSource (sources: (string * string) list) : Result<IRProgram, string> =
    match Blade.Parser.parseMultiSource sources with
    | Ok program -> Ok (lowerProgram program)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// New pipeline: Parse -> TypeCheck -> Lower
let lowerWithTypeCheck (source: string) : Result<IRProgram, string> =
    match Blade.Parser.parseProgram source with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok typedProgram -> Ok (lowerTypedProgram typedProgram (Some program))
        | Error errors -> 
            let msgs = errors |> List.map Blade.TypeCheck.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)
