// Blade-DSL Lowering Pass: transforms AST to IR, with support for array
// identity tracking (triangular iteration), lambda capture analysis, pattern
// binding, arity polymorphism, and kernel irank/orank inference.

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

/// Tracks how static functions are used (compile-time, runtime, or both),
/// for IDE diagnostics like "this static function is only ever called at
/// runtime".
[<Flags>]
type StaticUsage =
    | Unused       = 0
    | CompileTime  = 1   // called with all-static args -> evaluated at compile time
    | RunTime      = 2   // called with runtime args -> emitted as normal function call

let rec staticValueToIR (v: StaticEval.StaticValue) : IRExpr =
    match v with
    | StaticEval.SVInt n -> IRLit (IRLitInt n)
    | StaticEval.SVFloat f -> IRLit (IRLitFloat f)
    | StaticEval.SVBool b -> IRLit (IRLitBool b)
    // A folded STRING is an ordinary IR literal (IR.inferExprType types it
    // ETString and CodeGen.litToCpp emits std::string). Lowering it to
    // IRLitUnit instead would silently turn a string-carrying static into
    // `void`, e.g. a spec entry `let static SIN = [("A", 1)]` would lower to
    // std::tuple<void, int64_t> and fail to compile.
    | StaticEval.SVString s -> IRLit (IRLitString s)
    | StaticEval.SVUnit -> IRLit IRLitUnit
    | StaticEval.SVTuple vs -> IRTuple (vs |> List.map staticValueToIR)
    | StaticEval.SVStruct (n, fs) -> IRStructLit (n, fs |> List.map (fun (fn, v) -> (fn, staticValueToIR v)))

// The one definition of resolveUnitExpr is TypeEnv.resolveUnitExpr; the
// single use below calls it qualified.

// TypedAST-based Lowering: translates TypedAST to IR. Since type checking
// has already been done, this is a straightforward translation without
// inference.

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
    /// Provider alias -> registered provider module name (e.g. `import
    /// netcdf as nc` records "nc" -> "netcdf").
    ProviderAliases: Map<string, string>
    /// Provider load binding name -> (provider name, store path literal),
    /// recorded at tryInvokeProvider; used at a `view |> alias.read` site to
    /// recover provider + path by walking the var-reference to its root.
    ProviderPaths: Map<string, string * string>
    /// Deferred provider reads, keyed by the receiving binding's IRId.
    /// Copied into IRModule.ProviderReads at module assembly, consumed at
    /// codegen.
    ProviderReads: Map<IRId, ProviderReadSpec>
    /// Deferred provider writes (`alias.write("path", A)`), keyed by the
    /// write binding's IRId. Copied into IRModule.ProviderWrites.
    ProviderWrites: Map<IRId, ProviderWriteSpec>
    /// Deferred random-fill constructors, keyed by the receiving binding's
    /// IRId. Value is a RandomFillSpec (fill_random modulus, or a rand key).
    /// Copied into IRModule.RandomInits at module assembly.
    RandomInits: Map<IRId, RandomFillSpec>
    /// Deferred compound-construction constructors (compound(dense, mask)),
    /// keyed by the receiving binding's IRId, value (loweredDense,
    /// loweredMask). Copied into IRModule.CompoundInits; mirrors RandomInits.
    CompoundInits: Map<IRId, IRExpr * IRExpr>
    /// Deferred sparse-construction constructors (sparse(values, keys)),
    /// keyed by the receiving binding's IRId (keys ride the binding type's
    /// IRSparseKeys extent). Copied into IRModule.SparseInits.
    SparseInits: Map<IRId, IRExpr>
    /// Lifted lambda callables accumulated during lowering of the current
    /// module: every lambda-construction site adds its IRCallable here, and
    /// at module assembly these are appended to IRModule.Functions so lifted
    /// lambdas are available alongside source-level functions. Mutable
    /// shared state -- F# record `with` updates share the underlying
    /// ResizeArray, so additions from any nested call accumulate into the
    /// module's single list. Reset per-module at the start of
    /// lowerTypedModule.
    LiftedCallables: ResizeArray<IRCallable>
    /// Block-level `let mut` bindings of ARRAY type (IRLet has no
    /// mutability slot, so it is recorded here by IRId). Copied into
    /// IRModule.MutableArrayLets; consumed by codegen and the interpreter to
    /// give such bindings copy, not alias, semantics. Mutable shared state
    /// like LiftedCallables; reset per-module.
    MutableArrayLets: ResizeArray<IRId>
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
    ProviderWrites = Map.empty
    RandomInits = Map.empty
    CompoundInits = Map.empty
    SparseInits = Map.empty
    LiftedCallables = ResizeArray<IRCallable>()
    MutableArrayLets = ResizeArray<IRId>()
}

let bindTypedVar name id (env: TypedLowerEnv) : TypedLowerEnv =
    { env with Variables = Map.add name id env.Variables }

/// Value expression for destructured sub-binding #i of `binding`.
///
/// Positional destructuring reads element i of the tuple (or, for a struct
/// scrutinee, the field carrying the sub-binding's own name). A CONS binding
/// differs at its LAST leaf: `let head :: tail = (1, 2, 3)` binds `tail` to
/// the REMAINDER (2, 3), so that leaf lowers to a re-built tuple of
/// projections 1.. rather than to projection 1 -- a plain projection there
/// would miscompile `tail` as the single element 2.
///
/// A one-element remainder degrades to the bare element, because Blade has no
/// 1-tuple (`(x)` is just `x`); TypeCheck's PatCons arm types the leaf by the
/// identical rule, so the declared type and this value always agree.
///
/// `isFlat` only ever applies to positional projections: a cons binding has
/// fewer leaves than the tuple has slots, so checkDecl's flat-vs-structural
/// test never selects flat for one, and the remainder must index the
/// scrutinee structurally in any case.
let subBindingValue (binding: TypedBinding) (isStruct: bool) (isFlat: bool) (i: int) (name: string) : IRExpr =
    let baseVar = IRVar (binding.VarId, binding.Type)
    let isConsBinding = match binding.Destructure with DSConsRest -> true | DSPositional -> false
    let isRestLeaf = isConsBinding && i = binding.SubBindings.Length - 1
    if isStruct then IRFieldAccess (baseVar, name)
    else
        match binding.Type with
        | IRTPoly _ ->
            // Cons-destructuring a parameter pack (`let head :: tail = A`): head
            // leaves are pack-element reads (IRPolyIndex, same node `A[i]`
            // lowers to), the rest leaf is the symbolic pack tail (IRPolyTail).
            if isRestLeaf then IRPolyTail (baseVar, i)
            else IRPolyIndex (baseVar, IRLit (IRLitInt (int64 i)))
        | IRTTuple ts when isRestLeaf && ts.Length > i ->
            let rest = [ for j in i .. ts.Length - 1 -> IRTupleProj (baseVar, j, false) ]
            if rest.Length = 1 then rest.Head else IRTuple rest
        | _ -> IRTupleProj (baseVar, i, isFlat)

