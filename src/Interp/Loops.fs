// Blade tree-walking interpreter — loop-nest interpreter (Milestone M2).
//
// The heart of M2: turns the deferred/force combinator algebra and the
// dense loop-nest / reduction machinery into runtime Values that BYTE-MATCH
// the compiled C++ (the differential gate). The central anti-drift invariant
// is that this module consumes the SAME `LoopNestCodeGen` structure CodeGen
// renders — built by calling `IR.buildLoopNestCodeGen` DIRECTLY (never
// re-derived) — so iteration order, bound formulas (Extent − ΣDeps − Strict),
// per-level element peeling, and reduction seed/order/empty semantics cannot
// diverge from the compiled binary.
//
// Ground truth (line refs to the scout-time tree): genApplyCombinator
// (CodeGen.fs:5427), genLoopNestStreamed (3599), genElementBindingNew (2944),
// genForLoopHeader/genLoopBoundExpr (3381/3344), genComputeBinding +
// resolveComputation + applyFunctorWrappers (7473/7480/7534),
// genReduceBinding (8619), genReduceComputeBinding (8714), genParallelTree
// (6749), buildLoopNestCodeGen (IR.fs:2824).
//
// AsyncLocal finding (risk #3, probed): AsyncLocal DOES flow into
// Runtime.runOnLargeStack's worker thread (ExecutionContext capture on
// Thread.Start), AND a set performed on the worker is visible within the same
// worker synchronously. `withCallablesContext` installs the module's callables
// table via IR.setCallablesContext; Run.fs wraps the whole run in it on the
// worker thread, so buildLoopNestCodeGen's resolveKernel always resolves.
//
// Compiled AFTER Interp/Core.fs (references Core.evalExpr, InterpState,
// InterpHooks) and Interp/ArrayOps.fs (dense storage primitives), BEFORE
// Interp/Print.fs. Run.fs installs { EvalArrayNode = evalArrayNode;
// Force = force } into InterpState.Hooks.
module Blade.Interp.Loops

open System.Collections.Generic
open Blade.Types
open Blade.IR
open Blade.Interp.Value
open Blade.Interp.Core

module N = Blade.Interp.Numerics
module A = Blade.Interp.ArrayOps

// ============================================================================
// AnalysisContext install (buildLoopNestCodeGen's resolveKernel dependency)
// ============================================================================

/// Install the module's callables table into the AsyncLocal AnalysisContext for
/// the duration of `f`, restoring the prior context afterward. MUST wrap every
/// entry point that reaches buildLoopNestCodeGen (resolveKernel reads the table).
/// Run.fs calls this once around the whole run on the large-stack worker thread.
let withCallablesContext (modul: IRModule) (f: unit -> 'a) : 'a =
    let table = buildCallablesTableForModule modul
    let prev = setCallablesContext table
    try f ()
    finally restoreAnalysisContext prev

// ============================================================================
// Small value coercions (local mirrors of Core's private ones)
// ============================================================================

let private toI64 (v: Value) : int64 =
    match v with
    | VInt n -> n | VInt32 n -> int64 n
    | VFloat f -> int64 f | VFloat32 f -> int64 (float f)
    | VBool b -> (if b then 1L else 0L)
    | VChar c -> int64 (int c)
    | _ -> 0L

/// C++ truthiness of a kernel value for `<|>` (choice) and guard: `x != 0`.
let private isNonZero (v: Value) : bool =
    match v with
    | VBool b -> b
    | VInt n -> n <> 0L
    | VInt32 n -> n <> 0
    | VFloat f -> f <> 0.0
    | VFloat32 f -> f <> 0.0f
    | VComplex (r, i) -> r <> 0.0 || i <> 0.0
    | _ -> false

/// The zero seed of a scalar element type — mirrors `T name = 0;` (the scalar
/// accumulator declaration in genApplyCombinator's IRTScalar branch).
let private zeroOfElem (et: ElemType) : Value =
    match et with
    | ETInt64 | ETInt32 -> VInt 0L
    | ETBool -> VBool false
    | ETComplex128 | ETComplex64 -> VComplex (0.0, 0.0)
    | _ -> VFloat 0.0

let private nodeCase (e: IRExpr) : string =
    let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<IRExpr>)
    case.Name

/// Count the fully-flattened tuple leaves of a Value (mirrors Core.countLeaves /
/// IR.flattenTupleLeaves: only VTuple recurses; every other value is one leaf).
let rec private countLeaves (v: Value) : int =
    match v with
    | VTuple els -> els |> Array.sumBy countLeaves
    | _ -> 1

/// Project element `i` from a (forced) tuple value — structural (get<i>) or flat
/// (leaf of the fully-flattened tuple). Mirrors Core.projectStruct / projectFlat.
let rec private projectValue (v: Value) (i: int) (isFlat: bool) : Value =
    if not isFlat then
        match v with
        | VTuple els when i >= 0 && i < els.Length -> els.[i]
        | _ -> raise (InterpUnsupported "tuple projection of a non-tuple (deferred tuple did not force to a VTuple)")
    else
        match v with
        | VTuple els ->
            let mutable offset = 0
            let mutable result = None
            let mutable k = 0
            while result.IsNone && k < els.Length do
                let c = countLeaves els.[k]
                if i < offset + c then
                    match els.[k] with
                    | VTuple _ -> result <- Some (projectValue els.[k] (i - offset) true)
                    | leaf -> result <- Some leaf
                offset <- offset + c
                k <- k + 1
            match result with
            | Some r -> r
            | None -> raise (InterpUnsupported "flat tuple projection index out of range")
        | leaf -> leaf

// ============================================================================
// Nest input sources + output sinks
// ============================================================================

/// A resolved input to a nest, keyed by ArrayPosition. Virtual sources carry no
/// store: their element values are computed from the loop index at peel time.
type private ArraySource =
    | SReal of BladeArray
    | SVirtual

/// Output sink for interpretNest: dense cell writes (array output) OR a scalar
/// fold accumulator (scalar-output apply, or a reduce-over-computation leaf).
type private OutTarget =
    | OutArray of BladeArray
    | OutFold of acc: ValueRef * wrapper: (Value -> Value -> Value)

// ============================================================================
// applyFunctorWrappers — ported from CodeGen.genComputeBinding (7534).
// Folds functor-map wrappers into the ApplyInfo kernel body (beta-reduce +
// synthetic-callable registration in the AnalysisContext, which the
// interpreter's own resolveKernel then reads). Uses st.Builder for FreshId.
// ============================================================================

let private applyFunctorWrappers (st: InterpState) (info: ApplyInfo) (wrappers: IRExpr list) : ApplyInfo =
    if List.isEmpty wrappers then info
    else
        let betaReduce (wrapper: IRExpr) (body: IRExpr) : IRExpr =
            match resolveCallable wrapper with
            | Some c when c.Params.Length = 1 ->
                let paramId = c.Params.[0].VarId
                let rec subst (expr: IRExpr) =
                    match expr with
                    | IRVar (id, _) when id = paramId -> body
                    | IRVar _ | IRLit _ | IRParam _ -> expr
                    | IRBinOp (m, op, l, r) -> IRBinOp (m, op, subst l, subst r)
                    | IRUnaryOp (op, e) -> IRUnaryOp (op, subst e)
                    | IRIf (c2, t, e) -> IRIf (subst c2, subst t, subst e)
                    | IRApp (f, args, rt) -> IRApp (subst f, args |> List.map subst, rt)
                    | IRIndex (a2, idxs, ty) -> IRIndex (subst a2, idxs |> List.map subst, ty)
                    | IRTuple es -> IRTuple (es |> List.map subst)
                    | IRComplex (re, im) -> IRComplex (subst re, subst im)
                    | IRTupleProj (e, i, flat) -> IRTupleProj (subst e, i, flat)
                    | IRFieldAccess (e, f) -> IRFieldAccess (subst e, f)
                    | IRLet (id, v, b) -> IRLet (id, subst v, subst b)
                    | _ -> expr
                subst c.Body
            | _ ->
                let retTy =
                    match resolveCallable wrapper with
                    | Some c -> c.RetType
                    | None -> IRTScalar ETFloat64
                IRApp (wrapper, [body], retTy)
        let wrappedKernel =
            let wrapBody (body: IRExpr) = wrappers |> List.fold (fun b w -> betaReduce w b) body
            let buildInline (c: IRCallable) : IRExpr =
                let synthetic = { c with Id = st.Builder.FreshId(); Body = wrapBody c.Body }
                registerSyntheticCallable synthetic
            mapKernelInner buildInline info.Kernel
        let newOutputType =
            match wrappers |> List.tryHead with
            | Some w -> (match resolveCallable w with Some c -> c.RetType | None -> info.OutputType)
            | None -> info.OutputType
        let adjustedOutputType =
            match info.OutputType, newOutputType with
            | ArrayElem arr, IRTScalar et -> mkArrayLike { arr with ElemType = IRTScalar et }
            | _ -> newOutputType
        { info with Kernel = wrappedKernel; OutputType = adjustedOutputType }

// ============================================================================
// Input gating — mirror CodeGen's ragged/grouped/compound/mpi refusals so the
// differential gate SKIP-UNSUPPORTEDs the exact categories CodeGen also gates.
// ============================================================================

let private raggedFamilyOrCompound (ix: IRIndexType) : bool =
    match ix.IxKind with
    | IxKRagged | IxKRaggedInline | IxKRaggedOpaque
    | IxKDep | IxKDepInner | IxKDepOuter
    | IxKGroupOuter | IxKGroupMember
    | IxKCompound | IxKCompoundDynamic -> true
    | _ -> false

let private gateInputs (info: ApplyInfo) : unit =
    if info.ArrayTypes |> List.exists (fun at -> at.IndexTypes |> List.exists raggedFamilyOrCompound) then
        raise (InterpUnsupported "apply over ragged/grouped/compound input (M2.7)")

// ============================================================================
// Binary fold resolution (reduce kernels / choice sections lower to callables)
// ============================================================================

let private resolveBinaryFold (st: InterpState) (kernel: IRExpr) : (Value -> Value -> Value) =
    match resolveKernel kernel with
    | Some rk when rk.Callable.Params.Length = 2 ->
        let callable = rk.Callable
        (fun a b -> callCallable st callable [a; b])
    | _ -> raise (InterpUnsupported "reduce/fold kernel does not resolve to a binary callable")

// ============================================================================
// Array literal → dense BladeArray (via ArrayOps.arrayLitFromValues)
// ============================================================================
// The top-level element exprs evaluate to Values: scalar leaves for a rank-1
// literal; VArray rows for a rank>=2 literal (a nested IRArrayLit element goes
// back through Core.evalExpr → this hook → a VArray). arrayLitFromValues packs
// them (flat store rank-1 / SNested rows rank>=2). Ragged/DepIdx literals gated.

