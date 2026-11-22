// Blade-DSL Lowering Pass: transforms AST to IR, with support for array
// identity tracking (triangular iteration), lambda capture analysis, pattern
// binding, arity polymorphism, and kernel irank/orank inference.

module Blade.Lowering

open System
open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
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
    // Never reached through a consumer (StaticEval normalizes), but total:
    // a Float32 is its float32 literal, an Int32 the cast-form Int32 literal.
    | StaticEval.SVFloat32 f -> IRLit (IRLitFloat32 f)
    | StaticEval.SVInt32 n -> IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt (int64 n)))
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
    /// Binders declared `let mut` (IRModule.LetMutBinders).
    LetMutBinders: ResizeArray<IRId>
    /// The name of the nearest enclosing binding or function the program
    /// wrote -- what an ANONYMOUS lambda or operator function lowered here
    /// prints as being `in` (IR.anonymousCallableName: `lambda in r`,
    /// `(/) in mk`). Set by every binding, function and named lambda around
    /// the expression being lowered (withLambdaScope); None at top level.
    LambdaScope: string option
    /// Lowering INSIDE a `where repro` function's body (gap G74): every lambda
    /// and operator section lifted from it is `IsRepro` too, so the repro
    /// demand vetoes the reorder licence of every fold the body runs (each
    /// lane's `foldReorderLicensed`), and an unseeded fold's seed takes the
    /// strict identity (lowerUnseededSeed).
    InRepro: bool
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
    LetMutBinders = ResizeArray<IRId>()
    LambdaScope = None
    InRepro = false
}

let bindTypedVar name id (env: TypedLowerEnv) : TypedLowerEnv =
    { env with Variables = Map.add name id env.Variables }

/// Lower inside the binding or function `name`: the anonymous functions in it
/// print as `lambda in <name>`. A name the program did not write (a bare
/// top-level expression's `__expr<n>`, a desugaring's carrier) is no scope --
/// the enclosing one stays.
let withLambdaScope (name: string) (env: TypedLowerEnv) : TypedLowerEnv =
    if isSynthesizedName name then env else { env with LambdaScope = Some name }

/// The operator function `op` as an anonymous callable here: `(/) in r`.
let operatorCallableName (env: TypedLowerEnv) (op: IRBinOp) : string =
    anonymousCallableName env.LambdaScope (operatorFunctionName op)

/// What an unbound lambda is, before `in <scope>`: `lambda` -- unless the
/// CHECKER wrote it (every parameter a synthesized `__` name) to lift an
/// operator over arrays (`a - mean(a)` is `method_for(zip(..)) <@>
/// lambda(__zl, __zr) -> __zl - __zr`, a broadcast's `lambda(__bx)`, a cast's
/// `lambda(__castv)`): then it is that operator's function, `(-)` / `sqrt` /
/// `Float32`, as Lowering's own operator kernels are.
let private unboundLambdaWhat (info: TypedLambdaInfo) : string =
    let checkerWritten =
        not info.Params.IsEmpty && info.Params |> List.forall (fun p -> isSynthesizedName p.Name)
    if not checkerWritten then "lambda" else
    match info.Body.Kind with
    | TExprBinOp (_, op, _, _) ->
        match op with
        | OpAdd -> operatorFunctionName IRAdd | OpSub -> operatorFunctionName IRSub
        | OpMul -> operatorFunctionName IRMul | OpDiv -> operatorFunctionName IRDiv
        | OpMod -> operatorFunctionName IRMod | OpCaret -> operatorFunctionName IRCaret
        | OpEq -> operatorFunctionName IREq | OpNeq -> operatorFunctionName IRNeq
        | OpLt -> operatorFunctionName IRLt | OpLe -> operatorFunctionName IRLe
        | OpGt -> operatorFunctionName IRGt | OpGe -> operatorFunctionName IRGe
        | OpAnd -> operatorFunctionName IRAnd | OpOr -> operatorFunctionName IROr
        | OpMath2 name -> name
        | _ -> "lambda"
    | TExprUnaryOp (op, _) ->
        match op with
        | OpNeg -> "(-)" | OpNot -> "(!)" | OpConj -> "conj"
        | OpReal -> "real" | OpImag -> "imag" | OpArg -> "arg"
        | OpMath name | OpCast name -> name
    | _ -> "lambda"

/// Flat-vs-structural projection mode for a destructuring binding, plus the
/// slot each sub-binding reads.
///
/// The WIDTH that decides the mode is the pattern's slot count, which is only
/// the same as `SubBindings.Length` when every pattern position binds a name.
/// A wildcard binds nothing yet still covers a component, so the checker
/// states both numbers explicitly (DSTupleAt); re-deriving either one from the
/// sub-binding list is what made `let (_, g) = f(x)` read element 0.
/// Non-tuple shapes (struct fields, cons leaves, no destructuring) keep the
/// identity mapping and the historical count.
let destructureLayout (binding: TypedBinding) : bool * (int -> int) =
    let slotOfSub, slots =
        match binding.Destructure with
        | DSTupleAt (components, slots) ->
            let arr = List.toArray components
            (fun i -> if i < arr.Length then arr.[i] else i), slots
        | DSPositional | DSConsRest -> id, binding.SubBindings.Length
    let isFlat =
        match binding.Type with
        | IRTTuple _ ->
            let structCount = match binding.Type with IRTTuple ts -> ts.Length | _ -> 0
            let flatCount = IR.flattenTupleLeaves binding.Type |> List.length
            slots = flatCount && slots <> structCount
        | _ -> false
    (isFlat, slotOfSub)

/// Value expression for destructured sub-binding #i of `binding`, reading
/// tuple slot `slot` (they differ only when a pattern position binds nothing).
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
let subBindingValue (binding: TypedBinding) (isStruct: bool) (isFlat: bool) (i: int) (slot: int) (name: string) : IRExpr =
    let baseVar = IRVar (binding.VarId, binding.Type)
    // Cons/pack shapes never remap (slot = i there), so the rest-leaf test and
    // the pack reads below stay on the sub-binding index they were written for.
    let isConsBinding = match binding.Destructure with DSConsRest -> true | DSPositional | DSTupleAt _ -> false
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
        | _ -> IRTupleProj (baseVar, slot, isFlat)

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

/// `computeWrap`'s INSERT direction (docs/plan-kernel-body-materialization.md
/// sections 2 and 5, stage S2). The TExprCompute arm below owns the DROP
/// direction -- "this node materializes on its own, so the wrapper is a no-op".
/// This owns the mirror rule: inside a LAMBDA or FUNCTION body, a `let` whose
/// RHS is a bare (unforced) `IRApplyCombinator`/`IRComposeApply` must be FORCED,
/// because there is no forcing site downstream of it.
///
/// Why the body scope is load-bearing. At MODULE level the deferred protocol is
/// real: `genComputeBinding` peels `IRCompute` recursively and the consuming
/// `|> compute` materializes the producer under its own name. Inside a body
/// there is no such consumer -- `genFuncBodyScoped`'s let dispatch used to
/// register the name and emit NOTHING (the `'__v27' was not declared` bug), so
/// every downstream read named an identifier that was never declared and the
/// kernel that lived only on the dropped node was emitted and never called.
///
/// `compute` is idempotent at inference (Theme B), so a user-written `|> compute`
/// has already produced `IRCompute (IRApplyCombinator ...)` and is NOT matched
/// here -- `loops/114` (compute-elementwise-in-function-body) is unaffected.
/// The walk follows only the STATEMENT SPINE (let chains and the control-flow
/// forms a body's statements can nest in); it never descends into a combinator's
/// own operand slots, where a deferred node is the working protocol, and never
/// into a nested lambda (which is already an IRVar to its own callable by the
/// time this runs, and got this same treatment when IT was lowered).
/// S2's EXEMPTION: bindings a reduction join consumes as a deferred operand,
/// and consumes NOWHERE else (docs/plan-reduction-joins.md, "sharing by
/// naming"). `let ct = cos <@> ph` inside a kernel body, read only by the legs
/// of `object_for(<&!>) <@> (prodsum(s, ct), prodsum(ct, ct))`, must stay
/// deferred: the join inlines it as ONE per-iteration `const` in the joint
/// nest, and forcing it first would build a whole array per outer cell and
/// read it back -- the exact cost the join exists to remove.
///
/// The "nowhere else" half is what makes the exemption safe rather than a
/// guess. An id whose occurrences are not ALL join operands still has a
/// consumer that wants an array, and S2's rule (there is no forcing site
/// downstream of a body-local let) still holds for it, so it is left alone.
/// The set over SEVERAL expressions at once -- the reference accounting is
/// global, so a function body already split into its statement list must be
/// counted as one unit, not per statement (CodeGen.genFuncBodyScoped).
let joinDeferrableIdsMany (exprs: IRExpr list) : Set<IRId> =
    let joinRefs = System.Collections.Generic.Dictionary<IRId, int>()
    let allRefs = System.Collections.Generic.Dictionary<IRId, int>()
    let bump (d: System.Collections.Generic.Dictionary<IRId, int>) id =
        d.[id] <- (match d.TryGetValue id with | true, n -> n + 1 | _ -> 0) + 1
    let walk (e: IRExpr) =
        e |> visitTopDown (fun e ->
            (match e with
             | IRVar (id, _) -> bump allRefs id
             | IRReduceCompute (comp, IRTuple ks, IRTuple ss) when ks.Length = ss.Length && ks.Length >= 2 ->
                let rec leavesOf x =
                    match x with
                    | IRFusion (l, r) -> leavesOf l @ leavesOf r
                    | other -> [other]
                // The OPERAND slots -- `Arrays`, plus the `Loop` provenance that
                // repeats them (`method_for(zip(s, ct))` names `ct` twice, and an
                // accounting that saw only one of the two could never balance).
                // Deliberately NOT the kernel bodies: an operand a leg's kernel
                // reads as a whole ARRAY is a capture the join cannot inline.
                let bumpVars x =
                    x |> visitTopDown (fun n ->
                        (match n with IRVar (id, _) -> bump joinRefs id | _ -> ())
                        true)
                for leaf in leavesOf comp do
                    match leaf with
                    | IRApplyCombinator info ->
                        bumpVars info.Loop
                        for a in info.Arrays do
                            match a with
                            | IRVar (id, _) -> bump joinRefs id
                            | _ -> ()
                    // A leg whose LEAF is the named map itself (`reduce(<name>,
                    // op)` beside `prodsum(<name>, v)`): the typechecker keeps
                    // the name so the emitter binds the share once and this leg
                    // reads it. That read is a join reference like the slot's.
                    | IRVar (id, _) -> bump joinRefs id
                    | _ -> ()
             | _ -> ())
            true)
    exprs |> List.iter walk
    joinRefs
    |> Seq.filter (fun kv ->
        match allRefs.TryGetValue kv.Key with
        | true, total -> total = kv.Value
        | _ -> false)
    |> Seq.map _.Key
    |> Set.ofSeq

let joinDeferrableIds (expr: IRExpr) : Set<IRId> = joinDeferrableIdsMany [expr]

/// A value-keyed WRAPPER over arrays -- `A <|> B` (choice) or `A <|:> B`
/// (fallback) -- as a let's value (gap G50). It is forced where a map is: in a
/// function or lambda body always (S2 below; the C++ body emitter already
/// forced every deferring form at its let, forceDeferringForm), and at module
/// level / in a top-level block when its evaluation writes
/// (forceEffectfulModuleLets). A scalar choice is an ordinary value and is
/// never wrapped.
let isArrayWrapperValue (v: IRExpr) : bool =
    match v with
    | IRChoice _ | IRFallback _ ->
        (match stripUnits (typeOf v) with
         | ArrayElem _ -> true
         | _ -> false)
    | _ -> false

let rec forceBareCombinatorLetsExcept (skip: Set<IRId>) (expr: IRExpr) : IRExpr =
    match expr with
    | IRLet (id, value, body) ->
        let value' =
            match value with
            | IRApplyCombinator _ | IRComposeApply _ when Set.contains id skip -> value
            | IRApplyCombinator _ | IRComposeApply _ -> IRCompute value
            | v when isArrayWrapperValue v && not (Set.contains id skip) -> IRCompute v
            | _ -> forceBareCombinatorLetsExcept skip value
        IRLet (id, value', forceBareCombinatorLetsExcept skip body)
    | IRIf (c, t, e) -> IRIf (c, forceBareCombinatorLetsExcept skip t, forceBareCombinatorLetsExcept skip e)
    | IRForRange (vid, lo, hi, body) -> IRForRange (vid, lo, hi, forceBareCombinatorLetsExcept skip body)
    | IRMatch (scrut, cases) ->
        IRMatch (scrut, cases |> List.map (fun c -> { c with Body = forceBareCombinatorLetsExcept skip c.Body }))
    | _ -> expr

/// S2's second exemption: a body-local map over a CLOSED-FORM DOMAIN
/// (`range<R>` over a static struct with linear conjuncts) that nothing reads.
/// The typechecker splices such a map into the fold that consumed it
/// (tryInferReduceCompute's `overClosedFormDomain`), and the fold streams the
/// affine nest itself, so the let is left with no reader; forcing it would
/// build the sparse key view and a value buffer nobody looks at. Narrow on
/// purpose: any other dead combinator let keeps today's forcing.
let isClosedFormDomainApply (info: ApplyInfo) : bool =
    info.ArrayTypes |> List.exists (fun at ->
        at.IndexTypes |> List.exists (fun ix ->
            ix.IxKind = IxKSparse
            && (match ix.Extent with IRSparseKeys (SkDomain _) -> true | _ -> false)))

/// Over a body already split into its let list (CodeGen.genFuncBodyScoped):
/// the domain-map lets no value and no return reads.
let unreadDomainMapIdsOfLets (lets: (IRId * IRExpr) list) (retExpr: IRExpr) : Set<IRId> =
    let refs = System.Collections.Generic.HashSet<IRId>()
    for e in (lets |> List.map snd) @ [ retExpr ] do
        iterIRExpr (fun n -> match n with IRVar (id, _) -> refs.Add id |> ignore | _ -> ()) e
    lets
    |> List.choose (fun (id, v) ->
        match v with
        | IRApplyCombinator info when isClosedFormDomainApply info && not (refs.Contains id) -> Some id
        | _ -> None)
    |> Set.ofList

let unreadDomainMapIds (expr: IRExpr) : Set<IRId> =
    let refs = System.Collections.Generic.HashSet<IRId>()
    let candidates = System.Collections.Generic.HashSet<IRId>()
    iterIRExpr (fun e ->
        match e with
        | IRVar (id, _) -> refs.Add id |> ignore
        | IRLet (id, IRApplyCombinator info, _) when isClosedFormDomainApply info -> candidates.Add id |> ignore
        | _ -> ()) expr
    candidates |> Seq.filter (fun id -> not (refs.Contains id)) |> Set.ofSeq

let forceBareCombinatorLets (expr: IRExpr) : IRExpr =
    forceBareCombinatorLetsExcept (Set.union (joinDeferrableIds expr) (unreadDomainMapIds expr)) expr

/// The RETURN half of the same rule (docs/plan-kernel-body-materialization.md
/// manifestation M-D, stage S4): **the callee forces**. A callable whose return
/// expression is a bare `IRApplyCombinator`/`IRComposeApply` gets an `IRCompute`,
/// so `function f(xs) = { xs <@> lambda(x) -> x * 2.0 }` materializes inside `f`
/// instead of returning
/// `BLADE_CODEGEN_ERROR_UNEVALUATED_COMPUTATION_USED_AS_VALUE`.
///
/// WHY "THE CALLEE FORCES" AND NOT "THE CALLER'S COMPUTE REACHES INSIDE".
/// Three measured facts (plan section 1, M-D) settle it: the emitted signature
/// is ALREADY `Array<double,1> f(...)`, so the ABI promises a materialized
/// array; laziness demonstrably never crosses a named-function boundary today
/// (`reduce(mk(a), (+))` and `mk(a) <&> mk2(a)` die with the same sentinel, so
/// there is no lazy return to preserve); and `compute` is idempotent at
/// inference, so a caller-side `f(a) |> compute` stays a legal no-op. Sinking
/// the caller's `compute` into the callee would instead make the return type
/// vary by call site -- a monomorphization axis with no payoff.
///
/// SPINE, not tree. A return leaf hides behind a let chain (`{ let s = ...;
/// xs <@> k }`), an `if`, or a `match`, so all three are followed to their
/// tails. `IRForRange` is deliberately NOT followed: a for-body's tail is a
/// statement, not a return value, and forcing there would materialize a fresh
/// array per iteration and discard it. Let RHSs are `forceBareCombinatorLets`'
/// business (S2) -- this pass only ever touches tail position, so running the
/// two in sequence is exactly "force every leaf on the statement spine".
let rec forceReturnCombinator (expr: IRExpr) : IRExpr =
    match expr with
    | IRApplyCombinator _ | IRComposeApply _ -> IRCompute expr
    | IRLet (id, value, body) -> IRLet (id, value, forceReturnCombinator body)
    | IRIf (c, t, e) -> IRIf (c, forceReturnCombinator t, forceReturnCombinator e)
    | IRMatch (scrut, cases) ->
        IRMatch (scrut, cases |> List.map (fun c -> { c with Body = forceReturnCombinator c.Body }))
    | _ -> expr

/// The two body passes a callable's lowered body gets, in the order their
/// scopes compose: S2 forces every bare combinator bound by a `let` anywhere on
/// the statement spine, then S4 forces the bare combinator in each RETURN leaf.
let forceCallableBody (body: IRExpr) : IRExpr =
    forceReturnCombinator (forceBareCombinatorLets body)

/// A LOWERING REFUSAL: the program typechecked, but a construct sits in a
/// position lowering has no rule for (`compound(...)` or `rand.<fam>(...)`
/// inside a function body, an array-typed `zero` as a return value, ...).
/// Coded BL6002 and spanned at the offending expression, so `blade check`
/// (which lowers after typechecking) and `blade emit` report it where it is
/// written -- it used to be a spanless message from a bare `failwith`.
let private refuseLowering (span: Span) (message: string) : 'a =
    raise (Blade.Diagnostics.BladeDiagnosticException
            (Blade.Diagnostics.mkError "BL6002" Blade.Diagnostics.PhIRValidate span message))

/// An internal lowering invariant broke (a shape typecheck should already have
/// refused reached lowering): BL9003, spanned where the span is known.
let private loweringIce (span: Span) (message: string) : 'a =
    raise (Blade.Diagnostics.BladeDiagnosticException
            ({ Blade.Diagnostics.Codes.ice message with Code = "BL9003"; Span = span }))

/// A `lo..hi` operand with a runtime bound: typecheck recorded its axis with
/// the anonymous unknown extent, and Lowering built the range itself with
/// `hi - lo`. The loop levels take their extent from the RECORDED operand
/// types, where the unknown names nothing to evaluate -- so carry the range's
/// own extent onto every recorded index type with that axis's id (the
/// operand's type and the shared co-iteration records alike).
let private alignRangeOperandExtents (arrays: IRExpr list) (arrayTypes: IRArrayType list)
                                     (shared: IRIndexType list) : IRArrayType list * IRIndexType list =
    let byId =
        if arrays.Length <> arrayTypes.Length then Map.empty
        else
            List.zip arrays arrayTypes
            |> List.choose (fun (a, at) ->
                match a, at.IndexTypes with
                | IRRange ([rix], _), [tix] when (match tix.Extent with IRParam ("?", 0, _) -> true | _ -> false) ->
                    Some (tix.Id, rix.Extent)
                | _ -> None)
            |> Map.ofList
    if Map.isEmpty byId then (arrayTypes, shared)
    else
        let fix (ix: IRIndexType) =
            match Map.tryFind ix.Id byId with
            | Some e -> { ix with Extent = e }
            | None -> ix
        (arrayTypes |> List.map (fun at -> { at with IndexTypes = at.IndexTypes |> List.map fix }),
         shared |> List.map fix)

/// A direct cell of a plain static virtual range is its ordinal plus the
/// offset, without a materialized iota. Zonk has already proved or guarded
/// the subscript, so retain that expression (including its guard) exactly
/// once. Limit this fold to static bounds: discarding a runtime range's
/// extent could otherwise discard evaluation of its upper-bound expression.
let private lowerRangeIndex (arr: IRExpr) (indices: IRExpr list) identity : IRExpr =
    match arr, indices with
    | IRRange ([ix], offset), [index]
        when ix.IxKind = IxKPlain && ix.Rank = 1
             && (match ix.Tag with Some t -> not (t.StartsWith haloWinTagPrefix) | None -> true)
             && (tryEvalIntIR ix.Extent).IsSome
             && (offset |> Option.forall (fun e -> (tryEvalIntIR e).IsSome)) ->
        let ordinal = IRUnaryOp (IRCast (ETInt64, SrcLoc.Nowhere), index)
        match offset |> Option.bind tryEvalIntIR with
        | Some n when n <> 0L ->
            IRBinOp (IRElementwise, IRAdd, IRLit (IRLitInt n), ordinal, SrcLoc.Nowhere)
        | _ -> ordinal
    | _ -> IRIndex (arr, indices, identity)

/// THE ZERO-LEVEL APPLY (gap G64; formalism §6, "a zero-level application is
/// a call"): `object_for(f) <@> y` where f takes every operand WHOLE (no
/// S-dimension level on any operand: `SDimsPerArray` all 0) and returns a
/// scalar has no loop to run -- its value is the ONE term f(y1, .., yk). It is
/// lowered to that call, once, before the C++ / LLVM / interpreter lanes
/// split, so every lane evaluates the same call (each operand bound whole,
/// the term evaluated exactly once, its value -- signed zero included -- the
/// result) and no lane carries a zero-level nest form of its own (the C++
/// nest used to leave the whole-operand parameter unbound, BL9002; the
/// interpreter's added the term to a zero accumulator). Fail closed: a
/// resolvable lifted kernel (`IRVar`), at least one operand, one kernel
/// parameter per operand, no co-iteration, no Reynolds, a scalar result;
/// anything else keeps `IRApplyCombinator`.
let isZeroLevelWholeArrayApply (info: TypedApplyInfo) (loweredKernel: IRExpr) : bool =
    (match loweredKernel with IRVar _ -> true | _ -> false)
    && (match info.Loop.Kind with TExprObjectFor _ -> true | _ -> false)
    && not (List.isEmpty info.Arrays)
    && info.SDimsPerArray.Length = info.Arrays.Length
    && info.SDimsPerArray |> List.forall ((=) 0)
    && info.KernelInputRanks.Length = info.Arrays.Length
    // each operand's KNOWN rank is the rank its parameter takes: the kernel
    // consumes it whole (a generic `T^2` operand of a rank-1 row kernel --
    // sql-group-by/054's `rowdots` -- is a row map, not a call)
    && info.ArrayTypes.Length = info.Arrays.Length
    && List.forall2 (fun (at: IRArrayTypeG<IRExpr>) (k: int) -> k > 0 && (at.IndexTypes |> List.sumBy (fun ix -> ix.Rank)) = k)
           info.ArrayTypes info.KernelInputRanks
    && not info.IsCoIteration
    && not info.HasReynolds
    && (match stripUnits info.OutputType with IRTScalar _ -> true | _ -> false)

/// Lower a TypedExpr to IRExpr
let rec lowerTypedExpr (env: TypedLowerEnv) (texpr: TypedExpr) : IRExpr =
    if isUnseededSeed texpr then lowerUnseededSeed env texpr else
    match texpr.Kind with
    | TExprLit lit ->
        // An Int32-TYPED integer literal (round 6: the default of a literal
        // that fits Int32, or one adapted to Int32) carries its width into the
        // IR as a cast -- IRLitInt has no width and types Int64 -- so
        // exprTypeIfKnown, HM instance selection and every lane agree with the
        // checker (`inc(5)` is an Int32 instance). A bare operand of an
        // elementwise binop beside an Int32 partner is unwrapped again below
        // (IR's own "a literal adapts" rule types it there).
        // An arity-polymorphic scalar base arm can carry its declared
        // array result type. It still produces one scalar, at that array's
        // element width, rather than falling back to a wide literal.
        let literalTy = match texpr.Type with ArrayElem a -> a.ElemType | ty -> ty
        match lit, literalTy with
        | LitInt n, AnyPrimElem ETInt32 -> IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt n))
        // R19 (lane a2): a float literal whose SPELLING its double cannot hold
        // (`1e400`, a nonzero `1e-400`; Ast.DegenerateFloatLiterals) is typed
        // Float64 and keeps that width: an explicit Float64 cast carrying the
        // literal's span, so the per-instance narrowing (IRMono's
        // adaptFloatLiteralsModule) never narrows it -- it refuses it there.
        | LitFloat f, AnyPrimElem ETFloat64 when DegenerateFloatLiterals.isDegenerate texpr.Span ->
            IRUnaryOp (IRCast (ETFloat64, SrcLoc.Of texpr.Span), IRLit (IRLitFloat f))
        | _ -> IRLit (lowerLiteralValued lit literalTy)

    | TExprWildcard ->
        // A wildcard `_` is a hole, not a value. It is only meaningful where a
        // context consumes it (a compound-index coordinate marks a free axis).
        // Reaching lowering means it was used where no context interpreted it.
        refuseLowering texpr.Span "wildcard `_` is not valid here: it can only appear as a compound-index coordinate (e.g. B((a, _, c))) or in a pattern"
    
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
        // A literal operand beside a NON-literal Int32 partner stays a bare
        // IRLitInt (see the TExprLit arm): `i32 + 1` emits as it always did.
        let rec isLitInt (t: TypedExpr) =
            match t.Kind with
            | TExprLit (LitInt _) -> true
            | TExprUnaryOp (OpNeg, x) -> isLitInt x
            | _ -> false
        let i32Partner (t: TypedExpr) =
            not (isLitInt t)
            && (match stripUnits t.Type with
                | IRTScalar ETInt32 -> true
                | ArrayElem a -> (match stripUnits a.ElemType with IRTScalar ETInt32 -> true | _ -> false)
                | _ -> false)
        let rec unwrapI32 (e: IRExpr) =
            match e with
            | IRUnaryOp (IRCast (ETInt32, _), (IRLit (IRLitInt _) as lit)) -> lit
            | IRUnaryOp (IRNeg, inner) -> IRUnaryOp (IRNeg, unwrapI32 inner)
            | _ -> e
        let l = lowerTypedExpr env left
        let r = lowerTypedExpr env right
        let l = if mode = Elementwise && isLitInt left && i32Partner right then unwrapI32 l else l
        let r = if mode = Elementwise && isLitInt right && i32Partner left then unwrapI32 r else r
        lowerTypedBinOp env mode op l r left right texpr.Type (SrcLoc.Of texpr.Span)
    
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
        | OpCast name ->
            // The name is a numericCastTargets key by construction (only
            // TypeCheck's cast arm builds OpCast); resolve it to the target
            // element type here, where Types.fs is in scope.
            match Blade.Types.castTargetOf name with
            | Some et -> IRUnaryOp (IRCast (et, SrcLoc.Of texpr.Span), e)
            | None -> loweringIce texpr.Span $"OpCast head '{name}' is not a numeric cast target"
    
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
                | TNegIntLit n -> Some (-n)
                | _ -> None
            match staticOff with
            | Some o -> IRHaloUnhash (f, o)
            // Unreachable from a checked program: TypeCheck's window-read
            // walk (haloExtentClash's checkSite) refuses a non-literal
            // offset over a compound inner with a spanned diagnostic. Kept
            // as the invariant it now is.
            | None -> loweringIce offArg.Span "halo window read over a masked domain reached lowering with a non-literal offset (TypeCheck.checkSite should have refused it)"
        else
            IRBinOp (IRElementwise, IRAdd, f, lowerTypedExpr env offArg, SrcLoc.Nowhere)

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
        | ArrayElem _ -> lowerRangeIndex f as' None
        | _ ->
            // The call's span, for the opt-in kept inline's BL7005 (G69).
            (match f with IRVar (fid, _) -> Optimize.CallSpanTable.record fid as' texpr.Span | _ -> ())
            IRAppAt (f, as', texpr.Type, SrcLoc.Of texpr.Span)
    
    | TExprIndex (array, indices, identity) ->
        let arr = lowerTypedExpr env array
        let idxs = indices |> List.map (lowerTypedExpr env)
        lowerRangeIndex arr idxs identity
    
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
        let v = lowerBoundValue env name value
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

    | TExprFma (a, b, c) ->
        IRFma (lowerTypedExpr env a, lowerTypedExpr env b, lowerTypedExpr env c)
    
    | TExprArrayLit (elems, arrTy) ->
        let es = elems |> List.map (lowerTypedExpr env)
        IRArrayLit (es, arrTy)
    
    | TExprMethodFor info ->
        let arrays = info.Arrays |> List.map (lowerTypedExpr env)
        let (arrayTypes, shared) = alignRangeOperandExtents arrays info.ArrayTypes info.SharedIndexTypes
        IRMethodFor {
            Arrays = arrays
            Identities = info.Identities
            ArrayTypes = arrayTypes
            SDimsPerArray = info.SDimsPerArray
            TotalSDims = info.TotalSDims
            SharedIndexTypes = shared
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
        let arrays = info.Arrays |> List.map (lowerTypedExpr env)
        if isZeroLevelWholeArrayApply info loweredKernel then
            // G64: a ZERO-LEVEL scalar apply IS the call (see
            // isZeroLevelWholeArrayApply). Lowered here, before the lanes
            // split, so the C++ lane, the LLVM lane and the interpreter all
            // evaluate the same `f(operands)` -- one term, the operands whole.
            IRAppAt (loweredKernel, arrays, info.OutputType, SrcLoc.Of texpr.Span)
        else
        let (arrayTypes, shared) = alignRangeOperandExtents arrays info.ArrayTypes info.SharedIndexTypes
        IRApplyCombinator {
            Loop = loweredLoop
            Kernel = loweredKernel
            Arrays = arrays
            Identities = info.Identities
            ArrayTypes = arrayTypes
            SharedIndexTypes = shared
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
        // Static bounds arrive as literals (inferExpr's ExprDotDot arm folds
        // them), so the extent is a literal too.
        let extentExpr =
            match loIR, hiIR with
            | IRLit (IRLitInt a), IRLit (IRLitInt b) -> IRLit (IRLitInt (b - a))
            | _ -> IRBinOp (IRElementwise, IRSub, hiIR, loIR, SrcLoc.Nowhere)
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
    
    
    | TExprZip exprs ->
        IRZip (exprs |> List.map (lowerTypedExpr env))
    
    | TExprStack exprs ->
        IRStack (exprs |> List.map (lowerTypedExpr env))

    | TExprJoin (exprs, dim) ->
        IRJoin ((exprs |> List.map (lowerTypedExpr env)), dim)
    
    | TExprPure e ->
        IRPure (lowerTypedExpr env e)
    
    | TExprCompute e ->
        // ALREADY-EAGER FOLD: `|> compute` over a form that materializes on its
        // own is the identity, and the IRCompute wrapper actively HIDES it.
        // The bracketed outer product `A [op] B` lowers (lowerTypedBinOp's
        // both-operands-are-arrays branch) to a completed application
        // `IRApp(IRObjectFor kernel, [IRTuple [A; B]])`, which genBinding's
        // IRApp(IRObjectFor) arm and genFuncBody's hoistLoopApps both expand
        // into a real loop nest. Wrapped in IRCompute it instead reaches
        // genComputeBinding, which dispatches only the deferred combinator
        // shapes and otherwise falls through to genScalarBinding -- rendering
        // the loop object as the LOOP_OBJECT_USED_AS_VALUE sentinel. Drop the
        // no-op wrapper so the existing expansion paths see the application.
        // Narrow on purpose: only the BARE application folds; IRLet-wrapped
        // forms (the broadcast path's hoisted scalar) already work as-is.
        //
        // CONSTANT-FILL FOLD: `replicate(N, pure(lit)) |> compute` with a
        // concrete count and a literal body is exactly an N-element array
        // literal -- lower it as IRArrayLit so it rides the array-literal
        // machinery everywhere (the general IRSequence realization is
        // main-body-only). Non-literal counts and non-constant bodies keep
        // the general combinator path.
        let computeWrap (x: IRExpr) =
            match x with
            | IRApp (IRObjectFor _, _, _) -> x
            // A literal array is already materialized. Numeric array casts
            // keep a Compute boundary during typing so literal adaptation
            // cannot change their explicit width; the lowered array now
            // carries that fixed element type itself. Expose it to the
            // ordinary literal-argument hoister before expression emission.
            | IRArrayLit _ -> x
            | _ -> IRCompute x
        (match e.Kind with
         | TExprReplicate (cnt, body) ->
             (match cnt.Kind, body.Kind, texpr.Type with
              | TExprLit (LitInt n), TExprPure inner, ArrayElem arrTy
                    when n >= 0L && n <= 1_000_000L
                         && (match inner.Kind with TExprLit _ -> true | _ -> false) ->
                  let copies = List.replicate (int n) (lowerTypedExpr env inner)
                  IRArrayLit (copies, arrTy)
              | _ -> computeWrap (lowerTypedExpr env e))
         | TExprApply _ ->
             // A zero-level apply lowers to its scalar call (G64,
             // isZeroLevelWholeArrayApply): `|> compute` of that value is
             // the value, so no wrapper.
             (match lowerTypedExpr env e with
              | IRApp (IRVar _, _, _) as call -> call
              | other -> computeWrap other)
         | _ -> computeWrap (lowerTypedExpr env e))
    
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
        refuseLowering texpr.Span "fill_random(mod) is only valid as an annotated top-level let-binding value (let A: Array<..> = fill_random(mod))"

    // ONE ADDRESSED SAMPLE (`__rand_sample_at`, the cell of an `_at` draw's
    // inline map -- D13): a pure function of (key, stream, sample index,
    // parameters), so it is an ordinary scalar expression.
    | TExprRandGen (kind, key, pars, weights, Some (stream, index), [], None) ->
        IRRandAt (kind,
                  [ lowerTypedExpr env key; lowerTypedExpr env stream; lowerTypedExpr env index ]
                  @ (pars |> List.map (lowerTypedExpr env)),
                  weights |> Option.map (fun (w, n) -> (lowerTypedExpr env w, n)))
    // An ADDRESSED draw anywhere but a top-level `let` (which TDeclLet
    // intercepts into the fill): its samples as the map TypeCheck built.
    | TExprRandGen (_, _, _, _, Some _, _, Some inl) -> lowerTypedExpr env inl
    | TExprRandGen _ ->
        // A SEQUENCED draw is materialized only as a top-level let-binding
        // value, where TDeclLet intercepts it (records the kind/key/params in
        // RandomInits, allocates the self-typed array): its samples depend on
        // the order they are drawn in. Reaching here means it was used inline
        // / nested.
        refuseLowering texpr.Span "a sequenced rand.<fam>(...) draw is only valid as a top-level let-binding value (let A = rand.uniform(key, n)); inside a function, use the addressed family (rand.uniform_at(key, stream, offset, n) -- each sample a pure function of its address), or draw at top level and pass the array in"

    | TExprCompound _ ->
        // Only meaningful as a top-level let-binding value, where TDeclLet
        // intercepts it (records the lowered dense + mask in CompoundInits,
        // leaves a unit placeholder). Reaching here means it was used inline
        // or nested, which the compound-construction codegen path does not handle.
        refuseLowering texpr.Span "compound(dense, mask) is only valid as a top-level let-binding value (let B = compound(dense, mask)); inside a function, build the compound at top level and pass it in"

    | TExprSparse _ ->
        // Same top-level-let-only discipline as compound(dense, mask): the
        // TDeclLet loop intercepts and records the values expr in SparseInits.
        refuseLowering texpr.Span "sparse(values, keys) is only valid as a top-level let-binding value (let S = sparse(values, keys))"
    
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

    | TExprDisplayEmit (head, quoted, data, metaTail, idOpt) ->
        IRDisplayEmit (head, quoted, lowerTypedExpr env data, metaTail,
                       idOpt |> Option.map (lowerTypedExpr env))

    | TExprDisplayJson (rank, data) ->
        IRDisplayJson (rank, lowerTypedExpr env data)

    | TExprDisplayNum data ->
        IRDisplayNum (lowerTypedExpr env data)

    | TExprDisplayStr data ->
        IRDisplayStr (lowerTypedExpr env data)
    
    | TExprGroupBy (values, grouping) ->
        IRGroupBy (lowerTypedExpr env values, lowerTypedExpr env grouping)
    
    | TExprGroupKeys keys ->
        IRGroupKeys (keys |> List.map (lowerTypedExpr env))

    | TExprGroupBucket grouping ->
        IRGroupBucket (lowerTypedExpr env grouping)

    | TExprSegments (_, offsets, labels) ->
        IRSegments (offsets, labels)

    | TExprSegmentsGrid (_, bounds) ->
        IRSegmentsGrid bounds

    | TExprUngroupGrid (grouped, sources, bounds) ->
        IRUngroupGrid (lowerTypedExpr env grouped, sources, bounds)

    | TExprUngroup (grouped, source) ->
        IRUngroup (lowerTypedExpr env grouped, source)

    | TExprUngroupRows (rows, offsets, source) ->
        IRUngroupRows (rows |> List.map (lowerTypedExpr env), offsets, source)
    
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
         // The KEPT SPINE NAME (gap G235 (a), TypedAst.markKeptSpine): the
         // checker kept the spine variable (the marked wrapper's child) because
         // a capture of the fold kernel or a seed cell read forces it under its
         // own name; the fused terminal folds that snapshot.
         | TExprCompute ({ Kind = TExprVar _ } as spine), Some seed when array.Span = keptSpineSpan ->
            IRReduceCompute (lowerTypedExpr env spine, lowerTypedExpr env kernel,
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
    | TExprGramApply (left, right, vec) ->
        IRGramApply (lowerTypedExpr env left, lowerTypedExpr env right, lowerTypedExpr env vec)
    | TExprMatmul (left, right) ->
        IRMatmul (lowerTypedExpr env left, lowerTypedExpr env right)
    | TExprEigh operand ->
        IREigh (lowerTypedExpr env operand)
    | TExprLu matrix ->
        IRLu (lowerTypedExpr env matrix)
    | TExprLuSolve (lu, piv, rhs, t) ->
        IRLuSolve (lowerTypedExpr env lu, lowerTypedExpr env piv, lowerTypedExpr env rhs, t)
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
        // extents(gk): the grouping's per-group sizes. Not an IRExtent at all --
        // the answer is an ARRAY (one length per group), read straight off the
        // CSR offsets with no values gathered.
        | IRTGroupKeys _ -> IRGroupSizes arr'
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
        // (through a unit annotation or index tag, which erase; a complex
        // zero is built at its own width -- the untyped `0` it used to be
        // has no `complex + int` overload in C++ and no value in the
        // interpreter).
        match zeroLiteralOf texpr.Type with
        | Some lit -> lit
        | None ->
        match texpr.Type with
        // A GENERIC zero (`a + zero` in `addz(a: T^0)`: inferBinOp bound
        // the zero to T) has no literal yet -- a `0.0` here was every
        // instance's zero, `int64 + double` at an Int64 one (g++ -Werror,
        // BL9002). Keep the type on the node: HM monomorphization
        // substitutes it per instance, and resolveTypedZerosModule turns it
        // into that instance's literal (a variable still open there takes
        // the old Float64 default).
        | IRTInfer _ | IRTUnitAnnotated (IRTInfer _, _) -> IRZero texpr.Type
        | ArrayElem _ ->
            // An array-typed zero that reached lowering sits in a position
            // the binding-site materialization (inferLetBindingValue's zero
            // arm) does not cover -- emitting IRZero here would render as a
            // scalar `0` under an array type (a null pointer). Fail loudly.
            refuseLowering texpr.Span "zero at an array type is only materialized at an annotated let binding (`let A: Array<...> = zero`). In other positions (a function's return expression, a call argument), bind it first: `let z: Array<...> = zero` and use `z`."
        | other -> IRZero other  // a struct / tuple zero: IRMono.resolveTypedZerosModule gives it its value (IR.zeroValueOf)
    
    | TExprReynolds (kernel, isAntisym) ->
        IRReynolds (lowerTypedExpr env kernel, isAntisym)
    
    | TExprArity paramName ->
        IRArity (None, paramName)
    
    | TExprRank e ->
        // Resolve rank statically from the typed expression's type -- but
        // ONLY when the type is actually resolved. Inside a generic body an
        // abstract parameter's type is still an inference var here, and the
        // old blanket `| _ -> 0` fallback baked `IRLit 0` into the body that
        // every HM specialization then shared: `match rank(x)` answered the
        // rank-0 arm at every rank, silently. An unresolved operand now
        // lowers to the SYMBOLIC IRRank (every lane already handles it), and
        // the constant-match fold resolves it per specialization, once the
        // clone's types are concrete (IRMono.foldConstIntMatch).
        let rec rankOfType (t: IRType) : int option =
            match t with
            | ArrayElem at -> Some (at.IndexTypes |> List.sumBy _.Rank)
            | IRTInfer _ -> None
            | IRTUnitAnnotated (inner, _) -> rankOfType inner
            | _ -> Some 0
        (match rankOfType e.Type with
         | Some rank -> IRLit (IRLitInt (int64 rank))
         | None -> IRRank (lowerTypedExpr env e))
    
    | TExprStruct (typeName, fields) ->
        IRStructLit (typeName, fields |> List.map (fun (fname, e) -> fname, lowerTypedExpr env e))
    
    | TExprBlock (stmts, finalExpr) ->
        lowerTypedBlock env stmts finalExpr
    
    | TExprAssign (lhs, rhs) ->
        IRAssign (lowerTypedExpr env lhs, lowerTypedExpr env rhs)

    | TExprConstraintCheck (cond, code, message) ->
        // Carry the constraint's source span into IR so the runtime panic
        // can report file:line. texpr is the whole node in hand.
        IRConstraintCheck (lowerTypedExpr env cond, code, message, texpr.Span)

    | TExprBreakIf cond ->
        IRBreakIf (lowerTypedExpr env cond)
    
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

/// THE SEED OF AN UNSEEDED FOLD (gap G74; TypedAst.unseededSeedSpan marks the
/// checker's synthesized `(+)` seed). An unseeded fold's value is the left
/// fold of its cells. The mark becomes the IR node `IRUnseededSeed (base,
/// type, inRepro)` -- the BASE seed lowered exactly as before, and whether the
/// fold sits in a `where repro` body -- which carries the provenance through
/// monomorphization to `resolveUnseededSeedsModule` (before the optimizer):
/// once the producer's effects and the fold's effective licence are known, it
/// gives every OBLIGATORILY SERIAL route the EXACT `(+)` identity
/// (IR.addIdentityLiteralOf: `-0.0`, `-0.0f`, `(-0.0, -0.0)`, `0`), so every
/// form that keeps the seed (the walk's row callable, the ordered scatter,
/// the row-owned pull, the row action, the strict cell chains, the
/// interpreter) runs the head-peeled chain bit for bit (formalism 2.4's
/// identity insertion), and every licensed route the base seed byte for byte
/// (the licence's signed zero, G40).
and lowerUnseededSeed env (texpr: TypedExpr) : IRExpr =
    let baseSeed = lowerTypedExpr env { texpr with Span = Blade.Ast.noSpan }
    IRUnseededSeed (baseSeed, texpr.Type, env.InRepro)

/// Lower the value of the binding `name` (a top-level or block `let`, an
/// expression `let`): a lambda bound directly is no longer anonymous -- it
/// prints as `name` -- and the anonymous functions anywhere else in the value
/// print as `lambda in name`.
and lowerBoundValue env (name: string) (value: TypedExpr) : IRExpr =
    match value.Kind with
    | TExprLambda info when not (isSynthesizedName name) -> lowerTypedLambdaAs env (Some name) info
    | _ -> lowerTypedExpr (withLambdaScope name env) value

/// Lower a typed lambda
and lowerTypedLambda env (info: TypedLambdaInfo) : IRExpr =
    lowerTypedLambdaAs env None info

/// Lower a typed lambda that `boundTo` names, when a `let` binds it directly.
/// What it prints as (IRCallable.SourceName -- both lanes' panic frames, plan
/// subjects, diagnostics): that binding's name; a self-bound lambda's own
/// name; otherwise `lambda in <scope>` (the nearest enclosing binding or
/// function), or `lambda` where there is none -- `(-) in <scope>` for the
/// checker's operator-lifting lambdas (unboundLambdaWhat). Never its emitted
/// `__lambda_<id>`. The lambda's own name is the scope of the anonymous
/// functions in its body.
and lowerTypedLambdaAs env (boundTo: string option) (info: TypedLambdaInfo) : IRExpr =
    let shownName =
        match boundTo, info.SelfBinding with
        | Some n, _ -> n
        | None, Some (selfName, _) when not (isSynthesizedName selfName) -> selfName
        | None, _ -> anonymousCallableName env.LambdaScope (unboundLambdaWhat info)
    let mutable paramEnv = { env with LambdaScope = Some shownName }
    let paramInfos = info.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)

    let captures = info.Captures |> List.map (fun c ->
        { Id = c.VarId; Name = c.Name; Type = c.Type; IsMutable = c.IsMutable } : CaptureInfo)

    let body' = lowerTypedExpr paramEnv info.Body

    // A body shape that is value-position-illegal as a standalone function
    // return is wrapped in IRCompute -- bare IRApplyCombinator/IRComposeApply,
    // the combinator forms that need a destination to materialize into.
    // genFuncBody's return-position match handles `IRCompute(IRApplyCombinator
    // _)` by synthesizing an internal let binding and running the full
    // combinator codegen; use-site rendering is identical either way, so the
    // wrap doesn't change behavior at existing use sites.
    //
    // S2 forces a `let` INSIDE the body whose RHS is a bare combinator; S4
    // generalizes the wrap above from "the body's TOP node" to "each RETURN
    // leaf" (behind a let chain / if / match), which is the same rule this
    // site always intended. `forceCallableBody` is the pair, and the two
    // callable-construction sites now share it verbatim.
    let bodyWrapped = forceCallableBody body'

    // Build unified IRCallable. info.ReturnType comes from TypeCheck, so the
    // lambda has a concrete return type; we trust that annotation. Lambda-level
    // parallelism flows TypedLambdaInfo.Parallel -> here via the shared
    // extractor (same logic a function's where-clause uses).
    let (lamParallelism, lamIsOmp, lamIsCuda, lamBlock, lamIsMpi) =
        extractParallelism info.Parallel (info.Params |> List.map _.Name)
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
                 // IRLoopStructure.deduceWreathTie the values typecheck judged from.
                 SignParities = info.SignParities
                 SourceName = shownName
                 // a lambda in a `where repro` body inherits the demand (G74)
                 IsRepro = env.InRepro }
    // Emit IRVar(callable.Id, funcType): the callable lives in
    // LiftedCallables -> module.Functions, and the IRVar carries just the
    // function type for type-inference and consumer dispatch. Consumers use
    // `resolveCallable` to walk back when they need params/body/captures.
    // The function type uses the regular params only -- captures are an
    // implementation detail of the lifted function's signature.
    env.LiftedCallables.Add(callable)
    let funcType =
        let paramTypes = callable.Params |> List.map _.Type
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
        | AnyPrimElem ETFloat32 -> IRLitFloat32 (float32 n)
        | AnyPrimElem ETFloat64 -> IRLitFloat (float n)
        | _ -> IRLitInt n
    | LitFloat f ->
        match ty with
        | AnyPrimElem (ETInt32 | ETInt64) -> IRLitInt (int64 f)  // defensive; narrowing normally rejected
        // A literal in a Float32 position carries its WIDTH into the IR
        // (see IRLitFloat32's doc): the value is rounded here, once, so
        // every consumer -- codegen, the interpreter, typeOf -- agrees on
        // the same float32 quantity.
        | AnyPrimElem ETFloat32 -> IRLitFloat32 (float32 f)
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
    | TPatStruct (typeName, fields) ->
        // Field names are KEPT (they used to be dropped here, collapsing the
        // pattern to a positional tuple): the C++ lane needs them to emit
        // `.x` rather than `std::get<0>` on a real struct, and BOTH lanes
        // need them for a pattern whose field order differs from the
        // declaration's.
        IRPatStruct (typeName, fields |> List.map (fun (n, p) -> (n, lowerTypedPattern p)))
    | TPatGuarded (p, _) -> lowerTypedPattern p

/// Lower a typed block into nested IRLet
and lowerTypedBlock env (stmts: TypedStmt list) (finalExpr: TypedExpr option) : IRExpr =
    match stmts, finalExpr with
    | [], Some e -> lowerTypedExpr env e
    | [], None -> IRLit IRLitUnit
    | stmt :: rest, _ ->
        match stmt with
        // PRINT REPLAY (rec-print-replay v1): the ring sweep's row marker
        // `let __replay_row_<x> = ring(slot)` (TypeCheckInfer.recArrayLower,
        // replayRows) becomes the effectful row node naming x.
        | TStmtLet binding when binding.Name.StartsWith Blade.IR.recReplayRowPrefix
                                && binding.SubBindings.IsEmpty && binding.PostChecks.IsEmpty
                                && env.Variables.ContainsKey (binding.Name.Substring Blade.IR.recReplayRowPrefix.Length) ->
            let target = env.Variables.[binding.Name.Substring Blade.IR.recReplayRowPrefix.Length]
            let row = lowerTypedExpr env binding.Value
            let env' = bindTypedVar binding.Name binding.VarId env
            IRLet (binding.VarId, IRReplayRow (target, row), lowerTypedBlock env' rest finalExpr)
        | TStmtLet binding ->
            let value =
                match binding.Type with
                // A let-rec buffer the desugar proved needs no zero fill
                // (`IR.recUninitBufferPrefix`, formalism 7.5): bind the
                // UNFILLED pool instead of lowering the checked zero former.
                // The former was still type-checked against the declared
                // annotation, so the binding's type is the annotation's.
                | ArrayElem at when binding.Name.StartsWith Blade.IR.recUninitBufferPrefix
                                    && at.IndexTypes |> List.forall (fun ix ->
                                           ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1
                                           && (Blade.IRPrint.tryEvalIntIR ix.Extent).IsSome) ->
                    IRUninit binding.Type
                | _ -> lowerBoundValue env binding.Name binding.Value
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
            if binding.IsLetMut then env.LetMutBinders.Add binding.VarId
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
                let (isFlat, slotOf) = destructureLayout binding
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
                        let projExpr = subBindingValue binding isStruct isFlat i (slotOf i) name
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
        // atan2/log_base are order-sensitive, hence not commutative. No surface
        // SECTION syntax reaches them (they are call-shaped, not operators),
        // but map them anyway so the `_ -> IRAdd` fallback below can never
        // silently turn one into an addition.
        | OpMath2 name -> (IRMath2 name, false)
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
    let body = IRBinOp(IRElementwise, irOp, IRVar (aId, paramTy), IRVar (bId, paramTy), SrcLoc.Nowhere)
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
    let callable =
        { mkLambdaCallable env.Builder parms body retType [] isComm commGroups [] false false 256 false
            with SourceName = operatorCallableName env irOp
                 // inside a `where repro` body it is a repro kernel (G74)
                 IsRepro = env.InRepro }
    env.LiftedCallables.Add(callable)
    let funcType =
        let paramTypes = callable.Params |> List.map _.Type
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Lower a partial operator application to a lambda
and lowerTypedPartialApp env (op: BinOp) (argExpr: IRExpr) (isLeft: bool) (funcTy: IRType) : IRExpr =
    // Inlines argExpr into the lifted kernel with NO captures -- only safe
    // when argExpr is a literal or references module-level ids. The
    // array<->scalar broadcast path hoists computed scalars into a let and
    // threads them as captures instead (lowerTypedBinOp). Source operand
    // sections (`(s +)`, `(* s)`) never reach here: the parser desugars them
    // to the lambda they abbreviate (ParserGrammar.mkOperandSection), which
    // captures like every lambda, so nothing produces TExprPartialApp.
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
        // Reachable from lowerTypedBinOp's array<->scalar broadcast path:
        // without this arm a broadcast `atan2(A, c)` that ever reached lowering
        // directly would emit `A + c`. TypeCheck re-synthesizes the array forms
        // as an explicit kernel today, so this is a backstop, not a live path.
        | OpMath2 name -> IRMath2 name
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
        if isLeft then IRBinOp (IRElementwise, irOp, argExpr, IRVar (paramId, paramTy), SrcLoc.Nowhere)
        else IRBinOp (IRElementwise, irOp, IRVar (paramId, paramTy), argExpr, SrcLoc.Nowhere)
    let parms : IRParam list =
        [{ Name = "x"; Type = paramTy; Index = 0; VarId = paramId }]
    let retType =
        match irOp with
        | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
            IRTScalar ETBool
        | _ -> retTy
    let callable =
        { mkLambdaCallable env.Builder parms body retType captures false [] [] false false 256 false
            with SourceName = operatorCallableName env irOp
                 // inside a `where repro` body it is a repro kernel (G74)
                 IsRepro = env.InRepro }
    env.LiftedCallables.Add(callable)
    let funcType =
        let paramTypes = callable.Params |> List.map _.Type
        mkFuncArrow paramTypes callable.RetType
    IRVar (callable.Id, funcType)

/// Lower typed binary operations
and lowerTypedBinOp env mode op l r leftExpr rightExpr resultType (loc: SrcLoc) =
    let irMode = match mode with Elementwise -> IRElementwise | Outer -> IROuter
    
    // Check if both operands are arrays -- if so, synthesize object_for loop
    let isArithOp = match op with
                    | OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret
                    | OpEq | OpNeq | OpLt | OpLe | OpGt | OpGe
                    | OpAnd | OpOr | OpMath2 _ -> true
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
                   | OpAnd -> IRAnd | OpOr -> IROr
                   | OpMath2 name -> IRMath2 name | _ -> IRAdd
        // A kernel param takes its operand's element type: the primitive
        // (through a unit annotation or index tag, which erase), or -- inside
        // a generic body -- the signature's element VARIABLE, kept open so HM
        // monomorphization clones the kernel per instance and substitutes it
        // (IRMono.specializeHMFunction's needsClone). Defaulting that variable
        // to Float64 computed an Int64 instance's `a * b` in double: wrong
        // past 2^53, narrowed on the way back. Float64 remains the fallback
        // only for a non-primitive element (a struct), which codegen refuses
        // downstream anyway.
        let kernelElemOf (t: IRType) : IRType =
            match t with
            | ArrayElem a ->
                match a.ElemType with
                | AnyPrimElem et -> IRTScalar et
                | InferElem _ as v -> v
                // A unit-carrying generic element (`T<m>^1`): the unit erases
                // like a concrete element's does; the variable stays open.
                | IRTUnitAnnotated (InferElem _ as v, _) -> v
                | _ -> IRTScalar ETFloat64
            | _ -> IRTScalar ETFloat64
        let elemTypeL = kernelElemOf leftExpr.Type
        let elemTypeR = kernelElemOf rightExpr.Type
        let aId = env.Builder.FreshId()
        let bId = env.Builder.FreshId()
        let body = IRBinOp(IRElementwise, irOp, IRVar (aId, elemTypeL), IRVar (bId, elemTypeR), loc)
        let parms : IRParam list = [
            { Name = "__a"; Type = elemTypeL; Index = 0; VarId = aId }
            { Name = "__b"; Type = elemTypeR; Index = 1; VarId = bId }
        ]
        // Comparison/logical ops produce bool; arithmetic ops keep the left
        // operand's element type (matches IRBinOp typing conventions).
        let kernelRetType =
            match irOp with
            | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr ->
                IRTScalar ETBool
            | _ -> elemTypeL
        let commGroups = if mode = Elementwise then [[0; 1]] else []
        let lambdaInfo =
            { mkLambdaCallable env.Builder parms body kernelRetType [] false commGroups [] false false 256 false
                with SourceName = operatorCallableName env irOp
                     // inside a `where repro` body it is a repro kernel (G74)
                     IsRepro = env.InRepro }
        env.LiftedCallables.Add(lambdaInfo)
        // Kernel slot references the lifted callable via IRVar;
        // genObjectForApplication uses resolveCallable + wrapper to consume it.
        let kernelFuncType =
            let paramTypes = lambdaInfo.Params |> List.map _.Type
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
                Name = $"__bc_s{sVar}"
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
    | OpAdd -> IRBinOp (irMode, IRAdd, l, r, loc)
    | OpSub -> IRBinOp (irMode, IRSub, l, r, loc)
    | OpMul -> IRBinOp (irMode, IRMul, l, r, loc)
    | OpDiv -> IRBinOp (irMode, IRDiv, l, r, loc)
    | OpMod -> IRBinOp (irMode, IRMod, l, r, loc)
    | OpCaret -> IRBinOp (irMode, IRCaret, l, r, loc)
    | OpEq -> IRBinOp (irMode, IREq, l, r, loc)
    | OpNeq -> IRBinOp (irMode, IRNeq, l, r, loc)
    | OpLt -> IRBinOp (irMode, IRLt, l, r, loc)
    | OpLe -> IRBinOp (irMode, IRLe, l, r, loc)
    | OpGt -> IRBinOp (irMode, IRGt, l, r, loc)
    | OpGe -> IRBinOp (irMode, IRGe, l, r, loc)
    | OpAnd -> IRBinOp (irMode, IRAnd, l, r, loc)
    | OpOr -> IRBinOp (irMode, IROr, l, r, loc)
    | OpMath2 name -> IRBinOp (irMode, IRMath2 name, l, r, loc)

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
        extractParallelism declParallel (decl.Params |> List.map _.Name)

    // Bind parameters in environment for body lowering
    // The anonymous functions in the body print as `lambda in <name>`.
    // `where repro` covers the WHOLE body: the lambdas and sections lifted
    // from it inherit the demand (gap G74; TypedLowerEnv.InRepro).
    let declRepro = (match decl.WhereClause with Some wc -> wc.Repro | None -> false) || env.InRepro
    let mutable paramEnv = withLambdaScope decl.SourceName { env with PolyParamNames = polyParamNames; InRepro = declRepro }
    let irParams = decl.Params |> List.map (fun p ->
        paramEnv <- bindTypedVar p.Name p.VarId paramEnv
        { Name = p.Name; Type = p.Type; Index = p.Index; VarId = p.VarId } : IRParam)

    // A function body is a body in exactly the sense lowerTypedLambda's wrap is
    // about: a `let` bound to a bare combinator has no forcing site downstream
    // (S2), and neither does the RETURN expression (S4, manifestation M-D) --
    // `function f(xs) = { xs <@> lambda(x) -> x * 2.0 }` used to return the
    // UNEVALUATED_COMPUTATION_USED_AS_VALUE sentinel. Same pair as the lambda
    // site, so the two share `forceCallableBody`.
    let body = forceCallableBody (lowerTypedExpr paramEnv decl.Body)

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
        { mkCallable env.Builder funcOpts irParams body decl.ReturnType []
                     (not decl.CommGroups.IsEmpty) decl.CommGroups
                     parallelism isOmpParallel isCudaKernel cudaBlockSize isMpiParallel
            with
                // `where repro`, grafted here exactly as lowerTypedLambda
                // grafts AntisymGroups: this is the one construction site
                // that still sees the clause.
                IsRepro = declRepro
                // The effect summary, from the same typed declaration -- the
                // shared legality fact fusion's splice reads (IRMono.pureBody).
                Effects = decl.Effects
                // What it prints as: an array instance (`walk__arrow1`) is
                // its origin's declaration and says the origin's name.
                SourceName = decl.SourceName }

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
        loweringIce noSpan "TTDMutualGroup lowers via lowerTypedDecl (one alias per member)"

/// Lower a typed binding
let lowerTypedBinding (env: TypedLowerEnv) (binding: TypedBinding) : IRBinding * TypedLowerEnv =
    let value = lowerBoundValue env binding.Name binding.Value
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
        let (isFlat, slotOf) = destructureLayout binding
        let subIRBindings = binding.SubBindings |> List.mapi (fun i (name, subId, subTy) ->
            let projExpr = subBindingValue binding isStruct isFlat i (slotOf i) name
            let env' = bindTypedVar name subId env'
            { Id = subId; Name = name; Type = subTy; Value = projExpr; IsConst = true; IsMutable = false })
        let env'' = binding.SubBindings |> List.fold (fun e (name, subId, _) -> bindTypedVar name subId e) env'
        // Constraint guards run right after the destructure. Their IRIds were
        // allocated by the checker after the sub-binding ids, so the module's
        // id-ordered emission places them correctly.
        let checkBindings =
            binding.PostChecks |> List.mapi (fun i (checkId, tCheck) ->
                { Id = checkId; Name = $"__mg_check{i}"; Type = IRTUnit
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
                // The binding's CHECKED width decides a numeric static's IR
                // form (round 6): a Float32 static is the float32 literal of
                // its (already float-rounded) fold, an Int32 static the Int32
                // literal -- the cast form core's Lowering gives every
                // standalone Int32 literal. StaticEval folded it at that width.
                let narrow =
                    match IR.stripUnits binding.Type, sv with
                    | IRTScalar ETFloat32, StaticEval.SVFloat f ->
                        Some (IRLit (IRLitFloat32 (float32 f)), binding.Type)
                    | IRTScalar ETInt32, StaticEval.SVInt n ->
                        Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt (int64 (int32 n)))), binding.Type)
                    | _ -> None
                let irValue, ty =
                    match narrow with
                    | Some (v, t) -> v, t
                    | None ->
                    staticValueToIR sv,
                    (match sv with
                     | StaticEval.SVInt _ -> IRTScalar ETInt64
                     | StaticEval.SVFloat _ -> IRTScalar ETFloat64
                     | StaticEval.SVBool _ -> IRTScalar ETBool
                     | StaticEval.SVStruct (n, _) -> IRTNamed n
                     | _ -> IRTUnit)
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
        let (isFlat, slotOf) = destructureLayout binding
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
                        let projExpr = subBindingValue binding isStruct isFlat i (slotOf i) name
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
        // Register unit in environment (same logic as the typecheck pipeline,
        // minus the hard errors). registerUnit already rejected BOTH resolver
        // failures -- a terminal-quantity misuse (BL3011) and an unknown name
        // (BL3015) -- so neither Error arm is reachable for input that got
        // this far. They stay only to keep lowering total; reaching one means
        // env.UnitDefs here disagrees with env.Units in typecheck, which is an
        // internal inconsistency and NOT the old warn-and-fallback (that
        // deliberately compiled a misspelling into a fresh base unit).
        let baseSig = unitOfDims (Map.ofList [(unitDecl.Name, 1)])
        let sig' =
            match unitDecl.Definition with
            | None | Some UnitBase -> baseSig
            | Some (UnitDerived expr) ->
                match TypeEnv.resolveUnitExpr env.UnitDefs expr with
                | Ok resolved -> resolved
                | Error err ->
                    eprintfn "internal: unit '%s' resolved in typecheck but not in lowering: %s"
                        unitDecl.Name (TypeEnv.ppUnitResolveErr err)
                    baseSig
            | Some (UnitQuantity expr) ->
                match TypeEnv.resolveUnitExpr env.UnitDefs expr with
                | Ok resolved -> { resolved with Nominal = Some unitDecl.Name }
                | Error err ->
                    eprintfn "internal: quantity '%s' resolved in typecheck but not in lowering: %s"
                        unitDecl.Name (TypeEnv.ppUnitResolveErr err)
                    { baseSig with Nominal = Some unitDecl.Name }
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
                              Streamed = false
                              AllowMissing = false }
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
                              Streamed = false
                              AllowMissing = false }
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
                              Streamed = true
                              AllowMissing = false }
                   | _ -> None)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Detect the masked read's hidden RAW read (ProviderDesugar.rawReadVerb):
/// `let __present_raw_t = s.vars.t |> alias.<raw>`. A dense whole-variable
/// spec like tryPlainRead's, marked AllowMissing: the boundary rule's cells
/// reach the presence mask instead of refusing the read. The verb's name is
/// not lexable, so no program can spell this read itself.
let tryRawRead (env: TypedLowerEnv) (binding: TypedBinding) : ProviderReadSpec option =
    let rec rootName (e: TypedExpr) =
        match e.Kind with
        | TExprVar (n, _, _) -> Some n
        | TExprField (inner, _, _) -> rootName inner
        | _ -> None
    match binding.Value.Kind with
    | TExprApp ({ Kind = TExprField ({ Kind = TExprVar (alias, _, _) }, verb, _) }, [inner])
        when verb = Blade.ProviderDesugar.rawReadVerb && Map.containsKey alias env.ProviderAliases ->
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
                              Streamed = false
                              AllowMissing = true }
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
                              Streamed = false
                              AllowMissing = false }
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
                  // matching Id; (c) the slot's own index TAG; (d) dim<i>.
                  //
                  // (c) exists because (b) misses the case it was written for.
                  // A named index type reaches an array through
                  // `lowerIndexType`, which "stamps a fresh Id per use"
                  // (TypeEnv's own note at the provider-axis registration), so
                  // `it.Id = idx.Id` does not hold for a user-declared alias
                  // and never did: `type LatIdx = Idx<2>` written out as
                  // `nc.write("out.nc", A)` produced "dim0", discarding the
                  // one name the program actually stated. The Tag survives
                  // that re-stamping -- it is the axis's NOMINAL identity, the
                  // same field `unify` refuses to co-iterate across -- so it is
                  // the reliable carrier of the declared name.
                  let fromSourceStore =
                      match Map.tryFind srcId env.ProviderReads with
                      | Some rspec ->
                          (match Blade.ProviderRegistry.tryFind rspec.Provider with
                           | Some p -> p.VarDimNames rspec.FilePath rspec.VarName
                           | None -> None)
                      | None -> None
                  // A tag only names a dimension if it names it UNIQUELY.
                  // `Array<T like LatIdx, LatIdx>` is one dimension used
                  // twice, but the writers emit one dimension DEFINITION per
                  // slot (netcdf's `nc_def_dim` would fail NC_ENAMEINUSE on
                  // the repeat), so a repeated tag names no slot and every
                  // slot carrying it falls through to dim<i> -- exactly
                  // today's behaviour for that shape. Reusing one dimid
                  // across slots is the writers' question, not this one's.
                  let repeatedTags =
                      arrTy.IndexTypes
                      |> List.choose (fun idx -> idx.Tag)
                      |> List.countBy id
                      |> List.choose (fun (t, n) -> if n > 1 then Some t else None)
                      |> Set.ofList
                  // `__`-prefixed tags are the compiler's own sentinels and
                  // provenance keys (`__anon`, `__orbidx`, `__icaxis|lat@...`)
                  // -- not names a user wrote, and not names a store should
                  // carry. An icechunk axis still gets its real name from
                  // (a), which asks the provider.
                  let nominalTag (idx: IRIndexType) =
                      match idx.Tag with
                      | Some t when not (t.StartsWith "__") && not (Set.contains t repeatedTags) -> Some t
                      | _ -> None
                  let dimNames =
                      match fromSourceStore with
                      | Some names when names.Length = arrTy.IndexTypes.Length -> names
                      | _ ->
                          arrTy.IndexTypes |> List.mapi (fun i idx ->
                              typeDefs
                              |> List.tryPick (function
                                  | IRTDIndexType (n, it) when it.Id = idx.Id -> Some n
                                  | _ -> None)
                              |> Option.orElseWith (fun () -> nominalTag idx)
                              |> Option.defaultValue $"dim{i}")
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
                         MutableArrayLets = ResizeArray<IRId>()
                         LetMutBinders = ResizeArray<IRId>() }
    // The module-level `let mut` binders (block / body lets are recorded as
    // they are lowered).
    for d in modul.Decls do
        match d with
        | TDeclLet b when b.IsLetMut ->
            env.LetMutBinders.Add b.VarId
            for (_, subId, _) in b.SubBindings do env.LetMutBinders.Add subId
        | _ -> ()
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
                        let qualName = $"{alias}.{kv.Key}"
                        currentEnv <- bindTypedVar qualName kv.Value.Id currentEnv
                    for kv in exports.Functions do
                        currentEnv <- { currentEnv with Functions = Map.add $"{alias}.{kv.Key}" kv.Value currentEnv.Functions }
                    for kv in exports.StructDefs do
                        currentEnv <- { currentEnv with StructDefs = Map.add kv.Key kv.Value currentEnv.StructDefs }
                    for kv in exports.UnitDefs do
                        currentEnv <- { currentEnv with UnitDefs = Map.add kv.Key kv.Value currentEnv.UnitDefs }
                    for kv in exports.StaticValues do
                        currentEnv <- { currentEnv with StaticValues = Map.add $"{alias}.{kv.Key}" kv.Value currentEnv.StaticValues }
                    for kv in exports.StaticFunctions do
                        currentEnv <- { currentEnv with StaticFunctions = Map.add $"{alias}.{kv.Key}" kv.Value currentEnv.StaticFunctions }
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
        | TDeclLet binding when (tryRawRead currentEnv binding).IsSome ->
            // The masked read's hidden raw half (ProviderDesugar): a deferred
            // dense read exempt from the boundary rule; the presence mask and
            // the compound that follow it are ordinary bindings.
            let spec = (tryRawRead currentEnv binding).Value
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
            //
            // The categorical weights array lowers in the same env for the same
            // reason -- it is always an earlier binding -- and carries the
            // checker-pinned static extent through unchanged.
            let kind, keyIR, parIRs, weightsIR, addressIR =
                match binding.Value.Kind with
                | TExprRandGen (k, key, pars, weights, address, _, _) ->
                    k,
                    lowerTypedExpr currentEnv key,
                    (pars |> List.map (lowerTypedExpr currentEnv)),
                    (weights |> Option.map (fun (w, n) -> (lowerTypedExpr currentEnv w, n))),
                    (address |> Option.map (fun (s, o) -> (lowerTypedExpr currentEnv s, lowerTypedExpr currentEnv o)))
                | _ -> "uniform", IRLit (IRLitInt 0L), [], None, None  // unreachable: guarded by the `when` above
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
            currentEnv <- { currentEnv with RandomInits = Map.add binding.VarId (RandGen (kind, keyIR, parIRs, weightsIR, addressIR)) currentEnv.RandomInits }
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
        LetMutBinders = Set.ofSeq currentEnv.LetMutBinders
        // Nothing is synthesized-from-another at lowering time; the
        // copy-producing passes (shapeMonomorphizeModule) fill this in.
        DerivedFuncOrigins = Map.empty
        FunctionAliasNames = Set.empty
    }
    (irModule, moduleExport)

/// The C++ spelling of a module name as an identifier prefix: `units.SI` ->
/// `units__SI`. The same function names a binding (whose IR name is the
/// dotted `units.SI.x`, which CodeGenState.sanitizeCppName renders
/// `units__SI__x`) and a function (`units__SI__f`), so both kinds of module
/// member share one scheme.
let moduleCppPrefix (moduleName: string) : string = moduleName.Replace(".", "__")

/// The qualifier of the module at `index` in a multi-module program.
let private moduleQualifier (index: int) (moduleName: string) : string =
    if moduleName = "" then $"m{index}" else moduleName

/// MODULE NAMESPACING. Every module of a program lands in ONE C++ translation
/// unit (CodeGen merges them) and in one interpreter environment, and nothing
/// used to qualify their names: `Util.scale + Util2.scale` passed the checker
/// and died in g++ as a redeclaration of `double scale`, and a main-module
/// `let scale` collided with an imported module's `scale` the same way. (Worse
/// than a redeclaration is the silent case: a main-module FUNCTION parameter
/// named `scale` shadowed the imported global inside that function.)
///
/// So every module but the LAST -- the entry (main) module, which every path
/// that assembles a program puts last: ModuleResolve's dependency order, the
/// multi-file corpus harness, `ide check`'s dependency prepend -- has its
/// members renamed here, ONCE, after every lowering and optimization pass, so
/// the C++ lane and the interpreter consume the same names:
///   - a top-level binding `x` of module `M` becomes `M.x`: that is also its
///     print LABEL in both lanes (`M.x = ...`) and its `--print` spelling, and
///     sanitizeCppName renders it `M__x` in C++;
///   - a function `f` becomes `M__f` (functions never print). Lifted lambdas
///     (`__lambda_<id>`) are id-unique already and keep their names.
/// Every reference is IRId-based (IRVar), and codegen's VarNames map is filled
/// from these Name fields, so the rename is total by construction. The main
/// module is untouched, so a single-module program emits byte-identical C++.
///
/// TYPES are not renamed: a type name declared by two modules of one program
/// is refused by the checker (BL2009, TypeCheck.checkProgram) until type
/// identity is module-qualified.
let qualifyModuleNames (program: IRProgram) : IRProgram =
    match program.Modules with
    | [] | [ _ ] -> program
    | modules ->
        { program with
            Modules =
                modules |> List.mapi (fun i m ->
                    if Blade.Types.TypeOrigins.isMainModule i modules.Length then m
                    else
                        let q = moduleQualifier i m.Name
                        let fp = moduleCppPrefix q
                        { m with
                            Functions =
                                m.Functions |> List.map (fun f ->
                                    if f.Name.StartsWith "__lambda_" then f
                                    else { f with Name = $"{fp}__{f.Name}" })
                            Bindings =
                                m.Bindings |> List.map (fun b ->
                                    // `_(a,b)` tuple carriers are emitted by id
                                    // (bindingCppName) and never print.
                                    if b.Name.StartsWith "_(" then b
                                    else { b with Name = $"{q}.{b.Name}" }) }) }

/// G190 (e): a module binding whose whole value is a FUNCTION reference
/// (`let bomb_alias = bomb`, directly or through another such alias) and that
/// nothing assigns is that function, a pure renaming (a function value is
/// immutable): every read of the alias reads the function itself. A body
/// emitted at namespace scope (a callee of a kernel that captures an enclosing
/// parameter) used to spell the alias, a `std::function` local of main() it
/// cannot see (BL9002 at g++); naming the function instead also gives the
/// main-locality fixpoint its reference edge. The alias binding itself stays.
let private inlineModuleFunctionAliases (m: IRModule) : IRModule =
    // DECLARED functions only: a module `let op = lambda(..) -> ..` keeps its
    // own name (its call stays an unresolved function-value call, which the
    // write and totality analyses already judge -- sql-joins/019)
    let funcIds =
        m.Functions
        |> List.filter (fun f -> not (f.Name.StartsWith "__lambda") && not (f.Name.StartsWith "lambda"))
        |> List.map (_.Id) |> Set.ofList
    if Set.isEmpty funcIds then m else
    let assigned = System.Collections.Generic.HashSet<IRId>()
    let noteAssigns (e: IRExpr) =
        for r in e :: Optimize.embeddedExtentExprs e do
            iterIRExpr (fun n ->
                match n with
                | IRAssign (LVVar id, _) -> assigned.Add id |> ignore
                | _ -> ()) r
    // EVERY executable payload (Codex diff review: a reassignment inside a
    // random fill's parameter lives in a side table, not a binding value)
    for root in Optimize.compactionRoots m.Functions m do
        match root with
        | Optimize.RootBindingValue b -> noteAssigns b.Value
        | Optimize.RootSideOperand (_, _, e) -> noteAssigns e
        | Optimize.RootFunctionBody f -> noteAssigns f.Body
        | Optimize.RootType (_, t) -> for x in Optimize.irTypeExtentExprs t do noteAssigns x
        | _ -> ()
    let aliasOf =
        m.Bindings
        |> List.fold (fun (acc: Map<IRId, IRId>) b ->
            match b.Value with
            | IRVar (fid, _) when not (assigned.Contains b.Id) ->
                if funcIds.Contains fid then Map.add b.Id fid acc
                else match Map.tryFind fid acc with
                     | Some root -> Map.add b.Id root acc
                     | None -> acc
            | _ -> acc) Map.empty
    if Map.isEmpty aliasOf then m else
    let targets = aliasOf |> Map.toList |> List.map snd |> Set.ofList
    let rw =
        mapIRExpr (fun e ->
            match e with
            | IRVar (id, ty) when aliasOf.ContainsKey id -> IRVar (aliasOf.[id], ty)
            // `bomb_alias |> compute`: computing a function value is the value
            // itself (the interpreter has no force for a bare function name)
            | IRCompute (IRVar (fid, ty)) when targets.Contains fid -> IRVar (fid, ty)
            | e -> e)
    { m with
        Bindings = m.Bindings |> List.map (fun b -> if aliasOf.ContainsKey b.Id then b else { b with Value = rw b.Value })
        Functions = m.Functions |> List.map (fun f -> { f with Body = rw f.Body }) }

/// Lower a typed program (with optional raw program for static evaluation)
/// Packed storage whose cell count does not fit int64 is a USER error at the
/// declaration that needs it (BL4003), not an internal one. A compact group's
/// count is a binomial -- C(n+r-1, r) symmetric/Hermitian, C(n, r)
/// antisymmetric, the iterated fold for an OrbIdx class -- and an absurd
/// extent or rank pushes it past int64. Every pool sizer computes it through
/// OrbRank's exactly-checked arithmetic, which used to surface the overflow as
/// an exception the driver could only report as BL9001 ("internal compiler
/// error ... please report it"). Checked here, once literal extents are known
/// (after shape monomorphization), over every top-level binding's type and
/// every function's parameter and return types; the whole array's cell count
/// (the compact groups times the dense extents) has to fit too.
let private refuseUncountableStorage (modules: IRModule list) : unit =
    let groupCells (ix: IRIndexType) : Result<int64, string> option =
        match ix.Extent with
        | IROrbitClass (levels, IRLit (IRLitInt n)) ->
            Some (Blade.OrbRank.cellCountChecked (orbRankLevels levels) n
                  |> Result.mapError (fun e -> $"the class's iterated binomial over {n}: {e}"))
        | IRLit (IRLitInt n) ->
            let r = max 1 ix.Rank
            match ix.Symmetry with
            | SymSymmetric | SymHermitian when r >= 2 ->
                Some (Blade.OrbRank.binomChecked (n + int64 r - 1L) r |> Result.mapError (fun e -> $"C({n + int64 r - 1L}, {r}): {e}"))
            | SymAntisymmetric when r >= 2 ->
                Some (Blade.OrbRank.binomChecked n r |> Result.mapError (fun e -> $"C({n}, {r}): {e}"))
            | _ ->
                // A dense group: n per axis.
                Some ([ 1 .. r ] |> List.fold (fun acc _ -> acc |> Result.bind (fun a -> Blade.OrbRank.mulChecked a n)) (Ok 1L))
        | _ -> None
    let rec arrays (t: IRType) : IRArrayType list =
        match stripUnits t with
        | ArrayElem at -> at :: arrays at.ElemType
        | IRTTuple ts -> ts |> List.collect arrays
        | IRTComputation inner -> arrays inner
        | _ -> []
    let check (owner: string) (shown: string) (t: IRType) =
        for at in arrays t do
            let compact =
                at.IndexTypes |> List.exists (fun ix ->
                    ix.Rank >= 2 && ix.Symmetry <> SymNone || not (List.isEmpty (orbitLevelsOf ix)))
            if compact then
                let total =
                    at.IndexTypes |> List.choose groupCells
                    |> List.fold (fun acc r -> acc |> Result.bind (fun a -> r |> Result.bind (Blade.OrbRank.mulChecked a))) (Ok 1L)
                match total with
                | Ok _ -> ()
                | Error detail ->
                    raise (Blade.Diagnostics.BladeDiagnosticException
                            (Blade.Diagnostics.mkError "BL4003" Blade.Diagnostics.PhConstraints (IR.declSpanOf owner)
                                $"the packed storage of {shown} ({ppIRType (mkArrayLike at)}) has more cells than a 64-bit count can hold ({detail}): a compact array this large cannot be allocated. Reduce the extent or the symmetry rank."))
    for m in modules do
        for b in m.Bindings do check b.Name $"'{b.Name}'" b.Type
        for f in m.Functions do
            for p in f.Params do check f.Name $"parameter '{p.Name}' of '{f.SourceName}'" p.Type
            check f.Name $"the result of '{f.SourceName}'" f.RetType

/// THE SORT KEY'S RESULT TYPE after monomorphization (gap G126): the type
/// check refuses a complex / tuple / array key it can see, but a key whose
/// result is still a type variable there (`function ordered(a: T^1) = sort(a,
/// lambda(x) -> x)` called on a complex array) is fixed only by the instance.
/// Checked once every instance is concrete, the same BL3033 the checker gives.
let private refuseUnorderableSortKeys (modules: IRModule list) : unit =
    let funcs = modules |> List.collect (fun m -> m.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let unorderable (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETComplex64 | ETComplex128) | IRTTuple _ | ArrayElem _ -> true
        | IRTIdxTagged (inner, _) -> (match stripUnits inner with IRTScalar (ETComplex64 | ETComplex128) -> true | _ -> false)
        | _ -> false
    let checkExpr (owner: string) (e: IRExpr) =
        // A key named through a local alias (`let key = lambda ..` then
        // `sort(xs, key)`, Codex review round 2): the lets of this body, so
        // the alias chain resolves to the callable.
        let letValues = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        iterIRExpr (fun n -> match n with IRLet (id, v, _) -> letValues.[id] <- v | _ -> ()) e
        let rec callableRet (seen: Set<IRId>) (k: IRExpr) =
            match k with
            | IRVar (kid, _) ->
                (match Map.tryFind kid funcs with
                 | Some f -> Some f.RetType
                 | None ->
                     match letValues.TryGetValue kid with
                     | true, v when not (seen.Contains kid) -> callableRet (seen.Add kid) v
                     | _ -> resolveCallable k |> Option.map (fun c -> c.RetType))
            | _ -> resolveCallable k |> Option.map (fun c -> c.RetType)
        iterIRExpr (fun n ->
            match n with
            | IRSort (_, key) ->
                let ret = callableRet Set.empty key
                match ret with
                | Some r when unorderable r ->
                    raise (Blade.Diagnostics.BladeDiagnosticException
                            (Blade.Diagnostics.mkError "BL3033" Blade.Diagnostics.PhTypes (IR.declSpanOf owner)
                                $"sort(A, key): the key must return a real, integer, Bool or String scalar -- the types sort's order is defined for -- but at this instance it returns {ppIRType r}. A complex value has no order, and a tuple or array key is not supported."))
                | _ -> ()
            | _ -> ()) e
    for m in modules do
        for b in m.Bindings do checkExpr b.Name b.Value
        for f in m.Functions do checkExpr f.Name f.Body

/// ONE EVALUATION CONTRACT FOR AN EFFECTFUL NAMED DEFERRED MAP (gap G45;
/// formalism §10.2's closing paragraph). A `let` bound to a bare map
/// (`method_for(..) <@> k`, an `object_for` application) whose evaluation
/// WRITES -- its kernel, a callee, or a named deferred producer it reads
/// assigns storage outside itself -- is evaluated ONCE, COMPLETELY, at its
/// definition, in ascending storage order, exactly as `|> compute` would:
/// every later read (two folds, a print, a partial view) reads the stored
/// array. That was already the rule in a FUNCTION or LAMBDA body, where S2
/// (forceBareCombinatorLets) forces every body-local map at its let for want
/// of a downstream forcing site; at MODULE level and in a top-level block the
/// map stayed deferred and ran once per READ, so the same two folds printed
/// 36 / counter 8 in a block and 20 / counter 4 once the block was extracted
/// into a function. A pure map keeps the deferred protocol everywhere (its
/// evaluation count is unobservable, and fusion depends on it).
///
/// The exemption is S2's own, so the two scopes keep ONE rule: a map read
/// ONLY as the operands of reduction joins stays deferred (sharing by naming:
/// the join inlines it per joint cell, and the C++ reduction-join emitter
/// needs the leg unforced), so a writing one is refused by the join rule
/// (refuseWritingJoinLegs, BL4005, which runs AFTER this pass) in a function
/// body and at module level alike. A writing map with any other reader -- an
/// alias, a view, a tuple, a plain fold -- is forced, and a join that reaches
/// it through that reader reads the stored array: no writing leg. (An unread
/// closed-form domain map, S2's second exemption, is never left unread when
/// it writes: the fold splice declines a named writing producer --
/// tryInferReduceCompute's namedWritingProducer -- so the fold reads it.)
/// A writing value-keyed WRAPPER over arrays (choice / fallback,
/// isArrayWrapperValue) is forced by the same rule (gap G50: the interpreter
/// re-ran its writing side per fold in a block or body while both compiled
/// lanes ran it once); guard and sequence keep the deferred protocol, and a
/// join over a writing wrapper it reads only as operands is still refused
/// (mutability-errors/023). The write judgment is the shared scheduling
/// predicate (an unresolvable callee counts as a write: forcing a map that
/// does not write changes no value, only its schedule).
let private forceEffectfulModuleLets (programFuncs: IRCallable list) (m: IRModule) : IRModule =
    let writes = Optimize.scheduleMayWriteIn programFuncs m
    let exempt = joinDeferrableIdsMany (m.Bindings |> List.map _.Value)
    // G50: a writing value-keyed wrapper (choice / fallback) obeys the same
    // rule as a writing map -- once, at its definition
    let isBareMap (v: IRExpr) =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ -> true
        | v -> isArrayWrapperValue v
    // the statement spine of a top-level block, as S2 walks a body
    let rec spine (e: IRExpr) : IRExpr =
        match e with
        | IRLet (id, value, body) ->
            let value' =
                if isBareMap value && not (exempt.Contains id) && writes value then IRCompute value
                else spine value
            IRLet (id, value', spine body)
        | IRIf (c, t, f) -> IRIf (c, spine t, spine f)
        | IRForRange (vid, lo, hi, body) -> IRForRange (vid, lo, hi, spine body)
        | IRMatch (scrut, cases) -> IRMatch (scrut, cases |> List.map (fun c -> { c with Body = spine c.Body }))
        | _ -> e
    let bindings =
        m.Bindings |> List.map (fun b ->
            if isBareMap b.Value then
                if not (exempt.Contains b.Id) && writes b.Value then { b with Value = IRCompute b.Value } else b
            else { b with Value = spine b.Value })
    { m with Bindings = bindings }

/// The program-wide context the write refusals share: every callable, every
/// let-bound value (ids are unique per program), the assignable block lets,
/// let-alias chasing, the callee resolver and the shared write analysis's facts
/// (Optimize.exprMayWriteWith / callableWriteSummaryWith).
let private programWriteContext (modules: IRModule list) =
    let funcs =
        modules |> List.collect (fun m -> m.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    // Every let-bound value in the program (ids are unique per program): a leg
    // that NAMES a deferred computation, an alias of one, or an alias of a
    // callable resolves through it.
    let lets = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    // the lets of the extents types carry too (a range bound may bind its
    // own scratch: round 4 wave 6, the shared write analysis's roots)
    let noteLets (e: IRExpr) =
        for r in e :: Optimize.embeddedExtentExprs e do
            iterIRExpr (fun n -> match n with IRLet (id, v, _) -> lets.[id] <- v | _ -> ()) r
    for m in modules do
        for b in m.Bindings do
            lets.[b.Id] <- b.Value
            noteLets b.Value
        for f in m.Functions do noteLets f.Body
        // the side tables' payloads bind lets too (a random fill's parameter
        // block): G190 (c) judges their calls (Codex diff review round 3)
        for root in Optimize.compactionRoots m.Functions m do
            match root with
            | Optimize.RootSideOperand (_, _, e) -> noteLets e
            | _ -> ()
    let mutableLets = modules |> List.fold (fun acc m -> Set.union acc m.MutableArrayLets) Set.empty
    let rec chaseAlias (seen: Set<IRId>) (e: IRExpr) : IRExpr =
        match e with
        | IRVar (id, _) when not (funcs.ContainsKey id) && not (seen.Contains id) ->
            (match lets.TryGetValue id with
             | true, (IRVar _ as v) -> chaseAlias (seen.Add id) v
             | _ -> e)
        | _ -> e
    let resolve (e: IRExpr) =
        match chaseAlias Set.empty e with
        | IRVar (id, _) -> Map.tryFind id funcs
        | _ -> None
    // `let mut a = Z` over a dense array deep-copies Z in every lane.
    let copied (id: IRId) =
        mutableLets.Contains id
        && (match lets.TryGetValue id with
            | true, (IRVar (_, ty)) ->
                (match stripUnits ty with
                 | ArrayElem at -> at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
                 | _ -> false)
            | _ -> false)
    let facts : Optimize.WriteFacts =
        { Resolve = resolve
          OuterLet = (fun id -> match lets.TryGetValue id with | true, v -> Some v | _ -> None)
          CopiedLet = copied
          UnknownIsWrite = true
          Callbacks = (fun _ -> None) }
    (funcs, lets, mutableLets, chaseAlias, resolve, facts)

/// THE REPRO DEMAND ON EACH FOLD USE (gap G80). A fold performed in a `where
/// repro` body declines reordering WHATEVER ITS KERNEL (formalism 2.4: the
/// body "declines reordered folds"), but every lane's licence predicate
/// (`CodeGenExprSupport.foldReorderLicensed`, which the LLVM lane's
/// `foldFmfLicensed` / `foldFmfDecorable` read) consults the KERNEL's
/// `IsRepro`: a kernel lifted from the body carries it (TypedLowerEnv.InRepro),
/// a callable declared ELSEWHERE -- `reduce(xs, add)` with `add` a top-level or
/// imported function, a lambda lifted outside the body -- does not. So in the
/// body of every `IsRepro` callable of the program, each `reduce` (stored and
/// fused; a join's kernels leg by leg) whose kernel names such a binary
/// callable -- directly, or through `let k = add` aliases (local or
/// top-level, any chain) -- is pointed, FOR THAT USE ONLY, at the callable's
/// REPRO TWIN: an exact copy (same parameters, parameter ids, body, captures,
/// clauses and SourceName; a fresh id and emitted name; `IsRepro`) placed
/// right after its origin IN THE MODULE THAT DEFINES IT and recorded in
/// `DerivedFuncOrigins` (so codegen emits it at its origin's program point,
/// a main-local `std::function` included). Sharing the parameter ids keeps
/// every nested closure the body lifts valid -- a lambda inside the kernel
/// capturing its parameter captures the twin's (shape monomorphization's
/// copies share ids the same way); the SourceName keeps a panic's frame, and
/// `copyTwinDeclSpans` its declaration location, those of the origin. Every
/// clause is copied, so a `where omp` request is still a request, declined
/// by the repro veto with its census line. The twin's body is compiled
/// strict too (contraction off), as the body that runs it is; the origin's
/// other uses keep their licence. A function-typed PARAMETER kernel has no
/// callable to point at -- both compiled lanes refuse such a fold anyway
/// (BL7004 deferred, a pre-existing BL9002 stored) -- and is left as written.
/// Runs over the WHOLE PROGRAM after monomorphization, before every
/// per-module pass; a no-op in the reproducible build, where every fold is
/// strict. Returns the program and the (twin id, origin id) pairs.
let reproFoldUsesProgram (irModules: IRModule list) (builder: IRBuilder) : IRModule list * (IRId * IRId) list =
    let allFuncs = irModules |> List.collect (fun m -> m.Functions)
    if Blade.Toolchain.fpReproEnabled () || not (allFuncs |> List.exists (fun f -> f.IsRepro)) then irModules, []
    else
    let funcs = allFuncs |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let twins = System.Collections.Generic.Dictionary<IRId, IRCallable>()   // origin id -> its repro twin
    // a NAME bound to a callable -- `let k = add` in a body or at module level,
    // through any chain of such lets -- is that callable here
    let aliasOf =
        let acc = System.Collections.Generic.Dictionary<IRId, IRId>()
        let note (id: IRId) (v: IRExpr) = match v with IRVar (g, FuncElem _) -> acc.[id] <- g | _ -> ()
        for m in irModules do
            for b in m.Bindings do note b.Id b.Value
            for f in m.Functions do iterIRExpr (fun e -> match e with IRLet (id, v, _) -> note id v | _ -> ()) f.Body
        let rec chase (seen: Set<IRId>) (id: IRId) =
            match acc.TryGetValue id with
            | true, g when not (seen.Contains g) && not (funcs.ContainsKey id) -> chase (seen.Add id) g
            | _ -> id
        chase Set.empty
    let rec variantOf (k: IRExpr) : IRExpr =
        match k with
        | IRTuple ks -> IRTuple (ks |> List.map variantOf)
        | IRVar (id0, (FuncElem (paramTys, _) as fty)) when paramTys.Length = 2 ->
            let id = aliasOf id0
            (match Map.tryFind id funcs with
             | Some c when not c.IsRepro && c.Params.Length = 2 ->
                 let twin =
                     match twins.TryGetValue id with
                     | true, w -> w
                     | _ ->
                         let cid = builder.FreshId()
                         let w = { c with Id = cid; Name = $"{c.Name}__repro{cid}"; IsRepro = true }
                         twins.[id] <- w
                         w
                 IRVar (twin.Id, fty)
             | _ -> k)
        | _ -> k
    let rewriteBody (body: IRExpr) =
        body |> mapIRExpr (fun e ->
            match e with
            | IRReduce (src, k, init) -> IRReduce (src, variantOf k, init)
            | IRReduceCompute (comp, k, init) -> IRReduceCompute (comp, variantOf k, init)
            | _ -> e)
    let rewritten =
        irModules |> List.map (fun m ->
            { m with Functions = m.Functions |> List.map (fun f -> if f.IsRepro then { f with Body = rewriteBody f.Body } else f) })
    if twins.Count = 0 then irModules, []
    else
        let placed =
            rewritten |> List.map (fun m ->
                let mine = m.Functions |> List.choose (fun f -> match twins.TryGetValue f.Id with | true, w -> Some (f.Id, w) | _ -> None)
                if mine.IsEmpty then m
                else
                    { m with
                        Functions = m.Functions |> List.collect (fun f -> match twins.TryGetValue f.Id with | true, w -> [ f; w ] | _ -> [ f ])
                        DerivedFuncOrigins = mine |> List.fold (fun acc (oid, w) -> Map.add w.Id oid acc) m.DerivedFuncOrigins })
        placed, (twins |> Seq.map (fun kv -> (kv.Value.Id, kv.Key)) |> List.ofSeq)

/// The repro twins' DECLARATION SPANS (gap G80 / G76): a twin is located
/// where its origin is declared, in every lane. Run on the FINAL program
/// (after module qualification, which prefixes a non-main module's names):
/// the twin's emitted name gets its origin's recorded span.
let copyTwinDeclSpans (program: IRProgram) (pairs: (IRId * IRId) list) : unit =
    if not pairs.IsEmpty then
        let byId = program.Modules |> List.collect (fun m -> m.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
        for (tid, oid) in pairs do
            match Map.tryFind tid byId, Map.tryFind oid byId with
            | Some t, Some o ->
                let s = IR.declSpanOf o.Name
                let s = if s.StartLine > 0 then s else IR.declSpanOf o.SourceName
                IR.recordDeclSpan t.Name s
            | _ -> ()

/// The kernel-invariant hoist's minted callables (gap G151): each takes the
/// span of the declaration its code stood in per cell -- the kernel, or the
/// function / binding the nest stands in (Optimize.HoistDeclSource) -- keyed by
/// its FINAL name, after module qualification, as `copyTwinDeclSpans` does.
/// Mint order: a source is never a callable minted later.
let copyHoistDeclSpans (program: IRProgram) (pairs: (IRId * Optimize.HoistDeclSource) list) : unit =
    if not pairs.IsEmpty then
        let fns = program.Modules |> List.collect (fun m -> m.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
        let bs = program.Modules |> List.collect (fun m -> m.Bindings) |> List.map (fun b -> (b.Id, b)) |> Map.ofList
        for (mid, src) in pairs do
            match Map.tryFind mid fns with
            | Some m ->
                let s =
                    match src with
                    | Optimize.LocatedAsCallable oid ->
                        (match Map.tryFind oid fns with
                         | Some o ->
                            let s = IR.declSpanOf o.Name
                            if s.StartLine > 0 then s else IR.declSpanOf o.SourceName
                         | None -> Blade.Ast.noSpan)
                    | Optimize.LocatedAsBinding bid ->
                        (match Map.tryFind bid bs with
                         | Some b -> IR.declSpanOf b.Name
                         | None -> Blade.Ast.noSpan)
                IR.recordDeclSpan m.Name s
            | None -> ()

/// Every EXECUTABLE expression of a module: function bodies, binding values
/// and the side-table payloads each back end and the interpreter run at a
/// binding's site -- random fills (the modulus; a generator's key,
/// parameters, weights and address), compound constructors (dense source and
/// mask) and sparse constructors (the values). Provider specs carry no
/// expression.
let moduleExecutablePayloads (m: IRModule) : IRExpr list =
    [ for f in m.Functions do yield f.Body
      for b in m.Bindings do yield b.Value
      for kv in m.RandomInits do
          match kv.Value with
          | FillModulus e -> yield e
          | RandGen (_, key, pars, weights, address) ->
              yield key
              yield! pars
              match weights with Some (w, _) -> yield w | None -> ()
              match address with Some (s, o) -> yield s; yield o | None -> ()
      for kv in m.CompoundInits do
          let (d, k) = kv.Value
          yield d
          yield k
      for kv in m.SparseInits do yield kv.Value ]

/// `moduleExecutablePayloads`'s surface, rewritten: `body` over function
/// bodies and binding values, `side` over the side-table payloads.
let mapModuleExecutablePayloads (body: IRExpr -> IRExpr) (side: IRExpr -> IRExpr) (m: IRModule) : IRModule =
    { m with
        Functions = m.Functions |> List.map (fun f -> { f with Body = body f.Body })
        Bindings = m.Bindings |> List.map (fun b -> { b with Value = body b.Value })
        RandomInits =
            m.RandomInits |> Map.map (fun _ spec ->
                match spec with
                | FillModulus e -> FillModulus (side e)
                | RandGen (kind, key, pars, weights, address) ->
                    RandGen (kind, side key, pars |> List.map side,
                             weights |> Option.map (fun (w, n) -> (side w, n)),
                             address |> Option.map (fun (s, o) -> (side s, side o))))
        CompoundInits = m.CompoundInits |> Map.map (fun _ (d, k) -> (side d, side k))
        SparseInits = m.SparseInits |> Map.map (fun _ e -> side e) }

/// THE SHAPE OF A DEFERRED MAP, READ FROM ITS OPERANDS (gap G234; formalism
/// 10.2, the G218 rule): an EXTENT read of a deferred map evaluates nothing --
/// a static extent is a literal of its type, and a RUN-TIME extent (a map over
/// a compound) is the extent of the operand it maps cell for cell. The C++
/// lane and the interpreter forced the map to read
/// `d.idx->cardinality` / its stored extents (BL8013 at d); the LLVM lane read
/// the operand's (the agreed outcome). One rewrite before the lane split, over
/// MAIN-LEVEL code (module binding values; kernels are lifted callables, never
/// inside a binding value, so a capture that reads only `extents(d)` inside a
/// kernel still forces d, G214): `extents(d, k)` of a deferred ELEMENTWISE map
/// (one operand, or a zip co-iteration: every kernel input and the output a
/// cell, no Reynolds, an unsymmetric output of the operand's rank) whose
/// extent at k is not static becomes `extents(o, k)` of its first operand o,
/// through deferred aliases and chains of such maps, when o is a STORED binding
/// (not deferred). Anything else keeps the force (a range with a run-time
/// bound, a projection of a deferred tuple, a block value: staged, G234).
let resolveDeferredExtentsModule (modul: IRModule) : IRModule =
    let values = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    for b in modul.Bindings do values.[b.Id] <- b.Value
    let deferred = computeDeferredIds modul.Bindings
    let shapeOf (e: IRExpr) =
        match stripUnits (typeOf e) with
        | ArrayElem at -> Some at
        | _ -> None
    let staticAt (e: IRExpr) (dim: int) =
        match shapeOf e with
        | Some at when dim >= 0 && dim < at.IndexTypes.Length -> (tryEvalIntIR at.IndexTypes.[dim].Extent).IsSome
        | _ -> false
    let sameShape (a: IRExpr) (b: IRExpr) =
        match shapeOf a, shapeOf b with
        | Some x, Some y ->
            x.IndexTypes.Length = y.IndexTypes.Length
            && (x.IndexTypes |> List.sumBy (fun ix -> ix.Rank)) = (y.IndexTypes |> List.sumBy (fun ix -> ix.Rank))
            && x.IndexTypes |> List.forall (fun ix -> ix.Symmetry = SymNone)
        | _ -> false
    let rec source (seen: Set<IRId>) (e: IRExpr) : IRExpr option =
        match e with
        | IRVar (id, _) when deferred.Contains id && not (seen.Contains id) ->
            (match values.TryGetValue id with
             | true, (IRVar _ as alias) -> source (seen.Add id) alias
             | true, IRApplyCombinator info
                when (info.Arrays.Length = 1 || info.IsCoIteration)
                     && not info.Arrays.IsEmpty
                     && info.KernelInputRanks |> List.forall ((=) 0)
                     && info.KernelOutputRank = 0
                     && not info.HasReynolds
                     && sameShape e info.Arrays.Head ->
                (match info.Arrays.Head with
                 | IRVar (oid, _) as op when deferred.Contains oid -> source (seen.Add id) op
                 | IRVar _ as op -> Some op
                 | _ -> None)
             | _ -> None)
        | _ -> None
    let rewrite (e: IRExpr) : IRExpr =
        e |> rewriteTopDown (fun e ->
            match e with
            | IRExtent ((IRVar _ as a), dim) when not (staticAt a dim) ->
                ValueSome (match source Set.empty a with
                           | Some o -> IRExtent (o, dim)
                           | None -> e)
            | _ -> ValueNone)
    { modul with Bindings = modul.Bindings |> List.map (fun b -> { b with Value = rewrite b.Value }) }

/// THE UNSEEDED-SEED RESOLUTION (gap G74): every `IRUnseededSeed` of the
/// module's EXECUTABLE payloads (`moduleExecutablePayloads`: bodies, binding
/// values and side tables) becomes an ordinary seed, after monomorphization
/// (types concrete), after `reproFoldUsesProgram` and BEFORE the optimizer
/// (whose passes and plans read literal seeds); a surviving mark is an
/// internal error. The seed is the EXACT identity (`addIdentityLiteralOf`)
/// on every route that carries NO reorder licence -- the facts each compiled
/// lane's effective licence reads (C++ `licensedFold = foldReorderLicensed
/// callable && not mapBodyWrites`, LLVM `foldFmfLicensed`'s
/// `FoldOperandWrites`): the reproducible build; a `where repro` body (the
/// node's flag); a repro-vetoed kernel (`foldKernelReproVetoed`'s shapes,
/// the G80 twins included); a deferred computation that MAY WRITE (the shared
/// write analysis with callbacks joined, `Optimize.scheduleMayWriteWithCallbacksIn`,
/// asked of the code the emitters will see: constant-scrutinee matches
/// folded as the optimizer's first pass folds them) -- and the BASE seed,
/// byte for byte, on a licensed route, INCLUDING a licensed fold some lane
/// emits serially for want of a vector form (a streamed-source,
/// multi-level or mixed-operator join: formalism 6.4, its zero is the
/// licence's). A join's seeds share its computation's verdict (a writing leg
/// is refused at lowering, BL4005); a mark outside any fold node (none is
/// built today) is judged by the build and its flag alone. Tested on the
/// witnesses in `blade test orbit-scatter` (route leg) and the base-vs-head
/// emission sweep (`bench/harness/emission-sweep.ps1`), not proved in
/// general.
let resolveUnseededSeedsModule (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    let hasMark (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRUnseededSeed _ -> hit <- true | _ -> ()) e
        hit
    if not (moduleExecutablePayloads modul |> List.exists hasMark) then modul
    else
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    // The write analysis asks of the code the EMITTERS will see: the
    // optimizer's first pass folds constant-scrutinee matches (a dead arm's
    // write disappears there -- the second Codex look of W4B-D, finding 3:
    // `match 0 with | 0 -> x | _ -> { G(0) = 1.0; x }` is licensed in both
    // emitters), so the analysis runs over the module with that same fold
    // applied to its bodies and bindings (side-table payloads are not folded
    // by the optimizer, and are judged as written). No other pre-emission
    // pass removes a write (each consults the shared write analysis and
    // declines on one); that is tested on the witnesses, not proved.
    // ...and so must every OTHER module's callables the analysis resolves: an
    // imported kernel's dead writing arm is folded away by ITS module's
    // optimizer before emission too (the third Codex look of W4B-D). The
    // caller passes the whole-program snapshot ALREADY folded
    // (`foldedProgramFuncs`); this module's own entries are refolded here
    // (its per-module passes ran since the snapshot), and the analysis --
    // the callback table included -- sees only folded records.
    let folded = IRMono.foldConstMatchesModule modul
    let mayWrite = Optimize.scheduleMayWriteWithCallbacksIn programFuncs folded
    let repro = Blade.Toolchain.fpReproEnabled ()
    // `foldKernelReproVetoed`'s two shapes, over this module's callables
    let rec kernelRepro (k: IRExpr) =
        match k with
        | IRTuple ks -> ks |> List.exists kernelRepro
        | IRVar (id, _) ->
            (match Map.tryFind id funcs with
             | Some c ->
                 c.IsRepro
                 || (match c.Body with
                     | CallOfNamed (g, _, _, _) -> (match Map.tryFind g funcs with Some cg -> cg.IsRepro | None -> false)
                     | _ -> false)
             | None -> false)
        | _ -> false
    // the identity at the MARK's type (the fold's accumulator type, carried
    // through HM substitution and IRMono's open-seed arm); the base value's
    // own type only when the mark's says nothing (a still-open variable) --
    // a generic base can be IRMono's Float64 fallback zero, whose type is not
    // the fold's (a Complex128 instance kept (-0.0, -0.0) this way)
    let identityOf (b: IRExpr) (ty: IRType) =
        match addIdentityLiteralOf ty with
        | Some l -> l
        | None ->
            match exprTypeIfKnown b |> Option.bind addIdentityLiteralOf with
            | Some l -> l
            | None -> b
    let pick (strict: unit -> bool) (seed: IRExpr) =
        match seed with
        | IRUnseededSeed (b, ty, r) -> if r || strict () then identityOf b ty else b
        | other -> other
    let resolve (foldDead: bool) (e: IRExpr) =
        match e with
        | IRReduceCompute (comp, k, init) when hasMark init ->
            let strict = lazy (repro || kernelRepro k || mayWrite (if foldDead then IRMono.foldConstMatchesExpr comp else comp))
            let strictFor () = strict.Value
            (match init with
             | IRTuple ss -> IRReduceCompute (comp, k, IRTuple (ss |> List.map (pick strictFor)))
             | _ -> IRReduceCompute (comp, k, pick strictFor init))
        | _ -> e
    let leftover (e: IRExpr) =
        match e with
        | IRUnseededSeed (b, ty, r) -> if repro || r then identityOf b ty else b
        | _ -> e
    let rewriteWith (foldDead: bool) (x: IRExpr) = x |> mapIRExpr (resolve foldDead) |> mapIRExpr leftover
    let resolved = mapModuleExecutablePayloads (rewriteWith true) (rewriteWith false) modul
    // FAIL CLOSED: no mark survives anywhere a back end or the interpreter
    // executes (the first W4B-D cut missed the side tables -- a random key or
    // parameter, a compound's mask -- review finding 1)
    if moduleExecutablePayloads resolved |> List.exists hasMark then
        failwith $"internal: an unseeded-fold seed survived its resolution in module {modul.Name} (gap G74)"
    resolved
/// A JOIN WHOSE LEG WRITES (gap G43, BL4005). A join -- a `<&!>` / `<&>`
/// tree, or a reduction join `object_for(<&!>) <@> (reduce(..), ..)` -- runs
/// its legs side by side, one joint cell at a time, and legs naming the same
/// named deferred computation evaluate it ONCE per joint cell (formalism
/// §10.2). That schedule is the join's meaning for values; it was never a
/// promise about effects, and the lanes kept three different ones (the C++
/// lane shared the cell, the LLVM lane interleaved one evaluation per leg,
/// the interpreter ran the legs one after another), so a leg whose map kernel
/// or fold kernel WRITES a captured binding printed three different answers.
/// A serial write is an ordered side effect (§5.2), and a join has no single
/// order to give it: refused, like a write in a parallel body. The fix is to
/// force the writing map first (`|> compute`: one evaluation, stored) or fold
/// the legs separately. A joined evaluation must be PROVED free of observable
/// writes: an unresolvable callee or function-typed kernel counts as a write
/// (Optimize.exprMayWriteWith). Spanned at the enclosing declaration.
let private refuseWritingJoinLegs (modules: IRModule list) : unit =
    let (funcs, lets, mutableLets, chaseAlias, resolve, facts) = programWriteContext modules
    ignore mutableLets
    // An unresolved call head counts as a write here too: a joined evaluation
    // must be PROVED free of observable writes, not merely not seen writing.
    let writes (e: IRExpr) = Optimize.exprMayWriteWith facts e
    // The joint computation, through let aliases of a named tree.
    let rec resolveComp (seen: Set<IRId>) (e: IRExpr) : IRExpr =
        match e with
        | IRVar (id, _) when not (seen.Contains id) ->
            (match lets.TryGetValue id with
             | true, ((IRVar _ | IRFusion _ | IRParallel _) as v) -> resolveComp (seen.Add id) v
             | _ -> e)
        | _ -> e
    let rec leavesOf (e: IRExpr) : IRExpr list =
        match e with
        | IRFusion (l, r) | IRParallel (l, r, _) -> leavesOf l @ leavesOf r
        | other -> [ other ]
    // A destructuring `let (a, b) = <join>` binds a hidden tuple whose name has
    // no surface span: locate it at the first name the pattern binds.
    let projSpans =
        modules |> List.collect (fun m -> m.Bindings)
        |> List.choose (fun b ->
            match b.Value with
            | ProjOfNamed (src, _, _, _) -> Some (src, b.Name)
            | _ -> None)
    let spanOf (owner: string) (ownerId: IRId option) =
        let s = IR.declSpanOf owner
        if s <> Blade.Ast.noSpan then s
        else
            match ownerId |> Option.bind (fun id -> projSpans |> List.tryFind (fun (src, _) -> src = id)) with
            | Some (_, n) -> IR.declSpanOf n
            | None -> s
    let refuse (owner: string) (ownerId: IRId option) =
        raise (Blade.Diagnostics.BladeDiagnosticException
                (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints (spanOf owner ownerId)
                    "a leg of this join writes a captured binding: a join (`<&!>`, `<&>`, or a reduction join) runs its legs side by side, one joint cell at a time, and evaluates a named computation its legs share once per cell (formalism 10.2), so the order and count of the leg's writes have no single meaning. Force the writing map first (`|> compute`: one evaluation, stored, in order) or fold the legs separately."))
    let check (owner: string) (ownerId: IRId option) (root: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRFusion _ | IRParallel _ ->
                if leavesOf n |> List.exists writes then refuse owner ownerId
            | IRReduceCompute (comp, kernel, _) ->
                let leaves = leavesOf (resolveComp Set.empty comp)
                let joined =
                    leaves.Length >= 2
                    || (match kernel with IRTuple ks -> ks.Length >= 2 | _ -> false)
                if joined then
                    // the per-leg fold kernels of a reduction join, or the ONE
                    // scalar kernel a fused tree applies per leaf, and every leaf
                    let kernels = match kernel with IRTuple ks -> ks | k -> [ k ]
                    if kernels |> List.exists writes || leaves |> List.exists writes then refuse owner ownerId
            | _ -> ()) root
    for m in modules do
        for b in m.Bindings do check b.Name (Some b.Id) b.Value
        for f in m.Functions do check f.Name None f.Body
    // G41 (Codex finding 18): a PARALLEL kernel (`where omp` / cuda) whose body
    // writes storage outside itself through a local VIEW or ALIAS (`let t =
    // G(0); t(0) = ...`) -- the front end's BL4005 check is lexical, so it sees
    // the local name and lets it through; the cells then race. Judged with the
    // same storage-provenance analysis, refusing only what it can see (calls
    // are the front end's to judge, so an unresolved head is not assumed to
    // write here). Located at the declaration that applies the kernel.
    // CALLBACKS (Codex finding 20): a parallel kernel that calls a
    // function-valued parameter of its enclosing function is judged through
    // the ARGUMENTS every call site passes for that parameter: each must
    // resolve to a callable, and the worst of them stands in for the callback.
    // An argument that does not resolve leaves the callback unresolved, which
    // a parallel body may not have (UnknownIsWrite: proof required).
    let allFuncs = modules |> List.collect (fun m -> m.Functions)
    // ONE shared collector (Optimize.callbackTable): the least fixpoint of the
    // function values every callback parameter and alias may hold -- module
    // bindings keep their identity, indirect calls feed every target, and
    // only a genuine escape makes a callback unknown.
    let callbackArgs =
        Optimize.callbackTable resolve allFuncs (modules |> List.collect (fun m -> m.Bindings |> List.map (fun b -> (b.Id, b.Value))))
    // Every candidate is joined at each application (Codex finding 22): the
    // walker applies EACH argument a call site passes for the callback.
    let parallelFacts =
        { facts with
            Callbacks = (fun id ->
                let key =
                    match chaseAlias Set.empty (IRVar (id, IRTUnit)) with
                    | IRVar (j, _) -> j
                    | _ -> id
                match Map.tryFind key callbackArgs with
                | Some l when not l.IsEmpty -> Some l
                | _ -> None)
            UnknownIsWrite = true }
    let users =
        modules |> List.collect (fun m -> m.Bindings)
        |> List.map (fun b -> (b, collectVarRefsIR b.Value))
    for m in modules do
        for f in m.Functions do
            // PARAMETER-AWARE (Codex finding 21): a write to the kernel's own
            // parameter -- a `mut` array parameter, the caller's to grant --
            // is judged by the front end's mut-parameter policy, not here; only
            // writes to storage captured from OUTSIDE the body are a race.
            if (f.IsOmpParallel || f.IsCudaKernel) && (Optimize.callableWriteSummaryWith parallelFacts f).External then
                let span =
                    match users |> List.tryFind (fun (_, refs) -> Set.contains f.Id refs) with
                    | Some (b, _) -> spanOf b.Name (Some b.Id)
                    | None ->
                        // a kernel lifted out of a FUNCTION: locate it at that function
                        match allFuncs |> List.tryFind (fun g -> g.Id <> f.Id && Set.contains f.Id (collectVarRefsIR g.Body)) with
                        | Some g when IR.declSpanOf g.Name <> Blade.Ast.noSpan -> IR.declSpanOf g.Name
                        | _ -> IR.declSpanOf f.Name
                raise (Blade.Diagnostics.BladeDiagnosticException
                        (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints span
                            "a parallel kernel (`where omp` / cuda) writes storage captured from outside it -- through a local view or alias (a `let` bound to, or rebound to, a slice or element view of a captured array) or through a callback it calls (a function-valued parameter whose argument writes, or one that cannot be resolved): its cells run concurrently, so the store is a data race. Write through a fresh local array, pass a callback that writes nothing outside itself, or return the value and fold it."))
/// GAP G247: a kernel APPLIED inside a function whose callable captures a
/// binding of ANOTHER function's body that this function does not receive --
/// `function outer() = { let scale = 2.0; let f = lambda(x) -> x * scale;
/// function apply(scale: Float64, a) = { method_for(a) <@> f |> compute }; ..}`
/// -- had no meaning the lanes shared: the C++ lane inlined f and read
/// apply's same-named parameter (or named an undeclared `scale`), the
/// interpreter could not resolve the capture, the LLVM lane computed it. The
/// capture is not forwarded into the nested function (its lifted signature
/// carries the closure, not what the closure captured), so until that
/// forwarding exists the shape is refused here, BL7004, in every lane
/// (fail closed; the forwarding is the open part of gap G247). A capture is
/// FOREIGN to a function when some other function's body binds it (a let or
/// a parameter) and this one neither binds it nor receives it as a parameter
/// or a capture; a capture bound nowhere known is not judged.
let private refuseForeignKernelCaptures (modules: IRModule list) : unit =
    let funcs = modules |> List.collect (fun m -> m.Functions)
    // THIS program's callables, through let aliases (programWriteContext's
    // resolver; never the ambient analysis context, which a fresh process
    // leaves empty and a previous compilation leaves stale -- Codex diff review)
    let (_, _, _, _, resolve, _) = programWriteContext modules
    let owners = System.Collections.Generic.Dictionary<IRId, Set<IRId>>()
    let own (id: IRId) (fid: IRId) =
        owners.[id] <- (match owners.TryGetValue id with | true, s -> Set.add fid s | _ -> Set.singleton fid)
    for f in funcs do
        for p in f.Params do own p.VarId f.Id
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, _, _) -> own id f.Id
            | IRForRange (vid, _, _, _) -> own vid f.Id
            | _ -> ()) f.Body
    for f in funcs do
        let received =
            Set.union (f.Params |> List.map (fun p -> p.VarId) |> Set.ofList)
                      (f.Captures |> List.map (fun c -> c.Id) |> Set.ofList)
        iterIRExpr (fun n ->
            match n with
            | IRApplyCombinator info ->
                // a `reynolds(f)` wrapper is peeled first (resolveKernel's rule)
                (match resolve (fst (IR.peelReynolds info.Kernel)) with
                 | Some kf ->
                     for c in kf.Captures do
                         match owners.TryGetValue c.Id with
                         | true, s when not (s.Contains f.Id) && not (received.Contains c.Id) ->
                             let span =
                                 let sp = IR.declSpanOf f.SourceName
                                 if sp <> Blade.Ast.noSpan then sp else IR.declSpanOf f.Name
                             raise (Blade.Diagnostics.BladeDiagnosticException
                                     (Blade.Diagnostics.Codes.backendRefusal span
                                         ($"'{f.SourceName}': a kernel it applies is a closure that captures `{c.Name}`, a binding of an enclosing function which '{f.SourceName}' does not receive (a nested function receives the closure, not what the closure captured; gap G247) -- pass `{c.Name}` to '{f.SourceName}' as a parameter, or apply the kernel in the function that defines it")))
                         | _ -> ()
                 | None -> ())
            | _ -> ()) f.Body

/// The value class R11's argument judgment compares (refuseArgumentConversions):
/// a scalar's element, a non-number kind, a tuple's components, an array's
/// element, a function, a halo window -- or OPEN (not judged).
type private ValueClass =
    | VCNum of ElemType
    | VCBool
    | VCString
    | VCUnit
    | VCNamed of string
    | VCTuple of ValueClass list
    | VCArray of ValueClass
    | VCFunc
    | VCWindow
    | VCOpen

/// R11, THE NUMBERS-ONLY JUDGMENT AT EVERY INSTANCE (formalism 2.4: arithmetic
/// is defined on numbers only, and Blade converts nothing implicitly). Runs once
/// every function is concrete (after the arity, HM and shape monomorphizers) and
/// BEFORE the optimizer, which inlines calls and so erases the boundaries judged
/// here. Two judgments, over every binding value and every callable body:
///
///   * EVERY CALL (each IRApp, whatever spelling produced it: a declared
///     function, a let-bound lambda, an alias chain, a section, a block lambda,
///     a lambda applied where it is written, a function-typed parameter) hands
///     each argument to a parameter OF THE CALLEE'S DEFINITION (resolved through
///     let aliases; a function-typed parameter's own slot type otherwise). The
///     type checker unifies an argument with its parameter everywhere except
///     where it deliberately leaves a single-value callee's open parameter alone
///     (TypeCheckSupport's lambdaishHead arm: an open-argument LINK, so a numeric
///     result never changes) -- so after monomorphization a call is the one place
///     a value can meet a parameter of another class, and C++ would convert it
///     (a Bool into an Int32 parameter computes 1 + 1; a Bool into a function
///     slot dies in g++). Refused at the call's span: a non-number meeting a
///     number, a number meeting a non-number, two different non-number classes,
///     a non-function meeting a function slot -- BL3001; a LOSSY numeric
///     conversion (a float into an integer, Float64 into Float32, complex into
///     real), which g++ refused anyway -- BL3020. Also the call's RESULT: the
///     type the checker gave the call against the callee's return (an Int64
///     cell handed to a lambda whose parameter defaulted to Float64: the call
///     was typed Int64, the body returns a Float64). A numeric WIDENING (an
///     integer into a float, a narrower width into a wider one) is NOT refused:
///     base compiled and printed those values, and a numeric parameter is never
///     re-decided by its argument here.
///   * EVERY ARITHMETIC, COMPARISON, MATH AND LOGICAL NODE whose operand is not
///     of its class (IRValidate.numbersOnlyClashes, check 1d's judgment) -- a
///     generic body instantiated at a struct, a tuple or a function value --
///     refused at the operator's span (IRBinOp carries one), else the enclosing
///     declaration's.
///   * KERNEL SLOTS (a fold's, a mask predicate's, a sort key's, a map's
///     per-cell kernel): the checker unifies them with the operand's cells; the
///     judgment re-checks the CLASS of each cell against its parameter.
///
/// A literal argument is not judged for width (a literal adapts to its
/// parameter: IRMono.adaptFloatLiteralsModule narrows it later, and refuses
/// one whose value does not fit, R19 -- as the checker does for a concrete
/// parameter, TypeCheckSupport.literalArgMisfit).
let private refuseArgumentConversions (modules: IRModule list) : unit =
    let (funcs, programLets, _, _, resolve, _) = programWriteContext modules
    let structFields =
        modules
        |> List.collect (fun m -> m.Types)
        |> List.choose (fun td -> match td with IRTDStruct (n, fs) -> Some (n, fs) | _ -> None)
        |> Map.ofList
    // A named type is a CLASS of its own only when it is a struct or a sum type;
    // an alias is its target; anything else (an index or EnumIdx name) is open.
    let typeDefs = modules |> List.collect (fun m -> m.Types)
    let aliasOf (n: string) =
        typeDefs |> List.tryPick (fun td -> match td with IRTDAlias (a, ty) when a = n -> Some ty | _ -> None)
    let isNominal (n: string) =
        typeDefs |> List.exists (fun td ->
            match td with
            | IRTDStruct (a, _) | IRTDVariant (a, _) -> a = n
            | _ -> false)
    let rec cls (t: IRType) : ValueClass =
        match t with
        | IRTUnitAnnotated (inner, _) -> cls inner
        | IRTIdxTagged (_, IRefNamed tag) when tag.StartsWith "__halowin|" -> VCWindow
        | IRTIdxTagged (inner, _) -> cls inner
        | IRTScalar ETBool -> VCBool
        | IRTScalar ETString -> VCString
        | IRTScalar ETUnit -> VCUnit
        | IRTScalar et -> VCNum et
        | IRTTuple ts -> VCTuple (ts |> List.map cls)
        | IRTNamed n ->
            (match aliasOf n with
             | Some ty when ty <> t -> cls ty
             | _ -> if isNominal n then VCNamed n else VCOpen)
        | ArrayElem a -> VCArray (cls a.ElemType)
        | FuncElem _ | IRTArrow _ -> VCFunc
        | _ -> VCOpen
    // The type the monomorphizers specialized from (exprTypeIfKnown: an `abs`
    // of an integer is an integer there, where the reconstructing `typeOf`
    // answers Float64), else the reconstruction (tuples, struct fields).
    let tyOf (e: IRExpr) : IRType option =
        match exprTypeIfKnown e with
        | Some t -> Some t
        | None -> try Some (typeOf e) with _ -> None
    // The cell an array or a deferred computation yields.
    let rec cellOf (t: IRType) : IRType option =
        match stripUnits t with
        | IRTComputation inner -> cellOf inner
        | ArrayElem a -> Some a.ElemType
        | _ -> None
    // An EXPLICIT cast the user wrote (`Float64(1e100)`) is a fixed-width
    // VALUE, never a literal that adapts (G275, Codex round 4):
    // `saved(Float64(1e100) + Float64(1e100))` into a Float32 parameter was
    // handed over unjudged and narrowed to an infinity. Judged by PROVENANCE,
    // not by location: the parser marks the span object of every cast
    // application it builds (Ast.UserCasts), and the cast node carries that
    // span. Every other cast around a literal still adapts -- the IR's own
    // (an Int32 literal's width, a static's, the degenerate-literal marker)
    // and the AST casts an elaborator or the AD pipeline synthesizes at a
    // DECLARATION's span (`PplElaborate.asF64 (fLit 0.0)`, a fold seed over
    // Float32 cells: a located cast the user never wrote).
    let userCast (_et: ElemType) (loc: SrcLoc) (_x: IRExpr) =
        not (isNull loc) && Blade.Ast.UserCasts.isUserCast loc.Span
    let rec isLiteral (e: IRExpr) =
        match e with
        | IRLit _ -> true
        | IRUnaryOp (IRNeg, x) -> isLiteral x
        | IRUnaryOp (IRCast (et, loc), x) -> not (userCast et loc x) && isLiteral x
        | IRBinOp (_, (IRAdd | IRSub | IRMul | IRDiv | IRMod | IRCaret), l, r, _) -> isLiteral l && isLiteral r
        | IRComplex (re, im) -> isLiteral re && isLiteral im
        // a synthesized seed (resolved to the instance's own identity later)
        | IRUnseededSeed _ | IRZero _ -> true
        | _ -> false
    // A literal tree with no USER cast in it: an explicit cast
    // (`Int64(4294967296)`) is a fixed-width VALUE, which the narrowing proof
    // judges (G275); the compiler's own width casts around a literal (`1` at
    // Int32 inside `(46341 ^ (1 + 1)) / 2`) keep the tree a literal.
    let rec isPlainLiteral (e: IRExpr) =
        match e with
        | IRLit _ -> true
        | IRUnaryOp (IRNeg, x) -> isPlainLiteral x
        | IRUnaryOp (IRCast (et, loc), x) -> not (userCast et loc x) && isPlainLiteral x
        | IRBinOp (_, (IRAdd | IRSub | IRMul | IRDiv | IRMod | IRCaret), l, r, _) -> isPlainLiteral l && isPlainLiteral r
        | _ -> false
    let isFloat = function ETFloat32 | ETFloat64 -> true | _ -> false
    let isInt = function ETInt32 | ETInt64 -> true | _ -> false
    let isComplex = function ETComplex64 | ETComplex128 -> true | _ -> false
    // An INTEGER `let static` name (a module binding the static desugar marks
    // IsConst) adapts its width like the literal it folds to (formalism 2.4),
    // at any depth of a tuple argument; a FLOAT static is a constant of its
    // own width and is judged like any value.
    let constIntIds =
        modules
        |> List.collect (fun m -> m.Bindings)
        |> List.filter (fun b ->
            b.IsConst && not b.IsMutable
            && (match stripUnits b.Type with
                | IRTScalar (ETInt32 | ETInt64) -> true
                | IRTIdxTagged (inner, _) -> (match stripUnits inner with IRTScalar (ETInt32 | ETInt64) -> true | _ -> false)
                | _ -> false))
        |> List.map (fun b -> b.Id)
        |> Set.ofList
    let lossyOnly (src: ElemType) (dst: ElemType) =
        (isFloat src && isInt dst)
        || (src = ETFloat64 && dst = ETFloat32)
        || (isComplex src && not (isComplex dst))
        || (src = ETComplex128 && dst = ETComplex64)
    // G275: a numeric WIDENING (Float32 into Float64, Int32 into Int64, an
    // integer into a float, Complex64 into Complex128, a real into a complex)
    // converts as surely as a lossy one (formalism 2.4: a fixed non-literal
    // argument meets its parameter without conversion). Refused here too, on
    // the concrete instance types: what the checker could not see when it
    // typed the call -- a lambda value's parameter decided by a later call or
    // by its default, a generic body's call at a narrower instance, a fold
    // kernel's result fed back into a wider accumulator, a callable chosen by
    // an expression in a kernel slot. Int64 into Int32 is the narrowing proof
    // below (calls only), not this judgment.
    let widening (src: ElemType) (dst: ElemType) =
        src <> dst && (isInt src || isFloat src || isComplex src)
        && IR.promoteElemType src dst = Some dst
    let lossy (src: ElemType) (dst: ElemType) = lossyOnly src dst || widening src dst
    let elemName (e: ElemType) = ppIRType (IRTScalar e)
    let rec className (c: ValueClass) =
        match c with
        | VCNum e -> elemName e
        | VCBool -> "Bool"
        | VCString -> "String"
        | VCUnit -> "unit"
        | VCNamed n -> Blade.Types.TypeOrigins.sourceName n
        | VCTuple cs -> "(" + (cs |> List.map className |> String.concat ", ") + ")"
        | VCArray c -> $"an array of {className c}"
        | VCFunc -> "a function"
        | VCWindow -> "a halo window"
        | VCOpen -> "an open type"
    let article (c: ValueClass) =
        match c with
        | VCArray _ | VCFunc | VCWindow | VCOpen -> className c
        | VCTuple _ -> "a tuple " + className c
        | VCNum ETInt32 | VCNum ETInt64 -> "an " + className c + " value"
        | _ -> "a " + className c + " value"
    /// The first disagreement of an argument class with its slot's class:
    /// Some (numericLossy, argClass, slotClass), or None when they agree (or
    /// either is open, or the shapes are ones this judgment does not compare:
    /// an array and a non-array -- an array is a function of its indices).
    /// `numeric` judges numeric widths (a lossy conversion only).
    let rec clash (numeric: bool) (a: ValueClass) (p: ValueClass) : (bool * ValueClass * ValueClass) option =
        match a, p with
        | VCOpen, _ | _, VCOpen -> None
        | VCNum x, VCNum y -> if numeric && lossy x y then Some (true, a, p) else None
        // a window IS its centre ordinal (an Int64) everywhere but a function slot
        | VCWindow, VCNum _ | VCNum _, VCWindow -> None
        | VCArray x, VCArray y -> clash numeric x y
        // An array handed to a NUMBER parameter: only its element is judged
        // (a numeric array may be lifted over the parameter); an array of
        // non-numbers meets no number either way.
        | VCArray x, VCNum _ ->
            let rec elemOf (c: ValueClass) = match c with VCArray e -> elemOf e | c -> c
            (match elemOf x with
             | VCNum _ | VCOpen -> None
             | _ -> Some (false, a, p))
        | VCArray _, _ | _, VCArray _ -> None
        | VCTuple xs, VCTuple ys ->
            if xs.Length <> ys.Length then None
            else List.zip xs ys |> List.tryPick (fun (x, y) -> clash numeric x y)
        | VCFunc, VCFunc -> None
        | x, y when x = y -> None
        | _ -> Some (false, a, p)
    // G275: an ARGUMENT judged component by component (a tuple argument's
    // literals and integer statics adapt, its fixed components do not): a
    // literal is judged by class only (it adapts to its parameter: IRMono), an
    // integer let static name adapts its WIDTH but never its class, anything
    // else is a fixed value.
    // `proven` is the call's narrowing proof (fitsInt32) for an Int64
    // COMPONENT of a tuple handed to an Int32 one (a whole Int64 argument is the
    // call arm's own check); `literalClass` judges a literal's CLASS (an integer
    // literal is no float, a real literal no complex) except at an
    // elaborator-synthesized (`__`) callee, whose calls are its contract.
    let classOf (e: ElemType) = if isInt e then 0 elif isFloat e then 1 elif isComplex e then 2 else -1
    let rec clashArg (proven: IRExpr -> bool) (literalClass: bool) (arg: IRExpr) (a: ValueClass) (p: ValueClass)
                     : (bool * ValueClass * ValueClass) option =
        // (an Int64 component reaching an Int32 one through a NAMED or computed
        // tuple has no per-component expression to prove: refused)
        let rec narrowsInTuple (x: ValueClass) (y: ValueClass) =
            match x, y with
            | VCTuple xs, VCTuple ys when xs.Length = ys.Length ->
                List.exists2 (fun u v -> (u = VCNum ETInt64 && v = VCNum ETInt32) || narrowsInTuple u v) xs ys
            | _ -> false
        match arg, a, p with
        | IRTuple es, VCTuple xs, VCTuple ys when es.Length = xs.Length && xs.Length = ys.Length ->
            List.zip3 es xs ys |> List.tryPick (fun (e, x, y) ->
                match x, y with
                | VCNum ETInt64, VCNum ETInt32
                        when not (isPlainLiteral e)
                             && not (match e with IRVar (id, _) -> constIntIds.Contains id | _ -> false)
                             && not (proven e) -> Some (true, x, y)
                | _ -> clashArg proven literalClass e x y)
        | _, VCTuple _, VCTuple _ when narrowsInTuple a p -> Some (true, a, p)
        | _ when isLiteral arg ->
            (match a, p with
             | VCNum x, VCNum y when literalClass && classOf x >= 0 && classOf y >= 0 && classOf x <> classOf y -> Some (true, a, p)
             | _ -> clash false a p)
        | IRVar (id, _), VCNum x, VCNum y when constIntIds.Contains id ->
            if isInt x && not (isInt y) then Some (true, a, p) else None
        | _ -> clash true a p
    // The let values of a body, so a local alias chain resolves to its callable.
    let calleeOf (letValues: System.Collections.Generic.Dictionary<IRId, IRExpr>) (f: IRExpr) : IRCallable option =
        let rec go (fuel: int) (e: IRExpr) =
            match e with
            | IRVar (id, _) when fuel > 0 ->
                (match Map.tryFind id funcs with
                 | Some c -> Some c
                 | None ->
                     match letValues.TryGetValue id with
                     | true, (IRVar _ as v) -> go (fuel - 1) v
                     | _ -> resolve e)
            | _ -> None
        go 64 f
    // An operator section's lifted callable is named after its operator:
    // `(+) in <scope>` (IRCallable.SourceName).
    // (and is binary: a synthesized one-parameter kernel can carry such a name)
    let isSection (c: IRCallable) =
        c.Params.Length = 2 && System.Text.RegularExpressions.Regex.IsMatch(c.SourceName, @"^\([^\w\s()]+\)")
    let calleeName (c: IRCallable option) (f: IRExpr) =
        match c with
        | Some c when c.SourceName.StartsWith "lambda" || isSection c -> $"called {c.SourceName}"
        | Some c -> $"called '{c.SourceName}'"
        | None -> (match f with IRVar _ -> "called function value" | _ -> "called value")
    // The first located node of an expression (a call, an operator, a cast).
    let firstLoc (e: IRExpr) : SrcLoc =
        let mutable found = SrcLoc.Nowhere
        iterIRExpr (fun n ->
            if found.Span.StartLine = 0 then
                match n with
                | IRAppAt (_, _, _, l) | IRBinOp (_, _, _, _, l) | IRUnaryOp (IRCast (_, l), _)
                    when not (isNull l) && l.Span.StartLine > 0 -> found <- l
                | _ -> ()) e
        found
    // THE NARROWING PROOF. An Int64 argument handed to an Int32 parameter is
    // refused unless it is STATICALLY PROVEN to fit Int32 (the user's decision
    // of 2026-11-16): every exemption is a positive proof, anything else is
    // judged. Proven (`fitsInt32`): an integer literal; the variable of a
    // loop whose bounds are integer literals (IRForRange, half-open [lo, hi):
    // a `let rec` step ordinal, which the desugar bounds by the recursion's
    // static extent -- the surface has no other loop); a `let` alias of a
    // proven value (bound once in its body, or a module binding); `x + c`,
    // `x - c`, `c + x`, `c - x` of a proven x and an integer literal c, by
    // exact interval arithmetic. A variable any assignment in the program
    // targets (`v = e`, at any depth: a nested callable may write a captured
    // binding) is never proven. Nothing else: no product, no parameter, no
    // index tag (arithmetic on a tagged position keeps the tag).
    let mutable allBodies : IRExpr list = []
    let assignedIds =
        lazy (
            let s = System.Collections.Generic.HashSet<IRId>()
            let rec rootOf (t: IRExpr) =
                match t with
                | IRVar (id, _) -> s.Add id |> ignore
                | IRIndex (a, _, _) -> rootOf a
                | IRFieldAccess (o, _) -> rootOf o
                // any other target shape: every name in it (over-approximation)
                | other -> iterIRExpr (fun n -> match n with IRVar (id, _) -> s.Add id |> ignore | _ -> ()) other
            for b in allBodies do
                iterIRExpr (fun n -> match n with IRAssign (t, _) -> rootOf t | _ -> ()) b
            s)
    let staticInt (e: IRExpr) : bigint option =
        match e with
        | IRLit (IRLitInt v) -> Some (bigint v)
        | NegIntLit v -> Some (-(bigint v))
        | _ -> None
    // A body's loop bounds and let values, each id bound exactly ONCE in it
    // (a binder seen twice -- a cloned body -- proves nothing).
    let bodyFacts (body: IRExpr) =
        let bounds = System.Collections.Generic.Dictionary<IRId, (bigint * bigint) option>()
        let lets = System.Collections.Generic.Dictionary<IRId, IRExpr option>()
        iterIRExpr (fun n ->
            match n with
            | IRForRange (vid, lo, hi, _) ->
                let r =
                    match staticInt lo, staticInt hi with
                    | Some l, Some h when l < h -> Some (l, h - 1I)
                    | _ -> None
                bounds.[vid] <- (if bounds.ContainsKey vid then None else r)
            | IRLet (id, v, _) -> lets.[id] <- (if lets.ContainsKey id then None else Some v)
            | _ -> ()) body
        (bounds, lets)
    let int32Lo, int32Hi = bigint System.Int32.MinValue, bigint System.Int32.MaxValue
    // module bindings (unique ids; a body's own lets are its facts)
    let moduleLets = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    for m in modules do
        for b in m.Bindings do moduleLets.[b.Id] <- b.Value
    let fitsInt32 (facts: Lazy<System.Collections.Generic.Dictionary<IRId, (bigint * bigint) option>
                                * System.Collections.Generic.Dictionary<IRId, IRExpr option>>) (arg: IRExpr) : bool =
        let bounds, lets = facts.Value
        let rec rangeOf (fuel: int) (e: IRExpr) : (bigint * bigint) option =
            if fuel <= 0 then None
            else
            match e with
            | IRLit (IRLitInt v) -> Some (bigint v, bigint v)
            | IRVar (id, _) when not (assignedIds.Value.Contains id) ->
                (match bounds.TryGetValue id with
                 | true, r -> r
                 | _ ->
                     match lets.TryGetValue id with
                     | true, Some v -> rangeOf (fuel - 1) v
                     | true, None -> None
                     | _ ->
                         match moduleLets.TryGetValue id with
                         | true, v -> rangeOf (fuel - 1) v
                         | _ -> None)
            | IRBinOp (_, (IRAdd | IRSub as op), l, r, _) ->
                (match staticInt l, staticInt r with
                 | _, Some c ->
                     rangeOf (fuel - 1) l |> Option.map (fun (a, b) -> if op = IRAdd then (a + c, b + c) else (a - c, b - c))
                 | Some c, None ->
                     rangeOf (fuel - 1) r |> Option.map (fun (a, b) -> if op = IRAdd then (c + a, c + b) else (c - b, c - a))
                 | None, None -> None)
            | _ -> None
        match rangeOf 64 arg with
        | Some (a, b) -> a >= int32Lo && b <= int32Hi
        | None -> false
    let check (phase: int) (declName: string) (sourceName: string) (instance: string) (body: IRExpr) =
        // A node without a location is reported at its declaration, else (a
        // lifted lambda the span table does not name) at the first located
        // node of the body it is in -- never without a location.
        // (`lambda in r` / `lambda in lambda in r`: a lambda's source name
        // names the binding it is written in)
        let rec ownerSpan (n: string) =
            let s = IR.declSpanOf n
            if s.StartLine > 0 then s
            elif n.StartsWith "lambda in " then ownerSpan (n.Substring "lambda in ".Length)
            else Blade.Ast.noSpan
        let fallbackSpan () =
            let s = IR.declSpanOf declName
            if s.StartLine > 0 then s
            else
            let s = IR.declSpanOf sourceName
            if s.StartLine > 0 then s
            else
            let s = (firstLoc body).Span
            if s.StartLine > 0 then s
            else ownerSpan sourceName
        let spanOf (loc: SrcLoc) =
            if not (isNull loc) && loc.Span.StartLine > 0 then loc.Span else fallbackSpan ()
        let refuse (code: string) (loc: SrcLoc) (msg: string) =
            raise (Blade.Diagnostics.BladeDiagnosticException
                    (Blade.Diagnostics.mkError code (Blade.Diagnostics.Codes.phaseOfCode code) (spanOf loc) (msg + instance)))
        let letValues = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        iterIRExpr (fun n -> match n with IRLet (id, v, _) -> letValues.[id] <- v | _ -> ()) body
        let facts = lazy (bodyFacts body)
        let argMessage (i: int) (pname: string) (what: string) (lossyNum: bool) (a: ValueClass) (p: ValueClass) =
            let castHint =
                match a with
                | VCBool -> " -- convert it explicitly with a numeric cast (`Int64(b)` or `Float64(b)` is 1 or 0), or annotate the parameter"
                | _ -> " -- pass a value of the parameter's type, or annotate the parameter with the argument's type"
            if lossyNum then
                let loss =
                    match a, p with
                    | VCNum x, VCNum y when widening x y -> $"widening it to {elemName y}"
                    | VCNum ETInt64, VCNum ETInt32 ->
                        "wrapping it into the narrower integer when it does not fit (an Int64 value -- a tuple component included -- meets an Int32 only where it is statically proven to fit: an integer literal, a `let rec` step ordinal, through `let` aliases and `+` / `-` of an integer literal)"
                    | VCNum (ETInt32 | ETInt64), VCNum (ETInt32 | ETInt64) -> "wrapping it into the narrower integer"
                    | _ -> "losing precision"
                // G275 (the lead's ruling): a lambda VALUE (aliased, wrapped in
                // a block, passed) is monomorphic at its binding -- its open
                // parameter is decided by a DIRECT call or its default, never
                // by a call through the value -- so the remedy names both ways
                // to give the parameter the argument's width.
                let remedy =
                    match a, p with
                    | VCNum x, VCNum y when widening x y ->
                        $" -- annotate the parameter with the argument's type (`{pname}: {elemName x}`), or convert the argument explicitly (`{elemName y}(..)`); a lambda kept as a value (an alias, a block, a tuple member, an argument) is monomorphic at its binding, so call it directly by the name it is bound to if that call should decide its parameter"
                    | VCNum ETInt64, VCNum ETInt32 -> $" -- convert it explicitly at the call (`Int32(..)`), or declare the parameter Int64"
                    | _ -> " -- convert it explicitly at the call, or annotate the parameter"
                $"argument {i + 1} of this call is {article a}, but parameter `{pname}` of the {what} is {className p} (a parameter the callee's body left open is decided by its first use or its default, never by a later argument): the call would convert it, {loss}, and Blade converts nothing implicitly (formalism 2.4){remedy}"
            else
                match a, p with
                | VCWindow, VCFunc ->
                    Blade.TypeEnv.formatTypeError (Blade.Unify.HaloWindowEscapes ())
                | _, VCFunc ->
                    $"argument {i + 1} of this call is {article a}, but parameter `{pname}` of the {what} is a function its body applies: {article a} is not a function, and a call converts nothing"
                | (VCBool | VCString | VCUnit | VCNamed _ | VCTuple _ | VCFunc | VCWindow), VCNum _ ->
                    $"argument {i + 1} of this call is {article a}, but parameter `{pname}` of the {what} is {className p} (a parameter the callee's body left open is decided by its first use or its default, never by a later argument): {className a} is not a number, and Blade converts nothing implicitly{castHint}"
                | _ ->
                    $"argument {i + 1} of this call is {article a}, but parameter `{pname}` of the {what} is {className p} (a parameter the callee's body left open is decided by its first use or its default, never by a later argument): a call converts nothing{castHint}"
        let codeOf (lossyNum: bool) = if lossyNum then "BL3020" else "BL3001"
        // A kernel's signature: its DEFINITION's (resolved through aliases),
        // else the kernel expression's own function type (a callable returned
        // by a call, projected from a tuple, chosen by an `if`). An operator
        // SECTION is lifted over Float64 parameters whatever it folds: its
        // operands are the checker's fold / operator judgment, not this one.
        let rec kernelSig (k: IRExpr) : (string list * IRType list * IRType * IRExpr option * string) option =
            match calleeOf letValues k with
            | Some c when isSection c -> None
            | Some c -> Some (c.Params |> List.map (fun p -> p.Name), c.Params |> List.map (fun p -> p.Type), c.RetType, Some c.Body, calleeName (Some c) k)
            | None ->
                match k with
                // a COMPOSED kernel ( <$> (L <@> g) is L <@> (g then f)):
                // its parameters are the first stage's, its result the last's
                | IRCompose (first, second) ->
                    (match kernelSig first, kernelSig second with
                     | Some (n, ps, _, b, w), Some (_, _, r, _, _) -> Some (n, ps, r, b, w)
                     | Some (n, ps, _, b, w), None -> Some (n, ps, IRTInfer 0, b, w)
                     | _ -> None)
                | _ ->
                match tyOf k |> Option.map stripUnits with
                | Some (FuncElem (ps, r)) -> Some (ps |> List.mapi (fun i _ -> $"#{i + 1}"), ps, r, None, calleeName None k)
                // (an arrow of value slots: a kernel the checker synthesized)
                | Some (IRTArrow (slots, r, _)) when slots |> List.forall (function SVal _ -> true | _ -> false) ->
                    let ps = slots |> List.choose (function SVal t -> Some t | _ -> None)
                    Some (ps |> List.mapi (fun i _ -> $"#{i + 1}"), ps, r, None, calleeName None k)
                | _ -> None
        // One value handed to parameter i of a kernel.
        let judgeSlot (k: IRExpr) (i: int) (given: ValueClass) (numeric: bool) (role: string) =
            match kernelSig k with
            | Some (names, ps, _, _, what) when i < ps.Length ->
                (match clash numeric given (cls ps.[i]) with
                 | Some (lossyNum, a, p) -> refuse (codeOf lossyNum) SrcLoc.Nowhere (argMessage i names.[i] (what + role) lossyNum a p)
                 | None ->
                     // G276: a kernel SLOT has no call-site narrowing proof
                     // (fitsInt32 is a call's): an Int64 cell handed to an Int32
                     // parameter wraps, whatever expression chose the kernel.
                     let rec narrows (g: ValueClass) (q: ValueClass) =
                         match g, q with
                         | VCNum ETInt64, VCNum ETInt32 -> true
                         | VCArray x, VCArray y -> narrows x y
                         | VCArray x, (VCNum _ as y) -> narrows x y
                         | VCTuple xs, VCTuple ys when xs.Length = ys.Length -> List.exists2 narrows xs ys
                         | _ -> false
                     if numeric && narrows given (cls ps.[i]) then
                         refuse "BL3020" SrcLoc.Nowhere
                             ($"each value handed to parameter `{names.[i]}` of the {what}{role} is an Int64, and the parameter is an Int32: "
                              + "a kernel slot would wrap every value into the narrower integer, and Blade converts nothing implicitly (formalism 2.4) -- "
                              + "declare the parameter Int64, or convert the array first (`Int32(xs)`)"))
            | _ -> ()
        // A fold: each cell into the kernel's second parameter, the seed (or,
        // unseeded, the first cell) into its accumulator, and the kernel's
        // result into its next accumulator.
        let judgeFold (k: IRExpr) (cell: IRType) (init: IRExpr option) =
            // A COMPARISON section folded over NUMBERS returns a Bool that the
            // fold feeds back into its numeric accumulator (`reduce(xs, (<), 0)`
            // computes `acc < x` with acc a Bool after the first step). Sections
            // are otherwise the checker's (kernelSig skips them); a comparison
            // fold over Bools (`reduce(bs, (==))`) stays legal.
            (match calleeOf letValues k with
             | Some c when isSection c ->
                 let op = let i = c.SourceName.IndexOf ')' in c.SourceName.Substring(1, i - 1)
                 match op, cls cell with
                 | ("<" | "<=" | ">" | ">=" | "==" | "!="), VCNum _ ->
                     refuse "BL3001" SrcLoc.Nowhere
                         $"arithmetic is defined on numbers only: the fold kernel ({op}) returns a Bool, which the fold feeds back into its accumulator as the next left operand, beside a number -- Blade converts nothing implicitly; fold with a kernel that returns the accumulator's type"
                 | _ -> ()
             | _ -> ())
            match kernelSig k with
            | Some (names, ps, ret, kbody, what) when ps.Length = 2 ->
                judgeSlot k 1 (cls cell) true " (a fold kernel)"
                (match init with
                 | Some i ->
                     (match tyOf i with
                      | Some it -> judgeSlot k 0 (cls it) (not (isLiteral i)) " (a fold kernel, its seed)"
                      | None -> ())
                 | None -> judgeSlot k 0 (cls cell) true " (a fold kernel, its first cell as the accumulator)")
                match clash true (cls ret) (cls ps.[0]) with
                | Some (lossyNum, a, p) ->
                    // at the kernel's own body (its result is the defect)
                    let loc = match kbody with Some b -> firstLoc b | None -> SrcLoc.Nowhere
                    refuse (codeOf lossyNum) loc
                        $"the {what} (a fold kernel) returns {article a}, but its accumulator parameter `{names.[0]}` is {className p}: each step's result would be converted into the next, and Blade converts nothing implicitly (formalism 2.4) -- annotate the kernel's parameters"
                | None ->
                    // G275 (Codex round 4): the FEEDBACK edge has no narrowing
                    // proof either -- an Int64 result fed into an Int32
                    // accumulator wraps (a tuple state component included).
                    let rec narrows (g: ValueClass) (q: ValueClass) =
                        match g, q with
                        | VCNum ETInt64, VCNum ETInt32 -> true
                        | VCTuple xs, VCTuple ys when xs.Length = ys.Length -> List.exists2 narrows xs ys
                        | _ -> false
                    if narrows (cls ret) (cls ps.[0]) then
                        let loc = match kbody with Some b -> firstLoc b | None -> SrcLoc.Nowhere
                        refuse "BL3020" loc
                            ($"the {what} (a fold kernel) returns an Int64 value, but its accumulator parameter `{names.[0]}` is an Int32: each step's result would be wrapped into the next, "
                             + "and Blade converts nothing implicitly (formalism 2.4) -- declare the accumulator Int64, or convert the result explicitly (`Int32(..)`)")
            | _ -> ()
        // A DEFERRED binding's value (an alias chain of deferred stages); a
        // materialized one (`|> compute`) is a stored array: the emitter
        // resolves only its deferred computations, so the walk stops there.
        let deferredValue (id: IRId) : IRExpr option =
            let v =
                match letValues.TryGetValue id with
                | true, v -> Some v
                | _ -> match programLets.TryGetValue id with | true, v -> Some v | _ -> None
            match v with
            | Some (IRCompute _) -> None
            | v -> v
        // The kernel an `@>>` right stage contributes: the emitter's
        // extractInlinableKernel (a map's kernel, through named deferred stages;
        // a functor-wrapped stage's inner kernel, composed BEFORE the wrapper,
        // so the value goes to that kernel first).
        let rec stageKernel (fuel: int) (e: IRExpr) =
            match e with
            | IRApplyCombinator info -> Some info.Kernel
            // (a wrapped stage contributes its WHOLE composition, the inner
            // kernel first and the wrapper after it: extractInlinableKernel's
            // IRCompose(k, f))
            | IRFunctorMap (f, inner) -> stageKernel fuel inner |> Option.map (fun k -> IRCompose (k, f))
            | IRVar (id, _) when fuel > 0 -> deferredValue id |> Option.bind (stageKernel (fuel - 1))
            | _ -> None
        // (R20, review round 3: a `<$>` / `@>>` stage over a REYNOLDS map
        // applies to the symmetrized VALUE, after the permutation sum -- the
        // emitters' IR.composeFunctorWrappers -- so its result is never summed
        // and needs no numbers-only judgment of its own; only the reynolds
        // KERNEL's values are summed (the IRReynolds arm below). Every boundary
        // between stages stays a judged argument binding: the IRFunctorMap and
        // IRComposeMeth arms.)
        iterIRExpr (fun n ->
            // the numbers-only operand judgment (check 1d), at the node's span:
            // the LAST sweep, so a call that hands a parameter the wrong value
            // is named first (the callee's body is typed at that parameter)
            if phase = 2 then
                match numbersOnlyClashes n with
                | msg :: _ ->
                    let m = System.Text.RegularExpressions.Regex.Match(msg, @"^(BL\d{4}): (.*)$", System.Text.RegularExpressions.RegexOptions.Singleline)
                    let loc = match n with IRBinOp (_, _, _, _, l) -> l | _ -> SrcLoc.Nowhere
                    if m.Success then refuse m.Groups.[1].Value loc m.Groups.[2].Value
                | [] -> ()
            else
            match n with
            | IRAppAt (f, args, retTy, loc) when phase = 0 ->
                let callee = calleeOf letValues f
                let what = calleeName callee f
                let slots : (string * IRType) list option =
                    match callee with
                    | Some c -> Some (c.Params |> List.map (fun p -> (p.Name, p.Type)))
                    | None ->
                        match tyOf f |> Option.map stripUnits with
                        | Some (FuncElem (ps, _)) -> Some (ps |> List.mapi (fun i p -> ($"#{i + 1}", p)))
                        | _ -> None
                // An operator SECTION bound as a value (`let f = (+)`) is lifted
                // over Float64 parameters whatever it is applied to: its
                // arguments are judged against the operator itself.
                let sectionOp =
                    match callee with
                    | Some c when isSection c ->
                        let i = c.SourceName.IndexOf ')'
                        if i > 1 then Some (c.SourceName.Substring(1, i - 1)) else None
                    | _ -> None
                match sectionOp with
                | Some op ->
                    let rec scalarOf (c: ValueClass) = match c with VCArray e -> scalarOf e | c -> c
                    args |> List.iteri (fun i arg ->
                        match tyOf arg |> Option.map (cls >> scalarOf) with
                        | Some (VCOpen | VCNum _) | None -> ()
                        | Some VCString when op = "+" -> ()
                        | Some VCBool when op = "&&" || op = "||" -> ()
                        | Some a ->
                            match op with
                            | "+" | "-" | "*" | "/" | "%" | "^" ->
                                let castHint =
                                    match a with
                                    | VCBool -> " -- convert it explicitly with a numeric cast (`Int64(b)` or `Float64(b)` is 1 or 0)"
                                    | _ -> " -- pass a number"
                                refuse "BL3001" loc $"arithmetic is defined on numbers only: the section `({op})` is applied to {article a} (argument {i + 1}), and Blade converts nothing implicitly{castHint}"
                            | "&&" | "||" ->
                                refuse "BL3001" loc $"a logical operator takes Bools only: the section `({op})` is applied to {article a} (argument {i + 1}), and Blade converts nothing implicitly"
                            | _ -> ())
                | None ->
                    match slots with
                    | Some ps ->
                        List.zip (List.truncate ps.Length args) (List.truncate args.Length ps)
                        |> List.iteri (fun i (arg, (pname, pTy)) ->
                            match tyOf arg with
                            | Some aTy ->
                                match clashArg (fun e -> fitsInt32 facts e) (match callee with Some c -> not (c.SourceName.StartsWith "__") | None -> true) arg (cls aTy) (cls pTy) with
                                | Some (lossyNum, a, p) -> refuse (codeOf lossyNum) loc (argMessage i pname what lossyNum a p)
                                // INTEGER NARROWING at a call (an Int64 argument into an
                                // Int32 parameter, declared or decided by an earlier call
                                // or a generic): the call wrapped it silently. Refused
                                // unless the argument is proven to fit (fitsInt32). Judged
                                // at calls only: a kernel slot's parameter types may not be
                                // the ones its emitted body computes in (a fold kernel
                                // inlined over its cells).
                                | None when not (isPlainLiteral arg)
                                            && (let rec scalarOf (c: ValueClass) = match c with VCArray e -> scalarOf e | c -> c
                                                scalarOf (cls aTy) = VCNum ETInt64 && scalarOf (cls pTy) = VCNum ETInt32)
                                            && not (cls aTy = VCNum ETInt64 && fitsInt32 facts arg) ->
                                    refuse "BL3020" loc
                                        ($"argument {i + 1} of this call is {article (cls aTy)}, but parameter `{pname}` of the {what} is {className (cls pTy)} (a parameter the callee's body left open is decided by its first use or its default, never by a later argument): "
                                         + "the call would convert it, wrapping it into the narrower integer when it does not fit, and Blade converts nothing implicitly (formalism 2.4); "
                                         + "an Int64 argument is passed to an Int32 parameter only where it is statically proven to fit (an integer literal, or a loop ordinal bounded by a static extent -- a `let rec` step -- "
                                         + "through `let` aliases and `+` / `-` of an integer literal) -- convert it at the call (`Int32(x)`), or declare the parameter Int64")
                                | None ->
                                    // A FUNCTION ARGUMENT: the slot's parameter types are
                                    // what the callee will hand the function's own
                                    // definition (resolved through aliases), and its
                                    // return what the callee expects back.
                                    match stripUnits pTy, calleeOf letValues arg with
                                    | FuncElem (sps, sret), Some g when sps.Length = g.Params.Length && not (isSection g) ->
                                        List.zip sps g.Params
                                        |> List.iteri (fun j (sp, gp) ->
                                            match clash true (cls sp) (cls gp.Type) with
                                            | Some (lossyNum, a, p) ->
                                                let gName = (calleeName (Some g) arg).Replace("called ", "")
                                                refuse (codeOf lossyNum) loc
                                                    ($"argument {i + 1} of this call is the function {gName}, and the {what} will call it with {article a} as argument {j + 1}, but its parameter `{gp.Name}` is {className p} (a parameter its body left open is decided by its first use or its default): "
                                                     + "Blade converts nothing implicitly -- annotate the function's parameter with the type the callee passes")
                                            | None -> ())
                                        match clash true (cls g.RetType) (cls sret) with
                                        | Some (lossyNum, r, t) ->
                                            refuse (codeOf lossyNum) loc
                                                $"argument {i + 1} of this call is a function returning {article r}, but the {what} expects one returning {className t}: Blade converts nothing implicitly"
                                        | None -> ()
                                    | _ -> ()
                            | None -> ())
                        // the call's result, against the callee's own return
                        match callee with
                        | Some c when args.Length = ps.Length ->
                            (match clash true (cls c.RetType) (cls retTy) with
                             | Some (lossyNum, r, t) ->
                                 refuse (codeOf lossyNum) loc
                                     ($"this call is typed {className t}, but the {what} returns {article r} (its body was typed before this call, at its first use or its parameters' defaults): "
                                      + "the result would be converted, and Blade converts nothing implicitly (formalism 2.4) -- annotate the callee's parameters, or convert explicitly")
                             | None -> ())
                        | _ -> ()
                    | None -> ()
            | _ when phase = 0 -> ()
            // KERNEL SLOTS (the SECOND sweep): every node that applies a callable
            // without an IRAppAt hands a value to a parameter of the kernel's
            // signature (kernelSig: its definition, else its own function type).
            | IRReduce (arr, k, init) ->
                (match tyOf arr |> Option.bind cellOf with
                 | Some cell -> judgeFold k cell init
                 | None -> ())
            // The FUSED terminal: a fold over a deferred map (or a fusion tree of
            // maps). Each leaf's map is the IRApplyCombinator child the map arm
            // judges; its cells are folded by the leaf's own kernel and seed (a
            // reduction join: one tuple entry per leaf), or, for ONE leaf, by the
            // shared kernel. A shared kernel over several leaves folds the TUPLE
            // of their values, which the checker types as one tuple state.
            | IRReduceCompute (comp, kernel, init) ->
                let rec leaves (e: IRExpr) =
                    match e with
                    | IRFusion (l, r) -> leaves l @ leaves r
                    | IRCompute inner -> leaves inner
                    | e -> [ e ]
                let ls = leaves comp
                let leafCell (e: IRExpr) =
                    match e with
                    | IRApplyCombinator info -> cellOf info.OutputType
                    | _ -> tyOf e |> Option.bind cellOf
                // (every leaf folds into its OWN scalar accumulator, a shared
                // kernel and seed included: CodeGenBinding's one accumulator per
                // leaf, cc = kernel(acc, cell))
                let pairs =
                    let seeds =
                        match init with
                        | IRTuple is when is.Length = ls.Length -> is |> List.map Some
                        | i -> ls |> List.map (fun _ -> Some i)
                    match kernel with
                    | IRTuple ks when ks.Length = ls.Length -> List.zip3 ls ks seeds
                    | IRTuple _ -> []
                    | k -> List.zip3 ls (ls |> List.map (fun _ -> k)) seeds
                for (leaf, k, i) in pairs do
                    match leafCell leaf with
                    | Some cell -> judgeFold k cell i
                    | None -> ()
            // `wrapper <$> comp`: the wrapper receives each cell comp yields.
            | IRFunctorMap (wrapper, comp) ->
                (match tyOf comp |> Option.bind cellOf with
                 | Some cell -> judgeSlot wrapper 0 (cls cell) true " (a `<$>` wrapper, given each cell)"
                 | None -> ())
            // `left @>> right`: right's kernel receives each value left yields.
            | IRComposeMeth (left, right) ->
                (match stageKernel 1000000 right, tyOf left |> Option.bind cellOf with
                 | Some k, Some cell -> judgeSlot k 0 (cls cell) true " (an `@>>` stage, given each value of the stage before it)"
                 | _ -> ())
            // `reynolds(k)`: the kernel's values over the permutations of its
            // arguments are SUMMED (with multiplicities, a sign for anticomm), so
            // its result must be a number whatever its parameters are.
            | IRReynolds (k, _) ->
                // (only a kernel of two or more parameters is summed over permutations;
                // a unary reynolds is the kernel itself: CodeGenLoopNest / Interp)
                (match kernelSig k with
                 | Some (_, ps, ret, kbody, what) when ps.Length >= 2 ->
                     let rec elemOf (c: ValueClass) = match c with VCArray e -> elemOf e | c -> c
                     (match elemOf (cls ret) with
                      | VCNum _ | VCOpen -> ()
                      | r ->
                          let loc = match kbody with Some b -> firstLoc b | None -> SrcLoc.Nowhere
                          let castHint = match r with VCBool -> " -- convert it explicitly in the kernel (`Int64(b)` or `Float64(b)` is 1 or 0)" | _ -> " -- return a number"
                          refuse "BL3001" loc
                              ($"arithmetic is defined on numbers only: reynolds(..) sums the values of the {what} over the permutations of its arguments, and it returns {article r}; Blade converts nothing implicitly{castHint}"))
                 | _ -> ())
            // `object_for(f) >>@ object_for(g)`: g receives what f returns.
            | IRComposeObj (IRObjectFor lo, IRObjectFor ro) ->
                (match kernelSig lo.Kernel with
                 | Some (_, _, ret, _, _) -> judgeSlot ro.Kernel 0 (cls ret) true " (a `>>@` stage, given what the stage before it returns)"
                 | None -> ())
            | IRMask (arr, k) | IRSort (arr, k) ->
                (match tyOf arr |> Option.bind cellOf with
                 | Some cell -> judgeSlot k 0 (cls cell) true " (a per-element kernel)"
                 | None -> ())
            | IRApplyCombinator info when not info.HasReynolds ->
                (match kernelSig info.Kernel with
                 | Some (names, ps, ret, kbody, what) when ps.Length = info.Arrays.Length
                                                          && info.KernelInputRanks.Length = info.Arrays.Length
                                                          && info.ArrayTypes.Length = info.Arrays.Length ->
                     List.zip3 info.Arrays info.ArrayTypes (List.zip3 names ps info.KernelInputRanks)
                     |> List.iteri (fun i (arr, at, (pname, pTy, irank)) ->
                         // a range / halo operand's cells are index values (Int64
                         // ordinals or a window), never a Bool, a String or a struct
                         match arr with
                         | IRRange _ | IRVirtualReverse _ -> ()
                         | _ ->
                             // a cell (input rank 0) or a row (an array of the operand's element)
                             let given = if irank = 0 then cls at.ElemType else VCArray (cls at.ElemType)
                             match clash true given (cls pTy) with
                             | Some (lossyNum, a, pc) -> refuse (codeOf lossyNum) SrcLoc.Nowhere (argMessage i pname (what + " (a map kernel)") lossyNum a pc)
                             | None -> ())
                     // ...and the kernel's RESULT against the map's cells (a kernel
                     // whose tail call was typed at a cell's type while the callee
                     // returns its parameters' default)
                     match stripUnits info.OutputType with
                     | ArrayElem oa when info.KernelOutputRank = 0 ->
                         (match clash true (cls ret) (cls oa.ElemType) with
                          | Some (lossyNum, r, t) ->
                              let rec tailLoc (e: IRExpr) =
                                  match e with
                                  | IRAppAt (_, _, _, l) when not (isNull l) && l.Span.StartLine > 0 -> l
                                  | IRLet (_, _, b) -> tailLoc b
                                  | IRCompute inner -> tailLoc inner
                                  | _ -> SrcLoc.Nowhere
                              let loc = match kbody with Some b -> (let l = tailLoc b in if l.Span.StartLine > 0 then l else firstLoc b) | None -> SrcLoc.Nowhere
                              refuse (codeOf lossyNum) loc
                                  ($"the map's cells are {className t}, but its kernel returns {article r} (a lambda whose parameter its body left open is typed at its first use or its default, never by a later argument): "
                                   + "the value would be converted, and Blade converts nothing implicitly (formalism 2.4) -- annotate the lambda's parameter with the cell's type, or convert explicitly")
                          | None -> ())
                     | _ -> ()
                 | _ -> ())
            // G275: a REYNOLDS map sums its kernel over the PERMUTATIONS of its
            // arguments, so every operand's cell is handed to every parameter
            // (the positional pairing above is one permutation of many).
            | IRApplyCombinator info when info.HasReynolds ->
                let rec unReynolds (k: IRExpr) = match k with IRReynolds (inner, _) -> unReynolds inner | k -> k
                let k = unReynolds info.Kernel
                (match kernelSig k with
                 | Some (names, ps, _, _, what) when ps.Length = info.Arrays.Length
                                                     && info.KernelInputRanks.Length = info.Arrays.Length
                                                     && info.ArrayTypes.Length = info.Arrays.Length
                                                     && ps.Length >= 2 ->
                     for (arr, at, irank) in List.zip3 info.Arrays info.ArrayTypes info.KernelInputRanks do
                         match arr with
                         | IRRange _ | IRVirtualReverse _ -> ()
                         | _ ->
                             let given = if irank = 0 then cls at.ElemType else VCArray (cls at.ElemType)
                             List.zip names ps |> List.iteri (fun i (pname, pTy) ->
                                 match clash true given (cls pTy) with
                                 | Some (lossyNum, a, pc) ->
                                     refuse (codeOf lossyNum) SrcLoc.Nowhere
                                         (argMessage i pname (what + " (a reynolds kernel, which receives every operand at every parameter)") lossyNum a pc)
                                 | None -> ())
                 | _ -> ())
            // `comp >>= cont`: the continuation's parameter receives comp's value.
            | IRBind (comp, cont) ->
                (match tyOf comp with
                 | Some ct ->
                     let given = match stripUnits ct with IRTComputation inner -> inner | t -> t
                     judgeSlot cont 0 (cls given) true " (a `>>=` continuation)"
                 | None -> ())
            // A struct literal's field values against the declared field types.
            | IRStructLit (typeName, fields) ->
                (match Map.tryFind typeName structFields with
                 | Some declared ->
                     for (fname, fe) in fields do
                         match List.tryFind (fun (n, _) -> n = fname) declared, tyOf fe with
                         | Some (_, fty), Some aty ->
                             (match clash (not (isLiteral fe)) (cls aty) (cls fty) with
                              | Some (lossyNum, a, p) ->
                                  refuse (codeOf lossyNum) SrcLoc.Nowhere
                                      $"the field `{fname}` of a {Blade.Types.TypeOrigins.sourceName typeName} literal is given {article a}, but it is declared {className p}: Blade converts nothing implicitly"
                              | None -> ())
                         | _ -> ()
                 | None -> ())
            | _ -> ()) body
    // Constant-scrutinee matches folded first, as the optimizer folds them: an
    // arity-polymorphic instance's arms for OTHER ranks are dead there.
    let fold (e: IRExpr) = IRMono.foldConstMatchesExpr e
    let roots =
        [ for m in modules do
            for b in m.Bindings do
                yield (b.Name, b.Name, "", fold b.Value)
            for f in m.Functions do
                let instance =
                    if f.Name <> f.SourceName && f.Name.Contains "_HM_" && not f.Params.IsEmpty then
                        let ps = f.Params |> List.map (fun p -> $"{p.Name}: {ppIRType p.Type}") |> String.concat ", "
                        $" (in the instance of '{f.SourceName}' at {ps})"
                    else ""
                yield (f.Name, f.SourceName, instance, fold f.Body)
            // executable side-table payloads (a top-level compound / random /
            // sparse constructor's operands: the binding's value is a unit there)
            for root in Optimize.compactionRoots m.Functions m do
                match root with
                | Optimize.RootSideOperand (b, _, e) -> yield (b.Name, b.Name, "", fold e)
                | _ -> () ]
    // ...and the expressions the types along every root carry (a range bound,
    // a computed extent), which the expression walk does not enter
    let roots =
        roots @ (roots |> List.collect (fun (d, s, i, body) ->
                    Optimize.embeddedExtentExprs body |> List.map (fun x -> (d, s, i, fold x))))
    allBodies <- roots |> List.map (fun (_, _, _, body) -> body)
    // three sweeps, so the most direct cause is named: every call's arguments,
    // then every kernel slot, then every operand
    for phase in [ 0; 1; 2 ] do
        for (declName, sourceName, instance, body) in roots do
            check phase declName sourceName instance body

/// Two shapes the lanes cannot give ONE meaning, refused here so every lane
/// (C++, LLVM, the interpreter -- all lower through this pass) reports the
/// same diagnostic (round 3 wave 4, W4-1; formalism 10.2). COMPLETE rather
/// than clever: every judgment below over-approximates, and an approximation
/// it cannot finish refuses.
///
///   * A WRITE THROUGH A DEFERRED COMPUTATION (BL4005; gap G60). A deferred
///     binder -- a top-level map, its top-level aliases, a deferred tuple, a
///     bare map or non-copy alias bound in any block or callable body -- has
///     no storage of its own: it is evaluated where it is read and fused into
///     the folds over it. The WRITTEN-BINDER set is a program-wide fixpoint by
///     binder IDENTITY: the binders an element write or a whole reassignment
///     lands in (through every `let` view, alias, rebinding and `if` of one,
///     stopping only at a PROVED copy -- an assignable `let a = Z` of a dense
///     array, which every lane deep-copies -- or at fresh storage), the
///     arguments a call passes to a parameter in the set (two hops and more,
///     by iteration), every array argument of a call whose callee cannot be
///     resolved (a callback), and the inputs of a map whose kernel writes a
///     parameter. A deferred binder in the set is refused at its first write
///     site; a provenance walk that runs out of budget refuses too.
///   * AN `if` VALUE OVER COMPUTED ARRAYS the compiled lanes cannot order or
///     own (BL6002; gap G59) -- the shapes IRLift.ifNeedsOrderedArms names
///     (the C++ emitter's own judgment, shared). Inside a function body, a
///     lambda or a loop body: the taken arm's storage has no owner after the
///     branch. Anywhere: an arm that READS a deferred computation other than
///     as a copy member (a positional read, a capture, a member whose own
///     computation reads another deferred binding, a nested condition) would
///     force it only on the taken arm, and no lane's static print list follows
///     the branch.
/// G190 (c), round 4 wave 8 (lead decision, the settled contract): a `mut`
/// ARGUMENT -- an ad.grad / ad.vjp gradient buffer (the synthesized
/// `f__grad` / `f__vjp` takes its buffers as `mut` parameters) and an ordinary
/// `mut` parameter alike -- may NOT share storage with anything the callee
/// READS: another array or aggregate argument, or a module binding the callee
/// reads BY NAME, transitively (its body, the callables it calls and the
/// kernels it applies, a deferred map followed to everything its evaluation
/// reads, function values resolved to their callee). The callee's writes
/// through the parameter would otherwise be visible through its own reads
/// mid-call, and the lanes disagreed on what they saw (c9: a silently wrong
/// gradient in both compiled lanes). Judged by STORAGE PROVENANCE (the shared
/// walk of refuseDeferredWritesAndUnorderedIfs, `storageOf`, with its callee
/// RETURN SUMMARIES: `identity(a)` may return `a`, an allocating helper is
/// fresh) and refused HERE, before the lane split, with the no-alias code
/// BL4005 -- every lane and the interpreter agree. Binders that only CARRY
/// storage (a tuple, a struct) do not make two arrays one. A walk that runs out
/// of budget refuses (fail closed).
/// The declared `mut` parameter positions of a callable (TypeCheck publishes
/// them by binder id); a specialization answers through its ORIGIN
/// (IRModule.DerivedFuncOrigins, never a display name: Codex diff review round
/// 2). A lambda declares none; an ETA WRAPPER of a named function forwards its
/// parameters to that function's positions. Shared by the mut-argument
/// judgment and D9's protected-parameter roots (deep review R12).
let private mutParamPositionsOf
        (origins: Map<IRId, IRId>) (funcs: Map<IRId, IRCallable>)
        (resolve: IRExpr -> IRCallable option) (c0: IRCallable) : int list =
    let rec positionsOf (c: IRCallable) : int list =
        // a LAMBDA declares no mut parameter: a clone of one inherits none
        // (DerivedFuncOrigins files it under its enclosing function, for
        // emission order only: Codex diff review rounds 3 and 5) -- but an
        // ETA WRAPPER of a named function (`<@> bump`) forwards its params
        // to that function, so the function's positions map back onto it
        if c.Name.StartsWith "__lambda" then etaPositions c else
        let rec viaOrigin (seen: Set<IRId>) (id: IRId) =
            match Blade.Types.MutParamTable.positions id with
            | [] ->
                // an ORIGIN only for a true specialization: the same parameter
                // list by name (DerivedFuncOrigins also files a cloned LAMBDA
                // under its enclosing function, for emission order: Codex diff
                // review round 3)
                // (an origin the monomorphizer REMOVED -- an HM-generic
                // original no call names any more -- is taken as is: Codex
                // diff review round 4, functions/693)
                (match Map.tryFind id origins with
                 | Some o when not (seen.Contains o)
                               && (match Map.tryFind o funcs with
                                   | Some oc -> (oc.Params |> List.map (fun p -> p.Name)) = (c.Params |> List.map (fun p -> p.Name))
                                   | None -> true) -> viaOrigin (seen.Add id) o
                 | _ -> [])
            | l -> l
        viaOrigin Set.empty c.Id
    and etaPositions (c: IRCallable) : int list =
        let rec peel (e: IRExpr) = match e with IRLet (_, _, b) -> peel b | IRCompute x -> peel x | e -> e
        match peel c.Body with
        | IRApp (f, args, _) ->
            (match resolve f with
             | Some g when g.Id <> c.Id ->
                 let paramIndex (a: IRExpr) =
                     match a with
                     | IRVar (vid, _) -> c.Params |> List.tryFindIndex (fun p -> p.VarId = vid)
                     | IRParam (nm, _, _) -> c.Params |> List.tryFindIndex (fun p -> p.Name = nm)
                     | _ -> None
                 positionsOf g |> List.choose (fun k -> if k < args.Length then paramIndex args.[k] else None)
             | _ -> [])
        | _ -> []
    positionsOf c0

let private refuseAliasedMutArgumentsWith
        (origins: Map<IRId, IRId>)
        (allBindings: IRBinding list) (allFuncs: IRCallable list) (funcs: Map<IRId, IRCallable>)
        (lets: System.Collections.Generic.Dictionary<IRId, IRExpr>) (deferred: Set<IRId>)
        (resolve: IRExpr -> IRCallable option)
        (storageOf: int -> IRCallable option -> IRExpr -> Set<IRId> option)
        (exprHoldsStorage: IRExpr -> bool)
        (paramCands: IRExpr -> IRCallable list option)
        (sites: (Blade.Ast.Span * IRCallable option * IRExpr) list) : unit =
    let moduleIds = allBindings |> List.map (fun b -> b.Id) |> Set.ofList
    let bindingById = allBindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let positionsOf (c: IRCallable) : int list = mutParamPositionsOf origins funcs resolve c
    // the module bindings a callable reads by name, through the callables it
    // names (calls, kernels, captures) -- transitively
    // (reads, untraced): untraced when a body the callee may run APPLIES a
    // function value no resolution reaches -- a closure held by a module let
    // (`let kf = factory(g)`), an aggregate element (`fns(0)(x)`) -- whose
    // reads are unknown, so the call is refused fail closed (wave-8 fable
    // review: the closure route of G190 (c))
    let readsCache = System.Collections.Generic.Dictionary<IRId, Set<IRId> * bool>()
    // an expression with the expressions its TYPES carry (range bounds,
    // extents: G250 / Codex probe C, `reduce(0..touch(g, alias), (+), 0)`),
    // which every read and call scan of this rule walks too
    let rootsWithExtents (e: IRExpr) (extra: IRExpr list) : IRExpr list =
        let es = e :: extra
        es @ (es |> List.collect Optimize.embeddedExtentExprs)
    // G240 (4), round 4 wave 9: a LOCAL binder a callable the callee runs
    // captures (a view `let row = m(0)` or a deferred map `let d = ..` of a
    // top-level block, captured by a callback lambda) is a by-name read too:
    // its storage is followed like a module binding's (its type from the
    // capture record or the reference)
    let localTypes = System.Collections.Generic.Dictionary<IRId, IRType>()
    let localNames = System.Collections.Generic.Dictionary<IRId, string>()
    let calleeReadsT (c: IRCallable) : Set<IRId> * bool =
        match readsCache.TryGetValue c.Id with
        | true, s -> s
        | _ ->
            let visited = System.Collections.Generic.HashSet<IRId>()
            let acc = System.Collections.Generic.HashSet<IRId>()
            let untracedRead = ref false
            let isFunctionTyped (e: IRExpr) =
                match e with
                // a struct field's type may not be known yet (typeOf answers
                // unit before the field cache is filled): fail closed (Codex
                // review of the fable fix, finding 2)
                | IRFieldAccess _ -> (try (match stripUnits (typeOf e) with FuncElem _ | IRTUnit -> true | _ -> false) with _ -> true)
                | _ -> try (match stripUnits (typeOf e) with FuncElem _ -> true | _ -> false) with _ -> true
            // an ARGUMENT that may be a function value: function-typed, or a
            // struct field of unknown type
            let maybeFunction (e: IRExpr) =
                match e with
                | IRFieldAccess _ -> (try (match stripUnits (typeOf e) with FuncElem _ | IRTUnit -> true | _ -> false) with _ -> true)
                | _ -> (try (match stripUnits (typeOf e) with FuncElem _ -> true | _ -> false) with _ -> false)
            // a callable the body EXECUTES (a call, a map / fold kernel, a
            // callable handed to a call); merely naming one runs nothing
            // (Codex diff review round 2)
            let rec visit (g: IRCallable) =
                if visited.Add g.Id then
                    // the body AND the expressions its types carry (a range
                    // bound, a parameter's extent: G250 / Codex probe C)
                    let roots = rootsWithExtents g.Body (g.Params |> List.map (fun p -> IRVar (p.VarId, p.Type)))
                    for r in Set.union (roots |> List.map collectVarRefsIR |> Set.unionMany) (g.Captures |> List.map (fun ci -> ci.Id) |> Set.ofList) do
                        if moduleIds.Contains r then acc.Add r |> ignore
                    // a captured LOCAL (G240 (4)) is a read of it by name
                    for ci in g.Captures do
                        if not (moduleIds.Contains ci.Id) && not (funcs.ContainsKey ci.Id) then
                            acc.Add ci.Id |> ignore
                            localTypes.[ci.Id] <- ci.Type
                            localNames.[ci.Id] <- ci.Name
                    // EVERY callable reference may execute (a call head, a
                    // kernel, a sort key, a callable argument: Codex diff
                    // review round 3), except the value of a `let` that only
                    // BINDS a callable (`let unused = read_a`: round 2)
                    let ownParams = g.Params |> List.map (fun p -> p.Name) |> Set.ofList
                    let ownParamIds = g.Params |> List.map (fun p -> p.VarId) |> Set.ofList
                    let rec walk (e: IRExpr) =
                        match e with
                        // (binding a function value, resolved or not, runs nothing)
                        | IRLet (_, (IRVar _ as v), b) when (resolve v).IsSome || maybeFunction v -> walk b
                        // ... nor does binding or STORING one held in an aggregate:
                        // `let f = ks[0]`, `out(0) = f` (an array of closures written,
                        // functions/858, 549) -- its later application is judged where
                        // it is applied
                        | IRLet (_, ((IRTupleProj (a, _, _) | IRFieldAccess (a, _)) as v), b) when maybeFunction v -> walk a; walk b
                        | IRLet (_, (IRIndex (a, idxs, _) as v), b) when maybeFunction v -> walk a; List.iter walk idxs; walk b
                        | IRAssign (t, v) ->
                            (match t with
                             | IRIndex (a, idxs, _) -> walk a; List.iter walk idxs
                             | IRTupleProj (a, _, _) | IRFieldAccess (a, _) -> walk a
                             | _ -> walk t)
                            (match v with
                             | IRVar _ | IRTupleProj _ | IRFieldAccess _ | IRIndex _ when (resolve v).IsNone && maybeFunction v ->
                                 childrenOf v |> List.iter walk
                             | _ -> walk v)
                        | IRVar (vid, _) ->
                            (match resolve e with
                             | Some h -> visit h
                             | None ->
                                 // G240 (3-kernel), round 4 wave 9: a function
                                 // value no resolution reaches standing in a
                                 // KERNEL slot -- a sort key, a fold kernel, a
                                 // mask predicate, any callable position that is
                                 // no call head (`sort(y, kf)`, `reduce(y, kf2, s)`)
                                 // -- runs too, with unknown reads (fail closed);
                                 // this body's own parameters are judged at
                                 // its call sites
                                 if not (ownParamIds.Contains vid) && maybeFunction e then
                                     untracedRead.Value <- true)
                        // ... and a function value held in an AGGREGATE or a
                        // FIELD in such a slot (`sort(y, ks[0])`, `holder.cb`,
                        // an array of closures `fns(0)`): untraced too (G250 /
                        // Codex probe B)
                        // (a field of unknown type fails closed, as maybeFunction
                        // does: Codex diff review 5f64f352 finding 5)
                        | IRTupleProj _ | IRFieldAccess _ | IRIndex _ when
                                (resolve e).IsNone
                                && (match e with
                                    | IRFieldAccess _ -> maybeFunction e
                                    | _ -> (try (match stripUnits (typeOf e) with FuncElem _ -> true | _ -> false) with _ -> false)) ->
                            untracedRead.Value <- true
                            childrenOf e |> List.iter walk
                        | IRApp (f, args, _) ->
                            // a function value no resolution reaches handed ON
                            // to another call (`apply(kf, 1.0)` inside the
                            // callee: the helper invokes it) is untraced too
                            // (Codex review of the fable fix, finding 1)
                            for a in args do
                                match a with
                                | IRParam _ -> ()
                                | IRVar (vid, _) when ownParamIds.Contains vid -> ()
                                | _ when (resolve a).IsNone && maybeFunction a -> untracedRead.Value <- true
                                | _ -> ()
                            // an applied head that resolves to no callable and
                            // is a FUNCTION value (not an array read, not one
                            // of this body's own parameters -- their actual
                            // arguments are judged at the call site)
                            (match f with
                             | IRParam (nm, _, _) when ownParams.Contains nm -> ()
                             | IRVar (vid, _) when ownParamIds.Contains vid -> ()
                             | IRParam _ -> ()   // an intrinsic / constructor head
                             | _ when (resolve f).IsNone && isFunctionTyped f -> untracedRead.Value <- true
                             | _ -> ())
                            childrenOf e |> List.iter walk
                        | _ -> childrenOf e |> List.iter walk
                    roots |> List.iter walk
            visit c
            let s = (Set.ofSeq acc, untracedRead.Value)
            readsCache.[c.Id] <- s
            s
    let calleeReads (c: IRCallable) : Set<IRId> = fst (calleeReadsT c)
    // the binders whose storage a READ of module binding `id` may touch: its
    // own storage, and, for a DEFERRED map, everything its evaluation reads
    let rec readStorageFrom (visited: System.Collections.Generic.HashSet<IRId>) (owner: IRCallable option) (id: IRId) : Set<IRId> option =
        if not (visited.Add id) then Some Set.empty
        else
            // the binders the EVALUATION of a deferred value `v` reads
            // (module binders and the locals it names), transitively
            let viaEvaluationOf (v0: IRExpr) =
                let v = IRSequence (rootsWithExtents v0 [])
                let refs = collectVarRefsIR v
                iterIRExpr (fun n ->
                    match n with
                    | IRVar (rid, rty) when not (moduleIds.Contains rid) && not (funcs.ContainsKey rid) && not (localTypes.ContainsKey rid) ->
                        localTypes.[rid] <- rty
                    | _ -> ()) v
                let viaCallables =
                    refs |> Set.toList |> List.collect (fun r ->
                        match Map.tryFind r funcs with
                        | Some c -> (c.Captures |> List.map (fun ci -> ci.Id)) @ Set.toList (calleeReads c)
                        | None -> [])
                // a kernel of the map applying an untraceable function
                // value: unknown reads (fail closed)
                let kernelUntraced =
                    refs |> Set.exists (fun r ->
                        match Map.tryFind r funcs with
                        | Some c -> snd (calleeReadsT c)
                        | None -> false)
                if kernelUntraced then None else
                Set.union refs (Set.ofList viaCallables)
                |> Set.filter (fun d -> moduleIds.Contains d || localTypes.ContainsKey d)
                |> Set.toList
                |> List.fold (fun acc d ->
                    match acc, readStorageFrom visited owner d with
                    | Some a, Some s -> Some (Set.union a s)
                    | _ -> None) (Some Set.empty)
            let combine own viaEvaluation =
                match own, viaEvaluation with
                | Some a, Some v -> Some (Set.union a v)
                | _ -> None
            match bindingById.TryFind id with
            | Some b ->
                let own = storageOf 48 None (IRVar (id, b.Type))
                combine own (if deferred.Contains id then viaEvaluationOf b.Value else Some Set.empty)
            | None ->
                match localTypes.TryGetValue id with
                | true, ty ->
                    let own = storageOf 48 owner (IRVar (id, ty))
                    let viaEvaluation =
                        if not (deferred.Contains id) then Some Set.empty
                        else
                            match lets.TryGetValue id with
                            | true, v -> viaEvaluationOf v
                            | _ -> Some Set.empty
                    combine own viaEvaluation
                | _ -> Some (Set.singleton id)
    let readStorage (owner: IRCallable option) (id: IRId) = readStorageFrom (System.Collections.Generic.HashSet<IRId>()) owner id
    let readerName (id: IRId) =
        match bindingById.TryFind id with
        | Some b -> b.Name
        | None ->
            match localNames.TryGetValue id with
            | true, n -> n
            | _ -> "<local>"
    // every binder counts, a tuple / struct CARRIER too: an inline array
    // member of a tuple has no identity but its carrier's (Codex diff review
    // round 2), so two components of one tuple are judged one storage (a
    // conservative refusal, recorded)
    let isStorageId (_: IRId) = true
    let nameOfBinder (id: IRId) =
        match bindingById.TryFind id with
        | Some b -> $"'{b.Name}'"
        // a member identity of a known tuple carrier (storageAt, G240 (2))
        | None when id < 0 -> "one tuple member"
        | None -> "a local array"
    // the first binder `a` and `b` share, the one the refusal names
    let sharedOf (a: Set<IRId>) (b: Set<IRId>) : IRId option =
        a |> Seq.tryFind (fun x -> b.Contains x)
    let calleeName (c: IRCallable) =
        let n = if System.String.IsNullOrEmpty c.SourceName then c.Name else c.SourceName
        if n.EndsWith "__grad" then $"'{n}' (ad.grad({n.Substring(0, n.Length - 6)}))"
        elif n.EndsWith "__vjp" then $"'{n}' (ad.vjp({n.Substring(0, n.Length - 5)}))"
        else $"'{n}'"
    let refuse (span: Blade.Ast.Span) (msg: string) =
        raise (Blade.Diagnostics.BladeDiagnosticException
                (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints span msg))
    let untraced (span: Blade.Ast.Span) (c: IRCallable) (i: int) =
        refuse span ($"function {calleeName c}: the storage of argument {i + 1}, passed to a mut parameter, or of something the callee reads, could not be traced (more than 48 nested view, alias or call steps): it may share storage with what the callee reads (fail closed). Pass a separate array: bind a copy (`let mut c = <it>`) and pass c.")
    let remedy = "Pass a separate array: bind a copy (`let mut c = <it>`) and pass c to one of them."
    let untracedRead (span: Blade.Ast.Span) (c: IRCallable) (i: int) =
        refuse span ($"function {calleeName c}: argument {i + 1} is passed to a mut parameter, but the callee applies a function value whose reads could not be traced (a closure held by a binding, an aggregate element, a callback no resolution reaches): it may read the argument's storage mid-call (fail closed). {remedy}")
    // a site's roots: its value / body, the expressions the types it names
    // carry, and its parameters' types
    let siteRoots (owner: IRCallable option) (root: IRExpr) =
        rootsWithExtents root (match owner with Some f -> f.Params |> List.map (fun p -> IRVar (p.VarId, p.Type)) | None -> [])
    // G250 (1)-(3), round 4 wave 9 (Codex design review 5f64f352, option (a)):
    // a callable with a mut parameter used as a VALUE erases its write
    // permission -- a function type has no `mut`, so `apply(bump, xs)` (with
    // `function apply(h: (A, A) -> Float64, b: A) = h(b, b)`) wrote the
    // immutable xs through h unjudged, and so did a derivative held in a tuple
    // or a field or handed to a wrapper. Such a callable may appear only as a
    // DIRECT call head (the judged call), as a `let` alias of one, or as the
    // resolved kernel of a map application this rule judges (with the same
    // callable in that application's canonical object_for loop); anywhere else
    // -- a call argument, a tuple / array / struct member, a returned value, a
    // composition stage, an object_for outside a judged application -- is BL4005.
    let isMutCallable (e: IRExpr) =
        match resolve e with
        | Some c -> not (List.isEmpty (positionsOf c))
        | None -> false
    // a composition value, through let / binding aliases
    let rec compositionOf (seen: Set<IRId>) (e: IRExpr) : IRExpr option =
        match e with
        | IRComposeObj _ | IRObjectFor _ -> Some e
        | IRVar (id, _) when not (seen.Contains id) ->
            (match lets.TryGetValue id with
             | true, v -> compositionOf (seen.Add id) v
             | _ ->
                 match bindingById.TryFind id with
                 | Some b -> compositionOf (seen.Add id) b.Value
                 | None -> None)
        | _ -> None
    let rec stagesOf (e: IRExpr) : IRExpr list =
        match e with
        | IRComposeObj (l, r) -> stagesOf l @ stagesOf r
        | IRObjectFor oi -> [ oi.Kernel ]
        | _ -> []
    let compositionHasMutStage (e: IRExpr) =
        match compositionOf Set.empty e with
        | Some c -> stagesOf c |> List.exists isMutCallable
        | None -> false
    // the callable a kernel reference runs, for the bound-loop-object identity
    // test only: a wrapper whose body's TAIL (after its let prefix) is a call of
    // a resolved function on its own parameters, in order, forwards to that
    // function; any other callable is its own target. The application's full
    // body is still judged by the read analysis.
    let forwardTarget (k: IRExpr) : IRId option =
        match resolve k with
        | Some c ->
            let rec peel (x: IRExpr) = match x with IRLet (_, _, b) -> peel b | IRCompute y -> peel y | x -> x
            match peel c.Body with
            | IRApp (f, args, _) when args.Length = c.Params.Length
                                      && List.forall2 (fun (a: IRExpr) (p: IRParam) ->
                                             match a with
                                             | IRVar (vid, _) -> vid = p.VarId
                                             | IRParam (nm, _, _) -> nm = p.Name
                                             | _ -> false) args c.Params ->
                (match resolve f with
                 | Some g -> Some g.Id
                 | None -> Some c.Id)
            | _ -> Some c.Id
        | None -> None
    for (span, owner, root) in sites do
        // G240 (1), round 4 wave 10: a value use inside a call's arguments is
        // spanned at that call (its surface span), else at the declaration
        let vspan = ref span
        let mutValue (e: IRExpr) =
            let c = (resolve e).Value
            refuse vspan.Value ($"function {calleeName c} has a mut parameter and is used here as a VALUE (an argument, a tuple / array / struct member, a returned value, a composition stage): a function type carries no `mut`, so a call through it would write its argument unjudged (formalism: a mut argument may not share storage with what the callee reads). Call it directly (`{calleeName c}(..)` with its arguments), or bind the array it writes with `let mut` and pass a function without a mut parameter.")
        let rec scanV (e: IRExpr) =
            match e with
            | IRApp (f, args, _) ->
                (match f with
                 | IRVar _ when (resolve f).IsSome -> ()
                 | _ -> scanV f)
                let saved = vspan.Value
                (match e with
                 | IRAppAt (_, _, _, loc) when not (isNull loc) && loc.Span.StartLine > 0 -> vspan.Value <- loc.Span
                 | _ -> ())
                args |> List.iter scanV
                vspan.Value <- saved
            | IRLet (_, (IRVar _ as v), b) when isMutCallable v -> scanV b
            | IRApplyCombinator info ->
                let kc = resolve info.Kernel
                let kId = kc |> Option.map (fun c -> c.Id)
                // the canonical loop's kernel: the judged kernel itself, or the
                // callable an ETA wrapper kernel calls directly
                let etaTarget =
                    kc |> Option.bind (fun c ->
                        let rec peel (x: IRExpr) = match x with IRLet (_, _, b) -> peel b | IRCompute y -> peel y | x -> x
                        match peel c.Body with
                        | IRApp (f, _, _) -> resolve f |> Option.map (fun g -> g.Id)
                        | _ -> None)
                for ch in childrenOf e do
                    if obj.ReferenceEquals(ch, info.Kernel) && kId.IsSome then ()
                    elif obj.ReferenceEquals(ch, info.Loop) then
                        match info.Loop with
                        | IRObjectFor oi when kId.IsSome
                                              && (let lk = resolve oi.Kernel |> Option.map (fun c -> c.Id)
                                                  lk = kId || (lk.IsSome && lk = etaTarget)) ->
                            for ch2 in childrenOf info.Loop do
                                if not (obj.ReferenceEquals(ch2, oi.Kernel)) then scanV ch2
                        // a BOUND loop object applied by name (`K <@> xs` with
                        // `let K = object_for(poke)`): the same judged kernel
                        // (the binding and the application each lift their OWN
                        // eta wrapper of poke: compared by their verified
                        // forwarding targets, Codex diff review 5f64f352 round 6)
                        | IRVar _ when kId.IsSome
                                       && (match compositionOf Set.empty info.Loop with
                                           | Some (IRObjectFor oi) ->
                                               let lk = resolve oi.Kernel |> Option.map (fun c -> c.Id)
                                               lk = kId || (lk.IsSome && lk = etaTarget)
                                               || (let t = forwardTarget oi.Kernel in t.IsSome && t = forwardTarget info.Kernel)
                                           | _ -> false) -> ()
                        | _ -> scanV ch
                    else scanV ch
            // a COMPOSITION's stage kernel (`object_for(poke) >>@ ..`): the
            // stages run where the composition is APPLIED, and that
            // application (IRComposeApply) is judged below with its first
            // stage as the callee (index-types/555); the composition itself
            // may be bound and applied, never passed elsewhere
            // a composition is admitted only BOUND (a binding's or a let's
            // whole value) or APPLIED; built inline anywhere else (a tuple
            // member, an argument) with a mut stage it is a value use
            | IRLet (_, (IRComposeObj _ as v), b) -> scanComp v; scanV b
            // a bound bare LOOP OBJECT (`let K = object_for(poke)`) is a one-stage
            // composition: its application judges its kernel (fable review of
            // wave 9, F5)
            | IRLet (_, (IRObjectFor _ as v), b) -> scanComp v; scanV b
            | IRComposeObj _ when compositionHasMutStage e ->
                refuse span "a composition whose stage has a mut parameter is used here as a VALUE (an argument, a tuple member, a returned value): its writes would run where it is applied, unjudged. Bind it (`let K = ..`) and apply it directly (`K <@> arrays`)."
            | IRComposeApply info ->
                (match info.Composition with
                 | IRVar _ -> ()
                 | c -> scanComp c)
                info.InputArrays |> List.iter scanV
            | IRVar _ when isMutCallable e -> mutValue e
            | IRVar _ when compositionHasMutStage e -> refuse span "a composition whose stage has a mut parameter is used here as a VALUE (an argument, a tuple member, a returned value): its writes would run where it is applied, unjudged. Apply it directly (`K <@> arrays`)."
            | _ -> childrenOf e |> List.iter scanV
        and scanComp (e: IRExpr) =
            match e with
            | IRComposeObj (l, r) -> scanComp l; scanComp r
            | IRObjectFor oi when isMutCallable oi.Kernel -> ()
            | e -> scanV e
        // a module binding whose whole value is such a callable is an alias
        // (a MODULE binding only: a function body returning it is a value use,
        // Codex diff review 5f64f352 finding 1); a binding whose whole value
        // is a composition is the bound composition
        match owner, root with
        | None, IRVar _ when isMutCallable root || compositionHasMutStage root -> ()
        | None, (IRComposeObj _ | IRObjectFor _) -> scanComp root
        | _ -> siteRoots owner root |> List.iter scanV
    for (span, owner, root) in sites do
        iterIRExpr (fun n ->
            // a call, or a MAP whose kernel has mut parameters: each kernel
            // parameter receives its operand's storage (Codex diff review
            // round 4: `method_for(zip(b, a)) <@> bump` with b = identity(a))
            let call =
                match n with
                | IRApp (f, args, _) -> Some (f, args)
                // (only when the operands line up one to one with the kernel's
                // parameters: a multi-slot range supplies several -- Codex
                // diff review round 5)
                | IRApplyCombinator info ->
                    (match resolve info.Kernel with
                     | Some k when k.Params.Length = info.Arrays.Length -> Some (info.Kernel, info.Arrays)
                     | Some k when not (List.isEmpty (positionsOf k)) ->
                         // a multi-slot range beside an operand: the kernel's
                         // parameters do not line up with the operands, so a
                         // mut parameter's storage is not traced here (fail
                         // closed; wave-8 fable review)
                         refuse span ($"function {calleeName k}: a map applies it with a mut parameter, but its operands do not line up one to one with its parameters (a multi-slot range supplies several), so the storage the mut parameter receives could not be traced (fail closed). Iterate the operand itself (`method_for(A) <@> ..`) or pass a separate array.")
                     | _ -> None)
                // an APPLIED composition (G250): its FIRST stage receives the
                // applied arrays (a later stage reads the fresh intermediate)
                | IRComposeApply info ->
                    (match compositionOf Set.empty info.Composition |> Option.map stagesOf with
                     | Some (k0 :: _) ->
                         (match resolve k0 with
                          | Some k when k.Params.Length = info.InputArrays.Length -> Some (k0, info.InputArrays)
                          | Some k when not (List.isEmpty (positionsOf k)) ->
                              refuse span ($"function {calleeName k}: a composition applies it with a mut parameter, but the applied arrays do not line up one to one with its parameters, so the storage the mut parameter receives could not be traced (fail closed).")
                          | _ -> None)
                     | _ -> None)
                | _ -> None
            match call with
            | Some (f, args) ->
                (match resolve f with
                 | Some c ->
                     // a callable ARGUMENT runs inside the callee: its reads
                     // are the callee's (Codex diff review round 2). G240 (3),
                     // round 4 wave 10: an argument that is a function-valued
                     // PARAMETER of the enclosing callable forwarded on (`function
                     // outer(cb: .., y: mut A) = helper(cb, y)`) runs the callables
                     // its every direct call site passes there (paramCandidatesFrom,
                     // rebinding-aware; None -- a value-used callable, a partial
                     // application, any other function value -- stays untraced)
                     let argCallables (a: IRExpr) : IRCallable list option =
                         match resolve a with
                         | Some g -> Some [ g ]
                         | None -> paramCands a
                     let reads =
                         args |> List.fold (fun acc a ->
                             match argCallables a with
                             | Some gs -> gs |> List.fold (fun s g -> Set.union s (calleeReads g)) acc
                             | None -> acc) (calleeReads c)
                     // a callee that applies an untraceable function value, or
                     // a function-valued ARGUMENT no resolution reaches (a
                     // closure): its reads are unknown (fail closed)
                     let readsUntraced =
                         snd (calleeReadsT c)
                         || (args |> List.exists (fun a ->
                                match argCallables a with
                                | Some gs -> gs |> List.exists (fun g -> snd (calleeReadsT g))
                                | None ->
                                    (try (match stripUnits (typeOf a), a with
                                          | FuncElem _, _ -> true
                                          | IRTUnit, IRFieldAccess _ -> true   // a field of unknown type
                                          | _ -> false) with _ -> false)))
                     // G240 (1), round 4 wave 10: a CALL is spanned at the call
                     // itself -- every lowered call carries its surface span
                     // (Lowering's TExprApp arm: IRAppAt .. SrcLoc.Of texpr.Span,
                     // kept by every clone and specialization); a map / applied
                     // composition, and a synthesized call with no location,
                     // keep the enclosing declaration
                     let callSpan =
                         match n with
                         | IRAppAt (_, _, _, loc) when not (isNull loc) && loc.Span.StartLine > 0 -> loc.Span
                         | _ -> span
                     for i in positionsOf c do
                         if i < args.Length && exprHoldsStorage args.[i] then
                             let aspan = callSpan
                             match storageOf 48 owner args.[i] with
                             | None -> untraced aspan c i
                             | Some si ->
                                 let si = si |> Set.filter isStorageId
                                 if not si.IsEmpty && readsUntraced then untracedRead aspan c i
                                 if not si.IsEmpty then
                                     args |> List.iteri (fun j other ->
                                         if j <> i && exprHoldsStorage other then
                                             match storageOf 48 owner other with
                                             | None -> untraced aspan c j
                                             | Some sj ->
                                                 match sharedOf si (sj |> Set.filter isStorageId) with
                                                 | Some sh ->
                                                     refuse aspan ($"function {calleeName c}: argument {i + 1} is passed to a mut parameter, but its storage also reaches the callee through argument {j + 1} (both may share the storage of {nameOfBinder sh}) -- the callee's writes through the parameter would be visible through its own reads mid-call. {remedy}")
                                                 | None -> ())
                                     for r in reads do
                                         match readStorage owner r with
                                         | None -> untraced aspan c i
                                         | Some rs ->
                                             match sharedOf si (rs |> Set.filter isStorageId) with
                                             | Some sh ->
                                                 let rn = readerName r
                                                 let how =
                                                     if r = sh then $"'{rn}', which the callee reads by name"
                                                     elif deferred.Contains r then $"'{rn}', which the callee reads by name -- a deferred map whose evaluation reads the storage of {nameOfBinder sh}"
                                                     else $"'{rn}', which the callee reads by name and which shares the storage of {nameOfBinder sh}"
                                                 refuse aspan ($"function {calleeName c}: argument {i + 1} is passed to a mut parameter, but its storage also reaches the callee through {how} -- the callee's writes through the parameter would be visible through its own reads mid-call. {remedy}")
                                             | None -> ()
                 | None -> ())
            | _ -> ()) (IRSequence (siteRoots owner root))

let private refuseDeferredWritesAndUnorderedIfs (modules: IRModule list) : unit =
    // (BLADE_PHASE_TIMING: the pass's parts, printed at its end)
    let swParts = System.Diagnostics.Stopwatch.StartNew()
    let parts = ResizeArray<string * int64>()
    let part (name: string) = parts.Add((name, swParts.ElapsedMilliseconds)); swParts.Restart()
    let (funcs, lets, mutableLets, _chaseAlias, resolve, _facts) = programWriteContext modules
    let allBindings = modules |> List.collect (fun m -> m.Bindings)
    // a root with the expressions its types carry, computed once per root (R12's
    // three scans share it)
    let extentRootsCache = System.Collections.Generic.Dictionary<IRExpr, IRExpr list>(HashIdentity.Reference)
    let withExtents (e: IRExpr) =
        match extentRootsCache.TryGetValue e with
        | true, l -> l
        | _ ->
            let l = e :: Optimize.embeddedExtentExprs e
            extentRootsCache.[e] <- l
            l
    let allFuncs = modules |> List.collect (fun m -> m.Functions)
    let bindingById = allBindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let isArrayTy (t: IRType) = match stripUnits t with ArrayElem _ -> true | _ -> false
    let typeIsArray (e: IRExpr) = try isArrayTy (typeOf e) with _ -> false
    let isDeferredForm (e: IRExpr) =
        match e with
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _
        | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRZip _ -> true
        | _ -> false
    // ---- the deferred binders -------------------------------------------------
    // The module bindings computeDeferredIds classifies (the classification
    // every lane's print pass reads), plus every let in a block or callable
    // body that is a deferred form, a deferred tuple, or a NON-copy alias of
    // one (a block / body alias in MutableArrayLets is an assignable copy).
    // Callable bodies' bare maps are stored by S2 (forceBareCombinatorLets),
    // so they arrive here as computes and are not deferred.
    let deferred =
        let localLets = ResizeArray<IRId * IRExpr>()
        let collect (e: IRExpr) =
            iterIRExpr (fun n -> match n with IRLet (id, v, _) -> localLets.Add((id, v)) | _ -> ()) e
        for b in allBindings do collect b.Value
        for f in allFuncs do collect f.Body
        let mutable acc = IRLift.computeDeferredIds allBindings
        let mutable changed = true
        while changed do
            changed <- false
            for (id, v) in localLets do
                if not (acc.Contains id) then
                    let isDef (e: IRExpr) =
                        match e with
                        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
                        | IRVar (s, _) -> acc.Contains s
                        | _ -> false
                    // a block-valued initializer is judged by its let chain's
                    // RESULT (computeDeferredIds' and the C++ emitter's reading)
                    let rec norm (v: IRExpr) =
                        match v with
                        | IRLet _ -> norm (snd (IRLift.peelLetChain v))
                        | v -> v
                    // (tuples only: a block whose result is a bare alias keeps
                    // its own reading, a local copy)
                    let d =
                        match v, norm v with
                        | IRVar (src, _), _ -> acc.Contains src && not (mutableLets.Contains id)
                        | _, IRTuple es -> not es.IsEmpty && es |> List.forall isDef
                        | v, _ -> isDeferredForm v
                    if d then
                        acc <- acc.Add id
                        changed <- true
        acc
    let dv : IRLift.DeferredView =
        { IsDeferred = deferred.Contains
          ValueOf = (fun id -> if deferred.Contains id then (match lets.TryGetValue id with | true, v -> Some v | _ -> None) else None)
          TypeOfExpr = (fun e -> try typeOf e with _ -> IRTUnit) }
    // A usable name for a refusal: a module binding's own name, else the
    // shape of the local binder (its IR let carries no surface name).
    let describe (id: IRId) =
        match bindingById.TryFind id with
        | Some b -> ($"`{b.Name}`", $"bind `{b.Name} |> compute`")
        | None -> ("a deferred map bound by a `let` inside a block or a function body",
                   "add `|> compute` at that `let` (`let m = <map> |> compute`)")
    let users = allBindings |> List.map (fun b -> (b, collectVarRefsIR b.Value))
    let funcSpan (f: IRCallable) =
        let s = IR.declSpanOf f.Name
        if s <> Blade.Ast.noSpan then s
        else
            match users |> List.tryFind (fun (_, refs) -> Set.contains f.Id refs) with
            | Some (b, _) -> IR.declSpanOf b.Name
            | None ->
                match allFuncs |> List.tryFind (fun g -> g.Id <> f.Id && Set.contains f.Id (collectVarRefsIR g.Body)) with
                | Some g -> IR.declSpanOf g.Name
                | None -> s
    // ---- the written binders ----------------------------------------------------
    // Every rebinding of a name (`row = m(1)`) is another value it may hold.
    let rebinds = System.Collections.Generic.Dictionary<IRId, ResizeArray<IRExpr>>()
    // (R12: the expressions types carry are walked too. A store INTO a struct
    // field, `b.r = d(0)`, is NOT recorded as a value `b` may hold: tracking
    // what a field holds apart from the struct that holds it is the staged
    // carrier-provenance class -- see formalism 2.7's "not covered")
    let installedName (target: IRExpr) =
        match target with
        | IRVar (id, _) -> Some id
        | _ -> None
    let noteRebinds (e: IRExpr) =
        for r in withExtents e do
            iterIRExpr (fun n ->
                match n with
                | IRAssign (target, rhs) ->
                    (match installedName target with
                     | Some id ->
                         (match rebinds.TryGetValue id with
                          | true, l -> l.Add rhs
                          | _ -> rebinds.[id] <- ResizeArray [ rhs ])
                     | None -> ())
                | _ -> ()) r
    for b in allBindings do noteRebinds b.Value
    for f in allFuncs do noteRebinds f.Body
    // the side tables' payloads too (G190 (c): their calls are judged; Codex
    // diff review round 4)
    let sidePayloads =
        modules |> List.collect (fun m ->
            Optimize.compactionRoots m.Functions m
            |> List.choose (function
                | Optimize.RootSideOperand (b, _, e) -> Some (b, e)
                | _ -> None))
    for (_, e) in sidePayloads do noteRebinds e
    // Every match pattern variable, with the scrutinee and the projection
    // path its pattern binds it at (a tuple component's index; any other
    // pattern form -- a cons, a variant payload, a struct field -- the whole
    // scrutinee, conservatively).
    let patBinds = System.Collections.Generic.Dictionary<IRId, IRExpr * (int * bool) list>()
    let notePatBinds (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRMatch (scrut, cases) ->
                let rec bind (pat: IRPattern) (path: (int * bool) list) =
                    match pat with
                    | IRPatVar id -> patBinds.[id] <- (scrut, path)
                    | IRPatTuple ps -> ps |> List.iteri (fun k p -> bind p (path @ [ (k, false) ]))
                    | IRPatCons (h, tl) -> bind h []; bind tl []
                    | IRPatVariant (_, _, Some p, _) -> bind p []
                    | IRPatStruct (_, fs) -> fs |> List.iter (fun (_, p) -> bind p [])
                    | _ -> ()
                for c in cases do bind c.Pattern []
            | _ -> ()) e
    for b in allBindings do notePatBinds b.Value
    for f in allFuncs do notePatBinds f.Body
    for (_, e) in sidePayloads do notePatBinds e
    // `let a = Z` that every lane deep-copies: an ASSIGNABLE let (a block /
    // body MutableArrayLets member, or a non-static module binding) of a
    // plain dense array (the copy is dense-only in every lane).
    // CAPTURE MODE (deep review D9, the rule on who may write a captured
    // plain `let`; set only by `storageOfCapture` below): a rename of a
    // COMPACT array (SymIdx / AntisymIdx / Hermitian over plain slots) is a
    // copy too -- every lane copies it with its storage class (C++
    // genVarAliasBinding's materializeArrayCopyForm, the LLVM lane's
    // `copyArr` of any pool rename, the interpreter's deep copy; corpus
    // loops/517 `rs`). The deferred-write and mut-argument judgments keep the
    // dense-only reading.
    part "setup: context"
    let captureMode = ref false
    // The callables the program uses as VALUES (named anywhere but as the head
    // of a direct call: an argument, a let, a kernel, a returned closure): the
    // only ones a call through a FUNCTION VALUE (a callback parameter, a
    // function-typed name) can reach. Read by the capture judgment only.
    let valueUsedCallables : IRCallable list =
        let all = System.Collections.Generic.Dictionary<IRId, int>()
        let heads = System.Collections.Generic.Dictionary<IRId, int>()
        let bump (d: System.Collections.Generic.Dictionary<IRId, int>) id =
            d.[id] <- (match d.TryGetValue id with | true, n -> n + 1 | _ -> 1)
        // (the expressions types carry too: a callable handed on inside a range
        // bound is a value use -- R12's per-site candidates rely on this set)
        let scan (e: IRExpr) =
            for r in withExtents e do
                iterIRExpr (fun n ->
                    match n with
                    | IRVar (id, _) when Map.containsKey id funcs -> bump all id
                    | CallOfNamed (id, _, _, _) when Map.containsKey id funcs -> bump heads id
                    | _ -> ()) r
        for b in allBindings do scan b.Value
        for f in allFuncs do scan f.Body
        for (_, e) in sidePayloads do scan e
        allFuncs |> List.filter (fun c ->
            let n = match all.TryGetValue c.Id with | true, n -> n | _ -> 0
            let h = match heads.TryGetValue c.Id with | true, n -> n | _ -> 0
            n > h)
    let valueUsedIds = valueUsedCallables |> List.map (fun c -> c.Id) |> Set.ofList
    let origins = modules |> List.fold (fun acc m -> Map.fold (fun a k v -> Map.add k v a) acc m.DerivedFuncOrigins) Map.empty
    // Every parameter binder, with its callable and position (deep review
    // R12: a non-`mut` parameter is a protected root of the D9 judgment, and
    // a function-valued parameter's candidates are its callable's arguments).
    let paramOwners = System.Collections.Generic.Dictionary<IRId, IRCallable * int>()
    for f in allFuncs do
        f.Params |> List.iteri (fun i p -> paramOwners.[p.VarId] <- (f, i))
    // The parameter a call head names, when it is one: an IRParam of the
    // owner by name, or a parameter binder by id (the owner's, or one an
    // enclosing callable's lambda captured).
    let paramOfHead (owner: IRCallable option) (f: IRExpr) : (IRCallable * int) option =
        match f with
        | IRParam (nm, _, _) ->
            owner |> Option.bind (fun c ->
                c.Params |> List.tryFindIndex (fun p -> p.Name = nm) |> Option.map (fun i -> (c, i)))
        | IRVar (vid, _) ->
            (match paramOwners.TryGetValue vid with
             | true, ci -> Some ci
             | _ -> None)
        | _ -> None
    // Every DIRECT call of a callable, with the site's owner (to read an
    // IRParam argument): `callable id -> (owner, args)` list.
    let directCalls = System.Collections.Generic.Dictionary<IRId, ResizeArray<IRCallable option * IRExpr list>>()
    let noteCalls (owner: IRCallable option) (e: IRExpr) =
        for r in withExtents e do
            iterIRExpr (fun n ->
                match n with
                | CallOfNamed (id, _, args, _) when Map.containsKey id funcs ->
                    (match directCalls.TryGetValue id with
                     | true, l -> l.Add((owner, args))
                     | _ -> directCalls.[id] <- ResizeArray [ (owner, args) ])
                | _ -> ()) r
    for b in allBindings do noteCalls None b.Value
    for f in allFuncs do noteCalls (Some f) f.Body
    for (_, e) in sidePayloads do noteCalls None e
    part "setup: value uses and calls"
    // THE CALLABLES A FUNCTION-VALUED PARAMETER MAY HOLD (deep review R12,
    // defect 2): the arguments its callable receives at that position at
    // every direct call -- a resolved callable, or, when the argument is
    // itself a parameter of the caller, that parameter's candidates in turn.
    // None (unknown: the caller falls back to every value-used callable) when
    // the callable is itself used as a value (its calls are not all visible),
    // a call supplies too few arguments (a partial application), or an
    // argument is any other function value (a closure binder, an aggregate
    // element, a call result).
    let rec paramCandidatesFrom (seen: Set<IRId * int>) (c: IRCallable) (k: int) : IRCallable list option =
        if seen.Contains (c.Id, k) then Some []
        elif valueUsedIds.Contains c.Id then None
        else
            let seen = seen.Add (c.Id, k)
            let sites = match directCalls.TryGetValue c.Id with | true, l -> List.ofSeq l | _ -> []
            sites |> List.fold (fun acc (siteOwner, args) ->
                match acc with
                | None -> None
                | Some cs ->
                    if k >= args.Length then None
                    else
                        let a = args.[k]
                        // a name whose alias chain passes a REBOUND binder
                        // (`let h = fresh; h = ident; f(h)`) may hold any value
                        // it was assigned: unknown (Codex review of R12)
                        let rec rebound (seen: Set<IRId>) (x: IRExpr) =
                            match x with
                            | IRVar (id, _) when not (seen.Contains id) ->
                                rebinds.ContainsKey id
                                || (match lets.TryGetValue id with
                                    | true, v -> rebound (seen.Add id) v
                                    | _ -> false)
                            | _ -> false
                        match resolve a with
                        | _ when rebound Set.empty a -> None
                        | Some g -> Some (g :: cs)
                        | None ->
                            match paramOfHead siteOwner a with
                            | Some (c2, k2) ->
                                paramCandidatesFrom seen c2 k2 |> Option.map (fun more -> more @ cs)
                            | None -> None) (Some [])
    // The callables a call through the function-valued head `f` may run: its
    // per-site candidates when `f` is a parameter, else every value-used
    // callable of the call's arity (the reading before R12).
    // (`arity` None: no arity filter, the write fixpoint's reading)
    let fnValueCandidates (owner: IRCallable option) (f: IRExpr) (arity: int option) : IRCallable list =
        let fits (c: IRCallable) = match arity with Some n -> c.Params.Length = n | None -> true
        let fallback () = valueUsedCallables |> List.filter fits
        match paramOfHead owner f with
        | Some (c, k) ->
            (match paramCandidatesFrom Set.empty c k with
             | Some cs -> cs |> List.distinctBy (fun g -> g.Id) |> List.filter fits
             | None -> fallback ())
        | None -> fallback ()
    let isProvedCopy (id: IRId) (v: IRExpr) =
        match v with
        | IRVar (_, ty) ->
            let assignable =
                mutableLets.Contains id
                || (match bindingById.TryFind id with Some b -> b.IsMutable | None -> false)
            assignable
            && (match stripUnits ty with
                | ArrayElem at ->
                    at.IndexTypes |> List.forall (fun ix ->
                        ix.IxKind = IxKPlain
                        && (ix.Symmetry = SymNone || (captureMode.Value && ix.Dependencies.IsEmpty)))
                | _ -> false)
        | _ -> false
    // The binders an array expression's storage may belong to (None: the
    // budget ran out -- the caller refuses). Followed: names (their
    // initializer unless it is a proved copy or fresh storage, and every
    // rebinding), views (index, curry, slice, field, tuple projection), both
    // arms of an `if`, a let chain's value, and the array arguments of a call
    // (a callee may return one of them). Anything else is fresh storage.
    //
    // AGGREGATE CARRIERS (gap G60(a)): a tuple, a struct or a computation
    // value may HOLD views (`let pair = (m(0), m(1))`), so a name of such a
    // type is followed exactly as an array name is -- its initializer and
    // every rebinding, by binder identity -- and a projection follows the
    // carrier along its PATH: `pair[0]` reaches the first member of the
    // tuple literal that initialized `pair` (through nested tuples, `if`
    // values, let chains and aliases of it), never its siblings. A path that
    // meets a form it cannot see into (a call's result, a struct) falls back
    // to the whole carrier: over-approximate, never under. A tuple wrapper is
    // NOT a copy: only a proved copy or fresh storage ends the walk.
    let rec holdsStorage (t: IRType) =
        match t with
        | Tagged inner -> holdsStorage inner
        | IRTTuple ts -> ts |> List.exists holdsStorage
        | IRTNamed _ | IRTComputation _ -> true
        | t -> isArrayTy t
    // A VIEW form IR.typeOf leaves untyped (IRTUnit: a curry, a slice, a pack
    // projection, ...) holds its base's storage: judged structurally, so a
    // carrier holding one (`(g(0), [5.0, 6.0])` projected by a run-time index)
    // keeps it (D9 Codex round 3; an untyped view used to read as "no storage").
    // A NAME whose IR type is untyped for the same reason (`let v = t[k]`, a
    // run-time pack index) holds what its initializer holds.
    let rec exprHoldsStorageF (fuel: int) (e: IRExpr) =
        match e with
        | IRCurry (a, _, _) | IRSlice (a, _, _, _) | IRSubset (a, _, _, _) | IRShift (a, _, _, _)
        | IRReverse (a, _) | IRDiag a | IRPolyIndex (a, _) | IRPolyTail (a, _) | IRTupleDecons a ->
            exprHoldsStorageF fuel a
        | IRTupleCons (h, tl) -> exprHoldsStorageF fuel h || exprHoldsStorageF fuel tl
        | IRVar (id, _) ->
            (try holdsStorage (typeOf e) with _ -> false)
            || (fuel > 0
                && (match lets.TryGetValue id with
                    | true, v -> exprHoldsStorageF (fuel - 1) v
                    | _ -> false))
        | _ -> try holdsStorage (typeOf e) with _ -> false
    let exprHoldsStorage (e: IRExpr) = exprHoldsStorageF 16 e
    // G240 / G250, round 4 wave 9: the STRICT MODE of the mut-argument judgment
    // (only there: the write fixpoint keeps its reading). In strict mode the
    // walk follows the storage-holding ELEMENTS of an array literal (a fresh
    // container does not stop propagation through what it holds, G250 (4)), and
    // a storage-holding call result whose head does not resolve, or whose
    // callee's return summary is unknown, is UNKNOWN (None) instead of the
    // callee's arguments (a callback returning an existing array: G250 /
    // Codex probe E). Return summaries are cached per mode.
    let strictMode = ref false
    let returnSummaries = System.Collections.Generic.Dictionary<bool * bool * IRId, (Set<int> * Set<IRId>) option>()
    // G240 (2), round 4 wave 10 (STRICT MODE only): MEMBER IDENTITIES of a
    // KNOWN carrier. A binder initialized by a tuple LITERAL and never rebound
    // (let pair = (g, h)) holds its members at distinct identities: pair[k]
    // is the member (carrier, k) plus what the literal's k-th member holds, and
    // the whole carrier is the carrier plus every member identity -- so two
    // DIFFERENT members passed as a read argument and a mut argument are two
    // storages (each still reaches its own named array), while the same member
    // twice, or a member beside its whole carrier, still share. An OPAQUE
    // carrier (a tuple parameter, a call result, a rebound binder) keeps the
    // one carrier identity: distinct paths there are no proof (Codex probe A,
    // wave 9). The synthetic identities are negative ids no binder has.
    let memberIds = System.Collections.Generic.Dictionary<IRId * int, IRId>()
    let memberCarrier = System.Collections.Generic.Dictionary<IRId, IRId>()
    let memberIdOf (carrier: IRId) (k: int) : IRId =
        match memberIds.TryGetValue ((carrier, k)) with
        | true, m -> m
        | _ ->
            let m = -(1000000 + memberIds.Count)
            memberIds.[(carrier, k)] <- m
            memberCarrier.[m] <- carrier
            m
    let knownCarrierMembers (id: IRId) : IRExpr list option =
        if rebinds.ContainsKey id then None
        else
            match lets.TryGetValue id with
            | true, v ->
                (match snd (IRLift.peelLetChain v) with
                 | IRTuple es when not es.IsEmpty -> Some es
                 | _ -> None)
            | _ -> None
    let rec storageAt (seen: System.Collections.Generic.HashSet<IRId * (int * bool) list>) (fuel: int) (owner: IRCallable option) (path: (int * bool) list) (e: IRExpr) : Set<IRId> option =
        if fuel <= 0 then None
        else
            let allAt (p: (int * bool) list) (xs: IRExpr list) =
                xs |> List.fold (fun acc x ->
                    match acc, storageAt seen (fuel - 1) owner p x with
                    | Some a, Some b -> Some (Set.union a b)
                    | _ -> None) (Some Set.empty)
            let all = allAt []
            match e with
            // a binder already being followed along this path adds nothing new
            // (the walk is a union): a cyclic rebinding ends here, not at fuel
            | IRVar (id, _) when strictMode.Value
                                 && (knownCarrierMembers id).IsSome
                                 && seen.Add((id, path)) ->
                let es = (knownCarrierMembers id).Value
                let memberAt (k: int) (rest: (int * bool) list) =
                    storageAt seen (fuel - 1) owner rest es.[k] |> Option.map (Set.add (memberIdOf id k))
                (match path with
                 | (k, false) :: rest when k >= 0 && k < es.Length -> memberAt k rest
                 // the whole carrier, or a FLAT leaf path (a destructuring
                 // let (a, b, tag) = pair names leaves of nested members):
                 // the carrier and EVERY member identity, so it overlaps any
                 // structural path into the same carrier (Codex 03cfa2dd,
                 // finding 2)
                 | _ ->
                     es |> List.indexed |> List.fold (fun acc (k, _) ->
                         match acc, memberAt k [] with
                         | Some a, Some b -> Some (Set.union a b)
                         | _ -> None) (Some (Set.singleton id)))            | IRVar (id, _) when (exprHoldsStorage e || deferred.Contains id) && not (seen.Add((id, path))) -> Some (Set.singleton id)
            | IRVar (id, _) when exprHoldsStorage e || deferred.Contains id ->
                let viaInit = initStorageAt seen fuel owner path id
                let viaRebinds =
                    match rebinds.TryGetValue id with
                    | true, rs ->
                        allAt path (List.ofSeq rs)
                    | _ -> Some Set.empty
                (match viaInit, viaRebinds with
                 | Some a, Some b -> Some (Set.add id (Set.union a b))
                 | _ -> None)
            | IRVar _ -> Some Set.empty
            | IRParam (nm, _, _) ->
                (match owner |> Option.bind (fun c -> c.Params |> List.tryFind (fun p -> p.Name = nm)) with
                 | Some p -> Some (Set.singleton p.VarId)
                 | None -> Some Set.empty)
            | IRTupleProj (a, k, isFlat) -> storageAt seen (fuel - 1) owner ((k, isFlat) :: path) a
            | IRTuple es ->
                (match path with
                 | (k, false) :: rest when k >= 0 && k < es.Length -> storageAt seen (fuel - 1) owner rest es.[k]
                 // a FLAT leaf index selects precisely only over leaves that
                 // are not themselves tuples; otherwise every member
                 | (k, true) :: rest when k >= 0 && k < es.Length
                                         && es |> List.forall (fun m -> match (try typeOf m with _ -> IRTUnit) with IRTTuple _ -> false | _ -> true) ->
                     storageAt seen (fuel - 1) owner rest es.[k]
                 // the whole carrier: every member that can hold storage (a
                 // scalar member -- `m(0, 1)` -- is a value read, no view)
                 | _ -> all (es |> List.filter exprHoldsStorage))
            | IRStructLit (_, fields) -> all (fields |> List.map snd |> List.filter exprHoldsStorage)
            | IRIndex (a, _, _) | IRCurry (a, _, _) | IRSlice (a, _, _, _) | IRFieldAccess (a, _) ->
                storageAt seen (fuel - 1) owner [] a
            | IRIf (_, t, f) -> allAt path [ t; f ]
            | IRLet _ -> storageAt seen (fuel - 1) owner path (snd (IRLift.peelLetChain e))
            // a call returns (a view of) a storage-carrying argument, and an
            // APPLIED storage-carrying value (`row(1)` over a computation-typed
            // `row`) is a read of it
            //
            // A RESOLVED callee is read through its RETURN SUMMARY (G190 (c),
            // Codex design review of C4): exactly the arguments whose
            // parameters the return value may share, plus every binder its
            // body's return names beyond its parameters (a module array it
            // returns -- `function get() -> A = a` --, a capture). A callee
            // whose summary is unknown (in progress through recursion, or
            // untraced) keeps the conservative reading: every argument.
            | IRAppAt (f, args, _, _) ->
                let conservative () = all ((if exprHoldsStorage f then [ f ] else []) @ (args |> List.filter exprHoldsStorage))
                // STRICT MODE (G250 / Codex probe E): a storage-holding result of
                // a call through a FUNCTION VALUE (a closure binder, a callback
                // parameter, an aggregate element) may be any array its closure
                // reaches -- not only its arguments -- and an unknown return
                // summary is unknown: None
                let strictUnknown () = strictMode.Value && exprHoldsStorage e
                // unknown BY DEFAULT (Codex diff review 5f64f352 finding 2: a
                // call-valued head `factory(g)(1.0)`): only an intrinsic /
                // constructor head (an IRParam that is no parameter of the
                // owner) and an APPLIED ARRAY (`row(1)`, a read) keep the
                // argument reading
                let functionValueHead =
                    match f with
                    | IRParam (nm, _, _) -> (match owner with Some c -> c.Params |> List.exists (fun p -> p.Name = nm) | None -> false)
                    | _ -> not (try isArrayTy (typeOf f) with _ -> false)
                (match resolve f with
                 | Some c ->
                     (match calleeReturnSummary c with
                      | Some (positions, others) ->
                          let picked =
                              args |> List.indexed
                              |> List.filter (fun (i, a) -> positions.Contains i && exprHoldsStorage a)
                              |> List.map snd
                          all picked |> Option.map (Set.union others)
                      | None -> if strictUnknown () then None else conservative ())
                 | None ->
                     if strictUnknown () && functionValueHead then None
                     // CAPTURE MODE (D9): a call through a function-valued NAME
                     // reaches one of the program's value-used callables, so its
                     // result shares what one of THEIR return summaries says (a
                     // read-only callback returning fresh cells shares nothing);
                     // an unknown summary keeps the conservative reading
                     elif captureMode.Value && functionValueHead && not (exprHoldsStorage f)
                          && (match f with IRVar _ | IRParam _ -> true | _ -> false) then
                         // (per call site since R12: the callables that can
                         // reach a function-valued PARAMETER, fnValueCandidates)
                         let summaries =
                             fnValueCandidates owner f (Some args.Length)
                             |> List.map calleeReturnSummary
                         if summaries |> List.exists Option.isNone then conservative ()
                         else
                             summaries |> List.fold (fun acc s ->
                                 match acc, s with
                                 | Some a, Some (positions, others) ->
                                     let picked =
                                         args |> List.indexed
                                         |> List.filter (fun (i, a) -> positions.Contains i && exprHoldsStorage a)
                                         |> List.map snd
                                     all picked |> Option.map (fun ids -> Set.union a (Set.union ids others))
                                 | _ -> None) (Some Set.empty)
                     else conservative ())
            | IRTupleCons (h, t) -> all [ h; t ]
            | IRTupleDecons t -> all [ t ]
            // a match VALUE: every case body along the path, as an if's
            // arms (Codex review of W4B-A, finding 2: it used to be "fresh")
            | IRMatch (_, cases) -> allAt path (cases |> List.map (fun c -> c.Body))
            // forms that may hand back (a view of) an operand's storage and
            // have no proved fresh-copy emission in every lane (residual
            // view nodes, pack projections): their operands, conservatively
            | IRSubset (a, _, _, _) | IRReverse (a, _) | IRShift (a, _, _, _) | IRDiag a
            | IRPolyIndex (a, _) | IRPolyTail (a, _) | IRCompoundProject (a, _) ->
                storageAt seen (fuel - 1) owner [] a
            // `|> compute` of a DEFERRED computation (a map, a deferred binder)
            // or of any computing form (a guard, a transpose, a sequence, ...)
            // materializes FRESH cells (third Codex look of W4B-A, finding 1:
            // `guard(true, g) |> compute` is its own pool); of a stored VIEW or
            // name it is the identity in the compiled lanes (measured: a write
            // through `mc(0) |> compute` lands in mc in C++ and LLVM), so that
            // operand is followed
            | IRCompute x ->
                let rec strip (e: IRExpr) = match e with IRCompute y -> strip y | e -> e
                (match strip x with
                 | IRVar (id, _) when deferred.Contains id -> Some Set.empty
                 | (IRVar _ | IRParam _ | IRIndex _ | IRCurry _ | IRSlice _ | IRTupleProj _ | IRFieldAccess _ | IRPure _) as c ->
                     storageAt seen (fuel - 1) owner path c
                 | _ -> Some Set.empty)
            // IDENTITY WRAPPERS (second Codex look, finding 3): `pure(x)` is the
            // identity in every lane; a bare `guard` / `<|>` / `<|:>` / bind
            // VALUE stands for its operand(s) (as a named binding it is a
            // deferred binder anyway) -- followed
            | IRPure a | IRGuard (_, a) | IRBind (a, _) -> storageAt seen (fuel - 1) owner path a
            | IRChoice (l, r) | IRFallback (l, r) -> allAt path [ l; r ]
            // STRICT MODE (G250 (4)): an array literal is a fresh container,
            // but what its ELEMENTS hold (a tuple member `(identity(backing),
            // 0)`, an array row) is followed -- fail closed
            | IRArrayLit (es, _) when strictMode.Value -> all (es |> List.filter exprHoldsStorage)
            // FRESH: a computed value (the result's own new cells) or no
            // storage at all
            | IRLit _ | IRArrayLit _ | IRBinOp _ | IRUnaryOp _ | IRComplex _ | IRFma _ | IRRandAt _
            | IRMethodFor _ | IRObjectFor _ | IRApplyCombinator _ | IRComposeApply _ | IRParallel _
            | IRFusion _ | IRArrayProduct _ | IRComposeObj _ | IRComposeMeth _
            | IRCompose _ | IRFunctorMap _ | IRReynolds _
            // copies into their own pool in the C++ lane and the interpreter
            // (transpose in all three lanes); a sequence / replicate copies in C++
            // (the interpreter's concrete-array sequence SHARES its members' stores:
            // a pre-existing cross-lane divergence, recorded in G60, not refused here)
            | IRTranspose _ | IRDecompact _ | IRGroupBy _ | IRUngroup _ | IRUngroupGrid _ | IRStack _ | IRJoin _
            | IRUngroupRows _ | IRSequence _ | IRReplicate _
            | IRMask _ | IRIntersect _ | IRUnion _ | IRUnique _ | IRContains _ | IRDisplayEmit _ | IRDisplayJson _
            | IRDisplayNum _ | IRDisplayStr _ | IRGroupKeys _ | IRGroupBucket _ | IRSegments _ | IRSegmentsGrid _
            | IRGroupSizes _ | IRSort _ | IRReduce _ | IRReduceCompute _ | IRProdSum _ | IRZip _ | IRAlign _
            | IRGram _ | IRGramApply _ | IRMatmul _ | IREigh _ | IRSolve _ | IRLu _ | IRLuSolve _
            | IRArrayNegate _ | IRArrayConjugate _ | IRRange _ | IRVirtualReverse _ | IRHaloUnhash _ | IRArity _
            | IRNth | IRZero _ | IRUnseededSeed _ | IRUninit _ | IRBeginPoolScope _ | IRReleasePools _ | IRRank _ | IRExtent _
            | IRRaggedLookup _ | IRCompoundMask _ | IRSparseKeys _ | IROrbitClass _ | IROpaqueExtent
            | IRAssign _ | IRForRange _ | IRConstraintCheck _ | IRBreakIf _ | IRReplayRow _ ->
                Some Set.empty
            // (EXHAUSTIVE over IRExpr, no wildcard: a form added later is an
            // incomplete-match warning here until it is classified, never
            // silently "fresh")
    // What a name's INITIALIZER shares (its rebindings aside): none for a
    // proved copy; a match pattern variable's part of its scrutinee.
    and initStorageAt (seen: System.Collections.Generic.HashSet<IRId * (int * bool) list>) (fuel: int) (owner: IRCallable option) (path: (int * bool) list) (id: IRId) : Set<IRId> option =
        match lets.TryGetValue id with
        | true, v when path.IsEmpty && isProvedCopy id v -> Some Set.empty
        | true, v -> storageAt seen (fuel - 1) owner path v
        | _ ->
            // a match PATTERN variable: the part of the scrutinee
            // its pattern binds (finding 2's pattern-bound aliases)
            match patBinds.TryGetValue id with
            | true, (scrut, ppath) -> storageAt seen (fuel - 1) owner (ppath @ path) scrut
            | _ -> Some Set.empty
    // The RETURN SUMMARY of a callable: (the argument positions whose storage
    // the return value may share, every other binder it may share -- a module
    // array, a capture, a callee-local let), from its body's own storage walk;
    // None while it is being computed (recursion) or when the walk runs out.
    and calleeReturnSummary (c: IRCallable) : (Set<int> * Set<IRId>) option =
        let key = (strictMode.Value, captureMode.Value, c.Id)
        match returnSummaries.TryGetValue key with
        | true, s -> s
        | _ ->
            returnSummaries.[key] <- None
            let s =
                if not (exprHoldsStorage c.Body) then Some (Set.empty, Set.empty)
                else
                    match storageAt (System.Collections.Generic.HashSet<IRId * (int * bool) list>()) 48 (Some c) [] c.Body with
                    | Some ids ->
                        let paramIds = c.Params |> List.mapi (fun i p -> (p.VarId, i)) |> Map.ofList
                        let positions = ids |> Set.toList |> List.choose (fun id -> Map.tryFind id paramIds) |> Set.ofList
                        // the callee's OWN lets are fresh per call (an alias of
                        // a parameter or of an outer binder was followed to it
                        // already): exported, two calls of one allocating
                        // helper would look like one storage (Codex diff
                        // review round 2)
                        let ownLets = System.Collections.Generic.HashSet<IRId>()
                        iterIRExpr (fun n -> match n with IRLet (id, _, _) -> ownLets.Add id |> ignore | _ -> ()) c.Body
                        // (a member identity of an own carrier is own too: G240 (2),
                        // Codex 03cfa2dd finding 4)
                        let others = ids |> Set.filter (fun id -> not (paramIds.ContainsKey id) && not (ownLets.Contains id) && not (match memberCarrier.TryGetValue id with | true, cid -> ownLets.Contains cid | _ -> false))
                        Some (positions, others)
                    | None -> None
            returnSummaries.[key] <- s
            s
    let storageOf (fuel: int) (owner: IRCallable option) (e: IRExpr) =
        storageAt (System.Collections.Generic.HashSet<IRId * (int * bool) list>()) fuel owner [] e
    // the STRICT walk of the mut-argument rule (G240 / G250)
    let storageOfStrict (fuel: int) (owner: IRCallable option) (e: IRExpr) =
        let saved = strictMode.Value
        strictMode.Value <- true
        try storageOf fuel owner e
        finally strictMode.Value <- saved
    // the CAPTURE walk of the D9 rule (captureMode above)
    let storageOfCapture (fuel: int) (owner: IRCallable option) (e: IRExpr) =
        let saved = captureMode.Value
        captureMode.Value <- true
        try storageOf fuel owner e
        finally captureMode.Value <- saved
    // ---- WHO MAY WRITE A PLAIN `let`, the STORAGE half (deep review D9, the
    // user's ruling of 2026-11-14; formalism 2.7) --------------------------------
    // A function, lambda or kernel writes a binding it only CAPTURES when that
    // binding was declared `let mut`. The type checker refuses the direct store
    // (TypeCheckSupport.assignTargetError, rule 4: the target's root IS the
    // captured binding). A store that REACHES one -- through a local view
    // (`let t = G(0); t(1) = ..`), an alias, a tuple or struct member, a
    // rebinding, an `if` value, a callee's `mut` parameter or a callback -- is
    // judged here, by the walk that already follows every such path for the
    // deferred-write rule below (fail closed: an untraceable target is refused).
    // Captured = not a parameter or a `let` of the writing callable itself;
    // module-level code (no owner) writes its own bindings freely. A deferred
    // binder is left to the deferred-write refusal, a compiler-made (`__`)
    // module binding is exempt, and a local `let` is judged only when it holds
    // an ARRAY. `let mut` is IRModule.LetMutBinders (IsMutable / MutableArrayLets
    // say assignable, which a plain `let` is too).
    let letMutBinders = modules |> List.fold (fun acc m -> Set.union acc m.LetMutBinders) Set.empty
    let ownBinders = System.Collections.Generic.Dictionary<IRId, System.Collections.Generic.HashSet<IRId>>()
    let ownedBy (c: IRCallable) (id: IRId) : bool =
        let own =
            match ownBinders.TryGetValue c.Id with
            | true, s -> s
            | _ ->
                let s = System.Collections.Generic.HashSet<IRId>()
                for p in c.Params do s.Add p.VarId |> ignore
                iterIRExpr (fun n -> match n with IRLet (b, _, _) -> s.Add b |> ignore | _ -> ()) c.Body
                ownBinders.[c.Id] <- s
                s
        own.Contains id
    // A NON-`mut` PARAMETER is a protected root too (deep review R12, defect
    // 1): `function touch(d: D) = { let mut v = d(0); v(1) = 7 }` writes the
    // caller's array exactly as the direct `d(0, 1) = 7` the type checker
    // refuses (parameters bind read-only unless declared `mut`). The root is
    // judged for its own callable and for every callable that captures it
    // (a lambda in its body), by the same walk and the same sharing forms.
    // `mut` positions are mutParamPositionsOf's (an eta wrapper of a named
    // function forwards that function's).
    let mutPositionCache = System.Collections.Generic.Dictionary<IRId, Set<int>>()
    let mutPositions (c: IRCallable) =
        match mutPositionCache.TryGetValue c.Id with
        | true, s -> s
        | _ ->
            let s = mutParamPositionsOf origins funcs resolve c |> Set.ofList
            mutPositionCache.[c.Id] <- s
            s
    let protectedParam (id: IRId) : (string * string) option =
        match paramOwners.TryGetValue id with
        | true, (c, i) when not ((mutPositions c).Contains i) ->
            let p = c.Params.[i]
            let isFunctionTyped = (match stripUnits p.Type with FuncElem _ -> true | _ -> false)
            if isFunctionTyped then None
            elif c.Name.StartsWith "__lambda" then
                Some ($"the lambda or kernel parameter `{p.Name}`, which is read-only (a lambda declares no `mut` parameter)",
                      $"take a copy first (`let mut c = {p.Name}`) and write the copy, or hand the array to a named function whose parameter is `mut`")
            else
                let fname = if System.String.IsNullOrEmpty c.SourceName then c.Name else c.SourceName
                Some ($"the parameter `{p.Name}` of `{fname}`, which is not `mut` (a parameter is read-only unless declared `mut`)",
                      $"declare it `{p.Name}: mut ...` if the caller's array is meant to change, or take a copy first (`let mut c = {p.Name}`) and write the copy")
        | _ -> None
    let capturedPlainLet (owner: IRCallable option) (id: IRId) : string option =
        match owner with
        | None -> None
        | Some c ->
            if deferred.Contains id || ownedBy c id then None
            else
                match bindingById.TryFind id with
                | Some b ->
                    if letMutBinders.Contains id || b.Name.StartsWith "__" || not (holdsStorage b.Type) then None
                    else Some $"`{b.Name}`"
                | None ->
                    match lets.TryGetValue id with
                    | true, v when not (letMutBinders.Contains id) && typeIsArray v ->
                        Some "an array bound by a plain `let` of an enclosing function or block"
                    | _ -> None
    // ---- WHAT A LOCAL HOLDS AT A WRITE (deep review R12, defect 4) ------------
    // A whole `v = e` rebinds the name (formalism 2.7): after it, `v` no longer
    // shares what it held before. The shared walk is flow-insensitive (a name
    // that EVER held a view is judged as one at every later store), so the
    // capture judgment reads, at each write / call / map node of a callable, the
    // definitions of the callable's own lets that may REACH it: the initializer
    // and the rebindings on some path from the let to the node. Straight-line
    // code kills (`let t = G(0); t = [..]; t(0) = 9.0` writes the fresh array);
    // the arms of an `if` / `match` join; any other form (a loop body, an
    // operand of `&&`, a kernel slot) is ONE region whose rebindings all reach
    // every node inside it and everything after it (no kill across it, so a
    // repeated body sees its own later rebindings). A let is tracked only when
    // no OTHER callable rebinds it (a lambda that rebinds a captured `let mut`
    // may run at any of its calls); a node reached twice in the tree (a shared
    // subtree) has no state. Only the root name of the target is read this way
    // (`t(0)`, `t[1](0)`): every other name it reaches keeps the flow-
    // insensitive reading, which is what an alias bound BEFORE the rebinding
    // shares (`let w = t; t = [..]; w(0) = ..` still writes G).
    let rebindOwners = System.Collections.Generic.Dictionary<IRId, System.Collections.Generic.HashSet<IRId>>()
    let noteRebindOwner (ownerKey: IRId) (e: IRExpr) =
        for r in withExtents e do
            iterIRExpr (fun n ->
                match n with
                | IRAssign (target, _) when (installedName target).IsSome ->
                    let id = (installedName target).Value
                    (match rebindOwners.TryGetValue id with
                     | true, s -> s.Add ownerKey |> ignore
                     | _ -> rebindOwners.[id] <- System.Collections.Generic.HashSet<IRId> [ ownerKey ])
                | _ -> ()) r
    // (module-level code and side payloads under a key no callable has)
    let moduleKey : IRId = System.Int32.MinValue
    for b in allBindings do noteRebindOwner moduleKey b.Value
    for f in allFuncs do noteRebindOwner f.Id f.Body
    part "setup: walk"
    for (_, e) in sidePayloads do noteRebindOwner moduleKey e
    part "setup: rebind owners"
    let flowStates = System.Collections.Generic.Dictionary<IRId, System.Collections.Generic.Dictionary<IRExpr, Map<IRId, bool * IRExpr list> option>>()
    let statesOf (c: IRCallable) =
        match flowStates.TryGetValue c.Id with
        | true, d -> d
        | _ ->
            let d = System.Collections.Generic.Dictionary<IRExpr, Map<IRId, bool * IRExpr list> option>(HashIdentity.Reference)
            flowStates.[c.Id] <- d
            // a name (re)bound inside an expression a TYPE carries (a range
            // bound) is never tracked: those expressions are not children of
            // the tree the flow walks, so their order is unknown here
            let extentAssigned = System.Collections.Generic.HashSet<IRId>()
            for r in Optimize.embeddedExtentExprs c.Body @ (c.Params |> List.collect (fun p -> Optimize.embeddedExtentExprs (IRVar (p.VarId, p.Type)))) do
                iterIRExpr (fun n ->
                    match n with
                    | IRAssign (target, _) -> (match installedName target with Some id -> extentAssigned.Add id |> ignore | None -> ())
                    | _ -> ()) r
            let onlyHere (id: IRId) =
                not (extentAssigned.Contains id)
                && (match rebindOwners.TryGetValue id with
                    | true, s -> s.Count = 1 && s.Contains c.Id
                    | _ -> false)
            // nothing to kill without a rebinding of a tracked let
            let anyKill =
                let found = ref false
                iterIRExpr (fun n ->
                    match n with
                    | IRAssign (IRVar (id, _), _) when ownedBy c id && onlyHere id -> found.Value <- true
                    | _ -> ()) c.Body
                found.Value
            if anyKill then
                // the rebindings of tracked lets anywhere inside a node (memoized)
                let inner = System.Collections.Generic.Dictionary<IRExpr, (IRId * IRExpr) list>(HashIdentity.Reference)
                let rec rebindsIn (e: IRExpr) : (IRId * IRExpr) list =
                    match inner.TryGetValue e with
                    | true, l -> l
                    | _ ->
                        let own =
                            match e with
                            | IRAssign (target, rhs) ->
                                (match installedName target with
                                 | Some id when onlyHere id -> [ (id, rhs) ]
                                 | _ -> [])
                            | _ -> []
                        let l = own @ (childrenOf e |> List.collect rebindsIn)
                        inner.[e] <- l
                        l
                let addDefs (s: Map<IRId, bool * IRExpr list>) (adds: (IRId * IRExpr) list) =
                    adds |> List.fold (fun (acc: Map<IRId, bool * IRExpr list>) (id, rhs) ->
                        match acc.TryFind id with
                        | Some (init, rs) when not (rs |> List.exists (fun r -> obj.ReferenceEquals(r, rhs))) -> acc.Add(id, (init, rhs :: rs))
                        | _ -> acc) s
                let join (a: Map<IRId, bool * IRExpr list>) (b: Map<IRId, bool * IRExpr list>) =
                    b |> Map.fold (fun (acc: Map<IRId, bool * IRExpr list>) id (init, rs) ->
                        match acc.TryFind id with
                        | Some (init0, rs0) ->
                            let merged = rs |> List.fold (fun l r -> if l |> List.exists (fun x -> obj.ReferenceEquals(x, r)) then l else r :: l) rs0
                            acc.Add(id, (init0 || init, merged))
                        | None -> acc.Add(id, (init, rs))) a
                let record (n: IRExpr) (s: Map<IRId, bool * IRExpr list>) =
                    if d.ContainsKey n then d.[n] <- None else d.[n] <- Some s
                let rec flow (s: Map<IRId, bool * IRExpr list>) (e: IRExpr) : Map<IRId, bool * IRExpr list> =
                    match e with
                    | IRLet (id, v, b) ->
                        let s1 = flow s v
                        let s2 = if onlyHere id || not (rebindOwners.ContainsKey id) then s1.Add(id, (true, [])) else s1
                        flow s2 b
                    | IRSequence es -> es |> List.fold flow s
                    | IRIf (cond, t, f) ->
                        let s1 = flow s cond
                        join (flow s1 t) (flow s1 f)
                    | IRMatch (scrut, cases) ->
                        let s1 = flow s scrut
                        // a guard may run and fail before a later case: every
                        // guard's rebindings reach every case
                        let s1 = addDefs s1 (cases |> List.collect (fun cs -> match cs.Guard with Some g -> rebindsIn g | None -> []))
                        (match cases with
                         | [] -> s1
                         | _ ->
                             cases
                             |> List.map (fun cs ->
                                 (match cs.Guard with Some g -> flow s1 g |> ignore | None -> ())
                                 flow s1 cs.Body)
                             |> List.reduce join)
                    | IRAssign (IRVar (id, _), rhs) when s.ContainsKey id && onlyHere id ->
                        let s1 = flow s rhs
                        s1.Add(id, (false, [ rhs ]))
                    | _ ->
                        let sIn = addDefs s (rebindsIn e)
                        (match e with
                         | IRAssign _ | IRApp _ | IRApplyCombinator _ -> record e sIn
                         | _ -> ())
                        for ch in childrenOf e do flow sIn ch |> ignore
                        sIn
                flow Map.empty c.Body |> ignore
            d
    // The storage the capture judgment reads for a write target (or a call /
    // map argument) at `node`: the root name's reaching definitions when the
    // flow above has them, else the capture walk.
    let judgedStorage (owner: IRCallable option) (node: IRExpr) (e: IRExpr) : Set<IRId> option =
        let normal () = storageOfCapture 48 owner e
        let state =
            match owner with
            | Some c ->
                (match (statesOf c).TryGetValue node with
                 | true, s -> s
                 | _ -> None)
            | None -> None
        match state with
        | None -> normal ()
        | Some s ->
            let rec peel (x: IRExpr) (path: (int * bool) list) =
                match x with
                | IRIndex (a, _, _) | IRCurry (a, _, _) | IRSlice (a, _, _, _) | IRFieldAccess (a, _) -> peel a []
                | IRTupleProj (a, k, flat) -> peel a ((k, flat) :: path)
                | IRVar (id, _) when exprHoldsStorage x && not (deferred.Contains id) -> Some (id, path)
                | _ -> None
            match peel e [] with
            | Some (id, path) when s.ContainsKey id ->
                let (init, rhs) = s.[id]
                let saved = captureMode.Value
                captureMode.Value <- true
                try
                    let seen = System.Collections.Generic.HashSet<IRId * (int * bool) list>()
                    let viaInit = if init then initStorageAt seen 48 owner path id else Some Set.empty
                    rhs |> List.fold (fun acc r ->
                        match acc, storageAt seen 47 owner path r with
                        | Some a, Some b -> Some (Set.union a b)
                        | _ -> None) viaInit
                    |> Option.map (Set.add id)
                finally captureMode.Value <- saved
            | _ -> normal ()
    let written = System.Collections.Generic.HashSet<IRId>()
    let writeSite = System.Collections.Generic.Dictionary<IRId, Blade.Ast.Span>()
    let changed = ref true
    // `judgeCapture` is false for a whole `v = e` (any `v`: the callable's
    // own local or a captured `let mut`). That REBINDS the name (the old
    // array keeps its cells: measured in every lane), so it writes no storage
    // `v` reached -- the type checker judges the name itself (rule 4); it
    // still joins the deferred-write fixpoint as before. The capture judgment
    // reads the CAPTURE walk (compact renames are copies, `storageOfCapture`);
    // the fixpoint keeps the shared walk. Both are flow-insensitive -- a name
    // that EVER holds a view of a captured plain `let` is judged as one at
    // every later store through it.
    // (the first protected-parameter refusal, raised at the end of this pass)
    let pendingParamRefusal : Blade.Diagnostics.Diagnostic option ref = ref None
    // THE JUDGED WRITES (R12): the binders a JUDGED store may reach (the
    // capture walk's reading). `written` is the deferred-write fixpoint's
    // superset -- it also takes every array handed to a call the capture
    // judgment proved writes nothing (an intrinsic, a kernel or callback that
    // writes no parameter) and every whole rebinding -- so a callee's
    // parameter in `written` alone does not make the caller's argument a
    // write; one in `judgedWritten` does.
    let judgedWritten = System.Collections.Generic.HashSet<IRId>()
    let markWith (judgeCapture: bool) (span: Blade.Ast.Span) (owner: IRCallable option) (node: IRExpr) (e: IRExpr) =
        match storageOf 48 owner e with
        | Some ids ->
            if judgeCapture then
                let judged = judgedStorage owner node e |> Option.defaultValue ids
                for id in judged do
                    if judgedWritten.Add id then changed.Value <- true
                for id in judged do
                    (match protectedParam id with
                     | Some (what, remedy) when pendingParamRefusal.Value.IsNone ->
                         pendingParamRefusal.Value <-
                             Some (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints span
                                     ($"a write through a view, an alias or a `mut` argument reaches {what}: it would change the array the caller passed (formalism 2.7). To write it, {remedy}."))
                     | _ -> ())
                    match capturedPlainLet owner id with
                    | Some what ->
                        raise (Blade.Diagnostics.BladeDiagnosticException
                                (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints span
                                    ($"a write reaches {what} through a view, an alias or a `mut` argument, from a function, lambda or kernel that only captures it: a captured binding may be written only when it was declared `let mut` (formalism 2.7). Declare it `let mut`, or take a copy first (`let mut c = <it>`) and write the copy.")))
                    | None -> ()
            for id in ids do
                if written.Add id then
                    writeSite.[id] <- span
                    changed.Value <- true
        | None ->
            raise (Blade.Diagnostics.BladeDiagnosticException
                    (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints span
                        "a write whose target's storage could not be traced (through a storage-carrying form the provenance walk does not know, or more than 48 nested view, alias, projection or rebinding steps; a cycle of rebindings is followed once, not counted): it may write a deferred computation, which has no storage of its own (formalism 10.2). Bind the array the write is meant for with `|> compute` and write that name directly."))
    let mark = markWith true
    // A map / composition applying kernel `k` to `arrays`: an operand is a
    // judged write only when ITS parameter was (R12, Codex review: `method_for(a,
    // b) <@> poke` with `poke(a: mut R, b: R)` writes a, not b); a multi-slot
    // operand list that does not line up with the parameters judges every
    // operand. The deferred-write fixpoint keeps its reading (every operand
    // when any parameter is in `written`).
    let markKernelOperands (span: Blade.Ast.Span) (owner: IRCallable option) (n: IRExpr) (k: IRCallable) (arrays: IRExpr list) =
        let anyWritten = k.Params |> List.exists (fun p -> written.Contains p.VarId)
        if k.Params.Length = arrays.Length then
            List.zip k.Params arrays |> List.iter (fun (p, a) ->
                if judgedWritten.Contains p.VarId then mark span owner n a
                elif anyWritten then markWith false span owner n a)
        elif k.Params |> List.exists (fun p -> judgedWritten.Contains p.VarId) then
            arrays |> List.iter (mark span owner n)
        elif anyWritten then
            arrays |> List.iter (markWith false span owner n)
    // a composition value through let / binding aliases, and its stage kernels
    let rec compositionOfLocal (seen: Set<IRId>) (e: IRExpr) : IRExpr option =
        match e with
        | IRComposeObj _ | IRObjectFor _ -> Some e
        | IRVar (id, _) when not (seen.Contains id) ->
            (match lets.TryGetValue id with
             | true, v -> compositionOfLocal (seen.Add id) v
             | _ -> None)
        | _ -> None
    let rec stagesOfLocal (e: IRExpr) : IRExpr list =
        match e with
        | IRComposeObj (l, r) -> stagesOfLocal l @ stagesOfLocal r
        | IRObjectFor oi -> [ oi.Kernel ]
        | _ -> []
    // Every site, with its owner (the callable whose parameters an IRParam
    // names) and the span a refusal reports.
    let sites =
        [ for b in allBindings -> (IR.declSpanOf b.Name, None, b.Value)
          for f in allFuncs -> (funcSpan f, Some f, f.Body) ]
    let paramNamesOf (owner: IRCallable option) =
        match owner with
        | Some c -> c.Params |> List.map (fun p -> p.Name) |> Set.ofList
        | None -> Set.empty
    part "setup: sites and spans"
    while changed.Value do
        changed.Value <- false
        // (the expressions types carry too: a store inside a range bound is a
        // store -- R12, Codex review)
        for (span, owner, root) in sites do
            let ownParams = paramNamesOf owner
            iterIRExpr (fun n ->
                match n with
                | IRAssign (target, _) ->
                    let wholeRebind = match target with IRVar _ -> true | _ -> false
                    markWith (not wholeRebind) span owner n target

                | IRApp (f, args, _) ->
                    (match resolve f with
                     | Some c ->
                         List.zip (List.truncate args.Length c.Params) (List.truncate c.Params.Length args)
                         |> List.iter (fun (p, a) ->
                             if judgedWritten.Contains p.VarId then mark span owner n a
                             elif written.Contains p.VarId then markWith false span owner n a)
                     | None ->
                         // a FUNCTION VALUE (a callback parameter, a function-
                         // typed name): its effects are unknown, so every array
                         // it receives may be written. An intrinsic or a
                         // constructor (an IRParam head that is no parameter of
                         // this callable) writes nothing.
                         let unknownCallee =
                             match f with
                             | IRVar _ -> true
                             | IRParam (nm, _, _) -> ownParams.Contains nm
                             | _ -> true
                         if unknownCallee then
                             // D9's capture judgment only: an `object_for(k)`
                             // head (`exp <@> e`) runs k over the arrays' cells,
                             // so it writes what k writes -- nothing for an
                             // intrinsic (an IRParam that is no parameter of
                             // this callable), what a resolved k's parameters
                             // are written for one. Every argument still joins
                             // the deferred-write fixpoint, as before.
                             let judgeArgs =
                                 match f with
                                 | IRObjectFor info ->
                                     (match resolve info.Kernel with
                                      | Some k -> k.Params |> List.exists (fun p -> judgedWritten.Contains p.VarId)
                                      | None ->
                                          match info.Kernel with
                                          | IRParam (nm, _, _) -> ownParams.Contains nm
                                          | _ -> true)
                                 // a call through a function value writes what one
                                 // of the program's value-used callables writes:
                                 // judged only when one of them writes a parameter
                                 // (per call site since R12: fnValueCandidates)
                                 | _ ->
                                     fnValueCandidates owner f None |> List.exists (fun c ->
                                         c.Params |> List.exists (fun p -> judgedWritten.Contains p.VarId))
                             // (a tuple / struct carrying arrays too: finding 2)
                             args |> List.iter (fun a -> if exprHoldsStorage a then markWith judgeArgs span owner n a))
                | IRApplyCombinator info ->
                    (match resolve info.Kernel with
                     | Some k -> markKernelOperands span owner n k info.Arrays
                     | _ -> ())
                // an APPLIED composition: its FIRST stage receives the applied
                // arrays (R12, Codex review: `p <@> v` with `p = object_for(poke)
                // >>@ ..` writes v through poke's `mut` parameter)
                | IRComposeApply info ->
                    (match compositionOfLocal Set.empty info.Composition |> Option.map stagesOfLocal with
                     | Some (k0 :: _) ->
                         (match resolve k0 with
                          | Some k -> markKernelOperands span owner n k info.InputArrays
                          | None -> ())
                     | _ -> ())
                | _ -> ()) (IRSequence (withExtents root))
    for id in written do
        if deferred.Contains id then
            let (what, remedy) = describe id
            raise (Blade.Diagnostics.BladeDiagnosticException
                    (Blade.Diagnostics.mkError "BL4005" Blade.Diagnostics.PhConstraints writeSite.[id]
                        ($"a write through {what}, a deferred computation: a map is evaluated where it is read and fused into the folds over it (formalism 10.2), so it has no storage of its own to write -- the write would leave its folds, its prints and its aliases disagreeing. Materialize it first: {remedy} (for a writable copy, `let mut c = <it> |> compute`) and write that.")))
    // the side tables' payloads are call sites too (a random fill's
    // parameter, a compound / sparse operand: Codex diff review round 2)
    let sideSites = sidePayloads |> List.map (fun (b, e) -> (IR.declSpanOf b.Name, None, e))
    part "write fixpoint"
    let paramCands (a: IRExpr) : IRCallable list option =
        match a with
        | IRVar _ | IRParam _ when (try (match stripUnits (typeOf a) with FuncElem _ -> true | _ -> false) with _ -> false) ->
            (match paramOfHead None a with
             | Some (c, k) -> paramCandidatesFrom Set.empty c k
             | None -> None)
        | _ -> None
    refuseAliasedMutArgumentsWith origins allBindings allFuncs funcs lets deferred resolve storageOfStrict exprHoldsStorage paramCands (sites @ sideSites)
    part "mut arguments"
    // ---- if values over computed arrays -----------------------------------------
    let rec stripC (e: IRExpr) = match e with IRCompute x -> stripC x | e -> e
    // The deferred binders a piece of an arm READS: its references plus the
    // captures of every callable it names (a kernel's capture is a force).
    let readsOf (n: IRExpr) : IRId list =
        let refs = collectVarRefsIR n
        let all =
            refs |> Set.fold (fun acc id ->
                match Map.tryFind id funcs with
                | Some c -> c.Captures |> List.fold (fun a ci -> Set.add ci.Id a) acc
                | None -> acc) refs
        Set.intersect all deferred |> Set.toList
    // To the alias chain's end, no depth cutoff (gap G59); the visited set
    // only guards it (an initializer names only earlier binders).
    let rec computationOf (seen: Set<IRId>) (id: IRId) =
        match lets.TryGetValue id with
        | true, IRVar (src, _) when not (seen.Contains src) && deferred.Contains src -> computationOf (seen.Add id) src
        | true, v -> Some v
        | _ -> None
    // The first deferred binder an arm would force, if any. A copy member
    // (`m`, `m |> compute`, an inline map under compute) is admitted when its
    // own computation reads no deferred binding.
    let rec badRead (e: IRExpr) : IRId option =
        match e with
        | IRIf (c, t, f) ->
            (match readsOf c with
             | id :: _ -> Some id
             | [] -> badRead t |> Option.orElse (badRead f))
        | IRTuple es -> es |> List.tryPick badRead
        | m when (IRLift.deferredTupleMembersOf dv m).IsSome -> (IRLift.deferredTupleMembersOf dv m).Value |> List.tryPick badRead
        | e ->
            match stripC e with
            | IRVar (id, ty) when deferred.Contains id && isArrayTy ty ->
                (match computationOf Set.empty id with
                 | Some v -> readsOf v |> List.tryHead
                 | None -> None)
            | c when isDeferredForm c -> readsOf c |> List.tryHead
            | other -> readsOf other |> List.tryHead
    // The deferred binders a piece of an ARM forces when it is evaluated (gap
    // G77): a positional / extent / membership / slice read, a whole-array call
    // argument, a prodsum operand, an assignment's right-hand side, a fold over
    // a deferred value-keyed wrapper (no fold form reads it cell by cell), and
    // every capture of a callable the arm names. A fold or a map OVER a
    // deferred map fuses and forces nothing, so it is not here.
    let forcingReads (n: IRExpr) : IRId list =
        let out = ResizeArray<IRId>()
        let note (b: IRExpr) =
            match stripC b with
            | IRVar (id, _) when deferred.Contains id -> out.Add id
            | _ -> ()
        let isWrapper (b: IRExpr) =
            match stripC b with
            | IRVar (id, _) when deferred.Contains id ->
                (match computationOf Set.empty id with
                 | Some (IRChoice _ | IRFallback _) -> true
                 | _ -> false)
            | _ -> false
        iterIRExpr (fun x ->
            match x with
            | IRIndex (a, _, _) | IRCurry (a, _, _) | IRSlice (a, _, _, _) | IRSubset (a, _, _, _)
            | IRExtent (a, _) | IRContains (a, _) -> note a
            | IRApp (_, args, _) -> args |> List.iter note
            | IRProdSum es -> es |> List.iter note
            | IRAssign (_, v) -> note v
            | IRReduce (a, _, _) when isWrapper a -> note a
            | _ -> ()) n
        let refs = collectVarRefsIR n
        for id in refs do
            match Map.tryFind id funcs with
            | Some c -> for ci in c.Captures do if deferred.Contains ci.Id then out.Add ci.Id
            | None -> ()
        out |> Seq.distinct |> List.ofSeq
    // THE ADMITTED CONDITION FORCE (gap G77; Codex reviews of W4B-A, findings
    // 1 / 2). The C++ lane forces every deferred binder a statement reads
    // positionally at the START of that statement; the interpreter and the LLVM
    // lane force it where the evaluation reaches it. The two agree on the print
    // set AND the first failure only when the force is the FIRST thing in the
    // statement that can fail. So the one binder an `if`'s arm may read is the
    // binder whose forcing read is the condition's FIRST evaluated operation
    // that is not inert (operands left to right, before the node): a read
    // `g(..)` / `g(i, j)` of the binder itself, never a `|> compute` COPY of it
    // (a copy forces nothing and prints nothing) -- and only when the `if` is
    // the whole value of its statement (a let's value, a block's result, a
    // function's return), so no other work of the statement precedes it.
    // Inert: names, literals, parameters, `+ - *`, float `/`, comparisons, logic
    // (the left operand of `&&` / `||` runs first; the first event found is in
    // it), negation, tuple / field projections. Everything else may fail.
    let firstForceOf (cond: IRExpr) : IRId option =
        // the SCALAR element of a type, every unit / index-tag wrapper peeled
        // RECURSIVELY (thirteenth Codex look); None = not a proven scalar
        let rec peelScalar (ty: IRType) =
            match ty with
            | IRTScalar et -> Some et
            | Tagged inner -> peelScalar inner
            | _ -> None
        let elemOf (x: IRExpr) = try peelScalar (typeOf x) with _ -> None
        let isScalarExpr (x: IRExpr) = (elemOf x).IsSome
        // FAIL CLOSED: an operand whose type is not a proven real-float / complex
        // scalar may be an integer (IRTInfer, a named type, a Nat, a typeOf
        // failure), so integer division / modulus / power keep their barrier
        let maybeInt (x: IRExpr) =
            match elemOf x with
            | Some (ETFloat64 | ETFloat32 | ETComplex64 | ETComplex128) -> false
            | Some _ -> true
            | None -> true
        // a cast SOURCE that may be a float (unknown fails closed too)
        let maybeFloat (x: IRExpr) =
            match elemOf x with
            | Some (ETInt64 | ETInt32 | ETBool) -> false
            | _ -> true
        // Some (Some id) = the first event is the force of id; Some None = the
        // first event may fail otherwise; None = nothing evaluated may fail
        // EXHAUSTIVE over IRExpr (eleventh Codex look of W4B-A): every node is
        // its evaluated children in evaluation order, then -- when the node
        // itself may fail -- a barrier; or inert; or a force of a binder. No
        // wildcard: a constructor added later is an incomplete-match warning.
        let rec first (e: IRExpr) : IRId option option =
            let seq (xs: IRExpr list) (self: IRId option option) =
                match xs |> List.tryPick first with
                | Some ev -> Some ev
                | None -> self
            let barrier = Some None
            // a whole-array operand position that FORCES a bare deferred binder
            // (a call argument, a prodsum operand: C++'s pre-pass and the
            // interpreter both force it there)
            let operand (a: IRExpr) =
                match a with
                | IRVar (id, _) when deferred.Contains id -> Some (Some id)
                | a -> first a
            let seqOperands (xs: IRExpr list) (self: IRId option option) =
                match xs |> List.tryPick operand with
                | Some ev -> Some ev
                | None -> self
            match e with
            // leaves
            | IRLit _ | IRParam _ -> None
            // a bare deferred binder evaluated as a value is a COPY of its
            // computation (it may fail), never a stored force
            | IRVar (id, _) -> if deferred.Contains id then barrier else None
            // reads of the binder itself: its force
            | IndexOfNamed (id, _, _, _) | IRCurry (IRVar (id, _), _, _) | IRSlice (IRVar (id, _), _, _, _)
            | IRSubset (IRVar (id, _), _, _, _) | IRContains (IRVar (id, _), _) when deferred.Contains id -> Some (Some id)
            // checked reads: operands, then the read (a checked subscript) may fail
            | IRIndex (a, idxs, _) -> seq (a :: idxs) barrier
            | IRCurry (a, i, _) -> seq [ a; i ] barrier
            | IRSlice (a, _, s, t) -> seq [ a; s; t ] barrier
            | IRSubset (a, _, s, l) -> seq [ a; s; l ] barrier
            | IRContains (a, v) -> seq [ a; v ] barrier
            | IRExtent (IRVar (id, _), _) when deferred.Contains id -> barrier
            | IRExtent (a, _) | IRRank a -> seq [ a ] None
            // `&&` / `||`: only the LEFT operand runs unconditionally; an inert left
            // operand leaves the right one CONDITIONAL, a barrier (third look, finding 2)
            | IRBinOp (_, (IRAnd | IROr), l, _, _) -> (match first l with Some ev -> Some ev | None -> barrier)
            // SCALAR PANICS, formalism 2.4 exactly (twelfth Codex look): an operation
            // is a barrier after its operands ONLY if 2.4 says it can panic --
            //   integer `/` and `%` (by zero)                        BL8013
            //   integer-base `^` (a negative integer exponent)       BL8013
            //   a float -> INTEGER cast (NaN / out of range)         BL8014
            //   a checked read (above)                               BL8006
            //   a call (below: its body is not entered)
            // everything else is INERT after its operands: `+ - *`, float `/`,
            // a REAL-base `^` (whatever the exponent), comparisons, logic,
            // negation, every libm intrinsic (IEEE values, never a language
            // panic), a cast to Float64 / Float32 (or int -> int), complex ops, fma
            | IRBinOp (_, op, l, r, _) ->
                let selfFails =
                    match op with
                    | IRDiv | IRMod -> maybeInt l && maybeInt r
                    | IRCaret -> maybeInt l && (match r with IRLit (IRLitInt k) -> k < 0L | _ -> true)
                    | _ -> false
                seq [ l; r ] (if selfFails then barrier else None)
            // lgamma / digamma are Blade's series and panic BL8008 outside x > 0
            // (formalism 2.4; IR.irNodeMayAbort): a barrier after the operand
            | IRUnaryOp (IRMath ("lgamma" | "digamma"), a) -> seq [ a ] barrier
            | IRUnaryOp ((IRNeg | IRNot | IRConj | IRReal | IRImag | IRArg | IRMath _), a) -> seq [ a ] None
            | IRUnaryOp (IRCast (et, _), a) ->
                let toInt = (et = ETInt64 || et = ETInt32)
                let fromFloat = maybeFloat a
                seq [ a ] (if toInt && fromFloat then barrier else None)
            | IRComplex (a, b) -> seq [ a; b ] None
            | IRFma (a, b, c) -> seq [ a; b; c ] None
            | IRRandAt (_, args, w) -> seq (args @ (w |> Option.map fst |> Option.toList)) barrier
            // aggregates: members left to right
            | IRTuple es -> seq es None
            | IRTupleProj (a, _, _) | IRFieldAccess (a, _) | IRTupleDecons a | IRPure a -> seq [ a ] None
            | IRTupleCons (h, tl) -> seq [ h; tl ] None
            | IRStructLit (_, fields) -> seq (fields |> List.map snd) None
            | IRArrayLit (es, _) -> seq es None
            | IRPolyIndex (pk, i) -> seq [ pk; i ] None
            | IRPolyTail (pk, _) -> seq [ pk ] None
            // a CALL (eleventh look, finding 1): the callee expression and every
            // argument left to right first, then the invocation -- a barrier (its
            // body is not entered)
            | IRAppAt (f, args, _, _) -> (match first f with Some ev -> Some ev | None -> seqOperands args barrier)
            | IRProdSum es -> seqOperands es barrier
            // control: the condition / scrutinee runs first; the next event is in
            // an arm or case, which is conditional
            | IRIf (c, _, _) -> (match first c with Some ev -> Some ev | None -> barrier)
            | IRMatch (s, _) -> (match first s with Some ev -> Some ev | None -> barrier)
            | IRLet (_, v, b) -> seq [ v; b ] None
            // `|> compute` (finding 2): over a STORED name (not deferred: the
            // member-order predicate's reading) it adds no event; over a deferred
            // computation it evaluates a COPY -- may fail, never a force
            | IRCompute x ->
                let rec strip (y: IRExpr) = match y with IRCompute z -> strip z | y -> y
                (match strip x with
                 | IRVar (id, _) when not (deferred.Contains id) -> None
                 | IRLit _ | IRParam _ -> None
                 | _ -> barrier)
            // loop objects and function values: compile-time, nothing evaluated
            | IRMethodFor _ | IRObjectFor _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _ | IRReynolds _ -> None
            // deferred computations and computation wrappers held as values: their
            // consumers evaluate them (may fail)
            // a SCALAR guard / choice (second regression of the twelfth look): the
            // guard's condition, the choice's LEFT operand run unconditionally --
            // walk them, then a barrier (never admit from the conditional body /
            // right operand); an array-valued (deferred) wrapper stays opaque
            | IRGuard (c, _) when isScalarExpr e ->
                (match first c with Some ev -> Some ev | None -> barrier)
            | IRChoice (l, _) when isScalarExpr e ->
                (match first l with Some ev -> Some ev | None -> barrier)
            // deferred computations, computation wrappers and an eager sequence
            // held as values: opaque barriers (their consumers evaluate them)
            | IRApplyCombinator _ | IRComposeApply _ | IRBind _ | IRParallel _ | IRFusion _ | IRChoice _
            | IRFallback _ | IRArrayProduct _ | IRFunctorMap _ | IRGuard _ | IRSequence _ | IRReplicate _
            | IRZip _ | IRAlign _ -> barrier
            // eager array operations, folds, factorizations, effects: operands
            // first, then the operation may fail
            | IRMask _ | IRIntersect _ | IRUnion _ | IRUnique _ | IRSort _ | IRReduce _ | IRReduceCompute _
            | IRStack _ | IRJoin _ | IRTranspose _ | IRDecompact _ | IRGram _ | IRGramApply _ | IRMatmul _
            | IREigh _ | IRSolve _ | IRLu _ | IRLuSolve _ | IRArrayNegate _ | IRArrayConjugate _ | IRReverse _
            | IRShift _ | IRDiag _ | IRGroupBy _ | IRGroupKeys _ | IRGroupBucket _ | IRGroupSizes _ | IRUngroup _
            | IRUngroupGrid _ | IRUngroupRows _ | IRDisplayEmit _ | IRDisplayJson _ | IRDisplayNum _
            | IRDisplayStr _ -> seq (childrenOf e) barrier
            // inert markers and constants
            | IRSegments _ | IRSegmentsGrid _ | IRRange _ | IRVirtualReverse _ | IRHaloUnhash _ | IRArity _
            | IRNth | IRZero _ | IRUnseededSeed _ | IRUninit _ | IRBeginPoolScope _ | IRReleasePools _ | IRSparseKeys _ | IROrbitClass _
            | IROpaqueExtent -> None
            | IRRaggedLookup a | IRCompoundMask a | IRCompoundProject (a, _) -> seq [ a ] None
            // statements: their operands, then they may fail / branch
            | IRAssign (_, v) -> seq [ v ] barrier
            | IRForRange (_, lo, hi, _) -> seq [ lo; hi ] barrier
            | IRConstraintCheck (c, _, _, _) -> seq [ c ] barrier
            | IRBreakIf c -> seq [ c ] barrier
            | IRReplayRow (_, r) -> seq [ r ] barrier
        match first cond with
        | Some (Some id) -> Some id
        | _ -> None
    // The deferred binders a statement DEFINITELY FORCES -- forced at that
    // statement on every evaluation path (seventh Codex look of W4B-A): a read
    // of the binder itself (`g(k)`, a curry, a slice, a subset, a membership
    // test), the binder as a whole-array call argument, a prodsum operand; an
    // operand that runs unconditionally (only the LEFT side of `&&` / `||`, an
    // `if`'s condition plus what BOTH arms force, a `match`'s scrutinee plus what
    // EVERY case forces, a let chain's lets and result). NOT a force: a
    // `|> compute` COPY, a fold or a map over it (fused: it evaluates the
    // computation but neither stores nor prints it), an extent, anything under a
    // deferred computation, a kernel (a lifted callable: its capture is read where
    // its body reads it) or a computation wrapper. Forcing one member of a module
    // ALIAS CLASS forces the class (formalism 10.2).
    let aliasClasses = IRLift.deferredAliasClasses allBindings (IRLift.computeDeferredIds allBindings)
    let rec definitelyForces (e: IRExpr) : Set<IRId> =
        let note (b: IRExpr) =
            match b with
            | IRVar (id, _) when deferred.Contains id -> Set.singleton id
            | _ -> Set.empty
        let here =
            match e with
            | IRIndex (a, _, _) | IRCurry (a, _, _) | IRSlice (a, _, _, _) | IRSubset (a, _, _, _) | IRContains (a, _) -> note a
            | IRApp (_, args, _) -> args |> List.map note |> Set.unionMany
            | IRProdSum es -> es |> List.map note |> Set.unionMany
            | _ -> Set.empty
        let inter (xs: Set<IRId> list) = match xs with [] -> Set.empty | x :: rest -> List.fold Set.intersect x rest
        let kids =
            match e with
            | IRBinOp (_, (IRAnd | IROr), l, _, _) -> definitelyForces l
            | IRIf (c, t, f) -> Set.union (definitelyForces c) (Set.intersect (definitelyForces t) (definitelyForces f))
            | IRMatch (s, cases) -> Set.union (definitelyForces s) (cases |> List.map (fun c -> definitelyForces c.Body) |> inter)
            | IRLet (_, v, b) -> Set.union (definitelyForces v) (definitelyForces b)
            | IRForRange (_, lo, hi, _) -> Set.union (definitelyForces lo) (definitelyForces hi)
            | IRReduce _ | IRReduceCompute _ | IRExtent _ -> Set.empty
            | IRGuard _ | IRSequence _ | IRReplicate _ | IRChoice _ | IRFallback _ -> Set.empty
            | e when isDeferredForm e -> Set.empty
            | e -> childrenOf e |> List.map definitelyForces |> Set.unionMany
        Set.union here kids
    let dominatingForces (e: IRExpr) : Set<IRId> =
        definitelyForces e
        |> Set.fold (fun acc id ->
            match aliasClasses.TryFind id with
            | Some cls -> cls |> List.fold (fun a m -> Set.add m a) acc
            | None -> Set.add id acc) Set.empty
    let checkIfs (span: Blade.Ast.Span) (inFrame: bool) (initialForced: Set<IRId>) (root: IRExpr) =
        // `stmt`: the whole value of the statement `e` sits in (a binding's
        // value, a let's value, a let chain's result, a loop bound / body).
        let rec walk (frame: bool) (stmt: IRExpr) (forced: Set<IRId>) (e: IRExpr) =
            (match e with
             // AN `if` VALUE WHOSE ARM FORCES A DEFERRED COMPUTATION (gap G77):
             // the condition first, then only the taken arm is the
             // interpreter's order; the C++ lane forced every deferred map
             // either arm reads BEFORE the condition (so an untaken arm's map
             // printed and its failure fired), and the LLVM lane printed it
             // from both arms (forcing it, and failing, at print time). No lane
             // has a print rule that follows the branch, so the shape is
             // refused, every lane alike; the one binder whose forcing read is the
             // condition's first possibly-failing operation, in an `if` that is
             // its statement's whole value, is admitted (firstForceOf).
             // (TOP-LEVEL code only -- module statements and their blocks, NOT a
             // function / lambda / kernel body (ninth Codex look) nor a LOOP body
             // -- a let-rec step, a while guard (tenth look): the CURRENT frame;
             // there the entry force state is not decidable here, so only the
             // structural audit below applies)
             | IRIf (c, t, f) when not frame && not (IRLift.ifNeedsOrderedArms dv e) ->
                 // the binder whose force is the first event of the whole
                 // STATEMENT (what the C++ pre-pass moves to its start) and
                 // of this condition
                 // the binder whose force is the FIRST possibly-failing event of the
                 // whole STATEMENT (region): the C++ pre-pass moves it to the
                 // statement's start, where it already is in every lane -- whether it
                 // sits in this condition or anywhere earlier in the statement
                 // (`g(0) + (if z(0) > 0 then g(0) else 0)`; tenth Codex look). Any
                 // may-fail event before it vetoes (firstForceOf's barrier).
                 let admitted = firstForceOf stmt
                 (match forcingReads t @ forcingReads f |> List.tryFind (fun id -> Some id <> admitted && not (forced.Contains id)) with
                  | Some id ->
                      let (what, _) = describe id
                      refuseLowering span
                          ($"an `if` value whose arm forces the deferred computation {what} (by position, as a call argument, through a capture, ...): only the TAKEN arm is evaluated, and no lane's print list or first-failure point follows the branch (formalism 10.2). Force it before the `if` (`let v = <it> |> compute`, or read it in a statement of its own) and read `v` in the arm.")
                  | None -> ())
             | IRIf (_, t, f) when IRLift.ifNeedsOrderedArms dv e ->
                 if frame then
                     refuseLowering span
                         "an `if` value that selects between computed arrays (a `|> compute`, or a deferred map held in a tuple) inside a function body, a lambda or a loop body: the compiled lanes have no owner for the taken arm's storage after the branch. Compute each array before the `if` (`let a = m |> compute`) and select between the names, or bind the `if` at the top level."
                 else
                     match badRead t |> Option.orElse (badRead f) with
                     | Some id ->
                         let (what, _) = describe id
                         refuseLowering span
                             ($"an `if` value whose arm reads the deferred computation {what} (by position, through a capture, or inside a computed member): it would be evaluated only on the TAKEN arm, and no lane's static print list follows the branch. Force it before the `if` (`let v = <it> |> compute`) and read `v` in the arm.")
                     | None -> ()
             | _ -> ())
            match e with
            | IRForRange (_, lo, hi, body) ->
                walk frame lo forced lo
                walk frame hi forced hi
                walk true body (Set.union forced (Set.union (dominatingForces lo) (dominatingForces hi))) body
            // a let chain (a block, a function body) is ONE hoist region: the
            // C++ lane forces the whole block's deferred reads before its lets
            // (genLetChainBinding; third look, finding 3), so every earlier let
            // precedes the admitted force
            // a binding's tuple value the C++ emitter evaluates MEMBER BY
            // MEMBER (genMemberOrderedTupleBinding; IRLift.tupleNeedsMemberOrderOf,
            // the emitter's own judgment): each leaf is its own region, left to
            // right (fifth Codex look of W4B-A, finding 1). A tuple the emitter
            // renders whole keeps the enclosing region.
            | IRTuple _ when obj.ReferenceEquals (e, stmt) && not inFrame
                             && not (IRLift.isWhollyDeferredTupleOf dv (match e with IRTuple es -> es | _ -> []))
                             // the emitter's two halves: the STRUCTURAL one (an explicit
                             // compute / bare deferred / deferred-tuple member) over the
                             // program's deferred view -- a binder the emitter already
                             // forced makes it skip the ordered path, but then that member
                             // is a copy of stored cells, which cannot fail, so the first
                             // failure is the same either way (sixth Codex look, finding
                             // 1) -- and the may-fail COUNT with NO binder deferred: the
                             // emitter's count can only be higher, so a tuple ordered here
                             // by the count is ordered there
                             && (IRLift.memberNeedsOrderOf dv e
                                 || (IRLift.tupleLeavesOf e
                                     |> List.filter (IRLift.memberMayFailOf { dv with IsDeferred = (fun _ -> false); ValueOf = (fun _ -> None) })
                                     |> List.length) >= 2) ->
                // the leaves run left to right (C++ threads its context leaf to leaf):
                // each leaf is checked with what the EARLIER leaves definitely forced
                (IRLift.tupleLeavesOf e |> List.fold (fun acc leaf ->
                    walk frame leaf acc leaf
                    Set.union acc (dominatingForces leaf)) forced) |> ignore
            | IRLet (_, v, b) when inFrame ->
                // a FUNCTION / lambda body is emitted statement by statement
                // (genFuncBody): each let's value is its own region, and the
                // return's forcing code follows the preceding statements
                walk frame v forced v
                walk frame b (Set.union forced (dominatingForces v)) b
            | IRLet (_, v, b) ->
                walk frame stmt forced v
                let forcedB = Set.union forced (dominatingForces v)
                // a block whose RESULT is a tuple: the C++ block pre-pass
                // excludes the final tuple's reads, which force at the tuple's
                // own binding, after the block's statements -- its own region
                match stmt, b with
                | IRLet _, IRTuple _ -> walk frame b forcedB b
                | _ -> walk frame stmt forcedB b
            // the condition / scrutinee / loop bounds run BEFORE the arms / cases /
            // body in every lane: their definite forces are carried in (never one
            // arm into another, never one iteration into the next)
            | IRIf (c, t, f) ->
                walk frame stmt forced c
                let fc = Set.union forced (dominatingForces c)
                walk frame stmt fc t
                walk frame stmt fc f
            | IRMatch (s, cases) ->
                walk frame stmt forced s
                let fs = Set.union forced (dominatingForces s)
                for cs in cases do
                    cs.Guard |> Option.iter (walk frame stmt fs)
                    walk frame stmt fs cs.Body
            | _ -> childrenOf e |> List.iter (walk frame stmt forced)
        // The shared predicate refuses a cyclic deferred-tuple provenance
        // unspanned (IRLift.deferredTupleMembersOf); locate it at the binding.
        try walk inFrame root initialForced root
        with Blade.Diagnostics.BladeDiagnosticException d when d.Code = "BL6002" && d.Span = Blade.Ast.noSpan ->
            refuseLowering span d.Message
    // top-level statements DOMINATE the ones after them, module by module
    // in binding order: what an earlier statement definitely forced is stored
    // when a later `if`'s arm reads it (seventh Codex look, the one finding)
    // REGION BOUNDARIES of the force state (eighth Codex look of W4B-A):
    //   top-level statements -- accumulate, module by module in binding order;
    //   block lets and function-body lets -- accumulate (walk's IRLet arms);
    //   a block's final tuple -- inherits the block's lets (walk);
    //   member-ordered tuple leaves -- accumulate left to right (walk);
    //   if / match arms -- inherit PLUS the condition's / scrutinee's definite
    //     forces; add nothing for later code unless every branch forces;
    //   a FUNCTION, lambda or kernel body -- NOT AUDITED by the ordinary-arm
    //     refusal (only the structural one): a sound entry state needs every
    //     invocation / consumption site (captures, recursion, deferred kernels),
    //     recorded OPEN in G77;
    //   a loop body -- inherits plus the bounds' forces, adds nothing across iterations;
    //   a kernel -- like a function body (not audited).
    for m in modules do
        let mutable moduleForced = Set.empty
        for b in m.Bindings do
            checkIfs (IR.declSpanOf b.Name) false moduleForced b.Value
            moduleForced <- Set.union moduleForced (dominatingForces b.Value)
    for f in allFuncs do checkIfs (funcSpan f) true Set.empty f.Body
    part "if values"
    if Blade.Toolchain.envFlag "BLADE_PHASE_TIMING" false then
        eprintfn "[phase] write rules: %s" (parts |> Seq.map (fun (n, ms) -> sprintf "%s %d ms" n ms) |> String.concat ", ")
    // R12's protected-parameter refusal comes LAST, so a program every other
    // rule of this pass already refuses keeps that rule's diagnostic
    match pendingParamRefusal.Value with
    | Some d -> raise (Blade.Diagnostics.BladeDiagnosticException d)
    | None -> ()

// ---------------------------------------------------------------------------
// G239: A FORCE AT EACH READ POSITION INSIDE A FUNCTION BODY (round 4 wave 10,
// lane C, C1; Codex design review edb50dd4 rounds 1-2, BUILD WITH CHANGES).
//
// Formalism 10.2 / the confirmed G239 contract: inside a callable body every
// ordinary forcing use of a deferred MODULE map -- a positional read `d(i)` --
// evaluates the map AT ITS POSITION, in evaluation order; an untaken read (an
// `if` / `match` arm, the right operand of `&&` / `||`) evaluates nothing; a
// read in a loop step evaluates it per iteration, so the evaluation COUNT is
// observable when the body writes the map's input between two reads. That is
// what the interpreter does (each read forces privately: Loops.force under
// FunctionCallDepth > 0). Both compiled lanes force once per body STATEMENT,
// before it (CodeGen.genFuncBodyScoped's statement pre-pass; the LLVM lane's
// statement hoist).
//
// THE REWRITE (one IR pass before the lane split, so every lane runs one
// tree): in a statement whose order the pre-pass cannot reproduce, each read
// becomes a CALL of a one-statement helper function `__bfr<id>_<map>(i..) =
// map(i..)` -- an ordinary named function body, whose own statement pre-pass
// forces the map privately per call in every lane (holder ownership is the
// existing function-body machinery) -- and the statement's evaluated-once
// SPINE is put in A-normal form, in the interpreter's evaluation order
// (Interp/Core.fs evalExpr: binop left then right, call head then arguments,
// index base then indices, let value then body, condition first): every event
// on the spine (a call -- the helper calls included --, an operation that may
// fail, a checked subscript, an assignment) is bound to a fresh body let, and
// every scalar read of a binding a later event may write (a module value, or a
// local the statement itself assigns) is snapshotted before it. An arm, a
// right operand or a loop step is a nested region normalized the same way
// inside it (an arm that needs ordering becomes a block of lets), so a C++
// ternary / `&&` / `||` / loop evaluates a call only where the interpreter
// reads. The interpreter result is unchanged BY CONSTRUCTION: the pass
// rewrites a statement only when every condition below holds and otherwise
// leaves it byte-identical (fail closed in the do-not-touch sense; the
// residuals are recorded under G239):
//   * every index of every read is INERT (IRLift.memberMayFailOf clears it, it
//     names no deferred binder): the interpreter forces the map BEFORE it
//     evaluates the index, a helper call after; a force writes nothing (a
//     writing named map is stored at its definition, G45), so the order of an
//     inert index is unobservable;
//   * the deferred map appears in no other position (no whole-array use, no
//     operand, no alias, no deferred-tuple member) and NO callable the
//     statement references captures a deferred module binder (its capture is
//     forced by the statement pre-pass, which may sit outside the branch that
//     runs it -- Codex round 2, finding 1);
//   * the spine holds no non-scalar event and no traversal before a later
//     bound event (a fold / map / array call is never moved, so no planner
//     side table keyed by its binding is invalidated; an opaque node may stay
//     in place only as the LAST event of its region -- round 2, finding 2).
// A statement whose reads are all of ONE map, all on the spine, and which has
// no other event is left to the pre-pass: one force before it equals the
// interpreter's force-read-force-read (nothing can write or fail between).
//
// THE HELPER: one per (caller, map, index types); a named function (not
// `__lambda`), capture-free (the interpreter's private-force rule), its
// SourceName the caller's, HoistFrame = Some false (no shadow frame in any
// lane, so panic traces read `at <caller>` as before), located at the caller's
// declaration (HoistDeclTable -> Lowering.copyHoistDeclSpans), filed in
// DerivedFuncOrigins under its CALLER and placed just before it, so the C++
// lane emits it at the caller's program point (the same deferred context: a
// map an earlier top-level statement stored is read stored by both).
// ---------------------------------------------------------------------------

exception private BodyReadsCannotSplit

let rec private bodyReadsScalarType (t: IRType) =
    match t with
    | IRTScalar _ -> true
    | Tagged inner -> bodyReadsScalarType inner
    | _ -> false

let private bodyReadsUnitType (t: IRType) =
    match t with
    | IRTUnit -> true
    | _ -> false

let splitDeferredBodyReadsModule (builder: IRBuilder) (allModules: IRModule list) (modul: IRModule) : IRModule =
    let allFuncs = allModules |> List.collect (fun m -> m.Functions)
    let deferredIds = IRLift.computeDeferredIds modul.Bindings
    let rec stripC (e: IRExpr) = match e with IRCompute x -> stripC x | e -> e
    let maps =
        modul.Bindings
        |> List.filter (fun b ->
            deferredIds.Contains b.Id
            && (match stripUnits b.Type with ArrayElem _ -> true | _ -> false)
            && (match stripC b.Value with IRApplyCombinator _ | IRComposeApply _ -> true | _ -> false))
        |> List.map (fun b -> (b.Id, b))
        |> Map.ofList
    if maps.IsEmpty then modul else
    let bindingValues = modul.Bindings |> List.map (fun b -> (b.Id, b.Value)) |> Map.ofList
    let dv : IRLift.DeferredView =
        { IsDeferred = deferredIds.Contains
          ValueOf = fun id -> Map.tryFind id bindingValues
          TypeOfExpr = typeOf }
    let inert (e: IRExpr) = not (IRLift.memberMayFailOf dv e)
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in allFuncs do callables.[f.Id] <- f
    for f in modul.Functions do callables.[f.Id] <- f
    // A callable whose captures reach a deferred module binder, directly or
    // through a captured callable.
    let capMemo = System.Collections.Generic.Dictionary<IRId, bool>()
    let rec capturesDeferred (seen: Set<IRId>) (id: IRId) : bool =
        match capMemo.TryGetValue id with
        | true, v -> v
        | _ ->
            match callables.TryGetValue id with
            | true, c when not (seen.Contains id) ->
                let v =
                    c.Captures |> List.exists (fun cap ->
                        deferredIds.Contains cap.Id || capturesDeferred (seen.Add id) cap.Id)
                capMemo.[id] <- v
                v
            | _ -> false
    let isRead (e: IRExpr) =
        match e with
        | IndexOfNamed (d, _, (_ :: _), _) when maps.ContainsKey d -> bodyReadsScalarType (typeOf e)
        | _ -> false
    let rec hasRead (e: IRExpr) = isRead e || (childrenOf e |> List.exists hasRead)
    let staticExtent (ty: IRType) (dim: int) =
        match stripUnits ty with
        | ArrayElem at when dim >= 0 && dim < at.IndexTypes.Length ->
            (match at.IndexTypes.[dim].Extent with IRLit (IRLitInt _) -> true | _ -> false)
        | _ -> false
    // Every read's indices are inert, and no deferred binder or deferred-
    // capturing callable is named anywhere else in the statement.
    let rec admissible (e: IRExpr) : bool =
        match e with
        | IRIndex (IRVar _, idxs, _) when isRead e ->
            idxs |> List.forall (fun i -> inert i && not (hasRead i) && admissible i)
        // an extent read of a deferred map is inert only when the extent is a
        // static literal of its type (G218); a RUN-TIME extent forces the map
        // (G234) at its position, which a rewrite would move: declined (Codex
        // diff review 03cfa2dd, finding 1)
        | IRExtent (IRVar (id, ty), dim) when deferredIds.Contains id -> staticExtent ty dim
        | IRVar (id, _) -> not (deferredIds.Contains id) && not (capturesDeferred Set.empty id)
        | _ -> childrenOf e |> List.forall admissible
    // A read in a CONDITIONAL or REPEATED position: an if / match arm or guard,
    // the right operand of && / ||, a loop body or a while guard.
    let rec readOffSpine (e: IRExpr) : bool =
        match e with
        | IRIf (c, t, f) -> readOffSpine c || hasRead t || hasRead f
        | IRMatch (s, cases) ->
            readOffSpine s
            || cases |> List.exists (fun c -> hasRead c.Body || (c.Guard |> Option.exists hasRead))
        | IRBinOp (_, (IRAnd | IROr), l, r, _) -> readOffSpine l || hasRead r
        | IRForRange (_, lo, hi, body) -> readOffSpine lo || readOffSpine hi || hasRead body
        | IRBreakIf c -> hasRead c
        | _ -> childrenOf e |> List.exists readOffSpine
    let readMaps (e: IRExpr) =
        let acc = System.Collections.Generic.HashSet<IRId>()
        iterIRExpr (fun n -> match n with IndexOfNamed (d, _, _, _) when isRead n -> acc.Add d |> ignore | _ -> ()) e
        acc.Count
    // The statement with each read replaced by a plain value of its type: does
    // anything else in it act (fail, call, write)?
    let otherwiseInert (e: IRExpr) =
        let dummy = builder.FreshId()
        inert (mapIRExpr (fun n -> if isRead n then IRVar (dummy, typeOf n) else n) e)
    let wrapLets (pre: (IRId * IRExpr) list) (body: IRExpr) =
        List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) pre body
    let minted = ResizeArray<IRCallable * IRId * IRId>()   // helper, map, caller
    // G255: a module map no module-level code names is evaluated only inside
    // function bodies, privately (formalism 10.2): a function-body COPY of it
    // (d |> compute) or a MASK over it evaluates its own map form there --
    // the C++ lane named the module binder, which nothing declared (BL9002).
    // Only a let-free map form is inlined (ids stay unique).
    // (Codex design review 03cfa2dd, finding 1) every id MODULE-LEVEL code may
    // force in module mode: what the program's binding values, side payloads
    // and type-carried extents name, and -- through every callable they
    // reference other than as the head of a direct call of a capture-free
    // named function (whose body runs privately, under a call) -- that
    // callable's captures and body, transitively (a kernel runs inline in the
    // interpreter, at module depth: Codex finding 3)
    let isPlainNamedFn (c: IRCallable) = c.Captures.IsEmpty && not (c.Name.StartsWith "__lambda")
    let funcById = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in allFuncs do funcById.[f.Id] <- f
    let moduleNamed =
        let acc = System.Collections.Generic.HashSet<IRId>()
        let walked = System.Collections.Generic.HashSet<IRId>()
        let rec walk (e: IRExpr) =
            match e with
            | IRApp ((IRVar (fid, _)), args, _) when (match funcById.TryGetValue fid with | true, c -> isPlainNamedFn c | _ -> false) ->
                acc.Add fid |> ignore
                List.iter walk args
            | IRVar (id, _) ->
                acc.Add id |> ignore
                match funcById.TryGetValue id with
                | true, c when walked.Add id ->
                    for cap in c.Captures do acc.Add cap.Id |> ignore
                    walk c.Body
                | _ -> ()
            | IRApp ((IRVar _ as head), args, _) ->
                walk head
                List.iter walk args
            | _ -> childrenOf e |> List.iter walk
        for m in allModules do
            // binding values and side payloads (no function body)
            for r in moduleExecutablePayloads { m with Functions = [] } do
                walk r
                for x in Optimize.embeddedExtentExprs r do walk x
        acc
    // a capture-free named function every reference to which, anywhere in the
    // program, is the head of a direct call: its body runs only under a call
    // (the interpreter's private-force depth), never inline as a kernel
    let nonCallRefs =
        let acc = System.Collections.Generic.HashSet<IRId>()
        let rec walk (e: IRExpr) =
            match e with
            | IRApp ((IRVar _), args, _) -> List.iter walk args
            | IRVar (id, _) -> acc.Add id |> ignore
            | _ -> childrenOf e |> List.iter walk
        for m in allModules do
            for r in moduleExecutablePayloads m do
                walk r
                for x in Optimize.embeddedExtentExprs r do walk x
        for f in allFuncs do
            for cap in f.Captures do acc.Add cap.Id |> ignore
        acc
    let directCallOnly (f: IRCallable) = isPlainNamedFn f && not (nonCallRefs.Contains f.Id)
    let inlineValue (d: IRId) : IRExpr option =
        match Map.tryFind d maps with
        | Some b when not (moduleNamed.Contains d) ->
            let v = stripC b.Value
            let hasLet = ref false
            iterIRExpr (fun n -> match n with IRLet _ -> hasLet.Value <- true | _ -> ()) v
            if hasLet.Value then None else Some v
        | _ -> None
    let hasInlineUse (body: IRExpr) =
        let found = ref false
        iterIRExpr (fun n ->
            match n with
            | IRCompute (IRVar (d, _)) | IRMask (IRVar (d, _), _) when (inlineValue d).IsSome -> found.Value <- true
            | _ -> ()) body
        found.Value
    // closure: a lambda the body CALLS (G241, processed by the closure arm
    // below): every read is rewritten, the pre-pass keep rule does not apply
    // (its body reads the CAPTURED binder, never a private force)
    let processFunc (closure: bool) (f: IRCallable) : IRCallable =
        if (not closure && not (directCallOnly f))
           || f.HoistFrame.IsSome
           || f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || f.IsOmpParallel
           || not (hasRead f.Body || (not closure && hasInlineUse f.Body)) then f
        else
        let helperFor = System.Collections.Generic.Dictionary<IRId * IRType list, IRCallable>(HashIdentity.Structural)
        let helperCall (read: IRExpr) : IRExpr =
            match read with
            | IndexOfNamed (d, dty, idxs, ident) ->
                let idxTys = idxs |> List.map typeOf
                let h =
                    match helperFor.TryGetValue ((d, idxTys)) with
                    | true, h -> h
                    | _ ->
                        let ps = idxTys |> List.mapi (fun k t -> { Name = $"__bfr_i{k}"; Type = t; Index = k; VarId = builder.FreshId() })
                        let body = IRIndex (IRVar (d, dty), ps |> List.map (fun p -> IRVar (p.VarId, p.Type)), ident)
                        let hid = builder.FreshId()
                        let c =
                            mkCallable builder { defaultLambdaOptions with NameOverride = Some $"__bfr{hid}_{maps.[d].Name}"; IdOverride = Some hid }
                                ps body (typeOf read) [] false [] [] false false 256 false
                        let c = { c with SourceName = f.SourceName; HoistFrame = Some false }
                        helperFor.[(d, idxTys)] <- c
                        minted.Add((c, d, f.Id))
                        Optimize.HoistDeclTable.record hid (Optimize.LocatedAsCallable f.Id)
                        c
                IRApp (IRVar (h.Id, mkFuncArrow (h.Params |> List.map (fun p -> p.Type)) h.RetType), idxs, h.RetType)
            | _ -> read
        // the body's own binders (parameters, lets, loop variables): a later
        // event of the statement cannot write one the statement does not assign
        let locals = System.Collections.Generic.HashSet<IRId>()
        for p in f.Params do locals.Add p.VarId |> ignore
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, _, _) | IRForRange (id, _, _, _) -> locals.Add id |> ignore
            | _ -> ()) f.Body
        // ONE statement (a let value, an assignment, a loop, a return):
        // (lets bound before it, the residual statement)
        let rec normStmt (v: IRExpr) : (IRId * IRExpr) list * IRExpr =
            let assigned = System.Collections.Generic.HashSet<IRId>()
            iterIRExpr (fun n ->
                match n with
                | IRAssign (t, _) ->
                    let rec root (x: IRExpr) =
                        match x with
                        | IRVar (id, _) -> assigned.Add id |> ignore
                        | IRIndex (a, _, _) | IRTupleProj (a, _, _) | IRFieldAccess (a, _) -> root a
                        | _ -> ()
                    root t
                | _ -> ()) v
            let binds = ResizeArray<IRId * IRExpr>()
            let pending = ResizeArray<IRId * IRExpr>()
            let mutable opaqueSeen = false
            let flushPending () =
                for p in pending do binds.Add p
                pending.Clear()
            let bindAs (id: IRId) (e: IRExpr) =
                if opaqueSeen then raise BodyReadsCannotSplit
                flushPending ()
                binds.Add((id, e))
            let bind (e: IRExpr) : IRExpr =
                let ty = typeOf e
                if not (bodyReadsScalarType ty || bodyReadsUnitType ty) then raise BodyReadsCannotSplit
                let t = builder.FreshId()
                bindAs t e
                IRVar (t, ty)
            let event (root: bool) (n: IRExpr) = if root then n else bind n
            // a scalar read a later event of this statement may write: a
            // module value, or a local the statement assigns
            let snapVar (id: IRId) (ty: IRType) =
                not (deferredIds.Contains id) && not (callables.ContainsKey id)
                && bodyReadsScalarType ty && (not (locals.Contains id) || assigned.Contains id)
            let needsSnap (e: IRExpr) =
                let found = ref false
                iterIRExpr (fun n -> match n with IRVar (id, ty) when snapVar id ty -> found.Value <- true | _ -> ()) e
                found.Value
            let rec spine (root: bool) (e: IRExpr) : IRExpr =
                match e with
                | IRLit _ | IRParam _ -> e
                | IRVar (id, ty) ->
                    if deferredIds.Contains id then raise BodyReadsCannotSplit
                    elif snapVar id ty then
                        if opaqueSeen then raise BodyReadsCannotSplit
                        let ph = builder.FreshId()
                        pending.Add((ph, e))
                        IRVar (ph, ty)
                    else e
                | _ when isRead e -> event root (helperCall e)
                | IRBinOp (m, (IRAnd | IROr as op), l, r, loc) ->
                    let l' = spine false l
                    if not (hasRead r) && inert r && not (needsSnap r) then IRBinOp (m, op, l', r, loc)
                    else
                        match region r with
                        // a right operand that needs ordering is a block: written as
                        // the `if` it means (the C++ lane renders an `if` arm block
                        // as a sequenced IIFE; a block operand of `&&` would be
                        // flattened ahead of the statement)
                        | IRLet _ as r' ->
                            let n =
                                match op with
                                | IRAnd -> IRIf (l', r', IRLit (IRLitBool false))
                                | _ -> IRIf (l', IRLit (IRLitBool true), r')
                            event root n
                        | r' -> event root (IRBinOp (m, op, l', r', loc))
                | IRBinOp (m, op, l, r, loc) ->
                    let l' = spine false l
                    let r' = spine false r
                    let n = IRBinOp (m, op, l', r', loc)
                    if inert n then n else event root n
                | IRUnaryOp (op, a) ->
                    let n = IRUnaryOp (op, spine false a)
                    if inert n then n else event root n
                | IRTupleProj (a, i, fl) ->
                    let n = IRTupleProj (spine false a, i, fl)
                    if inert n then n else event root n
                | IRFieldAccess (a, fld) ->
                    let n = IRFieldAccess (spine false a, fld)
                    if inert n then n else event root n
                | IRTuple es -> IRTuple (es |> List.map (spine false))
                | IRComplex (a, b) ->
                    let a' = spine false a
                    IRComplex (a', spine false b)
                | IRFma (a, b, c) ->
                    let a' = spine false a
                    let b' = spine false b
                    IRFma (a', b', spine false c)
                | IRExtent (IRVar (id, ty), dim) when deferredIds.Contains id && not (staticExtent ty dim) -> raise BodyReadsCannotSplit
                | IRExtent _ when not (hasRead e) -> e
                | IRIndex (a, idxs, ident) ->
                    // a checked read of a stored array: base, then indices
                    let a' = spine false a
                    let idxs' = idxs |> List.map (spine false)
                    event root (IRIndex (a', idxs', ident))
                | IRApp ((IRVar _ as fn), args, rt) ->
                    let args' = args |> List.map (spine false)
                    event root (withAppParts e (fn, args', rt))
                | IRIf (c, t, fl) ->
                    let c' = spine false c
                    let t' = region t
                    let f' = region fl
                    let n = IRIf (c', t', f')
                    if inert n && not (needsSnap t' || needsSnap f') then n else event root n
                | IRMatch (s, cases) when hasRead e ->
                    let s' = spine false s
                    let cases' = cases |> List.map (fun c -> { c with Guard = c.Guard |> Option.map region; Body = region c.Body })
                    event root (IRMatch (s', cases'))
                | IRLet (x, v0, b) ->
                    let v' = spine true v0
                    bindAs x v'
                    spine root b
                | IRAssign (target, value) ->
                    let value' = spine false value
                    let rec lvalueOk (t: IRExpr) =
                        match t with
                        | IRVar _ -> true
                        | IRIndex (a, idxs, _) -> lvalueOk a && idxs |> List.forall (fun i -> inert i && not (hasRead i))
                        | _ -> false
                    if not (lvalueOk target) || hasRead target then raise BodyReadsCannotSplit
                    event root (IRAssign (target, value'))
                | IRForRange (vid, lo, hi, body) when root ->
                    let lo' = spine false lo
                    let hi' = spine false hi
                    IRForRange (vid, lo', hi', region body)
                | IRBreakIf c when root -> IRBreakIf (spine false c)
                | _ ->
                    if hasRead e then raise BodyReadsCannotSplit
                    elif inert e && not (needsSnap e) then e
                    else
                        // an opaque event (a fold, a map, an array call): kept in
                        // place, so nothing after it in this statement may be bound
                        if not root then opaqueSeen <- true
                        e
            let residual = spine true v
            // an unflushed snapshot is read where it stood: no event follows it
            let unflushed = pending |> Seq.map (fun (ph, orig) -> (ph, orig)) |> Map.ofSeq
            let residual =
                if unflushed.IsEmpty then residual
                else mapIRExpr (fun n -> match n with IRVar (id, _) when unflushed.ContainsKey id -> unflushed.[id] | n -> n) residual
            (List.ofSeq binds, residual)
        // A nested region (an arm, a right operand, a loop body): every
        // statement holding a read is normalized; a region without one is kept.
        and region (e: IRExpr) : IRExpr =
            if not (hasRead e) then e
            else
                // ONE read and nothing else that acts: the call replaces the read
                // in place (no block), nothing to order
                let light (v: IRExpr) =
                    let n = ref 0
                    iterIRExpr (fun x -> if isRead x then n.Value <- n.Value + 1) v
                    n.Value = 1 && otherwiseInert v && not (readOffSpine v)
                let inPlace (v: IRExpr) = mapIRExpr (fun x -> if isRead x then helperCall x else x) v
                match e with
                | IRLet (id, v, rest) ->
                    let (pre, v') =
                        if not (hasRead v) then ([], v)
                        elif light v then ([], inPlace v)
                        else normStmt v
                    wrapLets pre (IRLet (id, v', region rest))
                | e when light e -> inPlace e
                | e ->
                    let (pre, r) = normStmt e
                    wrapLets pre r
        let topStmt (v: IRExpr) : (IRId * IRExpr) list * IRExpr =
            match v with
            | IRCompute (IRVar (d, _)) when not closure && (inlineValue d).IsSome -> ([], IRCompute (inlineValue d).Value)
            | IRMask (IRVar (d, dty), pred) when not closure && (inlineValue d).IsSome ->
                let h = builder.FreshId()
                ([ (h, IRCompute (inlineValue d).Value) ], IRMask (IRVar (h, dty), pred))
            | _ ->
            if not (hasRead v) || not (admissible v) then ([], v)
            elif not closure && readMaps v = 1 && not (readOffSpine v) && otherwiseInert v then ([], v)
            else
                try normStmt v
                with BodyReadsCannotSplit -> ([], v)
        let rec rewriteBody (e: IRExpr) : IRExpr =
            match e with
            | IRLet (id, v, rest) ->
                let (pre, v') = topStmt v
                wrapLets pre (IRLet (id, v', rewriteBody rest))
            | e ->
                let (pre, r) = topStmt e
                wrapLets pre r
        { f with Body = rewriteBody f.Body }
    // G241: a LAMBDA capturing a deferred module map that the program only
    // CALLS -- bound by a let in a capture-free named function body and
    // applied there by that name, or applied directly; never a kernel, an
    // argument, a returned or stored value, never named by module code or
    // captured by another callable -- runs only inside that function's call, so
    // each read in its body is an ordinary forcing use AT ITS POSITION at each
    // call (the interpreter's evaluation; a closure binding forces nothing).
    // Its reads become helper calls and the map leaves its captures: the C++
    // closure copy-captured the module binder at the binding, which nothing had
    // declared (BL9002, the unused and the called twin alike).
    let namedFns = modul.Functions |> List.filter directCallOnly
    let calledOnly (lid: IRId) : bool =
        if moduleNamed.Contains lid then false else
        let aliases = System.Collections.Generic.HashSet<IRId>()
        let ok = ref true
        let rec walk (e: IRExpr) =
            match e with
            | IRLet (k, IRVar (id, _), b) when id = lid || aliases.Contains id -> aliases.Add k |> ignore; walk b
            | CallOfNamed (id, _, args, _) when id = lid || aliases.Contains id -> List.iter walk args
            | IRVar (id, _) when id = lid || aliases.Contains id -> ok.Value <- false
            | _ -> childrenOf e |> List.iter walk
        for f in namedFns do walk f.Body
        let named (e: IRExpr) = let r = collectVarRefsIR e in r.Contains lid || aliases |> Seq.exists r.Contains
        // EVERY other callable of the PROGRAM -- a lambda, a function used as a
        // value (not direct-call-only: its body may RETURN the closure, which
        // then escapes to module level, where a capturing closure's read is a
        // module force: wave-10 fable review F1), another module's -- must not
        // name the closure or an alias of it at all, nor capture it
        let namedIds = namedFns |> List.map (fun f -> f.Id) |> Set.ofList
        ok.Value
        && allFuncs |> List.forall (fun c ->
               (c.Captures |> List.forall (fun cap -> cap.Id <> lid && not (aliases.Contains cap.Id)))
               && (namedIds.Contains c.Id || c.Id = lid || not (named c.Body)))
    let closures' =
        modul.Functions |> List.choose (fun c ->
            if c.Name.StartsWith "__lambda" && not c.Captures.IsEmpty
               && c.Captures |> List.exists (fun cap -> maps.ContainsKey cap.Id)
               && hasRead c.Body && calledOnly c.Id then
                let c' = processFunc true c
                if obj.ReferenceEquals(c', c) then None
                else
                    // the deferred captures its rewritten body no longer names
                    // (directly or through a callable it names) leave
                    let refs = collectVarRefsIR c'.Body
                    let viaCallables =
                        refs |> Seq.collect (fun r ->
                            match callables.TryGetValue r with
                            | true, g -> g.Captures |> List.map (fun ci -> ci.Id)
                            | _ -> []) |> Set.ofSeq
                    let caps = c'.Captures |> List.filter (fun cap -> not (deferredIds.Contains cap.Id) || refs.Contains cap.Id || viaCallables.Contains cap.Id)
                    Some (c.Id, { c' with Captures = caps })
            else None)
        |> Map.ofList
    // the closure rewrite first: a named body applying such a closure then
    // sees its new captures (no deferred capture left to forbid its rewrite)
    for KeyValue (_, c) in closures' do callables.[c.Id] <- c
    capMemo.Clear()
    let functions' =
        modul.Functions |> List.map (fun f ->
            match Map.tryFind f.Id closures' with
            | Some c -> c
            | None -> processFunc false f)
    let changed = List.exists2 (fun (a: IRCallable) b -> not (obj.ReferenceEquals(a, b))) modul.Functions functions'
    if minted.Count = 0 && not changed then modul
    else
        // each helper just before its first caller (callee before caller in
        // any list-order consumer); the C++ order key is the map's binding
        let byCaller = minted |> Seq.groupBy (fun (_, _, caller) -> caller) |> Map.ofSeq
        { modul with
            Functions =
                functions' |> List.collect (fun f ->
                    match Map.tryFind f.Id byCaller with
                    | Some hs -> (hs |> Seq.map (fun (h, _, _) -> h) |> List.ofSeq) @ [ f ]
                    | None -> [ f ])
            // keyed on the CALLER (emitted just before it): the helper sees the
            // deferred context its caller does -- a map a top-level statement
            // forced before the caller's definition is read stored by both
            DerivedFuncOrigins =
                minted |> Seq.fold (fun acc (h, _, caller) -> Map.add h.Id caller acc) modul.DerivedFuncOrigins }

let lowerTypedProgram (program: TypedProgram) (rawProgram: Program option) (builder: IRBuilder) : IRProgram =
    // (BLADE_PHASE_TIMING: the lowering up to and through the write rules --
    // what an editor tier reporting them would pay; deep review R12, gap 5)
    let swLower = System.Diagnostics.Stopwatch.StartNew()
    // The scratch-reuse plan is keyed by let id and let ids restart per
    // compile, so a stale plan from an earlier program in this flow (the test
    // harness compiles hundreds) must never reach this one.
    Blade.Types.PoolReuseTable.reset ()
    Optimize.HotRowTable.reset ()
    Optimize.CellFoldTable.reset ()
    Optimize.RecStepTable.reset ()
    Optimize.CompactionTable.reset ()
    Optimize.EquiJoinTable.reset ()
    OptimizeSort.SortFormTable.reset ()
    OptimizeSort.SortTerminalTable.reset ()
    OptimizeJoinAgg.JoinAggTable.reset ()
    Optimize.HoistDeclTable.reset ()
    OptimizeStreamGroup.StreamGroupAggTable.reset ()
    Optimize.CompactRowTable.reset ()
    Optimize.OrbitPullTable.reset ()
    Optimize.OrbitActionTable.reset ()
    Optimize.MemberHoistTable.reset ()
    Optimize.GroupAggTable.reset ()
    Optimize.SiblingMapTable.reset ()
    Optimize.EpilogueLegTable.reset ()
    Optimize.CallSpanTable.reset ()
    Optimize.InlineEventSpans.reset ()
    Optimize.AliasElideTable.reset ()
    Optimize.LuCompanionTable.reset ()
    Optimize.GroupViewTable.reset ()
    Optimize.GroupRowMapTable.reset ()
    Optimize.StreamGroupFoldTable.reset ()
    Optimize.KeyedProjectionTable.reset ()
    Optimize.MaskReplayTable.reset ()
    Optimize.SinkStreamTable.reset ()
    Optimize.AssemblyDestTable.reset ()
    Optimize.blasGateProbe <- Blade.LinAlgPatterns.blasAvailable
    Optimize.lapackGateProbe <- Blade.LinAlgPatterns.lapackAvailable
    // The raw decl list callers hand us is the program they PARSED, not the
    // one `TypeCheck.typeCheck` desugared inside itself -- so the icechunk
    // checkout rewrite is applied again here, ahead of Phase 0's
    // `resolveStatics` (whose `providerRoots` scan reads exactly this list,
    // and whose miss is silent). Idempotent -- a rewritten binding loads a
    // path carrying `@`, which the repo-handle scan declines -- and
    // reference-equal for programs with no `import icechunk`. Errors cannot
    // arise on this path: typecheck ran `expand` first and rejected them.
    let rawProgram = rawProgram |> Option.map Blade.ProviderDesugar.desugarOrIdentity
    let env = { emptyTypedEnv() with Builder = builder }
    let mutable currentExports = Map.empty<string, ModuleExport>
    let mutable irModules = []
    
    let rawModules =
        match rawProgram with
        | Some p -> p.Modules |> List.map (fun m -> Some m.Decls)
        | None -> program.Modules |> List.map (fun _ -> None)

    // Hand the back end a name -> span table for its refusals (see
    // IR.recordDeclSpan). The surface program is the ONLY place these spans
    // still exist; a codegen refusal reported without one is a diagnostic the
    // user cannot locate. Recorded for every top-level `let`/`static`/
    // `function`; declarations without a surface form simply have no entry and
    // fall back to `noSpan`, exactly as before.
    IR.resetDeclSpans ()
    match rawProgram with
    | Some p ->
        // Every name a pattern binds points at the same declaration span; a
        // destructuring `let a, b = ...` has one source line for both.
        let rec patNames (pat: Pattern) : string list =
            match pat.Kind with
            | PatVar n -> [n]
            | PatTuple ps -> ps |> List.collect patNames
            | PatCons (h, t) -> patNames h @ patNames t
            | PatStruct (_, flds) -> flds |> List.collect (snd >> patNames)
            | PatVariant (_, Some inner) | PatGuarded (inner, _) | PatTyped (inner, _) -> patNames inner
            | _ -> []
        // Keyed by the name codegen will look the declaration up under, which
        // for a non-main module is its QUALIFIED name (qualifyModuleNames):
        // recording the bare name would both miss the lookup and let an
        // imported module's `x` shadow main's own `x` span.
        p.Modules |> List.iteri (fun i m ->
            let modName = m.Name |> String.concat "."
            let q = moduleQualifier i modName
            let isMain = Blade.Types.TypeOrigins.isMainModule i p.Modules.Length
            let valueKey (n: string) = if isMain then n else $"{q}.{n}"
            let funcKey (n: string) = if isMain then n else $"{moduleCppPrefix q}__{n}"
            for d in m.Decls do
                match d.Value with
                | DeclLet b | DeclStatic b ->
                    for n in patNames b.Pattern do IR.recordDeclSpan (valueKey n) d.Span
                | DeclFunction fd -> IR.recordDeclSpan (funcKey fd.Name) d.Span
                | _ -> ())
    | None -> ()

    for (tmod, rawDecls) in List.zip program.Modules rawModules do
        let moduleName = tmod.Name |> Option.map (String.concat ".") |> Option.defaultValue ""
        let envWithExports = { env with ModuleExports = currentExports }
        let (irModule, exports) = lowerTypedModule envWithExports tmod rawDecls
        // Monomorphize arity-polymorphic functions first: Poly<T^N> packs
        // get expanded into N concrete params per call site. After this,
        // every function has a fixed param count matching its call sites.
        let irModule = IRMono.monomorphizeModule irModule env.Builder
        currentExports <- Map.add moduleName exports currentExports
        irModules <- irModules @ [irModule]

    // HM monomorphization: substitute function-boundary type variables (e.g.
    // `T` in `Array<T like Idx<n>>`, or `T` extracted from `Poly<T^N>`'s base
    // type) with concrete types learned from each call site. Runs after Poly so
    // per-param/per-arg unification is straightforward -- each pack has already
    // been expanded.
    //
    // WHOLE-PROGRAM, and it has to be: specialization is driven by call sites,
    // so a generic DEFINED in module A and CALLED from module B learned nothing
    // while this ran inside the loop -- A saw no call site and dropped it, B did
    // not own it, and the call reached validateIR still carrying `IRTInfer`
    // (BL6001). That is what stopped a Blade-source stdlib from exporting a
    // generic function; `stdlib/stats.blade`'s `mean(row: T^1) -> T^0` is the
    // motivating case. See IRMono.monomorphizeHMFunctionsModules for why merging is
    // sound and how the result is split back; a single-module program is
    // unaffected, down to the ids the builder mints.
    let irModules = IRMono.monomorphizeHMFunctionsModules irModules env.Builder

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
    let irModules = IRMono.shapeMonomorphizeModules irModules env.Builder
    let irModules = irModules |> List.map inlineModuleFunctionAliases
    refuseUncountableStorage irModules
    refuseUnorderableSortKeys irModules
    // R11: every call's arguments and every arithmetic operand, at the instance
    // (before the optimizer inlines a call boundary away).
    refuseArgumentConversions irModules
    // G45: an effectful named map runs once, at its definition, in every
    // scope (before the join refusal: a join over a stored map writes nothing)
    let irModules =
        let allFuncs = irModules |> List.collect (fun m -> m.Functions)
        irModules |> List.map (forceEffectfulModuleLets allFuncs)
    refuseWritingJoinLegs irModules
    refuseForeignKernelCaptures irModules
    let swRules = System.Diagnostics.Stopwatch.StartNew()
    refuseDeferredWritesAndUnorderedIfs irModules
    if Blade.Toolchain.envFlag "BLADE_PHASE_TIMING" false then
        eprintfn "[phase] lower: through the write rules: %d ms (the rules alone: %d ms)" swLower.ElapsedMilliseconds swRules.ElapsedMilliseconds

    // Every struct the program declares, for `zero` at a struct type
    // (IRMono.resolveTypedZerosModule): the struct may live in another module
    // than the zero that names it.
    let zeroStructFields =
        let m =
            irModules
            |> List.collect (fun m -> m.Types)
            |> List.choose (fun td -> match td with IRTDStruct (n, fs) -> Some (n, fs) | _ -> None)
            |> Map.ofList
        fun (name: string) -> Map.tryFind name m
    // Every callable of the program, as the monomorphizers left it: the
    // optimizer resolves a callee defined in ANOTHER module here (a callee's
    // effect summary does not change under the per-module rewrites below).
    // the repro demand on every fold use in a `where repro` body (gap G80):
    // whole-program, so a twin lives in the module that defines its origin
    let (irModules, reproTwins) = reproFoldUsesProgram irModules env.Builder
    let programFuncs = irModules |> List.collect (fun m -> m.Functions)
    // Every module's binding names: the modules share one C++ `main` scope, so
    // a plan that mints an emitter name checks the WHOLE program's spellings
    // (stream-group-aggregate's `__sga` guard, round 4 wave 10 lane a, A3).
    let programBindingNames = lazy (irModules |> List.collect (fun m -> m.Bindings |> List.map (fun b -> b.Name)))
    // Every module's type declarations (the EnumIdx key backstop, R3).
    let programTypeDefs = irModules |> List.collect (fun m -> m.Types)
    // The same snapshot with constant-scrutinee matches folded, as every module's
    // optimizer folds them first: the unseeded-seed resolution judges writes on
    // the code the emitters will see, imported callables included (gap G74).
    let foldedProgramFuncs =
        lazy (programFuncs |> List.map (fun f -> { f with Body = IRMono.foldConstMatchesExpr f.Body }))
    // Every id each module's executable code, types and side tables name
    // (Optimize.moduleReferencedIds: the SAME traversal the census walks --
    // binding values, every side-table operand executed at a binding's site,
    // function bodies and captures, declared and embedded types, provider
    // sources): compaction and private-transpose admission run per module,
    // so ANOTHER module's references must reach them as uncertified uses.
    // Computed lazily once for a multi-module program.
    let moduleRefs =
        lazy (irModules |> List.map (fun m -> (m.Name, Optimize.moduleReferencedIds m)))
    // Call elision's program-wide value-reference census (a callee in another
    // module: Optimize.CallElideProgram), over the modules as they reach the
    // optimizer.
    Optimize.CallElideProgram.install (Optimize.programValueRefs irModules)
    let irModules =
        irModules |> List.map (fun irModule ->
        // Rewrite raw array-typed binops into object_for combinators now
        // that pack-element operand types are concrete (post Poly+HM):
        // `A[i] + A[j]` on rank>=1 pack elements couldn't be recognized as
        // an array op at lowering time (its element type was an unresolved
        // var), so it gets the same elementwise-loop lowering top-level
        // `x + y` does.
        // Generic zeros first: each specialization's `zero` becomes its own
        // literal (so a broadcast built below carries the right scalar type).
        let irModule = IRMono.resolveTypedZerosModule zeroStructFields irModule
        // Float literals beside a generic operand a clone made Float32 /
        // Complex64 adapt now, as the checker adapts them beside a concrete one.
        let irModule = IRMono.adaptFloatLiteralsModule irModule
        // ...and a generic `(*)` fold's implicit `1` seed takes its instance's type.
        let irModule = IRMono.adaptFoldOneSeedsModule irModule
        // ...and a specialization at an integer EnumIdx that computes on its
        // values is refused (keys are represented as positions, D2).
        IRMono.refuseEnumKeyArithModule programTypeDefs irModule
        let irModule = IRMono.lowerArrayBinOpsModule irModule env.Builder programFuncs
        // Array operators recovered after arity specialization can expose a
        // literal-only scalar base inside a newly minted broadcast kernel.
        // Its concrete cell partner now supplies the literal width.
        let irModule = IRMono.adaptFloatLiteralsModule irModule
        // every unseeded fold's seed by its EFFECTIVE route (gap G74), before any
        // optimizer pass reads a seed (the repro twins of gap G80 are in place)
        let irModule = resolveUnseededSeedsModule (foldedProgramFuncs.Force()) irModule
        // an extent read of a deferred map reads its operand's shape (gap G234)
        let irModule = resolveDeferredExtentsModule irModule
        // Every id another module names: the transpose normal form's and the
        // read-only alias plan's never-written closure treats such a name as
        // unjudged (Blade.Optimize.aliasWriteClosure). This is also PM19's and
        // C04's COMPLETE foreign-demand evidence. A private transpose has no
        // CompoundInits entry, so that shortcut would silently erase foreign
        // binding/type/side-payload observers.
        let foreignRefs =
            if irModules.Length < 2 then Set.empty
            else moduleRefs.Force() |> List.filter (fun (n, _) -> n <> irModule.Name) |> List.map snd |> Set.unionMany
        // PM19 terminal selection must run before IRLift/CSE and every
        // schedule side table (the transpose normal form's alias plan
        // included). It reads explicit seeds only, after their resolution
        // above; a removed private mask can never orphan a membership plan.
        let irModule = Optimize.fuseSelectedTerminalModule env.Builder programFuncs foreignRefs irModule
        // The semantic-equivalence optimization stage (Blade.Optimize --
        // see its charter): constant-scrutinee match folding (which also
        // resolves symbolic ranks per specialization), elementwise-chain
        // fusion and kernel-invariant hoisting (BLADE_HOIST; its callee
        // facts resolve through `programFuncs`, the whole program's
        // callables), in that order. Runs after the monomorphizers and the
        // binop rewrite (so specialized literals and late-minted
        // pack-element combinators are candidates), before
        // liftInlineFormsModule (so lifted forms inherit the optimized
        // shape), and in Lowering rather than a back end so codegen and the
        // interpreter consume one tree. Per-pass gates: BLADE_FUSION,
        // BLADE_FREEZE_IDIOM (the latter's recognition runs pre-lowering at
        // inferRecArray, where the idiom's shape is still declarative), and
        // BLADE_CSE / BLADE_POOL_REUSE for the two passes below. Because
        // both lanes consume the optimized tree, the interpreter differential
        // CANNOT see an optimizer bug (both print the same wrong answer); the
        // lane that can is `blade test opt-diff` (tests/OptDiff.fs), which
        // compares every gate OFF against every gate ON.
        // The transpose normal form (Blade.Optimize.normalizeTransposesModule,
        // P2 of docs/plans/plan-identity-transpose-compute.md): a chain of
        // transposes composes to one permutation, an identity disappears. A
        // semantic normal form every lane consumes; first among the optimizer
        // passes, so the passes below (C04's private transpose partial folds
        // included) see the base. Gate: BLADE_TRANSPOSE_ELIDE.
        let irModule = Optimize.normalizeTransposesModule env.Builder programFuncs foreignRefs (irModules.Length < 2) irModule
        // (a single-module program: compute elision's top-level mode reads the
        // print selection's names, which name the program's own bindings)
        let irModule = Optimize.optimizeModuleWithForeignCensus env.Builder programFuncs (irModules.Length < 2) foreignRefs irModule
        // Lift inline forms (mask/sort/intersect/union/group_by/group_keys
        // appearing in non-let-RHS positions) into auto-let bindings so
        // codegen sees the canonical "let-bound" pattern uniformly.
        let irModule = IRLift.liftInlineFormsModule irModule env.Builder
        // S2/S4, SECOND APPLICATION -- and it has to be here, not only at
        // `lowerTypedExpr` time. `lowerArrayBinOpsModule` SYNTHESIZES fresh
        // `IRApplyCombinator` nodes (an array-vs-array binop becomes
        // `method_for(zip ..) <@> kernel`), and it runs long after
        // `forceCallableBody` walked each callable's body. A synthesized
        // combinator landing in a FUNCTION-BODY let RHS therefore arrives at
        // codegen unforced, which is the shape genFuncBodyScoped refuses
        // outright (BL9001, "'__vN' as a name with no declaration behind it").
        // Measured on `examples/lswosa.blade`: `mod2`/`cos_sum`/`sin_sum` are
        // each a sum of two rank-1 per-frequency folds, six such lets in one
        // body.
        //
        // FUNCTIONS ONLY. At module level the deferred protocol is real --
        // `genBinding`/`genComputeBinding` materialize a bare combinator under
        // the binding's own name -- so `Bindings` is deliberately untouched.
        // Both passes are idempotent (an already-`IRCompute`-wrapped node
        // matches neither), so re-running over bodies that were forced at
        // lowering changes nothing.
        let irModule =
            { irModule with
                Functions =
                    irModule.Functions
                    |> List.map (fun f -> { f with Body = forceCallableBody f.Body }) }
        // Scratch reuse across barriers (Blade.Optimize.planPoolReuse): runs
        // HERE, after the second S2/S4 application, because its candidates
        // are the forced (`IRCompute`-wrapped) body-local applies and the
        // bare ones left behind are exactly the deferred join operands it
        // must see through. Records into Types.PoolReuseTable for codegen.
        // Let-level CSE over repeatable values first (fewer lets, fewer
        // pools), then the scratch-reuse plan over what remains. Gates:
        // BLADE_CSE, BLADE_POOL_REUSE (Optimize.cseEnabled/poolReuseEnabled).
        let irModule = Optimize.cseModule irModule
        Optimize.planPoolReuse (Optimize.scheduleMayRetainIn programFuncs irModule) irModule
        // Hot-row fold scheduling (Blade.Optimize.planHotRowFolds): a side
        // table like the scratch-reuse plan, read by both emitters; the
        // interpreter runs the tree as written. Gate: BLADE_HOT_ROW.
        Optimize.planHotRowFolds env.Builder programFuncs irModule
        // Membership hoisting (Blade.Optimize.planMembershipHoist, round 3
        // B-T1): a `contains(B, x)` probed once per cell of a host binding
        // (mask predicate, map or fold kernel) reads ONE search structure
        // built over the stored set B right before the host; side table,
        // both emitters; the interpreter runs the as-written scan. Gate:
        // BLADE_MEMBER_HOIST. It runs BEFORE compaction fusion, whose
        // membership-probe certificate (T4c, Part B) reads its plans.
        Optimize.planMembershipHoist programFuncs irModule
        // Compaction fusion (Blade.Optimize.planCompactionFusion): the
        // mask -> compound -> fold chain's side table, read by both emitters;
        // the interpreter runs the tree as written. Gate:
        // BLADE_COMPACTION_FUSION.
        Optimize.planCompactionFusion programFuncs foreignRefs irModule
        // Hash equijoin (Blade.Optimize.planEquiJoin, round 3 W3-2, item
        // T2): an inline rank-2 equijoin compound becomes a stable CSR build
        // over the right keys plus a left-major / right-minor probe in the
        // C++ lane; side table, the interpreter runs the as-written tree.
        // Gate: BLADE_HASH_JOIN.
        Optimize.planEquiJoin programFuncs foreignRefs irModule
        // Join to aggregate (Blade.OptimizeJoinAgg.planJoinAggregate, round
        // 4 wave 4 D2): an unprinted equijoin read only by strict folds and
        // extents is never stored -- each fold walks the probe at its own
        // site. Side table, the C++ emitter. Gate: BLADE_JOIN_AGG.
        OptimizeJoinAgg.planJoinAggregate programFuncs foreignRefs irModule
        // Streamed key grouping to aggregate (Blade.OptimizeStreamGroup, round
        // 4 wave 4 H2 / ST08): an unprinted group_by over a streamed variable
        // read only by per-group strict folds keeps one state per group per
        // fold in the windowed pass, no pool. Gate: BLADE_STREAM_GROUP_AGG.
        OptimizeStreamGroup.planStreamGroupAggregate programFuncs (programBindingNames.Force()) foreignRefs irModule
        // The sort form (Blade.OptimizeSort.planSortForms, round 4 wave 4
        // D1): a range operand sorted as its iota in place, a total pure
        // key cached once per element; side table, the C++ emitter reads
        // it, the interpreter runs the as-written sort. Gate: BLADE_SORT_FORM.
        OptimizeSort.planSortForms programFuncs irModule
        // The sort terminal (Blade.OptimizeSort.planSortTerminal, round 4
        // wave 5 D5 / G127): a sort read only by one strict fold feeds the fold
        // in sorted order under the spool grant; s never stored. Gate:
        // BLADE_SORT_TERMINAL.
        OptimizeSort.planSortTerminal programFuncs (foreignRefs) (irModules.Length < 2) irModule
        // Compact row classes (Blade.Optimize.planCompactRowClasses): a
        // compact rank-2 producer's orbit-fold class partials computed behind
        // each packed row; side table, both emitters. Gate:
        // BLADE_COMPACT_ROW_CLASSES.
        Optimize.planCompactRowClasses programFuncs irModule
        // The row-owned pull scatter's five-party audit (Blade.Optimize.
        // planOrbitPull, round 3 D-P3): facts only, read by the C++ emitter's
        // rank-3 scatter dispatch; no IR change. Knob: BLADE_ORBIT_PULL (the
        // emitter's).
        Optimize.planOrbitPull programFuncs irModule
        // The row orbit action's five-party audit (Blade.Optimize.
        // planOrbitActions, round 3 D-P2 / gap G73): facts only, read by both
        // emitters' action admission (OrbitFold.orbitActionAdmissible); no IR
        // change. Knob: BLADE_ORBIT_ACTIONS (the emitters').
        Optimize.planOrbitActions programFuncs irModule
        // Aggregate-only grouping (Blade.Optimize.planGroupAggregate, round 3
        // W3-1 / gap G71): a function-body `group_keys` consumed only by
        // per-group aggregate peels advances every peel's chain in ONE pass
        // over the rows in source order (no perm, no gather); side table,
        // both emitters; the interpreter runs the as-written CSR. Gate:
        // BLADE_GROUP_AGG.
        Optimize.planGroupAggregate programFuncs irModule
        // The read-only alias plan (Blade.Optimize.planAliasElision): an
        // assignable top-level `let X = Y` whose two names are never written
        // shares Y's storage instead of copying it (the symmetric transpose
        // alias, the identity transpose chain). A side table, both emitters;
        // the interpreter keeps its copy. Last, over the final tree. Gate:
        // BLADE_TRANSPOSE_ELIDE.
        Optimize.planAliasElision programFuncs (foreignRefs) irModule
        // The LU column-major companion (Blade.Optimize.planLuCompanion, gap
        // G109): a never-written top-level factor keeps ?getrf's column-major
        // scratch for its top-level solves (the routed arm only). Side table,
        // C++ emitter. Gate: BLADE_LU_COMPANION.
        Optimize.planLuCompanion programFuncs (foreignRefs) irModule
        // The structural group view (Blade.Optimize.planStructuralGroupView,
        // PM57): a top-level group_by over contiguous segments whose source,
        // grouped array and derived values are never written reads its rows
        // in the source's storage (and its ungroup shares it). Verdicts only;
        // the C++ emitter records the decision. Gate: BLADE_GROUP_VIEW.
        Optimize.planStructuralGroupView programFuncs (foreignRefs) irModule
        // The group row map share (Blade.Optimize.planGroupRowMap, G90): a
        // top-level `ungroup` of a per-group map over a structural grouping
        // shares the map's pool (its rows are already in axis order). Verdicts
        // only; the C++ emitter records the decision. Gate: BLADE_GROUP_ROWMAP.
        Optimize.planGroupRowMap programFuncs (foreignRefs) irModule
        // The stream group fold (Blade.Optimize.planStreamGroupFold, lane G
        // item G4 / G133): a group_by over a streamed variable whose one
        // reader is the next binding's fold peel streams each run into one
        // window and folds it there; no pool. Gate: BLADE_STREAM_GROUP_FOLD.
        Optimize.planStreamGroupFold programFuncs (foreignRefs) (irModules.Length < 2) irModule
        // The keyed-cell projection (Blade.Optimize.planKeyedCellProjection,
        // lane G item G6 / PM44): a `.read` used only by literal keyed reads
        // keeps its whole validating sweep but no array. Gate:
        // BLADE_KEYED_PROJECTION.
        Optimize.planKeyedCellProjection (foreignRefs) (irModules.Length < 2) irModule
        // The stream mask replay (Blade.Optimize.planStreamMaskReplay, lane D
        // item D1 / G142): a mask over a streamed variable read only by its
        // compound is never stored -- its sweep counts, the gather replays the
        // predicate. Reads CompactionTable. Gate: BLADE_STREAM_MASK_REPLAY.
        Optimize.planStreamMaskReplay programFuncs (foreignRefs) (irModules.Length < 2) irModule
        // The checker's stored-alias identity verdicts (plan 2.1 second step,
        // rule `alias-identity v1`), each applied one cross-checked against
        // the plan above. Records only; no IR change.
        Optimize.recordAliasIdentity programFuncs (foreignRefs) irModule
        // The streamed provider sink (Blade.Optimize.planProviderSinkStream,
        // round 4 wave 3, D1 / gap G91): a top-level map whose only consumer
        // is one Zarr write is produced and written in bounded blocks at the
        // write, never stored. After every plan whose claims it reads, before
        // the sibling maps (which read its claim). Verdicts only; the C++
        // emitter records the applied decision. Gate: BLADE_SINK_STREAM.
        Optimize.planProviderSinkStream programFuncs foreignRefs (irModules.Length < 2) irModule
        // The assembly destination fill (Blade.Optimize.planAssemblyDest, round 4
        // wave 4, F2 / PM26): a private cellwise map whose only observer is a
        // transpose / stack / join is never stored; the assembly computes each
        // cell into its destination. After the sink stream (whose claims it
        // reads), before the sibling maps (which read its claim). Verdicts only;
        // the C++ emitter records the applied decision. Gate: BLADE_ASSEMBLY_DEST.
        Optimize.planAssemblyDest programFuncs foreignRefs (irModules.Length < 2) irModule
        // Sibling maps (Blade.Optimize.planSiblingMaps, round 4 wave 2, PM04):
        // two to eight stored maps of one spine over one input index space share
        // ONE traversal, fused at the last sibling; side table, both emitters;
        // the interpreter runs the maps as written. LAST of the plans: it reads
        // every other plan's claims and the scratch-reuse decisions, and the
        // early pool-release schedule below reads its protected ids. Gate:
        // BLADE_SIBLING_MAPS.
        Optimize.planSiblingMaps programFuncs foreignRefs (irModules.Length < 2) irModule
        irModule)
    // A05 consumes every module's final executable/type/side payload and ALL
    // established owners. No planner runs after these typed resource barriers.
    let irModules = EarlyPoolRelease.scheduleModules builder irModules
    // G239: a function body forces a deferred module map at each read
    // position (splitDeferredBodyReadsModule): after every planner and the
    // release schedule (its lets are scalar and own no pool), before
    // dead-polymorph elimination and qualification.
    let irModules =
        irModules |> List.map (splitDeferredBodyReadsModule builder irModules)

    // DEAD-POLYMORPH ELIMINATION, last and whole-program: a lambda lifted out
    // of a never-instantiated generic keeps that generic's type vars in its
    // body, and (with concrete params) is not an hmFunc, so no monomorphizer
    // drops it. Unreachable from any binding, it can only ever be a BL6001.
    // Must run after EVERY specializing pass -- each can make a function
    // concrete or mint new references -- and before validateIR.
    // The declaration-span table TRAVELS WITH THE PROGRAM (gap G76): carried
    // in the record (IRProgram.DeclSpans), so every `{ p with .. }` copy --
    // qualifyModuleNames' multi-module arm among them (the second Codex look
    // of W4-3, NEW 3: an object-keyed attachment made before it was lost for
    // every multi-module program) -- keeps it; a consumer in another flow
    // (the interpreter differential) installs it.
    let program = IRValidate.eliminateDeadPolymorphs { Modules = irModules; DeclSpans = Map.empty }
    // Module namespacing, after every pass that could mint or specialize a
    // member (see qualifyModuleNames).
    let program = program |> qualifyModuleNames
    // a repro twin is located at its origin's declaration (gap G80)
    copyTwinDeclSpans program reproTwins
    // a kernel-invariant hoist's minted callables are located as the per-cell
    // evaluation was (gap G151)
    copyHoistDeclSpans program (Optimize.HoistDeclTable.all ())
    { program with DeclSpans = (IR.declSpansCell ()).Value }

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
/// `skipPins` drops the BL4010 PIN SUGGESTIONS for `--strict-pins`, which has
/// already re-reported exactly those as errors (every other BL4010 -- the
/// constrained-domain advisories -- stays). BL4011 and BL4014 are deliberately
/// NOT dropped: strict-pins owns the STORAGE decision, and a certificate
/// owns no storage decision at all, so neither has a promoted-to-error twin
/// a filter here would be de-duplicating.
let typeCheckWarningDiagnostics (skipPins: bool) : Blade.Diagnostics.Diagnostic list =
    // Only the BL4010s `--strict-pins` actually promoted: a pin suggestion is
    // written to BOTH channels with one message and one span, so that pair is
    // the identity. BL4010 is also the code of the constrained-domain
    // advisories (a `range<Domain>` enumerated as a key table, or proved
    // empty), which are not pin suggestions, have no promoted-to-error twin,
    // and so must survive the filter.
    let promoted =
        if skipPins then Set.ofList (Blade.TypeCheckIde.PinSuggestions.get ()) else Set.empty
    let own =
        Blade.TypeEnv.WarningLog.get ()
        |> List.filter (fun d -> not (d.Code = "BL4010" && promoted.Contains (d.Message, d.Span)))
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
             with Blade.Diagnostics.BladeDiagnosticException d -> Error d.Message | ex -> Error ex.Message)
        | Error errors ->
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error $"Parse error at {e.Line}:{e.Col}: {e.Message}"

/// Everything `lowerDiag` does AFTER the parse. Shared by the single-file,
/// multi-file and already-parsed entry points so they cannot drift.
/// Env-gated phase timing (`BLADE_PHASE_TIMING=1`): one `[phase] <name>: <ms>`
/// line per pipeline phase on stderr. See docs/plan-compile-speed.md Stage 0.
let phaseTimingEnabled () = Blade.Toolchain.envFlag "BLADE_PHASE_TIMING" false

let phaseMark (sw: System.Diagnostics.Stopwatch) (name: string) : unit =
    if phaseTimingEnabled () then
        eprintfn "[phase] %s: %d ms" name sw.ElapsedMilliseconds
    sw.Restart()

let private lowerCheckedProgram (program: Program)
    : Result<IRProgram * string list, Blade.Diagnostics.Diagnostic list> =
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let tc = Blade.TypeCheck.typeCheck program
    phaseMark sw "typecheck"
    match tc with
    | Error errors ->
        Error (errors |> List.map Blade.TypeEnv.diagnosticOfCompileError)
    | Ok (typedProgram, builder, warnings) ->
        // Lowering can THROW when a compile-time provider load fails
        // (e.g. `netcdf.load("missing.nc")` raises from tryInvokeProvider).
        // Convert it to a coded diagnostic so the compile driver reports it
        // cleanly instead of crashing.
        try
            let r = Ok (lowerTypedProgram typedProgram (Some program) builder, warnings)
            phaseMark sw "lower"
            r
        with
        // A phase that refuses by exception with its OWN coded diagnostic
        // (IRMono's base-case-free recursion refusal) keeps its code.
        | Blade.Diagnostics.BladeDiagnosticException d -> Error [ d ]
        // Anything else (a compile-time provider load that failed, a stage
        // outside Lowering refusing by exception) is BL6001's "a lowering
        // stage reported a failure" -- BL6002 is the positional refusal.
        | ex ->
            Error [ Blade.Diagnostics.mkError "BL6001" Blade.Diagnostics.PhIRValidate Blade.Ast.noSpan ex.Message ]

/// Structured-diagnostics entry: like `lower`, but errors stay as coded,
/// spanned Diagnostics, warnings come back structured, and the retained
/// source text returns as a SourceMap for snippet rendering. `fileName`
/// (when known) is stamped into spans and keys the SourceMap.
let lowerDiag (fileName: string option) (source: string)
    : Result<IRProgram * string list, Blade.Diagnostics.Diagnostic list> * Blade.Diagnostics.SourceMap =
    Blade.PerfCounters.refresh ()
    let key = defaultArg fileName "<input>"
    let sm = Blade.Diagnostics.SourceMap.ofSources [ key, source ]
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let parsed = Blade.Parser.parseProgramWithFile fileName source
    phaseMark sw "parse"
    let result =
        match parsed with
        | Error e -> Error [ Blade.Parser.diagnosticOfParseError fileName e ]
        | Ok program -> lowerCheckedProgram program
    result, sm

/// Multi-FILE twin of `lowerDiag`. `sources` is (path, source) in dependency
/// order with the ENTRY LAST -- what `ModuleResolve.resolveEntry` returns --
/// and every span keeps its own file, so the SourceMap (keyed on the same
/// paths) renders snippets for members as readily as for the entry.
///
/// Parsing goes through `ModuleResolve.parseResolved` rather than
/// `Parser.parseMultiSource` because the latter renames a header-less module
/// after its FILE NAME, which is right for the corpus harness and wrong for an
/// absolute path; see the note there.
let lowerDiagMulti (sources: (string * string) list)
    : Result<IRProgram * string list, Blade.Diagnostics.Diagnostic list> * Blade.Diagnostics.SourceMap =
    Blade.PerfCounters.refresh ()
    let sm = Blade.Diagnostics.SourceMap.ofSources sources
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let parsed = Blade.ModuleResolve.parseResolved sources
    phaseMark sw "parse(multi)"
    let result =
        match parsed with
        | Error d -> Error [ d ]
        | Ok program -> lowerCheckedProgram program
    result, sm

/// The CLI's front door: resolve `filePath`'s imports to files, then lower the
/// whole set.
///
/// The entry file is parsed HERE, once, and the AST is handed to the resolver
/// (which needs only its header and imports) and then to the checker. Both
/// used to parse it independently, so every compile paid for the entry file
/// twice.
///
/// A file whose imports all resolve to builtin pseudo-modules (or that has no
/// imports at all) is checked from that one parse with the SourceMap keyed on
/// `filePath` -- same key, same spans, same everything as before the module
/// layer existed.
let lowerFileDiag (filePath: string) (source: string)
    : Result<IRProgram * string list, Blade.Diagnostics.Diagnostic list> * Blade.Diagnostics.SourceMap =
    Blade.PerfCounters.refresh ()
    let selfSm () = Blade.Diagnostics.SourceMap.ofSources [ filePath, source ]
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let parsedEntry = Blade.Parser.parseProgramWithFile (Some filePath) source
    phaseMark sw "parse"
    match parsedEntry with
    | Error e ->
        // A file that does not parse has no imports to resolve: resolution
        // swallowed this error and the single-file path reported it.
        Error [ Blade.Parser.diagnosticOfParseError (Some filePath) e ], selfSm ()
    | Ok program ->
        let r =
            match program.Modules with
            | [ m ] -> Blade.ModuleResolve.resolveParsedEntry filePath source m
            | _ -> Blade.ModuleResolve.resolveEntry filePath source
        match r.Errors, r.Files with
        | [], [ _single ] -> lowerCheckedProgram program, selfSm ()
        | [], files ->
            let sm = Blade.Diagnostics.SourceMap.ofSources (Blade.ModuleResolve.sourcesOf files)
            sw.Restart()
            let parsedAll = Blade.ModuleResolve.parseResolvedFiles files
            phaseMark sw "parse(members)"
            let result =
                match parsedAll with
                | Error d -> Error [ d ]
                | Ok whole -> lowerCheckedProgram whole
            result, sm
        | ds, _ -> Error ds, Blade.ModuleResolve.sourceMapOf r

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
    // Resolve FILE-BACKED imports (stdlib `units.SI`, `plot`, ...) exactly
    // like the path-bearing entry points do, so the corpus lane exercises the
    // same program the run/serve lanes compile. The synthetic entry path
    // anchors the stdlib probe at the working directory (the corpus runs from
    // the repo root, where <cwd>/stdlib exists); pseudo-modules (ml, display,
    // ...) are exempt inside the resolver, and a source with no file imports
    // resolves to just itself -- the historical single-source pipeline.
    // Before this, `import units.SI` in a corpus test was a silent no-op and
    // every unit name it was supposed to bring in degraded to a bare float.
    let entryPath =
        System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "__corpus_entry__.blade")
    let r = Blade.ModuleResolve.resolveEntry entryPath source
    match r.Errors with
    | (d: Blade.Diagnostics.Diagnostic) :: _ ->
        Error $"{d.Code}: {d.Message}", []
    | [] ->
    match Blade.ModuleResolve.parseResolvedFiles r.Files with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, _) ->
            let warnings = typeCheckWarningDiagnostics false
            let result =
                // Lowering can THROW on a failed compile-time provider load; keep
                // this convenience entry point from surfacing an unhandled exception.
                try Ok (lowerTypedProgram typedProgram (Some program) builder)
                with Blade.Diagnostics.BladeDiagnosticException d -> Error d.Message | ex -> Error ex.Message
            result, warnings
        | Error errors ->
            let warnings = typeCheckWarningDiagnostics false
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs), warnings
    | Error d ->
        Error $"Parse error: {d.Message}", []

/// Lower multiple source files into a single IR program with cross-module imports
let lowerMultiSource (sources: (string * string) list) : Result<IRProgram, string> =
    match Blade.Parser.parseMultiSource sources with
    | Ok program ->
        match Blade.TypeCheck.typeCheck program with
        | Ok (typedProgram, builder, _) ->
            printTypeCheckWarnings false None false
            // Lowering can THROW on a failed compile-time provider load.
            (try Ok (lowerTypedProgram typedProgram (Some program) builder)
             with Blade.Diagnostics.BladeDiagnosticException d -> Error d.Message | ex -> Error ex.Message)
        | Error errors ->
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs)
    | Error e -> Error $"Parse error at {e.Line}:{e.Col}: {e.Message}"

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
                with Blade.Diagnostics.BladeDiagnosticException d -> Error d.Message | ex -> Error ex.Message
            result, warnings
        | Error errors ->
            let warnings = typeCheckWarningDiagnostics false
            let msgs = errors |> List.map Blade.TypeEnv.formatCompileError
            Error (String.concat "\n" msgs), warnings
    | Error e ->
        Error $"Parse error at {e.Line}:{e.Col}: {e.Message}", []
