// ============================================================================
// TypeCheck.fs - Type Checking and Inference
// ============================================================================
//
// This module performs type checking and inference on the AST, producing
// a TypedAST with all type annotations resolved. Key responsibilities:
//
// - Type inference for expressions
// - Array identity tracking
// - Commutativity group extraction
// - Symmetry analysis (SymcomState computation)
// - Capture analysis for lambdas
//
// The symmetry analysis that was previously done during lowering is now
// performed here, making type information available for IDE support.

module Blade.TypeCheck

open Blade.Ast
open Blade.IR
open Blade.TypedAst

// ============================================================================
// Type Checking Environment
// ============================================================================

/// Information about a bound variable during type checking
type VarInfo = {
    VarId: IRId
    Type: IRType
    Identity: ArrayIdentity option
    IsMutable: bool
}

/// Type checking environment
type TypeEnv = {
    Variables: Map<string, VarInfo>
    Types: Map<string, IRType>
    Functions: Map<string, IRType>
    Builder: IRBuilder
    OuterScope: Map<string, VarInfo>
    InPolyContext: bool
    CurrentCommGroups: int list list
}

/// Type checking errors
type TypeError =
    | UnboundVariable of string
    | TypeMismatch of expected: IRType * actual: IRType
    | ArityMismatch of expected: int * actual: int
    | InvalidArrayCapture of varName: string
    | InvalidApplication of funcType: IRType
    | PatternTypeMismatch of pattern: string * expected: IRType
    | Other of string

type TypeResult<'T> = Result<'T, TypeError>

// ============================================================================
// Environment Operations
// ============================================================================

let emptyEnv () = {
    Variables = Map.empty
    Types = Map.empty
    Functions = Map.empty
    Builder = IRBuilder()
    OuterScope = Map.empty
    InPolyContext = false
    CurrentCommGroups = []
}

let bindVar name info env =
    { env with Variables = Map.add name info env.Variables }

let bindVarSimple name varId ty env =
    bindVar name { VarId = varId; Type = ty; Identity = None; IsMutable = false } env

let bindVarWithIdentity name varId ty identity env =
    bindVar name { VarId = varId; Type = ty; Identity = Some identity; IsMutable = false } env

let bindVarFull name varId ty identity isMutable env =
    bindVar name { VarId = varId; Type = ty; Identity = identity; IsMutable = isMutable } env

let lookupVar name env =
    Map.tryFind name env.Variables

let lookupType name env =
    Map.tryFind name env.Types

let enterScope env =
    { env with OuterScope = env.Variables }

// ============================================================================
// Type Expression Lowering (AST TypeExpr -> IRType)
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
    
    | TyFunc (args, ret) ->
        IRTFunc (args |> List.map (lowerTypeExpr env), lowerTypeExpr env ret)
    
    | TyTuple tys ->
        IRTTuple (tys |> List.map (lowerTypeExpr env))
    
    | TyVar _ -> IRTScalar ETFloat64
    
    | TyIdx extent ->
        let idx = { Id = 0; Arity = 1; Extent = IRLit (IRLitInt 0L); Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
    | TySymIdx (arity, extent) ->
        let idx = { Id = 0; Arity = arity; Extent = IRLit (IRLitInt 0L); Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
        IRTArray { ElemType = ETFloat64; IndexTypes = [idx]; IsVirtual = false; Identity = None }
    
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
        { Id = id; Arity = 1; Extent = IRLit (IRLitInt 0L)
          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }
    | TySymIdx (arity, extent) ->
        { Id = id; Arity = arity; Extent = IRLit (IRLitInt 0L)
          Symmetry = SymSymmetric; Tag = None; Kind = SDimension; Dependencies = [] }
    | _ ->
        { Id = id; Arity = 1; Extent = IRLit (IRLitInt 0L)
          Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] }

// ============================================================================
// Capture Analysis
// ============================================================================

/// Collect free variables in an expression
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
            bindVarSimple p.Name (e.Builder.FreshId()) IRTUnit e) env
        collectFreeVars env' body
    | ExprLet (binding, body) ->
        let valueFree = collectFreeVars env binding.Value
        let env' = 
            match binding.Pattern with
            | PatVar name -> bindVarSimple name (env.Builder.FreshId()) IRTUnit env
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

/// Build capture list from free variables
let buildCaptures env (freeVars: Set<string>) : TypedVarInfo list =
    freeVars |> Set.toList |> List.choose (fun name ->
        match Map.tryFind name env.OuterScope with
        | Some info -> 
            Some { Name = name; Type = info.Type; Identity = info.Identity
                   IsMutable = info.IsMutable; VarId = info.VarId }
        | None -> None)