let private evalArrayLit (st: InterpState) (env: Env) (elems: IRExpr list) (arrType: IRArrayType) : Value =
    if arrType.IndexTypes |> List.exists raggedFamilyOrCompound then
        raise (InterpUnsupported "ragged/depidx array literal (M2.7)")
    let vals = elems |> List.map (Core.evalExpr st env)
    VArray (A.arrayLitFromValues arrType vals)

// ============================================================================
// Forward declarations resolved via the recursive block below
// ============================================================================

/// Public Force hook: drive a possibly-deferred Value to a concrete one.
let rec force (st: InterpState) (env: Env) (v: Value) : Value =
    match v with
    | VDeferred (expr, denv) ->
        // Forced-on-read auto-print parity: a payload that IS a module-level
        // binding's Value node (reference hit in DeferredBindingIndex) means
        // this force materializes that binding "under its own name" — the
        // value-space twin of CodeGen's forceDeferredArrayInput IRVar arm.
        // Record the id (Print adds it to the render list) and memoize the
        // result into the ROOT cell so later consumers — and Print — see the
        // materialized value, exactly as the C++ names the materialized array
        // once. Sub-expression VDeferreds miss the index and force as before.
        // NB: resolveComp/forceTreeShaped PEEL through root VDeferreds without
        // calling force — mirroring resolveComputation's inline resolution,
        // which does NOT materialize the source binding either.
        (match st.DeferredBindingIndex.TryGetValue expr with
         | true, id ->
             let fv = forceExpr st denv expr
             st.ForcedDeferred.Add id |> ignore
             (match st.Global with
              | Some g ->
                  (match envTryFind g id with
                   | Some cell -> cell.V <- fv
                   | None -> ())
              | None -> ())
             fv
         | _ -> forceExpr st denv expr)
    | other -> other

// ----------------------------------------------------------------------------
// resolveComputation (CodeGen.fs:7480) in value space: peel IRCompute /
// IRFunctorMap (collecting wrappers innermost-first) / IRVar-through-deferred /
// IRGuard (re-wrap) / IRComposeMeth (extract right's kernel as a wrapper).
// Threads the env so a var bound to a VDeferred with its OWN captured env
// resolves that env's captures correctly.
// ----------------------------------------------------------------------------
and private resolveComp (st: InterpState) (env: Env) (expr: IRExpr) (wrappers: IRExpr list) : IRExpr * IRExpr list * Env =
    match expr with
    | IRVar (id, _) ->
        match envTryFind env id with
        | Some cell ->
            match cell.V with
            | VDeferred (e2, env2) -> resolveComp st env2 e2 wrappers
            | _ -> (expr, wrappers, env)
        | None -> (expr, wrappers, env)
    | IRCompute inner -> resolveComp st env inner wrappers
    | IRFunctorMap (f, inner) -> resolveComp st env inner (f :: wrappers)
    | IRGuard (cond, body) ->
        let (r, w, e) = resolveComp st env body wrappers
        (IRGuard (cond, r), w, e)
    | IRComposeMeth (left, right) ->
        match extractInlinableKernel st env right with
        | Some k -> resolveComp st env left (k :: wrappers)
        | None -> (expr, wrappers, env)
    | _ -> (expr, wrappers, env)

/// Mirror resolveComputation's IRComposeMeth kernel extraction (7509): reach the
/// right operand's inline kernel through deferred vars / functor-map composition.
and private extractInlinableKernel (st: InterpState) (env: Env) (e: IRExpr) : IRExpr option =
    match e with
    | IRVar (id, _) ->
        match envTryFind env id with
        | Some cell -> (match cell.V with VDeferred (e2, env2) -> extractInlinableKernel st env2 e2 | _ -> None)
        | None -> None
    | IRApplyCombinator info ->
        match resolveCallable info.Kernel with Some _ -> Some info.Kernel | None -> None
    | IRFunctorMap (f, inner) ->
        match extractInlinableKernel st env inner with
        | Some k -> Some (IRCompose (k, f))
        | None -> None
    | _ -> None

/// Apply resolveComp-collected functor / compose wrappers (innermost-first) to a
/// concrete value — the value-space twin of applyFunctorWrappers for a base that
/// bottomed out at a CONCRETE array (or scalar), e.g. `f <$> A` where A is a plain
/// array (not an IRApplyCombinator). Mirrors materializeComposeApply's wrapAll fold
/// (IRCompose(k,f) = f∘k) and its INPUT-element-type allocation so the result
/// matches `method_for(A) <@> f |> compute` byte-for-byte.
and private applyWrappersToValue (st: InterpState) (env: Env) (wrappers: IRExpr list) (v: Value) : Value =
    if List.isEmpty wrappers then v else
    let rec wrapperFn (w: IRExpr) : (Value -> Value) =
        match w with
        | IRCompose (k, f) -> let kf = wrapperFn k in let ff = wrapperFn f in (fun x -> ff (kf x))
        | _ -> resolveUnaryKernel st w
    let wrapAll = wrappers |> List.fold (fun acc w -> let wf = wrapperFn w in (fun x -> wf (acc x))) id
    match v with
    | VArray a ->
        let out = A.allocDense a.ElemType a.IndexTypes a.Extents
        let rank = a.Extents.Length
        let rec walk (level: int) (acc: int64 list) =
            if level = rank then
                let coords = List.rev acc
                A.writeCell out coords (wrapAll (A.readCell a coords))
            else
                for i in 0L .. a.Extents.[level] - 1L do walk (level + 1) (i :: acc)
        walk 0 []
        VArray out
    | scalar -> wrapAll scalar

/// Force an IRExpr (the deferred payload) to a concrete Value. Mirrors
/// genComputeBinding's resolveComputation + dispatch.
and private forceExpr (st: InterpState) (env: Env) (expr: IRExpr) : Value =
    let (resolved, wrappers, renv) = resolveComp st env expr []
    match resolved with
    | IRApplyCombinator info -> materializeApply st renv info wrappers
    | IRParallel _ | IRFusion _ -> forceParallelTree st renv resolved wrappers
    | IRChoice (left, right) -> forceChoice st renv left right wrappers
    | IRFallback (a, b) -> forceFallback st renv a b wrappers
    | IRGuard (cond, body) -> forceGuard st renv cond body wrappers
    | IRSequence elems -> forceSequence st renv elems wrappers
    | IRBind (comp, cont) -> forceBind st renv comp cont
    | IRComposeMeth (left, right) -> forceComposeMeth st renv left right
    | IRComposeApply cinfo -> materializeComposeApply st renv cinfo wrappers
    | IRComposeObj _ -> raise (InterpUnsupported "IRComposeObj force")
    // A projection of a deferred tuple (`(c1,c2) = <combinator producing a tuple>`,
    // §4 tuple-of-deferred): force the inner to a VTuple, project element i, then
    // force the projected element (itself possibly a deferred computation).
    | IRTupleProj (inner, i, isFlat) ->
        let tv = forceExpr st renv inner
        applyWrappersToValue st renv wrappers (force st renv (projectValue tv i isFlat))
    | IRVar (id, _) ->
        // A base that bottomed out at a CONCRETE array/scalar (resolveComp already
        // followed any VDeferred alias); apply the trailing functor wrappers here —
        // `f <$> A` over a plain array A (previously the wrappers were dropped).
        match envTryFind renv id with
        | Some cell -> applyWrappersToValue st renv wrappers (force st renv cell.V)
        | None -> raise (InterpUnsupported "force of unbound var")
    | other -> applyWrappersToValue st renv wrappers (Core.evalExpr st renv other)

// ----------------------------------------------------------------------------
// Parallel / fusion: collect leaves (flatten <&>/<&!>, resolve deferred vars),
// materialize each independently (pillar (c): each leaf its own nest / order),
// and assemble a FLAT tuple in left-to-right leaf order (matching genParallelTree
// and genFusionTree's make_tuple convention consumed by tuple destructuring).
// ----------------------------------------------------------------------------
and private forceParallelTree (st: InterpState) (env: Env) (expr: IRExpr) (wrappers: IRExpr list) : Value =
    if not (List.isEmpty wrappers) then
        // CodeGen's IRParallel/IRFusion arms drop functor wrappers; rather than
        // silently reproduce a possibly-latent drop, gate (gate SKIPs).
        raise (InterpUnsupported "functor-map wrapper over a parallel/fusion tree")
    forceTreeShaped st env expr

/// Force a parallel/fusion tree to a STRUCTURED tuple mirroring the tree shape
/// — VTuple [| left; right |] per IRParallel/IRFusion node — i.e. the value-
/// space image of the expression's TYPE: `a <&> b <&> c` is ((α,β),γ). CodeGen
/// emits a FLAT make_tuple plus a TupleChildren map that genTupleProjBinding
/// consults (via tupleLeafRanges over the STRUCTURED type) to serve structural
/// projections; the interpreter's projections are SHAPE-driven
/// (Core.projectStruct / projectFlat), so the value shape itself must carry the
/// structure. A flat projection flattens through nested VTuples (countLeaves),
/// so BOTH destructure styles resolve identically to the compiled binary
/// (tuple-views structural 3-way pinned this: a flat 3-tuple made structural
/// proj 0 return leaf a instead of the (a,b) pair — BL8003 downstream).
and private forceTreeShaped (st: InterpState) (env: Env) (e: IRExpr) : Value =
    match e with
    | IRParallel (l, r, _) | IRFusion (l, r) ->
        VTuple [| forceTreeShaped st env l; forceTreeShaped st env r |]
    | IRVar (id, _) ->
        (match envTryFind env id with
         | Some cell ->
             (match cell.V with
              | VDeferred (e2, env2) -> forceTreeShaped st env2 e2
              | v -> v)                      // already-forced (memoized) leaf
         | None -> forceLeaf st env e)
    | leaf -> forceLeaf st env leaf

and private forceLeaf (st: InterpState) (env: Env) (leaf: IRExpr) : Value =
    match leaf with
    | IRApplyCombinator info -> materializeApply st env info []
    | _ -> forceExpr st env leaf

// ----------------------------------------------------------------------------
// Choice `<|>` (genChoiceBinding 8997 + choice force 7788): materialize both
// sides, elementwise `(lhs != 0) ? lhs : rhs`. Scalar sides use the same rule.
// ----------------------------------------------------------------------------
and private forceChoice (st: InterpState) (env: Env) (left: IRExpr) (right: IRExpr) (wrappers: IRExpr list) : Value =
    let wrapSide s = wrappers |> List.fold (fun acc w -> IRFunctorMap (w, acc)) s
    let lv = forceExpr st env (wrapSide left)
    let rv = forceExpr st env (wrapSide right)
    match lv, rv with
    | VArray la, VArray ra -> VArray (choiceArray la ra)
    | _ -> if isNonZero lv then lv else rv

