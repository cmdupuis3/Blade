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
open Blade.Types
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
    UnitDefs: Map<string, UnitSig>
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

// (The duplicated resolveUnitExpr that lived here is gone — audit Phase 0.3.
// The one definition is TypeEnv.resolveUnitExpr; the single use below
// calls it qualified.)

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
    /// Provider load binding name -> file path literal (e.g. "sample" -> "f.nc").
    /// Recorded at tryInvokeProvider; used at a `view |> read` site to recover
    /// the path by walking the var-reference to its root provider binding.
    ProviderPaths: Map<string, string>
    /// Deferred provider reads accumulated during lowering, keyed by the
    /// receiving binding's IRId. Copied into IRModule.ProviderReads at module
    /// assembly and consumed at codegen.
    ProviderReads: Map<IRId, ProviderReadSpec>
    /// Deferred random-fill constructors accumulated during lowering, keyed by
    /// the receiving binding's IRId. Value is the lowered modulus expr. Copied
    /// into IRModule.RandomInits at module assembly and consumed at codegen.
    RandomInits: Map<IRId, IRExpr>
    /// Deferred compound-construction constructors (compound(dense, mask))
    /// accumulated during lowering, keyed by the receiving binding's IRId.
    /// Value is (loweredDense, loweredMask). Copied into IRModule.CompoundInits
    /// at module assembly and consumed at codegen (P0 index materialization +
    /// scatter). Mirrors RandomInits.
    CompoundInits: Map<IRId, IRExpr * IRExpr>
    /// Lifted lambda callables accumulated during lowering of the current
    /// module. Each lambda-construction site (lowerTypedLambda,
    /// lowerTypedSection, lowerTypedPartialApp, binop-kernel synthesis)
    /// adds its newly-built IRCallable here. At module-assembly time
    /// (end of lowerTypedModule), these are appended to IRModule.Functions
    /// so the lifted lambdas are available alongside source-level
    /// functions for cross-procedural analysis and codegen.
    ///
    /// Mutable shared state — F# record `with` updates share the
    /// underlying ResizeArray, so additions from any nested call
    /// accumulate into the module's single list. Reset per-module
    /// at the start of lowerTypedModule.
    LiftedCallables: ResizeArray<IRCallable>
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
    ProviderPaths = Map.empty
    ProviderReads = Map.empty
    RandomInits = Map.empty
    CompoundInits = Map.empty
    LiftedCallables = ResizeArray<IRCallable>()
}

let bindTypedVar name id (env: TypedLowerEnv) : TypedLowerEnv =
    { env with Variables = Map.add name id env.Variables }

