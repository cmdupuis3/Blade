// Blade-DSL Lowering Pass
// Transforms AST to IR with proper support for:
// - Array identity tracking (for triangular iteration)
// - Lambda capture analysis
// - Pattern binding
// - Arity polymorphism
// - Kernel irank/orank inference

module Blade.Lowering

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

type LoweringEnv = {
    Variables: Map<string, VarInfo>
    Types: Map<string, IRType>
    Functions: Map<string, IRId>
    Providers: Map<string, string list>  // alias -> qualified name (e.g. "NetCDF" -> ["Providers";"NetCDF"])
    Builder: IRBuilder
    OuterScope: Map<string, VarInfo>
    InPolyContext: bool
    CurrentCommGroups: int list list
}

let emptyEnv () = {
    Variables = Map.empty
    Types = Map.empty
    Functions = Map.empty
    Providers = Map.empty
    Builder = IRBuilder()
    OuterScope = Map.empty
    InPolyContext = false
    CurrentCommGroups = []
}

let bindVar name info env =
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

let inferTypeFromExpr (expr: IRExpr) : IRType option =
    match expr with
    | IRArrayLit (_, arrTy) -> Some (IRTArray arrTy)
    | IRLit (IRLitInt _) -> Some (IRTScalar ETInt64)
    | IRLit (IRLitFloat _) -> Some (IRTScalar ETFloat64)
    | IRLit (IRLitBool _) -> Some (IRTScalar ETBool)
    | IRLit IRLitUnit -> Some IRTUnit
    | IRMethodFor info -> 
        Some (IRTLoop { Kind = LKMethod; Arity = Some info.Arrays.Length; 
                        ArrayTypes = info.ArrayTypes |> List.map IRTArray; KernelType = None })
    | IRObjectFor info ->
        Some (IRTLoop { Kind = LKObject; Arity = Some info.InputRanks.Length;
                        ArrayTypes = []; KernelType = Some (IRTUnit) })
    | IRLambda info ->
        let argTypes = info.Params |> List.map (fun p -> p.Type)
        Some (IRTFunc (argTypes, IRTUnit))
    | _ -> None

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
    | _ ->
        // Expression result - infer rank 0 (scalar result)
        { ElemType = ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

// ============================================================================
// Type Lowering
// ============================================================================

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
    | TyString -> IRTScalar ETInt64
    | TyChar -> IRTScalar ETInt32
    
    | TyNamed (name, args) ->
        match name with
        | "Int" | "Int32" -> IRTScalar ETInt32
        | "Int64" -> IRTScalar ETInt64
        | "Float" | "Float32" -> IRTScalar ETFloat32
        | "Float64" | "Double" -> IRTScalar ETFloat64
        | "Bool" -> IRTScalar ETBool
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
            | ExprArity -> 
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
    
    | TyVar _ -> IRTScalar ETFloat64
    
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
    
    | TyFullSymIdx (arity, extent) ->
        let extExpr = lowerExpr env extent
        let idx = { Id = 0; Arity = arity; Extent = extExpr; Symmetry = SymSymmetric; Tag = Some "full"; Kind = SDimension; Dependencies = [] }
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
    | _ -> ETFloat64

and lowerIndexType env id ty : IRIndexType =
    match ty with
    | TyIdx extent ->
        { Id = id; Arity = 1; Extent = lowerExpr env extent; Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
    | TySymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExpr env extent; Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyAntisymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExpr env extent; Symmetry = SymAntisymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyFullSymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = lowerExpr env extent; Symmetry = SymSymmetric; Tag = Some "full"; Kind = SDimension; Dependencies = [] }
    | TyHermitianIdx extent ->
        { Id = id; Arity = 2; Extent = lowerExpr env extent; Symmetry = SymHermitian; Tag = None; Kind = SDimension; Dependencies = [] }
    | TyNamed (name, args) ->
        let extent = 
            match args with
            | [TyNamed (n, [])] -> IRParam (n, 0)
            | [TyVar n] -> IRParam (n, 0)
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
        match lookupVar name env with
        | Some info -> IRVar info.Id
        | None -> 
            match Map.tryFind name env.OuterScope with
            | Some info -> IRVar info.Id
            | None -> IRParam (name, 0)
    | ExprQualified names ->
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
    | ExprField (obj, field) ->
        let o = lowerExpr env obj
        IRApp (IRParam (field, 0), [o])
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
    | ExprArity -> 
        // arity is unresolved at definition time, bound at call site
        if env.InPolyContext then IRArity None
        else failwith "arity keyword used outside of arity-polymorphic context"
    | ExprNth -> IRNth
    | ExprZero -> IRZero
    | ExprRank e -> IRRank (lowerExpr env e)
    | ExprStruct (name, fields) ->
        // For now, lower struct to tuple of field values
        IRTuple (fields |> List.map (fun (_, e) -> lowerExpr env e))
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
        let ty = match p.Type with 
                 | Some t -> lowerTypeExpr env t 
                 | None -> env.Builder.FreshInferType()  // Infer from context
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
    
    match op with
    // Arithmetic and comparison ops - affected by mode
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
        // Resolve loop to get method_for info (handles variable references)
        let resolvedLoop = resolveToMethodFor env l
        
        // Extract loop info from resolved loop
        let (identities, arrayTypes, sDimsPerArray) = 
            match resolvedLoop with
            | IRMethodFor mfInfo -> 
                (mfInfo.Identities, mfInfo.ArrayTypes, mfInfo.SDimsPerArray)
            | _ -> 
                // Fallback to extracting from AST
                extractLoopInfo env leftExpr
        
        // If no loop info from left, try right (for object_for case)
        let (identities, arrayTypes, sDimsPerArray) =
            if identities.IsEmpty then extractArrayInfoFromTuple env rightExpr
            else (identities, arrayTypes, sDimsPerArray)
        
        // Resolve kernel to get lambda info (handles variable references)
        let resolvedKernel = resolveToLambda env r
        
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
            match r with
            | IRReynolds (k, antisym) -> (k, true, antisym)
            | _ -> (r, false, false)
        
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
            Loop = l
            Kernel = r  // Keep original (may include IRReynolds wrapper)
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
    let body = 
        match op with
        | OpAdd -> IRBinOp(IRElementwise, IRAdd, IRVar aId, IRVar bId)
        | OpSub -> IRBinOp(IRElementwise, IRSub, IRVar aId, IRVar bId)
        | OpMul -> IRBinOp(IRElementwise, IRMul, IRVar aId, IRVar bId)
        | OpDiv -> IRBinOp(IRElementwise, IRDiv, IRVar aId, IRVar bId)
        | _ -> IRBinOp(IRElementwise, IRAdd, IRVar aId, IRVar bId)
    IRLambda {
        Params = [{ Name = "a"; Type = IRTScalar ETFloat64; Index = 0; VarId = aId }
                  { Name = "b"; Type = IRTScalar ETFloat64; Index = 1; VarId = bId }]
        Body = body
        Captures = []
        IsCommutative = false
        CommGroups = []
    }

/// Lower partial application
and lowerPartialApp env op arg isLeft =
    let argExpr = lowerExpr env arg
    let paramId = env.Builder.FreshId()
    let body =
        let irOp = match op with
                   | OpAdd -> IRAdd | OpSub -> IRSub
                   | OpMul -> IRMul | OpDiv -> IRDiv
                   | _ -> IRAdd
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
    let pat = lowerPattern env case.Pattern
    let guard = case.Guard |> Option.map (lowerExpr env)
    let body = lowerExpr env case.Body
    { Pattern = pat; Guard = guard; Body = body }

and lowerPattern env (pat: Pattern) : IRPattern =
    match pat with
    | PatWildcard -> IRPatWild
    | PatVar name ->
        let id = env.Builder.FreshId()
        IRPatVar id
    | PatLit lit ->
        match lit with
        | LitInt n -> IRPatLit (IRLitInt n)
        | LitFloat f -> IRPatLit (IRLitFloat f)
        | LitBool b -> IRPatLit (IRLitBool b)
        | _ -> IRPatWild
    | PatTuple pats ->
        IRPatTuple (pats |> List.map (lowerPattern env))
    | PatCons (head, tail) ->
        IRPatCons (lowerPattern env head, lowerPattern env tail)
    | PatVariant (name, innerOpt) ->
        let inner = innerOpt |> Option.map (lowerPattern env)
        IRPatVariant (0, inner)
    | _ -> IRPatWild

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
    
    // Check if any parameter has Poly type
    let polyParam = decl.Params |> List.tryFind (fun p ->
        match p.Type with
        | Some (TyPoly _) -> true
        | _ -> false)
    
    let isArityPoly = polyParam.IsSome
    let arityParamName = polyParam |> Option.map (fun p -> p.Name)
    
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
    // Set InPolyContext if this function has Poly parameters
    let mutable paramEnv = { env with CurrentCommGroups = commGroups; InPolyContext = isArityPoly }
    let params2 = decl.Params |> List.mapi (fun i p ->
        let paramId = env.Builder.FreshId()
        let ty = match p.Type with 
                 | Some t -> lowerTypeExpr env t 
                 | None -> env.Builder.FreshInferType()  // Infer from context
        let identity = AIDParameter (p.Name, i)
        paramEnv <- bindVarWithType p.Name paramId (Some identity) ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = paramId })
    
    let retTy = 
        match decl.ReturnType with
        | Some t -> lowerTypeExpr paramEnv t  // Use paramEnv so arity is in scope for return type
        | None -> env.Builder.FreshInferType()  // Infer from body
    
    let body2 = lowerExpr paramEnv decl.Body
    
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
        ArityParam = arityParamName
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
            inferTypeFromExpr value |> Option.defaultValue IRTUnit)
        let bd = {
            Id = id
            Name = name
            Type = ty
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = binding.Mutability = BindMut
        }
        [bd], [(name, id, ty, value)]
    
    | PatTuple pats ->
        // First, create a binding for the whole tuple
        let tupleId = env.Builder.FreshId()
        let tupleTy = declaredTy |> Option.defaultWith (fun () ->
            inferTypeFromExpr value |> Option.defaultValue IRTUnit)
        let tupleBd = {
            Id = tupleId
            Name = sprintf "_tuple_%d" tupleId
            Type = tupleTy
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = binding.Mutability = BindMut
        }
        
        // Then create bindings for each element
        let rec extractPatBindings (pat: Pattern) (proj: IRExpr) : IRBinding list * (string * IRId * IRType * IRExpr) list =
            match pat with
            | PatVar name ->
                let id = env.Builder.FreshId()
                let ty = IRTUnit  // Will be inferred from usage
                let bd = {
                    Id = id
                    Name = name
                    Type = ty
                    Value = proj
                    IsConst = binding.Mutability = BindConst
                    IsMutable = binding.Mutability = BindMut
                }
                [bd], [(name, id, ty, proj)]
            | PatWildcard -> [], []
            | PatTuple innerPats ->
                let mutable bds = []
                let mutable envUpdates = []
                for i, p in List.indexed innerPats do
                    let (bs, us) = extractPatBindings p (IRTupleProj(proj, i))
                    bds <- bds @ bs
                    envUpdates <- envUpdates @ us
                bds, envUpdates
            | _ -> [], []  // Other patterns not fully supported yet
        
        let mutable allBindings = [tupleBd]
        let mutable allEnvUpdates = []
        for i, p in List.indexed pats do
            let proj = IRTupleProj(IRVar tupleId, i)
            let (bs, us) = extractPatBindings p proj
            allBindings <- allBindings @ bs
            allEnvUpdates <- allEnvUpdates @ us
        
        allBindings, allEnvUpdates
    
    | _ ->
        // Fallback for other patterns
        let id = env.Builder.FreshId()
        let name = sprintf "binding_%d" id
        let ty = declaredTy |> Option.defaultWith (fun () ->
            inferTypeFromExpr value |> Option.defaultValue IRTUnit)
        let bd = {
            Id = id
            Name = name
            Type = ty
            Value = value
            IsConst = binding.Mutability = BindConst
            IsMutable = binding.Mutability = BindMut
        }
        [bd], [(name, id, ty, value)]

