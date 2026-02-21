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
open Blade.TypedAst

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

// ============================================================================
// TypedAST-based Lowering (New Pipeline)
// ============================================================================
//
// These functions translate from TypedAST to IR. Since type checking has
// already been done, this is a straightforward translation without inference.

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
    /// Provider alias -> qualified provider name (e.g. "NetCDF" -> ["Providers"; "NetCDF"])
    ProviderAliases: Map<string, string list>
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
    ProviderAliases = Map.empty
}

let bindTypedVar name id (env: TypedLowerEnv) : TypedLowerEnv =
    { env with Variables = Map.add name id env.Variables }

/// Lower a TypedExpr to IRExpr
let rec lowerTypedExpr (env: TypedLowerEnv) (texpr: TypedExpr) : IRExpr =
    match texpr.Kind with
    | TExprLit lit ->
        IRLit (lowerLiteralToIRLit lit)
    
    | TExprVar (name, varId, identity) ->
        // Variant constructors without payload (e.g. North) have type IRTNamed
        // Variant constructors with payload (e.g. Some) have type IRTFunc([_], IRTNamed _)
        // Neither are bound in the lowering environment. Emit the name verbatim.
        let isVariantCtor =
            not (Map.containsKey name env.Variables) &&
            not (Map.containsKey name env.Functions) &&
            match texpr.Type with
            | IRTNamed _ -> true
            | IRTFunc (_, IRTNamed _) -> true
            | _ -> false
        if isVariantCtor then IRParam (name, 0, texpr.Type)
        else IRVar (varId, texpr.Type)
    
    | TExprQualified names ->
        IRParam (String.concat "." names, 0, texpr.Type)
    
    | TExprBinOp (mode, op, left, right) ->
        let l = lowerTypedExpr env left
        let r = lowerTypedExpr env right
        lowerTypedBinOp env mode op l r left right texpr.Type
    
    | TExprUnaryOp (op, operand) ->
        let e = lowerTypedExpr env operand
        match op with
        | OpNeg -> IRUnaryOp (IRNeg, e)
        | OpNot -> IRUnaryOp (IRNot, e)
    
    | TExprApp (func, args) ->
        let f = lowerTypedExpr env func
        let as' = args |> List.map (lowerTypedExpr env)
        IRApp (f, as', texpr.Type)
    
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
            SharedIndexType = info.SharedIndexType
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
            Arrays = info.Arrays |> List.map (lowerTypedExpr env)
            Identities = info.Identities
            ArrayTypes = info.ArrayTypes
            SharedIndexType = info.SharedIndexType
            SymcomStates = info.SymcomStates
            TriangularLevels = info.TriangularLevels
            SDimsPerArray = info.SDimsPerArray
            KernelInputRanks = info.KernelInputRanks
            KernelOutputRank = info.KernelOutputRank
            SpeedupFactor = info.SpeedupFactor
            ReynoldsSpeedup = info.ReynoldsSpeedup
            HasReynolds = info.HasReynolds
            OutputType = info.OutputType
            IsCoIteration = info.IsCoIteration
        }
    
    | TExprBind (l, r) ->
        IRBind (lowerTypedExpr env l, lowerTypedExpr env r)
    
    | TExprParallel (l, r) ->
        IRParallel (lowerTypedExpr env l, lowerTypedExpr env r, None)
    
    | TExprFusion (l, r) ->
        IRFusion (lowerTypedExpr env l, lowerTypedExpr env r)
    
    | TExprFunctorMap (f, c) ->
        IRFunctorMap (lowerTypedExpr env f, lowerTypedExpr env c)
    
    | TExprChoice (l, r) ->
        IRChoice (lowerTypedExpr env l, lowerTypedExpr env r)
    
    | TExprCompose (op, l, r) ->
        let lIR = lowerTypedExpr env l
        let rIR = lowerTypedExpr env r
        match op with
        | OpComposeObj -> IRComposeObj (lIR, rIR)
        | OpComposeMeth -> IRComposeMeth (lIR, rIR)
        | _ -> IRCompose (lIR, rIR)
    
    | TExprRange indexType ->
        IRRange (indexType, None)

    | TExprDotDot (lo, hi) ->
        let loIR = lowerTypedExpr env lo
        let hiIR = lowerTypedExpr env hi
        let extentExpr = IRBinOp (IRElementwise, IRSub, hiIR, loIR)
        let idx = {
            Id = env.Builder.FreshId()
            Arity = 1
            Extent = extentExpr
            Symmetry = SymNone
            Tag = Some "__anon"
            Kind = SDimension
            Dependencies = []
        }
        let offset =
            match loIR with
            | IRLit (IRLitInt 0L) -> None
            | _ -> Some loIR
        IRRange (idx, offset)
    
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
    
    | TExprZero ->
        // Lower to type-appropriate zero literal based on resolved type
        match texpr.Type with
        | IRTScalar ETInt32 | IRTScalar ETInt64 -> IRLit (IRLitInt 0L)
        | IRTScalar ETBool -> IRLit (IRLitBool false)
        | IRTScalar ETFloat32 | IRTScalar ETFloat64 -> IRLit (IRLitFloat 0.0)
        | IRTInfer _ -> IRLit (IRLitFloat 0.0)  // unresolved defaults to float
        | _ -> IRZero  // fallback
    
    | TExprReynolds (kernel, isAntisym) ->
        IRReynolds (lowerTypedExpr env kernel, isAntisym)
    
    | TExprArity paramName ->
        IRArity (None, paramName)
    
    | TExprRank e ->
        // Resolve rank statically from the typed expression's type
        let rank = match e.Type with
                   | IRTArray at -> at.IndexTypes |> List.sumBy (fun idx -> idx.Arity)
                   | _ -> 0
        IRLit (IRLitInt (int64 rank))
    
    | TExprStruct (typeName, fields) ->
        IRStructLit (typeName, fields |> List.map (fun (fname, e) -> fname, lowerTypedExpr env e))
    
    | TExprBlock (stmts, finalExpr) ->
        lowerTypedBlock env stmts finalExpr
    
    | TExprAssign (lhs, rhs) ->
        IRAssign (lowerTypedExpr env lhs, lowerTypedExpr env rhs)
    
    | TExprSequence exprs ->
        // sequence(c1, c2, ..., cn) → IRSequence (flat n-ary parallel)
        let lowered = exprs |> List.map (lowerTypedExpr env)
        match lowered with
        | [] -> IRLit IRLitUnit
        | [single] -> single
        | _ -> IRSequence lowered
    
    | TExprReplicate (count, body) ->
        let loweredBody = lowerTypedExpr env body
        let n =
            match count.Kind with
            | TExprLit (LitInt v) -> int v
            | _ -> 1  // fallback (TypeCheck should have caught this)
        IRSequence (List.replicate n loweredBody)
    
    | TExprAlign (exprs, specOpt) ->
        let spec =
            match specOpt with
            | Some s -> { IR.AlignSpec.Offsets = s.Offsets; Boundary = lowerBndMode s.Boundary }
            | None -> { IR.AlignSpec.Offsets = []; Boundary = IR.BndShrink }
        IRAlign (exprs |> List.map (lowerTypedExpr env), spec)
    
    | TExprSection op ->
        lowerTypedSection env op
    
    | TExprPartialApp (op, arg, isLeft) ->
        lowerTypedPartialApp env op (lowerTypedExpr env arg) isLeft

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

