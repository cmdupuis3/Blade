// The type checker's REDUCE rules, outside the inference chain (cleanup round 2, K5): inferReduce and the forms it
// dispatches to (the partial and rank-k folds, the leading-axis fold), the fused reduce-compute recognizers
// (tryInferReduceCompute*), the fold's omp-licence check, and blockLocalVids, the block-let side table their alias
// chase reads. They type their operands through the chain's inferExpr and hand the general kernel fold to the
// chain's inferReduceGeneral, both reached through InferEntries (TypeCheckInferEntries.fs). (inferReduceGeneral
// stays in the chain: it types the kernel through inferLambdaHinted, whose six curried arguments would cost a
// closure per call through a record field.)
module Blade.TypeCheckInferReduce

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
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower
open Blade.TypeCheckSupport
open Blade.TypedBuilder
open Blade.TypeCheckInferUnits
open Blade.TypeCheckInferApply
open Blade.TypeCheckInferEntries
open Blade.TypeCheckInferRecArray
open Blade.TypeCheckInferArrayOps
open Blade.TypeCheckInferIntrinsics
open Blade.TypeCheckInferArith

/// The binder ids of lets bound INSIDE a block ({ let b = g; ... }, at top
/// level or in a body; inferBlock records them), for the reduce fusion's alias
/// chase (gap G60, formalism 10.2): only a TOP-LEVEL alias of a deferred map
/// is the same deferred value -- a block alias is a local assignable copy
/// (written freely: let mut b = g; b(1) = 50.0), so the chase stops at it
/// and the fold reads the copy's stored cells. By binder IDENTITY, so a
/// shadowing name elsewhere changes nothing. Reset by checkModule.
let internal blockLocalVidsStorage = System.Threading.AsyncLocal<System.Collections.Generic.HashSet<IRId>>()
let internal blockLocalVids () : System.Collections.Generic.HashSet<IRId> =
    let v = blockLocalVidsStorage.Value
    if isNull (box v) then
        let fresh = System.Collections.Generic.HashSet<IRId>()
        blockLocalVidsStorage.Value <- fresh
        fresh
    else v

/// Detect and type the fused reduction terminal. Returns None when the
/// first argument is NOT a deferred computation (ordinary array reduce
/// proceeds). Some (Ok ...) types the fold: the child spliced into the
/// typed node is a CANONICAL left-nested fusion over the RESOLVED leaves
/// (one let-hop each, the <@> resolution rule), so lowering and codegen
/// never chase variable bindings. Result type: elem for a single apply,
/// left-nested scalar pairs for a tree (mirroring `|> compute`'s tuple
/// convention -- nested pair TYPE, flat make_tuple VALUE, flat projections).
let rec tryInferReduceCompute (env: TypeEnv) (tArr: TypedExpr) (tKernel: TypedExpr) (tInitOpt: TypedExpr option) : TypeResult<TypedExpr> option =
    // Collect fusion leaves left-to-right, resolving each through bindings.
    // None = the root is not a deferred computation at all (fall through);
    // Some (Error _) = it IS deferred but malformed for a fused fold.
    let rec collect (t: TypedExpr) : Result<TypedExpr list, TypeError> option =
        match t.Kind with
        // An ANONYMOUS force in operand position: `reduce(x * y, (+))`,
        // `reduce(cos(A), (+))`. The elementwise-operator zip arm and the
        // intrinsic array lifts wrap their synthesized applies in
        // `ExprCompute` unconditionally -- right everywhere EXCEPT here,
        // where the compute node is a subexpression of this very reduce and
        // the fold is therefore its only possible consumer. There is no
        // binding to print, capture, or read twice, which is what the
        // declines below protect; seeing through the wrapper turns the
        // alloc-fill-refold shape (a fresh temp per evaluation -- per OUTPUT
        // CELL when the reduce sits in a kernel body, the gram fixture's
        // ~14.5 GB) into the fused nest. Matching `t.Kind` BEFORE the
        // binding walk keeps every NAMED computation on today's route: a
        // `let c = A + B` still resolves as `TExprVar`, still materializes
        // once, and still auto-prints (loops/095). Terminal-shape errors
        // under this unwrap decline instead of surfacing -- see
        // `anonymousUnwrap` at the callsite below.
        | TExprCompute inner -> collect inner
        | _ ->
        // Resolved to a FIXPOINT (gap G60): an alias of a deferred map
        // (`let g2 = g`) is the same deferred value (formalism 10.2), so
        // `reduce(g2, (+))` fuses exactly as `reduce(g, (+))` does. The
        // one-hop resolution stopped at `g` and left the fold an ordinary
        // reduce over a NAME, which the interpreter and the llvm lane force
        // (printing the map) where the direct spelling prints nothing.
        // Only a TOP-LEVEL alias is chased past: a BLOCK alias (let mut b =
        // g inside { }, blockLocalVids) is a local assignable copy, so --
        // as the one-hop resolution always did -- the chase stops at the
        // value it names and the fold reads the copy's stored cells. (A write
        // through a deferred binding itself is refused at lowering, so a
        // chased value is never one the program wrote.)
        let blockLocal = blockLocalVids ()
        let rec chase (fuel: int) (e: TypedExpr) : TypedExpr =
            match e.Kind with
            | TExprVar (_, vid, _) when fuel > 0 ->
                let r = resolveTypedExpr env e
                if System.Object.ReferenceEquals(r, e) || blockLocal.Contains vid then r
                else chase (fuel - 1) r
            | _ -> e
        let r = chase 64 t
        match r.Kind with
        | TExprFusion (l, rgt) ->
            (match collect l, collect rgt with
             | Some (Ok ls), Some (Ok rs) -> Some (Ok (ls @ rs))
             | Some (Error e), _ | _, Some (Error e) -> Some (Error e)
             | _ ->
                Some (Error (ReductionFormErr "reduce() over a fused tree requires every <&!> leaf to be an unforced `method_for/object_for <@> kernel` application")))
        | TExprApply info when not info.IsComposeApply -> Some (Ok [r])
        | TExprApply _ ->
            Some (Error (NotYetSupportedErr "reduce() over a composed (>>@/@>>) application is not supported yet -- force it with `|> compute` and reduce the resulting array"))
        | _ -> None
    tryInferReduceComputeFused env tKernel tArr tInitOpt collect