/// Validate that lambdas don't capture arrays
let validateNoArrayCaptures env (captures: TypedVarInfo list) : TypeResult<unit> =
    let arrayCapture = captures |> List.tryFind (fun c ->
        match c.Type with
        | IRTArray _ -> true
        | _ -> false)
    match arrayCapture with
    | Some c -> Error (InvalidArrayCapture c.Name)
    | None -> Ok ()

// ============================================================================
// Pattern Binding
// ============================================================================

/// Extract variable names bound by a pattern
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

// ============================================================================
// Array Type Inference
// ============================================================================

/// Infer element type from a list of expressions
let inferElemType (exprs: TypedExpr list) : ElemType =
    if List.isEmpty exprs then ETFloat64
    else
        match exprs.[0].Type with
        | IRTScalar et -> et
        | IRTArray arr -> arr.ElemType
        | _ -> ETFloat64

/// Infer array shape from nested array literals
let inferArrayLitShape (exprs: TypedExpr list) : int list =
    let mutable shape = []
    let mutable current = exprs
    let mutable cont = true
    while cont do
        match current with
        | [] -> 
            shape <- shape @ [0]
            cont <- false
        | first :: _ ->
            shape <- shape @ [List.length current]
            match first.Kind with
            | TExprArrayLit (inner, _) ->
                current <- inner
            | _ ->
                cont <- false
    shape

/// Infer array type from array literal elements
let inferArrayLitType (exprs: TypedExpr list) : IRArrayType =
    let elemType = inferElemType exprs
    let shape = inferArrayLitShape exprs
    let indexTypes = 
        shape |> List.mapi (fun i extent ->
            { Id = i; Arity = 1; Extent = IRLit (IRLitInt (int64 extent))
              Symmetry = SymNone; Tag = None; Kind = SDimension; Dependencies = [] })
    { ElemType = elemType; IndexTypes = indexTypes; IsVirtual = false; Identity = None }

/// Get array type for an expression
let getArrayType env (expr: Expr) : IRArrayType =
    match expr with
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            match info.Type with
            | IRTArray arrTy -> arrTy
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
    | _ ->
        // Expression result - infer rank 0 (scalar result)
        { ElemType = ETFloat64; IndexTypes = []; IsVirtual = false; Identity = None }

// ============================================================================
// Commutativity Extraction
// ============================================================================

/// Extract commutativity groups from where clause
let extractCommGroups (parms: LambdaParam list) (whereClause: WhereClause option) : int list list =
    match whereClause with
    | Some wc -> 
        wc.Commutativity |> List.map (fun names ->
            names |> List.choose (fun name ->
                parms |> List.tryFindIndex (fun p -> p.Name = name)))
    | None -> []

// ============================================================================
// Symmetry Analysis (moved from Lowering)
// ============================================================================

/// Information needed for symmetry analysis
type SymmetryContext = {
    Identities: ArrayIdentity list
    ArrayTypes: IRArrayType list
    CommGroups: int list list
    SDimsPerArray: int list
}

/// Compute symmetry information for a combinator application
let computeSymmetryInfo (ctx: SymmetryContext) (builder: IRBuilder) : TypedApplyInfo -> TypedApplyInfo =
    fun info ->
        let states = computeAllSymcomStates ctx.Identities ctx.ArrayTypes ctx.CommGroups ctx.SDimsPerArray
        let triangularLevels = computeTriangularLevels ctx.ArrayTypes ctx.Identities ctx.CommGroups ctx.SDimsPerArray
        let speedup = computePartialProductSpeedup ctx.ArrayTypes ctx.Identities ctx.CommGroups ctx.SDimsPerArray
        let outputType = deduceOutputType ctx.ArrayTypes ctx.Identities ctx.CommGroups ctx.SDimsPerArray info.KernelOutputRank builder
        { info with
            SymcomStates = states
            TriangularLevels = triangularLevels
            SpeedupFactor = speedup
            OutputType = outputType }

// ============================================================================
// Helper Functions
// ============================================================================