/// Simple wrapper for backward compatibility
let lowerBinding env (binding: Binding) : IRBinding =
    let (bindings, _) = lowerBindingWithEnv env binding
    List.head bindings

/// Returns bindings list and environment updates for pattern destructuring
let lowerDeclWithEnv env (decl: Decl) : (Choice<IRFuncDef, IRBinding list, IRTypeDef> * (string * IRId * IRType * IRExpr) list) option =
    match decl with
    | DeclFunction fd ->
        Some (Choice1Of3 (lowerFuncDecl env fd), [])
    | DeclLet binding ->
        let (bindings, envUpdates) = lowerBindingWithEnv env binding
        Some (Choice2Of3 bindings, envUpdates)
    | DeclStatic binding ->
        let (bindings, envUpdates) = lowerBindingWithEnv env binding
        Some (Choice2Of3 bindings, envUpdates)
    | DeclType td ->
        match td with
        | TyDeclAlias (name, _, body) ->
            Some (Choice3Of3 (IRTDAlias (name, lowerTypeExpr env body)), [])
        | TyDeclStruct (name, _, fields) ->
            let fields2 = fields |> List.map (fun f -> f.Name, lowerTypeExpr env f.Type)
            Some (Choice3Of3 (IRTDStruct (name, fields2)), [])
        | TyDeclSum (name, _, variants) ->
            let variants2 = variants |> List.map (fun v -> 
                v.Name, v.Data |> Option.map (lowerTypeExpr env))
            Some (Choice3Of3 (IRTDVariant (name, variants2)), [])
    | DeclImport _ -> None  // Handled specially in lowerModule
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