and tryInferReduceComputeFused (env: TypeEnv) (tKernel: TypedExpr) (tArr: TypedExpr) (tInitOpt: TypedExpr option) (collect: TypedExpr -> Result<TypedExpr list, TypeError> option) : TypeResult<TypedExpr> option =
    // ---- Don't splice a SECOND copy of an already-materialized let --------
    // The fusion above resolves through bindings and splices the RESOLVED
    // apply into the typed node -- a COPY of the computation, not a reference
    // to the binding. At module level that is exactly right: `let c = A <@> k`
    // stays deferred, nothing materializes it, and the fold is the only
    // consumer, so fusing is strictly cheaper than forcing.
    //
    // Inside a callable body the arithmetic inverts. Lowering's S2
    // (`forceBareCombinatorLets`) wraps every body-local `let` whose RHS is a
    // bare combinator in `IRCompute` -- there is no forcing site downstream of
    // it, so the array gets built whether or not anyone reads it. Splicing on
    // top of that materializes the SAME computation twice per outer cell: the
    // now-dead let, plus the fused nest. `units/065`'s
    // `let e = exp <@> (i*w*ts); reduce(e, (+))` emitted 3 arrays and 2 full
    // `std::exp` passes where 2 arrays and 1 pass compute the same values.
    // Declining here drops the fold to the ordinary `IRReduce` over the array
    // the let already built.
    //
    // A CAPTURED binding declines for the mirror-image reason. Splicing inside
    // a kernel body puts the two halves of the same lambda into disagreement:
    // the body fuses a copy of `c`'s producer (and so spells `c`'s OWN inputs,
    // dragging them in as module references), while lambda-lifting still lists
    // `c` itself in `Captures` and forwards it BY NAME at every call site. The
    // capture is undeclared (`c` is deferred, so it has no C++ definition), and
    // the extra module references demote the lambda to a main() local emitted
    // in id order -- after the very binding that calls it:
    //
    //   let c   = method_for(A) <@> lambda(x) -> x * 2.0        // deferred
    //   let out = ws <@> lambda(w) -> w * reduce(c, (+)) |> compute
    //   // 'c' was not declared in this scope
    //   // '__lambda_16' was not declared in this scope
    //
    // CodeGen now materializes a deferred capture at the boundary that
    // forwards it (collectDeferredKernelCaptures), so the array IS built by
    // the time the body runs and the ordinary `IRReduce` over it is right --
    // the same trade the body-local clause makes, for the same reason.
    //
    // Deliberately narrow, because every clause pays for itself:
    //  * `bodyLocalBinding` / `capturedOuterBinding` -- a MODULE-LEVEL fold
    //    (`loops/095`, the deferred-concrete corpus) is inside no callable at
    //    all, so nothing captures or materializes `c` and fusing stays
    //    strictly cheaper than forcing. That case is untouched.
    //  * a bare `TExprVar` root only -- a written-out `reduce(A <@> k, (+))`
    //    has no binding and nothing materialized, so it must still fuse.
    //  * a SINGLE apply, never a `<&!>` fusion tree -- a tree has no array
    //    form to fall back to (staggered leaf ranks, tuple result).
    //  * a plain array output -- compact symmetric/antisymmetric/Hermitian
    //    output still reaches the error below rather than silently changing
    //    which cells get folded.
    //  * an UNANNOTATED fold kernel -- see `foldKernelIsParallel`.
    //
    // A fold kernel carrying `omp`/`cuda`/`mpi` keeps the fused terminal, for
    // the same reason `rankKDesugar` declines for it (see `deferredWithOmpKernel`
    // there): the fused nest is what chunks the outer level, and the ordinary
    // `IRReduce` carries no clause, so declining would turn a licensed parallel
    // fold serial with no diagnostic -- the exact silent drop that feature
    // exists to prevent. Today's omp reduce-over-computation cases are all
    // module-level and written inline, so none of them reach this guard anyway;
    // it is here so the body-local spelling cannot regress into a silent
    // serialization.
    let foldKernelIsParallel () =
        match (resolveTypedExpr env tKernel).Kind with
        | TExprLambda li -> not li.Parallel.IsEmpty
        | TExprVar (_, fnId, _) ->
            (match env.FuncParallel.TryGetValue fnId with
             | true, (_, s) -> not s.IsEmpty
             | _ -> false)
        | _ -> false
    // A map over a CLOSED-FORM DOMAIN (`range<R>` over a static struct whose
    // conjuncts are linear, docs/plans/structural/06) is the one body-local
    // let the fold should still SPLICE: the fused nest streams the solutions
    // with no array and no key table, while the materialized route builds the
    // sparse key view AND the value buffer only to walk them once. Lowering's
    // S2 leaves such a let unforced once the splice has left it unreferenced
    // (`forceBareCombinatorLets`), so nothing is computed twice.
    let overClosedFormDomain (info: TypedApplyInfo) =
        info.ArrayTypes |> List.exists (fun at ->
            at.IndexTypes |> List.exists (fun ix ->
                ix.IxKind = IxKSparse
                && (match ix.Extent with IRSparseKeys (SkDomain _) -> true | _ -> false)))
    let alreadyMaterializedLet () =
        match tArr.Kind with
        | TExprVar (name, _, _) when (bodyLocalBinding name env || capturedOuterBinding name env)
                                     && not (foldKernelIsParallel ()) ->
            (match (resolveTypedExpr env tArr).Kind with
             | TExprApply info when not info.IsComposeApply && overClosedFormDomain info
                                    && bodyLocalBinding name env -> false
             | TExprApply info when not info.IsComposeApply ->
                (match env.Subst.Resolve info.OutputType with
                 | ArrayElem arr ->
                    arr.IndexTypes |> List.forall (fun ix -> ix.Symmetry = SymNone)
                 | _ -> false)
             | _ -> false)
        | _ -> false
    // Whether `collect` will see through an ANONYMOUS `|> compute` at the
    // root. Such an operand has today's materializing route as a WORKING
    // fallback, so a fused-terminal shape error (a lambda fold kernel with no
    // init, a packed-output leaf) must DECLINE rather than surface: the
    // program keeps compiling on the route it always had, and only shapes
    // the terminal actually handles change emission.
    let anonymousUnwrap = tArr.Kind.IsTExprCompute
    // INPUT CLASSES THE FUSED TERMINAL HAS NO ARM FOR. `genReduceComputeBinding`
    // bypasses `genApplyCombinator`'s special input paths -- ragged peel,
    // grouped, compound, dependent-inner -- and refuses them outright
    // (BL7004, CodeGenBinding.fs). That refusal is fine for a program that
    // ASKED to fuse, but the anonymous unwrap volunteers programs that never
    // did: before it, `reduce(<grouped computation>, op)` took the
    // materializing route and compiled. So the unwrap must decline exactly
    // where codegen refuses, or it converts working programs into BL7004 --
    // which is what it did to six grouped-peel AD tests (ad-jvp-comb 046 and
    // siblings), a regression the fused-fold measurement missed because it
    // only ran dense categories.
    //
    // Mirrors CodeGenBinding's predicate deliberately: same four kinds, so
    // "what declines here" and "what refuses there" cannot drift apart
    // silently. A NAMED operand is unaffected either way -- it never reaches
    // this unwrap, and its BL7004 (if any) is a real diagnostic about a
    // program that really did ask.
    let unsupportedFusedInput (leaves: TypedExpr list) : bool =
        leaves |> List.exists (fun leaf ->
            match leaf.Kind with
            | TExprApply info ->
                info.ArrayTypes |> List.exists (fun at ->
                    at.IndexTypes |> List.exists (fun ix ->
                        isRaggedFamilyKind ix.IxKind || ix.IxKind = IxKDepInner
                        || ix.IxKind = IxKGroupOuter || ix.IxKind = IxKCompound))
            | _ -> false)
    // ---- Don't splice a NAMED producer that WRITES (gap G45) ---------------
    // An effectful named map is evaluated ONCE, at its definition (Lowering's
    // forceEffectfulModuleLets, S2 in a body; formalism 10.2): every read,
    // this fold included, reads the stored array. Splicing a copy of the
    // producer into the fold would run its writes again per fold (a block
    // with two folds counted 3x). Declines to the ordinary `IRReduce` over
    // the named array. A written-out `reduce(A <@> k, op)` has no name and
    // still fuses; a `<&!>` tree over a writing leg is the join rule's.
    let namedWritingProducer () =
        match tArr.Kind with
        | TExprVar _ ->
            (match (resolveTypedExpr env tArr).Kind with
             | TExprApply info when not info.IsComposeApply ->
                let r = resolveTypedExpr env tArr
                not (outerWritesOfBody env env r).IsEmpty
                || (parallelCalleeWriteError env [] r).IsSome
             | _ -> false)
        | _ -> false
    // ---- Keep the spine's NAME when a capture forces it (gap G235 (a)) -----
    // A capture of the fold kernel forces a deferred binding ONCE under its own
    // name before the traversal (formalism 10.2, G214); when that force reaches
    // the spine p -- the kernel captures p, an alias of p, a top-level
    // projection of a wholly deferred tuple holding p, or a map whose operand or
    // kernel capture reaches p -- the traversal must fold p's SNAPSHOT, not a
    // fresh walk of p's producer (a seed that writes an input of p runs after
    // the force: `reduce(p, lambda(a, b) -> a + b + Float64(extents(q)),
    // { x(0) = 10.0; 0.0 })` with q over p folded the written x). The terminal
    // then keeps the spine VARIABLE as its child (TypedAst.markKeptSpine, Lowering:
    // `IRReduceCompute (IRVar p, ..)`), and each lane's fused-terminal prelude
    // forces leaf then fold kernel captures in order and folds the stored p.
    // NARROW by design: a mark where p is NOT forced by name would make the
    // fold force p itself (a new print line), while a missed member keeps the
    // fresh walk every lane agrees on. Not followed: choices, fallbacks,
    // guards, tuple values (copies, G213), named function kernels (a function
    // body reads a module map privately, G75), seed reads (staged, G235).
    // Module level and top-level blocks only; a rank-1 scalar-element spine
    // (a rank >= 2 or fusion-tree spine is G233); not a parallel fold kernel.
    // (shared by the kept root and the kept tree leaves, gap G233)
    let blockLocalK = lazy (blockLocalVids ())
    let valueOfK (name: string) (vid: IRId) : TypedExpr option =
        match lookupVar name env with
        | Some info when info.VarId = vid -> info.TypedValue
        | _ -> None
    // a plain dense unsymmetric scalar-element map, of any rank (G233): the
    // storage every lane's stored-leaf traversal folds
    // (IRStorage.storedLeafTraversal)
    let plainMapValue (v: TypedExpr) =
        match v.Kind with
        | TExprApply info when not info.IsComposeApply ->
            (match env.Subst.Resolve info.OutputType with
             | ArrayElem arr ->
                not arr.IndexTypes.IsEmpty
                && arr.IndexTypes |> List.forall (fun ix -> ix.Symmetry = SymNone && ix.IxKind = IxKPlain)
                && (match IR.stripUnits (env.Subst.Resolve arr.ElemType) with
                    | IRTScalar _ -> true
                    | _ -> false)
             | _ -> false)
        | _ -> false
    // Does a FORCING EVENT reach a binder of `targets`? A capture forces its
    // map under its own name, and that force forces the map's deferred
    // inputs and its kernel's captures under theirs (formalism 10.2). A fresh
    // visited set per question (Codex G233 design review: one shared set can
    // suppress another leaf's proof).
    let reachesTargets (targets: Set<IRId>) =
        let visited = System.Collections.Generic.HashSet<IRId>()
        let rec reachesVar (name: string) (vid: IRId) : bool =
            if targets.Contains vid then true
            elif not (visited.Add vid) || blockLocalK.Value.Contains vid then false
            else
                match valueOfK name vid with
                | Some value -> reachesValue value
                | None -> false
        and reachesValue (value: TypedExpr) : bool =
            match value.Kind with
            | TExprVar (n, vid, _) -> reachesVar n vid
            | TExprApply info when not info.IsComposeApply ->
                (info.Arrays |> List.exists (fun a ->
                    match a.Kind with
                    | TExprVar (n, vid, _) -> reachesVar n vid
                    | _ -> false))
                || kernelReaches info.Kernel
            // a BLOCK-valued binding defers exactly when its final
            // value does (IRLift's let chain): judged by that value
            | TExprBlock (stmts, Some final)
                when stmts |> List.forall (function TStmtLet _ -> true | _ -> false) ->
                reachesValue final
            // a top-level projection of a WHOLLY deferred tuple is an
            // alias of its member (formalism 10.2, gap G111)
            | TExprTupleIndex (t, { Kind = TExprLit (LitInt k) }) ->
                (match (resolveTypedExprDeep env t).Kind with
                 | TExprTuple es when int k >= 0 && int k < es.Length
                                      && es |> List.forall (fun m ->
                                             match m.Kind with
                                             | TExprVar _ ->
                                                (match (resolveTypedExprDeep env m).Kind with
                                                 | TExprApply mi -> not mi.IsComposeApply
                                                 | _ -> false)
                                             | _ -> false) ->
                    (match es.[int k].Kind with
                     | TExprVar (n, vid, _) -> reachesVar n vid
                     | _ -> false)
                 | _ -> false)
            | _ -> false
        and kernelReaches (k: TypedExpr) : bool =
            // through callable aliases (`let k2 = k`, Codex diff review)
            match (resolveTypedExprDeep env k).Kind with
            | TExprLambda li -> li.Captures |> List.exists (fun cv -> reachesVar cv.Name cv.VarId)
            | _ -> false
        (reachesValue, kernelReaches)
    // ---- Keep a TREE LEAF's NAME (gap G233, round 4 wave 10) ----------------
    // The `<&!>` tree's leaves are spliced as their RESOLVED applies; a leaf
    // written as the NAME of a deferred map binding that the fold's own
    // prelude forces under its own name -- a capture of ANY leaf kernel (the
    // other leaves' too) or of the fold kernel reaching it, or the private
    // evaluation of a leaf's deferred operand whose kernel captures (or whose
    // inputs reach) it -- keeps its NAME in the tree, so every lane's prelude
    // walks the as-written leaves and the traversal folds that leaf's
    // snapshot (IRStorage.storedLeafTraversal). Narrow as the root rule
    // below: module level and top-level blocks, a direct name of the map (no
    // alias chain, no block-local binder: staged, G233), a plain dense map.
    let leafNames (t: TypedExpr) : TypedExpr option list =
        let rec go (t: TypedExpr) =
            match t.Kind with
            | TExprCompute inner -> go inner |> List.map (fun _ -> None)
            | TExprFusion (l, r) -> go l @ go r
            | TExprVar (_, vid, _) when not (blockLocalK.Value.Contains vid) ->
                let r = resolveTypedExpr env t
                (match r.Kind with
                 | TExprApply info when not info.IsComposeApply && not (obj.ReferenceEquals (r, t)) -> [ Some t ]
                 | TExprFusion _ -> go r |> List.map (fun _ -> None)
                 | _ -> [ None ])
            | _ -> [ None ]
        go t
    let keptTreeLeaves (leaves: TypedExpr list) : TypedExpr list =
        if env.InCallableBody || foldKernelIsParallel () || leaves.Length < 2 then leaves else
        let names = leafNames tArr
        if names.Length <> leaves.Length then leaves else
        let leafVids =
            names |> List.choose (Option.bind (fun n -> match n.Kind with TExprVar (_, v, _) -> Some v | _ -> None)) |> Set.ofList
        let leafKernels =
            leaves |> List.choose (fun l -> match l.Kind with TExprApply info -> Some info.Kernel | _ -> None)
        // the leaves' deferred OPERANDS (a fold's private operands: evaluated
        // before the cells, their kernels' captures and inputs forced)
        let privateValues =
            leaves |> List.collect (fun l ->
                match l.Kind with
                | TExprApply info ->
                    info.Arrays |> List.choose (fun a ->
                        match a.Kind with
                        | TExprVar (n, v, _) when not (leafVids.Contains v) ->
                            (match valueOfK n v with
                             | Some ({ Kind = TExprApply pi } as pv) when not pi.IsComposeApply -> Some pv
                             | _ -> None)
                        | _ -> None)
                | _ -> [])
        List.map2 (fun (name: TypedExpr option) (leaf: TypedExpr) ->
            match name with
            | Some ({ Kind = TExprVar (n, vid, _) } as nameExpr) when plainMapValue leaf ->
                let (reachesValue, kernelReaches) = reachesTargets (Set.singleton vid)
                if (leafKernels @ [ tKernel ]) |> List.exists kernelReaches
                   || privateValues |> List.exists reachesValue then
                    Blade.Effects.Decisions.record
                        { Blade.Effects.Rule = "deferred-capture-force"; Version = 1; Span = nameExpr.Span; Subject = n
                          Outcome = Blade.Effects.Applied
                          Evidence = [ $"a capture in the fold's prelude forces the leaf `{n}` under its own name before the traversal: the tree keeps the leaf's name and folds that snapshot, not a fresh walk of its producer (G233)" ] }
                    nameExpr
                else leaf
            | _ -> leaf) names leaves
    let keepSpineName () : bool =
        if env.InCallableBody || foldKernelIsParallel () then false else
        match tArr.Kind with
        | TExprVar (_, spineVid0, _) ->
            let blockLocal = blockLocalK.Value
            let valueOf = valueOfK
            // the spine's alias chain (`reduce(p2, ..)`, `let p2 = p`): one class
            let rec chain (fuel: int) (name: string) (vid: IRId) (acc: Set<IRId>) =
                let acc = acc.Add vid
                if fuel <= 0 || blockLocal.Contains vid then (acc, None) else
                match valueOf name vid with
                | Some v ->
                    (match v.Kind with
                     | TExprVar (n2, v2, _) -> chain (fuel - 1) n2 v2 acc
                     | _ -> (acc, Some v))
                | None -> (acc, None)
            let spineName = match tArr.Kind with TExprVar (n, _, _) -> n | _ -> ""
            match chain 64 spineName spineVid0 Set.empty with
            // the root must BE the map binding: through an alias root
            // (`reduce(p2, ..)`, `let p2 = p`) the lanes relate the root to the
            // forced source differently (Codex diff review round 2) -- the
            // fresh walk every lane agrees on is kept
            | spineSet, Some _ when spineSet.Count > 1 -> false
            | spineSet, Some v ->
                if not (plainMapValue v) then false else
                let (_, kernelReaches) = reachesTargets spineSet
                // G248 residual (round 4 wave 9, B2): the SEED reads a cell of
                // the spine (or of an alias in its chain) in an UNCONDITIONAL
                // position -- `{ let t = p(0); x(0) = 10.0; t }` -- which forces
                // the spine under its own name at the fold's operand position
                // (the G214 chosen rule), so the fold reads that snapshot. Only
                // when the fold kernel captures nothing (no capture prelude to
                // order against the seed force: the C++ statement pre-pass
                // forces seed reads first, the interpreter the captures first).
                // Not walked: `if` / `match` arms, the right operand of && / ||,
                // lambda bodies (a read there is a capture, formed later).
                let seedReadsSpine () =
                    let kernelCaptureFree =
                        match (resolveTypedExprDeep env tKernel).Kind with
                        | TExprLambda li -> li.Captures.IsEmpty
                        | TExprSection _ -> true
                        | _ -> false
                    // the ROOT itself only (the interpreter's operand-position
                    // force reads the seed's read ids against the root)
                    let isSpine (e: TypedExpr) =
                        match e.Kind with
                        | TExprVar (_, vid, _) -> vid = spineVid0
                        | _ -> false
                    let rec walk (e: TypedExpr) : bool =
                        match e.Kind with
                        | TExprIndex (a, _, _) when isSpine a -> true
                        | TExprApp (f, _) when isSpine f -> true
                        | TExprLambda _ -> false
                        | TExprIf (cnd, _, _) -> walk cnd
                        | TExprMatch (s, _) -> walk s
                        | TExprBinOp (_, (OpAnd | OpOr), l, _) -> walk l
                        | _ -> typedExprChildren e |> List.exists walk
                    kernelCaptureFree
                    && (match tInitOpt with Some s -> walk s | None -> false)
                kernelReaches tKernel || seedReadsSpine ()
            | _ -> false
        | _ -> false
    if alreadyMaterializedLet () || namedWritingProducer () then None else
    tryInferReduceComputeLeaves env keepSpineName collect tArr anonymousUnwrap unsupportedFusedInput tInitOpt tKernel keptTreeLeaves