/// Map a callable's parallelization-strategy list (from a function's
/// where-clause or a lambda's `Parallel` list) into the five IRCallable
/// parallelism fields: (Parallelism, IsOmpParallel, IsCudaKernel,
/// CudaBlockSize, IsMpiParallel). `paramNames` resolves an omp clause's
/// named vars to param indices. omp/cuda/mpi are mutually exclusive today,
/// so at most one of the three flags is true; all false = serial host loop.
/// Shared by lowerTypedLambda and lowerTypedFuncDecl so the extraction lives
/// in one place.
let extractParallelism (strategies: ParallelStrategy list) (paramNames: string list)
    : (int * int) list * bool * bool * int * bool =
    let omp = strategies |> List.tryPick (function Omp o -> Some o | _ -> None)
    let parallelism =
        match omp with
        | Some o ->
            o.Vars |> List.choose (fun (name, dims) ->
                paramNames |> List.tryFindIndex (fun n -> n = name)
                |> Option.map (fun idx -> (idx, dims)))
        | None -> []
    let isOmp = Option.isSome omp
    let cuda = strategies |> List.tryPick (function Cuda c -> Some c | _ -> None)
    let isCuda = Option.isSome cuda
    let cudaBlock = match cuda with Some c -> c.BlockSize | None -> 256
    let isMpi = strategies |> List.exists (function Mpi -> true | _ -> false)
    (parallelism, isOmp, isCuda, cudaBlock, isMpi)