/// Lower a typed block into nested IRLet
and lowerTypedBlock env (stmts: TypedStmt list) (finalExpr: TypedExpr option) : IRExpr =
    match stmts, finalExpr with
    | [], Some e -> lowerTypedExpr env e
    | [], None -> IRLit IRLitUnit
    | stmt :: rest, _ ->
        match stmt with
        | TStmtLet binding ->
            let value = lowerTypedExpr env binding.Value
            let env' = bindTypedVar binding.Name binding.VarId env
            let body = lowerTypedBlock env' rest finalExpr
            IRLet (binding.VarId, value, body)
        | TStmtAssign (lhs, rhs) ->
            let target = lowerTypedExpr env lhs
            let value = lowerTypedExpr env rhs
            let rest' = lowerTypedBlock env rest finalExpr
            let dummyId = env.Builder.FreshId()
            IRLet (dummyId, IRAssign (target, value), rest')
        | TStmtExpr e ->
            let lowered = lowerTypedExpr env e
            let rest' = lowerTypedBlock env rest finalExpr
            // Wrap in IRLet with a dummy id to preserve side effects
            let dummyId = env.Builder.FreshId()
            IRLet (dummyId, lowered, rest')
        | TStmtForIn (varName, varId, lo, hi, bodyStmts) ->
            let loIR = lowerTypedExpr env lo
            let hiIR = lowerTypedExpr env hi
            let innerEnv = bindTypedVar varName varId env
            let bodyIR = lowerTypedBlock innerEnv bodyStmts None
            let rest' = lowerTypedBlock env rest finalExpr
            let dummyId = env.Builder.FreshId()
            IRLet (dummyId, IRForRange (varId, loIR, hiIR, bodyIR), rest')

/// Convert AST boundary mode to IR boundary mode
and lowerBndMode (mode: Ast.BoundaryMode) : IR.BoundaryMode =
    match mode with
    | Ast.BndShrink -> IR.BndShrink
    | Ast.BndPad _ -> IR.BndPad (IRLit (IRLitInt 0L))
    | Ast.BndPeriodic -> IR.BndPeriodic
    | Ast.BndReflect -> IR.BndReflect

/// Lower a sectioned operator to a lambda
and lowerTypedSection env (op: BinOp) : IRExpr =
    let aId = env.Builder.FreshId()
    let bId = env.Builder.FreshId()
    let (irOp, isComm) =
        match op with
        | OpAdd -> (IRAdd, true) | OpSub -> (IRSub, false)
        | OpMul -> (IRMul, true) | OpDiv -> (IRDiv, false)
        | OpMod -> (IRMod, false) | OpCaret -> (IRCaret, false)
        | OpEq -> (IREq, true) | OpNeq -> (IRNeq, true)
        | OpLt -> (IRLt, false) | OpLe -> (IRLe, false)
        | OpGt -> (IRGt, false) | OpGe -> (IRGe, false)
        | OpAnd -> (IRAnd, true) | OpOr -> (IROr, true)
        | _ -> (IRAdd, true)
    let body = IRBinOp(IRElementwise, irOp, IRVar (aId, IRTScalar ETFloat64), IRVar (bId, IRTScalar ETFloat64))
    IRLambda {
        Params = [{ Name = "a"; Type = IRTScalar ETFloat64; Index = 0; VarId = aId }
                  { Name = "b"; Type = IRTScalar ETFloat64; Index = 1; VarId = bId }]
        Body = body; Captures = []
        IsCommutative = isComm
        CommGroups = if isComm then [[0; 1]] else []
    }

/// Lower a partial operator application to a lambda
and lowerTypedPartialApp env (op: BinOp) (argExpr: IRExpr) (isLeft: bool) : IRExpr =
    let paramId = env.Builder.FreshId()
    let irOp =
        match op with
        | OpAdd -> IRAdd | OpSub -> IRSub
        | OpMul -> IRMul | OpDiv -> IRDiv
        | OpMod -> IRMod | OpCaret -> IRCaret
        | OpEq -> IREq | OpNeq -> IRNeq
        | OpLt -> IRLt | OpLe -> IRLe
        | OpGt -> IRGt | OpGe -> IRGe
        | OpAnd -> IRAnd | OpOr -> IROr
        | _ -> IRAdd
    let body =
        if isLeft then IRBinOp (IRElementwise, irOp, argExpr, IRVar (paramId, IRTScalar ETFloat64))
        else IRBinOp (IRElementwise, irOp, IRVar (paramId, IRTScalar ETFloat64), argExpr)
    IRLambda {
        Params = [{ Name = "x"; Type = IRTScalar ETFloat64; Index = 0; VarId = paramId }]
        Body = body; Captures = []
        IsCommutative = false; CommGroups = []
    }

/// Lower typed binary operations
and lowerTypedBinOp env mode op l r leftExpr rightExpr resultType =
    let irMode = match mode with Elementwise -> IRElementwise | Outer -> IROuter
    
    // Check if both operands are arrays — if so, synthesize object_for loop
    let isArithOp = match op with
                    | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
                    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
                    | OpAnd | OpOr -> true
                    | _ -> false
    let leftIsArray = match leftExpr.Type with IRTArray _ -> true | _ -> false
    let rightIsArray = match rightExpr.Type with IRTArray _ -> true | _ -> false
    
    if isArithOp && leftIsArray && rightIsArray then
        // Synthesize: object_for(lambda(x, y) -> x [op] y)(A, B)
        let irOp = match op with
                   | OpAdd -> IRAdd | OpSub -> IRSub | OpMul -> IRMul
                   | OpDiv -> IRDiv | OpMod -> IRMod | OpCaret -> IRCaret
                   | OpEq -> IREq | OpNeq -> IRNeq
                   | OpLt -> IRLt | OpLe -> IRLe | OpGt -> IRGt | OpGe -> IRGe
                   | OpAnd -> IRAnd | OpOr -> IROr | _ -> IRAdd
        let elemTypeL = match leftExpr.Type with IRTArray a -> a.ElemType | _ -> ETFloat64
        let elemTypeR = match rightExpr.Type with IRTArray a -> a.ElemType | _ -> ETFloat64
        let aId = env.Builder.FreshId()
        let bId = env.Builder.FreshId()
        let body = IRBinOp(IRElementwise, irOp, IRVar (aId, IRTScalar elemTypeL), IRVar (bId, IRTScalar elemTypeR))
        let lambdaInfo : LambdaInfo = {
            Params = [
                { Name = "__a"; Type = IRTScalar elemTypeL; Index = 0; VarId = aId }
                { Name = "__b"; Type = IRTScalar elemTypeR; Index = 1; VarId = bId }
            ]
            Body = body; Captures = []
            IsCommutative = false
            CommGroups = (if mode = Elementwise then [[0; 1]] else [])
        }
        let inputRanks = match mode with Outer -> [1; 1] | Elementwise -> [0; 0]
        let objInfo : ObjectForInfo = {
            Kernel = IRLambda lambdaInfo
            CommGroups = (if mode = Elementwise then [[0; 1]] else [])
            InputRanks = inputRanks
            OutputRank = 0
        }
        IRApp(IRObjectFor objInfo, [IRTuple [l; r]], resultType)
    else
    
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
            Arrays = []; Identities = []; ArrayTypes = []; SharedIndexType = None
            SymcomStates = []
            TriangularLevels = []
            SDimsPerArray = []
            KernelInputRanks = []
            KernelOutputRank = 0
            SpeedupFactor = 1L
            ReynoldsSpeedup = 1L
            HasReynolds = false
            OutputType = IRTUnit
            IsCoIteration = false
        }
    
    | OpBind -> IRBind (l, r)
    | OpParallel -> IRParallel (l, r, None)
    | OpFusion -> IRFusion (l, r)
    | OpArrayProd ->
        // <*> : merge two method_for array lists into one
        match l, r with
        | IRMethodFor m1, IRMethodFor m2 ->
            IRMethodFor {
                Arrays = m1.Arrays @ m2.Arrays
                Identities = m1.Identities @ m2.Identities
                ArrayTypes = m1.ArrayTypes @ m2.ArrayTypes
                SDimsPerArray = m1.SDimsPerArray @ m2.SDimsPerArray
                TotalSDims = m1.TotalSDims + m2.TotalSDims
                SharedIndexType = None
            }
        | _ -> IRArrayProduct (l, r)  // fallback for non-method_for operands
    | OpFunctor -> IRFunctorMap (l, r)
    | OpChoice -> IRChoice (l, r)
    | OpFallback -> IRChoice (l, r)
    | OpComposeObj -> IRComposeObj (l, r)
    | OpComposeMeth -> IRComposeMeth (l, r)
    | OpCompose -> IRCompose (l, r)
    | OpCons -> IRTupleCons (l, r)

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

/// Lower a TypedTypeDef to IRTypeDef (types already resolved by TypeCheck)
let lowerTypedTypeDef (env: TypedLowerEnv) (ttd: TypedTypeDef) : IRTypeDef =
    match ttd with
    | TTDAlias (name, _, resolved) ->
        IRTDAlias (name, resolved)
    | TTDStruct (name, _, fields, invariant) ->
        let constraintInfo =
            match invariant with
            | Some texpr ->
                let loweredExpr = lowerTypedExpr env texpr
                // Extract field name -> VarId from the typed expression
                // The type checker bound field names to VarIds; those ids persist in the lowered IR
                let fieldNames = fields |> List.map fst |> Set.ofList
                let rec collectTypedVars (te: TypedExpr) : (string * IRId) list =
                    match te.Kind with
                    | TExprVar (name, varId, _) when Set.contains name fieldNames -> [(name, varId)]
                    | TExprBinOp (_, _, l, r) -> collectTypedVars l @ collectTypedVars r
                    | TExprUnaryOp (_, e) -> collectTypedVars e
                    | TExprIf (c, t, e) -> collectTypedVars c @ collectTypedVars t @ collectTypedVars e
                    | TExprApp (f, args) -> collectTypedVars f @ (args |> List.collect collectTypedVars)
                    | _ -> []
                let fieldBindings = collectTypedVars texpr |> List.distinctBy fst
                Some { Expr = loweredExpr; FieldBindings = fieldBindings }
            | None -> None
        IRTDStruct (name, fields, constraintInfo)
    | TTDVariant (name, _, variants) ->
        IRTDVariant (name, variants)

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

/// Lower a typed declaration (may produce multiple bindings for destructuring)
let lowerTypedDecl (env: TypedLowerEnv) (decl: TypedDecl) : (Choice<IRFuncDef, IRBinding, IRTypeDef> list * TypedLowerEnv) =
    match decl with
    | TDeclLet binding ->
        let (irBinding, env') = lowerTypedBinding env binding
        // Emit sub-bindings for destructured patterns (tuple, cons, struct)
        let isStruct = match binding.Type with IRTNamed _ -> true | _ -> false
        // Determine if this is a flat destructuring (pattern count = flat leaf count != structural count)
        let isFlat =
            match binding.Type with
            | IRTTuple ts ->
                let structCount = ts.Length
                let flatCount = IR.flattenTupleLeaves binding.Type |> List.length
                binding.SubBindings.Length = flatCount && binding.SubBindings.Length <> structCount
            | _ -> false
        let subIRBindings = binding.SubBindings |> List.mapi (fun i (name, subId, subTy) ->
            let projExpr = 
                if isStruct then IRFieldAccess (IRVar (binding.VarId, binding.Type), name)
                else IRTupleProj (IRVar (binding.VarId, binding.Type), i, isFlat)
            let env' = bindTypedVar name subId env'
            { Id = subId; Name = name; Type = subTy; Value = projExpr; IsConst = true; IsMutable = false })
        let env'' = binding.SubBindings |> List.fold (fun e (name, subId, _) -> bindTypedVar name subId e) env'
        ([Choice2Of3 irBinding] @ (subIRBindings |> List.map Choice2Of3), env'')
    
    | TDeclFunction funcDecl ->
        let (funcDef, env') = lowerTypedFuncDecl env funcDecl
        ([Choice1Of3 funcDef], env')
    
    | TDeclStatic binding ->
        // Static values: use pre-evaluated value if available, else lower normally
        match Map.tryFind binding.Name env.StaticValues with
        | Some sv ->
            let irValue = staticValueToIR sv
            let ty = match sv with
                     | StaticEval.SVInt _ -> IRTScalar ETInt64
                     | StaticEval.SVFloat _ -> IRTScalar ETFloat64
                     | StaticEval.SVBool _ -> IRTScalar ETBool
                     | _ -> IRTUnit
            let bd = { Id = binding.VarId; Name = binding.Name; Type = ty; Value = irValue; IsConst = true; IsMutable = false }
            let env' = bindTypedVar binding.Name binding.VarId env
            ([Choice2Of3 bd], env')
        | None ->
            // Fallback: lower as normal binding
            let (irBinding, env') = lowerTypedBinding env binding
            ([Choice2Of3 irBinding], env')
    
    | TDeclType ttd ->
        let irTd = lowerTypedTypeDef env ttd
        ([Choice3Of3 irTd], env)
    
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
        ([], env')
    
    | TDeclImport _ ->
        // Handled specially in lowerTypedModule (needs module export threading)
        ([], env)
    
    | TDeclInterface ifaceDecl ->
        let env' = { env with Interfaces = Map.add ifaceDecl.Name ifaceDecl env.Interfaces }
        ([], env')
    
    | TDeclImpl timpl ->
        // Methods are already type-checked; lower each as a function
        // Handled in lowerTypedModule for proper function list accumulation
        ([], env)

/// Check if a typed expression is a provider call (e.g. NetCDF.load("path"))
let isProviderCall (env: TypedLowerEnv) (texpr: TypedExpr) : bool =
    match texpr.Kind with
    | TExprApp ({ Kind = TExprField ({ Kind = TExprVar (alias, _, _) }, "load", _) }, [arg]) ->
        Map.containsKey alias env.ProviderAliases
        && (match arg.Kind with TExprLit (LitString _) -> true | _ -> false)
    | _ -> false

/// Try to invoke a provider for a binding value. Returns types, binding, and updated env.
let tryInvokeProvider (env: TypedLowerEnv) (binding: TypedBinding) : (IRTypeDef list * IRBinding * TypedLowerEnv) option =
    match binding.Value.Kind with
    | TExprApp ({ Kind = TExprField ({ Kind = TExprVar (alias, _, _) }, "load", _) }, [arg]) ->
        match Map.tryFind alias env.ProviderAliases, arg.Kind with
        | Some qname, TExprLit (LitString path) when qname = ["Providers"; "NetCDF"] ->
            let providerModule = Blade.NetcdfProvider.loadAsModule env.Builder binding.Name path
            // The binding value becomes unit (types are injected separately)
            let bd = {
                Id = binding.VarId
                Name = binding.Name
                Type = IRTUnit
                Value = IRLit IRLitUnit
                IsConst = true
                IsMutable = false
            }
            let env' = bindTypedVar binding.Name binding.VarId env
            Some (providerModule.Types, bd, env')
        | _ -> None
    | _ -> None

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
                    // Check if this is a provider import (e.g. Providers.NetCDF)
                    if qname.Length >= 2 && qname.[0] = "Providers" then
                        currentEnv <- { currentEnv with ProviderAliases = Map.add alias qname currentEnv.ProviderAliases }
                    else
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
        
        // Handle impl blocks: methods already type-checked as TypedFunctionDecl
        | TDeclImpl timpl ->
            for method in timpl.Methods do
                let (fd, env') = lowerTypedFuncDecl currentEnv method
                funcs <- funcs @ [fd]
                currentEnv <- env'
                currentEnv <- { currentEnv with
                                    Functions = Map.add method.Name fd.Id currentEnv.Functions
                                    ImplMethods = Map.add (timpl.TypeName, method.Name) fd.Id currentEnv.ImplMethods }
                currentEnv <- bindTypedVar method.Name fd.Id currentEnv
        
        // All other declarations go through lowerTypedDecl
        // But first check for provider calls (e.g. let sample = NetCDF.load("sample.nc"))
        | TDeclLet binding when isProviderCall currentEnv binding.Value ->
            match tryInvokeProvider currentEnv binding with
            | Some (providerTypes, bd, env') ->
                types <- types @ providerTypes
                for td in providerTypes do
                    match td with
                    | IRTDStruct (name, fields, _) ->
                        currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                    | _ -> ()
                bindings <- bindings @ [bd]
                currentEnv <- env'
            | None ->
                // Provider invocation failed — fall through to normal lowering
                let (items, env') = lowerTypedDecl currentEnv (TDeclLet binding)
                currentEnv <- env'
                for item in items do
                    match item with
                    | Choice1Of3 fd ->
                        funcs <- funcs @ [fd]
                        currentEnv <- { currentEnv with Functions = Map.add fd.Name fd.Id currentEnv.Functions }
                        currentEnv <- bindTypedVar fd.Name fd.Id currentEnv
                    | Choice2Of3 bd -> bindings <- bindings @ [bd]
                    | Choice3Of3 td ->
                        types <- types @ [td]
                        match td with
                        | IRTDStruct (name, fields, _) ->
                            currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                        | _ -> ()

        | _ ->
            let (items, env') = lowerTypedDecl currentEnv decl
            currentEnv <- env'
            for item in items do
                match item with
                | Choice1Of3 fd ->
                    funcs <- funcs @ [fd]
                    currentEnv <- { currentEnv with Functions = Map.add fd.Name fd.Id currentEnv.Functions }
                    currentEnv <- bindTypedVar fd.Name fd.Id currentEnv
                | Choice2Of3 bd ->
                    bindings <- bindings @ [bd]
                | Choice3Of3 td ->
                    types <- types @ [td]
                    match td with
                    | IRTDStruct (name, fields, _) ->
                        currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                    | _ -> ()
    
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
let lowerTypedProgram (program: TypedProgram) (rawProgram: Program option) (builder: IRBuilder) : IRProgram =
    let env = { emptyTypedEnv() with Builder = builder }
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
        // Monomorphize arity-polymorphic functions before codegen
        let irModule = IR.monomorphizeModule irModule env.Builder
        currentExports <- Map.add moduleName exports currentExports
        irModules <- irModules @ [irModule]
    
    { Modules = irModules }

// ============================================================================
// Convenience functions for testing
// ============================================================================

/// Main pipeline: Parse -> TypeCheck -> Lower
let lower (source: string) : Result<IRProgram, string> =
    match Blade.Parser.parseProgram source with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder) -> Ok (lowerTypedProgram typedProgram (Some program) builder)
        | Error errors -> 
            let msgs = errors |> List.map Blade.TypeCheck.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// Lower multiple source files into a single IR program with cross-module imports
let lowerMultiSource (sources: (string * string) list) : Result<IRProgram, string> =
    match Blade.Parser.parseMultiSource sources with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder) -> Ok (lowerTypedProgram typedProgram (Some program) builder)
        | Error errors ->
            let msgs = errors |> List.map Blade.TypeCheck.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// Alias for backward compatibility
let lowerWithTypeCheck = lower