and tryInferReduceComputeLeaves (env: TypeEnv) (keepSpineName: unit -> bool) (collect: TypedExpr -> Result<TypedExpr list, TypeError> option) (tArr: TypedExpr) (anonymousUnwrap: bool) (unsupportedFusedInput: TypedExpr list -> bool) (tInitOpt: TypedExpr option) (tKernel: TypedExpr) (keptTreeLeaves: TypedExpr list -> TypedExpr list) : TypeResult<TypedExpr> option =
    let keepSpine = keepSpineName ()
    match collect tArr with
    | None -> None
    | Some leavesR when anonymousUnwrap
                        && (match leavesR with Ok ls -> unsupportedFusedInput ls | Error _ -> false) -> None
    | Some leavesR ->
        let built = (
            leavesR |> Result.bind (fun leaves ->
            // Each leaf must produce plain (non-compact) cells: folding
            // canonical vs logical cells of symmetric storage differ, the
            // same ambiguity the array form rejects.
            let leafElem (leaf: TypedExpr) : Result<IRType, TypeError> =
                match leaf.Kind with
                | TExprApply info ->
                    (match env.Subst.Resolve info.OutputType with
                     | ArrayElem arr ->
                        let packed =
                            arr.IndexTypes |> List.exists (fun ix -> not ix.Symmetry.IsSymNone)
                        if packed then
                            Error (CompactStorageErr "reduce() over a computation with compact symmetric/antisymmetric/Hermitian output is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. Force with `|> compute` and decompact(A, d) first for the logical fold.")
                        else Ok arr.ElemType
                     | IRTScalar _ as s -> Ok s
                     | _ -> Error (ReductionFormErr "reduce() over a deferred computation needs an array-producing kernel application"))
                | _ -> Error (InternalErr "reduce(): internal -- fusion leaf is not an apply")
            leaves |> List.map leafElem |> sequenceResults |> Result.bind (fun elems ->
            let elem0 = elems.Head
            // LEGS THAT DIFFER ONLY IN UNIT. A shared fold kernel is ONE typed
            // node, so the chain below unifies every leaf's element with the
            // first -- right for one element type, wrong for the multi-
            // statistic sweep over a unit-carrying array: `reduce((L <@> id)
            // <&!> (L <@> sq) <&!> (L <@> one), (+))` folds meters, meters^2
            // and a bare count, and the unify refused it (BL3006) although
            // the Fusion rule types the tree alpha x beta (formalism 10.2) and
            // the reduction-JOIN spelling of the same three legs was already
            // admitted. An operator section is polymorphic, so a per-leg
            // instance of it is the same fold: when the unify fails and the
            // legs are concrete scalars of ONE element type, the node takes
            // the join encoding (k kernels, k seeds, flat Tuple<k> result,
            // see IRReduceCompute) -- exactly what the join form builds.
            // Only on that failure, so every chain that typed before is
            // untouched; a lambda kernel (one typed body) still needs one
            // element type. Each leg's fold must still preserve its unit
            // (`*` does not: reduceKernelUnitCheck, as the array form).
            let unitOnlyLegs () =
                elems.Length >= 2 && tInitOpt.IsNone
                && (match tKernel.Kind with
                    | TExprSection (OpAdd | OpMul) -> true
                    | _ -> false)
                && (let rs = elems |> List.map env.Subst.Resolve
                    rs |> List.forall (fun r ->
                        Set.isEmpty (freeInferVars env.Subst r)
                        && (match IR.stripUnits r with IRTScalar _ -> true | _ -> false))
                    && (rs |> List.map IR.stripUnits |> List.distinct |> List.length) = 1)
            let perLegJoin () : TypeResult<TypedExpr> =
                let op = match tKernel.Kind with TExprSection op -> op | _ -> OpAdd
                let rs = elems |> List.map env.Subst.Resolve
                let kernels = rs |> List.map (fun e -> mkTyped (TExprSection op) (mkFuncArrow [e; e] e))
                List.zip kernels rs
                |> List.fold (fun acc (k, e) -> acc |> Result.bind (fun () -> reduceKernelUnitCheck env k e)) (Ok ())
                |> Result.map (fun () ->
                    let seeds =
                        rs |> List.map (fun e ->
                            let et = match e with AnyPrimElem x -> x | _ -> ETFloat64
                            let lit =
                                match op, et with
                                | OpMul, (ETInt32 | ETInt64) -> LitInt 1L
                                | OpMul, _ -> LitFloat 1.0
                                | _, (ETInt32 | ETInt64) -> LitInt 0L
                                | _ -> LitFloat 0.0
                            // an unseeded `(+)` leg's seed carries the
                            // UNSEEDED mark (gap G74; TypedAst.markUnseeded)
                            let s = mkTyped (TExprLit lit) e
                            if op = OpAdd then markUnseeded s else s)
                    let tree =
                        // G233: a leaf the prelude forces keeps its name
                        match keptTreeLeaves leaves with
                        | first :: rest ->
                            rest |> List.fold (fun acc leaf ->
                                mkTyped (TExprFusion (acc, leaf)) (IRTTuple [acc.Type; leaf.Type])) first
                        | [] -> tArr
                    let kCarrier = mkTyped (TExprTuple kernels) (IRTTuple (kernels |> List.map (_.Type)))
                    let sCarrier = mkTyped (TExprTuple seeds) (IRTTuple (seeds |> List.map (_.Type)))
                    mkTyped (TExprReduce (tree, kCarrier, Some sCarrier)) (IRTTuple rs))
            let legsUnified =
                elems.Tail
                |> List.fold (fun acc e -> acc |> Result.bind (fun () -> unify env.Subst e elem0)) (Ok ())
            match legsUnified with
            | Error _ when unitOnlyLegs () -> perLegJoin ()
            | _ ->
            legsUnified
            |> Result.bind (fun () ->
            // Fold-kernel params and init share the leaves' element type
            // (same unification the array form performs).
            (match env.Subst.Resolve(tKernel.Type) with
             | FuncElem (paramTys, _) ->
                paramTys |> List.fold (fun acc pTy ->
                    acc |> Result.bind (fun () -> unify env.Subst pTy elem0)) (Ok ())
             | _ -> Ok ())
            // (F2: projections of the now-known parameters, as the array form)
            |> Result.map (fun () -> settleOpenProjections env tKernel)
            |> Result.bind (fun () ->
            (match tInitOpt with
             | Some tInit -> unify env.Subst tInit.Type elem0
             | None -> Ok ())
            |> Result.bind (fun () ->
            // Seed: user's init, else the section's identity. A fused nest
            // cannot seed-with-first (no single first cell across a
            // multi-dim or multi-leaf nest), so everything else is an error.
            let seed : Result<TypedExpr, TypeError> =
                match tInitOpt with
                | Some tInit -> Ok tInit
                | None ->
                    let et = match env.Subst.Resolve elem0 with AnyPrimElem e -> e | _ -> ETFloat64
                    (match tKernel.Kind with
                     | TExprSection OpAdd ->
                        // An element still OPEN here is a generic body's
                        // (`function f(v) = reduce(method_for(v) <@> k, (+))`):
                        // a typed zero, which monomorphization makes each
                        // instance's own zero (resolveTypedZerosModule) -- a
                        // `0.0` seed was every instance's, so the Int64 one
                        // folded into a Float64 accumulator.
                        let lit =
                            if isOpenElemType env elem0 then TExprZero
                            else match et with ETInt32 | ETInt64 -> TExprLit (LitInt 0L) | _ -> TExprLit (LitFloat 0.0)
                        // The fold is UNSEEDED: its value is the left fold of
                        // its cells. The base seed above is what every
                        // licensed route keeps; the mark lets Lowering give a
                        // STRICT route the exact identity instead (gap G74;
                        // TypedAst.unseededSeedSpan, Lowering.lowerUnseededSeed).
                        Ok (markUnseeded (mkTyped lit elem0))
                     | TExprSection OpMul ->
                        let lit = match et with ETInt32 | ETInt64 -> TExprLit (LitInt 1L) | _ -> TExprLit (LitFloat 1.0)
                        Ok (mkTyped lit elem0)
                     | TExprSection _ ->
                        Error (ReductionFormErr "reduce() over a deferred computation requires an explicit init for this kernel (3-arg form `reduce(c, op, init)`) -- only (+) and (*) carry implicit identities")
                     | _ ->
                        Error (ReductionFormErr "reduce() over a deferred computation requires an explicit init for a lambda kernel (3-arg form `reduce(c, op, init)`) -- a fused fold cannot seed from its first element"))
            seed |> Result.map (fun tSeed ->
            let rebuilt =
                match leaves with
                | [_] when keepSpine && not anonymousUnwrap ->
                    // G235 (a): the spine's NAME, marked on the fold node
                    // itself for Lowering (TypedAst.markKeptSpine)
                    (match tArr.Kind with
                     | TExprVar (n, _, _) ->
                        Blade.Effects.Decisions.record
                            { Blade.Effects.Rule = "deferred-capture-force"; Version = 1; Span = tArr.Span; Subject = n
                              Outcome = Blade.Effects.Applied
                              Evidence = [ $"a capture of the fold kernel or a cell read of the seed forces the spine `{n}` under its own name before the traversal: the fold reads that snapshot, not a fresh walk of its producer (G235, G248)" ] }
                     | _ -> ())
                    markKeptSpine tArr
                | [one] -> one
                | _ :: _ ->
                    // G233: a leaf the prelude forces keeps its name
                    (match keptTreeLeaves leaves with
                     | first :: rest ->
                         rest |> List.fold (fun acc leaf ->
                             mkTyped (TExprFusion (acc, leaf)) (IRTTuple [acc.Type; leaf.Type])) first
                     | [] -> tArr)
                | [] -> tArr
            let resultType =
                match leaves with
                | [_] -> elem0
                | _ :: rest -> rest |> List.fold (fun acc _ -> IRTTuple [acc; elem0]) elem0
                | [] -> elem0
            mkTyped (TExprReduce (rebuilt, tKernel, Some tSeed)) resultType)))))))
        match built with
        | Error _ when anonymousUnwrap -> None
        | _ -> Some built