and private choiceArray (la: BladeArray) (ra: BladeArray) : BladeArray =
    let out = A.allocDense la.ElemType la.IndexTypes la.Extents
    let rank = la.Extents.Length
    let rec walk (level: int) (acc: int64 list) =
        if level = rank then
            let coords = List.rev acc
            let lval = A.readCell la coords
            A.writeCell out coords (if isNonZero lval then lval else A.readCell ra coords)
        else
            for i in 0L .. la.Extents.[level] - 1L do walk (level + 1) (i :: acc)
    walk 0 []
    out

// ----------------------------------------------------------------------------
// Fallback `<|:>` (genFallbackBinding always-defers + genFallbackMaterialize
// 9368). ALLOCATION-keyed, unlike value-keyed `<|>`. Two regimes on the LEFT
// operand type: dense-left = fallback_copy (A fully allocated ⇒ `A <|:> B ≡ A`,
// allocated zeros survive); compound-left = SQL sparse overlay, which needs the
// M2.7 compound-index array representation (gated, like compound-halo / apply-
// over-compound). The RIGHT operand is unused in the dense-left regime and is
// NOT forced (arrays are pure; its materialization is output-invisible, and
// forcing it could spuriously raise for an otherwise-fine program).
// ----------------------------------------------------------------------------
and private forceFallback (st: InterpState) (env: Env) (a: IRExpr) (b: IRExpr) (wrappers: IRExpr list) : Value =
    if not (List.isEmpty wrappers) then
        raise (InterpUnsupported "functor-map wrapper over a fallback (<$> over <|:> is steered to error in CodeGen)")
    match forceExpr st env a with
    | VArray la ->
        VArray (A.fallbackDense la)
    | VCompound cvS ->
        // Compound-left `S <|:> D`: the SQL sparse overlay. Here the RIGHT
        // operand IS needed (D fills the absent leading cells), so force it —
        // it resolves to a plain dense array (a nested `S2 <|:> D` inner already
        // forced to dense).
        (match forceExpr st env b with
         | VArray d -> VArray (A.fallbackCompoundLeft cvS d)
         | _ -> raise (InterpUnsupported "compound-left <|:>: right operand did not force to a dense array"))
    | _ ->
        raise (InterpUnsupported "apply over ragged/grouped/compound input (M2.7)")

// ----------------------------------------------------------------------------
// Guard `guard(cond, comp)` (genGuardBinding 8862): fold the predicate into the
// kernel body (λargs → cond ? body : 0) via a synthetic callable, then
// materialize — an allocated array filled with zeros where the guard is false.
// ----------------------------------------------------------------------------
and private forceGuard (st: InterpState) (env: Env) (cond: IRExpr) (body: IRExpr) (wrappers: IRExpr list) : Value =
    let (resolved, innerWrappers, renv) = resolveComp st env body []
    let allWrappers = wrappers @ innerWrappers
    match resolved with
    | IRApplyCombinator info ->
        let zeroForReturnType (retTy: IRType) =
            match retTy with
            | IRTScalar ETBool -> IRLit (IRLitBool false)
            | IRTScalar ETInt64 | IRTScalar ETInt32 -> IRLit (IRLitInt 0L)
            | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> IRLit (IRLitInt 0L)
            | _ -> IRLit (IRLitFloat 0.0)
        let buildGuarded (c: IRCallable) : IRExpr =
            let synthetic = { c with Id = st.Builder.FreshId(); Body = IRIf (cond, c.Body, zeroForReturnType c.RetType) }
            registerSyntheticCallable synthetic
        let wrappedKernel = mapKernelInner buildGuarded info.Kernel
        materializeApply st renv { info with Kernel = wrappedKernel } allWrappers
    | IRParallel _ | IRFusion _ ->
        raise (InterpUnsupported "guard over a parallel/fusion computation")
    | _ ->
        // guard over a CONCRETE array / scalar (or choice/sequence) body: the
        // predicate is a scalar here (it cannot reference per-cell values without a
        // kernel), so evaluate it once — true ⇒ the (wrapper-applied) materialized
        // body, false ⇒ a zero array/scalar of the same shape. Mirrors CodeGen's
        // non-apply guard materialization.
        let bodyVal = applyWrappersToValue st renv allWrappers (forceExpr st renv resolved)
        if isNonZero (Core.evalExpr st env cond) then bodyVal
        else
            match bodyVal with
            | VArray a -> VArray (A.allocDense a.ElemType a.IndexTypes a.Extents)
            | VFloat _ -> VFloat 0.0
            | VFloat32 _ -> VFloat32 0.0f
            | VInt _ -> VInt 0L
            | VInt32 _ -> VInt32 0
            | VComplex _ -> VComplex (0.0, 0.0)
            | VBool _ -> VBool false
            | other -> other

// ----------------------------------------------------------------------------
// Sequence (genSequenceBinding 7928): n children of same shape stacked into a
// rank-added array [N, child_extents...]; `out[i] = child_i`.
// ----------------------------------------------------------------------------
and private forceSequence (st: InterpState) (env: Env) (elems: IRExpr list) (wrappers: IRExpr list) : Value =
    let wrap s = wrappers |> List.fold (fun acc w -> IRFunctorMap (w, acc)) s
    let childVals = elems |> List.map (fun e -> forceExpr st env (wrap e))
    match childVals with
    | (VArray first) :: _ when childVals |> List.forall (function VArray _ -> true | _ -> false) ->
        // Stack child rows into a rank-added array [N, child_extents...]. Built
        // as an SNested record directly (mkDenseArray reshapes a FLAT store; the
        // rows are already nested stores). Printing keys off the binding type, so
        // the extra outer Idx<N> need not be reflected in IndexTypes here.
        let rows = childVals |> List.map (function VArray a -> a.Data | _ -> failwith "unreachable") |> Array.ofList
        let outExtents = Array.append [| int64 elems.Length |] first.Extents
        VArray { ElemType = first.ElemType; IndexTypes = first.IndexTypes; Extents = outExtents; Data = SNested rows }
    | _ ->
        // Scalar children: rank-1 array of the child values (via storeOfValues).
        let et = match childVals with (VFloat _) :: _ -> ETFloat64 | (VInt _) :: _ -> ETInt64 | _ -> ETFloat64
        VArray (A.mkDenseArray (IRTScalar et) [] [| int64 elems.Length |] (A.storeOfValues (IRTScalar et) (Array.ofList childVals)))

// ----------------------------------------------------------------------------
// Bind (genBindChainBinding / IRBind force 7724): materialize comp as s1, bind
// the continuation's parameter to s1's value, force the continuation body.
// ----------------------------------------------------------------------------
and private forceBind (st: InterpState) (env: Env) (comp: IRExpr) (cont: IRExpr) : Value =
    let s1 = forceExpr st env comp
    match resolveCallable (resolveContRef st env cont) with
    | Some lInfo when lInfo.Params.Length >= 1 ->
        let benv = envChild env
        envBind benv lInfo.Params.[0].VarId s1 |> ignore
        for c in lInfo.Captures do
            match envTryFind env c.Id with Some cell -> envBindRef benv c.Id cell | None -> ()
        // The continuation body is itself a computation (IRCompute-wrapped).
        forceExpr st benv lInfo.Body
    | _ -> raise (InterpUnsupported "bind continuation does not resolve to a callable")

and private resolveContRef (st: InterpState) (env: Env) (e: IRExpr) : IRExpr =
    match e with
    | IRVar (id, _) ->
        match envTryFind env id with
        | Some cell -> (match cell.V with VDeferred (e2, env2) -> resolveContRef st env2 e2 | _ -> e)
        | None -> e
    | _ -> e

// ----------------------------------------------------------------------------
// Method composition @>> (IRComposeMeth force 7662): materialize left as s1,
// then apply right's kernel elementwise over s1.
// ----------------------------------------------------------------------------
and private forceComposeMeth (st: InterpState) (env: Env) (left: IRExpr) (right: IRExpr) : Value =
    let s1 = forceExpr st env left
    match s1 with
    | VArray a ->
        match extractInlinableKernel st env right with
        | Some kernelRef ->
            match resolveKernel kernelRef with
            | Some rk when rk.Callable.Params.Length = 1 ->
                let callable = rk.Callable
                let out = A.allocDense a.ElemType a.IndexTypes a.Extents
                let rank = a.Extents.Length
                let rec walk (level: int) (acc: int64 list) =
                    if level = rank then
                        let coords = List.rev acc
                        A.writeCell out coords (callCallable st callable [ A.readCell a coords ])
                    else
                        for i in 0L .. a.Extents.[level] - 1L do walk (level + 1) (i :: acc)
                walk 0 []
                VArray out
            | _ -> raise (InterpUnsupported "compose-meth right kernel arity != 1")
        | None -> raise (InterpUnsupported "compose-meth right kernel not resolvable")
    | _ -> raise (InterpUnsupported "compose-meth left side is not an array")

