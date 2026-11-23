// Call elision, rule call-elision: a callee's map lowered jointly with its consumer (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeCallElision

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeComputeElision

// ===========================================================================
// CALL ELISION (BLADE_CALL_ELIDE, plan rule `call-elision`; the P4 entry of
// docs/plans/plan-identity-transpose-compute.md, section 4's boundary rule).
//
// THE CALLEE FORCES (Lowering.forceReturnCombinator): `prep(a) = { map(a) |>
// compute }` materializes its result inside prep, so `reduce(prep(A), (+))`
// allocates prep's return pool, fills it in one loop and folds it in another,
// where the same lines written out (`reduce(method_for(A) <@> k, (+))`) fuse
// with no pool. When a DIRECT call of a SUPPORTED callee is the only call of
// a consumer statement this pass takes, the call is JOINTLY LOWERED at that
// site: the callee's scalar let spine is inlined (fresh ids) ahead of the
// statement, and its one map -- parameters replaced by the arguments --
// becomes the consumer's operand:
//
//   FOLD  `reduce(f(..), k[, s])`, the whole value of a statement -> the
//         fused `IRReduceCompute (map, k, seed)`: no pool, no separate loop.
//   MAP   a pointwise map / co-iteration `IRCompute (method_for(.., f(..), ..)
//         <@> g)`, the whole value of a statement, whose kernel reads the
//         call's cell exactly once -> the map is that operand in place;
//         elementwise fusion (next) splices it. Recorded after fusion ran.
//
// The callee itself is untouched: every other call site, export, callback
// and foreign boundary keeps its materialized-array ABI and its forcing point
// (plan section 4). LIFETIMES: the inlined spine owns no allocation -- each
// let is a scalar and the map is read in place by the fused fold, or spliced
// by fusion (where fusion declines, it is the consumer's computed operand,
// built and released by the consumer exactly as a written-out
// `method_for(method_for(A) <@> f |> compute) <@> g`) -- so nothing outlives
// the statement and no return-boundary event (IRReleasePools, G69) is needed.
// An inline that would allocate anything but scalars declines.
//
// Commitments (plan section 4), kept as compute elision keeps them:
//   * SNAPSHOT. The call runs inside its consumer's statement, so the only
//     code between it and the consumer is that statement: the statement (its
//     fold kernel and seed, its host kernel, the extents its types carry) may
//     not write (`scheduleMayWriteIn`) and its binding carries no provider
//     effect; an argument that is a `mut` / assigned binding declines.
//   * FAILURE SITE AND ORDER. The callee's lets and its map kernel pass the
//     IRTotality whitelist, a co-iteration map has literal equal extents, its
//     extents are plain: nothing in the callee can fail, so evaluating it
//     inside the consumer's loop is unobservable.
//   * ONCE-ONLY EFFECTS. A callee that may write declines.
//   * EVALUATION COUNT. One consumer reading each cell once.
//   * EMPTY INPUT AND THE SEED. `elidedFoldSeed`, compute elision's rule: an
//     unseeded builtin fold only over a literal extent >= 1 (an empty input
//     keeps the call and its BL8003 at the fold), the seed by W4-3's route.
// WAVE 3 (P4 remainder): (a) a LET-BOUND single-reader call (`let x = f(..)`
// whose one reader is a later fold of x or a pointwise map with x as one
// operand, nothing written in between) moves to its reader and is lowered
// there; (b) a callee in ANOTHER module is inlined into the caller's when the
// program-wide value-reference census (CallElideProgram) is installed, its
// body and kernel name nothing but its parameters, lets, literals and the
// caller's module's callables, and its kernel is cloned into the caller's
// module; (c) a KERNEL (lambda) body is a caller (a kernel with a parallel
// strategy, or lexically inside a `where repro` body, declines as a repro or
// parallel caller); (d) a callee with a NON-SCALAR let still declines, its
// record naming gap G69 (no audited return-boundary release; scope model A).
// Fails closed (each a recorded decline) on: a foreign callee without the
// census, or naming another module's callables;
// static, generic / arity-polymorphic, `where repro`, omp / cuda / mpi
// callees and a `where repro` caller; captures; recursion; a callee
// referenced as a value; an argument that is not a stored variable or a
// literal (deferred, streamed, function-valued, `mut`); a body that is not a
// scalar let spine ending in one plain dense `|> compute` map over its
// parameters; a body or map kernel reading a name it does not bind; a kernel
// naming a capturing callable; a map extent naming a parameter (a shape the
// specializer did not bake); a declared return type the map does not
// produce; several calls in one statement. A module holding a CUDA kernel is
// skipped whole.
//
// A TREE REWRITE before the lane split, like compute elision: the
// interpreter runs the rewritten tree, so `blade test opt-diff` (the gate off
// vs on) is the differential. A captured scalar of the callee is re-bound
// under a fresh id in the caller, so a capturing map kernel is CLONED (a new
// lambda whose captures name the fresh copies; DerivedFuncOrigins keeps it at
// its origin's program point); a capture-free kernel is shared as is.
// ===========================================================================