/// The operand's `<@>` leaves (through `<&!>` trees and an anonymous
/// `|> compute`) are folded, never stored, so the WHOLE reduce inference runs
/// inside `FoldOperandApplies.within` their spans: the BL4010 storage
/// suggestion stands down for exactly those applies (pinning `where comm` on a
/// fold leaf is the BL3026 compact-fold refusal, or the `axes = n` rank
/// refusal, not a storage choice). The whole body and not just the first
/// operand inference, because the operand is inferred again on the main path
/// and inside the rank-k desugar's synthesized block -- same Expr, same spans.
and inferReduce (ie: InferEntries) (env: TypeEnv) (array: Expr) (kernel: Expr) (init: Expr option) (axes: Expr option) : TypeResult<TypedExpr> =
    let rec foldLeafSpans (e: Expr) : Span list =
        match e.Kind with
        | ExprCompute inner -> foldLeafSpans inner
        | ExprBinOp (_, OpFusion, l, r) -> foldLeafSpans l @ foldLeafSpans r
        | ExprBinOp (_, OpApply, l, r) -> [ l.Span; r.Span ]
        | _ -> []
    FoldOperandApplies.within (foldLeafSpans array) (fun () -> inferReduceCore ie env array kernel init axes)

and inferReduceCore ({ InferEntries.InferExpr = inferExpr; InferEntries.InferReduceGeneral = inferReduceGeneral } as ie) (env: TypeEnv) (array: Expr) (kernel: Expr) (init: Expr option) (axes: Expr option) : TypeResult<TypedExpr> =
    // ---- Axis count (`axes = n`, default 1) --------------------------------
    // `reduce` folds the innermost `n` axes (each a LEFT fold in ascending
    // storage order): rank k in, rank k-n
    // out, and n = k is the full fold to a scalar. `n` must be an integer
    // LITERAL -- the result RANK is k - n, a static property of the type, so a
    // symbolic count would make the result type depend on a runtime value.
    let axisCountR : Result<int option, TypeError> =
        match axes with
        | None -> Ok None
        | Some ae ->
            match ae.Kind with
            | ExprKind.ExprLit (LitInt n) -> Ok (Some (int n))
            | _ ->
                Error (ReductionFormErr "reduce: `axes = n` requires an integer literal -- the result rank is rank(A) - n, so a symbolic axis count (a variable, a parameter, or an expression) has no static result type (deferred)")
    axisCountR |> Result.bind (fun axisCountOpt ->
    // The operand, inferred ONCE for this whole function. Everything below
    // that needs the operand's shape -- the rank probe, the element type, the
    // gates, the row annotation -- reads this, and `rankKDesugar` takes it as
    // an argument instead of re-inferring. Inference is not free of visible
    // side effects: each run mints fresh ids, and an extra run SHIFTS every
    // generated `__lambda_N` / `__vN` downstream. Sharing one keeps the
    // emitted C++ of an unchanged program byte-identical, which is the point.
    let tArrCache = lazy (inferExpr env array)
    // ROUND 6: a literal-only SEED takes the operand element's width on every
    // fold path (the desugars below re-bind it as an accumulator): it is
    // ascribed at the element when that is a concrete numeric of its class.
    let init =
        match init with
        | Some ie when (surfaceLiteralClass ie).IsSome ->
            (match tArrCache.Force() with
             | Ok tArr ->
                 (match IR.stripUnits (env.Subst.Resolve tArr.Type) with
                  | ArrayElem a ->
                      (match IR.stripUnits (env.Subst.Resolve a.ElemType) with
                       | IRTScalar et -> Some (ascribeLiteralOnly et ie)
                       | _ -> init)
                  | _ -> init)
             | Error _ -> init)
        | _ -> init
    // THE NUMBERS-ONLY RULE (formalism 2.4) for a builtin ARITHMETIC section
    // kernel: `reduce(m, (+))` over a Bool (or other non-numeric) array adds
    // values that are not numbers, as `m(0) + m(1)` would -- refused here
    // (`(+)` over Strings joins them and stays). A generic element is judged
    // per instance (GONumericOperand). Read lazily, below, through
    // `operandElemOf`, which is defined after the operand walks.
    let sectionOp =
        match kernel.Kind with
        | ExprKind.ExprSection ((OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret | OpAnd | OpOr) as sop) -> Some sop
        | _ -> None
    // Statically-known rank of the fold's OPERAND: an array's index-type
    // count, or -- for the fused reduction terminal -- the deferred
    // computation's output rank (a fused fold walks the same cell grid).
    // None whenever the rank is not yet pinned (an unconstrained inference
    // variable, e.g. `lambda(g) -> reduce(g, (+))`); with the default n = 1
    // that leaves every existing path byte-identical.
    let operandRank () : int option =
        match tArrCache.Force() with
        | Error _ -> None
        | Ok t ->
            let rankOfOut (ty: IRType) =
                match env.Subst.Resolve ty with
                | ArrayElem at -> Some at.IndexTypes.Length
                // A `T^k` PARAMETER is rank-pinned but shapeless: the caret
                // records `k` in the substitution's arityConstraints, and the
                // type stays a bare inference var until some demand mints the
                // array. Reading only the resolved type left `rk = None` for
                // every abstract operand, so `reduce(x, (+))` on a `T^2` param
                // skipped BOTH rank-aware routes (leading-axis and partial
                // fold) and fell to the tail synthesis, which minted a rank-1
                // array, failed to unify against the `^2` pin, dropped that
                // failure on the floor (`|> ignore`) and reported "reduce()
                // requires an array as first argument" -- the rank-peeling
                // body of a rank-recursive function could not be written at
                // all. The pin is exactly the static rank the routing wants.
                //
                // The declared pin only, never `GetRankLowerBound`: a deduced
                // lower bound is evidence for `unify` to CHECK a synthesis
                // against, not a shape to route on. See the long note at
                // `requireArrayArgMinRank`.
                | IRTInfer vid ->
                    (match env.Subst.GetArityConstraint vid with
                     | Some k when k > 0 -> Some k
                     | _ -> None)
                | _ -> None
            match rankOfOut t.Type with
            | Some r -> Some r
            | None ->
                // Deferred computation: a SINGLE apply leaf answers with its
                // output rank, so `reduce(A <@> f, (+))` partial-folds exactly
                // like the array it would have materialized into.
                //
                // An `<&!>` fusion TREE deliberately answers None -- it has no
                // partial form to give. Its leaves may have DIFFERENT ranks
                // (the staggered-arity nest accumulates each leaf at its own
                // depth) and its result is a TUPLE of scalars, so "fold the
                // innermost axis" names no single axis and no array shape.
                // The tree terminal stays the full fold it has always been.
                match (resolveTypedExpr env t).Kind with
                | TExprApply info when not info.IsComposeApply -> rankOfOut info.OutputType
                | _ -> None
    // The operand's ELEMENT type, by the same walk. Needed at this level for
    // the units gate below, which cannot fire from inside the synthesized
    // kernel body.
    let operandElem () : IRType option =
        match tArrCache.Force() with
        | Error _ -> None
        | Ok t ->
            let elemOfOut (ty: IRType) =
                match env.Subst.Resolve ty with
                | ArrayElem at -> Some at.ElemType
                | _ -> None
            match elemOfOut t.Type with
            | Some e -> Some e
            | None ->
                // Single apply leaf only, matching `operandRank` -- a fusion
                // tree never reaches the partial path, so it needs no element.
                let leafElem (te: TypedExpr) =
                    match (resolveTypedExpr env te).Kind with
                    | TExprApply info when not info.IsComposeApply -> elemOfOut info.OutputType
                    | _ -> None
                leafElem t
    let sectionRefusal =
        match sectionOp with
        | None -> None
        | Some sop ->
            match operandElem () with
            | None -> None
            | Some elem ->
                let opName =
                    match sop with
                    | OpAdd -> "+" | OpSub -> "-" | OpMul -> "*" | OpDiv -> "/" | OpMod -> "%" | _ -> "^"
                // The logical mirror: `reduce(xs, (&&))` folds Bools only.
                if sop = OpAnd || sop = OpOr then
                    match nonBoolOperandOf env elem with
                    | Some what ->
                        let nm = if sop = OpAnd then "&&" else "||"
                        Some (TypeMismatchErr ($"a logical operator takes Bools only: reduce() folds `({nm})` over cells that are "
                                               + $"{what}, not Bools, and Blade converts nothing implicitly -- compare them explicitly "
                                               + "(`method_for(xs) <@> lambda(v) -> v != 0`)."))
                    | None -> None
                else
                match nonNumericOperandOf env elem with
                | Some n when not (sop = OpAdd && n.NKind = "String") ->
                    let advice =
                        if n.NKind = "Bool" then
                            " Convert the cells explicitly with a numeric cast -- `reduce(Int64(m), (+))` counts the true cells."
                        else ""
                    let noun = n.NWhat.Substring(n.NWhat.IndexOf ' ' + 1)
                    Some (TypeMismatchErr ($"arithmetic is defined on numbers only: reduce() folds `({opName})` over {noun} "
                                           + $"cells, which are not numbers, and Blade converts nothing implicitly.{advice}"))
                | Some _ -> None
                | None ->
                    (match env.CurrentGenericObligations, IR.stripUnits (env.Subst.Resolve elem) with
                     | Some acc, IRTInfer v when env.Subst.IsPolymorphicId v && (env.Subst.GetLiteralDefault v).IsNone ->
                         acc.Add(GONumericOperand (v, $"({opName})", sop = OpAdd))
                     | _ -> ())
                    None
    // R3 (D2): an arithmetic section folded over values of an INTEGER EnumIdx
    // folds their KEYS -- `reduce(codes, (+))` is `reduce(method_for(codes) <@>
    // lambda(c) -> Int64(c), (+))`, the fused terminal (nothing stored), its
    // seed and axes unchanged. Folding the operand as written would add the
    // positions that represent the keys (it was refused, BL4003).
    // (A section bound to a name, `let op = (+)`, is the same section.)
    let keyedSectionOp =
        match sectionOp, kernel.Kind with
        | Some op, _ -> Some op
        | None, ExprKind.ExprVar v ->
            (match lookupVar v env with
             | Some { TypedValue = Some { Kind = TExprSection op } } -> Some op
             | _ -> None)
        | _ -> None
    let keyedSectionFold =
        match keyedSectionOp with
        | Some (OpAdd | OpSub | OpMul | OpDiv | OpMod | OpCaret) when (lookupVar "Int64" env).IsNone ->
            (match operandElem () with
             | Some elem -> intEnumValueType env elem
             | None -> false)
        | _ -> false
    // (A named section folds the stored keys: a deferred operand would need
    // an explicit seed for a kernel the fold does not see as a section.)
    if keyedSectionFold then
        // The fused terminal seeds (+) / (*) with their identity, and folds only
        // DENSE operands; every other operand folds the stored keys.
        let kind = match tArrCache.Force() with Ok t -> enumKeyStorageKind env t.Type | Error _ -> ""
        let deferredOk =
            match sectionOp with
            | Some (OpAdd | OpMul) -> kind = "dense"
            | _ -> false
        // A sparse array of keys (or any other storage) has no key array both
        // lanes build: refused (lead review of R3).
        if kind = "sparse" || kind = "other" then
            if array.Span.StartLine > 0 then setCurrentExprSpan array.Span
            Error (enumKeyStorageRefusal "A fold of an arithmetic section" kind)
        else
        inferReduceCore ie env (if deferredOk then enumKeyMapAst env array else enumKeyCastAst array) kernel init axes
    else
    match sectionRefusal with
    | Some e -> Error e
    | None ->
    let rk = operandRank ()
    // ---- FULL fold over a compact group: `reduce(S, op[, init], axes = r)` --
    // S's index is ONE symmetric/antisymmetric record of logical rank r, so
    // `axes = r` folds every logical axis: the FULL-DOMAIN fold over the
    // logical (mirrored) cells, docs/plans/plan-compact-sym-folds.md section
    // 4.3's recommended meaning -- the value `reduce(decompact(S, 0), op[,
    // init], axes = r)` already had. (A PARTIAL fold over a compact group
    // stays the BL3026 refusal: which half of the group it folds is the open
    // question.) For the builtin `(+)` it is rewritten into the spelling that
    // names the logical traversal -- r nested folds over `range<I>` reading
    // `S(i_1, .., i_r)`, the seed on the outermost fold only (the dense
    // `axes = r` fold seeds once too) -- which the licence turns into the
    // orbit-class fold (Blade.OrbitFold) in both back ends. That is the dense
    // fold's multiset of terms in a row-then-total association (the dense
    // full fold is one flat left fold), so the two spellings agree up to
    // association: exactly for integers, to rounding for floats. The nested
    // spelling needs a NAMED array and a NAMED index (`range<I>`); any other
    // operand, and every other kernel (whose flat dense fold is not a
    // regrouping of a nested one: `(-)`), takes the decompact form itself.
    let compactFull =
        match axisCountOpt, tArrCache.Force() with
        | Some n, Ok t when n >= 2 ->
            (match env.Subst.Resolve t.Type with
             | ArrayElem at when not at.IsVirtual ->
                 (match at.IndexTypes with
                  | [ ix ] when ix.Rank = n && (ix.Symmetry = SymSymmetric || ix.Symmetry = SymAntisymmetric) -> Some (ix, n)
                  | _ -> None)
             | _ -> None)
        | _ -> None
    match compactFull with
    | Some (ix, n) ->
        let span = array.Span
        let mk k = mkExpr span k
        let viaDecompact () =
            let dense = List.fold (fun (e: Expr) d -> mk (ExprDecompact (e, d))) array [ 0 .. n - 2 ]
            inferReduceCore ie env dense kernel init axes
        match array.Kind, ix.Tag, kernel.Kind with
        | ExprKind.ExprVar _, Some tag, ExprKind.ExprSection OpAdd ->
            let vars = [ for t in 1 .. n -> $"__orbit_i{t}_{span.StartLine}_{span.StartCol}" ]
            let read = mk (ExprApp (array, vars |> List.map (fun v -> mk (ExprVar v))))
            let nested =
                List.foldBack (fun (t: int, v: string) (body: Expr) ->
                    let loop = mk (ExprMethodFor [ mk (ExprRange [ TyNamed (tag, []) ]) ])
                    let lam = mk (ExprLambda ([ { Name = v; Type = None; Default = None; NameSpan = noSpan } ], None, body))
                    let mapped = mk (ExprBinOp (Elementwise, OpApply, loop, lam))
                    mk (ExprReduce (mapped, kernel, (if t = 1 then init else None), None)))
                    (List.indexed vars |> List.map (fun (i, v) -> (i + 1, v))) read
            (match inferExpr env nested with
             | Ok te -> Ok te
             | Error _ -> viaDecompact ())
        | _ -> viaDecompact ()
    | None ->
    let axisRangeErr =
        match axisCountOpt with
        | Some n when n < 1 ->
            Some (ReductionFormErr $"reduce: `axes = {n}` is out of range -- the axis count must be at least 1 (a zero-axis fold is the identity on A, so it has no fold to perform)")
        | Some n ->
            (match rk with
             | Some r when n > r ->
                Some (ReductionFormErr $"reduce: `axes = {n}` exceeds the operand's rank {r} -- the axis count must satisfy 1 <= n <= rank(A), and n = {r} is the full fold to a scalar")
             | _ -> None)
        | None -> None
    match axisRangeErr with
    | Some e -> Error e
    | None ->
    let partialFold (r: int) (n: int) : TypeResult<TypedExpr> option = reducePartialFold ie env array operandElem kernel tArrCache init r n
    let leadingAxisFold (r: int) : TypeResult<TypedExpr> option = reduceLeadingAxisFold ie env kernel axisCountOpt tArrCache init array r
    let leadingResult =
        match rk with
        | Some r when r >= 2 -> leadingAxisFold r
        | _ -> None
    match leadingResult with
    | Some result -> result
    | None ->
    let partialResult =
        match rk with
        | Some r when r >= 2 && (defaultArg axisCountOpt 1) < r -> partialFold r (defaultArg axisCountOpt 1)
        | _ -> None
    match partialResult with
    | Some result -> result
    | None ->
    let rankKDesugar () = reduceRankKDesugar ie env tArrCache kernel array axes init ()
    match rankKDesugar () with
    | Some result -> result
    | None ->
    (match inferReduceGeneral env array kernel init axisCountOpt with Ok t -> Ok t | Error e -> Error e))