/// Lower a TypedExpr to IRExpr
let rec lowerTypedExpr (env: TypedLowerEnv) (texpr: TypedExpr) : IRExpr =
    match texpr.Kind with
    | TExprLit lit ->
        IRLit (lowerLiteralValued lit texpr.Type)

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
        | OpReal -> IRUnaryOp (IRReal, e)
        | OpImag -> IRUnaryOp (IRImag, e)
        | OpArg -> IRUnaryOp (IRArg, e)
        | OpMath name -> IRUnaryOp (IRMath name, e)
    
    | TExprApp (func, args) when
        (match func.Type, args with
         | IRTIdxTagged (_, IRefNamed tag), [_] -> tag.StartsWith("__halowin|")
         | _ -> false) ->
        // halo window read: w(o). `w` is bound to the true CENTER ordinal by
        // the underlying range slot's VirtualRange peel (int64 w = i +
        // startOffset), so the neighbor ordinal is (w + o); BndShrink
        // guarantees in-bounds. Dense inner ("__halowin|d:"): the ordinal IS
        // the index, plain add. Compound inner ("__halowin|c:"): the
        // neighbor is the (w+o)-th PRESENT cell; IRHaloUnhash renders its
        // coordinate through the peel-emitted cidx alias, and the offset
        // must be an integer literal (nothing else can be proven in-reach).
        let tag = match func.Type with IRTIdxTagged (_, IRefNamed t) -> t | _ -> ""
        let f = lowerTypedExpr env func
        let offArg = List.head args
        if tag.StartsWith "__halowin|c:" then
            let staticOff =
                match offArg.Kind with
                | TExprLit (LitInt n) -> Some n
                | TExprUnaryOp (OpNeg, { Kind = TExprLit (LitInt n) }) -> Some (-n)
                | _ -> None
            match staticOff with
            | Some o -> IRHaloUnhash (f, o)
            | None -> failwith "halo window read over a masked domain: the offset must be an integer literal (e.g. w(-1), w(0), w(1))"
        else
            IRBinOp (IRElementwise, IRAdd, f, lowerTypedExpr env offArg)

    | TExprApp (func, args) ->
        let f = lowerTypedExpr env func
        let as' = args |> List.map (lowerTypedExpr env)
        // If TypeCheck pinned the function position to an array type (e.g.
        // after buildApplyInfo unified a kernel param to Array<T, N>), the
        // application is structurally an index, not a function call.
        // Dispatching here (rather than leaving it to a codegen-time
        // workaround) makes the body's shape correct regardless of whether
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
        // A LITERAL index into a real tuple is a static projection
        // (IRTupleProj, same as destructuring emits) -- the checker's
        // cumulant(d, k) arm produces this shape once Dist has zonk-erased
        // to IRTTuple. Everything else stays on the poly-pack path.
        match tuple.Type, index.Kind with
        | IRTTuple _, TExprLit (LitInt n) -> IRTupleProj (tup, int n, false)
        | _ ->
            let idx = lowerTypedExpr env index
            IRPolyIndex (tup, idx)
    
    | TExprPolyTail (pack, drop) ->
        IRPolyTail (lowerTypedExpr env pack, drop)

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
        // Lower to IRComplex, not IRTuple: complex is a scalar at IR level,
        // and flattening to a tuple of floats would let downstream code
        // reshape it as part of the surrounding rank, producing wrong-rank
        // arrays.
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
            SharedIndexTypes = info.SharedIndexTypes
        }
    
    | TExprObjectFor info ->
        IRObjectFor {
            Kernel = lowerTypedExpr env info.Kernel
            CommGroups = info.CommGroups
            InputRanks = info.InputRanks
            OutputRank = info.OutputRank
        }
    
    | TExprApply info when info.IsComposeApply ->
        // Slot-inverted compose application: `(o1 >>@ o2) <@> A`. TypeCheck
        // flagged this with IsComposeApply = true, storing the input arrays
        // in both `info.Arrays` and (redundantly) `info.Kernel`; the IR
        // form `IRComposeApply` carries them only in `InputArrays`.
        IRComposeApply {
            Composition = lowerTypedExpr env info.Loop
            InputArrays = info.Arrays |> List.map (lowerTypedExpr env)
            OutputType = info.OutputType
        }

    | TExprApply info ->
        // Canonical apply. Symmetry info already computed during type
        // checking. Lift the kernel ONCE and reuse the lifted IRVar for the
        // loop-provenance IRObjectFor.Kernel, rather than re-lowering
        // info.Loop's embedded kernel: TypeCheck stores the same eta-lambda
        // in both info.Kernel and (inside) info.Loop's object_for, and
        // lowering both used to mint two identical __lambda_N callables,
        // each anchoring its own arity/HM specialization chain (the HM
        // clone path bypasses spec dedup) -- producing duplicate C++
        // definitions once multiple arities of a recursive poly kernel are
        // specialized. The Loop kernel is codegen-dead for a canonical apply
        // (genApplyCombinator reads only info.Kernel), so sharing the single
        // lifted id is safe and lets the existing dedup collapse the chains.
        let loweredKernel = lowerTypedExpr env info.Kernel
        let loweredLoop =
            match info.Loop.Kind, loweredKernel with
            | TExprObjectFor ofInfo, IRVar _ ->
                IRObjectFor {
                    Kernel = loweredKernel
                    CommGroups = ofInfo.CommGroups
                    InputRanks = ofInfo.InputRanks
                    OutputRank = ofInfo.OutputRank
                }
            | _ -> lowerTypedExpr env info.Loop
        IRApplyCombinator {
            Loop = loweredLoop
            Kernel = loweredKernel
            Arrays = info.Arrays |> List.map (lowerTypedExpr env)
            Identities = info.Identities
            ArrayTypes = info.ArrayTypes
            SharedIndexTypes = info.SharedIndexTypes
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

    | TExprFallback (l, r) ->
        IRFallback (lowerTypedExpr env l, lowerTypedExpr env r)
    
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

    | TExprJoin (exprs, dim) ->
        IRJoin ((exprs |> List.map (lowerTypedExpr env)), dim)
    
    | TExprPure e ->
        IRPure (lowerTypedExpr env e)
    
    | TExprCompute e ->
        // CONSTANT-FILL FOLD: `replicate(N, pure(lit)) |> compute` with a
        // concrete count and a literal body is exactly an N-element array
        // literal -- lower it as IRArrayLit so it rides the array-literal
        // machinery everywhere (the general IRSequence realization is
        // main-body-only). Non-literal counts and non-constant bodies keep
        // the general combinator path.
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
        // |> read will force the deferred provider read that load_as
        // produces; until that exists, reading lowers to the operand itself
        // (a no-op).
        lowerTypedExpr env e

    | TExprFillRandom _ ->
        // Only meaningful as an annotated top-level let-binding value, where
        // TDeclLet intercepts it (needs the binding's array type for the
        // shape, records it in RandomInits). Reaching here means it was used
        // inline / in a nested let, which has no annotation to supply the shape.
        failwith "fill_random(mod) is only valid as an annotated top-level let-binding value (let A: Array<..> = fill_random(mod))"

    | TExprRandGen _ ->
        // Materialized only as a top-level let-binding value, where TDeclLet
        // intercepts it (records the kind/key/params in RandomInits, allocates
        // the self-typed array). Reaching here means it was used inline / nested.
        failwith "rand.<fam>(...) is only valid as a top-level let-binding value (let A = rand.uniform(key, n))"

    | TExprCompound _ ->
        // Only meaningful as a top-level let-binding value, where TDeclLet
        // intercepts it (records the lowered dense + mask in CompoundInits,
        // leaves a unit placeholder). Reaching here means it was used inline
        // or nested, which the compound-construction codegen path does not handle.
        failwith "compound(dense, mask) is only valid as a top-level let-binding value (let B = compound(dense, mask))"

    | TExprSparse _ ->
        // Same top-level-let-only discipline as compound(dense, mask): the
        // TDeclLet loop intercepts and records the values expr in SparseInits.
        failwith "sparse(values, keys) is only valid as a top-level let-binding value (let S = sparse(values, keys))"
    
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
         // materializing -- codegen emits one nest with scalar accumulators.
         | (TExprApply _ | TExprFusion _), Some seed ->
            IRReduceCompute (lowerTypedExpr env array, lowerTypedExpr env kernel,
                             lowerTypedExpr env seed)
         | _ ->
            let srcIR = lowerTypedExpr env array
            let kernelIR = lowerTypedExpr env kernel
            let initIR = init |> Option.map (lowerTypedExpr env)
            match srcIR with
            | IRArrayLit _ ->
                // Array-LITERAL source (`reduce([a, b, c], op)`): the fold
                // codegen NAMES its source (extents + element reads), which
                // an inline literal cannot provide -- bind it to a fresh id.
                let srcId = env.Builder.FreshId()
                IRLet (srcId, srcIR, IRReduce (IRVar (srcId, array.Type), kernelIR, initIR))
            | _ -> IRReduce (srcIR, kernelIR, initIR))

    | TExprProdSum args ->
        IRProdSum (args |> List.map (lowerTypedExpr env))

    | TExprTranspose (array, dim1, dim2) ->
        IRTranspose (lowerTypedExpr env array, dim1, dim2)
    | TExprDecompact (array, dim) ->
        IRDecompact (lowerTypedExpr env array, dim)
    | TExprGram (left, right, isSameArray) ->
        IRGram (lowerTypedExpr env left, lowerTypedExpr env right, isSameArray)
    | TExprMatmul (left, right) ->
        IRMatmul (lowerTypedExpr env left, lowerTypedExpr env right)
    | TExprEigh operand ->
        IREigh (lowerTypedExpr env operand)
    | TExprSolve (matrix, rhs) ->
        IRSolve (lowerTypedExpr env matrix, lowerTypedExpr env rhs)
    | TExprArrayNegate array ->
        IRArrayNegate (lowerTypedExpr env array)
    | TExprArrayConjugate array ->
        IRArrayConjugate (lowerTypedExpr env array)
    
    | TExprExtents array ->
        // Rank-1: a single IRExtent (arr, 0). Rank-N: a tuple of
        // IRExtent (arr, i) for i in 0..N-1.
        let arr' = lowerTypedExpr env array
        match array.Type with
        | ArrayElem arrTy when arrTy.IndexTypes.Length = 1 ->
            IRExtent (arr', 0)
        | ArrayElem arrTy ->
            let n = arrTy.IndexTypes.Length
            IRTuple (List.init n (fun i -> IRExtent (arr', i)))
        | _ ->
            // Typecheck should have rejected -- degenerate fallback rather
            // than crash; the IR validator will catch oddities.
            IRExtent (arr', 0)
    
    | TExprZero ->
        // Lower to type-appropriate zero literal based on resolved type
        match texpr.Type with
        | IRTScalar ETInt32 | IRTScalar ETInt64 -> IRLit (IRLitInt 0L)
        | IRTIdxTagged (IRTScalar (ETInt32 | ETInt64), _) -> IRLit (IRLitInt 0L)
        | IRTScalar ETBool -> IRLit (IRLitBool false)
        | IRTScalar ETFloat32 | IRTScalar ETFloat64 -> IRLit (IRLitFloat 0.0)
        | IRTInfer _ -> IRLit (IRLitFloat 0.0)  // unresolved defaults to float
        | ArrayElem _ ->
            // An array-typed zero that reached lowering sits in a position
            // the binding-site materialization (inferLetBindingValue's zero
            // arm) does not cover -- emitting IRZero here would render as a
            // scalar `0` under an array type (a null pointer). Fail loudly.
            failwith "zero at an array type is only materialized at an annotated let binding (`let A: Array<...> = zero`). In other positions (a function's return expression, a call argument), bind it first: `let z: Array<...> = zero` and use `z`."
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

    | TExprConstraintCheck (cond, message) ->
        // Carry the constraint's source span into IR so the runtime panic
        // (BL8001) can report file:line. texpr is the whole node in hand.
        IRConstraintCheck (lowerTypedExpr env cond, message, texpr.Span)
    
    | TExprSequence exprs ->
        // sequence(c1, c2, ..., cn) -> IRSequence (flat n-ary parallel)
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
    // standalone function return, wrap it in IRCompute. This applies only to
    // bare IRApplyCombinator -- `method_for { ... }` and similar combinator
    // forms that need a destination to materialize into. genFuncBody's
    // return-position match handles `IRCompute(IRApplyCombinator _)` by
    // synthesizing an internal let binding and running the full combinator
    // codegen; use-site rendering is identical either way, so the wrap
    // doesn't change behavior at existing use sites.
    let bodyWrapped =
        match body' with
        | IRApplyCombinator _ | IRComposeApply _ -> IRCompute body'
        | _ -> body'

    // Build unified IRCallable. info.ReturnType comes from TypeCheck, so the
    // lambda has a concrete return type; we trust that annotation. Lambda-level
    // parallelism flows TypedLambdaInfo.Parallel -> here via the shared
    // extractor (same logic a function's where-clause uses).
    let (lamParallelism, lamIsOmp, lamIsCuda, lamBlock, lamIsMpi) =
        extractParallelism info.Parallel (info.Params |> List.map (fun p -> p.Name))
    // A named, recursive lambda (`let const name = lambda ...` whose body
    // refers to itself) carries a self-binding: give the lifted callable the
    // real name and the id the body's self-reference resolves to, so the
    // emitted top-level C++ function can call itself. An anonymous lambda
    // gets the default synthesized "__lambda_<id>" name and a fresh id.
    let lamOpts =
        match info.SelfBinding with
        | Some (selfName, selfId) ->
            { defaultLambdaOptions with NameOverride = Some selfName; IdOverride = Some selfId }
        | None -> defaultLambdaOptions
    // A `where anticomm(a, b)` group is an axis group exactly like a comm
    // group (same fusion, same triangular iteration), so it rides CommGroups
    // for every grouping consumer, with the separate AntisymGroups list
    // carrying the one extra bit: the simplex is STRICT (no diagonal, sign
    // flip on swapped reads). IsCommutative stays the user's comm
    // declaration: an antisym kernel is NOT commutative.
    let callable =
        { mkCallable env.Builder lamOpts paramInfos bodyWrapped info.ReturnType
                     captures info.IsCommutative (info.CommGroups @ info.AntisymGroups)
                     lamParallelism lamIsOmp lamIsCuda lamBlock lamIsMpi
            with AntisymGroups = info.AntisymGroups
                 // The apply seam's per-parameter sign summary rides along
                 // the same way, so codegen and the interpreter can hand
                 // IR.deduceWreathTie the values typecheck judged from.
                 SignParities = info.SignParities }
    // Emit IRVar(callable.Id, funcType): the callable lives in
    // LiftedCallables -> module.Functions, and the IRVar carries just the
    // function type for type-inference and consumer dispatch. Consumers use
    // `resolveCallable` to walk back when they need params/body/captures.
    // The function type uses the regular params only -- captures are an
    // implementation detail of the lifted function's signature.
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

/// Lower a value-position literal, reconciling the source value with the
/// resolved element type (mirrors `TExprZero`). A numeric literal that flexed
/// to a wider type (e.g. an int literal pinned to Float64) emits the matching
/// constructor so it stays consistent with `CarriedType`/CodeGen. An unpinned
/// (`IRTInfer`) or non-scalar type falls back to the natural constructor: the
/// literal stays a scalar value and any consuming op broadcasts it.
and lowerLiteralValued lit (ty: IRType) : IRLit =
    match lit with
    | LitInt n ->
        match ty with
        | AnyPrimElem (ETFloat32 | ETFloat64) -> IRLitFloat (float n)
        | _ -> IRLitInt n
    | LitFloat f ->
        match ty with
        | AnyPrimElem (ETInt32 | ETInt64) -> IRLitInt (int64 f)  // defensive; narrowing normally rejected
        | _ -> IRLitFloat f
    | _ -> lowerLiteralToIRLit lit

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
            // A named, recursive lambda binding (`let const f = lambda ... f
            // ...`, incl. the nested-`function` desugar) lifts to a
            // module-level callable whose id IS binding.VarId, so `f`
            // resolves to that callable everywhere. The lowered value is
            // then a bare self-reference IRVar(binding.VarId); wrapping it
            // in an IRLet would create a degenerate self-alias and a stray
            // main-local shadow of the top-level function, so skip the
            // IRLet -- the callable was already registered above.
            let isSelfBoundLambda =
                match binding.Value.Kind with
                | TExprLambda info ->
                    (match info.SelfBinding with Some (_, id) -> id = binding.VarId | None -> false)
                | _ -> false
            // IRLet has no mutability slot, so record mut ARRAY lets in the
            // module side table -- codegen/interp give them copy semantics
            // (a mut binding initialized from an existing array must not
            // alias its storage).
            (match binding.Type with
             | ArrayElem _ when binding.IsMutable ->
                 env.MutableArrayLets.Add binding.VarId
             | _ -> ())
            let env' = bindTypedVar binding.Name binding.VarId env
            if isSelfBoundLambda then
                lowerTypedBlock env' rest finalExpr
            elif binding.SubBindings.IsEmpty && binding.PostChecks.IsEmpty then
                let body = lowerTypedBlock env' rest finalExpr
                IRLet (binding.VarId, value, body)
            else
                // Destructuring let inside a block (`let (x, y) = p`): chain
                // a projection IRLet per pattern leaf after the primary
                // binding -- without these the leaf VarIds dangle.
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
                // Constraint guards run right after the destructure, before
                // the rest of the block.
                let withChecks =
                    List.foldBack (fun (checkId, tCheck) acc ->
                        IRLet (checkId, lowerTypedExpr env'' tCheck, acc)) binding.PostChecks body
                let indexedSubs =
                    binding.SubBindings |> List.mapi (fun i (name, subId, _subTy) -> (i, name, subId))
                let chained =
                    List.foldBack (fun (i, name, subId) acc ->
                        let projExpr = subBindingValue binding isStruct isFlat i name
                        IRLet (subId, projExpr, acc)) indexedSubs withChecks
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
    // Extract the resolved param/return types from the typed section's
    // function type instead of hardcoding Float64: TypeCheck.inferExpr
    // ExprSection types sections polymorphically (`a -> a -> a`), and
    // subsequent unifications in consumer-position handlers bind that fresh
    // `a` to the actual context type. By zonk time the section's funcTy
    // carries the resolved scalar type. Float64 is only a fallback for
    // sections whose type genuinely couldn't be resolved.
    let (paramTy, retTy) =
        match funcTy with
        | IRTArrow (slots, r, _) when slots.Length = 2 ->
            // Both slots should resolve to the same scalar; read the first
            // as canonical.
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
    // Comparison and logical ops produce bool regardless of operand type;
    // arithmetic ops produce the operand element type. retTy from funcTy
    // should already encode this, but recompute defensively so a malformed
    // funcTy doesn't put a wrong return type on the lifted callable.
    let retType =
        match irOp with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
            IRTScalar ETBool
        | _ -> retTy
    let commGroups = if isComm then [[0; 1]] else []
    let callable = mkLambdaCallable env.Builder parms body retType [] isComm commGroups [] false false 256 false
    env.LiftedCallables.Add(callable)
    let funcType =
        let paramTypes = callable.Params |> List.map (fun p -> p.Type)
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Lower a partial operator application to a lambda
and lowerTypedPartialApp env (op: BinOp) (argExpr: IRExpr) (isLeft: bool) (funcTy: IRType) : IRExpr =
    // Inlines argExpr into the lifted kernel with NO captures -- only safe
    // when argExpr is a literal or references module-level ids. The
    // array<->scalar broadcast path hoists computed scalars into a let and
    // threads them as captures instead (lowerTypedBinOp); explicit
    // partial-app sections (`(s +)`) with function-local operands still
    // share this inline hazard.
    lowerTypedPartialAppWith env op argExpr isLeft funcTy []

and lowerTypedPartialAppWith env (op: BinOp) (argExpr: IRExpr) (isLeft: bool) (funcTy: IRType) (captures: CaptureInfo list) : IRExpr =
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
    // Same resolved-type extraction as lowerTypedSection: pull the partial
    // application's param/return scalar types from the typed function type
    // (`a -> a`, or `a -> Bool` for comparisons), reading slot 0 as the
    // param type. argExpr was already lowered and carries its own type via
    // the IR, so it doesn't need consulting here.
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
    let callable = mkLambdaCallable env.Builder parms body retType captures false [] [] false false 256 false
    env.LiftedCallables.Add(callable)
    let funcType =
        let paramTypes = callable.Params |> List.map (fun p -> p.Type)
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Lower typed binary operations
and lowerTypedBinOp env mode op l r leftExpr rightExpr resultType =
    let irMode = match mode with Elementwise -> IRElementwise | Outer -> IROuter
    
    // Check if both operands are arrays -- if so, synthesize object_for loop
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
        // Lambda params for arithmetic ops require concrete scalar types;
        // default to Float64 if the array's elem type isn't a primitive
        // (e.g. struct or unresolved infer), since codegen would otherwise
        // fail downstream.
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
        // Comparison/logical ops produce bool; arithmetic ops keep the left
        // operand's element type (matches IRBinOp typing conventions).
        let kernelRetType =
            match irOp with
            | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
                IRTScalar ETBool
            | _ -> IRTScalar elemTypeL
        let commGroups = if mode = Elementwise then [[0; 1]] else []
        let lambdaInfo =
            mkLambdaCallable env.Builder parms body kernelRetType [] false commGroups [] false false 256 false
        env.LiftedCallables.Add(lambdaInfo)
        // Kernel slot references the lifted callable via IRVar;
        // genObjectForApplication uses resolveCallable + wrapper to consume it.
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
    elif mode = Elementwise && isArithOp && (leftIsArray <> rightIsArray) then
        // Array<->scalar broadcast: `A > 2.0`, `A + a`, `2.0 / A`. The op is
        // T^0 -> T^0 -> T^0; co-iteration peels the array operand down to
        // T^0 and the scalar already matches that rank, so iterate the
        // array with the scalar held fixed via a 1-param partial-application
        // kernel (lambda(x) -> x op s, or lambda(x) -> s op x); isLeft
        // selects the side. Comparisons/logicals produce Bool elements;
        // arithmetic keeps the result type's element type.
        let kernelRet =
            match op with
            | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe | OpAnd | OpOr -> IRTScalar ETBool
            | _ ->
                match IR.stripUnits resultType with
                | ArrayElem a -> a.ElemType
                | _ -> IRTScalar ETFloat64
        let (arrayIR, scalarIR, scalarTy, fixedIsLeft, funcTy) =
            if leftIsArray then
                // A op scalar  ->  lambda(x) -> x op scalar   (fixed arg on right, isLeft = false)
                let elemTy = (match leftExpr.Type with ArrayElem a -> a.ElemType | _ -> IRTScalar ETFloat64)
                (l, r, rightExpr.Type, false, mkFuncArrow [elemTy] kernelRet)
            else
                // scalar op A  ->  lambda(x) -> scalar op x   (fixed arg on left, isLeft = true)
                let elemTy = (match rightExpr.Type with ArrayElem a -> a.ElemType | _ -> IRTScalar ETFloat64)
                (r, l, leftExpr.Type, true, mkFuncArrow [elemTy] kernelRet)
        match scalarIR with
        | IRLit _ ->
            // Literal fixed arg (`A > 2.0`): a literal cannot dangle and
            // needs no hoisting.
            let kernelVar = lowerTypedPartialApp env op scalarIR fixedIsLeft funcTy
            let objInfo : ObjectForInfo = {
                Kernel = kernelVar
                CommGroups = []
                InputRanks = [0]
                OutputRank = 0
            }
            IRApp(IRObjectFor objInfo, [arrayIR], resultType)
        | _ ->
            // Computed or variable fixed arg (`a - mymean(a)` inside a
            // function body): inlining it into the lifted kernel leaves any
            // function-local VarIds inside it DANGLING (BL6001) and would
            // recompute the scalar per element. Hoist it into a let so it
            // evaluates once, and thread the let-bound var into the kernel
            // as a proper CAPTURE -- the capture-forwarding machinery closes
            // the scope at every consumer site.
            let sVar = env.Builder.FreshId()
            let cap : CaptureInfo = {
                Id = sVar
                Name = sprintf "__bc_s%d" sVar
                Type = scalarTy
                IsMutable = false
            }
            let kernelVar = lowerTypedPartialAppWith env op (IRVar (sVar, scalarTy)) fixedIsLeft funcTy [cap]
            let objInfo : ObjectForInfo = {
                Kernel = kernelVar
                CommGroups = []
                InputRanks = [0]
                OutputRank = 0
            }
            IRLet (sVar, scalarIR, IRApp(IRObjectFor objInfo, [arrayIR], resultType))
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
            Arrays = []; Identities = []; ArrayTypes = []; SharedIndexTypes = []
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
                SharedIndexTypes = []
            }
        | _ -> IRArrayProduct (l, r)  // fallback for non-method_for operands
    | OpFunctor -> IRFunctorMap (l, r)
    | OpChoice -> IRChoice (l, r)
    | OpFallback -> IRFallback (l, r)
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

    // Extract parallelism from the where-clause strategy list via the shared
    // extractor (same logic a lambda's Parallel list uses): omp -> per-param
    // (index, level) detail + flag, cuda -> flag + block size, mpi -> flag.
    let declParallel = match decl.WhereClause with Some wc -> wc.Parallel | None -> []
    let (parallelism, isOmpParallel, isCudaKernel, cudaBlockSize, isMpiParallel) =
        extractParallelism declParallel (decl.Params |> List.map (fun p -> p.Name))

    // Bind parameters in environment for body lowering
    let mutable paramEnv = { env with PolyParamNames = polyParamNames }
    let irParams = decl.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)

    let body = lowerTypedExpr paramEnv decl.Body

    // Source-level functions live at top level and have no enclosing scope to
    // capture from, so Captures = []. The function-only metadata (name, id,
    // static, arity-poly) is supplied via CallableOptions; everything else is
    // shared with the lambda-construction path through mkCallable.
    let funcOpts : CallableOptions =
        { NameOverride = Some decl.Name
          IdOverride   = Some decl.FuncId
          IsStatic     = decl.IsStatic
          IsArityPoly  = isArityPoly
          ArityParam   = polyParamNames |> List.tryHead }
    let funcDef =
        mkCallable env.Builder funcOpts irParams body decl.ReturnType []
                   (not decl.CommGroups.IsEmpty) decl.CommGroups
                   parallelism isOmpParallel isCudaKernel cudaBlockSize isMpiParallel

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
    | TTDStruct (name, _, fields) ->
        IRTDStruct (name, fields)
    | TTDVariant (name, _, variants) ->
        IRTDVariant (name, variants)
    | TTDMutualGroup _ ->
        // Lowered by lowerTypedDecl's TDeclType arm into one IRTDAlias per
        // member; reaching here is an internal invariant violation.
        failwith "TTDMutualGroup lowers via lowerTypedDecl (one alias per member)"

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
            let projExpr = subBindingValue binding isStruct isFlat i name
            let env' = bindTypedVar name subId env'
            { Id = subId; Name = name; Type = subTy; Value = projExpr; IsConst = true; IsMutable = false })
        let env'' = binding.SubBindings |> List.fold (fun e (name, subId, _) -> bindTypedVar name subId e) env'
        // Constraint guards run right after the destructure. Their IRIds were
        // allocated by the checker after the sub-binding ids, so the module's
        // id-ordered emission places them correctly.
        let checkBindings =
            binding.PostChecks |> List.mapi (fun i (checkId, tCheck) ->
                { Id = checkId; Name = sprintf "__mg_check%d" i; Type = IRTUnit
                  Value = lowerTypedExpr env'' tCheck; IsConst = true; IsMutable = false })
        ([Choice2Of3 irBinding] @ (subIRBindings |> List.map Choice2Of3) @ (checkBindings |> List.map Choice2Of3), env'')
    
    | TDeclFunction funcDecl ->
        let (funcDef, env') = lowerTypedFuncDecl env funcDecl
        ([Choice1Of3 funcDef], env')
    
    | TDeclStatic binding ->
        // Static values: use pre-evaluated value if available, else lower
        // normally. The fast path is for plain `let static x` only -- a
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
                         | StaticEval.SVStruct (n, _) -> IRTNamed n
                         | _ -> IRTUnit
                let bd = { Id = binding.VarId; Name = binding.Name; Type = ty; Value = irValue; IsConst = true; IsMutable = false }
                let env' = bindTypedVar binding.Name binding.VarId env
                (bd, env')
            | _ ->
                // Fallback: lower as normal binding
                let (irBinding, env') = lowerTypedBinding env binding
                (irBinding, env')

        // Emit sub-bindings for destructured patterns. The static evaluator's
        // bindPattern has already populated env.StaticValues with each
        // sub-name -> value mapping for tuple destructuring; prefer those
        // direct constants, falling back to tuple projection of the primary
        // binding for shapes the static evaluator didn't reach.
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
                        // Direct static constant -- preferred path for
                        // statically-evaluated tuple destructuring.
                        let irValue = staticValueToIR sv
                        { Id = subId; Name = name; Type = subTy; Value = irValue
                          IsConst = true; IsMutable = false }
                    | None ->
                        // Projection fallback -- same shape as TDeclLet's branch.
                        let projExpr = subBindingValue binding isStruct isFlat i name
                        { Id = subId; Name = name; Type = subTy; Value = projExpr
                          IsConst = true; IsMutable = false }
                (acc @ [bd], bindTypedVar name subId e)
            ) ([], env')
        ([Choice2Of3 primary] @ (subIRBindings |> List.map Choice2Of3), envFinal)
    
    | TDeclType (TTDMutualGroup members) ->
        // One transparent alias typedef per member; the joint constraint is
        // emitted at binding sites, not in the type defs.
        (members |> List.map (fun (n, ty) -> Choice3Of3 (IRTDAlias (n, ty))), env)

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
        | Some pname, TExprLit (LitString path) ->
            match Blade.ProviderRegistry.tryFind pname with
            | None -> None
            | Some spec ->
                let providerModule = spec.LoadAsModule env.Builder binding.Name path
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
                let env' = { env' with ProviderPaths = Map.add binding.Name (pname, path) env'.ProviderPaths }
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
                       let (pname, path) = env.ProviderPaths.[root]
                       Some { Provider = pname
                              FilePath = path
                              VarName = varName
                              VarType = varArr
                              MaskName = Some maskName
                              MaskType = Some maskArr
                              Window = None
                              Streamed = false }
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
                       let (pname, path) = env.ProviderPaths.[root]
                       Some { Provider = pname
                              FilePath = path
                              VarName = varName
                              VarType = varArr
                              MaskName = None
                              MaskType = None
                              Window = None
                              Streamed = false }
                   | _ -> None)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Detect a streamed read: `let A = s.vars.A |> alias.stream`. Mirrors
/// tryPlainRead (dense whole-variable spec) but marks the spec Streamed:
/// codegen emits no materialization -- consuming nests inline fiber reads.
let tryStreamRead (env: TypedLowerEnv) (binding: TypedBinding) : ProviderReadSpec option =
    let rec rootName (e: TypedExpr) =
        match e.Kind with
        | TExprVar (n, _, _) -> Some n
        | TExprField (inner, _, _) -> rootName inner
        | _ -> None
    match binding.Value.Kind with
    | TExprApp ({ Kind = TExprField ({ Kind = TExprVar (alias, _, _) }, "stream", _) }, [inner])
        when Map.containsKey alias env.ProviderAliases ->
        (match inner.Kind with
         | TExprField (_, varName, _) ->
             (match rootName inner with
              | Some root when Map.containsKey root env.ProviderPaths ->
                  (match inner.Type with
                   | ArrayElem varArr ->
                       let (pname, path) = env.ProviderPaths.[root]
                       Some { Provider = pname
                              FilePath = path
                              VarName = varName
                              VarType = varArr
                              MaskName = None
                              MaskType = None
                              Window = None
                              Streamed = true }
                   | _ -> None)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Detect a windowed packed read: `let W = alias.read_window(s.vars.C, lo, hi)`.
/// The checker guarantees the shape (provider alias, packed operand, literal
/// integer bounds) and typed the binding as the WINDOW array (leading packed
/// extent hi-lo); this recovers store/var via the operand's root provider
/// binding, mirroring tryPlainRead, and records the window in the spec.
let tryWindowRead (env: TypedLowerEnv) (binding: TypedBinding) : ProviderReadSpec option =
    let rec rootName (e: TypedExpr) =
        match e.Kind with
        | TExprVar (n, _, _) -> Some n
        | TExprField (inner, _, _) -> rootName inner
        | _ -> None
    match binding.Value.Kind with
    | TExprApp ({ Kind = TExprField ({ Kind = TExprVar (alias, _, _) }, "read_window", _) }, [inner; tLo; tHi])
        when Map.containsKey alias env.ProviderAliases ->
        (match inner.Kind, tLo.Kind, tHi.Kind with
         | TExprField (_, varName, _), TExprLit (LitInt lo), TExprLit (LitInt hi) ->
             (match rootName inner with
              | Some root when Map.containsKey root env.ProviderPaths ->
                  (match binding.Type with
                   | ArrayElem winArr ->
                       let (pname, path) = env.ProviderPaths.[root]
                       Some { Provider = pname
                              FilePath = path
                              VarName = varName
                              VarType = winArr
                              MaskName = None
                              MaskType = None
                              Window = Some (lo, hi)
                              Streamed = false }
                   | _ -> None)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Detect a provider write: `let _ = alias.write("path", A)`. Recovers the
/// source binding, its array type, and dimension names -- named index types
/// when the array's index Ids match module-level IRTDIndexType defs,
/// synthesized dim<i> otherwise. The binding lowers to a unit placeholder;
/// codegen emits a flatten prologue + the provider's writer at the
/// ProviderWrites intercept.
let tryProviderWrite (env: TypedLowerEnv) (typeDefs: IRTypeDef list) (binding: TypedBinding) : ProviderWriteSpec option =
    match binding.Value.Kind with
    | TExprApp ({ Kind = TExprField ({ Kind = TExprVar (alias, _, _) }, "write", _) }, [tPath; tValue]) ->
        (match Map.tryFind alias env.ProviderAliases, tPath.Kind, tValue.Kind with
         | Some pname, TExprLit (LitString path), TExprVar (srcName, srcId, _) ->
             (match tValue.Type with
              | ArrayElem arrTy ->
                  // Dimension names, best source first: (a) the source is
                  // itself a provider read, ask its provider for the store's
                  // names; (b) a module-level named index type with a
                  // matching Id; (c) synthesized dim<i>.
                  let fromSourceStore =
                      match Map.tryFind srcId env.ProviderReads with
                      | Some rspec ->
                          (match Blade.ProviderRegistry.tryFind rspec.Provider with
                           | Some p -> p.VarDimNames rspec.FilePath rspec.VarName
                           | None -> None)
                      | None -> None
                  let dimNames =
                      match fromSourceStore with
                      | Some names when names.Length = arrTy.IndexTypes.Length -> names
                      | _ ->
                          arrTy.IndexTypes |> List.mapi (fun i idx ->
                              typeDefs
                              |> List.tryPick (function
                                  | IRTDIndexType (n, it) when it.Id = idx.Id -> Some n
                                  | _ -> None)
                              |> Option.defaultValue (sprintf "dim%d" i))
                  Some { Provider = pname
                         FilePath = path
                         VarName = srcName
                         SourceId = srcId
                         SourceType = arrTy
                         DimNames = dimNames }
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Lower a typed module
let lowerTypedModule (env: TypedLowerEnv) (modul: TypedModule) (rawDecls: Located<Decl> list option) : IRModule * ModuleExport =
    // Fresh LiftedCallables / MutableArrayLets for this module. Lifted lambdas
    // and mut-let records from a previous module's lowering must not leak into
    // this one.
    let env = { env with LiftedCallables = ResizeArray<IRCallable>()
                         MutableArrayLets = ResizeArray<IRId>() }
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
                    // Check if this is a provider-module import (e.g. `import netcdf as nc`)
                    match qname with
                    | [pname] when (Blade.ProviderRegistry.tryFind pname).IsSome ->
                        currentEnv <- { currentEnv with ProviderAliases = Map.add alias pname currentEnv.ProviderAliases }
                    | _ ->
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
            // Deferred compound read (`load_compound(var, mask) |> read`): no
            // C++ is emitted here. genBinding materializes it via
            // genReadCompoundVar when it sees the binding's IRId in
            // ctx.ProviderReads. Value is a unit placeholder.
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
            // Deferred dense read (`sample.vars.A |> read`): materialized in
            // codegen via genReadVar (the no-mask arm of the ProviderReads
            // intercept). Mirrors the compound arm above; the matchers are
            // mutually exclusive (compound wraps a load_compound app, this
            // wraps a plain field access).
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
        | TDeclLet binding when (tryStreamRead currentEnv binding).IsSome ->
            // Streamed read (`alias.stream(view)`): a deferred-read binding
            // whose spec is marked Streamed -- codegen emits the stream-open
            // prologue only; nests inline the fiber reads.
            let spec = (tryStreamRead currentEnv binding).Value
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
        | TDeclLet binding when (tryWindowRead currentEnv binding).IsSome ->
            // Windowed packed read (`alias.read_window(view, lo, hi)`): same
            // deferred-read mechanics as the arms below; the spec carries the
            // window and the binding's type is the window array.
            let spec = (tryWindowRead currentEnv binding).Value
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
        | TDeclLet binding when (tryProviderWrite currentEnv types binding).IsSome ->
            // Deferred provider write: codegen emits the flatten prologue +
            // the provider's writer when it sees the binding's IRId in
            // ctx.ProviderWrites. Mirrors the read intercepts above.
            let spec = (tryProviderWrite currentEnv types binding).Value
            let bd = {
                Id = binding.VarId
                Name = binding.Name
                Type = IRTUnit
                Value = IRLit IRLitUnit
                IsConst = true
                IsMutable = false
            }
            bindings <- bindings @ [bd]
            currentEnv <- bindTypedVar binding.Name binding.VarId currentEnv
            currentEnv <- { currentEnv with ProviderWrites = Map.add binding.VarId spec currentEnv.ProviderWrites }
        | TDeclLet binding when (match binding.Value.Kind with TExprFillRandom _ -> true | _ -> false) ->
            // Random-fill constructor: materialized in codegen via allocate<>
            // + the runtime fill_random (the RandomInits intercept in
            // genBinding). The modulus is lowered and recorded.
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
            currentEnv <- { currentEnv with RandomInits = Map.add binding.VarId (FillModulus modIR) currentEnv.RandomInits }
        | TDeclLet binding when (match binding.Value.Kind with TExprRandGen _ -> true | _ -> false) ->
            // rand.<fam>(key, params.., shape): materialized via allocate<> +
            // the runtime blade_rand fill (RandomInits/RandGen intercept).
            // The key and the family's runtime Float64 params are lowered in
            // the CURRENT env (so they may reference earlier bindings, exactly
            // as the key may) and recorded. Mirrors the fill_random arm.
            let kind, keyIR, parIRs =
                match binding.Value.Kind with
                | TExprRandGen (k, key, pars, _) ->
                    k, lowerTypedExpr currentEnv key, (pars |> List.map (lowerTypedExpr currentEnv))
                | _ -> "uniform", IRLit (IRLitInt 0L), []  // unreachable: guarded by the `when` above
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
            currentEnv <- { currentEnv with RandomInits = Map.add binding.VarId (RandGen (kind, keyIR, parIRs)) currentEnv.RandomInits }
        | TDeclLet binding when (match binding.Value.Kind with TExprCompound _ -> true | _ -> false) ->
            // Compound-construction constructor: materialized via P0
            // (genCompoundIndexFromMask) + a dense->compact scatter (the
            // CompoundInits intercept). Dense and mask are lowered and
            // recorded. Mirrors the fill_random arm.
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
        | TDeclLet binding when (match binding.Value.Kind with TExprSparse _ -> true | _ -> false) ->
            // Sparse-construction constructor: materialized via the sparse
            // index build + a straight pool copy (values are already in key
            // order, no scatter). Mirrors the compound arm above.
            let valuesIR =
                match binding.Value.Kind with
                | TExprSparse (v, _) -> lowerTypedExpr currentEnv v
                | _ -> IRLit IRLitUnit  // unreachable: guarded by the `when` above
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
            currentEnv <- { currentEnv with SparseInits = Map.add binding.VarId valuesIR currentEnv.SparseInits }
        | TDeclLet binding when isProviderCall currentEnv binding.Value ->
            match tryInvokeProvider currentEnv binding with
            | Some (providerTypes, bd, env') ->
                types <- types @ providerTypes
                for td in providerTypes do
                    match td with
                    | IRTDStruct (name, fields) ->
                        currentEnv <- { currentEnv with StructDefs = Map.add name fields currentEnv.StructDefs }
                    | _ -> ()
                bindings <- bindings @ [bd]
                currentEnv <- env'
            | None ->
                // Provider invocation failed -- fall through to normal lowering
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
                        | IRTDStruct (name, fields) ->
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
                    | IRTDStruct (name, fields) ->
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
        ProviderWrites = currentEnv.ProviderWrites
        RandomInits = currentEnv.RandomInits
        CompoundInits = currentEnv.CompoundInits
        SparseInits = currentEnv.SparseInits
        MutableArrayLets = Set.ofSeq currentEnv.MutableArrayLets
        // Nothing is synthesized-from-another at lowering time; the
        // copy-producing passes (shapeMonomorphizeModule) fill this in.
        DerivedFuncOrigins = Map.empty
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
        // (e.g. `T` in `Array<T like Idx<n>>`, or `T` extracted from
        // `Poly<T^N>`'s base type) with concrete types learned from each
        // call site. Runs after Poly so per-param/per-arg unification is
        // straightforward -- each pack has already been expanded.
        let irModule = IR.monomorphizeHMFunctions irModule env.Builder
        currentExports <- Map.add moduleName exports currentExports
        irModules <- irModules @ [irModule]

    // SHAPE monomorphization: a function over a symbolic extent (`Idx<n>`)
    // gets a specialized copy per distinct call-site extent signature, with
    // the cosmetic `IRParam` extent placeholders rewritten to `IRLit`. Must
    // run AFTER both monomorphizers (each creates call sites this one reads,
    // and neither carries extents itself) and before the combinator-producing
    // rewrites below, so the array types those build inherit the literals
    // rather than needing a second rewrite.
    //
    // It is the one pass here that runs over the WHOLE PROGRAM rather than per
    // module, which is what lets a literal-shaped call in module A specialize
    // a function defined in module B. That needs every module's arity/HM
    // monomorphization to have happened first -- hence the split loop -- and
    // costs nothing else: a module's exports are fixed by `lowerTypedModule`
    // before any of these passes run, so hoisting them out of the loop cannot
    // change what a later module sees, and for a single-module program the
    // builder mints exactly the same ids in exactly the same order as before.
    let irModules = IR.shapeMonomorphizeModules irModules env.Builder

    let irModules =
        irModules |> List.map (fun irModule ->
        // Rewrite raw array-typed binops into object_for combinators now
        // that pack-element operand types are concrete (post Poly+HM):
        // `A[i] + A[j]` on rank>=1 pack elements couldn't be recognized as
        // an array op at lowering time (its element type was an unresolved
        // var), so it gets the same elementwise-loop lowering top-level
        // `x + y` does.
        let irModule = IR.lowerArrayBinOpsModule irModule env.Builder
        // Lift inline forms (mask/sort/intersect/union/group_by/group_keys
        // appearing in non-let-RHS positions) into auto-let bindings so
        // codegen sees the canonical "let-bound" pattern uniformly.
        let irModule = IR.liftInlineFormsModule irModule env.Builder
        // mask+contains fusion always runs a linear scan; the semijoin
        // hash-set is a separate, not-yet-implemented optimization.
        irModule)

    { Modules = irModules }

// Typecheck warning surfacing (shared by every CLI lane)

/// Every typecheck warning channel, drained and assembled as coded, spanned
/// Diagnostics. Three producers feed it: `TypeEnv.WarningLog` (the checker's
/// own `emitWarning`s: BL4001/BL4003/BL4004/BL4010/BL9001, which survive the
/// checker's ERROR path); `ML.Equiv.CertSuggestions` (stage-6a certificate
/// suggestions, BL4011, filled by the ML elaborator); and
/// `ML.Galilean.GalCertSuggestions`, the galilean twin (BL4014), drained
/// AFTER the equiv channel so a file earning both reads equiv-then-galilean,
/// each channel in its own insertion order.
///
/// `skipPins` drops BL4010 for `--strict-pins`, which has already
/// re-reported exactly those as errors. BL4011 and BL4014 are deliberately
/// NOT dropped: strict-pins owns the STORAGE decision, and a certificate
/// owns no storage decision at all, so neither has a promoted-to-error twin
/// a filter here would be de-duplicating.
let typeCheckWarningDiagnostics (skipPins: bool) : Blade.Diagnostics.Diagnostic list =
    let own =
        Blade.TypeEnv.WarningLog.get ()
        |> List.filter (fun d -> not (skipPins && d.Code = "BL4010"))
    let certs =
        Blade.ML.Equiv.CertSuggestions.get ()
        |> List.map (fun (msg, span) ->
            Blade.Diagnostics.mkWarning "BL4011" Blade.Diagnostics.PhConstraints span msg)
    let galCerts =
        Blade.ML.Galilean.GalCertSuggestions.get ()
        |> List.map (fun (msg, span) ->
            Blade.Diagnostics.mkWarning "BL4014" Blade.Diagnostics.PhConstraints span msg)
    (own @ certs @ galCerts) |> List.distinct

/// The surfacing helper: one format, one stream for every CLI lane. Warnings
/// render exactly like errors (`warning[BL4010]: ...` + snippet) and go to
/// STDERR everywhere: warnings are diagnostics, and diagnostics belong on
/// stderr so `blade check` / `blade emit` stdout stays pipeable.
let printTypeCheckWarnings (useColor: bool) (sm: Blade.Diagnostics.SourceMap option)
                           (skipPins: bool) : unit =
    match typeCheckWarningDiagnostics skipPins with
    | [] -> ()
    | ds -> eprintfn "%s" (Blade.Diagnostics.Render.renderAll useColor sm ds)

// Convenience functions for testing

/// Main pipeline: Parse -> TypeCheck -> Lower
let lower (source: string) : Result<IRProgram, string> =
    match Blade.Parser.parseProgram source with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, _) ->
            // Warnings go to stderr so the pipeline output (the IR program)
            // stays clean; no SourceMap/TTY here, so rendering degrades to
            // headers + `--> file:line:col` locations.
            printTypeCheckWarnings false None false
            // Lowering can THROW on a failed compile-time provider load; keep
            // this convenience entry point from surfacing an unhandled exception.
            (try Ok (lowerTypedProgram typedProgram (Some program) builder)
             with ex -> Error ex.Message)
        | Error errors ->
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// Structured-diagnostics entry: like `lower`, but errors stay as coded,
/// spanned Diagnostics, warnings come back structured, and the retained
/// source text returns as a SourceMap for snippet rendering. `fileName`
/// (when known) is stamped into spans and keys the SourceMap.
let lowerDiag (fileName: string option) (source: string)
    : Result<IRProgram * string list, Blade.Diagnostics.Diagnostic list> * Blade.Diagnostics.SourceMap =
    let key = defaultArg fileName "<input>"
    let sm = Blade.Diagnostics.SourceMap.ofSources [ key, source ]
    let result =
        match Blade.Parser.parseProgramWithFile fileName source with
        | Error e -> Error [ Blade.Parser.diagnosticOfParseError fileName e ]
        | Ok program ->
            match Blade.TypeCheck.typeCheck program with
            | Error errors ->
                Error (errors |> List.map Blade.TypeEnv.diagnosticOfCompileError)
            | Ok (typedProgram, builder, warnings) ->
                // Lowering can THROW when a compile-time provider load fails
                // (e.g. `netcdf.load("missing.nc")` raises from
                // tryInvokeProvider). Convert it to a coded diagnostic so the
                // compile driver reports it cleanly instead of crashing.
                try Ok (lowerTypedProgram typedProgram (Some program) builder, warnings)
                with ex ->
                    Error [ Blade.Diagnostics.mkError "BL6002" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan ex.Message ]
    result, sm

/// Harness twin of `lower`: the same parse -> typecheck -> lower pipeline and
/// the same `Result`, but the typecheck warnings come back as coded
/// `Diagnostic`s instead of being rendered to stderr.
///
/// It exists because `lower`'s unconditional `printTypeCheckWarnings` is a
/// LEAK when the caller is the test suite: ~4k corpus files per run sprayed
/// their warnings into the console with no SourceMap and no file name (so each
/// one rendered as a bare `--> line:col`), interleaved with the parallel
/// progress lines of whichever tests happened to be running. Handing the
/// warnings back lets the runner hold each test's warnings against that test's
/// own `// WARN:` pins.
///
/// Two deliberate differences from `lower`:
///
///   * The drain happens on the typecheck-ERROR arm too, where `lower` prints
///     nothing. The warning channels survive the checker's error path — that
///     is exactly what `Cli.compileFile` relies on at its `Error ds` arm — so a
///     program that earned warnings before being refused really did earn them,
///     and the pin discipline can hold a reject-probe to them like any other
///     test.
///   * Nothing is printed, ever. Whether a warning is expected is a question
///     only the caller can answer.
///
/// On the Ok arm the drain happens BEFORE `lowerTypedProgram`, exactly where
/// `lower` prints, so the captured set is the set `lower` would have printed.
/// On a PARSE error nothing is drained: the checker never ran, so it never
/// reset the (AsyncLocal, reset-per-`typeCheck`) channels, and draining them
/// would hand this file the PREVIOUS file's warnings.
let lowerCaptured (source: string) : Result<IRProgram, string> * Blade.Diagnostics.Diagnostic list =
    match Blade.Parser.parseProgram source with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, _) ->
            let warnings = typeCheckWarningDiagnostics false
            let result =
                // Lowering can THROW on a failed compile-time provider load; keep
                // this convenience entry point from surfacing an unhandled exception.
                try Ok (lowerTypedProgram typedProgram (Some program) builder)
                with ex -> Error ex.Message
            result, warnings
        | Error errors ->
            let warnings = typeCheckWarningDiagnostics false
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs), warnings
    | Error e ->
        Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message), []

/// Lower multiple source files into a single IR program with cross-module imports
let lowerMultiSource (sources: (string * string) list) : Result<IRProgram, string> =
    match Blade.Parser.parseMultiSource sources with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, _) ->
            printTypeCheckWarnings false None false
            // Lowering can THROW on a failed compile-time provider load.
            (try Ok (lowerTypedProgram typedProgram (Some program) builder)
             with ex -> Error ex.Message)
        | Error errors ->
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message)

/// The multi-source twin of `lowerCaptured`, on exactly the same terms: same
/// pipeline as `lowerMultiSource`, warnings returned rather than printed,
/// drained on the typecheck-error arm as well as the Ok arm, and NOT drained
/// after a parse error (where the checker never ran to reset the channels).
let lowerMultiSourceCaptured (sources: (string * string) list)
    : Result<IRProgram, string> * Blade.Diagnostics.Diagnostic list =
    match Blade.Parser.parseMultiSource sources with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, _) ->
            let warnings = typeCheckWarningDiagnostics false
            let result =
                try Ok (lowerTypedProgram typedProgram (Some program) builder)
                with ex -> Error ex.Message
            result, warnings
        | Error errors ->
            let warnings = typeCheckWarningDiagnostics false
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs), warnings
    | Error e ->
        Error (sprintf "Parse error at %d:%d: %s" e.Line e.Col e.Message), []