let lowerModule env (modul: ModuleDecl) : IRModule =
    let mutable currentEnv = env
    let mutable funcs = []
    let mutable bindings = []
    let mutable types = []
    
    for locDecl in modul.Decls do
        match locDecl.Value with
        // Handle imports: register provider in environment
        | DeclImport (qname, aliasOpt) ->
            let alias = aliasOpt |> Option.defaultValue (List.last qname)
            currentEnv <- { currentEnv with Providers = Map.add alias qname currentEnv.Providers }

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

        // Normal declarations
        | _ ->
            match lowerDeclWithEnv currentEnv locDecl.Value with
            | Some (Choice1Of3 fd, _) ->
                funcs <- funcs @ [fd]
                currentEnv <- { currentEnv with Functions = Map.add fd.Name fd.Id currentEnv.Functions }
            | Some (Choice2Of3 bds, envUpdates) ->
                bindings <- bindings @ bds
                for (name, id, ty, value) in envUpdates do
                    let identity = AIDVariable name
                    currentEnv <- bindVarWithValue name id (Some identity) (Some ty) value currentEnv
            | Some (Choice3Of3 td, _) ->
                types <- types @ [td]
            | None -> ()
    
    {
        Name = String.concat "." modul.Name
        Types = types
        Functions = funcs
        Bindings = bindings
    }