/// Sequence a list of Results into a Result of list
let sequenceResults (results: TypeResult<'a> list) : TypeResult<'a list> =
    let rec loop acc = function
        | [] -> Ok (List.rev acc)
        | (Ok x) :: rest -> loop (x :: acc) rest
        | (Error e) :: _ -> Error e
    loop [] results

// ============================================================================
// Main Type Inference
// ============================================================================

/// Infer the type of an expression, producing a TypedExpr
let rec inferExpr (env: TypeEnv) (expr: Expr) : TypeResult<TypedExpr> =
    match expr with
    | ExprLit lit ->
        let ty = inferLiteralType lit
        Ok (mkTyped (TExprLit lit) ty)
    
    | ExprVar name ->
        match lookupVar name env with
        | Some info ->
            Ok (mkTyped (TExprVar (name, info.VarId, info.Identity)) info.Type)
        | None ->
            Error (UnboundVariable name)
    
    | ExprBinOp (mode, op, left, right) ->
        inferExpr env left |> Result.bind (fun tLeft ->
        inferExpr env right |> Result.bind (fun tRight ->
            let resultType = inferBinOpType mode op tLeft.Type tRight.Type
            Ok (mkTyped (TExprBinOp (mode, op, tLeft, tRight)) resultType)))
    
    | ExprUnaryOp (op, operand) ->
        inferExpr env operand |> Result.bind (fun tOperand ->
            let resultType = inferUnaryOpType op tOperand.Type
            Ok (mkTyped (TExprUnaryOp (op, tOperand)) resultType))
    
    | ExprApp (func, args) ->
        inferExpr env func |> Result.bind (fun tFunc ->
        args |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArgs ->
            let resultType = inferAppType tFunc.Type (tArgs |> List.map (fun a -> a.Type))
            // Check if this is array indexing
            match tFunc.Type with
            | IRTArray arrTy when tArgs.Length = arrTy.IndexTypes.Length ->
                let identity = 
                    match tFunc.Kind with
                    | TExprVar (_, _, id) -> id
                    | _ -> None
                Ok (mkTyped (TExprIndex (tFunc, tArgs, identity)) (IRTScalar arrTy.ElemType))
            | _ ->
                Ok (mkTyped (TExprApp (tFunc, tArgs)) resultType)))
    
    | ExprIf (cond, thenBr, elseBr) ->
        inferExpr env cond |> Result.bind (fun tCond ->
        inferExpr env thenBr |> Result.bind (fun tThen ->
        inferExpr env elseBr |> Result.bind (fun tElse ->
            Ok (mkTyped (TExprIf (tCond, tThen, tElse)) tThen.Type))))
    
    | ExprTuple exprs ->
        exprs |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tExprs ->
            let ty = IRTTuple (tExprs |> List.map (fun e -> e.Type))
            Ok (mkTyped (TExprTuple tExprs) ty))
    
    | ExprArrayLit elems ->
        elems |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tElems ->
            let arrTy = inferArrayLitType tElems
            Ok (mkTyped (TExprArrayLit (tElems, arrTy)) (IRTArray arrTy)))
    
    | ExprLambda (parms, whereClause, body) ->
        inferLambda env parms whereClause body
    
    | ExprLet (binding, body) ->
        inferLetBinding env binding body
    
    | ExprMatch (scrutinee, cases) ->
        inferMatch env scrutinee cases
    
    | ExprMethodFor arrays ->
        inferMethodFor env arrays
    
    | ExprObjectFor kernel ->
        inferObjectFor env kernel
    
    | ExprReynolds (kernel, isAntisym) ->
        inferExpr env kernel |> Result.bind (fun tKernel ->
            Ok (mkTyped (TExprReynolds (tKernel, isAntisym)) tKernel.Type))
    
    | ExprRank e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprRank tE) (IRTScalar ETInt64)))
    
    | ExprArity ->
        Ok (mkTyped TExprArity (IRTScalar ETInt64))
    
    | ExprPure e ->
        inferExpr env e |> Result.bind (fun tE ->
            Ok (mkTyped (TExprPure tE) (IRTComputation tE.Type)))
    
    | ExprCompute e ->
        inferExpr env e |> Result.bind (fun tE ->
            let innerTy = match tE.Type with IRTComputation t -> t | t -> t
            Ok (mkTyped (TExprCompute tE) innerTy))
    
    | ExprGuard (cond, body) ->
        inferExpr env cond |> Result.bind (fun tCond ->
        inferExpr env body |> Result.bind (fun tBody ->
            Ok (mkTyped (TExprGuard (tCond, tBody)) tBody.Type)))
    
    | ExprStruct (name, fields) ->
        let inferField (fname: Ident, e: Expr) =
            inferExpr env e |> Result.map (fun te -> (fname, te))
        fields |> List.map inferField
        |> sequenceResults |> Result.bind (fun tFields ->
            let ty = IRTTuple (tFields |> List.map (fun (_, e) -> e.Type))
            Ok (mkTyped (TExprStruct (name, tFields)) ty))
    
    | ExprQualified names ->
        Ok (mkTyped (TExprQualified names) IRTUnit)
    
    | _ ->
        // Fallback for unhandled cases
        Ok (mkTyped (TExprLit (LitInt 0L)) IRTUnit)