// ---- PARTIAL fold (n < rank): the row-mode apply --------------------
// A rank-k operand folded over its innermost n axes is exactly the
// documented row-wise idiom one level up:
//
//     (method_for(A) <@> lambda(row) -> reduce(row, op[, init])) |> compute
//
// so the partial form is REWRITTEN into it rather than given its own IR
// node: no new node, no new loop, no new codegen. Everything the fold owes
// -- the seed / empty-group rule, the compact-storage refusal on the
// folded axis, the `omp` fold licence, the interpreter and codegen paths
// -- is inherited from the `reduce` now sitting in the kernel body, and
// the outer iteration is the proven `<@>` former. (The two checks the body
// position CANNOT perform, because they read a type that is not pinned
// until after the body is typed, are lifted to this level below: units and
// statically-empty groups.) For 1 < n < rank the body is the row's own
// FULL fold and the row parameter carries its slice type -- the hosvd
// corpus's hand-written shape. n = rank skips all of this and keeps the
// existing full-fold nest byte-identical.
and reducePartialFold ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (array: Expr) (operandElem: unit -> IRType option) (kernel: Expr) (tArrCache: Lazy<TypeResult<TypedExpr>>) (init: Expr option) (r: int) (n: int) : TypeResult<TypedExpr> option =
    let span = array.Span
    let synAt k = mkExpr span k
    // UNITS. The endomorphism check the rank-1 fold performs cannot fire
    // from inside the synthesized kernel body: the row parameter is still
    // an unresolved inference variable while that body is typed (its
    // element type is pinned later, at the apply's perRowType
    // unification), so `IR.getUnits` sees nothing there and a `*` fold
    // over `Float64<meter>` would be waved through and then MISLABELLED
    // meter (it is meter^2). Run the identical check HERE, where the
    // operand's element type is known -- same function, same message.
    let unitGate : TypeResult<unit> =
        match operandElem () with
        | Some et ->
            (match inferExpr env kernel with
             | Ok tk -> reduceKernelUnitCheck env tk et
             | Error _ -> Ok ())
        | None -> Ok ()
    // COMPACT FOLDED AXES. The rank-1 form refuses to fold a
    // symmetric/antisymmetric/Hermitian record (canonical cells and
    // logical mirrored cells give different answers). The partial form
    // inherits the REFUSAL through the row parameter -- but as an
    // unification failure about index slots, since the fresh rank-1 row it
    // mints cannot match a rank-k compact group. Say the real reason here
    // instead, in the rank-1 form's own words.
    let compactGate : TypeResult<unit> =
        match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
        | Ok (ArrayElem at) when at.IndexTypes.Length >= n ->
            if at.IndexTypes |> List.skip (at.IndexTypes.Length - n)
                             |> List.exists (fun ix -> ix.Symmetry <> SymNone) then
                Error (CompactStorageErr "reduce() over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical cells and folding the logical (mirrored) cells differ. decompact(A, d) first for the logical fold.")
            else Ok ()
        | _ -> Ok ()
    // EMPTY GROUPS. Same reason as the units gate: the rank-1 form's
    // statically-empty rejection reads the ROW's extent, which is not yet
    // pinned inside the synthesized body, so a `Idx<0>` folded axis would
    // slip through to a runtime panic. Check the FOLDED (innermost n)
    // axes here. With an init the empty fold is defined (it is init), so
    // this fires only for the seedless form -- the rank-1 rule verbatim.
    let emptyGate : TypeResult<unit> =
        if init.IsSome then Ok ()
        else
            match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
            | Ok (ArrayElem at) when at.IndexTypes.Length >= n ->
                at.IndexTypes
                |> List.skip (at.IndexTypes.Length - n)
                |> List.tryPick (fun ix ->
                    match tryEvalIntIR ix.Extent with
                    | Some e when e <= 0L -> Some e
                    | _ -> None)
                |> function
                   | Some e -> Error (ReduceEmptyArray e)
                   | None -> Ok ()
            | _ -> Ok ()
    match unitGate |> Result.bind (fun () -> compactGate) |> Result.bind (fun () -> emptyGate) with
    | Error e -> Some (Error e)
    | Ok () ->
    // ---- The deferred OUTER PRODUCT under the default partial fold ------
    // `reduce(method_for(A1, .., Ar) <@> lambda(p1, .., pr) -> body, op)`
    // with the default `axes = 1` folds the innermost axis of a rank-r
    // product NOBODY else reads -- and the route below still built it:
    // `__pfsrc` forced the whole M x N (x ..) pool, then the row map
    // folded each row of it (docs/plans/structural/03 section 1.3: two
    // full passes over a pool the answer never needs). The rewrite is the
    // per-row shape the idiom package documents (examples/10):
    //
    //   (method_for(A1) <@> lambda(p1) ->
    //        reduce(method_for(A2, .., Ar) <@> lambda(p2, .., pr) -> body,
    //               op[, init])) |> compute
    //
    // an outer apply whose row kernel is the FUSED terminal over the
    // remaining operands with `p1` captured by value -- routes that exist,
    // no new IR, emitter or interpreter arm. Per row the fused fold is a
    // left fold from the identity/init in row-major order, which is the
    // row-mode fold's order too; the row mode seeds with the first cell
    // where a `(+)` seeds 0.0, so bits agree except the sign of an
    // all-negative-zero row (03 section 3.3 D). Admitted narrowly: NAMED
    // rank-1 plain dense scalar operands (each row kernel reads them by
    // name), a lambda kernel of exactly r params with no `where` clause
    // (a `comm` claim is the compact-output refusal's business, and a
    // parallel licence would be dropped silently), a fold kernel that is
    // a `(+)`/`(*)` section or carries its init (the fused terminal
    // cannot seed from its first element), and no licence on the fold.
    // A NAMED operand (`let S = ..; reduce(S, (+))`) stays on the route
    // below: it may be printed or read elsewhere. TWO operands only: for
    // a deeper product the row kernel would answer an ARRAY (the partial
    // fold of a rank-(r-1) slice), the rank-raising row map, whose result
    // index records do not yet agree with the row-mode route's (measured:
    // `t - tr` between the two spellings fails BL3027 on the second
    // axis); a rank-3 product keeps the materialized route.
    let outerProductRewrite () : TypeResult<TypedExpr> option =
        if n <> 1 then None else
        match array.Kind with
        | ExprBinOp (Elementwise, OpApply,
                     { Kind = ExprMethodFor srcs },
                     { Kind = ExprLambda (parms, None, body) })
                when srcs.Length = 2 && parms.Length = srcs.Length
                     && srcs |> List.forall (fun s -> s.Kind.IsExprVar)
                     && parms |> List.forall (fun p -> p.Default.IsNone) ->
            let operandOk =
                match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
                | Ok (ArrayElem at) ->
                    at.IndexTypes.Length = r && r = srcs.Length
                    && at.IndexTypes |> List.forall (fun ix ->
                           ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1)
                    && (env.Subst.Resolve at.ElemType).IsIRTScalar
                | _ -> false
            let foldLicensed =
                match kernel.Kind with
                | ExprLambda (_, Some wc, _) -> not (List.isEmpty wc.Parallel)
                | ExprVar fn ->
                    (match lookupVar fn env |> Option.map (fun vi -> env.FuncParallel.TryGetValue vi.VarId) with
                     | Some (true, (_, s)) -> not (List.isEmpty s)
                     | _ -> false)
                | _ -> false
            let foldSeeded =
                match kernel.Kind with
                | ExprSection (OpAdd | OpMul) -> true
                | _ -> init.IsSome
            if not (operandOk && foldSeeded) || foldLicensed then None
            else
                let inner =
                    synAt (ExprBinOp (Elementwise, OpApply,
                                      synAt (ExprMethodFor (List.tail srcs)),
                                      synAt (ExprLambda (List.tail parms, None, body))))
                let rowBody = synAt (ExprReduce (inner, kernel, init, None))
                let outer = synAt (ExprLambda ([List.head parms], None, rowBody))
                Some (inferExpr env (synAt (ExprCompute (
                    synAt (ExprBinOp (Elementwise, OpApply, synAt (ExprMethodFor [List.head srcs]), outer))))))
        | _ -> None
    match outerProductRewrite () with
    | Some rewritten -> Some rewritten
    | None ->
    let uid = env.Builder.FreshId()
    let rowName = $"__pfrow{uid}"
    let rowVar = synAt (ExprVar rowName)
    // The former's source. The row-mode apply hands out ROW VIEWS of it,
    // and codegen names those views after the source buffer -- so the
    // source has to BE a named buffer.
    //
    // A bare variable already is one: it goes in directly, and binding it
    // to a synthetic `let` first would emit a pointless full `std::copy_n`
    // of the operand (measured in the emitted C++) for a source the apply
    // reads exactly once. This is the hot shape (`reduce(A, (+))`).
    //
    // Anything else -- a deferred `A <@> f`, a bracketed product, an array
    // literal -- has no name, and codegen emits a reference to an
    // undeclared buffer ("'arr0' was not declared in this scope",
    // measured). Those get the `let`, which also forces a deferred operand
    // exactly once: the fused terminal has no partial form (it collapses a
    // whole nest into scalars), so the cells must exist before rows can be
    // handed out.
    let srcIsNamed = array.Kind.IsExprVar
    let srcName = $"__pfsrc{uid}"
    // Kernel body + row-parameter annotation.
    //
    // n = 1: `reduce(row, op[, init])`, the rank-1 fold. A known numeric
    // element is carried by a rank-only row annotation, so a literal seed
    // takes that width before the apply supplies the row's full type.
    // Other rows keep the inner reduce's inferred rank-1 shape.
    //
    // n > 1: the body is the row's own FULL fold, `reduce(row, op[,
    // init], axes = n)`, and the row parameter MUST carry its type. The
    // full fold needs static extents, and inside an unannotated kernel
    // body the row is still an inference variable when that body is
    // typed -- the extents arrive later, at perRowType, far too late.
    // (Measured: the unannotated spelling types the row as a SCALAR and
    // emits `row(i, j)` as a call, which does not compile.) With the
    // annotation it is exactly the hand-written hosvd shape
    // `lambda(face: Array<Float64 like Idx<3>, Idx<3>>) -> reduce(face,
    // (+), axes = 2)`, which is proven. The annotation is only
    // constructible for a dense, statically-sized, untagged, unitless
    // scalar slice; anything else is refused and pointed at the manual
    // spelling, where the author can write the type the printer shows.
    let rowAnnotR : Result<TypeExpr option, TypeError> =
        if n = 1 then
            // Preserve the known cell width while the row body is checked.
            // Otherwise its literal seed meets an open row, takes the wide
            // default, and rejects a Float32 source at the later apply.
            // A rank-only annotation keeps extents and index tags open.
            let annot =
                match operandElem () with
                | Some (IRTScalar et) ->
                    typeExprOfElem et
                    |> Option.map (fun te -> TyAbstractArray (te, synAt (ExprLit (LitInt 1L)), None))
                | _ -> None
            Ok annot
        else
            let bail () =
                Error (ReductionFormErr $"reduce: `axes = {n}` over a rank-{r} operand needs the folded slice to be dense (plain, untagged, non-compact) with static extents and a unitless scalar element -- write the row-wise form explicitly instead: `method_for(A) <@> lambda(row: <the slice type>) -> reduce(row, op, axes = {n}) |> compute`")
            match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
            | Ok (ArrayElem at) when at.IndexTypes.Length = r ->
                let innerDims = at.IndexTypes |> List.skip (r - n)
                let elemTy = env.Subst.Resolve at.ElemType
                let elemTE =
                    match elemTy, IR.getUnits elemTy with
                    | _, Some _ -> None                       // units cannot be re-rendered here
                    | IRTScalar ETFloat64, _ -> Some TyFloat64
                    | IRTScalar ETFloat32, _ -> Some TyFloat32
                    | IRTScalar ETInt64, _ -> Some TyInt64
                    | IRTScalar ETInt32, _ -> Some TyInt32
                    | IRTScalar ETComplex128, _ -> Some TyComplex128
                    | IRTScalar ETComplex64, _ -> Some TyComplex64
                    | IRTScalar ETBool, _ -> Some TyBool
                    | _ -> None
                let slotTEs =
                    innerDims |> List.map (fun ix ->
                        if ix.IxKind <> IxKPlain || ix.Symmetry <> SymNone
                           || ix.Tag.IsSome || ix.Rank <> 1 then None
                        else
                            match tryEvalIntIR ix.Extent with
                            | Some e when e > 0L -> Some (TyIdx (synAt (ExprLit (LitInt e))))
                            | _ -> None)
                match elemTE with
                | Some et when slotTEs |> List.forall Option.isSome ->
                    Ok (Some (TyArray (et, slotTEs |> List.map Option.get)))
                | _ -> bail ()
            | _ -> bail ()
    match rowAnnotR with
    | Error e -> Some (Error e)
    | Ok rowAnnot ->
        let body =
            if n = 1 then synAt (ExprReduce (rowVar, kernel, init, None))
            else synAt (ExprReduce (rowVar, kernel, init, Some (synAt (ExprLit (LitInt (int64 n))))))
        let lam = synAt (ExprLambda ([{ Name = rowName; Type = rowAnnot; Default = None; NameSpan = span }], None, body))
        let appliedOver (src: Expr) =
            synAt (ExprCompute (synAt (ExprBinOp (Elementwise, OpApply,
                                                  synAt (ExprMethodFor [src]), lam))))
        if srcIsNamed then Some (inferExpr env (appliedOver array))
        else
            Some (inferExpr env (synAt (ExprBlock (
                    [ StmtLet { Mutability = BindLet
                                Pattern = mkPat span (PatVar srcName)
                                Type = None
                                Value = array } ],
                    Some (appliedOver (synAt (ExprVar srcName)))))))