// ----------------------------------------------------------------------------
// Compose-object apply `(object_for(k1) >>@ object_for(k2)) <@> A`
// (IRComposeApply; genComposeApply, CodeGen.fs:6653): the SLOT-INVERTED apply.
// Two SEPARATE elementwise stages over the (single, rank-1 in corpus) input:
//   s1[i] = k1(A[i]);   out[i] = k2(s1[i])   ⇒   out[i] = k2(k1(A[i])).
// CodeGen allocates BOTH s1 and the output with the INPUT array's element type
// (not the kernels' return types — genComposeApply's `elemType` comes from the
// input array), so writeCell's coercion reproduces the compiled store exactly.
// Composition resolves through deferred vars to IRComposeObj; each object's
// kernel is IRObjectFor.Kernel (or a bare kernel expr). Returns a plain VArray
// so forceTreeShaped wraps a parallel/fusion leaf correctly (046/047).
//
// `wrappers` are trailing functor-map / @>>-extracted kernels resolveComp
// collected around this node (innermost-first): `p @>> q` reaches here with
// q's kernel as ONE wrapper (loops/048), so they are applied as a final
// elementwise stage `out[i] = wrapAll(k2(k1(A[i])))`, folding left-to-right
// exactly like applyFunctorWrappers.
// ----------------------------------------------------------------------------
and private materializeComposeApply (st: InterpState) (env: Env) (cinfo: ComposeApplyInfo) (wrappers: IRExpr list) : Value =
    let rec resolveDef (e: IRExpr) (en: Env) : IRExpr * Env =
        match e with
        | IRVar (id, _) ->
            match envTryFind en id with
            | Some cell ->
                match cell.V with
                | VDeferred (e2, en2) -> resolveDef e2 en2
                // A let-bound object (`let o = object_for(f)`) is a VLoopObj, not
                // a VDeferred; unwrap to its IRObjectFor provenance so kernelOf can
                // reach `.Kernel`. Mirrors the codegen ObjectLoopBindings chase.
                | VLoopObj lo -> resolveDef lo.Provenance lo.Captured
                | _ -> (e, en)
            | None -> (e, en)
        | _ -> (e, en)
    let (comp, cenv) = resolveDef cinfo.Composition env
    match comp with
    | IRComposeObj (o1, o2) ->
        let kernelOf (o: IRExpr) : IRExpr =
            let (ro, _) = resolveDef o cenv
            match ro with IRObjectFor lo -> lo.Kernel | _ -> ro
        let call1 = resolveUnaryKernel st (kernelOf o1)
        let call2 = resolveUnaryKernel st (kernelOf o2)
        // A wrapper is a unary transform; an extracted IRCompose(k,f) means
        // f∘k (applyValue's compose convention). Fold all wrappers innermost-
        // first onto stage 2's result.
        let rec wrapperFn (w: IRExpr) : (Value -> Value) =
            match w with
            | IRCompose (k, f) -> let kf = wrapperFn k in let ff = wrapperFn f in (fun v -> ff (kf v))
            | _ -> resolveUnaryKernel st w
        let wrapAll = wrappers |> List.fold (fun acc w -> let wf = wrapperFn w in (fun v -> wf (acc v))) id
        let call2Wrapped v = wrapAll (call2 v)
        match cinfo.InputArrays with
        | [ arrExpr ] ->
            let a = forceInputArray st env arrExpr
            let rank = a.Extents.Length
            let rec walk (src: BladeArray) (dst: BladeArray) (call: Value -> Value) (level: int) (acc: int64 list) =
                if level = rank then
                    let coords = List.rev acc
                    A.writeCell dst coords (call (A.readCell src coords))
                else
                    for i in 0L .. src.Extents.[level] - 1L do walk src dst call (level + 1) (i :: acc)
            // Stage 1 then stage 2, each its own pass (matching CodeGen's two
            // loops); both stores carry the INPUT element type.
            let s1 = A.allocDense a.ElemType a.IndexTypes a.Extents
            walk a s1 call1 0 []
            let out = A.allocDense a.ElemType a.IndexTypes a.Extents
            walk s1 out call2Wrapped 0 []
            VArray out
        | _ -> raise (InterpUnsupported "compose-apply with multiple input arrays (M2.3)")
    | _ -> raise (InterpUnsupported "IRComposeApply: composition did not resolve to IRComposeObj")

/// Force an eager-op / compose-apply INPUT expr to a concrete BladeArray,
/// memoizing a deferred IRVar cell (resolveArraySource's double-consumer rule,
/// §0.3) so a second consumer of the same binding sees the already-materialized
/// array — the value-space twin of forceDeferredArrayInput. A bare virtual
/// source fed directly to an eager op is not in the corpus (CodeGen's eager ops
/// index a materialized `.extents[0]`), so it gates.
and private forceInputArray (st: InterpState) (env: Env) (arrExpr: IRExpr) : BladeArray =
    match resolveArraySource st env arrExpr with
    | SReal a -> a
    | SVirtual -> raise (InterpUnsupported "eager op over a bare virtual source (materialize first)")

/// Resolve a sort key / mask predicate / compose-apply stage expr to a unary
/// Value->Value closure via resolveKernel (peels Reynolds, resolves through the
/// callables + synthetic table). Invoked with empty captures like
/// resolveBinaryFold — module-level kernels reach their captures via st.Global.
and private resolveUnaryKernel (st: InterpState) (kernel: IRExpr) : (Value -> Value) =
    match resolveKernel kernel with
    | Some rk when rk.Callable.Params.Length = 1 -> (fun v -> callCallable st rk.Callable [ v ])
    | _ -> raise (InterpUnsupported "sort/mask kernel does not resolve to a unary callable")

/// Inline object_for application: `A [op] B` (bracketed OUTER product) and its
/// elementwise / single-array-broadcast siblings. Mirrors genObjectForApplication
/// (CodeGen.fs:6537): force each input array, resolve the (1- or 2-param) kernel
/// callable, allocate a fresh DENSE output of the carried type, and fill by
/// invoking the kernel per output cell. `objInfo.InputRanks` selects the shape
/// ([1;1] = outer m×p, [0;0] = elementwise m, [0] = single-array map). (The
/// two-array ELEMENTWISE binop `A + B` is re-synthesized by the checker as
/// `compute(zip <@> λ)` and never reaches here; only the OUTER form and any
/// residual single/elementwise object_for applications do.) Note: the kernel's
/// return type IS the output element type (a comparison `[<]` consumes numbers
/// and produces bool) — carried by the output array type, so a Bool store is
/// allocated and writeCell coerces the VBool cell.
and private materializeObjectForApp
        (st: InterpState) (env: Env) (outType: IRType) (objInfo: ObjectForInfo) (arrays: IRExpr list) : Value =
    let arrs = arrays |> List.map (forceInputArray st env)
    let kernel =
        match resolveKernel objInfo.Kernel with
        | Some rk -> rk.Callable
        | None -> raise (InterpUnsupported "object_for application: kernel does not resolve to a callable")
    let call (vs: Value list) : Value = callCallable st kernel vs
    // Output element type + dense index slots. genObjectForApplication derives the
    // element type from the KERNEL's return type (a comparison `[<]` / logical
    // `[&&]` consumes numbers but PRODUCES bool) and IGNORES the IRApp's carried
    // result type — which, for the bool-returning OUTER forms, the checker even
    // collapses to a bare scalar. (The compiled binary then prints such a binding's
    // raw Array data POINTER via `cout << arr`, masked by the differential
    // normalizer to 0xPTR; the interp still materializes the true bool array so a
    // pointer-aware Print emitter can render the matching token.) Prefer the carried
    // array type when present (arithmetic — byte-verified); else rebuild dense slots
    // from the inputs.
    let outElem, srcIdxTys =
        match outType with
        | ArrayElem outArr -> outArr.ElemType, outArr.IndexTypes
        | _ ->
            let firstIdx (arr: BladeArray) = match arr.IndexTypes with ix :: _ -> [ix] | [] -> []
            kernel.RetType, (arrs |> List.collect firstIdx)
    match objInfo.InputRanks, arrs with
    | [1; 1], [ a; b ] ->
        // Outer product: out[i][j] = kernel(A[i], B[j]); dense m×p.
        let m = a.Extents.[0]
        let p = b.Extents.[0]
        let out = A.allocDense outElem srcIdxTys [| m; p |]
        for i in 0L .. m - 1L do
            for j in 0L .. p - 1L do
                A.writeCell out [ i; j ] (call [ A.peelDim a i; A.peelDim b j ])
        VArray out
    | [0; 0], [ a; b ] ->
        // Elementwise: out[i] = kernel(A[i], B[i]); dense m.
        let m = a.Extents.[0]
        let out = A.allocDense outElem srcIdxTys [| m |]
        for i in 0L .. m - 1L do
            A.writeCell out [ i ] (call [ A.peelDim a i; A.peelDim b i ])
        VArray out
    | [0], [ a ] ->
        // Single-array broadcast map: out[i] = kernel(A[i]); dense m.
        let m = a.Extents.[0]
        let out = A.allocDense outElem srcIdxTys [| m |]
        for i in 0L .. m - 1L do
            A.writeCell out [ i ] (call [ A.peelDim a i ])
        VArray out
    | _ -> raise (InterpUnsupported "object_for application: unsupported input-rank configuration")

// ============================================================================
// materializeApply — the standard dense/co-iteration path (genApplyCombinator).
// ============================================================================

and private materializeApply (st: InterpState) (env: Env) (info0: ApplyInfo) (wrappers: IRExpr list) : Value =
    match tryCompoundHaloMap info0 with
    | Some (maskExpr, leadRank, tag) -> materializeCompoundHaloMap st env info0 wrappers maskExpr leadRank tag
    | None ->
    match tryCompoundRangeMap info0 with
    | Some (maskExpr, leadRank) -> materializeCompoundRangeMap st env info0 wrappers maskExpr leadRank
    | None ->
    if tryGroupedMap info0 then materializeGroupedMap st env info0 wrappers
    else
    gateInputs info0
    let info = applyFunctorWrappers st info0 wrappers
    let arrayNames = info.Arrays |> List.mapi (fun i _ -> sprintf "a%d" i)
    let cg = buildLoopNestCodeGen info arrayNames "out" st.Builder
    // M3: symmetric/antisymmetric/Hermitian OUTPUT storage (compact) and Reynolds
    // KERNELS (permutation sum) are now interpreted — see the ArrayElem arm's
    // compact allocation and interpretNest's Reynolds path. FUSED-JOINT output
    // levels (Arc-1 fused S-block: a single loop level spanning d plain-dense
    // source dims of its array — joint symmetry over the compound axis) are
    // materialized by interpretNest's fused-peel arm (per-dim coordinate decode
    // from the compound index, mirroring genElementBinding CodeGen.fs:3087).
    // Resolve input array VALUES by position.
    let inputs = Dictionary<int, ArraySource>()
    info.Arrays |> List.iteri (fun i arr -> inputs.[i] <- resolveArraySource st env arr)
    let realAt (pos: int) =
        match inputs.TryGetValue pos with
        | true, SReal a -> a
        | _ -> raise (InterpUnsupported (sprintf "expected a materialized array at position %d" pos))
    // Loop-level extent (mirror genLoopBoundExpr's EXTENT, pre-subtraction). A
    // fused-joint level's extent is the PRODUCT of the array's first d plain-dense
    // extents (the compound-axis cardinality), not a single dim.
    let levelExtent (b: LoopIndexBinding) : int64 =
        match b.FusedRank with
        | Some d ->
            let pos = match b.Elements with e :: _ -> e.ArrayPosition | [] -> 0
            let a = realAt pos
            [ 0 .. d - 1 ] |> List.fold (fun acc j -> acc * a.Extents.[j]) 1L
        | None ->
        match b.Extent with
        | IRLit (IRLitInt n) -> n
        | IRCompoundMask _ -> raise (InterpUnsupported "compound-index loop level (M2.7)")
        | _ ->
            let pos = match b.Elements with e :: _ -> e.ArrayPosition | [] -> 0
            match inputs.TryGetValue pos with
            | true, SReal a -> a.Extents.[b.ExtentDimRef]
            | _ -> toI64 (Core.evalExpr st env b.Extent)
    match cg.OutputType with
    | IRTScalar et ->
        let acc = { V = zeroOfElem et }
        interpretNest st env cg inputs realAt levelExtent (OutFold (acc, (fun a b -> N.evalBinOp IRAdd a b)))
        acc.V
    | ArrayElem arr ->
        let extents = cg.Bindings |> List.map levelExtent |> Array.ofList
        st.Cells <- st.Cells + (extents |> Array.fold (*) 1L)
        // Compact storage iff the OUTPUT index type carries a real symmetry
        // group (adjacent-equal storage group). buildSymmVecWithStrict groups
        // sym/herm/antisym together, so hasRealSymmetry on ITS vec detects ALL
        // three compact classes; the strict vec drives the antisym diagonal drop.
        let (osym, ostrict) = buildSymmVecWithStrict cg.OutputType
        let outArr =
            if hasRealSymmetry osym then
                A.allocCompact arr.ElemType arr.IndexTypes extents (Array.ofList osym) (Array.ofList ostrict)
            else
                A.allocDense arr.ElemType arr.IndexTypes extents
        interpretNest st env cg inputs realAt levelExtent (OutArray outArr)
        VArray outArr
    | other -> raise (InterpUnsupported (sprintf "apply output type %s" (nodeTypeName other)))