let lowerProgram (program: Program) : IRProgram =
    let env = emptyEnv()
    let modules = program.Modules |> List.map (lowerModule env)
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
    Builder: IRBuilder
}

let emptyTypedEnv () : TypedLowerEnv = {
    Variables = Map.empty
    Builder = IRBuilder()
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
        IRApp (IRParam (field, 0), [o])
    
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
    
    | TExprArity ->
        IRArity None
    
    | TExprRank e ->
        IRRank (lowerTypedExpr env e)
    
    | TExprStruct (_, fields) ->
        IRTuple (fields |> List.map (fun (_, e) -> lowerTypedExpr env e))

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
        IRPatVariant (hash tag, payload |> Option.map lowerTypedPattern)
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
        // Simplified - would need full implementation
        (None, env)
    
    | TDeclType td ->
        (None, env)
    
    | TDeclInterface _ ->
        (None, env)
    
    | TDeclImpl _ ->
        (None, env)

/// Lower a typed module
let lowerTypedModule (env: TypedLowerEnv) (modul: TypedModule) : IRModule =
    let mutable currentEnv = env
    let mutable bindings = []
    
    for decl in modul.Decls do
        match lowerTypedDecl currentEnv decl with
        | (Some (Choice2Of3 bd), env') ->
            bindings <- bindings @ [bd]
            currentEnv <- env'
        | (_, env') ->
            currentEnv <- env'
    
    {
        Name = modul.Name |> Option.map (String.concat ".") |> Option.defaultValue ""
        Types = []
        Functions = []
        Bindings = bindings
    }

/// Lower a typed program
let lowerTypedProgram (program: TypedProgram) : IRProgram =
    let env = emptyTypedEnv()
    let modules = program.Modules |> List.map (lowerTypedModule env)
    { Modules = modules }

// ============================================================================
// Convenience function for testing
// ============================================================================

let lower (source: string) : Result<IRProgram, string> =
    match Blade.Parser.parseProgram source with
    | Ok program -> Ok (lowerProgram program)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// New pipeline: Parse -> TypeCheck -> Lower
let lowerWithTypeCheck (source: string) : Result<IRProgram, string> =
    match Blade.Parser.parseProgram source with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok typedProgram -> Ok (lowerTypedProgram typedProgram)
        | Error e -> 
            match e with
            | Blade.TypeCheck.UnboundVariable name -> Error (sprintf "Unbound variable: %s" name)
            | Blade.TypeCheck.TypeMismatch (exp, act) -> Error (sprintf "Type mismatch: expected %A, got %A" exp act)
            | Blade.TypeCheck.InvalidArrayCapture name -> Error (sprintf "Lambda cannot capture array '%s'" name)
            | Blade.TypeCheck.Other msg -> Error msg
            | _ -> Error "Type checking failed"
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)