// ---- LEADING-AXIS fold (array-valued kernel) ------------------------
// `reduce(G, lambda(a: T^{r-1}, b: T^{r-1}) -> a + b)` over a rank-r
// operand folds the LEADING axis: the fold's elements are the rank-(r-1)
// slices G(0), G(1), ..., and the result is ONE rank-(r-1) array. The
// complement of the innermost-axis default (a scalar kernel folds cells;
// an (r-1)-slice kernel can only be folding leading-axis slices), and the
// spelling examples/lswosa.blade's family_spectra tracks as a feature: it
// replaces that function's (grid + transpose + per-column innermost fold)
// with one pass and no transposed intermediate.
//
// REWRITTEN into existing machinery, like every other reduce form: the
// internal loop statement rankKDesugar already synthesizes (StmtForIn is
// refused for USERS in the parser -- BL1003/BL1999 -- but fully supported
// downstream, with runtime bounds), plus whole-array assignment through a
// named intermediate (the mut copy-in-place path; measured working inside
// function bodies). Fold order and seed match the rank-1 scalar fold one
// slice up (measured: the seedless scalar fold is a LEFT fold seeded with
// the FIRST element -- reduce([10,1,100], (-)) = (10-1)-100 = -91):
//
//     let mut acc = <copy of G(0)>          // of `init`, when given
//     for j in 1..N { let nxt = kernel(acc, G(j)) |> compute; acc = nxt }
//     acc
//
// The identity-map copy at the seed keeps `acc` a buffer the loop owns:
// seeding with the raw `G(0)` view would alias the source row, and the
// first assignment through the copy-in-place path would overwrite source
// cells mid-read. The per-step `|> compute` materializes into a fresh
// buffer BEFORE the assign copies it into `acc`, so `kernel(acc, ...)`
// reads a stable accumulator. Type stability of the accumulator (the
// endomorphism the rank-1 unit gate checks) is enforced by the loop
// itself: the assignment unifies `acc`'s type with the kernel result, so
// a unit- or type-growing kernel fails to type right here.
//
// DETECTION IS SYNTACTIC -- the lambda's parameter annotations, or a
// named function's declared signature read through lookupVar + Resolve,
// never an inference run -- so every existing path mints exactly the ids
// it always did and stays byte-identical. The arm activates only on
// shapes that previously could not compile at all (an array-arity fold
// kernel died in every route, unifying a rank-k parameter against a
// scalar cell), so it strictly widens the language.
and reduceLeadingAxisFold ({ InferEntries.InferExpr = inferExpr }) (env: TypeEnv) (kernel: Expr) (axisCountOpt: int option) (tArrCache: Lazy<TypeResult<TypedExpr>>) (init: Expr option) (array: Expr) (r: int) : TypeResult<TypedExpr> option =
    // The kernel's slice arity, read WITHOUT inference. None = not an
    // array-valued fold kernel (or not knowably one): fall through to the
    // existing routes and their existing diagnostics.
    let annotRank (t: TypeExpr option) : int option =
        match t with
        | Some (TyVar (_, Some k)) when k >= 1 -> Some k
        | Some (TyAbstractArray (_, rankE, _)) ->
            (match rankE.Kind with
             | ExprKind.ExprLit (LitInt k) when k >= 1L -> Some (int k)
             | _ -> None)
        | Some (TyArray (_, idxs)) when not idxs.IsEmpty -> Some idxs.Length
        | _ -> None
    // LAMBDA KERNELS ONLY, deliberately. A NAMED function's `T^1` params
    // are silently vacuous on the existing fold routes -- `reduce(G, f)`
    // for `function f(a: T^1, b: T^1) = a + b` over rank-2 G compiles
    // TODAY and returns ROW sums (the params specialize to scalars; the
    // dispatch-looseness family), measured on master. Routing that
    // spelling here would silently change a green program's answer from
    // row sums to column sums -- the exact verdict flip this arm's
    // syntactic detection exists to rule out. The lambda spelling has no
    // such history: every array-annotated fold-kernel lambda was an error
    // before this arm, so claiming it is pure widening.
    let kernelParallel : bool =
        match kernel.Kind with
        | ExprKind.ExprLambda (_, Some wc, _) -> not (List.isEmpty wc.Parallel)
        | _ -> false
    let sliceArity : int option =
        match kernel.Kind with
        | ExprKind.ExprLambda ([p1; p2], _, _) ->
            (match annotRank p1.Type, annotRank p2.Type with
             | Some k1, Some k2 when k1 = k2 -> Some k1
             | _ -> None)
        | _ -> None
    match sliceArity with
    | None -> None
    | Some q ->
        if axisCountOpt.IsSome then
            Some (Error (ReductionFormErr "reduce: an array-valued kernel folds the LEADING axis (its elements are whole slices), so `axes = n` -- which counts INNERMOST axes -- cannot combine with it. Drop the axes argument, or fold cells with a scalar kernel."))
        elif q <> r - 1 then
            Some (Error (ReductionFormErr $"reduce: this kernel combines rank-{q} slices, but a rank-{r} operand's leading-axis slices have rank {r - 1}. Only the leading-axis fold (slice rank = rank(A) - 1) is supported; peel or reshape for anything deeper."))
        elif kernelParallel then
            Some (Error (WhereClauseErr "reduce: the leading-axis fold is sequential by construction (each step consumes the previous accumulator), so a `where omp/cuda/mpi` clause on its kernel licenses nothing and would be dropped silently. Remove the clause; the per-step slice combine is an ordinary elementwise apply and may carry its own licence inside the kernel body."))
        else
        match tArrCache.Force() |> Result.map (fun t -> env.Subst.Resolve t.Type) with
        | Ok (ArrayElem at) when at.IndexTypes.Length = r ->
            let lead = at.IndexTypes.Head
            // The LEADING axis may be plain -- or GROUP-OUTER: a grid a
            // grouped apply returned (group axis x static kernel axes) is
            // rectangular storage whose member axis was consumed by the
            // kernel, so its slices are uniform. What it may NOT be is
            // anything whose slices differ in shape -- and that is
            // exactly "some TRAILING axis is not plain dense", checked
            // below, since a surviving ragged member/dep/compound slot
            // lives in the trailing positions.
            if lead.Symmetry <> SymNone then
                Some (Error (CompactStorageErr "reduce: leading-axis fold over compact symmetric/antisymmetric/Hermitian storage is not supported: folding the canonical slices and folding the logical (mirrored) slices differ. decompact(A, d) first for the logical fold."))
            elif lead.IxKind <> IxKPlain && lead.IxKind <> IxKGroupOuter then
                Some (Error (ReductionFormErr "reduce: leading-axis fold needs a plain dense (or group-outer) leading axis -- ragged, compound, or sparse leading slices have no uniform slice shape to combine. Fold each row to a scalar instead, or restructure."))
            elif at.IndexTypes.Tail |> List.exists (fun ix ->
                    ix.IxKind <> IxKPlain || ix.Symmetry <> SymNone || ix.Rank <> 1) then
                Some (Error (ReductionFormErr "reduce: leading-axis fold needs plain dense trailing axes (the slices being combined must share one rectangular shape) -- ragged, grouped, compact, or multi-rank trailing axes do not. Fold each row to a scalar instead, or restructure."))
            else
            let staticLead = tryEvalIntIR lead.Extent
            match staticLead with
            | Some n when n <= 0L && init.IsNone -> Some (Error (ReduceEmptyArray n))
            | _ ->
            let uid = env.Builder.FreshId()
            let span = array.Span
            let synAt k = mkExpr span k
            let srcIsNamed = array.Kind.IsExprVar
            let srcName = $"__lasrc{uid}"
            let srcVar = if srcIsNamed then array else synAt (ExprVar srcName)
            let accName = $"__laacc{uid}"
            let accVar = synAt (ExprVar accName)
            let nxtName = $"__lanxt{uid}"
            let jName = $"__laj{uid}"
            let cpName = $"__lacp{uid}"
            // Materialize a private copy: identity map + compute (the
            // measured-working function-body shape).
            let matCopy (e: Expr) =
                let idLam = synAt (ExprLambda ([{ Name = cpName; Type = None; Default = None; NameSpan = span }], None, synAt (ExprVar cpName)))
                synAt (ExprCompute (synAt (ExprBinOp (Elementwise, OpApply, e, idLam))))
            let sliceAt (ix: Expr) = synAt (ExprApp (srcVar, [ix]))
            let seedExpr =
                match init with
                | Some ie -> matCopy ie
                | None -> matCopy (sliceAt (synAt (ExprLit (LitInt 0L))))
            // Static leading extent bakes the bound (the loop-bound rule
            // literal Idx<N> params already follow); a runtime extent
            // reads the operand's leading slot of the extents tuple.
            let hiExpr =
                match staticLead with
                | Some n -> synAt (ExprLit (LitInt n))
                | None ->
                    synAt (ExprTupleIndex (synAt (ExprExtents srcVar),
                                           synAt (ExprLit (LitInt 0L))))
            let startLit = if init.IsSome then 0L else 1L
            let combined = synAt (ExprCompute (synAt (ExprApp (kernel, [accVar; sliceAt (synAt (ExprVar jName))]))))
            let loopBody =
                [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar nxtName); Type = None; Value = combined }
                  StmtAssign (synAt (ExprVar accName), AssignEq, synAt (ExprVar nxtName)) ]
            let stmts =
                (if srcIsNamed then []
                 else [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar srcName); Type = None; Value = array } ])
                @ [ StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar accName); Type = None; Value = seedExpr }
                    StmtForIn (jName, synAt (ExprKind.ExprDotDot (synAt (ExprLit (LitInt startLit)), hiExpr)), loopBody) ]
            Some (inferExpr env (synAt (ExprBlock (stmts, Some accVar))))
        | _ -> None