and private nodeTypeName (ty: IRType) : string =
    let case, _ = Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(ty, typeof<IRType>)
    case.Name

/// Detect a `method_for(range<CompoundIdx<m>>) <@> kernel` map: the sole input
/// is a range whose sole index type is a compound mask. Returns (maskExpr,
/// leadRank). This is the ONE supported compound-range form (a compound slot
/// cannot mix with other index types — CodeGen.fs:5971).
and private tryCompoundRangeMap (info: ApplyInfo) : (IRExpr * int) option =
    match info.Arrays with
    | [ IRRange (idxTys, _) ] ->
        (match idxTys with
         // A halo<CompoundIdx<m>, [..]> slot ALSO has IxKCompound + IRCompoundMask
         // but carries a "__halowin|" tag and reads via IRHaloUnhash (window
         // pointer into the table) — a different peel. Exclude it (it stays a
         // clean skip rather than being mis-driven as a plain coordinate map).
         | [ ix ] when ix.IxKind = IxKCompound
                       && (match ix.Tag with Some t when t.StartsWith "__halowin|" -> false | _ -> true) ->
             (match ix.Extent with IRCompoundMask m -> Some (m, ix.Rank) | _ -> None)
         | _ -> None)
    | _ -> None

/// Materialize a range<CompoundIdx<m>> map to a Compound VALUE: iterate the
/// present cells (in lex rank order), binding each kernel param to its tuple
/// COORDINATE via the index's O(1) unhash, and store the kernel result into the
/// compact buffer at that rank (genElementBindingNew's compound-range arm +
/// compoundOutputSubscript, CodeGen.fs:3054-3064 / 3696-3704). The output shares
/// the range's mask/index (same present tuples), trailing_stride 1.
and private materializeCompoundRangeMap
        (st: InterpState) (env: Env) (info0: ApplyInfo) (wrappers: IRExpr list)
        (maskExpr: IRExpr) (leadRank: int) : Value =
    if not (List.isEmpty wrappers) then
        raise (InterpUnsupported "functor-map wrapper over a range<CompoundIdx> map")
    let maskArr =
        match force st env (Core.evalExpr st env maskExpr) with
        | VArray a -> a
        | _ -> raise (InterpUnsupported "range<CompoundIdx>: mask is not an array")
    let leadExtents = maskArr.Extents
    let maskBits = A.maskToBits maskArr
    let (table, rankOf, card) = A.buildCompoundIndex leadExtents maskBits
    // Kernel + per-coordinate param plan from the SAME loop-nest builder CodeGen
    // uses (so the kernel body + param identities cannot drift).
    let arrayNames = info0.Arrays |> List.mapi (fun i _ -> sprintf "a%d" i)
    let cg = buildLoopNestCodeGen info0 arrayNames "out" st.Builder
    let (elemTy, outIdxTys) =
        match cg.OutputType with
        | ArrayElem arr -> (arr.ElemType, arr.IndexTypes)
        | other -> raise (InterpUnsupported (sprintf "range<CompoundIdx> map output type %s" (nodeTypeName other)))
    // Kernel env: captures + reusable param cells (as interpretNest builds them).
    let kenv = envChild env
    for c in cg.Captures do
        match envTryFind env c.Id with Some cell -> envBindRef kenv c.Id cell | None -> ()
    let paramCells = Dictionary<IRId, ValueRef>()
    for p in cg.KernelParams do
        let cell = { V = VUnit }
        paramCells.[p.VarId] <- cell
        envBindRef kenv p.VarId cell
    // (paramVarId, coordinate index rc): the compound level's element bindings,
    // each `int64 <param> = <cidx>->unhash(r)[rc]`.
    let paramCoord =
        cg.Bindings |> List.collect (fun b -> b.Elements |> List.map (fun e -> (e.ParamVarId, e.RankComponent)))
    st.Cells <- st.Cells + card
    let results = Array.create (int card) VUnit
    for r in 0 .. int card - 1 do
        let tuple = table.[r]
        for (pv, rc) in paramCoord do
            match paramCells.TryGetValue pv with
            | true, cell -> cell.V <- VInt tuple.[rc]
            | _ -> ()
        results.[r] <- force st kenv (Core.evalExpr st kenv cg.KernelExpr)
    VCompound
        { ElemType = elemTy
          IndexTypes = outIdxTys
          LeadRank = leadRank
          LeadExtents = leadExtents
          Mask = maskBits
          Table = table
          RankOf = rankOf
          Cardinality = card
          TrailingStride = 1L
          Data = A.storeOfValues elemTy results }

/// Detect a `method_for(halo<CompoundIdx<m>, [..]>) <@> lambda(w) -> ...` map:
/// the sole input is a range whose sole index is a compound mask carrying a
/// "__halowin|" tag. Returns (maskExpr, leadRank, haloTag).
and private tryCompoundHaloMap (info: ApplyInfo) : (IRExpr * int * string) option =
    match info.Arrays with
    | [ IRRange (idxTys, _) ] ->
        (match idxTys with
         | [ ix ] when ix.IxKind = IxKCompound ->
             (match ix.Tag, ix.Extent with
              | Some tag, IRCompoundMask m when tag.StartsWith "__halowin|" -> Some (m, ix.Rank, tag)
              | _ -> None)
         | _ -> None)
    | _ -> None

/// Materialize a compound-halo map: the ordinals walk the PRESENT cells, so the
/// window `w` at ordinal c exposes `w(o)` = the COORDINATE of the (c+o)-th present
/// cell (IRHaloUnhash over rank_to_tuple). The loop shrinks by the halo's interior
/// loss (the runtime-cardinality bound minus the window span, IR.fs:3109-3120);
/// each `w` is bound to (coordinate column, center ordinal) so IRHaloUnhash reads
/// col[center+o]. Output is a dense rank-1 array over the shrunk present-cell axis.
and private materializeCompoundHaloMap
        (st: InterpState) (env: Env) (info: ApplyInfo) (wrappers: IRExpr list)
        (maskExpr: IRExpr) (leadRank: int) (tag: string) : Value =
    if not (List.isEmpty wrappers) then
        raise (InterpUnsupported "functor-map wrapper over a compound-halo map")
    if leadRank <> 1 then
        raise (InterpUnsupported "compound-halo over a rank>1 mask (rank-1 only)")
    let maskArr =
        match force st env (Core.evalExpr st env maskExpr) with
        | VArray a -> a
        | _ -> raise (InterpUnsupported "compound-halo: mask is not an array")
    let (table, _, card) = A.buildCompoundIndex maskArr.Extents (A.maskToBits maskArr)
    let start = Blade.Types.haloStartOffsetOfTag tag |> Option.defaultValue 0L
    let shrink = Blade.Types.haloShrinkOfTag tag |> Option.defaultValue 0L
    let outLen = card - shrink
    // Coordinate column: coordinate 0 of each present tuple, in rank order.
    let coordCol =
        VArray
            { ElemType = IRTScalar ETInt64
              IndexTypes = []
              Extents = [| card |]
              Data = SInt (Array.init (int card) (fun r -> table.[r].[0])) }
    let arrayNames = info.Arrays |> List.mapi (fun i _ -> sprintf "a%d" i)
    let cg = buildLoopNestCodeGen info arrayNames "out" st.Builder
    let elemTy =
        match cg.OutputType with
        | ArrayElem arr -> arr.ElemType
        | IRTScalar et -> IRTScalar et
        | other -> raise (InterpUnsupported (sprintf "compound-halo map output type %s" (nodeTypeName other)))
    let param =
        match cg.KernelParams with
        | [ p ] -> p
        | _ -> raise (InterpUnsupported "compound-halo map: kernel is not single-parameter")
    let kenv = envChild env
    for c in cg.Captures do
        match envTryFind env c.Id with Some cell -> envBindRef kenv c.Id cell | None -> ()
    let cell = { V = VUnit }
    envBindRef kenv param.VarId cell
    st.Cells <- st.Cells + (if outLen > 0L then outLen else 0L)
    let n = if outLen > 0L then int outLen else 0
    let results =
        Array.init n (fun c ->
            cell.V <- VTuple [| coordCol; VInt (int64 c + start) |]
            force st kenv (Core.evalExpr st kenv cg.KernelExpr))
    VArray (A.mkDenseArray elemTy [] [| int64 n |] (A.storeOfValues elemTy results))

/// Detect a `method_for(grouped) <@> lambda(g) -> ...` map: the sole input is a
/// group_by result (its first index is IxKGroupOuter). CodeGen supports exactly
/// the single-array, single-row-param form here (CodeGen.fs:5933-5936); the
/// kernel receives each group's ragged ROW as `g`.
and private tryGroupedMap (info: ApplyInfo) : bool =
    match info.ArrayTypes with
    | [ at ] -> (match at.IndexTypes with h :: _ -> h.IxKind = IxKGroupOuter | [] -> false)
    | _ -> false

/// Materialize a grouped map: iterate the outer group axis, bind the kernel's
/// single row-param to each peeled group row (a ragged-row sub-array), and store
/// the per-group scalar result into a dense rank-1 output. `g(0)`, `reduce(g,+)`,
/// `extents(g)` in the body all operate on the peeled row (a plain rank-1 array).
and private materializeGroupedMap (st: InterpState) (env: Env) (info: ApplyInfo) (wrappers: IRExpr list) : Value =
    if not (List.isEmpty wrappers) then
        raise (InterpUnsupported "functor-map wrapper over a grouped map")
    let grouped =
        match resolveArraySource st env info.Arrays.[0] with
        | SReal a -> a
        | _ -> raise (InterpUnsupported "grouped map: input is not a materialized array")
    let arrayNames = info.Arrays |> List.mapi (fun i _ -> sprintf "a%d" i)
    let cg = buildLoopNestCodeGen info arrayNames "out" st.Builder
    let elemTy =
        match cg.OutputType with
        | ArrayElem arr -> arr.ElemType
        | IRTScalar et -> IRTScalar et
        | other -> raise (InterpUnsupported (sprintf "grouped map output type %s" (nodeTypeName other)))
    let param =
        match cg.KernelParams with
        | [ p ] -> p
        | _ -> raise (InterpUnsupported "grouped map: kernel is not single-parameter")
    let kenv = envChild env
    for c in cg.Captures do
        match envTryFind env c.Id with Some cell -> envBindRef kenv c.Id cell | None -> ()
    let cell = { V = VUnit }
    envBindRef kenv param.VarId cell
    let ngroups = int grouped.Extents.[0]
    st.Cells <- st.Cells + int64 ngroups
    let results =
        Array.init ngroups (fun g ->
            cell.V <- A.peelDim grouped (int64 g)
            force st kenv (Core.evalExpr st kenv cg.KernelExpr))
    VArray (A.mkDenseArray elemTy [] [| int64 ngroups |] (A.storeOfValues elemTy results))