/// Infer type of a literal
and inferLiteralType lit =
    match lit with
    | LitInt _ -> IRTScalar ETInt64
    | LitFloat _ -> IRTScalar ETFloat64
    | LitBool _ -> IRTScalar ETBool
    | LitString _ -> IRTScalar ETInt64
    | LitChar _ -> IRTScalar ETInt32
    | LitUnit -> IRTUnit

/// Infer result type of binary operation
and inferBinOpType mode op leftTy rightTy =
    match op with
    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe -> IRTScalar ETBool
    | OpAnd | OpOr -> IRTScalar ETBool
    | _ ->
        // For arithmetic, result type follows left operand
        match mode with
        | Outer ->
            // Outer product increases rank
            match leftTy, rightTy with
            | IRTArray arrL, IRTArray arrR ->
                IRTArray { arrL with IndexTypes = arrL.IndexTypes @ arrR.IndexTypes }
            | _ -> leftTy
        | Elementwise -> leftTy

/// Infer result type of unary operation
and inferUnaryOpType op operandTy =
    match op with
    | OpNot -> IRTScalar ETBool
    | OpNeg -> operandTy

/// Infer result type of function application
and inferAppType funcTy argTypes =
    match funcTy with
    | IRTFunc (_, retTy) -> retTy
    | IRTArray arrTy -> IRTScalar arrTy.ElemType  // Array indexing
    | _ -> IRTUnit

/// Infer lambda expression
and inferLambda env parms whereClause body =
    let scopeEnv = enterScope env
    
    // Extract commutativity groups
    let commGroups = extractCommGroups parms whereClause
    
    // Bind parameters
    let mutable paramEnv = scopeEnv
    let typedParams = parms |> List.mapi (fun i p ->
        let varId = env.Builder.FreshId()
        let ty = match p.Type with 
                 | Some t -> lowerTypeExpr env t 
                 | None -> IRTScalar ETFloat64
        paramEnv <- bindVarSimple p.Name varId ty paramEnv
        { Name = p.Name; Type = ty; Index = i; VarId = varId } : TypedParam)
    
    // Collect captures
    let freeVars = collectFreeVars paramEnv body
    let captures = buildCaptures scopeEnv freeVars
    
    // Validate no array captures
    validateNoArrayCaptures env captures |> Result.bind (fun () ->
    
    // Infer body
    inferExpr paramEnv body |> Result.bind (fun tBody ->
        let lambdaInfo : TypedLambdaInfo = {
            Params = typedParams
            Body = tBody
            ReturnType = tBody.Type
            CommGroups = commGroups
            Captures = captures
            IsCommutative = not (List.isEmpty commGroups)
        }
        let funcTy = IRTFunc (typedParams |> List.map (fun p -> p.Type), tBody.Type)
        Ok (mkTyped (TExprLambda lambdaInfo) funcTy)))

/// Infer let binding
and inferLetBinding env binding body =
    inferExpr env binding.Value |> Result.bind (fun tValue ->
        let varId = env.Builder.FreshId()
        let identity = 
            match binding.Pattern with
            | PatVar name -> Some (AIDVariable name)
            | _ -> None
        let env' = 
            match binding.Pattern, identity with
            | PatVar name, Some id -> bindVarWithIdentity name varId tValue.Type id env
            | PatVar name, None -> bindVarSimple name varId tValue.Type env
            | _ -> env
        inferExpr env' body |> Result.bind (fun tBody ->
            match binding.Pattern with
            | PatVar name ->
                Ok (mkTyped (TExprLet (name, varId, tValue, tBody)) tBody.Type)
            | _ ->
                // Complex pattern - simplified handling
                Ok (mkTyped (TExprLet ("_", varId, tValue, tBody)) tBody.Type)))

/// Infer match expression
and inferMatch env scrutinee cases =
    inferExpr env scrutinee |> Result.bind (fun tScrutinee ->
        cases |> List.map (fun case ->
            // Simplified: just infer body type
            inferExpr env case.Body |> Result.map (fun tBody ->
                { Pattern = { Kind = TPatWild; Type = tScrutinee.Type; Bindings = [] }
                  Guard = None
                  Body = tBody } : TypedMatchCase))
        |> sequenceResults |> Result.bind (fun tCases ->
            let resultTy = if tCases.IsEmpty then IRTUnit else tCases.[0].Body.Type
            Ok (mkTyped (TExprMatch (tScrutinee, tCases)) resultTy)))