// ---- Rank-k dense fold (k >= 2): desugar to the internal loop nest ----
// Folds every element in DECLARED (row-major) order through the kernel
// with one scalar accumulator -- byte-identical to the imperative
// accumulation nest it replaces (the hosvd/math-corpus port shape and
// the structured-binning route). Bounds: static extents, dense
// non-symmetric axes, scalar elements. Seed = the 3-arg init, or the
// (+)/(*) identity for operator sections (a rank-k nest cannot
// seed-with-first without a per-element guard).
and reduceRankKDesugar ({ InferEntries.InferExpr = inferExpr } as ie) (env: TypeEnv) (tArrCache: Lazy<TypeResult<TypedExpr>>) (kernel: Expr) (array: Expr) (axes: Expr option) (init: Expr option) () =
    match tArrCache.Force() with
    | Error _ -> None
    | Ok tArr0 ->
        // A DEFERRED rank-k computation whose fold kernel asked for `omp`
        // is deliberately NOT desugared here. The desugar MATERIALIZES the
        // computation into a temporary and folds it with a hand-built nest
        // that carries no clause at all -- so a licensed `where comm, omp`
        // would come out serial with no trace, which is precisely the
        // silent-drop failure this feature exists to avoid. Declining sends
        // it to the fused reduction terminal instead (tryInferReduceCompute
        // below), which needs no intermediate array AND can chunk the outer
        // level. Everything else -- including every unannotated rank-k
        // reduce -- is untouched.
        // Does the fold kernel ask for `omp`? Read off the surface kernel
        // (a lambda's clause, or a named function's), never re-typed.
        let kernelRequestsOmp () =
            match inferExpr env kernel with
            | Ok tk ->
               (match (resolveTypedExpr env tk).Kind with
                | TExprLambda li ->
                   li.Parallel |> List.exists _.IsOmp
                | TExprVar (_, fnId, _) ->
                   (match env.FuncParallel.TryGetValue fnId with
                    | true, (_, s) -> s |> List.exists _.IsOmp
                    | _ -> false)
                | _ -> false)
            | Error _ -> false
        let operandIsDeferred () =
            match (resolveTypedExpr env tArr0).Kind with
            | TExprApply _ | TExprFusion _ -> true
            | _ -> false
        let deferredWithOmpKernel () = operandIsDeferred () && kernelRequestsOmp ()
        // G233 (round 4 wave 10; Codex design review D4a): a NAMED
        // operand that is a deferred MAP is not desugared either. The
        // desugar reads it by position (`p(i, j)` in a synthesized block),
        // a forcing use: the compiled lanes forced and PRINTED it at the
        // block's pre-pass, the interpreter at the first read (after the
        // seed). A fold over a deferred map fuses and forces nothing
        // (formalism 10.2): the fused terminal below folds every cell, and
        // its prelude runs the captures before the seed (a captured spine
        // folds its snapshot). Module level and top-level blocks only (a
        // callable body's S2 stores the let; its fold reads the array).
        let namedDeferredMap () =
            array.Kind.IsExprVar && not env.InCallableBody
            && (let r = resolveTypedExpr env tArr0
                match r.Kind with
                // a WRITING map is stored at its definition (G45): its
                // fold reads the array, which this desugar keeps
                // ... and only the shape the fused terminal folds
                // (Codex diff review: a ROW kernel, `lambda(row: T^1) ->
                // row + 1.0`, or a ragged / grouped / compound input kept
                // its working desugar): a scalar-cell kernel over plain
                // dense operands
                | TExprApply info when not info.IsComposeApply ->
                    info.KernelOutputRank = 0
                    && info.KernelInputRanks |> List.forall (fun k -> k = 0)
                    && info.ArrayTypes |> List.forall (fun at ->
                           at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone))
                    && (outerWritesOfBody env env r).IsEmpty
                    && (parallelCalleeWriteError env [] r).IsNone
                | _ -> false)
        // The same silent drop for a MATERIALIZED operand (bench gap G10 /
        // the `reduce(lap * lap, lambda ... where omp ..., 0.0, axes = 2)`
        // probe): the nest below carries no clause, so the licence vanished
        // with no census line. Re-spell the operand as the identity map
        // over its cells, `method_for(A) <@> lambda(c) -> c` -- a DEFERRED
        // computation, so the re-inferred fold declines this desugar
        // (`deferredWithOmpKernel`) and reaches the fused reduction
        // terminal, which threads the outer level (Path B), or says why it
        // did not. Same cells, same row-major order, no extra pass: the
        // map is never materialized.
        // SYNTACTIC omp read (the partial-fold gate's precedent above): no
        // re-inference of the kernel, so an unchanged program mints the same
        // ids it always did and its emission stays byte-identical.
        let kernelAsksOmpSurface () =
            let isOmp (p: ParallelStrategy) = match p with Omp _ -> true | _ -> false
            match kernel.Kind with
            | ExprLambda (_, Some wc, _) -> wc.Parallel |> List.exists isOmp
            | ExprVar fn ->
                (match lookupVar fn env |> Option.map (fun vi -> env.FuncParallel.TryGetValue vi.VarId) with
                 | Some (true, (_, s)) -> s |> List.exists _.IsOmp
                 | _ -> false)
            | _ -> false
        let ompOverMaterialized () =
            not (operandIsDeferred ()) && kernelAsksOmpSurface ()
            && (match axes with Some _ -> true | None -> false)
        if ompOverMaterialized () then
            let span = array.Span
            let synAt k = mkExpr span k
            let cell = $"__rkc{env.Builder.FreshId()}"
            let identity =
                synAt (ExprLambda ([{ Name = cell; Type = None; Default = None; NameSpan = span }], None,
                                   synAt (ExprVar cell)))
            let deferred =
                synAt (ExprBinOp (Elementwise, OpApply, synAt (ExprMethodFor [array]), identity))
            Some (inferReduceCore ie env deferred kernel init axes)
        else
        match env.Subst.Resolve tArr0.Type with
        | ArrayElem at when at.IndexTypes.Length >= 2
                            && at.IndexTypes |> List.forall (fun ix ->
                                   ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
                            && (env.Subst.Resolve at.ElemType).IsIRTScalar
                            && not (deferredWithOmpKernel ())
                            && not (namedDeferredMap ()) ->
            let extents =
                at.IndexTypes |> List.map (fun ix ->
                    match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
            if extents |> List.exists Option.isNone then None
            else
                let ns = extents |> List.map Option.get
                let span = array.Span
                let synAt k = mkExpr span k
                let iLit (v: int64) = synAt (ExprLit (LitInt v))
                let elemTy = env.Subst.Resolve at.ElemType
                // The implicit `(+)` seed of this UNSEEDED fold (gap G74).
                // The nest below is a plain accumulator loop -- SERIAL in
                // every build and lane, never a licensed form -- so this
                // fold is OBLIGATORILY serial on every route and its seed
                // is the EXACT identity in every build (`-0.0`;
                // `(-0.0, -0.0)` complex): the chain is the left fold of
                // the cells bit for bit, and a fold of no cells (a zero
                // extent) is that identity, as every strict route's empty
                // fold is (formalism 6.4).
                let zeroOf () =
                    match elemTy with
                    | IRTScalar (ETFloat64 | ETFloat32) -> Some (synAt (ExprLit (LitFloat -0.0)))
                    | IRTScalar (ETInt64 | ETInt32) -> Some (synAt (ExprLit (LitInt 0L)))
                    | IRTScalar (ETComplex128 | ETComplex64) ->
                        Some (synAt (ExprApp (synAt (ExprVar "complex"),
                                              [synAt (ExprLit (LitFloat -0.0)); synAt (ExprLit (LitFloat -0.0))])))
                    | _ -> None
                let oneOf () =
                    match elemTy with
                    | IRTScalar (ETFloat64 | ETFloat32) -> Some (synAt (ExprLit (LitFloat 1.0)))
                    | IRTScalar (ETInt64 | ETInt32) -> Some (synAt (ExprLit (LitInt 1L)))
                    | _ -> None
                let seedOpt =
                    match init, kernel.Kind with
                    | Some e, _ -> Some e
                    | None, ExprKind.ExprSection OpAdd -> zeroOf ()
                    | None, ExprKind.ExprSection OpMul -> oneOf ()
                    | _ -> None
                match seedOpt with
                | None -> Some (Error (ReductionFormErr "reduce over a rank >= 2 array needs an explicit init (3-arg reduce) unless the kernel is a (+) or (*) section"))
                | Some seed ->
                    // The accumulator of a LITERAL seed (the synthesized
                    // identity, or an init written as a literal) is typed
                    // at the element's width, so a Float32 / Int32 fold
                    // accumulates in its own width (round 6).
                    let rec litSeed (e: Expr) =
                        match e.Kind with
                        | ExprKind.ExprLit (LitInt _ | LitFloat _) -> true
                        | ExprKind.ExprUnaryOp (OpNeg, x) -> litSeed x
                        | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar "complex" }, [ a; b ]) -> litSeed a && litSeed b
                        | _ -> false
                    let accAnn =
                        if not (litSeed seed) then None
                        else
                            match elemTy with
                            | IRTScalar ETFloat64 -> Some TyFloat64
                            | IRTScalar ETFloat32 -> Some TyFloat32
                            | IRTScalar ETInt64 -> Some TyInt64
                            | IRTScalar ETInt32 -> Some TyInt32
                            | IRTScalar ETComplex64 -> Some TyComplex64
                            | IRTScalar ETComplex128 -> Some TyComplex128
                            | _ -> None
                    let uid = env.Builder.FreshId()
                    // A NAMED operand is read in place: `let __rksrc = A`
                    // is a whole-array COPY in the C++ lane (a full read +
                    // write pass before the fold reads it again), and the
                    // nest below only reads. Same rule as the leading-axis
                    // and partial-fold desugars' `srcIsNamed`.
                    let srcIsNamed = array.Kind.IsExprVar
                    let srcName = $"__rksrc{uid}"
                    let accName = $"__rkacc{uid}"
                    let srcVar = if srcIsNamed then array else synAt (ExprVar srcName)
                    let accVar = synAt (ExprVar accName)
                    let ivars = ns |> List.mapi (fun k _ -> $"__rk{uid}_{k}")
                    let elemRead = synAt (ExprApp (srcVar, ivars |> List.map (fun v -> synAt (ExprVar v))))
                    let combined =
                        match kernel.Kind with
                        | ExprKind.ExprSection op -> synAt (ExprBinOp (Elementwise, op, accVar, elemRead))
                        | _ -> synAt (ExprApp (kernel, [accVar; elemRead]))
                    let assign = StmtExpr (synAt (ExprAssign (accVar, combined)))
                    let nest =
                        List.foldBack2 (fun ivar ext inner ->
                            [ StmtForIn (ivar, synAt (ExprKind.ExprDotDot (iLit 0L, iLit ext)), inner) ])
                            ivars ns [assign]
                    let block =
                        synAt (ExprBlock (
                            (if srcIsNamed then []
                             else [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar srcName); Type = None; Value = array } ])
                            @ [ StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar accName); Type = accAnn; Value = seed } ]
                            @ nest, Some accVar))
                    Some (inferExpr env block)
        | _ -> None