/// Build a Compound VALUE for a `let B = compound(dense, mask)` binding (recorded
/// in IRModule.CompoundInits). Run.fs intercepts the binding at its position in
/// the sequence — like RandomInits — and calls this with the lowered dense/mask
/// exprs; both are forced to concrete arrays here, then bundled via the pure
/// ArrayOps builder. Mirrors CodeGen.genCompoundInitBinding (CodeGen.fs:8581).
and materializeCompoundBinding
        (st: InterpState) (env: Env) (binding: IRBinding) (denseExpr: IRExpr) (maskExpr: IRExpr) : Value =
    let forceArr (e: IRExpr) (what: string) : BladeArray =
        match force st env (Core.evalExpr st env e) with
        | VArray a -> a
        | _ -> raise (InterpUnsupported (sprintf "compound() %s operand is not an array" what))
    match binding.Type with
    | ArrayElem arrTy ->
        let dense = forceArr denseExpr "dense"
        let mask = forceArr maskExpr "mask"
        let cv = A.buildCompound arrTy dense mask
        st.Cells <- st.Cells + cv.Cardinality * cv.TrailingStride
        VCompound cv
    | _ -> raise (InterpUnsupported "compound() binding is not an array type")

/// Resolve one `info.Arrays.[pos]` to an ArraySource. Virtual sources
/// (range/reverse/blocked) carry no store. A deferred `IRVar` input is forced
/// AND its cell overwritten with the materialized array (forceDeferredArrayInput's
/// double-consumer memoization, §0.3).
and private resolveArraySource (st: InterpState) (env: Env) (arr: IRExpr) : ArraySource =
    match arr with
    | IRRange _ | IRVirtualReverse _ | IRBlocked _ -> SVirtual
    | IRVar (id, _) ->
        match envTryFind env id with
        | Some cell ->
            match cell.V with
            | VArray a -> SReal a
            // A compound operand of an eager op (sort/reduce/set-op over a
            // `compound(...)`) walks its compact present-cell buffer as a plain
            // dense rank-1 array (§4.1 compound-operand path).
            | VCompound cv -> SReal (A.compoundToDense cv)
            | VDeferred _ as d ->
                let fv = force st env d
                cell.V <- fv   // memoize once (a second consumer sees the array)
                (match fv with
                 | VArray a -> SReal a
                 | VCompound cv -> SReal (A.compoundToDense cv)
                 | _ -> raise (InterpUnsupported "deferred array input did not force to an array"))
            | other ->
                (match other with
                 | VArray a -> SReal a
                 | VCompound cv -> SReal (A.compoundToDense cv)
                 | _ -> raise (InterpUnsupported "array input var is not an array"))
        | None ->
            match Core.evalExpr st env arr with
            | VArray a -> SReal a
            | VCompound cv -> SReal (A.compoundToDense cv)
            | _ -> raise (InterpUnsupported "array input var unbound")
    | _ ->
        match force st env (Core.evalExpr st env arr) with
        | VArray a -> SReal a
        | VCompound cv -> SReal (A.compoundToDense cv)
        | _ -> raise (InterpUnsupported "array input expression is not an array")

// ============================================================================
// interpretNest — the nest interpreter (analog of genLoopNest). Outermost-first
// recursion; bound = Extent − ΣBoundDependencies − StrictOffset; per-level
// element peeling arm-for-arm; innermost kernel via Core.evalExpr.
// ============================================================================

and private interpretNest
        (st: InterpState) (env: Env) (cg: LoopNestCodeGen)
        (inputs: Dictionary<int, ArraySource>) (realAt: int -> BladeArray)
        (levelExtent: LoopIndexBinding -> int64) (out: OutTarget) : unit =
    let bindings = cg.Bindings |> Array.ofList
    let n = bindings.Length
    let idxVals : int64[] = Array.zeroCreate n
    // Kernel env: child of the deferred env (so module bindings + enclosing
    // locals remain reachable), with capture cells + reusable param cells.
    let kenv = envChild env
    for c in cg.Captures do
        match envTryFind env c.Id with Some cell -> envBindRef kenv c.Id cell | None -> ()
    let paramCells = Dictionary<IRId, ValueRef>()
    for p in cg.KernelParams do
        let cell = { V = VUnit }
        paramCells.[p.VarId] <- cell
        envBindRef kenv p.VarId cell

    // Reynolds term plan (precomputed once per nest; iteration-invariant). The
    // kernel is summed over the surviving parameter permutations exactly as
    // CodeGen.genKernelExprWithReynolds renders it (CodeGen.fs:3531). The buildKey
    // REUSES CodeGen.canonicalKey with a synthetic per-index name map: the map is
    // a consistent bijective renaming of CodeGen's peeled C++ names, and
    // canonicalKey grouping depends only on the key EQUALITY relation, so the
    // dedup / coefficient / first-occurrence ordering are IDENTICAL to the
    // compiled binary by construction (captures fall to canonicalKey's own "v%d"
    // fallback — fixed across permutations, matching their fixed peeled names).
    let reynoldsPlan : (ValueRef[] * Blade.ReynoldsCore.ReynoldsTermPlan) option =
        if cg.HasReynolds && cg.KernelParams.Length >= 2 then
            let n = cg.KernelParams.Length
            let paramNames = Array.init n (fun i -> sprintf "__rp%d" i)
            let permNameMap (perm: int list) : Map<int, string> =
                cg.KernelParams
                |> List.mapi (fun i p -> (p.VarId, paramNames.[perm.[i]]))
                |> List.fold (fun acc (vid, nm) -> Map.add vid nm acc) Map.empty
            let plan =
                Blade.ReynoldsCore.reynoldsTermPlan n cg.IsAntisymmetric
                    (fun perm -> Blade.CodeGen.canonicalKey (permNameMap perm) cg.KernelExpr)
            let pcells = cg.KernelParams |> List.map (fun p -> paramCells.[p.VarId]) |> Array.ofList
            Some (pcells, plan)
        else None

    // Evaluate the Reynolds permutation sum for the CURRENT peeled param values,
    // mirroring genKernelExprWithReynolds's formatTerm/sumExpr value semantics:
    //   symmetric  : Σ_i (coeff_i==1 ? v_i : coeff_i * v_i)         [coeffs > 0]
    //   antisym    : first term signed; subsequent negative terms SUBTRACTED
    //                (acc - |c|*v), positive ADDED (acc + c*v); |c|==1 drops the
    //                multiply (unary neg for a lone negative). Empty plan -> 0.0.
    let scaleCoeff (c: int) (v: Value) : Value = N.evalBinOp IRMul (VFloat (float c)) v
    let evalReynolds (pcells: ValueRef[]) (plan: Blade.ReynoldsCore.ReynoldsTermPlan) : Value =
        let origVals = pcells |> Array.map (fun c -> c.V)
        let evalPerm (perm: int list) : Value =
            perm |> List.iteri (fun i src -> pcells.[i].V <- origVals.[src])
            force st kenv (Core.evalExpr st kenv cg.KernelExpr)
        let result =
            match plan.Terms with
            | [] -> VFloat 0.0
            | (coeff0, perm0) :: rest ->
                let v0 = evalPerm perm0
                let mutable acc =
                    if cg.IsAntisymmetric then
                        if abs coeff0 = 1 then (if coeff0 > 0 then v0 else N.evalUnaryOp IRNeg v0)
                        else scaleCoeff coeff0 v0
                    else
                        if coeff0 = 1 then v0 else scaleCoeff coeff0 v0
                for (coeff, perm) in rest do
                    let v = evalPerm perm
                    if cg.IsAntisymmetric && coeff < 0 then
                        let part = if abs coeff = 1 then v else scaleCoeff (abs coeff) v
                        acc <- N.evalBinOp IRSub acc part
                    else
                        let part = if coeff = 1 then v else scaleCoeff coeff v
                        acc <- N.evalBinOp IRAdd acc part
                acc
        // Restore the peeled values so the next output cell starts clean.
        Array.iteri (fun i (c: ValueRef) -> c.V <- origVals.[i]) pcells
        result

    // Peel one element at `level` given the current per-position peeled arrays
    // (immutable Map threaded down the recursion — sibling iterations don't see
    // each other's peels) and the "sliced?" set (positions peeled at an ancestor
    // level). Returns the updated (curArrays, slicedSet). Mirrors
    // genElementBindingNew arm-for-arm in value space.
    let peelElement (b: LoopIndexBinding) (elem: ElementBinding) (extent: int64)
                    (curArrays: Map<int, BladeArray>) (sliced: Set<int>) : Map<int, BladeArray> * Set<int> =
        let i = idxVals.[b.Level]
        match elem.Virtual with
        | VirtualRange (Some off) ->
            let offV = toI64 (Core.evalExpr st kenv off)
            paramCells.[elem.ParamVarId].V <- VInt (i + offV)
            (curArrays, sliced)
        | VirtualRange None ->
            paramCells.[elem.ParamVarId].V <- VInt i
            (curArrays, sliced)
        | VirtualReverse ->
            paramCells.[elem.ParamVarId].V <- VInt (extent - 1L - i)
            (curArrays, sliced)
        | RealArray when b.FusedRank.IsSome ->
            // Arc-1 fused JOINT level (IR.fuseJointSLevels; genElementBinding
            // CodeGen.fs:3087). This level spans the array's whole d-dim plain-
            // dense S-block. The loop var is a left-justified compound coordinate;
            // component 0 shifts it to the ABSOLUTE compound coord p (bound deps +
            // strict offset), then component rc decodes its per-dim coordinate
            //   coord_rc = (p / prod_{j>rc} n_j) % n_rc
            // (row-major lex, matching the storage bijection) and peels ONE dim of
            // the progressively-sliced array. The d components chain through
            // curArrays[pos]; the final (rc = d-1) peel binds the kernel param.
            let d = b.FusedRank.Value
            let rc = elem.RankComponent
            let pos = elem.ArrayPosition
            let baseArr = realAt pos
            let pAbs =
                i + (b.BoundDependencies |> List.sumBy (fun dd -> idxVals.[dd])) + int64 b.StrictOffset
            let extAt j = baseArr.Extents.[j]
            let strideAfter k =
                if k >= d - 1 then 1L
                else [ k + 1 .. d - 1 ] |> List.fold (fun acc j -> acc * extAt j) 1L
            let coordRc =
                if d = 1 then pAbs
                elif rc = 0 then pAbs / (strideAfter 0)
                elif rc = d - 1 then pAbs % (extAt rc)
                else (pAbs / (strideAfter rc)) % (extAt rc)
            let currentArr = Map.tryFind pos curArrays |> Option.defaultValue baseArr
            let peeled = A.peelDim currentArr coordRc
            paramCells.[elem.ParamVarId].V <- peeled
            match peeled with
            | VArray sub -> (Map.add pos sub curArrays, Set.add pos sliced)
            | _ -> (curArrays, sliced)
        | RealArray ->
            let pos = elem.ArrayPosition
            let baseArr = realAt pos
            let currentArr = Map.tryFind pos curArrays |> Option.defaultValue baseArr
            let isSliced = Set.contains pos sliced
            // Absolute flat coordinate: local loop var + (bound-deps + strict) if
            // reading the ORIGINAL array; local var + strict only if already
            // peeled at an outer level (deps already consumed by the slice).
            let index =
                if isSliced then i + int64 b.StrictOffset
                else (b.BoundDependencies |> List.sumBy (fun d -> idxVals.[d])) + int64 b.StrictOffset + i
            let peeled = A.peelDim currentArr index
            paramCells.[elem.ParamVarId].V <- peeled
            match peeled with
            | VArray sub -> (Map.add pos sub curArrays, Set.add pos sliced)
            | _ -> (curArrays, sliced)

    let evalKernelAndStore () =
        // Force the kernel result: the real Core.fs defers some sub-expression
        // forms (e.g. a kernel-embedded `<|>` / guard → VDeferred) rather than
        // routing them to this backend, so a raw Core.evalExpr can hand back a
        // VDeferred that must be resolved to a scalar before it is stored. A
        // concrete value passes straight through. A Reynolds kernel instead sums
        // the kernel over its surviving parameter permutations (plan precomputed).
        let v =
            match reynoldsPlan with
            | Some (pcells, plan) -> evalReynolds pcells plan
            | None -> force st kenv (Core.evalExpr st kenv cg.KernelExpr)
        match out with
        | OutArray a ->
            let coords = [ for lvl in 0 .. n - 1 -> idxVals.[lvl] ]
            A.writeCell a coords v
        | OutFold (acc, wrapper) ->
            acc.V <- wrapper acc.V v

    let rec loop (lvl: int) (curArrays: Map<int, BladeArray>) (sliced: Set<int>) =
        if lvl = n then evalKernelAndStore ()
        else
            let b = bindings.[lvl]
            let extent = levelExtent b
            let sub = (b.BoundDependencies |> List.sumBy (fun d -> idxVals.[d])) + int64 b.StrictOffset
            let bound = extent - sub
            let mutable i = 0L
            while i < bound do
                idxVals.[lvl] <- i
                let mutable ca = curArrays
                let mutable sl = sliced
                for elem in b.Elements do
                    let (ca', sl') = peelElement b elem extent ca sl
                    ca <- ca'
                    sl <- sl'
                loop (lvl + 1) ca sl
                i <- i + 1L

    // Base current-arrays: each real position starts at its base array.
    let baseMap =
        inputs
        |> Seq.choose (fun kv -> match kv.Value with SReal a -> Some (kv.Key, a) | _ -> None)
        |> Map.ofSeq
    loop 0 baseMap Set.empty