/// Infer method_for expression
and inferMethodFor env arrays =
    arrays |> List.map (inferExpr env) |> sequenceResults |> Result.bind (fun tArrays ->
        let identities = arrays |> List.map (fun arr ->
            match arr with
            | ExprVar name -> AIDVariable name
            | _ -> AIDLiteral (env.Builder.FreshId()))
        let arrayTypes = arrays |> List.map (getArrayType env)
        let sDimsPerArray = computeSDimsPerArray arrayTypes
        let totalSDims = List.sum sDimsPerArray
        
        let info : TypedMethodForInfo = {
            Arrays = tArrays
            Identities = identities
            ArrayTypes = arrayTypes
            SDimsPerArray = sDimsPerArray
            TotalSDims = totalSDims
        }
        
        let loopTy = IRTLoop { 
            Kind = LKMethod
            Arity = Some arrays.Length
            ArrayTypes = arrayTypes |> List.map IRTArray
            KernelType = None 
        }
        Ok (mkTyped (TExprMethodFor info) loopTy))

/// Infer object_for expression
and inferObjectFor env kernel =
    inferExpr env kernel |> Result.bind (fun tKernel ->
        let (commGroups, inputRanks, outputRank) =
            match tKernel.Kind with
            | TExprLambda info ->
                let iRanks = info.Params |> List.map (fun p ->
                    match p.Type with
                    | IRTArray arr -> arr.IndexTypes.Length
                    | _ -> 0)
                (info.CommGroups, iRanks, 0)
            | _ -> ([], [], 0)
        
        let info : TypedObjectForInfo = {
            Kernel = tKernel
            CommGroups = commGroups
            InputRanks = inputRanks
            OutputRank = outputRank
        }
        
        let loopTy = IRTLoop {
            Kind = LKObject
            Arity = Some inputRanks.Length
            ArrayTypes = []
            KernelType = Some tKernel.Type
        }
        Ok (mkTyped (TExprObjectFor info) loopTy))

// ============================================================================
// Declaration Type Checking
// ============================================================================

/// Type check a declaration
let checkDecl (env: TypeEnv) (decl: Decl) : TypeResult<TypedDecl * TypeEnv> =
    match decl with
    | DeclLet binding ->
        inferExpr env binding.Value |> Result.bind (fun tValue ->
            let varId = env.Builder.FreshId()
            let identity = 
                match binding.Pattern with
                | PatVar name -> Some (AIDVariable name)
                | _ -> None
            let env' = 
                match binding.Pattern, identity with
                | PatVar name, Some id -> bindVarWithIdentity name varId tValue.Type id env
                | PatVar name, None -> bindVarSimple name varId tValue.Type env
                | _ -> env
            let isMutable = match binding.Mutability with BindMut -> true | _ -> false
            let typedBinding : TypedBinding = {
                Name = match binding.Pattern with PatVar n -> n | _ -> "_"
                VarId = varId
                Type = tValue.Type
                Identity = identity
                IsMutable = isMutable
                Value = tValue
            }
            Ok (TDeclLet typedBinding, env'))
    
    | DeclFunction funcDecl ->
        // Simplified function checking
        let env' = env  // Would add function to env
        Ok (TDeclType (TyDeclAlias ("_", [], TyUnit)), env')
    
    | DeclType typeDecl ->
        Ok (TDeclType typeDecl, env)
    
    | DeclInterface ifaceDecl ->
        Ok (TDeclInterface ifaceDecl, env)
    
    | DeclImpl implDecl ->
        Ok (TDeclImpl implDecl, env)
    
    | _ ->
        Ok (TDeclType (TyDeclAlias ("_", [], TyUnit)), env)

/// Type check a module
let checkModule (env: TypeEnv) (modul: ModuleDecl) : TypeResult<TypedModule> =
    let rec checkDecls env (decls: Located<Decl> list) acc =
        match decls with
        | [] -> Ok (List.rev acc, env)
        | d :: rest ->
            checkDecl env d.Value |> Result.bind (fun (td, env') ->
                checkDecls env' rest (td :: acc))
    
    checkDecls env modul.Decls [] |> Result.map (fun (tDecls, _) ->
        { Name = Some modul.Name; Decls = tDecls })

/// Type check a program
let checkProgram (program: Program) : TypeResult<TypedProgram> =
    let env = emptyEnv ()
    program.Modules |> List.map (checkModule env) |> sequenceResults 
    |> Result.map (fun tModules -> { Modules = tModules })

// ============================================================================
// Convenience Entry Point
// ============================================================================

/// Type check and return typed AST, or error
let typeCheck (program: Program) : TypeResult<TypedProgram> =
    checkProgram program