/// Reorder licence for a fold kernel that carries `where ... omp`.
///
/// `omp` on the SECOND argument of `reduce` is the opt-in for a parallel fold
/// (docs/plan-cpp-perf-exploitation.md): codegen chunks the reduced axis,
/// folds each chunk serially, and combines the partials in a fixed order.
/// That is sound only if the kernel is commutative and associative, so an
/// unlicensed `omp` is REFUSED here rather than silently emitted serial --
/// same reasoning as the BL9001 dropped-clause guard: "asked and got serial"
/// and "never asked" produce byte-identical C++, so the user could not tell.
/// Ok () for every kernel that did not ask (the overwhelming majority: an
/// operator section cannot carry a where-clause at all).
and checkFoldOmpLicense (env: TypeEnv) (tKernel: TypedExpr) : TypeResult<unit> =
    let asksOmp (strategies: ParallelStrategy list) =
        strategies |> List.exists _.IsOmp
    match (resolveTypedExpr env tKernel).Kind with
    // Inline / let-bound lambda: the clause and the body are both right here.
    | TExprLambda info when asksOmp info.Parallel ->
        if info.IsCommutative
           || not (List.isEmpty info.CommGroups)
           || isBuiltinFoldBodyTyped info.Params info.Body then Ok ()
        else Error (FoldOmpNeedsLicense "this fold kernel")
    // Named function: the clause and the comm groups live in the side channels
    // checkFunctionDecl fills (a named function's body never reaches here).
    | TExprVar (fname, fnId, _) ->
        let asks =
            match env.FuncParallel.TryGetValue fnId with
            | true, (_, strategies) -> asksOmp strategies
            | _ -> false
        if not asks then Ok ()
        else
            let licensed =
                (match env.FuncCommGroups.TryGetValue fnId with
                 | true, cg -> not (List.isEmpty cg)
                 | _ -> false)
                || (match env.FuncFoldBuiltin.TryGetValue fnId with
                    | true, b -> b
                    | _ -> false)
            if licensed then Ok ()
            else Error (FoldOmpNeedsLicense $"fold kernel '{fname}'")
    | _ -> Ok ()

// extents(array) -- extent(s) along each dimension as Int64. Rank-1 ->
// scalar Int64; rank-N -> tuple (Int64, ..., Int64) of length N, outermost
// first. Codegen prefers a static literal when the extent expression is
// statically evaluable (tryEvalIntIR), matching rank()'s behavior.
//
// A homogeneous List<Int64> would be a more natural return type for the
// multi-rank case than a tuple once List exists.


// reduce(array, kernel) -- T/S reduction primitive. Consumes the innermost
// dimension via a binary kernel; rank-1 input produces a scalar (multi-rank
// reduction deferred). Empty-array policy: statically-0 extent is a
// typecheck error (caller bug); statically->0 emits a standard loop;
// dynamic extent adds a runtime guard that aborts cleanly on empty.


// stack / join -- the two rank-changing assembly combinators (formalism 2.6)
//
// Both materialize a fresh DENSE rectangular pool by copying their operands
// (the codegen twins are materializeStackForm / materializeJoinForm), so both
// share the same admissibility fence: every operand must be an array whose
// index slots are plain arity-1 SymNone dimensions. A compact / ragged /
// compound slot has no rectangular address space to copy into, and silently
// densifying one would be a storage-class change behind the user's back -- so
// it is rejected with a decompact steer instead.