let recordCallElision (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    recordPendingElision "call-elision" subject outcome evidence

/// Callables named as a VALUE anywhere in the program: more IRVar mentions
/// than call heads, counted over every module's binding payloads (side-table
/// operands included) and every function body, each once -- and (round 4
/// wave 7, lane c, the G123 class-closure round's hardening) over every
/// extent expression a type carries in them (`embeddedExtentExprs`) and in
/// each function's declared parameter / return types: a `>>=` continuation
/// hidden in a range extent (`extents(0..Int64(floor((p >>= rowsum) |>
/// compute)))`) is a value reference too. Heads and mentions are counted by
/// the same visitor on the same roots, so a direct call inside an extent
/// stays balanced.
let programValueRefs (modules: IRModule list) : Set<IRId> =
    let fids = modules |> List.collect (fun m -> m.Functions |> List.map (fun f -> f.Id)) |> Set.ofList
    let heads = System.Collections.Generic.Dictionary<IRId, int>()
    let vars = System.Collections.Generic.Dictionary<IRId, int>()
    let bump (d: System.Collections.Generic.Dictionary<IRId, int>) (id: IRId) =
        d.[id] <- (match d.TryGetValue id with | true, n -> n + 1 | _ -> 1)
    let countOne (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | CallOfNamed (f, _, _, _) -> bump heads f
            | IRVar (f, _) when fids.Contains f -> bump vars f
            | _ -> ()) e
    let count (e: IRExpr) =
        countOne e
        for x in embeddedExtentExprs e do countOne x
    let countType (t: IRType) =
        for x in irTypeExtentExprs t do count x
    for m in modules do
        for f in m.Functions do
            count f.Body
            for p in f.Params do countType p.Type
            countType f.RetType
        for b in m.Bindings do
            for p in bindingPayload m b do count p
    vars |> Seq.filter (fun kv -> kv.Value > (match heads.TryGetValue kv.Key with | true, n -> n | _ -> 0))
         |> Seq.map (fun kv -> kv.Key) |> Set.ofSeq

/// The setup of `elideCallsModule` that its callee judge and its per-statement elision read, built once
/// per call (cleanup item 2). Field names are the bindings' own, so the lifted bodies read them unchanged.
type private EcContext =
    { modul: IRModule
      builder: IRBuilder
      pending: ResizeArray<ComputeElisionPending>
      minted: ResizeArray<IRCallable * IRId>
      enabled: bool
      own: Map<IRId, IRCallable>
      funcs: Map<IRId, IRCallable>
      localCallables: System.Collections.Generic.Dictionary<IRId, IRCallable>
      resolver: System.Collections.Generic.Dictionary<IRId, IRCallable>
      resolve: IRExpr -> IRCallable option
      mayWrite: IRExpr -> bool
      payloadOf: IRId -> IRExpr -> IRExpr
      assigned: System.Collections.Generic.HashSet<IRId>
      deferredIds: System.Collections.Generic.HashSet<IRId>
      deferredW: IRId -> bool
      mutableIds: Set<IRId>
      streamedIds: Set<IRId>
      referencedAsValue: IRCallable -> bool
      nameOfId: IRId -> string
      showArg: IRExpr -> string
      refsOf: IRId -> IRExpr -> int
      plainExtent: IRExpr -> bool
      staticExtents: IRArrayType -> int64 option list
      sameLayout: IRType -> IRType -> bool
      userCall: IRExpr -> (IRCallable * IRExpr list) option
      countCalls: IRExpr -> int
      binderIds: IRExpr -> Set<IRId>
      unroll: IRExpr -> (IRId * IRExpr) list * IRExpr }

/// One statement's call elision (lifted from elideCallsModule, cleanup item 2): the setup through the
/// context; the callee judge and the region's three flags (the same ref cells) as parameters.
let private ecTryElideCore (ctx: EcContext) (regionKernel: bool ref) (regionParallel: bool ref) (judge: bool -> IRCallable -> IRExpr list -> Result<(IRId * IRExpr) list * ApplyInfo * IRCallable * IRCallable option * string list, string>) (regionRepro: bool ref) (caller: string) (stmtId: IRId option) (sv: IRExpr) (letBound: (string * string list) option) : ((IRId * IRExpr) list * IRExpr) option =
    let { userCall = userCall; showArg = showArg; countCalls = countCalls; modul = modul; payloadOf = payloadOf;
          mayWrite = mayWrite; mutableIds = mutableIds; assigned = assigned; nameOfId = nameOfId; minted = minted;
          resolve = resolve; enabled = enabled; localCallables = localCallables; resolver = resolver;
          refsOf = refsOf; pending = pending } = ctx
    let consumer =
        match sv with
        | IRReduce (op, k, init) ->
            (match userCall op with
             | Some (c, args) -> Some (Choice1Of2 (c, args, k, init))
             | None -> None)
        | IRCompute (IRApplyCombinator h) ->
            (match h.Arrays |> List.indexed |> List.tryPick (fun (i, a) -> userCall a |> Option.map (fun (c, args) -> (i, c, args))) with
             | Some (i, c, args) -> Some (Choice2Of2 (h, i, c, args))
             | None -> None)
        | _ -> None
    match consumer with
    | None -> None
    | Some kind ->
    let (c, args) = match kind with Choice1Of2 (c, args, _, _) -> (c, args) | Choice2Of2 (_, _, c, args) -> (c, args)
    let callDisp = $"""{c.SourceName}({args |> List.map showArg |> String.concat ", "})"""
    let subject =
        match kind, letBound with
        | Choice1Of2 _, Some (xn, _) -> $"{caller}: let {xn} = {callDisp}; reduce({xn}) -> one fused fold"
        | Choice2Of2 _, Some (xn, _) -> $"{caller}: let {xn} = {callDisp}; a pointwise map over {xn} -> one fused nest"
        | Choice1Of2 _, None -> $"{caller}: reduce({callDisp}) -> one fused fold"
        | Choice2Of2 _, None -> $"{caller}: a pointwise map over {callDisp} -> one fused nest"
    let decline (why: string) (ev: string list) =
        recordCallElision subject (Blade.Effects.Declined why) ev
        None
    let extraEv =
        (match letBound with Some (_, ev) -> ev | None -> [])
        @ (if regionKernel.Value then [ "the caller is a kernel (lambda) body: the call's arguments are its parameters, its captures or its own lets, all bound before the call" ] else [])
    if countCalls sv > 1 then decline "several calls in one statement (v1 jointly lowers a statement's only call)" []
    elif regionParallel.Value then
        decline "the caller is a kernel with a parallel strategy (omp / cuda / mpi): v1 jointly lowers into serial kernel bodies only" []
    else
    match judge regionRepro.Value c args with
    | Error why -> decline why []
    | Ok (lets', p', pkc, clone, ev00) ->
    let ev0 = ev00 @ extraEv
    // the statement itself (fold kernel and seed, host kernel, the extents
    // its types carry) runs between the call and the consumer's reads
    let writer =
        let prov = match stmtId with Some id -> intervalHasProviderEffect modul id | None -> false
        let payload = match stmtId with Some id -> payloadOf id sv | None -> sv
        prov || mayWrite payload || (embeddedExtentExprs sv |> List.exists mayWrite)
    let mutArg =
        args |> List.tryPick (function
            | IRVar (id, _) when mutableIds.Contains id || assigned.Contains id ->
                Some $"argument `{nameOfId id}` is a `mut` / assigned binding (it may be a `mut` parameter's caller-visible array; v1 keeps the call)"
            | _ -> None)
    let commit (v': IRExpr) =
        clone |> Option.iter (fun k -> minted.Add((k, pkc.Id)))
        Some (lets', v')
    if writer then decline "the consumer statement may write (its fold kernel, seed or map kernel): the inlined map would read the arguments after the write" ev0
    else
    match mutArg with
    | Some why -> decline why ev0
    | None ->
    // P5 step 2: the legality clause and the cost basis (the callee's
    // return pool is the materialized baseline; its scalar lets run in both)
    let clLegality =
        Blade.CostModel.legalityLine "joint lowering of a supported call at its site (plan section 4's boundary rule; snapshot: a write-free consumer statement and no `mut` / assigned argument; failure site: IRTotality-total callee lets and map; once-only effects: a write-free callee; evaluation count: one consumer, each cell once; empty input: the fused fold's seed by W4-3's route rule; the callee keeps its ABI at every other site)"
    let clPool = $"{c.SourceName}'s return pool"
    let clStored = Blade.CostModel.storedArrayName nameOfId
    let clLets = lets' |> List.map snd
    match kind with
    | Choice1Of2 (_, _, k, init) ->
        match elidedFoldSeed regionRepro.Value p'.OutputType (resolve k) init with
        | Error why -> decline why ev0
        | Ok (seed, seedNote) ->
            let costLine = Blade.CostModel.foldElisionLine resolve nameOfId clStored clPool clLets p' (resolve k)
            let ev = ev0 @ [ "single consumer fold `reduce` (the whole value of its statement)"; seedNote; "the fold reads the callee's map in place: no return pool, no separate loop"; clLegality; costLine ]
            if not enabled then decline "disabled by BLADE_CALL_ELIDE" ev
            else
                recordCallElision subject Blade.Effects.Applied ev
                commit (IRReduceCompute (IRApplyCombinator p', k, seed))
    | Choice2Of2 (h, pos, _, _) ->
        let mapProblem =
            match tileKernelOf localCallables h with
            | _ when not (Blade.Toolchain.envFlag "BLADE_FUSION" true) ->
                Some "elementwise fusion, the map consumer's realization, is disabled (BLADE_FUSION)"
            | None -> Some "the consumer's kernel is not a resolvable callable"
            | Some hk ->
                if not (tilePlainInfo h) then Some "the consumer is not a plain dense map or co-iteration"
                elif not (tilePlainKernel hk h.Arrays.Length) then Some "the consumer kernel declares symmetry, a parallel strategy or arity polymorphism"
                elif not (tilePureBody resolver Set.empty hk.Body) then Some "the consumer kernel is not repeatable"
                elif not (tilePureBody resolver Set.empty pkc.Body) then Some "the callee's map kernel is not repeatable"
                elif refsOf hk.Params.[pos].VarId hk.Body <> 1 then
                    Some $"the consumer kernel reads the call's cell {refsOf hk.Params.[pos].VarId hk.Body} times (exactly once is required)"
                elif h.Arrays.Length + p'.Arrays.Length - 1 > 8 then Some "the fused operand count would exceed elementwise fusion's fan-in cap (8)"
                elif h.SharedIndexTypes.IsEmpty && h.Arrays.Length + p'.Arrays.Length - 1 > 1 && p'.SharedIndexTypes.IsEmpty then
                    Some "the fused node would read as an outer product (no shared index records)"
                else None
        match mapProblem with
        | Some why -> decline why ev0
        | None ->
            let repl = IRCompute (IRApplyCombinator p')
            let setAt (xs: 'a list) (x: 'a) = xs |> List.mapi (fun i y -> if i = pos then x else y)
            let outAt = match stripUnits p'.OutputType with ArrayElem at -> Some at | _ -> None
            let loop' =
                match h.Loop with
                | IRMethodFor mf when mf.Arrays.Length = h.Arrays.Length ->
                    IRMethodFor { mf with Arrays = setAt mf.Arrays repl
                                          ArrayTypes = (match outAt with Some at when mf.ArrayTypes.Length = h.Arrays.Length -> setAt mf.ArrayTypes at | _ -> mf.ArrayTypes) }
                | l -> l
            let h' =
                { h with Arrays = setAt h.Arrays repl
                         ArrayTypes = (match outAt with Some at -> setAt h.ArrayTypes at | None -> h.ArrayTypes)
                         Loop = loop' }
            let callOperand = h.Arrays.[pos]
            let storedBefore (e: IRExpr) = if e = callOperand then Some clPool else clStored e
            let outName = match stmtId with Some id -> nameOfId id | None -> "the returned value"
            let costLine = Blade.CostModel.mapElisionLine resolve nameOfId storedBefore clStored clPool outName clLets p' h h'
            let ev = ev0 @ [ "single consumer pointwise map (it reads the call's cell once, at its own coordinate)"; "realized by elementwise fusion: the callee's map is spliced into the consumer's nest, no return pool"; clLegality; costLine ]
            if not enabled then decline "disabled by BLADE_CALL_ELIDE" ev
            else
                let hkId = match h.Kernel with IRVar (kid, _) -> kid | _ -> -1
                pending.Add { PendingRule = "call-elision"; PendingSubject = subject; HostKernel = hkId; PendingEvidence = ev; SplicedKernel = None }
                commit (IRCompute (IRApplyCombinator h'))

/// THE CALLEE JUDGE of call elision (lifted from elideCallsModule, cleanup item 2): the setup through the
/// context.
let private ecJudge (ctx: EcContext) (callerRepro: bool) (c: IRCallable) (args: IRExpr list)
    : Result<(IRId * IRExpr) list * ApplyInfo * IRCallable * IRCallable option * string list, string> =
    let { funcs = funcs; streamedIds = streamedIds; nameOfId = nameOfId; deferredIds = deferredIds; own = own;
          referencedAsValue = referencedAsValue; mayWrite = mayWrite; unroll = unroll; binderIds = binderIds;
          resolve = resolve; deferredW = deferredW; plainExtent = plainExtent; staticExtents = staticExtents;
          sameLayout = sameLayout; builder = builder } = ctx
    let argProblem (a: IRExpr) =
        match a with
        | IRLit _ -> None
        | IRVar (id, ty) when isFunctionTyped ty || Map.containsKey id funcs -> Some "an argument is a function value"
        | IRVar (id, _) when streamedIds.Contains id -> Some $"argument `{nameOfId id}` is a streamed provider read (walked one block at a time; not a stored value)"
        | IRVar (id, _) when deferredIds.Contains id -> Some $"argument `{nameOfId id}` is a deferred computation (the call materializes it; v1 jointly lowers over stored arrays and scalars only)"
        | IRVar _ -> None
        | _ -> Some "an argument is not a variable or a literal"
    let foreignCallee = not (Map.containsKey c.Id own)
    if foreignCallee && (CallElideProgram.valueRefs ()).IsNone then
        Error "the callee is defined in another module (v1 jointly lowers a callee of the caller's own module only) -- the program's cross-module value-reference census is unavailable here"
    elif foreignCallee && not (Map.containsKey c.Id funcs) then
        Error "the callee is defined in another module (v1 jointly lowers a callee of the caller's own module only) -- it is not in the program's callable table"
    elif c.IsStatic then Error "a `static` callee"
    elif c.IsArityPoly || hasTypeVarsInSignature c then Error "a generic or arity-polymorphic callee the shape specializer has not resolved to a concrete instance"
    elif c.IsOmpParallel || not c.Parallelism.IsEmpty || c.IsCudaKernel || c.IsMpiParallel then
        Error "the callee requests a parallel strategy (omp / cuda / mpi)"
    elif c.IsRepro then Error "a `where repro` callee keeps its per-function contract"
    elif callerRepro then Error "a `where repro` caller (the callee's arithmetic would take the caller's contraction and fold contract)"
    elif not c.Captures.IsEmpty then Error "the callee captures"
    elif c.Params.Length <> args.Length then Error "an argument-count mismatch"
    elif Set.contains c.Id (collectVarRefsIR c.Body) then Error "a recursive callee"
    elif referencedAsValue c then Error $"`{c.SourceName}` is referenced as a value (an alias, an argument or a kernel), not only called"
    else
    match args |> List.tryPick argProblem with
    | Some why -> Error why
    | None ->
    if mayWrite c.Body then Error "the callee may write (an element write through a `mut` parameter included: a writing callee keeps its call, its effects happen once, at the call)"
    else
    let (lets0, tail0) = unroll c.Body
    match tail0 with
    | IRCompute (IRApplyCombinator p) ->
        let pset = c.Params |> List.map (fun pp -> pp.VarId) |> Set.ofList
        let binders = binderIds c.Body
        let localIds = Set.union pset binders
        let foreign =
            collectVarRefsIR c.Body |> Set.filter (fun id -> not (localIds.Contains id) && not (Map.containsKey id funcs))
        let mapKernelId = match p.Kernel with IRVar (kid, _) -> kid | _ -> -1
        if not foreign.IsEmpty then Error "the callee body reads a name it does not bind (a module value or a capture)"
        // a capturing callable named outside the map kernel (a lambda
        // applied in a scalar let) would carry its captures of the
        // callee's names into the caller unremapped (Codex review)
        elif collectVarRefsIR c.Body
             |> Set.exists (fun id -> id <> mapKernelId && Map.containsKey id funcs && not funcs.[id].Captures.IsEmpty) then
            Error "the callee body names a capturing callable outside its map kernel"
        else
        let scalarTy (e: IRExpr) = try (Blade.IRTotality.scalarElem (typeOf e)).IsSome with _ -> false
        let rec letProblem (bound: Set<IRId>) (ls: (IRId * IRExpr) list) =
            match ls with
            | [] -> None
            | (id, v) :: rest ->
                if not (scalarTy v) then
                    Some $"callee let `__v{id}` is not a scalar (an inline that would allocate anything but scalars declines) -- gap G69: the inlined storage would need its release at an IR-level return-boundary event that every pass and both lanes honour, and IRReleasePools' lifetime is not audited for this inline (the boundary let can be deleted by a join rewrite, merged by CSE, reused by pool reuse); left for the plan's scope model A"
                else
                match Blade.IRTotality.walk resolve (fun _ -> None) deferredW Map.empty Map.empty bound Set.empty (Set.singleton c.Id) v with
                | Error why -> Some $"callee let `__v{id}` is not proved total ({why})"
                | Ok _ -> letProblem (bound.Add id) rest
        match letProblem pset lets0 with
        | Some why -> Error why
        | None ->
        match resolve p.Kernel with
        | None -> Error "the callee's map kernel does not resolve to a callable"
        | Some pkc ->
        if not (tilePlainInfo p) then
            Error "the callee's map is not a plain dense map or co-iteration (structured storage, symmetry, or a non-scalar kernel)"
        elif not (tilePlainKernel pkc p.Arrays.Length) then
            Error "the callee's map kernel declares symmetry, a parallel strategy or arity polymorphism"
        elif not (p.Arrays |> List.forall (function IRVar (id, _) -> pset.Contains id | IRRange _ -> true | _ -> false)) then
            Error "an operand of the callee's map is not one of its parameters or a range"
        elif not (embeddedExtentExprs (IRApplyCombinator p)
                  |> List.forall (fun x -> plainExtent x && Set.isEmpty (Set.intersect (collectVarRefsIR x) localIds))) then
            Error "an extent of the callee's map is not a literal or a plain extent free of its parameters (a shape the specializer did not bake, or one that could fail where the consumer runs)"
        else
        match Blade.IRTotality.auditApply resolve (fun _ -> None) deferredW p with
        | Error why -> Error $"the callee's map kernel is not proved total ({why})"
        | Ok () ->
        if p.Arrays.Length > 1
             && (let exts = p.ArrayTypes |> List.map staticExtents
                 exts |> List.exists (List.exists Option.isNone) || (exts |> List.distinct |> List.length) > 1) then
            Error "a co-iteration map over run-time or unequal extents (its agreement check would move to the consumer)"
        elif not (sameLayout c.RetType p.OutputType) then
            Error "the callee's declared return type is not the layout its map produces"
        else
        let kernelRefs = collectVarRefsIR pkc.Body
        // a FOREIGN callee's body and kernel may name no other callable of
        // a module but the caller's own (a cross-module reference from the
        // inlined copy is not audited in v1)
        let foreignNamed =
            if not foreignCallee then None
            else
                Set.union (collectVarRefsIR c.Body) kernelRefs
                |> Set.toList
                |> List.tryFind (fun id -> id <> c.Id && id <> pkc.Id && Map.containsKey id funcs && not (Map.containsKey id own))
        let kAllowed =
            Set.unionMany [ pkc.Params |> List.map (fun pp -> pp.VarId) |> Set.ofList
                            binderIds pkc.Body
                            pkc.Captures |> List.map (fun cap -> cap.Id) |> Set.ofList ]
        if foreignNamed.IsSome then
            Error $"the callee is defined in another module and its body or map kernel names another callable (`{funcs.[foreignNamed.Value].SourceName}`) of a module other than the caller's (a cross-module reference from the inlined copy is not audited in v1)"
        elif kernelRefs |> Set.exists (fun id -> id <> pkc.Id && Map.containsKey id funcs && not funcs.[id].Captures.IsEmpty) then
            Error "the callee's map kernel names a capturing callable"
        elif pkc.Captures |> List.exists (fun cap -> not (localIds.Contains cap.Id)) then
            Error "the callee's map kernel captures a value the callee does not bind"
        elif kernelRefs |> Set.exists (fun id -> not (kAllowed.Contains id) && not (Map.containsKey id funcs)) then
            Error "the callee's map kernel reads a name it neither binds nor captures"
        else
        // ---- the joint lowering ----------------------------------------
        let argOf = List.zip c.Params args |> List.map (fun (pp, a) -> (pp.VarId, a)) |> Map.ofList
        let fresh = binders |> Seq.map (fun id -> (id, builder.FreshId())) |> Map.ofSeq
        let substIn (e: IRExpr) =
            mapIRExpr (fun n ->
                match n with
                | IRVar (id, ty) ->
                    (match Map.tryFind id argOf with
                     | Some a -> a
                     | None ->
                         match Map.tryFind id fresh with
                         | Some nid -> IRVar (nid, ty)
                         | None -> n)
                | IRLet (id, v, b) ->
                    (match Map.tryFind id fresh with
                     | Some nid -> IRLet (nid, v, b)
                     | None -> n)
                | _ -> n) e
        let lets' = lets0 |> List.map (fun (id, v) -> (fresh.[id], substIn v))
        // a capturing kernel follows the inline: a CLONE whose captures
        // name the fresh copies (or the argument itself; a literal
        // argument is substituted and captured by nobody)
        // (a FOREIGN callee's kernel is always cloned: the inlined map then
        // names only this module's callables)
        let clone =
            if pkc.Captures.IsEmpty && not foreignCallee then None
            else
                let kid = builder.FreshId()
                let (ps', body') = freshenCallable builder pkc
                let (caps, capSubst) =
                    pkc.Captures |> List.fold (fun (cs: CaptureInfo list, m: Map<IRId, IRExpr>) cap ->
                        let addCap (ci: CaptureInfo) = if cs |> List.exists (fun x -> x.Id = ci.Id) then cs else cs @ [ ci ]
                        match Map.tryFind cap.Id argOf with
                        | Some (IRVar (aid, aty)) ->
                            (addCap { cap with Id = aid; Name = $"__v{aid}"; Type = aty }, Map.add cap.Id (IRVar (aid, aty)) m)
                        | Some a -> (cs, Map.add cap.Id a m)
                        | None ->
                            let nid = fresh.[cap.Id]
                            (addCap { cap with Id = nid; Name = $"__v{nid}" }, Map.add cap.Id (IRVar (nid, cap.Type)) m)) ([], Map.empty)
                let body'' =
                    mapIRExpr (fun n ->
                        match n with
                        | IRVar (id, _) when Map.containsKey id capSubst -> capSubst.[id]
                        | _ -> n) body'
                Some { pkc with Id = kid; Name = $"__lambda_{kid}"; Params = ps'; Body = body''; Captures = caps }
        let p1 = match substIn (IRApplyCombinator p) with IRApplyCombinator q -> q | _ -> p
        let p2 =
            match clone, p1.Kernel with
            | Some k, IRVar (_, kty) -> { p1 with Kernel = IRVar (k.Id, kty) }
            | _ -> p1
        let ev =
            [ $"callee `{c.SourceName}`: {lets0.Length} scalar let(s) and one plain dense `|> compute` map, total (IRTotality whitelist) and write-free"
              (match clone with
               | Some k when pkc.Captures.IsEmpty -> $"its map kernel captures nothing; it is cloned into the caller's module as `{k.Name}` (the callee is foreign)"
               | Some k -> $"its map kernel captures the callee's scalars: a clone `{k.Name}` captures their inlined copies"
               | None -> "its map kernel captures nothing (shared with the callee)")
              "the inlined spine allocates nothing but scalars"
              $"`{c.SourceName}` keeps its materialized-array ABI and forcing point for every other call site" ]
            @ (if foreignCallee then
                   [ $"`{c.SourceName}` is defined in ANOTHER module: inlined under fresh ids into this one; its body and map kernel read only its parameters, its own lets and literals (no module value, so no qualified name, export contract or module-initialization order is involved), and no module passes it as a value (the program-wide census); its own module keeps its code and ABI" ]
               else [])
        Ok (lets', p2, pkc, clone, ev)
    | _ -> Error "the callee's body is not a scalar let spine ending in one `|> compute` map"

let elideCallsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule * ComputeElisionPending list =
    if modul.Functions |> List.exists (fun f -> f.IsCudaKernel) then (modul, []) else
    let pending = ResizeArray<ComputeElisionPending>()
    let minted = ResizeArray<IRCallable * IRId>()
    let enabled = callElideEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs = programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let localCallables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in modul.Functions do localCallables.[f.Id] <- f
    let resolver = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do resolver.[f.Id] <- f
    for f in modul.Functions do resolver.[f.Id] <- f
    let resolve (k: IRExpr) = resolveCallableIn funcs k
    let mayWrite = scheduleMayWriteIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    // ---- the module census ---------------------------------------------------
    let assigned = System.Collections.Generic.HashSet<IRId>()
    let deferredIds = System.Collections.Generic.HashSet<IRId>()
    // the census widened by the program's deferred-value classifier (gap G98
    // class audit) for the totality audits below
    let deferredW = widenDeferred modul (modul.Functions @ programFuncs) deferredIds.Contains
    let rec rootVar (t: IRExpr) =
        match t with
        | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> rootVar b
        | IRVar (id, _) -> Some id
        | _ -> None
    let census (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRAssign (t, _) -> (match rootVar t with Some id -> assigned.Add id |> ignore | None -> ())
            | IRLet (id, v, _) when isDeferredForm v -> deferredIds.Add id |> ignore
            | _ -> ()) e
    for f in modul.Functions do census f.Body
    for b in modul.Bindings do
        if isDeferredForm b.Value then deferredIds.Add b.Id |> ignore
        for p in bindingPayload modul b do census p
    // A plain top-level `let` is ASSIGNABLE (IRBinding.IsMutable), so the
    // binding flag proves nothing; what does: a block `let mut` array
    // (MutableArrayLets) and any id an assignment roots at (`assigned`). A
    // `mut` parameter's argument is a `let mut` binding or a forwarded `mut`
    // parameter; a write through it anywhere is in the census, and the callee
    // writing through it fails the callee's write check first.
    let mutableIds = modul.MutableArrayLets
    let streamedIds =
        modul.ProviderReads |> Map.toSeq |> Seq.filter (fun (_, spec) -> spec.Streamed) |> Seq.map fst |> Set.ofSeq
    // A callable REFERENCED AS A VALUE: an IRVar names it outside the head of
    // a call (an alias, an argument, a kernel, a stored value).
    let callHeads = System.Collections.Generic.Dictionary<IRId, int>()
    let varRefs = System.Collections.Generic.Dictionary<IRId, int>()
    let bump (d: System.Collections.Generic.Dictionary<IRId, int>) (id: IRId) =
        d.[id] <- (match d.TryGetValue id with | true, n -> n + 1 | _ -> 1)
    let countRefs (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | CallOfNamed (f, _, _, _) -> bump callHeads f
            | IRVar (f, _) when Map.containsKey f funcs -> bump varRefs f
            | _ -> ()) e
    for KeyValue (_, f) in funcs do countRefs f.Body
    for b in modul.Bindings do
        for p in bindingPayload modul b do countRefs p
    let countOf (d: System.Collections.Generic.Dictionary<IRId, int>) (id: IRId) =
        match d.TryGetValue id with
        | true, n -> n
        | _ -> 0
    let referencedAsValue (c: IRCallable) =
        // the instance and every callable the program wrote under its name
        // (its generic origin, its other specializations)
        // ... and, program-wide, every module's bindings (CallElideProgram:
        // another module may pass the callee as a value; when the census is
        // unavailable a foreign callee has already declined)
        let progRefs = defaultArg (CallElideProgram.valueRefs ()) Set.empty
        funcs |> Map.exists (fun _ g ->
            (g.Id = c.Id || (g.SourceName = c.SourceName && not (g.Name.StartsWith "__lambda_")))
            && (countOf varRefs g.Id > countOf callHeads g.Id || progRefs.Contains g.Id))
    let names = System.Collections.Generic.Dictionary<IRId, string>()
    for b in modul.Bindings do names.[b.Id] <- b.Name
    for KeyValue (_, f) in funcs do
        for p in f.Params do
            if not (names.ContainsKey p.VarId) then names.[p.VarId] <- p.Name
    let nameOfId (id: IRId) =
        match names.TryGetValue id with
        | true, n -> n
        | _ -> $"__v{id}"
    let showArg (e: IRExpr) =
        match e with
        | IRVar (id, _) -> nameOfId id
        | IRLit (IRLitInt n) -> string n
        | IRLit (IRLitFloat f) ->
            let s = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            if s.Contains "." || s.Contains "E" || s.Contains "N" || s.Contains "I" then s else s + ".0"
        | IRLit (IRLitBool b) -> (if b then "true" else "false")
        | _ -> ".."
    let refsOf (x: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun m -> match m with IRVar (id, _) when id = x -> n <- n + 1 | _ -> ()) e
        n
    /// An extent expression that cannot fail or write (compute elision's rule).
    let rec plainExtent (e: IRExpr) : bool =
        match e with
        | IRLit _ -> true
        | IRVar (id, ty) -> not (Map.containsKey id funcs) && not (isFunctionTyped ty) && not (deferredIds.Contains id)
        | IRParam (_, _, ty) -> not (isFunctionTyped ty)
        | IRExtent (IRVar (id, _), _) -> not (deferredIds.Contains id)
        | IRExtent (IRParam (_, _, ty), _) -> not (isFunctionTyped ty)
        | IRBinOp (_, (IRAdd | IRSub | IRMul), a, b, _) -> plainExtent a && plainExtent b
        | IRUnaryOp (IRNeg, a) -> plainExtent a
        | _ -> false
    let staticExtents (at: IRArrayType) =
        at.IndexTypes |> List.map (fun ix -> match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
    /// The layout a declared return type fixes (elements, index records,
    /// extents), units aside.
    let sameLayout (a: IRType) (b: IRType) =
        match stripUnits a, stripUnits b with
        | ArrayElem x, ArrayElem y ->
            stripUnits x.ElemType = stripUnits y.ElemType
            && x.IndexTypes.Length = y.IndexTypes.Length
            && List.forall2 (fun (i: IRIndexType) (j: IRIndexType) ->
                    i.Rank = j.Rank && i.Symmetry = j.Symmetry && i.IxKind = j.IxKind && i.Extent = j.Extent)
                   x.IndexTypes y.IndexTypes
        | _ -> false
    let userCall (e: IRExpr) =
        match e with
        | CallOfNamed (fid, _, args, _) ->
            (match Map.tryFind fid funcs with
             | Some c when not (c.Name.StartsWith "__lambda_") -> Some (c, args)
             | _ -> None)
        | _ -> None
    /// The calls a statement evaluates (an apply's Loop repeats its operands,
    /// so it is not walked twice).
    let rec countCalls (e: IRExpr) : int =
        match e with
        | IRApplyCombinator info -> (info.Kernel :: info.Arrays) |> List.sumBy countCalls
        | ExprShape (children, _) -> (if (userCall e).IsSome then 1 else 0) + (children |> List.sumBy countCalls)
    let binderIds (e: IRExpr) =
        let acc = System.Collections.Generic.HashSet<IRId>()
        iterIRExpr (fun n -> match n with IRLet (id, _, _) -> acc.Add id |> ignore | _ -> ()) e
        acc |> Set.ofSeq
    let rec unroll (e: IRExpr) =
        match e with
        | IRLet (id, v, b) -> let (ls, t) = unroll b in ((id, v) :: ls, t)
        | t -> ([], t)

    // ---- the callee: Ok (scalar lets, the substituted map, the original map
    // kernel, a capturing kernel's clone, evidence) or Error (the decline) ------
    let ctx : EcContext =
        { modul = modul; builder = builder; pending = pending; minted = minted; enabled = enabled; own = own;
          funcs = funcs; localCallables = localCallables; resolver = resolver; resolve = resolve;
          mayWrite = mayWrite; payloadOf = payloadOf; assigned = assigned; deferredIds = deferredIds;
          deferredW = deferredW; mutableIds = mutableIds; streamedIds = streamedIds;
          referencedAsValue = referencedAsValue; nameOfId = nameOfId; showArg = showArg; refsOf = refsOf;
          plainExtent = plainExtent; staticExtents = staticExtents; sameLayout = sameLayout; userCall = userCall;
          countCalls = countCalls; binderIds = binderIds; unroll = unroll }
    let judge = ecJudge ctx

    // ---- one statement -------------------------------------------------------
    let regionRepro = ref false
    // A KERNEL caller (wave 3 B, P4 remainder (c)): a lambda body is a
    // statement spine like a function body; a kernel with a parallel strategy
    // declines (its body runs on a team).
    let regionKernel = ref false
    let regionParallel = ref false
    // the region (function or lambda body, binding value) readers are counted in
    let regionRoot = ref (IRLit IRLitUnit)
    // `letBound` (P4 remainder (a)): Some (the let's name, evidence) when sv is
    // the consumer of `let x = f(..)` with x substituted by the call.
    let tryElideCore = ecTryElideCore ctx regionKernel regionParallel judge regionRepro

    // A fold whose call is KEPT (declined, or the gate off) still evaluates
    // the call ONCE: both C++ reduce emitters spell an inline call operand at
    // every element read, the bound and the guard, so `reduce(prep(A), k)`
    // called prep n + 1 times (O(n^2)), leaked n + 1 return pools and let a
    // writing fold kernel change what later calls read (functions/561 printed
    // 206 for the snapshot's 12). The call is bound to its own let right
    // before its statement -- exactly `let t = prep(A)` written first; the
    // arguments are atoms and the array operand is the fold's first operand,
    // so no evaluation moves across another. Statement level only (a let of
    // the spine, the tail, a top-level binding's value, an `if` / `match`
    // arm): a fold nested inside a larger expression keeps today's emission
    // (an open gap). NOT gated: it is the language's evaluation count, not an
    // optimization, so opt-diff sees the same tree on both sides.
    // (In a KERNEL body the kept call is left to the C++ emitters' own bind-
    // once, CodeGenExpr.bindInlineArrayCall: a kernel body is an expression
    // there, and the binding sits at the fold's evaluation point.)
    let tryElide (caller: string) (stmtId: IRId option) (sv: IRExpr) : ((IRId * IRExpr) list * IRExpr) option =
        match tryElideCore caller stmtId sv None with
        | Some r -> Some r
        | None ->
            match sv with
            | IRReduce ((IRApp (_, _, rt) as call), k, init)
                when not regionKernel.Value
                     && (userCall call).IsSome && (match stripUnits rt with ArrayElem _ -> true | _ -> false) ->
                let id = builder.FreshId()
                Some ([ (id, call) ], IRReduce (IRVar (id, rt), k, init))
            | _ -> None

    let captured =
        funcs |> Map.fold (fun acc _ f -> f.Captures |> List.fold (fun a cap -> Set.add cap.Id a) acc) Set.empty
    let isArrayTyped (e: IRExpr) = (try (match stripUnits (typeOf e) with ArrayElem _ -> true | _ -> false) with _ -> false)
    let isX (x: IRId) (e: IRExpr) = match e with IRVar (id, _) -> id = x | _ -> false

    // ---- statement spines ----------------------------------------------------
    let rec processSpine (caller: string) (tailId: IRId option) (e: IRExpr) : IRExpr =
        let (letList, tail0) = unroll e
        let lets = letList |> List.map (fun (id, v0) -> (id, nest caller v0)) |> Array.ofList
        let mutable tail = nest caller tail0
        // ---- LET-BOUND SINGLE-READER CALLS (P4 remainder (a)) ---------------
        // `let x = f(..)` whose ONE reader is a later statement of this spine
        // that folds x (`reduce(x, k[, s])`, the whole value) or reads it as one
        // operand of a pointwise map: the call moves to its reader and is
        // jointly lowered there exactly as the inline call would be (the let is
        // dropped). Moving it is unobservable only when nothing between the
        // let and the reader writes (the arguments' snapshot) -- the callee
        // itself is total and write-free by the judge -- and x has no other
        // reader anywhere in its region: no capture, no assignment, no extent
        // a type carries. Any other shape keeps the let (silently, or with a
        // recorded decline once the reader is a fold or a map of x).
        let dropped = System.Collections.Generic.HashSet<int>()
        let preAt = System.Collections.Generic.Dictionary<int, (IRId * IRExpr) list>()
        let rewrittenAt = System.Collections.Generic.HashSet<int option>()
        let mutable preTail : (IRId * IRExpr) list = []
        for i in 0 .. lets.Length - 1 do
            let (xid, xv) = lets.[i]
            match userCall xv with
            | Some (c, args) when isArrayTyped xv ->
                let later = [ for j in i + 1 .. lets.Length - 1 -> (Some j, snd lets.[j]) ] @ [ (None, tail) ]
                match later |> List.tryFind (fun (_, v) -> refsOf xid v > 0) with
                | Some (at, sv) ->
                    let consumerShape =
                        match sv with
                        | ReduceOfNamed (y, _, k, init) when y = xid ->
                            refsOf xid k + (init |> Option.map (refsOf xid) |> Option.defaultValue 0) = 0
                        | IRCompute (IRApplyCombinator h) ->
                            (h.Arrays |> List.filter (isX xid) |> List.length) = 1 && refsOf xid h.Kernel = 0
                        | _ -> false
                    if consumerShape && rewrittenAt.Contains at then
                        recordCallElision
                            $"""{caller}: let __v{xid} = {c.SourceName}({args |> List.map showArg |> String.concat ", "}); its reader -> one fused form"""
                            (Blade.Effects.Declined "several calls in one statement (v1 jointly lowers a statement's only call): its reader already took another let-bound call") []
                    elif consumerShape then
                        let xn = $"__v{xid}"
                        let callDisp = $"""{c.SourceName}({args |> List.map showArg |> String.concat ", "})"""
                        let subj =
                            match sv with
                            | IRReduce _ -> $"{caller}: let {xn} = {callDisp}; reduce({xn}) -> one fused fold"
                            | _ -> $"{caller}: let {xn} = {callDisp}; a pointwise map over {xn} -> one fused nest"
                        let between = [ for j in i + 1 .. (match at with Some j -> j - 1 | None -> lets.Length - 1) -> lets.[j] ]
                        let total = refsOf xid regionRoot.Value
                        let readerProblem =
                            if Set.contains xid captured then Some $"`{xn}` (the call's result) is captured by a kernel or closure"
                            // (an assignable body let is in MutableArrayLets by
                            // default; what proves a write is an assignment
                            // rooted at x -- an element write included -- and
                            // passing x to a `mut` parameter is a second reader)
                            elif assigned.Contains xid then Some $"`{xn}` (the call's result) is assigned"
                            elif embeddedExtentExprs regionRoot.Value |> List.exists (fun x -> refsOf xid x > 0) then
                                Some $"`{xn}` (the call's result) is read by an extent expression a type carries"
                            // (a map's Loop repeats its operands: the reader's
                            // references are all of x's, its consumer shape one slot)
                            elif total <> refsOf xid sv then
                                Some $"`{xn}` (the call's result) has more than one reader ({total} references; a single consumer is required)"
                            else
                            // the arguments' snapshot: nothing between the let
                            // and its reader may write or touch a provider
                            between |> List.tryPick (fun (bid, bv) ->
                                let payload = payloadOf bid bv
                                if intervalHasProviderEffect modul bid || mayWrite payload || (embeddedExtentExprs payload |> List.exists mayWrite) then
                                    Some "an intervening statement may write between the call's let and its reader (the arguments' snapshot would move past the write)"
                                else None)
                        match readerProblem with
                        | Some why -> recordCallElision subj (Blade.Effects.Declined why) []
                        | None ->
                            let sv' = mapIRExpr (fun n -> match n with IRVar (y, _) when y = xid -> xv | n -> n) sv
                            let stmtId = match at with Some j -> Some (fst lets.[j]) | None -> tailId
                            let letEv =
                                [ $"let-bound: `{xn}` = {callDisp} has ONE reader, this statement ({between.Length} intervening statement(s), write-free and provider-free: the arguments' snapshot holds at the reader); the let is dropped and the call jointly lowered at its reader" ]
                            match tryElideCore caller stmtId sv' (Some (xn, letEv)) with
                            | Some (pre, v') ->
                                dropped.Add i |> ignore
                                rewrittenAt.Add at |> ignore
                                match at with
                                | Some j ->
                                    preAt.[j] <- (match preAt.TryGetValue j with | true, ps -> ps @ pre | _ -> pre)
                                    lets.[j] <- (fst lets.[j], v')
                                | None ->
                                    preTail <- preTail @ pre
                                    tail <- v'
                            | None -> ()
                | None -> ()
            | _ -> ()
        let out = ResizeArray<IRId * IRExpr>()
        for i in 0 .. lets.Length - 1 do
            if not (dropped.Contains i) then
                let (id, v) = lets.[i]
                match preAt.TryGetValue i with
                | true, ps -> out.AddRange ps
                | _ -> ()
                match tryElide caller (Some id) v with
                | Some (pre, v') ->
                    out.AddRange pre
                    out.Add((id, v'))
                | None -> out.Add((id, v))
        out.AddRange preTail
        let tail =
            match tryElide caller tailId tail with
            | Some (pre, t') ->
                out.AddRange pre
                t'
            | None -> tail
        Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) out tail
    and nest (caller: string) (v: IRExpr) : IRExpr =
        match v with
        | IRLet _ -> processSpine caller None v
        | IRIf (c, t, f) -> IRIf (c, processSpine caller None t, processSpine caller None f)
        | IRMatch (s, cases) -> IRMatch (s, cases |> List.map (fun cs -> { cs with Body = processSpine caller None cs.Body }))
        | _ -> v

    // A kernel lexically inside a `where repro` body (transitively, through
    // nested kernels) is a repro caller: whether lifting kept the flag on the
    // lambda or not, it keeps the region's contract (fail closed).
    let reproKernels =
        let mutable acc =
            funcs |> Map.fold (fun s _ f -> if f.IsRepro then Set.add f.Id s else s) Set.empty
        let mutable changed = true
        while changed do
            changed <- false
            for KeyValue (_, f) in funcs do
                if acc.Contains f.Id then
                    for id in collectVarRefsIR f.Body do
                        match Map.tryFind id funcs with
                        | Some g when g.Name.StartsWith "__lambda_" && not (acc.Contains id) ->
                            acc <- Set.add id acc
                            changed <- true
                        | _ -> ()
        acc
    let functions' =
        modul.Functions |> List.map (fun f ->
            if f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || hasTypeVarsInSignature f then f
            else
                let isKernel = f.Name.StartsWith "__lambda_"
                let caller =
                    if isKernel && f.SourceName.StartsWith "lambda in " && f.SourceName.Length > 10 then $"a kernel in {f.SourceName.Substring 10}"
                    elif System.String.IsNullOrEmpty f.SourceName then f.Name
                    else f.SourceName
                regionRepro.Value <- f.IsRepro || (isKernel && reproKernels.Contains f.Id)
                regionKernel.Value <- isKernel
                regionParallel.Value <- isKernel && (f.IsOmpParallel || not f.Parallelism.IsEmpty)
                regionRoot.Value <- f.Body
                let body' = processSpine caller None f.Body
                regionRepro.Value <- false
                regionKernel.Value <- false
                regionParallel.Value <- false
                { f with Body = body' })
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            if not (bindingSideOperands modul b.Id).IsEmpty || modul.ProviderWrites.ContainsKey b.Id then b
            else
                regionRoot.Value <- b.Value
                { b with Value = processSpine b.Name (Some b.Id) b.Value })
    if minted.Count = 0 then ({ modul with Functions = functions'; Bindings = bindings' }, List.ofSeq pending)
    else
        let byOrigin = minted |> Seq.groupBy snd |> Seq.map (fun (o, cs) -> (o, cs |> Seq.map fst |> List.ofSeq)) |> Map.ofSeq
        let ownIds = functions' |> List.map (fun f -> f.Id) |> Set.ofList
        let loose = minted |> Seq.filter (fun (_, o) -> not (ownIds.Contains o)) |> Seq.map fst |> List.ofSeq
        ({ modul with
            Functions =
                (functions' |> List.collect (fun f ->
                    match Map.tryFind f.Id byOrigin with
                    | Some cs -> f :: cs
                    | None -> [ f ])) @ loose
            Bindings = bindings'
            DerivedFuncOrigins = minted |> Seq.fold (fun acc (c, o) -> Map.add c.Id o acc) modul.DerivedFuncOrigins },
         List.ofSeq pending)