// ============================================================================
// Reduce over a DEFERRED computation
// ============================================================================

/// reduce over a DEFERRED computation (genReduceComputeBinding 8714): fold every
/// cell of each leaf apply into a per-leaf scalar accumulator (all seeded with
/// `seed`), through the fold kernel wrapper — ONE nest per leaf (value-identical
/// to CodeGen's staggered merged nest, since leaves don't interact). Single leaf
/// ⇒ scalar; fusion tree ⇒ a STRUCTURED tuple mirroring the tree shape (same
/// rationale as forceTreeShaped: shape-driven projections must see the type's
/// structure; a flat destructure flattens through nesting, so both styles work).
let rec private forceReduceCompute (st: InterpState) (env: Env) (comp: IRExpr) (kernel: IRExpr) (seed: Value) : Value =
    let rec resolveDeferred e =
        match e with
        | IRVar (id, _) -> (match envTryFind env id with Some cell -> (match cell.V with VDeferred (e2, _) -> resolveDeferred e2 | _ -> e) | None -> e)
        | _ -> e
    let rec collect e =
        match resolveDeferred e with
        | IRFusion (l, r) -> collect l @ collect r
        | other -> [ other ]
    let leaves = collect comp
    let infos = leaves |> List.choose (function IRApplyCombinator i -> Some i | _ -> None)
    if infos.IsEmpty || infos.Length <> leaves.Length then
        raise (InterpUnsupported "reduce over a deferred computation with non-apply leaves")
    let fold = resolveBinaryFold st kernel
    let accVals =
        infos |> List.map (fun info ->
            gateInputs info
            let names = info.Arrays |> List.mapi (fun i _ -> sprintf "a%d" i)
            let cg = buildLoopNestCodeGen info names "acc" st.Builder
            if hasRealSymmetry cg.OutputSymmVec || cg.HasReynolds || (cg.Bindings |> List.exists (fun b -> b.FusedRank.IsSome)) then
                raise (InterpUnsupported "reduce over symmetric/Reynolds/fused computation (M2.5)")
            let inputs = Dictionary<int, ArraySource>()
            info.Arrays |> List.iteri (fun i arr -> inputs.[i] <- resolveArraySource st env arr)
            let realAt (pos: int) = match inputs.TryGetValue pos with | true, SReal a -> a | _ -> raise (InterpUnsupported "reduce-compute: virtual position needs no realAt")
            let levelExtent (b: LoopIndexBinding) : int64 =
                match b.Extent with
                | IRLit (IRLitInt n) -> n
                | IRCompoundMask _ -> raise (InterpUnsupported "compound-index loop level (M2.7)")
                | _ ->
                    let pos = match b.Elements with e :: _ -> e.ArrayPosition | [] -> 0
                    match inputs.TryGetValue pos with | true, SReal a -> a.Extents.[b.ExtentDimRef] | _ -> toI64 (Core.evalExpr st env b.Extent)
            let acc = { V = seed }
            interpretNest st env cg inputs realAt levelExtent (OutFold (acc, fold))
            acc.V)
    match accVals with
    | [ single ] -> single
    | _ ->
        // Reassemble the accumulators into the TREE shape (accVals is the
        // in-order leaf list, so consuming it left-to-right while walking the
        // fusion tree reproduces the type's nesting exactly).
        let rec assemble (e: IRExpr) (accs: Value list) : Value * Value list =
            match resolveDeferred e with
            | IRFusion (l, r) ->
                let (lv, rest) = assemble l accs
                let (rv, rest') = assemble r rest
                (VTuple [| lv; rv |], rest')
            | _ ->
                match accs with
                | a :: rest -> (a, rest)
                | [] -> raise (InterpUnsupported "reduce-compute: accumulator/tree shape mismatch")
        fst (assemble comp accVals)

// ============================================================================
// Standalone virtual-array materialization (rare: a bare printed range/reverse)
// ============================================================================

let private materializeVirtual (st: InterpState) (env: Env) (idxTys: IRIndexType list) (kind: VirtualKind) : Value =
    match idxTys with
    | [ ix ] ->
        let n = match ix.Extent with IRLit (IRLitInt n) -> n | e -> toI64 (Core.evalExpr st env e)
        let data =
            match kind with
            | VirtualReverse -> SInt (Array.init (int n) (fun i -> n - 1L - int64 i))
            | VirtualRange (Some (IRLit (IRLitInt off))) -> SInt (Array.init (int n) (fun i -> int64 i + off))
            | VirtualRange (Some off) -> let o = toI64 (Core.evalExpr st env off) in SInt (Array.init (int n) (fun i -> int64 i + o))
            | VirtualRange None -> SInt (Array.init (int n) (fun i -> int64 i))
            | RealArray -> raise (InterpUnsupported "materializeVirtual: RealArray")
        VArray (A.mkDenseArray (IRTScalar ETInt64) idxTys [| n |] data)
    | _ -> raise (InterpUnsupported "standalone multi-index virtual array")

// ============================================================================
// evalArrayNode — Core's array/loop/combinator fallthrough hook
// ============================================================================

let rec evalArrayNode (st: InterpState) (env: Env) (expr: IRExpr) : Value =
    match expr with
    // -- Loop objects: pure provenance (the <@> apply reads the baked ApplyInfo).
    | IRMethodFor _ | IRObjectFor _ -> VLoopObj { Provenance = expr; Captured = env }

    // -- Deferred combinator forms: hold the unevaluated expr + its env.
    | IRApplyCombinator _ | IRComposeApply _
    | IRParallel _ | IRFusion _ | IRFunctorMap _
    | IRGuard _ | IRSequence _ | IRBind _
    | IRComposeObj _ | IRComposeMeth _
    | IRFallback _ -> VDeferred (expr, env)

    // -- Choice `<|>`: DEFER only when an operand is itself a computation
    //    (mirrors computeDeferredIds' isDeferred — the printability oracle);
    //    otherwise it materializes NOW. A SCALAR choice (both operands scalar,
    //    incl. one embedded in a kernel body, 039) is `(l != 0) ? l : r` with
    //    `l` evaluated once — the exprToCpp(IRChoice) ternary.
    | IRChoice (left, right) ->
        let isChoiceComp (e: IRExpr) =
            match e with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _
            | IRFunctorMap _ | IRChoice _ | IRComposeObj _ | IRComposeMeth _
            | IRBind _ | IRGuard _ | IRSequence _ | IRZip _ -> true
            | IRVar (id, _) ->
                (match envTryFind env id with
                 | Some c -> (match c.V with VDeferred _ -> true | _ -> false)
                 | None -> false)
            | _ -> false
        if isChoiceComp left || isChoiceComp right then VDeferred (expr, env)
        else
            let lv = Core.evalExpr st env left
            if isNonZero lv then lv else Core.evalExpr st env right

    // -- Zip in kernel position: a value tuple. (A bare top-level zip binding is
    //    tuple-typed and prints nothing; consumed forms are absorbed into
    //    co-iteration ApplyInfo, never reaching here.)
    | IRZip elems -> VTuple (elems |> List.map (Core.evalExpr st env) |> Array.ofList)

    // -- The force point.
    | IRCompute inner -> forceExpr st env inner

    // -- Materialized array producers.
    | IRArrayLit (elems, arrType) -> evalArrayLit st env elems arrType
    | IRReduce (arrExpr, kernel, init) ->
        let av = force st env (Core.evalExpr st env arrExpr)
        let initV = init |> Option.map (Core.evalExpr st env)
        match av with
        | VArray a ->
            if a.Extents.Length <> 1 then raise (InterpUnsupported "reduce over rank>1 array (M2.7)")
            A.reduceArray a (resolveBinaryFold st kernel) initV
        | VCompound cv ->
            // reduce over a compound walks its compact present-cell buffer
            // (genReduceBinding compound arm, CodeGen.fs:1934-1938).
            A.compoundReduce cv (resolveBinaryFold st kernel) initV
        | _ -> raise (InterpUnsupported "reduce over a non-array value")
    | IRReduceCompute (comp, kernel, seedExpr) ->
        let seed = Core.evalExpr st env seedExpr
        forceReduceCompute st env comp kernel seed
    | IRProdSum args ->
        let arrs = args |> List.map (fun e -> match force st env (Core.evalExpr st env e) with VArray a -> a | _ -> raise (InterpUnsupported "prodsum over non-array"))
        A.prodSum arrs

    // -- group_by(vals, gk): gather each CSR group's values into a ragged array
    //    (genGroupByBinding). `gk` is a VGroupKeys (built at group_keys binding
    //    time); `vals` is forced (double-consumer memoized) so a deferred/computed
    //    values array is materialized once before the gather (013/022).
    | IRGroupBy (valsExpr, gkExpr) ->
        let gk =
            match Core.evalExpr st env gkExpr with
            | VGroupKeys g -> g
            | _ -> raise (InterpUnsupported "group_by: grouping operand is not a group_keys value")
        let vals = forceInputArray st env valsExpr
        let idxTys = match typeOf expr with ArrayElem at -> at.IndexTypes | _ -> []
        VArray (A.buildGroupBy idxTys gk vals)

    // -- Virtual arrays (standalone materialization; usually consumed as inputs).
    | IRRange (idxTys, offset) -> materializeVirtual st env idxTys (VirtualRange offset)
    | IRVirtualReverse ix -> materializeVirtual st env [ ix ] VirtualReverse
    | IRBlocked _ -> raise (InterpUnsupported "IRBlocked standalone materialization (M2.7)")

    // -- Array expression ops.
    | IRIndex (arrExpr, idxExprs, _) ->
        (match force st env (Core.evalExpr st env arrExpr) with
         | VArray a -> A.indexArray a (idxExprs |> List.map (Core.evalExpr st env))
         | VCompound cv -> compoundIndexRead st env cv idxExprs
         | _ -> raise (InterpUnsupported "IRIndex on non-array value"))
    | IRCurry (arrExpr, idxExpr, _) ->
        (match force st env (Core.evalExpr st env arrExpr) with
         | VArray a -> A.curryArray a (toI64 (Core.evalExpr st env idxExpr))
         | _ -> raise (InterpUnsupported "curry of non-array"))
    | IRExtent (arrExpr, dim) ->
        (match force st env (Core.evalExpr st env arrExpr) with
         | VArray a -> VInt (A.extent a dim)
         | _ -> raise (InterpUnsupported "extent of non-array"))
    | IRPolyIndex (packExpr, idxExpr) ->
        A.polyIndex (force st env (Core.evalExpr st env packExpr)) (toI64 (Core.evalExpr st env idxExpr))
    | IRContains (arrExpr, valExpr) ->
        // A compound operand walks its compact present-cell buffer as a plain
        // dense array (contains over an empty compound → false, 006).
        let arrOpt =
            match force st env (Core.evalExpr st env arrExpr) with
            | VArray a -> Some a
            | VCompound cv -> Some (A.compoundToDense cv)
            | _ -> None
        (match arrOpt with
         | Some a ->
            let target = Core.evalExpr st env valExpr
            let rank = a.Extents.Length
            let mutable found = false
            let rec walk lvl acc =
                if found then ()
                elif lvl = rank then
                    let cell = A.readCell a (List.rev acc)
                    if valuesEqual cell target then found <- true
                else for i in 0L .. a.Extents.[lvl] - 1L do (if not found then walk (lvl + 1) (i :: acc))
            walk 0 []
            VBool found
         | None -> raise (InterpUnsupported "contains over non-array"))

    | IRReplicate (countExpr, bodyExpr) ->
        let count = toI64 (Core.evalExpr st env countExpr)
        A.replicateArray count (force st env (Core.evalExpr st env bodyExpr))

    // -- M4 wave-1 eager set/reshape ops. Each FORCES its input(s) first (the
    //    double-consumer memoization via forceInputArray), then materializes a
    //    fresh dense rank-1 array (transpose: any rank). Semantics pinned to the
    //    CodeGen materialize*Form emitters (byte-verified):
    //      mask  = Bool PRESENCE array `m[i]=pred(A[i])`, source index space,
    //              NO compaction (maskPresence, NOT the deprecated maskArray);
    //      sort  = stable ascending by key (stable_sort on a permutation);
    //      unique/intersect/union = unordered_set first-occurrence order
    //              (unique: A; intersect: A-order deduped ∩ B; union: A then
    //               B-not-in-A);
    //      transpose = hard swap of two arity-1 axes into a fresh pool.
    | IRMask (arrExpr, predExpr) ->
        let a = forceInputArray st env arrExpr
        VArray (A.maskPresence a (resolveUnaryKernel st predExpr))
    | IRSort (arrExpr, keyExpr) ->
        let a = forceInputArray st env arrExpr
        VArray (A.sortArray a (resolveUnaryKernel st keyExpr))
    | IRUnique arrExpr ->
        VArray (A.uniqueArray (forceInputArray st env arrExpr))
    | IRIntersect (aExpr, bExpr) ->
        let a = forceInputArray st env aExpr
        let b = forceInputArray st env bExpr
        VArray (A.intersectArray a b)
    | IRUnion (aExpr, bExpr) ->
        let a = forceInputArray st env aExpr
        let b = forceInputArray st env bExpr
        VArray (A.unionArray a b)
    | IRTranspose (arrExpr, d1, d2) ->
        VArray (A.transposeArray (forceInputArray st env arrExpr) d1 d2)

    // -- Rank-changing assembly (formalism 2.6). Each operand is FORCED first
    //    (a deferred producer materializes once), then copied into a fresh
    //    dense pool: stack adds a leading selector axis, join concatenates
    //    along dim d. Pinned to CodeGen's materialize{Stack,Join}Form.
    | IRStack arrs ->
        VArray (A.stackArrays (arrs |> List.map (forceInputArray st env)))
    | IRJoin (arrs, dim) ->
        VArray (A.joinArrays (arrs |> List.map (forceInputArray st env)) dim)

    // -- Symmetry producers (M7-β). Each FORCES its input(s) first (so a deferred
    //    reynolds/gram source materializes once — 109/110/066), then materializes
    //    a fresh output. `typeOf expr` carries the fission / gram / preserved
    //    output type (shape + symmetry), driving storage class and print routing.
    //      decompact = value-equivalent group fission (read src at each output
    //                  cell's logical coord via readCompact);
    //      gram      = A·Bᴴ contraction into Sym/Hermitian-compact (same) or
    //                  dense (distinct);
    //      negate/conjugate = shape-preserving contiguous-pool transform.
    | IRDecompact (arrExpr, _) ->
        VArray (A.decompactArray (forceInputArray st env arrExpr) (typeOf expr))
    | IRGram (lExpr, rExpr, _) ->
        let l = forceInputArray st env lExpr
        let r = forceInputArray st env rExpr
        VArray (A.gramArray l r (typeOf expr))
    | IRArrayNegate arrExpr ->
        VArray (A.negateConjugateArray false (forceInputArray st env arrExpr))
    | IRArrayConjugate arrExpr ->
        VArray (A.negateConjugateArray true (forceInputArray st env arrExpr))

    // -- Inline object_for application: `A [op] B` outer product (and its
    //    elementwise / single-array-broadcast siblings). CodeGen.fs:7388 peels
    //    the array list out of the single tuple argument the same way.
    | IRApp (IRObjectFor objInfo, args, _) ->
        let arrays = match args with [ IRTuple elems ] -> elems | _ -> args
        materializeObjectForApp st env (typeOf expr) objInfo arrays

    | other -> raise (InterpUnsupported (nodeCase other))

/// Read a compound value at an index tuple (full / trailing / partial),
/// mirroring exprToCppCore's compoundRead dispatch (CodeGen.fs:1589-1712).
/// The coordinate exprs are evaluated in `env`; wildcard positions arrive as
/// `IRLit IRLitUnit` sentinels (classifyCompoundIndexTuple).
and private compoundIndexRead (st: InterpState) (env: Env) (cv: CompoundValue) (idxExprs: IRExpr list) : Value =
    // Rank-1 compound scalar sugar: `C(i)` == the 1-tuple read `C((i))`
    // (there is no surface 1-tuple literal). CodeGen.fs:1598-1606.
    let idxExprs =
        match cv.LeadRank, idxExprs with
        | 1, first :: rest when (match first with IRTuple _ -> false | _ -> true) -> IRTuple [ first ] :: rest
        | _ -> idxExprs
    match idxExprs with
    | (IRTuple coords) :: trailingIdxs ->
        match classifyCompoundIndexTuple cv.LeadRank coords with
        | CompoundFull ->
            let coordVals = coords |> List.map (fun c -> toI64 (Core.evalExpr st env c)) |> Array.ofList
            // A trailing wildcard (unit sentinel) or an omitted trailing index
            // leaves that dim free -> row sub-view; a concrete trailing index
            // supplies the offset -> scalar (CodeGen.fs:1688-1710).
            let concreteTrail =
                trailingIdxs |> List.filter (function IRLit IRLitUnit -> false | _ -> true)
            if cv.TrailingStride = 1L then
                A.compoundFullScalar cv coordVals 0L
            elif not (List.isEmpty concreteTrail) then
                A.compoundFullScalar cv coordVals (toI64 (Core.evalExpr st env (List.head concreteTrail)))
            else
                A.compoundRow cv coordVals
        | CompoundPartial (pinned, freePos) ->
            let pinnedVals = pinned |> List.map (fun (pos, c) -> (pos, toI64 (Core.evalExpr st env c)))
            A.compoundPartial cv pinnedVals freePos
    | _ -> raise (InterpUnsupported "compound index without a tuple head")

/// Structural value equality for `contains` (numeric-aware).
and private valuesEqual (a: Value) (b: Value) : bool =
    match a, b with
    | VInt x, VInt y -> x = y
    | VFloat x, VFloat y -> x = y
    | VInt x, VFloat y | VFloat y, VInt x -> float x = y
    | VBool x, VBool y -> x = y
    | VString x, VString y -> System.String.Equals(x, y, System.StringComparison.Ordinal)
    | VComplex (r1, i1), VComplex (r2, i2) -> r1 = r2 && i1 = i2
    | _ -> false