/// Lower a TypedExpr to IRExpr
let rec lowerTypedExpr (env: TypedLowerEnv) (texpr: TypedExpr) : IRExpr =
    match texpr.Kind with
    | TExprLit lit ->
        IRLit (lowerLiteralToIRLit lit)

    | TExprWildcard ->
        // A wildcard `_` is a hole, not a value. It is only meaningful where a
        // context consumes it (a compound-index coordinate marks a free axis).
        // Reaching lowering means it was used where no context interpreted it.
        failwith "wildcard `_` is not valid here: it can only appear as a compound-index coordinate (e.g. B((a, _, c))) or in a pattern"
    
    | TExprVar (name, varId, identity) ->
        // Variant constructors without payload (e.g. North) have type IRTNamed
        // Variant constructors with payload (e.g. Some) have type FuncElem ([_], IRTNamed _)
        // Neither are bound in the lowering environment. Emit the name verbatim.
        let isVariantCtor =
            not (Map.containsKey name env.Variables) &&
            not (Map.containsKey name env.Functions) &&
            match texpr.Type with
            | IRTNamed _ -> true
            | FuncElem (_, IRTNamed _) -> true
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
        | OpConj -> IRUnaryOp (IRConj, e)
        | OpMath name -> IRUnaryOp (IRMath name, e)
    
    | TExprApp (func, args) ->
        let f = lowerTypedExpr env func
        let as' = args |> List.map (lowerTypedExpr env)
        // Path 1 fix (Stage 3c follow-on): if TypeCheck pinned the
        // function position to an array type — e.g., after
        // buildApplyInfo unified a kernel param to Array<T, N> — the
        // application is structurally an index, not a function call.
        // Dispatch here so the IR's IRIndex/IRApp split matches the
        // semantic intent at the value-renderer side. Codegen's
        // ragged-peel pass has historically applied this rewrite at
        // codegen time as a workaround; pushing it back to Lowering
        // ensures the body's shape is correct regardless of whether
        // the lambda is inlined or lifted to a top-level function.
        match func.Type with
        | ArrayElem _ -> IRIndex (f, as', None)
        | _ -> IRApp (f, as', texpr.Type)
    
    | TExprIndex (array, indices, identity) ->
        let arr = lowerTypedExpr env array
        let idxs = indices |> List.map (lowerTypedExpr env)
        IRIndex (arr, idxs, identity)
    
    | TExprTupleIndex (tuple, index) ->
        let tup = lowerTypedExpr env tuple
        // A LITERAL index into a real tuple is a static projection —
        // IRTupleProj, same as destructuring emits. (The checker's
        // cumulant(d, k) arm produces exactly this shape: by lowering
        // time the Dist type has zonk-erased to IRTTuple.) Everything
        // else stays on the poly-pack path (IRPolyIndex).
        match tuple.Type, index.Kind with
        | IRTTuple _, TExprLit (LitInt n) -> IRTupleProj (tup, int n, false)
        | _ ->
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
    
    | TExprComplexLit (re, im) ->
        // Lower to IRComplex, NOT IRTuple. Complex is a scalar at IR
        // level — flattening to a tuple of floats here would let
        // downstream code (array lowering, codegen) reshape it as part
        // of the surrounding rank, producing wrong-rank arrays.
        IRComplex (lowerTypedExpr env re, lowerTypedExpr env im)
    
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
    
    | TExprApply info when info.IsComposeApply ->
        // Slot-inverted compose application: `(o1 >>@ o2) <@> A`.
        // TypeCheck flagged this case with IsComposeApply = true, storing
        // the input arrays in BOTH `info.Arrays` and (redundantly)
        // `info.Kernel`. The IR form `IRComposeApply` carries them only
        // in `InputArrays`; the redundancy goes away.
        IRComposeApply {
            Composition = lowerTypedExpr env info.Loop
            InputArrays = info.Arrays |> List.map (lowerTypedExpr env)
            OutputType = info.OutputType
        }

    | TExprApply info ->
        // Canonical apply. Symmetry info already computed during type checking.
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
            KernelTDims = info.KernelTDims
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
    
    | TExprRange indexTypes ->
        IRRange (indexTypes, None)

    | TExprDotDot (lo, hi) ->
        let loIR = lowerTypedExpr env lo
        let hiIR = lowerTypedExpr env hi
        let extentExpr = IRBinOp (IRElementwise, IRSub, hiIR, loIR)
        let idx = {
            Id = env.Builder.FreshId()
            Rank = 1
            Extent = extentExpr
            Symmetry = SymNone
            Tag = Some "__anon"; IxKind = IxKPlain
            Kind = SDimension
            Dependencies = []
        }
        let offset =
            match loIR with
            | IRLit (IRLitInt 0L) -> None
            | _ -> Some loIR
        IRRange ([idx], offset)
    
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
        // CONSTANT-FILL FOLD: `replicate(N, pure(lit)) |> compute` with a
        // concrete count and a literal body is exactly an N-element array
        // literal — lower it as IRArrayLit so it rides the array-literal
        // machinery everywhere (function bodies included; the general
        // IRSequence realization is main-body-only). The generated C++ is
        // byte-identical to writing the literal out. Non-literal counts
        // and non-constant bodies keep the general combinator path.
        (match e.Kind with
         | TExprReplicate (cnt, body) ->
             (match cnt.Kind, body.Kind, texpr.Type with
              | TExprLit (LitInt n), TExprPure inner, ArrayElem arrTy
                    when n >= 0L && n <= 1_000_000L
                         && (match inner.Kind with TExprLit _ -> true | _ -> false) ->
                  let copies = List.replicate (int n) (lowerTypedExpr env inner)
                  IRArrayLit (copies, arrTy)
              | _ -> IRCompute (lowerTypedExpr env e))
         | _ -> IRCompute (lowerTypedExpr env e))
    
    | TExprRead e ->
        // Slice 1: passthrough. |> read will force the deferred provider read
        // that load_as produces (introduced in a later slice); until that
        // exists, reading lowers to the operand itself (a no-op).
        lowerTypedExpr env e

    | TExprFillRandom _ ->
        // fill_random(mod) is only meaningful as an annotated top-level
        // let-binding value, where the TDeclLet loop intercepts it (it needs the
        // binding's array type for the shape and records it in RandomInits).
        // Reaching here means it was used inline / in a nested let, which has no
        // annotation to supply the shape.
        failwith "fill_random(mod) is only valid as an annotated top-level let-binding value (let A: Array<..> = fill_random(mod))"
    
    | TExprCompound _ ->
        // compound(dense, mask) is only meaningful as a top-level let-binding
        // value, where the TDeclLet loop intercepts it (it records the lowered
        // dense + mask in CompoundInits and leaves a unit placeholder, mirroring
        // fill_random). Reaching here means it was used inline / in a nested let,
        // which the compound-construction codegen path does not handle.
        failwith "compound(dense, mask) is only valid as a top-level let-binding value (let B = compound(dense, mask))"
    
    | TExprGuard (cond, body) ->
        IRGuard (lowerTypedExpr env cond, lowerTypedExpr env body)
    
    | TExprMask (array, pred) ->
        IRMask (lowerTypedExpr env array, lowerTypedExpr env pred)
    
    | TExprIntersect (a, b) ->
        IRIntersect (lowerTypedExpr env a, lowerTypedExpr env b)
    
    | TExprUnion (a, b) ->
        IRUnion (lowerTypedExpr env a, lowerTypedExpr env b)
    
    | TExprUnique a ->
        IRUnique (lowerTypedExpr env a)
    
    | TExprContains (a, v) ->
        IRContains (lowerTypedExpr env a, lowerTypedExpr env v)
    
    | TExprGroupBy (values, grouping) ->
        IRGroupBy (lowerTypedExpr env values, lowerTypedExpr env grouping)
    
    | TExprGroupKeys keys ->
        IRGroupKeys (keys |> List.map (lowerTypedExpr env))
    
    | TExprSort (array, key) ->
        IRSort (lowerTypedExpr env array, lowerTypedExpr env key)
    
    | TExprReduce (array, kernel, init) ->
        (match array.Kind, init with
         // Fused reduction terminal: the checker spliced a RESOLVED deferred
         // computation (plain apply or canonical fusion tree) as the child
         // and always filled the seed (tryInferReduceCompute). Fold without
         // materializing — codegen emits one nest with scalar accumulators.
         | (TExprApply _ | TExprFusion _), Some seed ->
            IRReduceCompute (lowerTypedExpr env array, lowerTypedExpr env kernel,
                             lowerTypedExpr env seed)
         | _ ->
            IRReduce (lowerTypedExpr env array, lowerTypedExpr env kernel,
                      init |> Option.map (lowerTypedExpr env)))

    | TExprProdSum args ->
        IRProdSum (args |> List.map (lowerTypedExpr env))

    | TExprTranspose (array, dim1, dim2) ->
        IRTranspose (lowerTypedExpr env array, dim1, dim2)
    | TExprDecompact (array, dim) ->
        IRDecompact (lowerTypedExpr env array, dim)
    | TExprGram (left, right, isSameArray) ->
        IRGram (lowerTypedExpr env left, lowerTypedExpr env right, isSameArray)
    | TExprArrayNegate array ->
        IRArrayNegate (lowerTypedExpr env array)
    | TExprArrayConjugate array ->
        IRArrayConjugate (lowerTypedExpr env array)
    
    | TExprExtents array ->
        // Rank-1: emit a single IRExtent (arr, 0).
        // Rank-N: emit a tuple of IRExtent (arr, i) for i in 0..N-1.
        let arr' = lowerTypedExpr env array
        match array.Type with
        | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
            IRExtent (arr', 0)
        | ArrayElem arrTy ->
            let n = arrTy.IndexTypes.Length
            IRTuple (List.init n (fun i -> IRExtent (arr', i)))
        | _ ->
            // Typecheck should have rejected — fall back to a degenerate form
            // rather than crash; downstream IR validator will catch oddities.
            IRExtent (arr', 0)
    
    | TExprZero ->
        // Lower to type-appropriate zero literal based on resolved type
        match texpr.Type with
        | IRTScalar ETInt32 | IRTScalar ETInt64 -> IRLit (IRLitInt 0L)
        | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) -> IRLit (IRLitInt 0L)
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
                   | ArrayElem at -> at.IndexTypes |> List.sumBy (fun idx -> idx.Rank)
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
        lowerTypedSection env op texpr.Type
    
    | TExprPartialApp (op, arg, isLeft) ->
        lowerTypedPartialApp env op (lowerTypedExpr env arg) isLeft texpr.Type

/// Lower a typed lambda
and lowerTypedLambda env (info: TypedLambdaInfo) : IRExpr =
    let mutable paramEnv = env
    let paramInfos = info.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)

    let captures = info.Captures |> List.map (fun c ->
        { Id = c.VarId; Name = c.Name; Type = c.Type; IsMutable = c.IsMutable } : CaptureInfo)

    let body' = lowerTypedExpr paramEnv info.Body

    // If the body's top-level shape is value-position-illegal as a
    // standalone function return, wrap it in IRCompute. Currently this
    // applies only to bare IRApplyCombinator — `method_for { ... }` and
    // similar combinator forms that need a destination to materialize
    // into. genFuncBody's return-position match handles
    // `IRCompute(IRApplyCombinator _)` by synthesizing an internal let
    // binding, running the full combinator codegen, and returning the
    // bound name. Use-site rendering (exprToCppCore's IRCompute arm) is
    // identical for IRCompute(IRApplyCombinator) and bare IRApplyCombinator,
    // so the wrap doesn't change behavior at existing use sites.
    let bodyWrapped =
        match body' with
        | IRApplyCombinator _ | IRComposeApply _ -> IRCompute body'
        | _ -> body'

    // Build unified IRCallable. info.ReturnType comes from TypeCheck,
    // so the lambda has a concrete return type. The body's IRExpr type
    // matches it (modulo inference); we trust TypeCheck's annotation.
    // Lambda-level parallelism: find the Omp assignment (if any) in the strategy
    // list and map its named Vars to param indices. (Today the list has 0 or 1
    // element; List.tryPick generalizes cleanly to the future mixed case where
    // omp and cuda assignments coexist.) Cuda/none => no omp parallelism here.
    let lamOmp = info.Parallel |> List.tryPick (function Omp o -> Some o | _ -> None)
    let lamParallelism =
        match lamOmp with
        | Some omp ->
            omp.Vars |> List.choose (fun (name, dims) ->
                info.Params |> List.tryFindIndex (fun p -> p.Name = name)
                |> Option.map (fun idx -> (idx, dims)))
        | None -> []
    let lamIsOmp = Option.isSome lamOmp
    // Lambda-level cuda: find a Cuda assignment (if any) and its block size.
    let lamCuda = info.Parallel |> List.tryPick (function Cuda c -> Some c | _ -> None)
    let lamIsCuda = Option.isSome lamCuda
    let lamBlock = match lamCuda with Some c -> c.BlockSize | None -> 256
    let callable =
        mkLambdaCallable env.Builder paramInfos bodyWrapped info.ReturnType
                         captures info.IsCommutative info.CommGroups
                         lamParallelism lamIsOmp lamIsCuda lamBlock
    // Emit IRVar(callable.Id, funcType) — the callable lives in
    // LiftedCallables → module.Functions; the IRVar carries just the
    // function type for type-inference and consumer dispatch.
    // Consumers use `resolveCallable` to walk back to the callable
    // when they need params/body/captures. The function type for the
    // IRVar annotation uses the regular params only — captures are
    // an implementation detail of the lifted function's signature
    // and aren't part of what consumers see.
    env.LiftedCallables.Add(callable)
    let funcType =
        let paramTypes = callable.Params |> List.map (fun p -> p.Type)
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

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
    | LitString s -> IRLitString s
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
    | TPatVariant (tag, payload, isEnum) -> 
        IRPatVariant (tag, hash tag, payload |> Option.map lowerTypedPattern, isEnum)
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
            if binding.SubBindings.IsEmpty then
                let body = lowerTypedBlock env' rest finalExpr
                IRLet (binding.VarId, value, body)
            else
                // Destructuring let inside a block (`let (x, y) = p`): chain a
                // projection IRLet per pattern leaf after the primary binding,
                // mirroring the TDeclLet path — without these the leaf VarIds
                // dangle (the body references bindings never introduced).
                let isStruct = match binding.Type with IRTNamed _ -> true | _ -> false
                let isFlat =
                    match binding.Type with
                    | IRTTuple ts ->
                        let structCount = ts.Length
                        let flatCount = IR.flattenTupleLeaves binding.Type |> List.length
                        binding.SubBindings.Length = flatCount && binding.SubBindings.Length <> structCount
                    | _ -> false
                let env'' = binding.SubBindings |> List.fold (fun e (name, subId, _) -> bindTypedVar name subId e) env'
                let body = lowerTypedBlock env'' rest finalExpr
                let indexedSubs =
                    binding.SubBindings |> List.mapi (fun i (name, subId, _subTy) -> (i, name, subId))
                let chained =
                    List.foldBack (fun (i, name, subId) acc ->
                        let projExpr =
                            if isStruct then IRFieldAccess (IRVar (binding.VarId, binding.Type), name)
                            else IRTupleProj (IRVar (binding.VarId, binding.Type), i, isFlat)
                        IRLet (subId, projExpr, acc)) indexedSubs body
                IRLet (binding.VarId, value, chained)
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
and lowerTypedSection env (op: BinOp) (funcTy: IRType) : IRExpr =
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
    // Stage 3c.2 follow-on: extract the resolved param/return types from
    // the typed section's function type instead of hardcoding Float64.
    // TypeCheck.inferExpr ExprSection already types sections
    // polymorphically (`α → α → α`); subsequent unifications in
    // consumer-position handlers (e.g., inferReduce pinning kernel
    // params to the array element type) bind that fresh α to the actual
    // context type. By zonk time the section's funcTy carries the
    // resolved scalar type, and we pull it from there. Float64 only
    // appears as a fallback for sections whose type genuinely couldn't
    // be resolved — a case that should not occur in practice but is
    // preserved as a defensive default rather than crashing.
    let (paramTy, retTy) =
        match funcTy with
        | IRTArrow (slots, r, _) when slots.Length = 2 ->
            // Pull the first param's resolved scalar type. Both slots
            // should resolve to the same scalar after typecheck's
            // section unification, but we read the first as canonical.
            let first =
                match slots.[0] with
                | SVal t -> t
                | SIdx _ | SIdxVirt _ -> IRTScalar ETFloat64
            (first, r)
        | _ -> (IRTScalar ETFloat64, IRTScalar ETFloat64)
    let body = IRBinOp(IRElementwise, irOp, IRVar (aId, paramTy), IRVar (bId, paramTy))
    let parms : IRParam list =
        [{ Name = "a"; Type = paramTy; Index = 0; VarId = aId }
         { Name = "b"; Type = paramTy; Index = 1; VarId = bId }]
    // Comparison and logical ops produce bool regardless of operand
    // type; arithmetic ops produce the operand element type. retTy
    // from the funcTy should already encode this distinction, but
    // we recompute defensively so a malformed funcTy doesn't put a
    // wrong return type on the lifted callable.
    let retType =
        match irOp with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
            IRTScalar ETBool
        | _ -> retTy
    let commGroups = if isComm then [[0; 1]] else []
    let callable = mkLambdaCallable env.Builder parms body retType [] isComm commGroups [] false false 256
    env.LiftedCallables.Add(callable)
    // Stage 3c.3: emit IRVar reference to the lifted callable.
    let funcType =
        let paramTypes = callable.Params |> List.map (fun p -> p.Type)
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Lower a partial operator application to a lambda
and lowerTypedPartialApp env (op: BinOp) (argExpr: IRExpr) (isLeft: bool) (funcTy: IRType) : IRExpr =
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
    // Same resolved-type extraction as lowerTypedSection: pull the
    // partial application's param/return scalar types from the typed
    // function type. For partial app the funcTy is `α → α` (or
    // `α → Bool` for comparisons), so we read slot 0 as the param
    // type. argExpr was already lowered and carries its own type via
    // the IR; we don't need to consult it here because the typechecker
    // already unified arg's type with the operator's left-or-right
    // operand position.
    let (paramTy, retTy) =
        match funcTy with
        | IRTArrow (slots, r, _) when slots.Length = 1 ->
            let p =
                match slots.[0] with
                | SVal t -> t
                | SIdx _ | SIdxVirt _ -> IRTScalar ETFloat64
            (p, r)
        | _ -> (IRTScalar ETFloat64, IRTScalar ETFloat64)
    let body =
        if isLeft then IRBinOp (IRElementwise, irOp, argExpr, IRVar (paramId, paramTy))
        else IRBinOp (IRElementwise, irOp, IRVar (paramId, paramTy), argExpr)
    let parms : IRParam list =
        [{ Name = "x"; Type = paramTy; Index = 0; VarId = paramId }]
    let retType =
        match irOp with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
            IRTScalar ETBool
        | _ -> retTy
    let callable = mkLambdaCallable env.Builder parms body retType [] false [] [] false false 256
    env.LiftedCallables.Add(callable)
    // Stage 3c.3: emit IRVar reference to the lifted callable.
    let funcType =
        let paramTypes = callable.Params |> List.map (fun p -> p.Type)
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Lower typed binary operations
and lowerTypedBinOp env mode op l r leftExpr rightExpr resultType =
    let irMode = match mode with Elementwise -> IRElementwise | Outer -> IROuter
    
    // Check if both operands are arrays — if so, synthesize object_for loop
    let isArithOp = match op with
                    | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
                    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
                    | OpAnd | OpOr -> true
                    | _ -> false
    let leftIsArray = match leftExpr.Type with ArrayElem _ -> true | _ -> false
    let rightIsArray = match rightExpr.Type with ArrayElem _ -> true | _ -> false
    
    if isArithOp && leftIsArray && rightIsArray then
        // Synthesize: object_for(lambda(x, y) -> x [op] y)(A, B)
        let irOp = match op with
                   | OpAdd -> IRAdd | OpSub -> IRSub | OpMul -> IRMul
                   | OpDiv -> IRDiv | OpMod -> IRMod | OpCaret -> IRCaret
                   | OpEq -> IREq | OpNeq -> IRNeq
                   | OpLt -> IRLt | OpLe -> IRLe | OpGt -> IRGt | OpGe -> IRGe
                   | OpAnd -> IRAnd | OpOr -> IROr | _ -> IRAdd
        // S3 tag: relic. Default to Float64 if elem type isn't a primitive.
        // Lambda params for arithmetic ops require concrete scalar types;
        // if the array's elem type isn't a primitive (e.g., struct or
        // unresolved infer), the codegen would fail downstream. Preserving
        // current default behavior; a stricter error here would be a
        // separate cleanup.
        let elemTypeL =
            match leftExpr.Type with
            | ArrayElem a ->
                match a.ElemType with PrimElem et -> et | _ -> ETFloat64
            | _ -> ETFloat64
        let elemTypeR =
            match rightExpr.Type with
            | ArrayElem a ->
                match a.ElemType with PrimElem et -> et | _ -> ETFloat64
            | _ -> ETFloat64
        let aId = env.Builder.FreshId()
        let bId = env.Builder.FreshId()
        let body = IRBinOp(IRElementwise, irOp, IRVar (aId, IRTScalar elemTypeL), IRVar (bId, IRTScalar elemTypeR))
        let parms : IRParam list = [
            { Name = "__a"; Type = IRTScalar elemTypeL; Index = 0; VarId = aId }
            { Name = "__b"; Type = IRTScalar elemTypeR; Index = 1; VarId = bId }
        ]
        // Kernel return type: comparison/logical ops produce bool;
        // arithmetic ops keep the left operand's element type (matches
        // existing IRBinOp typing conventions for elementwise ops).
        let kernelRetType =
            match irOp with
            | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
                IRTScalar ETBool
            | _ -> IRTScalar elemTypeL
        let commGroups = if mode = Elementwise then [[0; 1]] else []
        let lambdaInfo =
            mkLambdaCallable env.Builder parms body kernelRetType [] false commGroups [] false false 256
        env.LiftedCallables.Add(lambdaInfo)
        // Kernel slot references the lifted callable via IRVar;
        // genObjectForApplication uses resolveCallable + wrapper to
        // consume it.
        let kernelFuncType =
            let paramTypes = lambdaInfo.Params |> List.map (fun p -> p.Type)
            mkFuncArrow paramTypes lambdaInfo.RetType
        let inputRanks = match mode with Outer -> [1; 1] | Elementwise -> [0; 0]
        let objInfo : ObjectForInfo = {
            Kernel = IRVar (lambdaInfo.Id, kernelFuncType)
            CommGroups = commGroups
            InputRanks = inputRanks
            OutputRank = 0
        }
        IRApp(IRObjectFor objInfo, [IRTuple [l; r]], resultType)
    elif mode = Elementwise
         && (match op with OpEq|OpNeq|OpLt|OpLe|OpGt|OpGe|OpAnd|OpOr -> true | _ -> false)
         && (leftIsArray <> rightIsArray) then
        // Array<->scalar broadcast for comparison/logical ops: `A > 2.0` or
        // `2.0 < A`. The op is T^0 -> T^0 -> T^0; co-iteration peels the array
        // operand down to T^0 and the scalar already matches that rank, so we
        // iterate the array with the scalar held fixed via a 1-param partial-
        // application kernel (lambda(x) -> x op s, or lambda(x) -> s op x). The
        // scalar operand is captured as the fixed arg; isLeft selects the side.
        let (arrayIR, kernelVar) =
            if leftIsArray then
                // A op scalar  ->  lambda(x) -> x op scalar   (fixed arg on right, isLeft = false)
                let elemTy = (match leftExpr.Type with ArrayElem a -> a.ElemType | _ -> IRTScalar ETFloat64)
                let funcTy = mkFuncArrow [elemTy] (IRTScalar ETBool)
                (l, lowerTypedPartialApp env op r false funcTy)
            else
                // scalar op A  ->  lambda(x) -> scalar op x   (fixed arg on left, isLeft = true)
                let elemTy = (match rightExpr.Type with ArrayElem a -> a.ElemType | _ -> IRTScalar ETFloat64)
                let funcTy = mkFuncArrow [elemTy] (IRTScalar ETBool)
                (r, lowerTypedPartialApp env op l true funcTy)
        let objInfo : ObjectForInfo = {
            Kernel = kernelVar
            CommGroups = []
            InputRanks = [0]
            OutputRank = 0
        }
        IRApp(IRObjectFor objInfo, [arrayIR], resultType)
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
            KernelTDims = []
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

    // Extract parallelism from where clause. The AST carries a ParallelStrategy
    // LIST; find the Omp assignment (if any) and map its named vars + dim counts
    // into the IRCallable.Parallelism (int*int) shape (param-index, level). Cuda
    // does not populate this field (its IR channel is added in a later phase).
    // No omp assignment => [] (serial), the default. (List.tryPick generalizes
    // to the future mixed omp+cuda case.)
    let declOmp =
        match decl.WhereClause with
        | Some wc -> wc.Parallel |> List.tryPick (function Omp o -> Some o | _ -> None)
        | None -> None
    let parallelism =
        match declOmp with
        | Some omp ->
            omp.Vars |> List.choose (fun (name, dims) ->
                decl.Params |> List.tryFindIndex (fun p -> p.Name = name)
                |> Option.map (fun idx -> (idx, dims)))
        | None -> []

    // The opt-in OpenMP signal: true iff the where-clause carried an `omp(...)`
    // assignment. Distinguishes omp from cuda and from serial, so loop
    // parallelization keys on this, not on Parallelism-list emptiness.
    let isOmpParallel = Option.isSome declOmp

    // The opt-in CUDA signal: find a Cuda assignment in the strategy list. When
    // present, codegen emits a flat-launch kernel (following increment). Carries
    // the launch block size; default 256 if no Cuda assignment.
    let declCuda =
        match decl.WhereClause with
        | Some wc -> wc.Parallel |> List.tryPick (function Cuda c -> Some c | _ -> None)
        | None -> None
    let isCudaKernel = Option.isSome declCuda
    let cudaBlockSize = match declCuda with Some c -> c.BlockSize | None -> 256

    // Bind parameters in environment for body lowering
    let mutable paramEnv = { env with PolyParamNames = polyParamNames }
    let irParams = decl.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)

    let body = lowerTypedExpr paramEnv decl.Body

    let funcDef : IRCallable = {
        Id = decl.FuncId
        Name = decl.Name
        Params = irParams
        RetType = decl.ReturnType
        Body = body
        IsStatic = decl.IsStatic
        IsCommutative = not decl.CommGroups.IsEmpty
        CommGroups = decl.CommGroups
        Parallelism = parallelism
        IsOmpParallel = isOmpParallel
        IsCudaKernel = isCudaKernel
        CudaBlockSize = cudaBlockSize
        IsArityPoly = isArityPoly
        ArityParam = polyParamNames |> List.tryHead
        // Source-level functions live at top level and have no enclosing
        // scope to capture from. Lifted lambdas (handled separately in
        // Stage 4) populate Captures from the lambda's free variables.
        Captures = []
    }

    let env' = bindTypedVar decl.Name decl.FuncId env
    (funcDef, env')

/// Lower a TypedTypeDef to IRTypeDef (types already resolved by TypeCheck)
let lowerTypedTypeDef (env: TypedLowerEnv) (ttd: TypedTypeDef) : IRTypeDef =
    match ttd with
    | TTDAlias (name, _, resolved) ->
        IRTDAlias (name, resolved)
    | TTDIndexType (name, idx) ->
        // Map to IRTDIndexType so CodeGen.genTypeDefs emits a typedef
        // (using Name = int64_t;) and ETIndexRef Name renders as the alias.
        IRTDIndexType (name, idx)
    | TTDEnumIdx (name, idx, values) ->
        IRTDEnumIdx (name, idx, values)
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
        // Static values: use pre-evaluated value if available, else lower
        // normally. The fast path is for plain `let static x` only — a
        // destructured static's primary is the synthetic "_" and its leaves
        // are emitted as constants from the sub-binding loop below.
        let (primary, env') =
            match (if binding.SubBindings.IsEmpty then Map.tryFind binding.Name env.StaticValues else None) with
            | Some sv ->
                let irValue = staticValueToIR sv
                let ty = match sv with
                         | StaticEval.SVInt _ -> IRTScalar ETInt64
                         | StaticEval.SVFloat _ -> IRTScalar ETFloat64
                         | StaticEval.SVBool _ -> IRTScalar ETBool
                         | _ -> IRTUnit
                let bd = { Id = binding.VarId; Name = binding.Name; Type = ty; Value = irValue; IsConst = true; IsMutable = false }
                let env' = bindTypedVar binding.Name binding.VarId env
                (bd, env')
            | _ ->
                // Fallback: lower as normal binding
                let (irBinding, env') = lowerTypedBinding env binding
                (irBinding, env')

        // Emit sub-bindings for destructured patterns. The static evaluator's
        // bindPattern (StaticEval.fs) has already populated env.StaticValues
        // with each sub-name → value mapping for tuple destructuring; prefer
        // those direct constants. Fall back to tuple projection of the
        // primary binding for shapes the static evaluator didn't reach.
        let isStruct = match binding.Type with IRTNamed _ -> true | _ -> false
        let isFlat =
            match binding.Type with
            | IRTTuple ts ->
                let structCount = ts.Length
                let flatCount = IR.flattenTupleLeaves binding.Type |> List.length
                binding.SubBindings.Length = flatCount && binding.SubBindings.Length <> structCount
            | _ -> false
        let (subIRBindings, envFinal) =
            binding.SubBindings |> List.mapi (fun i (name, subId, subTy) -> (i, name, subId, subTy))
            |> List.fold (fun (acc, e) (i, name, subId, subTy) ->
                let bd =
                    match Map.tryFind name env.StaticValues with
                    | Some sv ->
                        // Direct static constant — preferred path for statically-
                        // evaluated tuple destructuring.
                        let irValue = staticValueToIR sv
                        { Id = subId; Name = name; Type = subTy; Value = irValue
                          IsConst = true; IsMutable = false }
                    | None ->
                        // Projection fallback — same shape as TDeclLet's branch.
                        let projExpr =
                            if isStruct then IRFieldAccess (IRVar (binding.VarId, binding.Type), name)
                            else IRTupleProj (IRVar (binding.VarId, binding.Type), i, isFlat)
                        { Id = subId; Name = name; Type = subTy; Value = projExpr
                          IsConst = true; IsMutable = false }
                (acc @ [bd], bindTypedVar name subId e)
            ) ([], env')
        ([Choice2Of3 primary] @ (subIRBindings |> List.map Choice2Of3), envFinal)
    
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
                match TypeEnv.resolveUnitExpr env.UnitDefs expr with
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
            let env' = { env' with ProviderPaths = Map.add binding.Name path env'.ProviderPaths }
            Some (providerModule.Types, bd, env')
        | _ -> None
    | _ -> None

/// Detect a deferred compound read: `let data = NetCDF.load_compound(var, mask) |> read`.
/// Recovers everything genReadCompoundVar needs from the typed argument shape:
/// the file path (via the variable reference's root provider binding recorded in
/// ProviderPaths), the variable and mask names (the outer field of each provider
/// field access, e.g. `sample.vars.B` -> "B"), and their array types. The
/// presence of a mask is what marks this a compound (vs plain dense) read.
let tryCompoundRead (env: TypedLowerEnv) (binding: TypedBinding) : ProviderReadSpec option =
    let rec rootName (e: TypedExpr) =
        match e.Kind with
        | TExprVar (n, _, _) -> Some n
        | TExprField (inner, _, _) -> rootName inner
        | _ -> None
    let fieldName (e: TypedExpr) =
        match e.Kind with
        | TExprField (_, f, _) -> Some f
        | _ -> None
    match binding.Value.Kind with
    | TExprRead inner ->
        (match inner.Kind with
         | TExprApp ({ Kind = TExprField ({ Kind = TExprVar _ }, "load_compound", _) }, [tVar; tMask]) ->
             (match rootName tVar, fieldName tVar, fieldName tMask with
              | Some root, Some varName, Some maskName when Map.containsKey root env.ProviderPaths ->
                  (match tVar.Type, tMask.Type with
                   | ArrayElem varArr, ArrayElem maskArr ->
                       Some { FilePath = env.ProviderPaths.[root]
                              VarName = varName
                              VarType = varArr
                              MaskName = Some maskName
                              MaskType = Some maskArr }
                   | _ -> None)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Detect a deferred dense read: `let A = sample.vars.A |> read`. The dense
/// (maskless) analog of tryCompoundRead -- a provider VAR field access piped to
/// `read`, with NO mask. Recovers the file path (via the var's root provider
/// binding in ProviderPaths), the variable name (the outer field, e.g.
/// `sample.vars.A` -> "A"), and the array type. genBinding materializes it via
/// genReadVar (the no-mask arm of the ProviderReads intercept). Distinct from a
/// compound read: that wraps a `load_compound(...)` application; this wraps a
/// plain field access, so the two matchers are mutually exclusive.
let tryPlainRead (env: TypedLowerEnv) (binding: TypedBinding) : ProviderReadSpec option =
    let rec rootName (e: TypedExpr) =
        match e.Kind with
        | TExprVar (n, _, _) -> Some n
        | TExprField (inner, _, _) -> rootName inner
        | _ -> None
    match binding.Value.Kind with
    | TExprRead inner ->
        (match inner.Kind with
         | TExprField (_, varName, _) ->
             (match rootName inner with
              | Some root when Map.containsKey root env.ProviderPaths ->
                  (match inner.Type with
                   | ArrayElem varArr ->
                       Some { FilePath = env.ProviderPaths.[root]
                              VarName = varName
                              VarType = varArr
                              MaskName = None
                              MaskType = None }
                   | _ -> None)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Lower a typed module
let lowerTypedModule (env: TypedLowerEnv) (modul: TypedModule) (rawDecls: Located<Decl> list option) : IRModule * ModuleExport =
    // Fresh LiftedCallables for this module. Lifted lambdas from a
    // previous module's lowering must not leak into this one.
    let env = { env with LiftedCallables = ResizeArray<IRCallable>() }
    // Phase 0: Resolve static values/functions from raw declarations
    let mutable currentEnv =
        match rawDecls with
        | Some decls ->
            match StaticEval.resolveStatics decls with
            // Failures were already reported as compile errors by the
            // type-checker's pre-pass; unfolded statics lower as runtime
            // bindings here regardless.
            | Ok (staticEnv, _) ->
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
        | TDeclLet binding when (tryCompoundRead currentEnv binding).IsSome ->
            // Deferred compound read (`load_compound(var, mask) |> read`): the
            // binding holds the compact Compound value, but no C++ is emitted
            // here. genBinding materializes it via genReadCompoundVar when it
            // sees the binding's IRId in ctx.ProviderReads. Value is a unit
            // placeholder; the type is the compound view type from typecheck.
            let spec = (tryCompoundRead currentEnv binding).Value
            let bd = {
                Id = binding.VarId
                Name = binding.Name
                Type = binding.Type
                Value = IRLit IRLitUnit
                IsConst = true
                IsMutable = binding.IsMutable
            }
            bindings <- bindings @ [bd]
            currentEnv <- bindTypedVar binding.Name binding.VarId currentEnv
            currentEnv <- { currentEnv with ProviderReads = Map.add binding.VarId spec currentEnv.ProviderReads }
        | TDeclLet binding when (tryPlainRead currentEnv binding).IsSome ->
            // Deferred dense read (`sample.vars.A |> read`): the binding holds the
            // dense array value, materialized in codegen via genReadVar (the
            // no-mask arm of the ProviderReads intercept). Value is a unit
            // placeholder; the type is the array type from typecheck. Mirrors the
            // compound arm above; the matchers are mutually exclusive (compound
            // wraps a load_compound app, this wraps a plain field access).
            let spec = (tryPlainRead currentEnv binding).Value
            let bd = {
                Id = binding.VarId
                Name = binding.Name
                Type = binding.Type
                Value = IRLit IRLitUnit
                IsConst = true
                IsMutable = binding.IsMutable
            }
            bindings <- bindings @ [bd]
            currentEnv <- bindTypedVar binding.Name binding.VarId currentEnv
            currentEnv <- { currentEnv with ProviderReads = Map.add binding.VarId spec currentEnv.ProviderReads }
        | TDeclLet binding when (match binding.Value.Kind with TExprFillRandom _ -> true | _ -> false) ->
            // Random-fill constructor (`let A: Array<..> = fill_random(mod)`): the
            // binding holds a random-filled array, materialized in codegen via
            // allocate<> + the runtime fill_random (the RandomInits intercept in
            // genBinding). Value is a unit placeholder; the type is the array type
            // from the annotation (typecheck). The modulus is lowered and recorded.
            let modIR =
                match binding.Value.Kind with
                | TExprFillRandom m -> lowerTypedExpr currentEnv m
                | _ -> IRLit (IRLitInt 1L)  // unreachable: guarded by the `when` above
            let bd = {
                Id = binding.VarId
                Name = binding.Name
                Type = binding.Type
                Value = IRLit IRLitUnit
                IsConst = true
                IsMutable = binding.IsMutable
            }
            bindings <- bindings @ [bd]
            currentEnv <- bindTypedVar binding.Name binding.VarId currentEnv
            currentEnv <- { currentEnv with RandomInits = Map.add binding.VarId modIR currentEnv.RandomInits }
        | TDeclLet binding when (match binding.Value.Kind with TExprCompound _ -> true | _ -> false) ->
            // Compound-construction constructor (`let B = compound(dense, mask)`):
            // the binding holds a CompoundIdx-typed compact array, materialized in
            // codegen via P0 (genCompoundIndexFromMask) + a dense->compact scatter
            // (the CompoundInits intercept in genBinding). Value is a unit
            // placeholder; the type is the Compound view type from typecheck. The
            // dense and mask are lowered and recorded. Mirrors the fill_random arm.
            let denseIR, maskIR =
                match binding.Value.Kind with
                | TExprCompound (d, m) -> lowerTypedExpr currentEnv d, lowerTypedExpr currentEnv m
                | _ -> IRLit IRLitUnit, IRLit IRLitUnit  // unreachable: guarded by the `when` above
            let bd = {
                Id = binding.VarId
                Name = binding.Name
                Type = binding.Type
                Value = IRLit IRLitUnit
                IsConst = true
                IsMutable = binding.IsMutable
            }
            bindings <- bindings @ [bd]
            currentEnv <- bindTypedVar binding.Name binding.VarId currentEnv
            currentEnv <- { currentEnv with CompoundInits = Map.add binding.VarId (denseIR, maskIR) currentEnv.CompoundInits }
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
        // Source-level functions plus all lambdas lifted to module
        // scope during lowering. Use sites reference these callables
        // via IRVar(callable.Id, funcType); the single canonical
        // definition lives here in module.Functions.
        Functions = funcs @ (currentEnv.LiftedCallables |> List.ofSeq)
        Bindings = bindings
        StaticFunctionUsage = usageReport
        ProviderReads = currentEnv.ProviderReads
        RandomInits = currentEnv.RandomInits
        CompoundInits = currentEnv.CompoundInits
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
        // Monomorphize arity-polymorphic functions first: Poly<T^N> packs
        // get expanded into N concrete params per call site. After this,
        // every function has a fixed param count matching its call sites.
        let irModule = IR.monomorphizeModule irModule env.Builder
        // HM monomorphization: substitute function-boundary type variables
        // (e.g., `T` in `Array<T like Idx<n>>`, or `T` extracted from
        // `Poly<T^N>`'s base type) with concrete types learned from each
        // call site. Runs after Poly so per-param/per-arg unification is
        // straightforward — each pack has already been expanded.
        let irModule = IR.monomorphizeHMFunctions irModule env.Builder
        // Lift inline forms (mask/sort/intersect/union/group_by/group_keys
        // appearing in non-let-RHS positions) into auto-let bindings so
        // codegen sees the canonical "let-bound" pattern uniformly.
        let irModule = IR.liftInlineFormsModule irModule env.Builder
        // M1.4: structural rewrite of mask+contains fusion. Recognizes
        // The mask+contains fusion pass, the IRMaskWithSet/IRSetMember IR
        // variants, the dead mask-emitter set-hoist, and the ContainsProbe
        // probe machinery (type, active pattern, the exprAttrs Probes field
        // and its collection/consumption) are all removed. NOTE: mask+contains
        // now always runs a linear scan; the semijoin hash-set is a separate,
        // not-yet-implemented optimization.
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
        | Ok (typedProgram, builder, warnings) ->
            // Print TypeCheck warnings to stderr so the pipeline output
            // (the IR program) stays clean. This is the lower-level entry
            // point; the Main.fs runners surface warnings to stdout, but
            // here we don't have a structured channel.
            for w in warnings do
                eprintfn "[TypeCheck Warning] %s" w
            Ok (lowerTypedProgram typedProgram (Some program) builder)
        | Error errors -> 
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// Lower multiple source files into a single IR program with cross-module imports
let lowerMultiSource (sources: (string * string) list) : Result<IRProgram, string> =
    match Blade.Parser.parseMultiSource sources with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, warnings) ->
            for w in warnings do
                eprintfn "[TypeCheck Warning] %s" w
            Ok (lowerTypedProgram typedProgram (Some program) builder)
        | Error errors ->
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)
