// Per-binding-kind statement emission: one rec-chain from genBinding
// through the ~40 genXBinding emitters (compute, provider read/write,
// group-by family, sort/transpose/stack/join, reduce family, guard/
// sequence/let-chain). Atomic by mutual recursion -- do not split further.
module Blade.CodeGenBinding

open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.EmitCpp
open Blade.ReynoldsCore
open Blade.CodeGenState
open Blade.CodeGenExprSupport
open Blade.CodeGenExpr
open Blade.CodeGenLoopNest
open Blade.CodeGenCuda
open Blade.CodeGenFusion

// ---- G35: threaded complete-row folds under the repro build --------------
//
// Under BLADE_FP_REPRO=1 every Path B chunk form is dead (its per-thread
// partials reassociate the fold). What the `where omp` request can still buy
// without touching any chain is CONCURRENT EVALUATION OF INDEPENDENT COMPLETE
// ROWS: when the outer level's value at index i is ONE complete scalar fold
// (its own seed, its own serial chain), thread t evaluates the rows it owns
// whole into `__rslot[i]`, and the outer chain then runs serially, ascending
// from its seed, over the slots. Every chain is the serial walk's own; the
// licence's reorder half stays unspent (ChunkRowSlots in IRStorage.fs).
//
// THE ADMISSION is a CLOSED whitelist, because the threads evaluate rows
// concurrently: `blade_rt::panic` is first-arriving-thread-wins
// (blade_runtime.hpp), so a body that can panic would report a racy failing
// coordinate, and a body that writes anything would race outright. Admitted:
// literals; level coordinates; immutable scalar and array reads; float
// `+ - * /` (IEEE, total) and integer `+ - *` (wrapping, total, formalism
// section 2.4); comparisons and `&&`/`||`; negation; libm intrinsics other
// than lgamma/digamma; `atan2`/`log_base`; casts other than float -> int;
// `if`, `let`, fma; subscripts PROVEN in range (every coordinate a level
// coordinate of exactly the subscripted index type -- a compact pool's group
// or a dense dimension); nested folds over `range<I>` sweeps of static extent;
// calls of resolved callables whose bodies pass the same whitelist. Anything
// else -- integer `/` `%`, a possibly negative integer `^`, a float -> int
// cast, lgamma/digamma, an unproven subscript, a write, an unresolved call,
// any array-level form -- declines with its name.

// The whitelist itself lives in Blade.IRTotality (lane-neutral), so the
// optimizer's sibling-fold pass applies the SAME predicate before it builds a
// join whose team this emitter would otherwise refuse (G7). The aliases keep
// this file's spelling.
let private rowSlotScalarElem = Blade.IRTotality.scalarElem
let private rowSlotRangeIx = Blade.IRTotality.rangeIx
let private rowSlotMul = Blade.IRTotality.mulSat

/// The whitelist walk (Blade.IRTotality.walk) with this lane's resolver and
/// deferred-computation table: Ok (inner fold steps) or Error (first refusal).
/// `internal` so the omp-pragma block can drive it on hand-built IR (names
/// the front end never produces must decline too).
let internal rowSlotWalk (deferred: Map<IRId, IRExpr>) (levels: Map<IRId, IRIndexType>) (levelNames: Map<string, IRIndexType>)
                         (bound: Set<IRId>) (boundNames: Set<string>) (visiting: Set<IRId>) (e: IRExpr) : Result<int64, string> =
    Blade.IRTotality.walk resolveCallable (fun id -> Map.tryFind id deferred) (fun id -> Map.containsKey id deferred)
        levels levelNames bound boundNames visiting e

/// G51: the stored base of a ROW-VIEW inner fold -- the row kernel's value
/// (after its leading lets) is `reduce(u(i), op[, s])` over a row view of a
/// named array -- with the fold's kernel and seed. The whitelist walk
/// (IRTotality's IRReduce arm) owns the detailed proof (stored, dense rank 2,
/// index identity, static extent, total kernel and seed); this only spots the
/// shape, for the admission's write audit and its record.
let private rowViewFoldOf (rowBody: IRExpr) : (IRId * IRType * IRExpr * IRExpr option) option =
    let rec spine (e: IRExpr) =
        match e with
        | IRLet (_, _, b) -> spine b
        | other -> other
    match spine rowBody with
    | IRReduce (IRIndex (IRVar (u, uty), [ _ ], _), k, s) -> Some (u, uty, k, s)
    | _ -> None

/// G51, the licensed arm: the row-view fold a Path B chunk folds with
/// `blade_fold::sum` per row -- the base array (an IRVar), the row subscript
/// (the level coordinate), the inner seed, and the row's static cell count.
type private RowViewSumPlan =
    { Base: IRExpr
      Subscript: IRExpr
      Seed: IRExpr option
      Cells: int64 }

/// G51: the `blade plan` lines of an admitted row-view fold -- what each row
/// is, and the five-party matrix as audited. `form` names the threaded form
/// (the repro row slots, or the licensed vector helper).
let private rowViewFoldMatrix (form: string) (seedHandling: string) : string =
    "five-party audit (shared transitive write analysis + the IRTotality closed whitelist): "
    + "producer = the stored base (not deferred, non-virtual, dense rank 2; no producer evaluation moves) -- no write; "
    + "consumer = the row callable incl. its leading lets and the inner fold callable -- total, no write; "
    + "outer kernel = total (the closed whitelist, its parameters bound as plain values), no write, an explicit omp request on the fold kernel or the outer map level; "
    + $"seeds = {seedHandling}; "
    + $"intervening = none (no cross-binding motion: only this fold's row evaluations move before its outer-kernel applications, every one write-free); form: {form}"

/// The seed party, as both arms audit it.
let private rowViewSeedParty =
    "the inner seed (evaluated once per row in the row's own scope; total, no write) and the outer seed (evaluated once, before the team, in its original scope as written; no write)"

let private rowViewFoldRecord (baseName: string) (seeded: bool) : string list =
    let seedWord =
        if seeded then "its explicit seed, evaluated once per row in the row's own scope"
        else "unseeded: head-peeled from the row's first cell (a statically non-empty row, so no BL8003 can fire)"
    [ $"row-view folds (G51): each row's value is reduce over a ROW VIEW {baseName}(i) of the stored dense rank-2 array {baseName} (rows contiguous; the subscript is the level coordinate of the base's first index type), {seedWord}; each row's chain ascending over the base's second axis on one thread, then the outer chain ascending from its seed: bitwise the serial walk under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off"
      rowViewFoldMatrix "threaded complete rows (repro), the ascending outer combination kept" rowViewSeedParty ]

/// G35 admission for the single-leaf Path B fold nest: Ok (rows, cells, the
/// G51 row-view record lines when the row value is a row-view fold) or Error
/// (the first failed condition, for the `fold-form` decline record). Called
/// only for a `chunkable` plan in a repro build.
///
/// THE FIVE-PARTY WRITE AUDIT (preamble rule 4; the shared transitive write
/// analysis, `exprMayWriteIn` / `callableMayWrite` = Optimize.exprMayWriteWith
/// over the codegen resolver, the deferred table and the program callback
/// table, an unresolved callee counting as a write), applied AFTER the closed
/// totality walk so a refusal keeps the walk's own name for it:
///   producer   -- the stored base of a row view (or the stored arrays a row
///                 reads): never deferred (the walk refuses a deferred read),
///                 so no producer evaluation moves;
///   consumer   -- the whole row callable, its leading lets and (row view) the
///                 inner fold's kernel callable and seed;
///   outer      -- the outer fold kernel (its own explicit request is the
///                 `chunkable` gate; the ascending outer chain is unchanged);
///   seeds      -- the inner seed (inside the row callable) and the OUTER seed,
///                 evaluated once before the team in its original scope;
///   intervening -- nothing outside this fold moves (no cross-binding motion);
///                 inside it, every row's evaluation moves before every outer-
///                 kernel application, and those are the parties above.
/// A writing party declines (the serial walk, as written).
let private rowSlotAdmission (deferred: Map<IRId, IRExpr>) (baseName: IRId -> string) (info: ApplyInfo) (foldCl: IRCallable) (seedExpr: IRExpr) (bindings: LoopIndexBinding list) : Result<int64 * int64 * string list option, string> =
    if not (foldRowSlotsEnabled ()) then Error "disabled by BLADE_FOLD_ROW_SLOTS=0"
    elif not (ompThreadEmissionEnabled ()) then Error (ompThreadsSuppressedReason ())
    elif bindings.Length <> 1 then
        Error "not one complete fold per outer index (a multi-level nest continues ONE chain across the rows)"
    else
    match info.Arrays |> List.map rowSlotRangeIx with
    | [ Some outerIx ] when not info.HasReynolds && not info.IsCoIteration ->
        (match resolveCallable info.Kernel with
         | Some rowCl when rowCl.Params.Length = 1 ->
             let rec spine (e: IRExpr) =
                 match e with
                 | IRLet (_, _, b) -> spine b
                 | other -> other
             let rowView = rowViewFoldOf rowCl.Body
             let isCompleteFold =
                 match spine rowCl.Body with
                 | IRReduceCompute (_, k, s) -> (match k, s with IRTuple _, _ | _, IRTuple _ -> false | _ -> true)
                 // G51: a complete fold over a row view of a stored array
                 // (the walk below proves the rest, or declines with why)
                 | IRReduce (_, IRTuple _, _) -> false
                 | IRReduce _ -> rowView.IsSome
                 | _ -> false
             match rowSlotScalarElem rowCl.RetType with
             | None -> Error "not one complete fold per outer index (the row value is not a scalar)"
             | Some _ when not isCompleteFold ->
                 Error "not one complete fold per outer index (the row value is not a fold)"
             | Some _ ->
                 let p = rowCl.Params.Head
                 let levels = Map.ofList [ p.VarId, outerIx ]
                 let names = Map.ofList [ p.Name, outerIx ]
                 // THE OUTER FOLD KERNEL, audited by the same closed whitelist
                 // (its two parameters bound as plain values): the slots move
                 // EVERY row evaluation before EVERY outer-kernel application,
                 // so a kernel that writes anything a row reads (a captured
                 // array cell; a `comm`-declared serial kernel may, the front
                 // end's write ban covers only parallel bodies) or that has an
                 // unaudited effect would change the operands. A reorder
                 // licence is not a purity certificate.
                 let foldBound = foldCl.Params |> List.map (fun q -> q.VarId) |> Set.ofList
                 let foldNames = foldCl.Params |> List.map (fun q -> q.Name) |> Set.ofList
                 match rowSlotWalk deferred levels names Set.empty Set.empty (Set.singleton rowCl.Id) rowCl.Body,
                       rowSlotWalk deferred Map.empty Map.empty foldBound foldNames (Set.singleton foldCl.Id) foldCl.Body with
                 | Error why, _ -> Error why
                 | _, Error why -> Error $"the outer fold kernel is outside the row-slot whitelist ({why}): its applications cannot move behind every row"
                 | Ok perRow, Ok _ ->
                     // The five-party write audit (see the header), each party
                     // by the shared transitive analysis.
                     let innerKernelWrites =
                         match rowView with
                         | Some (_, _, k, _) ->
                             (match resolveCallable k with
                              | Some cl -> callableMayWrite cl
                              | None -> true)
                         | None -> false
                     if exprMayWriteIn deferred rowCl.Body then
                         Error "the row kernel may write (shared transitive write analysis; an unresolved callee counts as a write): its evaluations cannot move before the outer chain"
                     elif innerKernelWrites then
                         Error "the inner fold's kernel may write (shared transitive write analysis): its applications are ordered side effects"
                     elif callableMayWrite foldCl then
                         Error "the outer fold kernel may write (shared transitive write analysis): its applications cannot move behind every row"
                     elif exprMayWriteIn deferred seedExpr then
                         Error "the outer fold's seed may write (shared transitive write analysis)"
                     else
                     let rows = defaultArg (tryEvalIntIR outerIx.Extent) 0L
                     let cells = rowSlotMul rows perRow
                     let gate = rowSlotsMin ()
                     if cells < gate then
                         Error $"{rows} row(s) x {perRow} fold step(s) = {cells} below rowslots_min = {gate} (BLADE_TILE_TUNE; provisional gate)"
                     else
                     // G51: the row-view record lines -- what the row is, and
                     // the five-party matrix as audited.
                     let rowViewLines =
                         rowView |> Option.map (fun (u, _, _, s) -> rowViewFoldRecord (baseName u) s.IsSome)
                     Ok (rows, cells, rowViewLines)
         | _ -> Error "the row kernel is not a resolved one-parameter callable")
    | _ -> Error "the outer level is not a plain range<I> sweep of static extent (v1 slots range-driven rows)"

/// G7 (the threaded reduction join): the shared traversal's bodies -- every
/// leaf map kernel, every shared-producer kernel, every fold kernel -- audited
/// by the SAME closed purity/totality whitelist the row-slot form uses
/// (`rowSlotWalk`, each callable's parameters bound as plain values). A team
/// moves every cell's evaluation onto some worker and changes which failure
/// arrives first, so an effect (a captured write: the front end's write ban
/// covers only PARALLEL bodies, and a serial map beside an omp fold kernel is
/// not one), a possible panic, an unaudited call or a subscript not proven in
/// range declines the team; the serial join keeps today's semantics. A
/// request is not a purity certificate. Ok () or the first failure.
let private joinThreadAudit (deferred: Map<IRId, IRExpr>) (maps: (string * ApplyInfo) list) (folds: (string * IRCallable) list) : Result<unit, string> =
    // THE SAME predicate the sibling-fold pass applies before it synthesizes a
    // threaded join (Blade.IRTotality.auditApply / auditCallable): a map
    // kernel's parameters bound to a plain `range<I>` operand carry that index
    // type, so a subscript by them into that index space is proven in range.
    let res = resolveCallable
    let dOf (id: IRId) = Map.tryFind id deferred
    let isD (id: IRId) = Map.containsKey id deferred
    let wrap (what: string) (r: Result<unit, string>) =
        match r with
        | Ok () -> Ok ()
        | Error why -> Error $"{what} is outside the closed purity/totality whitelist a team requires ({why}); the serial join keeps its semantics"
    let checks =
        (maps |> List.map (fun (what, info) () -> wrap what (Blade.IRTotality.auditApply res dOf isD info)))
        @ (folds |> List.map (fun (what, cl) () -> wrap what (Blade.IRTotality.auditCallable res dOf isD cl [])))
    checks |> List.fold (fun acc f -> match acc with Ok () -> f () | e -> e) (Ok ())

/// Gaps G41 / G43 at the BACKEND: the observable-write gate of a joined
/// traversal, applied to its vectorized (serial simd) form AND its team. A
/// leaf map, a shared producer or a fold kernel that writes storage outside
/// itself gives the join no form but the ordered serial chains -- lowering
/// already refuses such a join (BL4005, Lowering.refuseWritingJoinLegs); this
/// keeps the emitter sound on its own (and for synthesized joins), and the
/// totality audit (joinThreadAudit) is additional evidence, not a substitute.
let private joinEffectAudit (deferred: Map<IRId, IRExpr>) (maps: (string * ApplyInfo) list) (folds: (string * IRCallable) list) : Result<unit, string> =
    match maps |> List.tryFind (fun (_, info) -> exprMayWriteIn deferred (IRApplyCombinator info)) with
    | Some (what, _) ->
        Error $"{what} writes a captured binding: a joined traversal never reorders, threads or vectorizes an observable write (gaps G41 / G43)"
    | None ->
    match folds |> List.tryFind (fun (_, cl) -> callableMayWrite cl) with
    | Some (what, _) ->
        Error $"{what} writes a captured binding: a joined traversal never reorders, threads or vectorizes an observable write (gaps G41 / G43)"
    | None -> Ok ()
/// THE ROW-OWNED PULL SCATTER's admission (round 3, D-P3; Blade.OrbitFold's
/// pull header and blade_orbit_scatter.hpp leaf3_pull carry the proof),
/// called only for a walk the strict scatter admitted (`sp`, planStrict: rank,
/// symmetry, the closed total whitelist over f and every level's kernel and
/// seed, capture dominance) at the G35 request / depth gate in a threaded
/// strict build. Ok (the record's five-party matrix) or Error (the first
/// failed condition, for the decline record). The parties:
///   * the planner's audit (Optimize.planOrbitPull: the shared transitive
///     write analysis over the module for the pool's producer, the walk, the
///     outer kernel, the outer seed and every intervening binding by its
///     payload) -- a walk it recorded nothing for declines (unknown = refuse);
///   * the TEAM'S BODIES, by the callback-aware codegen analysis
///     (`exprMayWriteIn` / `callableMayWrite`: the program callback table, an
///     unresolved callee a write): the walk, the cell function, every level's
///     fold kernel and seed.
let private orbitPullAdmission (deferred: Map<IRId, IRExpr>) (consumerId: IRId) (sp: Blade.OrbitFold.StrictPlan) (compExpr: IRExpr) : Result<string list, string> =
    let s = sp.Shape
    let (slice, pulls, _) = Blade.OrbitFold.pullCounts sp
    let gate = Blade.OrbitFold.pullMin ()
    if s.Rank <> 3 then Error $"rank {s.Rank}: v1 pulls the rank-3 leaf chains only"
    elif not (Blade.OrbitFold.orbitPullEnabled ()) then Error "disabled by BLADE_ORBIT_PULL=0"
    elif slice + pulls < gate then
        Error $"{slice} slice cells + {pulls} pulls = {slice + pulls} pool reads below pull_min = {gate} (BLADE_TILE_TUNE; provisional gate)"
    else
    match Blade.Optimize.OrbitPullTable.tryAudit consumerId s.SourceId with
    | None ->
        Error $"no five-party audit recorded for this walk over `{s.SourceName}` (the pool is not bound in the fold's own let chain, a parameter of its function, or a top-level binding ahead of the enclosing one): unknown declines"
    | Some (Error why) -> Error why
    | Some (Ok planner) ->
    let level (k: int) = $"level {k + 1}'s"
    let kernelWrites = s.Levels |> List.tryFindIndex (fun l -> callableMayWrite l.Fold)
    let seedWrites = s.Levels |> List.tryFindIndex (fun l -> exprMayWriteIn deferred l.Seed)
    if exprMayWriteIn deferred compExpr then
        Error "the walk may write (callback-aware analysis of the team's bodies; an unresolved callee counts as a write)"
    elif exprMayWriteIn deferred s.Body then
        Error "the cell function may write (callback-aware analysis of the team's bodies)"
    elif kernelWrites.IsSome then
        Error $"{level kernelWrites.Value} fold kernel may write (callback-aware analysis of the team's bodies)"
    elif seedWrites.IsSome then
        Error $"{level seedWrites.Value} seed may write (callback-aware analysis of the team's bodies)"
    else
    Ok (planner
        @ [ "team bodies (the callback-aware codegen write analysis, the program callback table, an unresolved callee a write): the walk, the cell function, every level's fold kernel and seed -- no write; "
            + "totality and purity: planStrict's closed whitelist (Blade.IRTotality) over f, every fold kernel and every seed -- no panic, so no failure can arrive in a different order; "
            + "request: the outer level's (the G35 request / depth gate), threads emitted, every fold strict in this build" ])

/// S2: bridge ONE rank-1 provider to an existing named shared scalar producer
/// and two seeded scalar terminals. This owns no resident allocation: every
/// producer here was already deferred. Admission finishes before any block is
/// emitted, and an unsupported streamed region is a loud complete refusal.
let private tryGenRank1StreamJoin (ctx: CodeGenContext) (binding: IRBinding)
                                 (infos: ApplyInfo list) (sharedIds: (IRId * ApplyInfo) list)
                                 (leafVarIds: IRId option list) (folds: IRCallable list)
                                 (seeds: IRExpr list) (allCgs: LoopNestCodeGen list)
                                 : Result<string list, string> option =
    let streams =
        allCgs |> List.collect (fun cg -> cg.Bindings |> List.collect (_.Elements))
        |> List.map (_.ArrayName) |> List.distinct
        |> List.choose (fun n -> Map.tryFind n ctx.StreamedArrays |> Option.map (fun s -> n, s))
    if not (streams |> List.exists (fun (_, s) -> s.VarType.IndexTypes.Length = 1)) then None
    else
    let refuse why = Some (Error why)
    let scalar64 ty = ty = IRTScalar ETFloat64
    // A deliberately smaller set than the general totality whitelist: literal
    // coefficients, closed scalar +/-/* and nonrecursive closed calls. No
    // captured state, subscripts, effects, runtime casts or array expressions.
    let rec arithmetic (ids: Set<IRId>) (names: Set<string>) (visiting: Set<IRId>) e =
        let go = arithmetic ids names visiting
        match e with
        | IRLit (IRLitFloat _) -> true
        | IRVar (id, ty) -> ids.Contains id && scalar64 ty
        | IRParam (nm, _, ty) -> names.Contains nm && scalar64 ty
        | IRBinOp (_, (IRAdd | IRSub | IRMul), a, b, _) -> go a && go b
        | IRUnaryOp (IRNeg, a) -> go a
        | IRLet (id, v, body) -> go v && arithmetic (ids.Add id) names visiting body
        | IRApp (f, args, _) ->
            match resolveCallable f with
            | Some cl when not (visiting.Contains cl.Id) && cl.Captures.IsEmpty
                           && cl.Params.Length = args.Length && scalar64 cl.RetType
                           && (cl.Params |> List.forall (fun p -> scalar64 p.Type)) ->
                (args |> List.forall go)
                && arithmetic (cl.Params |> List.map (_.VarId) |> Set.ofList)
                              (cl.Params |> List.map (_.Name) |> Set.ofList)
                              (visiting.Add cl.Id) cl.Body
            | _ -> false
        | _ -> false
    let closedKernel (info: ApplyInfo) =
        match resolveCallable info.Kernel with
        | Some cl when cl.Captures.IsEmpty && scalar64 cl.RetType
                       && (cl.Params |> List.forall (fun p -> scalar64 p.Type)) ->
            arithmetic (cl.Params |> List.map (_.VarId) |> Set.ofList)
                       (cl.Params |> List.map (_.Name) |> Set.ofList) (Set.singleton cl.Id) cl.Body
            && (Blade.IRTotality.auditApply resolveCallable (fun _ -> None) (fun _ -> false) info = Ok ())
        | _ -> false
    if streams.Length <> 1 || infos.Length <> 2 || folds.Length <> 2 || sharedIds.Length <> 1 then
        refuse "requires one rank-1 source, one named shared producer and two seeded Float64 legs"
    elif folds |> List.exists (fun cl -> not (scalar64 cl.RetType) || not cl.Captures.IsEmpty
                                        || cl.IsCudaKernel || cl.IsMpiParallel || cl.IsOmpParallel
                                        || not (cl.Params |> List.forall (fun p -> scalar64 p.Type))
                                        || foldKernelBuiltinOp cl <> Some IRAdd) then
        refuse "requires two independently seeded Float64 addition chains"
    elif fpReassocEnabled () && (folds |> List.exists foldReorderLicensed) then
        refuse "a licensed fold requires BLADE_FP_REPRO=1 for the scalar block route"
    elif seeds |> List.exists (function IRLit (IRLitFloat _) | IRUnaryOp (IRNeg, IRLit (IRLitFloat _)) -> false | _ -> true) then
        refuse "requires literal Float64 seeds (no moved seed effects or failures)"
    elif (infos @ (sharedIds |> List.map snd)) |> List.exists (fun i -> not (closedKernel i) || classifyLeafBackend i <> BkSerial) then
        refuse "requires closed pure total Float64 scalar arithmetic on the serial host"
    else
    let sourceName, spec = streams.Head
    match Blade.ZarrProvider.CppZarr.rank1TerminalChunk spec, ctx.StreamTerminalModule with
    | Error why, _ -> refuse why
    | _, None -> refuse "whole-module demand facts are unavailable"
    | Ok (block, diskBytes), Some modul ->
    let srcId = ctx.ProviderReads |> Map.toList |> List.tryPick (fun (id, s) ->
                    if s = spec && Map.tryFind id ctx.VarNames = Some sourceName then Some id else None)
    match srcId with
    | None -> refuse "the source binding/version is not known in this context"
    | Some sourceId ->
    let owned = Set.ofList (sourceId :: ((sharedIds |> List.map fst) @ (leafVarIds |> List.choose id)))
    let region = owned.Add binding.Id
    let refs e =
        Set.union (collectVarRefsIR e)
            (Blade.Optimize.embeddedExtentExprs e |> List.map collectVarRefsIR |> Set.unionMany)
    let typeRefs ty = Blade.Optimize.irTypeExtentExprs ty |> List.map refs |> Set.unionMany
    let demanded ids = not (Set.intersect owned ids).IsEmpty
    let ownedNames = modul.Bindings |> List.filter (fun b -> owned.Contains b.Id) |> List.map (_.Name) |> Set.ofList
    // The shared census includes declared types and their embedded extents.
    let escaped =
        Blade.Optimize.compactionRoots modul.Functions modul |> List.exists (function
            | Blade.Optimize.RootBindingValue b -> not (region.Contains b.Id) && demanded (refs b.Value)
            | Blade.Optimize.RootSideOperand (_, _, e) -> demanded (refs e)
            | Blade.Optimize.RootFunctionBody f -> demanded (refs f.Body) || (f.Captures |> List.exists (fun c -> owned.Contains c.Id))
            | Blade.Optimize.RootType (_, ty) -> demanded (typeRefs ty)
            | Blade.Optimize.RootSourceRef (_, id) -> owned.Contains id
            | Blade.Optimize.RootNameRef (_, nm) -> ownedNames.Contains nm)
    let explicitlyPrinted =
        match printSelection () with
        | None -> false // deferred producers and streams have no default printed form
        | Some names -> modul.Bindings |> List.exists (fun b -> owned.Contains b.Id && names.Contains b.Name)
    let overlaps =
        (region |> Set.exists (fun id ->
            (Blade.Optimize.HotRowTable.tryProducer id).IsSome || (Blade.Optimize.HotRowTable.tryConsumer id).IsSome
            || (Blade.Optimize.CompactionTable.tryFoldMap id).IsSome
            || (Blade.Optimize.CompactRowTable.tryProducer id).IsSome || (Blade.Optimize.CompactRowTable.tryConsumer id).IsSome))
        || (PoolReuseTable.donors () |> Map.exists (fun k v -> region.Contains k || region.Contains v))
    let n = match spec.VarType.IndexTypes.Head.Extent with IRLit (IRLitInt n) -> n | _ -> -1L
    let shapeOk =
        allCgs |> List.forall (fun cg ->
            not cg.HasReynolds && not cg.IsAntisymmetric && not cg.MpiSlab && not cg.OmpRequested
            && cg.Captures.IsEmpty && cg.FoldChunk.IsNone && cg.RowEpilogue.IsNone
            && cg.FlatEpilogue.IsNone && cg.CompactRowEpilogue.IsNone
            && (match cg.Bindings with
                | [ b ] -> b.BoundDependencies.IsEmpty && b.StrictOffset = 0 && b.FusedRank.IsNone
                           && b.Extent = IRLit (IRLitInt n) && not b.Elements.IsEmpty
                           && (b.Elements |> List.forall (fun el -> el.ArrayName = sourceName && el.ArrayRank = 1
                                                                  && el.RankComponent = 0 && el.DimIndex = 0
                                                                  && el.Virtual = RealArray && scalar64 el.ArrayElemType))
                | _ -> false))
    // Hidden payload tables are rejected outright, rather than relying on an
    // expression walk that would miss their source IDs and extent demands.
    let hidden = modul.ProviderReads.Count <> 1 || not modul.ProviderWrites.IsEmpty
                 || not modul.RandomInits.IsEmpty || not modul.CompoundInits.IsEmpty
                 || not modul.SparseInits.IsEmpty || not modul.MutableArrayLets.IsEmpty
                 || not ctx.TilePlans.IsEmpty || not ctx.TileReads.IsEmpty
    // No fallible/effectful statement can move across the original read's
    // validation boundary in a corresponding .read program.
    let sourcePosition = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = sourceId)
    let terminalPosition = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = binding.Id)
    let dominates = match sourcePosition, terminalPosition with Some s, Some t -> s < t | _ -> false
    let barrier =
        modul.Bindings |> List.indexed |> List.exists (fun (position, b) ->
            (match sourcePosition, terminalPosition with Some s, Some t -> s < position && position < t | _ -> true)
            && not (region.Contains b.Id)
            && (match resolveCallable b.Value with
                | Some cl when cl.Captures.IsEmpty -> false
                | _ ->
                    match Blade.IRTotality.walk resolveCallable (fun _ -> None) (fun _ -> false)
                              Map.empty Map.empty Set.empty Set.empty Set.empty b.Value with
                    | Ok _ -> false | Error _ -> true))
    if not dominates || not ctx.ParamIds.IsEmpty then
        refuse "only a dominated top-level terminal has a whole-program source/demand boundary"
    elif escaped || explicitlyPrinted then refuse "the source or a deferred producer has a surviving print, escape, capture, index or extent demand"
    elif hidden then refuse "additional provider/hidden operands, mutation or tile ownership is present"
    elif overlaps then refuse "an existing storage planner already owns a member of the region"
    elif barrier then refuse "an intervening statement has no purity/totality certificate"
    elif not shapeOk then refuse "requires one plain rank-1 scalar traversal and no other scheduling owner"
    else
    let outerInd = indentStr ctx
    let ind = outerInd + "    "
    let i1, i2 = ind + "    ", ind + "        "
    let tag = bindingCppName binding
    let buf, lo, len, hi, q = $"{tag}__input", $"{tag}__lo", $"{tag}__len", $"{tag}__hi", $"{tag}__q"
    let tally = $"{tag}__read"
    let cell = $"{buf}[{q}]"
    let cellLines =
        allCgs |> List.collect (fun cg ->
            let b = cg.Bindings.Head
            let pfn = b.Elements |> List.map (fun el -> el.ParamVarId, cell) |> Map.ofList
            let names = pfn |> Map.fold (fun acc k v -> Map.add k v acc) ctx.VarNames
            // ReynoldsResult consists of CppExpr and two statistics only; it
            // carries no discarded prelude/statements. HasReynolds was refused.
            let rr = genKernelExprWithReynolds cg.KernelExpr cg.KernelParams false false names pfn
            let assignment =
                match cg.ShareDecl, cg.FoldWrapper with
                | Some ty, _ -> $"const {ty} {cg.OutputName} = {rr.CppExpr};"
                | None, Some wrap -> $"{cg.OutputName} = {wrap}({cg.OutputName}, {rr.CppExpr});"
                | _ -> failwith "rank-1 stream join: admitted leaf has no scalar consumer"
            [ i2 + assignment ])
    let rule = Blade.ZarrProvider.missingRuleOf spec.FilePath spec.VarName
    let read = Blade.ZarrProvider.CppZarr.genStreamRowsTerminal spec sourceName buf lo hi tally
               |> List.map (fun s -> i1 + s)
    recordCodegenDecision "stream-terminal" binding.Name Blade.Effects.Applied
        [ "rank1-zarr-joined-scalars: two independent seeded ascending chains; shared scalar producer"
          $"workspace: {block * 8L} converted input bytes + {block * int64 diskBytes} padded disk chunk bytes + constant cursor/validation/fold state; no run table or resident map"
          "provider I/O paths/iostream bookkeeping are outside the payload byte model; missing-value report completes before scalar publication" ]
    Some (Ok (
        [ $"{outerInd}{{"
          $"{ind}// rank1-zarr-joined-scalars: bounded input, shared producer, original ascending seeded chains"
          $"{ind}std::vector<double> {buf}({block}UL);" ]
        @ (Blade.ProviderRegistry.tallyDeclCpp rule tally |> List.map (fun s -> ind + s))
        @ [ $"{ind}for (size_t {lo} = 0; {lo} < {n}UL; ) {{"
            $"{i1}const size_t {len} = std::min((size_t){block}UL, (size_t){n}UL - {lo});"
            $"{i1}const size_t {hi} = {lo} + {len};" ]
        @ read
        @ [ $"{i1}for (size_t {q} = 0; {q} < {len}; ++{q}) {{"
            $"{i2}const size_t {allCgs.Head.Bindings.Head.IndexName} = {lo} + {q};" ]
        @ cellLines
        @ [ $"{i1}}}"; $"{i1}{lo} += {len};"; $"{ind}}}" ]
        @ (Blade.ProviderRegistry.tallyReportCpp rule tally spec.VarName spec.FilePath [ $"{n}UL" ]
           |> List.map (fun s -> ind + s))
        @ [ $"{outerInd}}}" ]))

let rec genBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let (code, ctx') = genBindingDispatch ctx binding builder
    // Gap G42: a binding whose own C++ name gained TupleChildren holds a FLAT
    // tuple (every emitter that registers children for its own name builds
    // `std::make_tuple(<leaves>)` or aliases one). Record its id so an inline
    // projection of it (a function's return, a nested `t[0][1]`) renders by
    // flat leaf position, not by walking the nested IR type.
    (match IR.stripUnits binding.Type with
     | IRTTuple _ as tt when (match Map.tryFind (bindingCppName binding) ctx'.TupleChildren with
                              | Some leaves -> leaves.Length = (IR.flattenTupleLeaves tt).Length
                              | None -> false) ->
         let cell = flatTupleIdsCell ()
         cell.Value <- Set.add binding.Id cell.Value
     | _ -> ())
    (code, ctx')

and genBindingDispatch (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding

    match binding.Value with
    | IRBeginPoolScope call ->
        ([ $"{ind}// A01: begin inline pool scope: {call}" ], addVarName binding.Id name ctx)
    | IRReleasePools (_, pools, call) ->
        (releaseInlinePools ind ctx.VarNames pools call, addVarName binding.Id name ctx)
    | _ when Map.containsKey binding.Id ctx.ProviderReads ->
        genProviderReadBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.ProviderWrites ->
        genProviderWriteBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.RandomInits ->
        match ctx.RandomInits.[binding.Id] with
        | RandGen _ -> genRandGenBinding ctx binding builder
        | FillModulus _ -> genRandomInitBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.CompoundInits ->
        genCompoundInitBinding ctx binding builder
    | _ when Map.containsKey binding.Id ctx.SparseInits ->
        genSparseInitBinding ctx binding
    // Compaction fusion (Blade.Optimize.planCompactionFusion, B4a): the lifted
    // `y * y` whose one reader is a planned fold is never materialized -- the
    // fold (renderCompactionFold) reads `y`'s compact buffer itself.
    | IRCompute (IRApplyCombinator _) when (Blade.Optimize.CompactionTable.tryFoldMap binding.Id).IsSome ->
        let plan = (Blade.Optimize.CompactionTable.tryFoldMap binding.Id).Value
        ([ $"{ind}// {name}: `{plan.CompoundName} * {plan.CompoundName}` is folded straight over `{plan.CompoundName}`'s compact buffer by `{plan.HostName}` (compaction fusion) -- no square pool" ],
         addVarName binding.Id name ctx)
    | IRMask (arrExpr, predExpr) ->
        genMaskBinding ctx binding builder arrExpr predExpr
    | IRIntersect (aExpr, bExpr) ->
        genIntersectBinding ctx binding builder aExpr bExpr
    | IRUnion (aExpr, bExpr) ->
        genUnionBinding ctx binding builder aExpr bExpr
    | IRUnique arrExpr ->
        genUniqueBinding ctx binding builder arrExpr
    | IRRange _ ->
        genRangeBinding ctx binding
    | IRGroupKeys keys ->
        genGroupKeysBinding ctx binding builder keys
    | IRSegments (offsets, _) ->
        genSegmentsBinding ctx binding offsets
    | IRSegmentsGrid bounds ->
        genSegmentsGridBinding ctx binding bounds
    | IRUngroupGrid (g, srcs, bounds) ->
        genUngroupGridBinding ctx binding g srcs bounds
    | IRUngroup (g, src) ->
        genUngroupBinding ctx binding g src
    | IRUngroupRows (rows, offsets, src) ->
        genUngroupRowsBinding ctx binding rows offsets src
    | IRGroupBy (vals, gk) ->
        let (code, ctx') = genGroupByBinding ctx binding builder vals gk
        // Every arm registers name -> grouping in GroupedArrays; the printer
        // needs the same fact by binding id (groupedBindingsCell).
        let name = bindingCppName binding
        (match Map.tryFind name ctx'.GroupedArrays with
         | Some gkName -> noteGroupedBinding binding.Id name gkName
         | None -> ())
        (code, ctx')
    | IRGroupBucket gk ->
        genGroupBucketBinding ctx binding builder gk
    | IRGroupSizes gk ->
        genGroupSizesBinding ctx binding builder gk
    | IRSort (arrExpr, keyExpr) ->
        genSortBinding ctx binding builder arrExpr keyExpr
    | IRTranspose (arrExpr, d1, d2) ->
        genTransposeBinding ctx binding builder arrExpr d1 d2
    | IRStack arrs ->
        genStackJoinBinding ctx binding builder arrs None
    | IRJoin (arrs, dim) ->
        genStackJoinBinding ctx binding builder arrs (Some dim)
    | IRDecompact (arrExpr, d) ->
        genDecompactBinding ctx binding builder arrExpr d
    | IRArrayNegate arrExpr | IRArrayConjugate arrExpr ->
        genArrayNegateConjugateBinding ctx binding builder arrExpr
    | IRGram (_, _, _) ->
        genGramBinding ctx binding builder
    | IRGramApply (_, _, _) ->
        genGramApplyBinding ctx binding builder
    | IRMatmul (_, _) ->
        genMatmulBinding ctx binding builder
    | IREigh _ ->
        genEighBinding ctx binding builder
    | IRSolve (_, _) ->
        genSolveBinding ctx binding builder
    | IRLu _ ->
        genLuBinding ctx binding builder
    | IRLuSolve _ ->
        genLuSolveBinding ctx binding builder
    | IRReduce (arrExpr, kernelExpr, initExpr) when isDeferredWrapperOperand ctx arrExpr ->
        // A named deferred choice / fallback as the fold operand (gap G48):
        // genReduceBinding spells it by name, so materialize it first.
        let (forceCode, ctx, arrExpr') = forceDeferredArrayInput ctx builder ($"{bindingCppName binding}__arr") arrExpr
        let (code, ctx') = genReduceBinding ctx binding builder arrExpr' kernelExpr initExpr
        (forceCode @ code, ctx')
    | IRReduce (arrExpr, kernelExpr, initExpr) ->
        genReduceBinding ctx binding builder arrExpr kernelExpr initExpr
    | IRReduceCompute (compExpr, kernelExpr, seedExpr) ->
        // Hot-row fold scheduling (Blade.Optimize.planHotRowFolds): when the
        // producer's nest ran this fold's row map behind each produced row,
        // the row sums sit in a materialized rank-1 array and this binding is
        // the OUTER combine over them -- the same values, folded in the same
        // order from the same seed, through the ordinary materialized-array
        // fold emitter (which applies this build's fold-form policy to it).
        // A producer whose nest did not admit the epilogue published
        // nothing, and the two-pass form below is unchanged.
        //
        // A7c (round 3, gap G69): a JOIN whose legs rode a rank-1 producer's
        // flat sweep (CodeGenCuda.attachEpilogueLegs published them): the
        // tuple is bound HERE, at the join's own statement, from the legs'
        // finished strict chains -- or, for a licensed leg, from its own
        // seed (evaluated here, as the joined pass did) combined with the
        // lane partial through the leg's wrapper. The same leaf names and
        // tuple as genReduceJoinCore, so the projections are unchanged.
        let legPlan = Blade.Optimize.EpilogueLegTable.tryJoin binding.Id
        match legPlan, kernelExpr, seedExpr with
        | Some lp, IRTuple ks, IRTuple ss when lp.EmittedCpp.Value.IsSome && ks.Length = lp.Legs.Length && ss.Length = lp.Legs.Length ->
            let published = lp.EmittedCpp.Value.Value
            lp.EmittedCpp.Value <- None
            let name = bindingCppName binding
            let leafNames = lp.Legs |> List.map (fun l -> $"{name}_{l.Slot}")
            let lines =
                List.zip3 lp.Legs published (List.zip ks ss)
                |> List.collect (fun (l, (src, isPart), (k, s)) ->
                    let cb =
                        match resolveCallable k with
                        | Some cb when cb.Params.Length = 2 -> cb
                        | _ ->
                            raise (Blade.Diagnostics.BladeDiagnosticException
                                    (Blade.Diagnostics.Codes.iceCodegen "internal: an epilogue-leg join whose leg kernel no longer resolves"))
                    let elemStr = match cb.RetType with IRTScalar et -> primTypeToCpp et | t -> irTypeToCpp t
                    let leaf = $"{name}_{l.Slot}"
                    if isPart then
                        let (wrapperCode, wname) = genCallableWrapper ctx.VarNames $"{name}_j{l.Slot}" cb
                        (wrapperCode |> List.map (fun x -> ind + x))
                        @ [ $"{ind}{elemStr} {leaf} = {exprToCppCtx ctx s};"
                            $"{ind}{leaf} = {wname}({leaf}, {src});   // A7c: leg {l.Name}'s lane partial, fed inside `{lp.ProducerName}`'s producer loop" ]
                    else
                        [ $"{ind}{elemStr} {leaf} = {src};   // A7c: leg {l.Name}'s strict chain ran inside `{lp.ProducerName}`'s producer loop" ])
            recordFoldForm name $"producer-input legs ({lp.Legs.Length}) carried by `{lp.ProducerName}`'s producer loop (epilogue legs)" $"reduction join of {lp.Legs.Length} legs (riding the producer's sweep)"
            let tupleLine = $"""{ind}auto {name} = std::make_tuple({(leafNames |> String.concat ", ")});"""
            let ctx' = addVarName binding.Id name ctx
            let ctxOut = { ctx' with TupleChildren = Map.add name leafNames ctx'.TupleChildren }
            (lines @ [ tupleLine ], ctxOut)
        | _ ->
        (match legPlan with
         | Some lp when not lp.DecidedCpp.Value ->
            lp.DecidedCpp.Value <- true
            Blade.Optimize.recordEpilogueLegs lp.Subject
                (Blade.Effects.Declined "C++ lane: the producer's sweep did not carry the legs (its cell epilogue was not placed in this build: a declined epilogue, a non-flat or dispatched nest, or a flat-path gate)")
                ([ $"""legs {(lp.Legs |> List.map (fun l -> l.Name) |> String.concat ", ")} keep their joined pass""" ]
                 @ (match lp.InlinedCall with
                    | Some call -> [ $"{call} stays inlined (planned for these legs under the OPT-IN BLADE_EPILOGUE_LEGS=1 arm, whose lifetime audit is incomplete, G69); its pools are still released once at the call's return-boundary event (IRReleasePools)" ]
                    | None -> []))
         | _ -> ())
        match Blade.Optimize.HotRowTable.tryConsumer binding.Id with
        // B1 (round-2 cell epilogues): the producer's flat sweep already fed
        // this fold (CodeGenLoopNest.tryGenFlatEpilogueNest published the
        // form). What remains is the CONSUMER's own part, at the consumer's
        // own site: the seed (evaluated HERE, as before -- a P1 partial is
        // seed-independent) and the combine through the fold's wrapper,
        //   lanes   seed, then F(acc, partial)               (OLD serial K-lane arm)
        //   chunks  seed, then F(acc, part[t]) for each non-empty chunk in
        //           thread order, then the partial arrays freed (OLD Path B)
        //   chain   the accumulator the chain carried (P2: the seed is a
        //           literal the producer evaluated; same chain, same order)
        | Some plan when (match plan.Form with Blade.Optimize.ChunkFold -> true | _ -> false)
                         && plan.EmittedCpp.Value.IsSome ->
            let published = plan.EmittedCpp.Value.Value
            // One consumption per publication: a re-emission of the producer
            // publishes again; a stale tag must never reach a later emission.
            plan.EmittedCpp.Value <- None
            let tag = published.Substring(published.IndexOf ':' + 1)
            let formKey = published.Substring(0, published.IndexOf ':')
            let name = bindingCppName binding
            match resolveCallable kernelExpr with
            | Some callable when callable.Params.Length = 2 ->
                let elemStr =
                    match callable.RetType with
                    | IRTScalar et -> primTypeToCpp et
                    | t -> irTypeToCpp t
                let part = $"__cep_part_{tag}"
                let code =
                    match formKey with
                    | "chain" ->
                        recordFoldForm name "serial (repro or unlicensed) chain interleaved into the producer's sweep (cell epilogue)" "reduce over a deferred computation (rank-1 producer epilogue)"
                        [ $"{ind}// cell epilogue: `{name}`'s ordered chain ran inside `{plan.ProducerName}`'s sweep, one step per cell behind its store (Blade.Optimize.planHotRowFolds, rank-1 arm)"
                          $"{ind}{elemStr} {name} = __cep_acc_{tag};" ]
                    | _ ->
                        let (wrapperCode, wname) = genCallableWrapper ctx.VarNames name callable
                        let seedStr = exprToCppCtx ctx seedExpr
                        let decl = (wrapperCode |> List.map (fun s -> ind + s)) @ [ $"{ind}{elemStr} {name} = {seedStr};" ]
                        let kind = match plan.CellChain with Some Blade.Optimize.Square -> VecSq | _ -> VecSum
                        let label = foldVecLabel kind (foldVecLanes kind)
                        if formKey = "chunks" then
                            recordFoldForm name $"threaded(Path B chunks, {label} per chunk, fed behind the producer's stores)" "reduce over a deferred computation (rank-1 producer epilogue)"
                            decl
                            @ [ $"{ind}// cell epilogue: each Path B chunk's lane-tree partial was fed behind `{plan.ProducerName}`'s stores by the thread that produced it; combined here in thread order, seed first, empty chunks skipped"
                                $"{ind}for (int __rt = 0; __rt < __cep_T_{tag}; __rt++) if (__cep_had_{tag}[__rt]) {name} = {wname}({name}, {part}[__rt]);"
                                $"{ind}delete[] {part}; delete[] __cep_had_{tag};" ]
                        else
                            recordFoldForm name $"{label} stream fed behind the producer's stores (cell epilogue)" "reduce over a deferred computation (rank-1 producer epilogue)"
                            decl
                            @ [ $"{ind}// cell epilogue: the lane-tree partial was fed behind `{plan.ProducerName}`'s stores; the seed and the combine stay here"
                                $"{ind}{name} = {wname}({name}, {part});" ]
                (code, addVarName binding.Id name ctx)
            | _ ->
                // Unreachable (buildFlatEpilogue resolved the same kernel);
                // loud rather than a silent two-pass over consumed partials.
                raise (Blade.Diagnostics.BladeDiagnosticException
                        (Blade.Diagnostics.Codes.iceCodegen "internal: a cell-epilogue consumer whose fold kernel no longer resolves"))
        | Some plan when (match plan.Form with Blade.Optimize.RowFold -> true | _ -> false)
                         && plan.EmittedCpp.Value.IsSome ->
            let rsName = plan.EmittedCpp.Value.Value
            let rsVar = IRVar (plan.RowSumId, plan.RowSumType)
            let ctxR = { ctx with VarNames = Map.add plan.RowSumId rsName ctx.VarNames }
            let note =
                [ $"{ind}// hot-row fold: the row values of `{plan.ProducerName}`'s row map were computed behind each produced row into {rsName} (Blade.Optimize.planHotRowFolds); this is the outer combine over them, same order, same seed" ]
            let (code, ctx') = genReduceBinding ctxR binding builder rsVar kernelExpr (Some seedExpr)
            (note @ code, ctx')
        | _ ->
        genReduceComputeBinding ctx binding builder compExpr kernelExpr seedExpr
    | IRProdSum _ ->
        // Scalar result; the expression renderer emits the fused-loop IIFE.
        // That IIFE subscripts every operand BY NAME, so a still-deferred
        // producer must be materialized first (this arm bypasses
        // genScalarExprBinding, which is where the pre-pass normally runs).
        let (forceCode, ctx) = forceDeferredPositionalReads ctx builder ($"{name}__def") binding.Value
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (forceCode @ code, ctx')
    | IRArrayLit (elements, arrType) ->
        let code = genArrayLiteral ctx name binding.Name elements arrType
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    | IRUninit _ ->
        // The let-rec buffer whose zero fill was proved dead (IR.IRUninit):
        // the extents table and the allocation, registered for freeing like
        // any materialized pool -- and no fill nest.
        let matStmts =
            match materializeInlineForm emptySubst ctx.VarNames name (lazy "") binding.Value with
            | Some (s, allocs) -> registerMaterializedAllocs allocs; s
            | None ->
                raise (Blade.Diagnostics.BladeDiagnosticException
                        (Blade.Diagnostics.Codes.iceCodegen
                            "internal: an unfilled recursive-array buffer (IRUninit) of a shape the C++ lane cannot allocate -- the desugar admits only dense, statically shaped buffers"))
        let ctx' = addVarName binding.Id name ctx
        (matStmts |> List.map (fun s -> ind + s), ctx')

    | IRApplyCombinator info ->
        // Defer: computation not materialized until |> compute or combinator forces it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred computation>"], ctx')

    | IRComposeApply _ ->
        // Defer: compose-apply is also a lazy computation; materialized
        // when |> compute reaches it (or a combinator forces it).
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred compose-apply>"], ctx')
    
    | IRParallel _ | IRFusion _ ->
        // Defer: computation combinator not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred computation combinator>"], ctx')
    
    | IRFunctorMap _ ->
        // Defer: functor map not materialized until |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred functor map>"], ctx')
    
    | IRZip _ ->
        // Defer: zip is a lazy array combinator, absorbed by method_for or materialized by |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred zip>"], ctx')
    
    | IRChoice (left, right) ->
        genChoiceBinding ctx binding builder left right
    | IRFallback (left, right) ->
        genFallbackBinding ctx binding builder left right
    | IRGuard (_, body) ->
        genGuardBinding ctx binding builder body
    | IRSequence elems ->
        genSequenceBinding ctx binding builder elems
    // `|> compute` on an ALREADY-EAGER whole-array form is the identity: each
    // of these materializes a fresh pool by construction, so there is nothing
    // deferred left to force. genComputeBinding's dispatch has no arm for any
    // of them and its fall-through treats the value as a SCALAR, so they died
    // on the unsupported-node sentinel -- `let n = -A` worked while
    // `let n = -A |> compute` did not, and `decompact(S, 0) |> compute` was
    // BL7001 at module scope even though the unwrapped spelling was fine.
    // Re-dispatch on the unwrapped form, which the arms above already handle.
    //
    // The set is `isStatementShaped` MINUS the deferring family (whose whole
    // point is that `|> compute` IS the forcing site -- peeling their wrapper
    // would route them back to the emitter that defers, and the compute would
    // become a no-op) and MINUS IRGroupKeys/IRGroupBy (a `|> compute` on a
    // grouping is not a spelling the surface produces, and genComputeBinding
    // has no arm to fall back on if this guess were wrong).
    //
    // WRITTEN AS THAT SUBTRACTION, not as the 18 constructors it works out to.
    // The hand-enumerated version said the same thing in the comment and then
    // re-derived it by hand below, so the two could drift the moment a new
    // statement-shaped node was added: `isStatementShaped` would gain it and
    // this list would not, and the miss is silent (the node falls to
    // genComputeBinding's scalar fall-through and dies on the unsupported-node
    // sentinel -- exactly the BL7001 this arm was added to stop). Both
    // subtrahends are predicates in IR.fs beside `isStatementShaped` itself.
    | IRCompute (IRRange _ as inner) ->
        // `0..n |> compute`: compute IS the forcing request, and the range's
        // own binding arm is the materializer -- re-dispatch on the unwrapped
        // range. (Not statement-shaped, so the subtraction arm below never
        // catches it, and genComputeBinding's fall-through would treat it as
        // a scalar and die on the unsupported-node sentinel.)
        genBinding ctx { binding with Value = inner } builder
    | IRCompute eager when
        isStatementShaped eager && not (isDeferringForm eager) && not (isGroupTableForm eager) ->
        genBinding ctx { binding with Value = eager } builder
    | IRCompute inner ->
        genComputeBinding ctx binding builder inner
    | IRMethodFor _ ->
        // method_for creates a loop object - no runtime code needed
        // Just track the variable name for later use
        let ctx' = addVarName binding.Id name ctx
        ([$"{ind}// {name} = method_for(...) [loop object]"], ctx')
    
    | IRObjectFor _ ->
        // object_for creates a loop object - no runtime code needed.
        // Register the provenance in ObjectLoopBindings (NOT DeferredComputations)
        // so genComposeApply can chase a composed leaf `IRVar(o)` back to this
        // IRObjectFor; without it, `(o1 >>@ o2) <@> A` over let-bound objects
        // emits the object binding name as an undeclared C++ callable.
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with ObjectLoopBindings = Map.add binding.Id binding.Value ctx'.ObjectLoopBindings }
        ([$"{ind}// {name} = object_for(...) [loop object]"], ctx')
    
    | IRApp (IRObjectFor objInfo, args, _) ->
        // Inline application of object_for - need to expand to loop nest
        // This handles cases like: let added = A [+] B
        // Convert to an ApplyCombinator-like structure and generate
        let arrays =
            match args with
            | [IRTuple elems] -> elems
            | _ -> args
        let code = genObjectForApplication ctx name objInfo arrays builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRStructLit _ ->
        // Struct construction -- where-constraint guards are separate
        // IRConstraintCheck bindings synthesized by the checker.
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')
    
    | IRTupleProj (parentExpr, projIdx, isFlat) ->
        genTupleProjBinding ctx binding builder parentExpr projIdx isFlat
    | IRVar (srcId, _) ->
        genVarAliasBinding ctx binding builder srcId
    | IRBind (comp, cont) ->
        genBindChainBinding ctx binding builder comp cont
    | IRTuple _ | IRComplex _ | IRFma _ | IRRandAt _ | IRFieldAccess _ | IRLit _ | IRBinOp _ | IRUnaryOp _ | IRIf _ | IRApp _ | IRParam _ | IRMatch _
    // display.emit is a Bool-valued scalar like the rest of this group -- the
    // frame write is a side effect of evaluating it, and it lands in main()'s
    // BODY, ahead of the timing line and the print block. That position is what
    // the interpreter mirrors (Interp/Run.fs flushes its frame buffer before
    // printBindings), which is what keeps the differential gate happy.
    | IRPure _ | IRIndex _ | IRExtent _ | IRContains _ | IRDisplayEmit _
    // json_array / json_num / json_string answer a String scalar; the same
    // scalar-binding path serves them (their C++ is a single
    // blade_display::json* call).
    | IRDisplayJson _ | IRDisplayNum _ | IRDisplayStr _ ->
        genScalarExprBinding ctx binding builder
    
    | IRCompose _ ->
        // Function composition: uses generic lambdas (auto... args)
        let valueStr = exprToCppCtx ctx binding.Value
        let code = [$"{ind}auto {name} = {valueStr};"]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRComposeObj _ ->
        // Defer: ObjectLoop composition, materialized when applied via <@>
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred compose_obj>"], ctx')

    | IRComposeMeth _ ->
        // Defer: computation composition, materialized when |> compute
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred compose_meth>"], ctx')
    
    | IRLet _ ->
        genLetChainBinding ctx binding builder 
    | IRAssign _ ->
        // Assignment expression: generate as statement. A deferred map READ
        // positionally in it (`x(0) = g(1)`) is forced first, at the
        // statement, as in every other binding. (A write THROUGH a deferred
        // binding is refused at lowering, BL4005, gap G60.)
        let (forceCode, ctx) = forceDeferredPositionalReads ctx builder ($"{name}__def") binding.Value
        let code = [$"{ind}{(exprToCppCtx ctx binding.Value)};"]
        let ctx' = addVarName binding.Id name ctx
        (forceCode @ code, ctx')

    | IRConstraintCheck (cond, blCode, message, span) ->
        // Runtime constraint guard -- the loud-failure idiom (cerr + abort).
        let code =
            [ $$"""{{ind}}if (!({{(exprToCppCtx ctx cond)}})) {"""
              $"{ind}    blade_rt::panic(\"{blCode}\", \"{message}\", {(panicSpanArgs span)});"
              $"{ind}}}" ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRBreakIf cond ->
        // Early exit from the enclosing for-range (the rec-array `while`
        // guard). The per-iteration frees accumulated SO FAR must run before
        // the break -- a naive `break` skips the bottom-of-body frees and
        // leaks whatever this iteration already materialized. Peek, don't
        // pop: the frame stays live for the non-breaking path.
        let frees = peekAllocScopeFrees (ind + "    ")
        let code =
            [ $$"""{{ind}}if ({{(exprToCppCtx ctx cond)}}) {""" ]
            @ frees
            @ [ $"{ind}    break;"
                $"{ind}}}" ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | IRForRange (vid, lo, hi, body) ->
        genForRangeBinding ctx binding builder vid lo hi body
    | other ->
        let ctx' = addVarName binding.Id name ctx
        let nodeType = other.GetType().Name
        // Same classification as exprToCpp's catch-all: no arm for this shape
        // in binding position is a back-end gap, reported as a Blade error.
        recordUnhandledIRNode ($"binding position (binding '{name}')") nodeType
        (codegenError ctx ind ($"unsupported expression for binding '{name}' (IR node: {nodeType})"), ctx')

// Module Generation

/// Generate a function body as a list of C++ statements.
/// Unrolls IRLet chains into sequential variable declarations with a final return.

and genScalarExprBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    match binding.Value with
    // A tuple VALUE holding an array that is still a computation (round-3
    // W1-3) -- `(h |> compute, y |> compute)` returned from a block. Routed
    // BEFORE the whole-expression positional-read pre-pass below: that pass
    // would force a LATER member's producer ahead of an EARLIER member's
    // compute and reverse the first failure. genMemberOrderedTupleBinding
    // evaluates the members left to right instead.
    | IRTuple elems when not (isWhollyDeferredTuple ctx elems) && tupleNeedsMemberOrder ctx binding.Value ->
        genMemberOrderedTupleBinding ctx binding builder
    // An `if` whose branch is such a tuple, or an explicit compute of an
    // unforced array (gap G59): the taken branch is evaluated, member by
    // member, inside its own arm -- never both branches' producers ahead of
    // the condition.
    | IRIf _ when ifNeedsMemberOrder ctx binding.Value ->
        genMemberOrderedIfBinding ctx binding builder
    // A call whose argument is an explicit compute of an unforced array,
    // `f(m1 |> compute, 2.0)` (gap G57): the arguments are evaluated left to
    // right into temporaries before the call.
    | IRApp (_, args, _) when args |> List.exists (fun a -> (explicitComputeTupleMember ctx a).IsSome) ->
        genOrderedCallBinding ctx binding builder
    // An ORDINARY call two or more of whose arguments may fail (gap G79, the
    // call twin of G62's tuple rule): C++17 leaves argument evaluation order
    // unspecified (g++ evaluates right to left), so the LAST failing argument
    // would report first where the interpreter and the llvm lane report the
    // first. The same ordered path binds every evaluating argument at its own
    // position. A call with at most one argument that may fail keeps its
    // single-expression emission, byte for byte.
    | IRApp (_, args, _) when callNeedsArgumentOrder ctx args ->
        genOrderedCallBinding ctx binding builder
    | _ ->
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Force deferred producers read positionally inside a compound scalar
    // expression (`let x = A(0) + A(1)`, `if A(0) > 0.0 then ...`). The direct
    // single-read case (`let x = A(i)`) is handled by the dedicated IRIndex arm
    // below; this pre-pass covers reads nested in binops / ifs / calls / match.
    // After forcing, those bases are no longer in DeferredComputations, so the
    // IRIndex arm's guard falls through to the plain by-name render.
    let (forceCode, ctx) = forceDeferredPositionalReads ctx builder ($"{name}__def") binding.Value
    let prepend (code, ctx') = (forceCode @ code, ctx')
    // Check if it's a tuple of deferred computations
    prepend <|
    match binding.Value with
    | IRTuple elems when isWhollyDeferredTuple ctx elems ->
        // All elements are computations -- defer the whole tuple
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred computation tuple>"], ctx')
    | IRFieldAccess _ when (match binding.Type with ArrayElem _ -> true | _ -> false) ->
        // Struct field of array type: the field itself is already an
        // Array<T,N> / Ragged<T> wrapper (per genStructDef field
        // rendering). Assigning it to a wrapper-typed binding copies
        // the wrapper, which carries its shape via .extents. No
        // companion alias needed.
        let dataCode = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (dataCode, ctx')
    | IRIndex (arrExpr, indices, identity) when (match arrExpr with
                                                 | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
                                                 | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _
                                                 | IRFunctorMap _ | IRComposeMeth _ | IRBind _ | IRCompute _ -> true
                                                 | _ -> false) ->
        // Positional read whose base array is a still-unforced computation
        // (e.g. the PPL formers' row slices `let __ppl_row_A_i = A(i)` over a
        // COMPUTED source array): the emitted C++ indexes the array by NAME
        // (`A.data[i]`), so the producer must be materialized in scope first --
        // the same contract the rearrangement combinators enforce via the
        // shared forceDeferredArrayInput helper.
        let (forceCode, ctx, arrExpr') = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
        let code = genScalarBinding ctx name (IRIndex (arrExpr', indices, identity)) binding.Type
        let ctx' = addVarName binding.Id name ctx
        (forceCode @ code, ctx')
    | _ ->
        // Scalar expressions including tuples, field access, match, bind, pure
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

/// This emitter's view of deferred binders for the SHARED member-order
/// predicates (IRLift.DeferredView; Codex second look of W4-1, finding 4):
/// Lowering.refuseDeferredWritesAndUnorderedIfs judges the same shapes with
/// the same functions over the program's let table.
and deferredViewOf (ctx: CodeGenContext) : IRLift.DeferredView =
    { IsDeferred = (fun id -> Map.containsKey id ctx.DeferredComputations)
      ValueOf = (fun id -> Map.tryFind id ctx.DeferredComputations)
      TypeOfExpr = inferExprType }

/// Every element of a tuple value is a deferred computation (an inline map /
/// fusion, or a name bound to one): the whole tuple is deferred, not rendered.
and isWhollyDeferredTuple (ctx: CodeGenContext) (elems: IRExpr list) : bool =
    IRLift.isWhollyDeferredTupleOf (deferredViewOf ctx) elems

and stripComputeWrappers (e: IRExpr) : IRExpr =
    match e with
    | IRCompute inner -> stripComputeWrappers inner
    | e -> e

and isArrayTyped (t: IRType) : bool =
    match t with
    | ArrayElem _ -> true
    | _ -> false

/// A tuple member that EXPLICITLY computes an array still unforced at this
/// point of the emission: x |> compute over a deferred binding, or a map
/// (compose-apply, functor map, ...) written in place under its |> compute.
/// Returns the computation under the compute wrapper (IRLift, shared).
and explicitComputeTupleMember (ctx: CodeGenContext) (e: IRExpr) : IRExpr option =
    IRLift.explicitComputeMemberOf (deferredViewOf ctx) e

/// A tuple member that is a BARE unforced array -- g naming a deferred map,
/// or a map written in place, with no |> compute. A tuple is a VALUE
/// (formalism 10.2, "Deferred members of a tuple value", gap G61): such a
/// member is evaluated AT THE TUPLE'S CONSTRUCTION, at its own position, as
/// g |> compute would evaluate it -- a copy of the computation into a
/// temporary; the binding it names is not forced (it prints only if a forcing
/// use forces it). Every lane does this now: the interpreter's IRTuple arm
/// (Interp/Core.fs) forces the member there, the llvm lane always did.
and bareDeferredTupleMember (ctx: CodeGenContext) (e: IRExpr) : bool =
    IRLift.bareDeferredMemberOf (deferredViewOf ctx) e

/// A member naming a deferred TUPLE (p in let p = (m1, m2); let q = (p,
/// 1.0), gap G58; through |> compute and deferred aliases): its members.
/// A value tuple evaluates them in place, left to right, as if the deferred
/// tuple had been written there; p itself stays deferred.
and deferredTupleMembers (ctx: CodeGenContext) (e: IRExpr) : IRExpr list option =
    IRLift.deferredTupleMembersOf (deferredViewOf ctx) e

/// A member whose evaluation MAY FAIL (gap G62). Conservative in the
/// direction that matters: only forms that CANNOT panic are cleared -- names,
/// literals, a read of a stored binding, a tuple / field projection or an
/// extent of one, and `+ - * /` arithmetic, comparisons and logic over them
/// (an INTEGER `/` or `%` can divide by zero, an integer `^` can take a
/// negative exponent, a float -> int cast can be out of range: those count);
/// everything else -- a subscript (a checked index), an index cast, a call, a
/// fold, a match, a deferred read -- may fail. A member wrongly cleared would
/// only keep `std::make_tuple`'s unspecified order (the pre-W4-1 emission);
/// one wrongly flagged costs a temporary.
and memberMayFail (ctx: CodeGenContext) (e: IRExpr) : bool =
    // ONE predicate, shared with Lowering's G77 forcing regions
    IRLift.memberMayFailOf (deferredViewOf ctx) e

/// Two or more arguments of a call may fail (gap G79): its arguments must be
/// evaluated left to right, each at its own position (genOrderedCallBinding).
and callNeedsArgumentOrder (ctx: CodeGenContext) (args: IRExpr list) : bool =
    (args |> List.filter (memberMayFail ctx) |> List.length) >= 2

/// The leaves of a (possibly nested) tuple value, left to right.
and tupleLeaves (value: IRExpr) : IRExpr list =
    match value with
    | IRTuple es -> es |> List.collect tupleLeaves
    | e -> [ e ]

/// Some leaf of a (possibly nested) tuple value needs the member-ordered
/// path: an explicit compute of an unforced array, a bare one (G61), a named
/// deferred tuple (G58) -- or MORE THAN ONE leaf may fail (G62): C++17 leaves
/// the evaluation order of `std::make_tuple`'s arguments unspecified (g++
/// evaluates them right to left), so two failing members would report the
/// LAST one's failure where the interpreter and the llvm lane report the
/// first. An ordinary tuple (at most one member that can fail -- every
/// literal / name tuple among them) keeps its single `std::make_tuple` line.
and tupleNeedsMemberOrder (ctx: CodeGenContext) (value: IRExpr) : bool =
    IRLift.tupleNeedsMemberOrderOf (deferredViewOf ctx) value

/// The structural half of tupleNeedsMemberOrder: some leaf is an explicit
/// compute of an unforced array, a bare one (G61), or a named deferred tuple
/// (G58).
and memberNeedsOrder (ctx: CodeGenContext) (e: IRExpr) : bool =
    IRLift.memberNeedsOrderOf (deferredViewOf ctx) e

/// A module- or block-level tuple value that holds an explicit compute of an
/// unforced array (round-3 W1-3; the shape `(h |> compute, y |> compute)`
/// returned from a block). `std::make_tuple` takes every member BY NAME, and
/// a deferred member is only a `// <deferred computation>` comment in the
/// C++ (the BL9002 "'__v53' was not declared" of round-3 fixture 02).
///
/// The members are evaluated LEFT TO RIGHT, each at its own position, exactly
/// as the interpreter's IRTuple arm evaluates them:
///   - an explicit compute is emitted as `let tmp = <member> |> compute` would
///     be: genComputeBinding computes it into a temporary, forcing whatever
///     deferred producers its kernels capture under their OWN names (a compact
///     `comm` map read through a nested walk), so their stores happen and a
///     later reader sees them. A member naming a deferred binding leaves that
///     binding deferred, as `let r = y |> compute` does: the member is a copy
///     of the computation, not the binding (the interpreter prints the same
///     bindings: the deferred one stays unprinted, a capture-forced one prints);
///   - a BARE unforced array member (G61) is evaluated the same way: a tuple
///     is a value, so its members are values at its construction;
///   - a member naming a deferred TUPLE (G58) is replaced by that tuple's
///     members, evaluated here in order;
///   - EVERY other member that evaluates something -- a scalar expression, a
///     positional read of a deferred producer, an eager array op -- is bound to
///     a temporary at its own position, through the ordinary let path, so its
///     own dependencies are forced THERE (the let's positional-read pre-pass)
///     and not hoisted ahead of an earlier member. This is also what fixes the
///     order between two evaluating members: C++17 leaves the evaluation order
///     of `std::make_tuple`'s arguments unspecified (g++ evaluates them right
///     to left), so no evaluating member reaches the call;
///   - a plain name or literal evaluates nothing and stays in place.
/// FAIL CLOSED: a temporary the ordinary let path left DEFERRED (a member
/// shape no arm above evaluates) would reach `std::make_tuple` as an
/// undeclared name; the binding is refused instead (BL7004).
/// Members are judged against the THREADED context: an earlier member's
/// capture force may have materialized a later member's binding under its own
/// name, and then that member reads it by name, as the interpreter reads the
/// memoized cell. All temporaries are minted while THIS let is being
/// rendered, so they share its allocation owner: freed with the tuple, spared
/// when it escapes (setAllocOwner).
and genMemberOrderedTupleBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let pos = ref -1
    let refused = ref false
    let rec go (accCode: string list, accCtx: CodeGenContext) (e: IRExpr) : (string list * CodeGenContext) * IRExpr =
        match e with
        | IRTuple es ->
            let (st, es') =
                es |> List.fold (fun (st, acc) m ->
                    let (st', m') = go st m
                    (st', acc @ [ m' ])) ((accCode, accCtx), [])
            (st, IRTuple es')
        | leaf when (deferredTupleMembers accCtx leaf).IsSome ->
            go (accCode, accCtx) (IRTuple (deferredTupleMembers accCtx leaf).Value)
        | leaf ->
            pos.Value <- pos.Value + 1
            let k = pos.Value
            let bindTemp (v: IRExpr) (ty: IRType) =
                let tmp : IRBinding =
                    { Id = builder.FreshId(); Name = $"{name}__m{k}"; Type = ty; Value = v
                      IsConst = true; IsMutable = false }
                let (code, ctx') = genBinding accCtx tmp builder
                if Map.containsKey tmp.Id ctx'.DeferredComputations then refused.Value <- true
                ((accCode @ code, ctx'), IRVar (tmp.Id, ty))
            match explicitComputeTupleMember accCtx leaf with
            | Some c ->
                let ty = match c with IRVar (_, t) -> t | _ -> inferExprType c
                bindTemp (IRCompute c) ty
            | None when bareDeferredTupleMember accCtx leaf ->
                let ty = match leaf with IRVar (_, t) -> t | _ -> inferExprType leaf
                bindTemp (IRCompute leaf) ty
            | None ->
                // A plain name or literal (under any `|> compute`, the identity
                // on a stored value) stays in place; anything else is bound as
                // written, through the path `let tmp = <member>` takes.
                match stripComputeWrappers leaf with
                | (IRVar _ | IRParam _ | IRLit _) as inert -> ((accCode, accCtx), inert)
                | _ -> bindTemp leaf (inferExprType leaf)
    let ((code, ctxF), value') = go ([], ctx) binding.Value
    if refused.Value then
        (codegenError ctx ind "unevaluated computation used as value - use |> compute",
         addVarName binding.Id name ctx)
    else
        let line = genScalarBinding ctxF name value' binding.Type
        (code @ line, addVarName binding.Id name ctxF)

/// An `if` VALUE one of whose branches (through nested `if`s) is a tuple
/// that needs the member-ordered path, or an explicit compute of an unforced
/// array (gap G59): `if c then (m1 |> compute, m2 |> compute) else (...)`.
/// The expression path rendered `c ? std::make_tuple(m1, m2) : ...` over two
/// names the C++ never declared (BL9002).
and ifNeedsMemberOrder (ctx: CodeGenContext) (value: IRExpr) : bool =
    // STRUCTURAL members only (computes / bare deferred / deferred tuples),
    // never G62's failing-member count; the ONE judgment Lowering's refusal
    // audits too (IRLift.ifNeedsOrderedArms).
    IRLift.ifNeedsOrderedArms (deferredViewOf ctx) value

/// The `if` of ifNeedsMemberOrder, as an immediately-invoked lambda: the
/// condition first (its own positional reads forced at the binding's
/// indent), then ONLY the taken arm, each arm a temporary bound through the
/// ordinary let path inside its own branch -- so a tuple arm takes the
/// member-ordered path (its members left to right) and a nested `if` arm
/// recurses. Nothing an arm materializes escapes it (the arm's context is not
/// threaded out; its forces happen at a nested indent, so they are
/// block-scoped and unprinted, as any nested-scope force), and the untaken
/// arm evaluates nothing -- the interpreter's and the llvm lane's IRIf order.
/// At main's top level the arms' pools live for the program, as every module
/// binding's do. Inside a function or loop frame the shape is REFUSED (their
/// names die with the lambda, so no frame could free them): Lowering refuses
/// it in every lane (BL6002), and this emitter fails closed besides. An arm
/// that reads a deferred computation other than as a copy member is refused at
/// lowering too (gap G59), so an arm forces nothing the print list would have
/// to follow. FAIL CLOSED: an arm the let path left deferred is refused
/// (BL7004).
and genMemberOrderedIfBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    match binding.Value with
    | IRIf (cond, tb, fb) ->
        let (condForce, ctxC) = forceDeferredPositionalReads ctx builder ($"{name}__def") cond
        let armCtx = { ctxC with Indent = ctxC.Indent + 2 }
        // FAIL CLOSED inside an allocation frame (a function / loop body):
        // the arms' pools would have no owner after the branch.
        // Lowering.refuseDeferredWritesAndUnorderedIfs refuses the shape there
        // (BL6002, every lane); this is the emitter's own backstop.
        let inFrame = (currentAllocScope ()).IsSome
        let arm (k: int) (e: IRExpr) =
            let tmp : IRBinding =
                { Id = builder.FreshId(); Name = $"{name}__arm{k}"; Type = binding.Type; Value = e
                  IsConst = true; IsMutable = false }
            let (code, ctxA) = genBinding armCtx tmp builder
            (code, bindingCppName tmp, Map.containsKey tmp.Id ctxA.DeferredComputations)
        let (tCode, tName, tDeferred) = if inFrame then ([], "", true) else arm 0 tb
        let (fCode, fName, fDeferred) = if inFrame then ([], "", true) else arm 1 fb
        if inFrame then
            (codegenError ctx ind "an if value over computed arrays inside a function or loop body has no owner for the taken arm's storage (refused at lowering, BL6002)",
             addVarName binding.Id name ctx)
        elif tDeferred || fDeferred then
            (codegenError ctx ind "unevaluated computation used as value - use |> compute",
             addVarName binding.Id name ctx)
        else
            let retTy =
                match binding.Type with
                | ArrayElem arr -> cppArrayTypeStr arr
                | t -> irTypeToCpp t
            let condStr = exprToCppCtx ctxC cond
            let code =
                condForce
                @ [ $"{ind}// {name}: an `if` value, the taken arm evaluated inside its own branch (gap G59)"
                    $$"""{{ind}}{{retTy}} {{name}} = [&]() -> {{retTy}} {"""
                    $$"""{{ind}}    if ({{condStr}}) {""" ]
                @ tCode
                @ [ $"{ind}        return {tName};"
                    $$"""{{ind}}    } else {""" ]
                @ fCode
                @ [ $"{ind}        return {fName};"
                    $"{ind}    }}"
                    $"{ind}}}();" ]
            (code, addVarName binding.Id name ctxC)
    | _ -> genScalarExprBinding ctx binding builder

/// A call with an argument that EXPLICITLY computes an unforced array,
/// `f(m1 |> compute, 2.0)` (gap G57). The call renders its arguments by name,
/// and the IRCompute wrapper hid `m1` from the positional-read pre-pass's
/// direct-IRVar test (CodeGenFusion.collectDeferredPositionalReads), so the
/// C++ named an undeclared `m1`. The arguments are now evaluated LEFT TO
/// RIGHT before the call: an explicit compute into a temporary (a copy of the
/// computation, as `let tmp = m1 |> compute`: `m1` stays deferred, as in the
/// interpreter and the llvm lane); a BARE deferred array argument forced under
/// its own name at its position, exactly as the pre-pass forces the bare
/// `f(m1)`; an evaluating SCALAR argument bound to a temporary at its
/// position (C++ leaves argument evaluation order unspecified), and so is an
/// evaluating ARRAY argument (a call, an array op, a view, an inline map --
/// materialized there when the let path defers it); only a plain name, literal
/// or parameter stays in the call.
and genOrderedCallBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let name = bindingCppName binding
    match binding.Value with
    | IRApp (f, args, info) ->
        let (code, ctxF, args') =
            args |> List.indexed |> List.fold (fun (accCode, accCtx: CodeGenContext, acc) (k, a) ->
                let bindTemp (v: IRExpr) (ty: IRType) =
                    let tmp : IRBinding =
                        { Id = builder.FreshId(); Name = $"{name}__a{k}"; Type = ty; Value = v
                          IsConst = true; IsMutable = false }
                    let (c, ctx') = genBinding accCtx tmp builder
                    (accCode @ c, ctx', acc @ [ IRVar (tmp.Id, ty) ])
                match explicitComputeTupleMember accCtx a with
                | Some c ->
                    let ty = match c with IRVar (_, t) -> t | _ -> inferExprType c
                    bindTemp (IRCompute c) ty
                | None ->
                    match a with
                    | IRVar (id, _) when Map.containsKey id accCtx.DeferredComputations ->
                        let (c, ctx', a') = forceDeferredArrayInput accCtx builder ($"{name}__a{k}") a
                        (accCode @ c, ctx', acc @ [ a' ])
                    | IRVar _ | IRParam _ | IRLit _ -> (accCode, accCtx, acc @ [ a ])
                    | _ ->
                        match inferExprType a with
                        | ArrayElem _ as ty ->
                            // An EVALUATING array argument (a call, an array
                            // op, a view, an inline map) is bound at its own
                            // position too, through the path `let tmp = <arg>`
                            // takes; one the let path leaves deferred (an inline
                            // map) is then materialized there, as
                            // materializeCompoundOperand does -- so nothing is
                            // left to evaluate inside the call (Codex review of
                            // W4-1, finding 5).
                            let (c1, ctx1, a1) = bindTemp a ty
                            let tmpVar = List.last a1
                            let (c2, ctx2, tmpVar') = forceDeferredArrayInput ctx1 builder ($"{name}__a{k}f") tmpVar
                            (c1 @ c2, ctx2, (List.take (a1.Length - 1) a1) @ [ tmpVar' ])
                        | ty -> bindTemp a ty) ([], ctx, [])
        let (callCode, ctx') = genScalarExprBinding ctxF { binding with Value = IRApp (f, args', info) } builder
        (code @ callCode, ctx')
    | _ -> genScalarExprBinding ctx binding builder

and genGroupKeysBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (keys: IRExpr list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // group_keys: build CSR offsets + permutation from a key array. Three
    // cases, dispatched on (ngroupsOpt, enumValuesOpt) from IRTGroupKeys:
    //   Case 1 -- positional buckets (Idx<N> keys): ngroups known at compile
    //     time, keys are bucket indices in [0, N); stack-allocated counts/
    //     offsets/fill.
    //   Case 2 -- EnumIdx reverse lookup: ngroups known, plus an explicit list
    //     of admissible key values; a __bucket(__v) lambda maps each key to
    //     its position in the values list.
    //   Case 3 -- dynamic discovery: ngroups unknown; builds a key ->
    //     bucket-index unordered_map in first-occurrence order, reused for
    //     counts and permutation. All sizing arrays heap-allocated.
    //
    // C++ ABI, all three cases (consumed by IRGroupBy codegen and method_for
    // ragged-peel paths below): `<name>__ngroups` (size_t, group count),
    // `<name>__offsets` (CSR, length ngroups+1), `<name>__perm` (permutation,
    // length input), `<name>__nsrc` (source row count = the allocated length of
    // __perm), plus case-specific transients not consumed elsewhere.
    // `<name>` itself is a void* sentinel -- state lives in these suffixed
    // symbols, read by name downstream.
    //
    // __perm is allocated at __nsrc but only FILLED to offsets[ngroups]: a row
    // whose key was negative is dropped (negativeKeyDrop) and never named. So
    // __nsrc is not recoverable from the CSR pair, and genGroupBucketBinding --
    // which must answer for every source row, dropped ones included -- needs it
    // carried explicitly.
    //
    // Compound (multi-key) mode (`keys` length >1): dispatch becomes an
    // unordered_map<std::tuple<...>, size_t> keyed by component tuple, each
    // unique tuple its own bucket. Same ABI as the single-key dynamic case, so
    // downstream consumers don't need to know single- vs multi-key. Requires
    // the tuple_hasher helper from nested_array_utilities.hpp.
    // AGGREGATE-ONLY GROUPING (Blade.Optimize.planGroupAggregate, gap G71):
    // a planned grouping runs ONE pass that also advances every peel's state;
    // the handshake flag says to its group_by and peel emissions whether it
    // did (a regime this site does not recognize emits today's CSR instead).
    let gaPlan = Blade.Optimize.GroupAggTable.tryGk binding.Id
    (match gaPlan with Some p -> p.EmittedCpp.Value <- false | None -> ())
    let gaResult =
        match keys, gaPlan with
        | [ singleKey ], Some plan -> genGroupAggKeysBinding ctx binding singleKey plan
        | _ -> None
    match gaResult with
    | Some r -> r
    | None ->
    match keys with
    | [] ->
        let ctx' = addVarName binding.Id name ctx
        (codegenError ctx ind "group_keys with empty key list (should have been caught by typechecker)", ctx')
    | [singleKey] ->
        // Existing single-key path: three sub-cases (positional /
        // EnumIdx / dynamic), dispatched on the binding's
        // IRTGroupKeys (ngroupsOpt, enumValuesOpt).
        let keysName = exprToCppCtx ctx singleKey
        let (elemType, keysElemErrCode) = inferElemTypeStrict ctx ind singleKey "group_keys"
        let elemStr = elemTypeToCpp elemType
        // The key array may be a rank-1 COMPOUND compact view (the
        // mask -> compound -> map chain), which has no .extents / operator[].
        let (keysBound, keysAt) = compactOrDenseSource singleKey keysName
        // Negative keys drop their row from the grouping (see negativeKeyDrop).
        let dropNeg = negativeKeyDrop elemType "__k" ind
        match binding.Type with
        | IRTGroupKeys (outerIdx, _, enumValuesOpt) ->
            let ngroupsOpt =
                match outerIdx.Extent with
                | IRLit (IRLitInt n) -> Some (int n)
                | _ -> None
            match ngroupsOpt, enumValuesOpt with
            | None, _ ->
                // Case 3 -- dynamic ngroups via hash discovery. Builds a key ->
                // bucket-index map in a single discovery pass, then reuses the
                // map for counts/offsets/permutation. Bucket indices are
                // assigned in first-occurrence order: the bucket for a key is
                // its position in the sequence of distinct keys as encountered
                // walking the input left-to-right. This handles sparse keys
                // (e.g. [101, 205, 307]) correctly, unlike a max-key-scan
                // (which would allocate one bucket per integer in [0..max]).
                // Users who want numeric bucket ordering instead of
                // first-occurrence order should annotate with `Idx<N>` to opt
                // into Case 1.
                // Each key pass opens the same way: read the cell into __k,
                // then (numeric keys) skip the row when the key is negative.
                let openPass =
                    [ $$"""{{ind}}for (size_t __ki = 0; __ki < {{keysBound}}; __ki++) {"""
                      $"""{ind}    {elemStr} __k = {(keysAt "__ki")};""" ] @ dropNeg
                let code = List.concat [
                    keysElemErrCode
                    [ $"{ind}// group_keys: dynamic ngroups (hash discovery, {elemStr} keys)"
                      $"{ind}std::unordered_map<{elemStr}, size_t> {name}__lookup;"
                      $"{ind}size_t {name}__ngroups = 0;" ]
                    openPass
                    [ $"{ind}    if ({name}__lookup.find(__k) == {name}__lookup.end()) {name}__lookup[__k] = {name}__ngroups++;"
                      $"{ind}}}"
                      $"{ind}size_t* {name}__counts = new size_t[{name}__ngroups]();" ]
                    openPass
                    [ $"{ind}    {name}__counts[{name}__lookup[__k]]++;"
                      $"{ind}}}"
                      $"{ind}size_t* {name}__offsets = new size_t[{name}__ngroups + 1];"
                      $"{ind}{name}__offsets[0] = 0;"
                      $"{ind}for (size_t __gi = 0; __gi < {name}__ngroups; __gi++) {name}__offsets[__gi + 1] = {name}__offsets[__gi] + {name}__counts[__gi];"
                      $"{ind}size_t* {name}__fill = new size_t[{name}__ngroups]();"
                      $"{ind}size_t* {name}__perm = new size_t[{keysBound}];" ]
                    openPass
                    [ $"{ind}    size_t __g = {name}__lookup[__k];"
                      $"{ind}    {name}__perm[{name}__offsets[__g] + {name}__fill[__g]++] = __ki;"
                      $"{ind}}}"
                      $"{ind}size_t {name}__nsrc = {keysBound}; // source rows (>= offsets[ngroups]; negative keys drop)"
                      $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
                      $"{ind}auto {name}__at = [&](size_t __p) {{ return {name}__perm[__p]; }};"
                      $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets, {name}__perm" ]
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | Some ngroups, None ->
                // Case 1: positional bucketing. keys[i] in [0, ngroups).
                let openPass =
                    [ $$"""{{ind}}for (size_t __ki = 0; __ki < {{keysBound}}; __ki++) {"""
                      $"""{ind}    {elemStr} __k = {(keysAt "__ki")};""" ] @ dropNeg
                let code = List.concat [
                    keysElemErrCode
                    [ $"{ind}// group_keys: {ngroups} groups, positional buckets (Idx<N> keys)"
                      $"{ind}size_t {name}__ngroups = {ngroups};"
                      $$"""{{ind}}size_t {{name}}__counts[{{ngroups}}] = {0};""" ]
                    openPass
                    // A key past the last bucket is not a position of the
                    // key type: abort (BL8006, the interpreter's twin in
                    // ArrayOps.buildGroupKeys) instead of writing past the
                    // stack counts array. Negative keys already dropped.
                    [ $$"""{{ind}}    if ((size_t)__k >= {{ngroups}}) blade_rt::panic("BL8006", "index out of bounds: a group_keys key outside 0 .. {{ngroups - 1}}", {{declPanicArgs ()}});"""
                      $"{ind}    {name}__counts[__k]++;"
                      $"{ind}}}"
                      $"{ind}size_t {name}__offsets[{ngroups + 1}];"
                      $"{ind}{name}__offsets[0] = 0;"
                      $"{ind}for (size_t __gi = 0; __gi < {ngroups}; __gi++) {name}__offsets[__gi + 1] = {name}__offsets[__gi] + {name}__counts[__gi];"
                      $$"""{{ind}}size_t {{name}}__fill[{{ngroups}}] = {0};"""
                      $"{ind}size_t* {name}__perm = new size_t[{keysBound}];" ]
                    openPass
                    [ $"{ind}    size_t __g = (size_t)__k;"
                      $"{ind}    {name}__perm[{name}__offsets[__g] + {name}__fill[__g]++] = __ki;"
                      $"{ind}}}"
                      $"{ind}size_t {name}__nsrc = {keysBound}; // source rows (>= offsets[ngroups]; negative keys drop)"
                      $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
                      $"{ind}auto {name}__at = [&](size_t __p) {{ return {name}__perm[__p]; }};"
                      $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets, {name}__perm" ]
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | Some ngroups, Some values ->
                // Case 2: EnumIdx -- keys are arbitrary integers OR strings,
                // mapped to bucket indices [0, ngroups) via an
                // `unordered_map<K, size_t>` lookup. Each value's bucket
                // index is its position in the EnumIdx values list. The
                // map is `static const` at the enclosing function scope:
                // initialized once on first encounter (thread-safe magic
                // static), reused across every group_keys evaluation in
                // the same call site. An unknown key ABORTS (BL8006, the
                // interpreter's twin in ArrayOps.buildGroupKeys): a key
                // column can come from a provider, and falling through to
                // bucket 0 silently merged it into the first group.
                //
                // Why a map and not a switch / if-chain: dispatch cost
                // scales O(1) instead of O(values) per element. Especially
                // visible for string EnumIdx, where prior if-chains
                // compared each key against every value-literal in turn.
                let bucketEntries =
                    let renderVal v =
                        match v with
                        | EVInt n -> $"{n}LL"
                        | EVString s -> escapeStringLit s
                    values
                    |> List.mapi (fun i v ->
                        $"{{{(renderVal v)}, (size_t){i}}}")
                    |> String.concat ", "
                let bucketMapDecl =
                    $"static const std::unordered_map<{elemStr}, size_t> {name}__bucket_map = {{{bucketEntries}}};"
                let bucketLambdaDecl =
                    $$"""auto {{name}}__bucket = [](const {{elemStr}}& __v) -> size_t { auto it = {{name}}__bucket_map.find(__v); if (it == {{name}}__bucket_map.end()) blade_rt::panic("BL8006", "index out of bounds: a group_keys key outside its EnumIdx", {{declPanicArgs ()}}); return it->second; };"""
                let code = keysElemErrCode @ [
                    $"{ind}// group_keys: {ngroups} groups, EnumIdx reverse lookup (unordered_map dispatch)"
                    $"{ind}size_t {name}__ngroups = {ngroups};"
                    $"{ind}{bucketMapDecl}"
                    $"{ind}{bucketLambdaDecl}"
                    $$"""{{ind}}size_t {{name}}__counts[{{ngroups}}] = {0};"""
                    $$"""{{ind}}for (size_t __ki = 0; __ki < {{keysBound}}; __ki++) {"""
                    $"""{ind}    {name}__counts[{name}__bucket({(keysAt "__ki")})]++;"""
                    $"{ind}}}"
                    $"{ind}size_t {name}__offsets[{ngroups + 1}];"
                    $"{ind}{name}__offsets[0] = 0;"
                    $"{ind}for (size_t __gi = 0; __gi < {ngroups}; __gi++) {name}__offsets[__gi + 1] = {name}__offsets[__gi] + {name}__counts[__gi];"
                    $$"""{{ind}}size_t {{name}}__fill[{{ngroups}}] = {0};"""
                    $"{ind}size_t* {name}__perm = new size_t[{keysBound}];"
                    $$"""{{ind}}for (size_t __ki = 0; __ki < {{keysBound}}; __ki++) {"""
                    $"""{ind}    size_t __g = {name}__bucket({(keysAt "__ki")});"""
                    $"{ind}    {name}__perm[{name}__offsets[__g] + {name}__fill[__g]++] = __ki;"
                    $"{ind}}}"
                    $"{ind}size_t {name}__nsrc = {keysBound}; // source rows (EnumIdx keys never drop, so == offsets[ngroups])"
                    $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
                    $"{ind}auto {name}__at = [&](size_t __p) {{ return {name}__perm[__p]; }};"
                    $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets, {name}__perm"
                ]
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        | _ ->
            let ctx' = addVarName binding.Id name ctx
            (codegenError ctx ind ($"group_keys binding '{name}' has wrong inferred type (expected IRTGroupKeys)"), ctx')
    | multipleKeys ->
        // Compound (multi-key) dispatch: tuple-keyed unordered_map. Each
        // (k1, k2, ...) tuple discovered becomes its own bucket in
        // first-occurrence order -- the dynamic Case 3, generalized to
        // multi-component keys. Tuple type std::tuple<T1, T2, ...>: each Ti
        // must be a hashable scalar (enforced by IRTGroupKeys construction in
        // typecheck); `tuple_hasher` (nested_array_utilities.hpp) applies the
        // canonical hash-combine recipe. C++ ABI is identical to single-key
        // Case 3 (`<name>__ngroups`/`__offsets`/`__perm`, plus compound-
        // specific `__lookup`/`__counts`/`__fill`), so downstream IRGroupBy
        // and method_for ragged-peel paths see a normal CSR structure without
        // knowing whether grouping was compound or scalar.
        
        // Per-key data: C++ name + element type + any err lines.
        let keyData =
            multipleKeys |> List.map (fun k ->
                let kName = exprToCppCtx ctx k
                let (kElem, kErr) = inferElemTypeStrict ctx ind k "group_keys (compound key)"
                (kName, elemTypeToCpp kElem, kErr))
        let keyErrCode = keyData |> List.collect (fun (_, _, e) -> e)
        let keyNames = keyData |> List.map (fun (n, _, _) -> n)
        let tupleTypeStr =
            keyData
            |> List.map (fun (_, t, _) -> t)
            |> String.concat ", "
            |> sprintf "std::tuple<%s>"
        // Use the FIRST key array's length for outer iteration. Typecheck has
        // verified all key arrays share the outer extent. Each component reads
        // through the compound-or-dense accessor, so a compact view works here
        // too.
        let keyAccess = List.map2 (fun k n -> compactOrDenseSource k n) multipleKeys keyNames
        let outerExtent = fst (List.head keyAccess)
        // make_tuple(k1[__ki], k2[__ki], ...) expression.
        let makeTupleAt indexVar =
            keyAccess
            |> List.map (fun (_, at) -> at indexVar)
            |> String.concat ", "
            |> sprintf "std::make_tuple(%s)"
        // A tuple row is dropped when ANY numeric component is negative --
        // the componentwise reading of the negative-key sentinel (a row that
        // no family claims on any axis belongs to no group).
        let dropNegTuple =
            let tests =
                List.map2 (fun k (_, at) ->
                    let (kElem, _) = inferElemTypeStrict ctx ind k "group_keys (compound key)"
                    if List.isEmpty (negativeKeyDrop kElem "__x" ind) then None
                    else Some ($"""{(at "__ki")} < 0""")) multipleKeys keyAccess
                |> List.choose id
            if List.isEmpty tests then []
            else [$"""{ind}    if ({(String.concat " || " tests)}) continue; // negative key component: row belongs to no group"""]
        let openPass =
            [ $$"""{{ind}}for (size_t __ki = 0; __ki < {{outerExtent}}; __ki++) {""" ] @ dropNegTuple
        let code = List.concat [
            keyErrCode
            [ $"{ind}// group_keys: compound dispatch ({multipleKeys.Length}-key tuple), dynamic ngroups via hash discovery"
              $"{ind}std::unordered_map<{tupleTypeStr}, size_t, tuple_hasher> {name}__lookup;"
              $"{ind}size_t {name}__ngroups = 0;" ]
            openPass
            [ $"""{ind}    auto __k = {(makeTupleAt "__ki")};"""
              $"{ind}    if ({name}__lookup.find(__k) == {name}__lookup.end()) {name}__lookup[__k] = {name}__ngroups++;"
              $"{ind}}}"
              $"{ind}size_t* {name}__counts = new size_t[{name}__ngroups]();" ]
            openPass
            [ $"""{ind}    {name}__counts[{name}__lookup[{(makeTupleAt "__ki")}]]++;"""
              $"{ind}}}"
              $"{ind}size_t* {name}__offsets = new size_t[{name}__ngroups + 1];"
              $"{ind}{name}__offsets[0] = 0;"
              $"{ind}for (size_t __gi = 0; __gi < {name}__ngroups; __gi++) {name}__offsets[__gi + 1] = {name}__offsets[__gi] + {name}__counts[__gi];"
              $"{ind}size_t* {name}__fill = new size_t[{name}__ngroups]();"
              $"{ind}size_t* {name}__perm = new size_t[{outerExtent}];" ]
            openPass
            [ $"""{ind}    size_t __g = {name}__lookup[{(makeTupleAt "__ki")}];"""
              $"{ind}    {name}__perm[{name}__offsets[__g] + {name}__fill[__g]++] = __ki;"
              $"{ind}}}"
              $"{ind}size_t {name}__nsrc = {outerExtent}; // source rows (>= offsets[ngroups]; negative components drop)"
              $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
              $"{ind}auto {name}__at = [&](size_t __p) {{ return {name}__perm[__p]; }};"
              $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets, {name}__perm (compound)" ]
        ]
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genComputeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (inner: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Compute unwraps - handle the inner expression
    // Recursive resolver: peels IRFunctorMap wrappers, resolves IRVar through deferred,
    // and handles IRComposeMeth by extracting right's kernel as a functor wrapper.
    // Returns (innerExpr, wrapperFunctions) where wrappers are innermost-first
    let rec resolveComputation (expr: IRExpr) (wrappers: IRExpr list) : IRExpr * IRExpr list =
        match expr with
        | IRVar (id, _) ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some deferred -> resolveComputation deferred wrappers
            | None -> (expr, wrappers)
        | IRCompute inner ->
            // An IRCompute wrap is added at lambda lift time around
            // bodies whose top form is IRApplyCombinator. The wrap is
            // semantically the identity at compute time (force evaluation,
            // which a computation already is). Peel it through here so the
            // downstream dispatch sees the unwrapped form. Without this,
            // a double-wrapped IRCompute(IRCompute(IRApplyCombinator)) --
            // produced when the bind expansion explicitly wraps an
            // already-wrapped continuation body -- would fall through to
            // the generic case below and fail to materialize.
            resolveComputation inner wrappers
        | IRFunctorMap (f, inner) ->
            resolveComputation inner (f :: wrappers)
        | IRGuard (cond, body) ->
            // Resolve through guard: push wrappers into the body
            let (innerResolved, innerWrappers) = resolveComputation body wrappers
            (IRGuard (cond, innerResolved), innerWrappers)
        | IRComposeMeth (left, right) ->
            // @>> : c1 @>> c2 means "at each index, apply c2's kernel to c1's result"
            // The kernel resolves to a callable via the
            // CallablesTable; the returned kernel expression flows
            // into the wrappers list and gets substituted by
            // betaReduce via the same resolution.
            let rec extractInlinableKernel e =
                match e with
                | IRVar (id, _) ->
                    match Map.tryFind id ctx.DeferredComputations with
                    | Some d -> extractInlinableKernel d
                    | None -> None
                | IRApplyCombinator info ->
                    match resolveCallable info.Kernel with
                    | Some _ -> Some info.Kernel
                    | None -> None
                | IRFunctorMap (f, inner) ->
                    match extractInlinableKernel inner with
                    | Some k -> Some (IRCompose (k, f))
                    | None -> None
                | _ -> None
            match extractInlinableKernel right with
            | Some kernel -> resolveComputation left (kernel :: wrappers)
            | None -> (expr, wrappers)  // fallback: leave for IRComposeMeth handler
        | _ -> (expr, wrappers)
    
    let (resolved, functorWrappers) = resolveComputation inner []
    
    // Compose functor wrappers into ApplyInfo kernel if present
    // f <$> (L <@> g) -> L <@> (f . g)
    // Wraps kernel body: lambdaparams -> f(g(params))
    let applyFunctorWrappers (info: ApplyInfo) (wrappers: IRExpr list) : ApplyInfo =
        if wrappers.IsEmpty then info
        else
            // Beta-reduce: substitute wrapper's parameter with inner body
            // f <$> (L <@> g) where f = lambdax -> h(x)
            // becomes L <@> lambdaparams -> h(g(params))
            let betaReduce (wrapper: IRExpr) (body: IRExpr) : IRExpr =
                // Resolve through the CallablesTable. When the
                // wrapper resolves to a single-param callable,
                // substitute the param VarId with `body`. When it
                // doesn't resolve (or arity mismatch), fall back
                // to IRApp form (still correct, just not inlined).
                match resolveCallable wrapper with
                | Some c when c.Params.Length = 1 ->
                    // Substitute param VarId with body expression
                    let paramId = c.Params.[0].VarId
                    let rec subst (expr: IRExpr) =
                        match expr with
                        | IRVar (id, _) when id = paramId -> body
                        | IRVar _ | IRLit _ | IRParam _ -> expr
                        | IRBinOp (m, op, l, r, loc) -> IRBinOp (m, op, subst l, subst r, loc)
                        | IRUnaryOp (op, e) -> IRUnaryOp (op, subst e)
                        | IRIf (c, t, e) -> IRIf (subst c, subst t, subst e)
                        | IRApp (f, args, rt) -> IRApp (subst f, args |> List.map subst, rt)
                        | IRIndex (a, idxs, ty) -> IRIndex (subst a, idxs |> List.map subst, ty)
                        | IRTuple es -> IRTuple (es |> List.map subst)
                        | IRComplex (re, im) -> IRComplex (subst re, subst im)
                        | IRFma (a, b, c) -> IRFma (subst a, subst b, subst c)
                        | IRRandAt (k, args, w) -> IRRandAt (k, List.map subst args, w |> Option.map (fun (x, n) -> (subst x, n)))
                        | IRTupleProj (e, i, flat) -> IRTupleProj (subst e, i, flat)
                        | IRFieldAccess (e, f) -> IRFieldAccess (subst e, f)
                        | IRLet (id, v, b) -> IRLet (id, subst v, subst b)
                        | _ -> expr  // For complex nodes, leave as-is
                    subst c.Body
                | _ ->
                    // Can't beta-reduce (couldn't resolve, or arity
                    // mismatch), fall back to IRApp (IIFE in C++).
                    let retTy =
                        match resolveCallable wrapper with
                        | Some c -> c.RetType
                        | None -> IRTScalar ETFloat64
                    IRApp (wrapper, [body], retTy)
            
            let wrappedKernel =
                // Synthetic-registry construction. Each
                // per-use-site wrap produces a fresh IRCallable with
                // a new builder-allocated id, registers it in the
                // codegen-pass synthetic registry, and returns an
                // IRVar reference. resolveCallable queries both
                // the module's CallablesTable and the synthetic
                // registry, so downstream consumers (buildLoopNest-
                // CodeGen for inline emission, betaReduce for further
                // kernel-fold) see the wrapped body uniformly.
                // `mapKernelInner` peels any `IRReynolds` wrapper,
                // applies the transform to the inner callable, and
                // re-wraps with Reynolds (preserving isAntisymmetric)
                // if it was present.
                let wrapBody (body: IRExpr) =
                    wrappers |> List.fold (fun b w -> betaReduce w b) body
                let buildInline (c: IRCallable) : IRExpr =
                    let synthetic =
                        { c with Id = builder.FreshId()
                                 Body = wrapBody c.Body }
                    registerSyntheticCallable synthetic
                mapKernelInner buildInline info.Kernel
            // Update output type from outermost wrapper's return
            // type, resolving the wrapper through resolveCallable.
            // info.OutputType might otherwise be stale relative to
            // the wrapped kernel, mis-typing downstream sites (the
            // element-type adjustment below, output allocation).
            let newOutputType =
                match wrappers |> List.tryHead with
                | Some w ->
                    match resolveCallable w with
                    | Some c -> c.RetType
                    | None -> info.OutputType
                | None -> info.OutputType
            // If output is an array, update the element type
            let adjustedOutputType =
                match info.OutputType, newOutputType with
                | ArrayElem arr, IRTScalar et -> mkArrayLike { arr with ElemType = IRTScalar et }
                | _ -> newOutputType
            { info with Kernel = wrappedKernel; OutputType = adjustedOutputType }
    
    // THE ROW ORBIT ACTION (round 3, D-P2 / gap G73): a row action over a
    // stored SymIdx<2, n> pool, admitted (Blade.OrbitFold.orbitActionAdmissible),
    // is one pass over the pool's canonical cells filling this binding's pool.
    // Only the direct `map |> compute` spelling (no functor wrapper, no
    // deferred alias) is considered.
    let actionCode =
        match resolved, inner with
        | IRApplyCombinator info, IRApplyCombinator _ when functorWrappers.IsEmpty -> tryGenOrbitActionCpp ctx binding info
        | _ -> None
    match actionCode with
    | Some code -> (code, addVarName binding.Id name ctx)
    | None ->
    match resolved with
    | IRApplyCombinator info ->
        // info.Loop is never a composed-object chain here -- those route
        // through the IRComposeApply arm below.
        let info' = applyFunctorWrappers info functorWrappers
        // Force any DEFERRED method_for inputs first: `let llsq = ll^2` over
        // a deliberately-deferred `ll` (a bare kernel binding with no
        // |> compute) reads ll by NAME inside the emitted nest, so the
        // producer must be materialized (under its own name, dropped from
        // the deferred map) before this nest renders -- exactly what the
        // rearrangement combinators do for their inputs. Recursion through
        // genBinding handles chains: forcing k2i forces llsq/kksq first.
        let (forceCode, ctx) =
            info'.Arrays
            |> List.fold (fun (accCode, accCtx: CodeGenContext) arr ->
                match arr with
                | IRVar (id, _) when Map.containsKey id accCtx.DeferredComputations ->
                    let (fcode, fctx, _) = forceDeferredArrayInput accCtx builder ($"{name}__in{id}") arr
                    (accCode @ fcode, fctx)
                | _ -> (accCode, accCtx)) ([], ctx)
        // Same rule one slot over: a deferred binding the KERNEL closes over
        // is forwarded BY NAME as a capture argument at every call site, so it
        // must be materialized before the nest renders too (see
        // collectDeferredKernelCaptures). Runs after the input forcing so a
        // binding that is both an input and a capture is materialized once.
        let (capForceCode, ctx) =
            collectDeferredKernelCaptures ctx info'.Kernel
            |> forceDeferredBindingIds ctx builder ($"{name}__cap")
        // Hot-row fold scheduling: hand the plan to the apply emitter by the
        // output's NAME, for the duration of this one emission (the
        // function-body twin is CodeGen's IRCompute(IRApplyCombinator) arm).
        (match Blade.Optimize.HotRowTable.tryProducer binding.Id with
         | Some plan -> (hotRowPendingCell ()).[name] <- plan
         | None -> ())
        // Compact row classes (planCompactRowClasses): the same hand-over.
        (match Blade.Optimize.CompactRowTable.tryProducer binding.Id with
         | Some plan -> plan.EmittedCpp.Value <- None; plan.DeclinedCpp.Value <- false; (compactRowPendingCell ()).[name] <- plan
         | None -> ())
        let code =
            try genApplyCombinator ctx name info' builder
            finally
                (hotRowPendingCell ()).Remove name |> ignore
                (compactRowPendingCell ()).Remove name |> ignore
        let ctx' = addVarName binding.Id name ctx
        (forceCode @ capForceCode @ code, ctx')

    | IRComposeApply info ->
        // Slot-inverted apply: (object_for(f) >>@ object_for(g)) <@> A.
        //
        // If functorWrappers is non-empty (the @>> case, where
        // extractInlinableKernel of the right operand surfaced its
        // kernel as a wrapper to apply to the left's result),
        // materialize the compose-apply into a temporary and then
        // emit a separate element-wise stage that applies the
        // wrapped kernel chain. For canonical IRApplyCombinator
        // this is handled by applyFunctorWrappers folding the
        // wrappers into the kernel body; for compose-apply that
        // doesn't apply (no single kernel slot), so we use the
        // stage-on-stage form instead.
        if functorWrappers.IsEmpty then
            let (code, ctx') = genComposeApply ctx name info info.OutputType builder
            let ctx'' = addVarName binding.Id name ctx'
            (code, ctx'')
        else
            let s1Name = $"{name}__wrap_s1"
            let s1Id = builder.FreshId()
            let s1Type = info.OutputType
            let (code1, ctx1) = genComposeApply ctx s1Name info s1Type builder
            let ctx1' = addVarName s1Id s1Name ctx1
            // Build an ApplyInfo whose kernel is the innermost
            // wrapper, then fold the rest on top via the existing
            // wrapper-composition machinery.
            let firstWrapper = List.head functorWrappers
            let restWrappers = List.tail functorWrappers
            let baseInfo = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] firstWrapper binding.Type
            let finalInfo = applyFunctorWrappers baseInfo restWrappers
            let code2 = genApplyCombinator ctx1' name finalInfo builder
            let ctx2 = addVarName binding.Id name ctx1'
            (code1 @ [""] @ code2, ctx2)
    
    | IRComposeMeth (left, right) ->
        // @>> : sequential composition -- compute left, feed result to right's kernel
        // Stage 1: materialize left computation
        let s1Name = $"{name}__s1"
        let s1Id = builder.FreshId()
        
        // Resolve left through deferred
        let rec resolveDeferred e =
            match e with
            | IRVar (id, _) -> 
                match Map.tryFind id ctx.DeferredComputations with
                | Some d -> resolveDeferred d
                | None -> e
            | _ -> e
        let resolvedLeft = resolveDeferred left
        let resolvedRight = resolveDeferred right

        // Extract right's kernel
        let rightKernel = 
            match resolvedRight with 
            | IRApplyCombinator info -> info.Kernel 
            | _ -> resolvedRight
        let rightKernelName = 
            match rightKernel with 
            | IRVar (id, _) -> Map.tryFind id ctx.VarNames 
            | _ -> None
        
        // Materialize left as stage 1
        let s1Type = inferExprType resolvedLeft
        let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedLeft; IsConst = true; IsMutable = false }
        let (code1, ctx1) = genBinding ctx s1Binding builder
        
        match rightKernelName with
        | Some kName ->
            // Right kernel is a named function -- generate element-wise function-call loop
            let arrRank = match s1Type with ArrayElem arr -> arrayRank arr | _ -> 1
            // s1Type comes from the upstream binding; it MUST be an
            // array at this point or the composition is malformed.
            // No `double` fallback: a non-array s1Type indicates a
            // real upstream bug worth diagnosing, not papering over.
            let (elemType, elemTypeErrCode) =
                match s1Type with
                | ArrayElem arr -> (elemTypeToCpp arr.ElemType, [])
                | t ->
                    (elemTypeToCpp (IRTScalar ETFloat64),
                     codegenError ctx ind (sprintf "method composition: left side has non-array type %A (typechecker or IR bug)" t))
            // Deterministic deallocation, site 5a: `@>>` stage 2. Dense/nullptr,
            // with extents borrowed from stage 1, so nothing is owned here. Stage 1
            // was emitted (and registered on its own path) above, so this lands
            // SECOND and is therefore freed FIRST -- which is exactly what keeps the
            // borrowed `.extents` pointer live across this free.
            (match binding.Type with
             | ArrayElem at when isFreeableDenseArrayType at ->
                 registerPoolAlloc AllocDense elemType arrRank "nullptr" (name + "_extents") name None
             | _ -> ())
            // Stage 2 sweeps stage 1's shape; stage 1's own type is what says
            // whether that shape is statically pinned.
            let s2Bound =
                match s1Type with
                | ArrayElem at -> literalOrRuntimeExtentOfArray at s1Name 0
                | _ -> $"{s1Name}.extents[0]"
            let s2Code = [
                $"{ind}const size_t* {name}_extents = {s1Name}.extents;"
                $$"""{{ind}}Array<{{elemType}}, {{arrRank}}> {{name}} = { allocate<typename promote<{{elemType}}, {{arrRank}}>::type, nullptr>({{name}}_extents), {{name}}_extents };"""
                $$"""{{ind}}for (size_t __i0 = 0; __i0 < {{s2Bound}}; __i0++) {"""
                $"{ind}    {name}[__i0] = {kName}({s1Name}[__i0]);"
                $"{ind}}}"
            ]
            let ctx2 = addVarName binding.Id name ctx1
            (code1 @ [""] @ elemTypeErrCode @ s2Code, ctx2)
        | None ->
            // Right kernel is inline lambda -- use buildSimpleApplyInfo path
            let s2Info = buildSimpleApplyInfo [IRVar(s1Id, s1Type)] rightKernel binding.Type
            let code2 = genApplyCombinator ctx1 name s2Info builder
            let ctx2 = addVarName binding.Id name ctx1
            (code1 @ [""] @ code2, ctx2)
    
    | IRBind (comp, cont) ->
        // Monadic bind: c >>= k
        // Stage 1: materialize comp
        let s1Name = $"{name}__s1"
        let s1Id = builder.FreshId()
        
        // Resolve comp through deferred
        let rec resolveDeferred e =
            match e with
            | IRVar (id, _) -> 
                match Map.tryFind id ctx.DeferredComputations with
                | Some d -> resolveDeferred d
                | None -> e
            | _ -> e
        let resolvedComp = resolveDeferred comp
        
        let s1Type = inferExprType resolvedComp
        let s1Binding = { Id = s1Id; Name = s1Name; Type = s1Type; Value = IRCompute resolvedComp; IsConst = true; IsMutable = false }
        let (code1, ctx1) = genBinding ctx s1Binding builder
        
        // Resolve continuation to its underlying callable. Post-3c.4
        // the continuation arrives as IRVar(callableId, _) for any
        // let-bound or inline continuation; resolveCallable handles
        // both the CallablesTable and the synthetic registry.
        let resolvedCont = resolveDeferred cont
        match resolveCallable resolvedCont with
        | Some lInfo when lInfo.Params.Length >= 1 ->
            // Bind lambda parameter to stage 1 result
            let param = lInfo.Params.[0]
            let ctx2 = addVarName param.VarId s1Name ctx1
            
            // Generate code for lambda body as a computation
            let bodyBinding = { Id = binding.Id; Name = name; Type = binding.Type; Value = IRCompute lInfo.Body; IsConst = true; IsMutable = false }
            let (code2, ctx3) = genBinding ctx2 bodyBinding builder
            (code1 @ [""] @ code2, ctx3)
        | _ ->
            // Fallback: continuation not resolvable to callable --
            // generate a function call against whatever cont
            // reference we have.
            let contName = match cont with IRVar (id, _) -> Map.tryFind id ctx.VarNames | _ -> None
            match contName with
            | Some kName ->
                let code = [$"{ind}auto {name} = {kName}({s1Name});"]
                let ctx' = addVarName binding.Id name ctx1
                (code1 @ [""] @ code, ctx')
            | None ->
                let code = genScalarBinding ctx1 name (IRApp(cont, [IRVar(s1Id, s1Type)], binding.Type)) binding.Type
                let ctx' = addVarName binding.Id name ctx1
                (code1 @ [""] @ code, ctx')

    | IRParallel _ ->
        // Parallel composition: recursively generate independent loops, combine as nested pairs
        let (code, _, childrenMap) = genParallelTree ctx name resolved builder
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
        (code, ctx')
    
    | IRFusion _ ->
        // Mandatory fusion: single fused loop nest with all kernels
        let (code, _, childrenMap) = genFusionTree ctx name resolved builder
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with TupleChildren = Map.fold (fun acc k v -> Map.add k v acc) ctx'.TupleChildren childrenMap }
        (code, ctx')
    
    | IRChoice (left, right) ->
        // Computation-level choice: materialize both sides, element-wise combine
        // result[i] = (lhs[i] != 0) ? lhs[i] : rhs[i]
        // If functor wrappers present: f <$> (c1 <|> c2) == (f <$> c1) <|> (f <$> c2)
        let wrapSide side =
            if functorWrappers.IsEmpty then side
            else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) side
        let left' = wrapSide left
        let right' = wrapSide right
        let nameL = $"{name}__lhs"
        let nameR = $"{name}__rhs"
        let idL = builder.FreshId()
        let idR = builder.FreshId()
        let bindingL = { Id = idL; Name = nameL; Type = binding.Type; Value = IRCompute left'; IsConst = true; IsMutable = false }
        let bindingR = { Id = idR; Name = nameR; Type = binding.Type; Value = IRCompute right'; IsConst = true; IsMutable = false }
        let (codeL, ctxL) = genBinding ctx bindingL builder
        let (codeR, ctxR) = genBinding ctxL bindingR builder
        
        let ind = indentStr ctx
        let rank = match binding.Type with ArrayElem arr -> arrayRank arr | _ -> 0
        // Choice `<|>` legitimately handles both array and scalar
        // bindings (rank=0 => scalar). Both cases get their elem type
        // from the binding's resolved type. A type that's neither
        // ArrayElem nor IRTScalar at this point is an upstream
        // typechecker bug.
        let (elemType, elemTypeErrCode) =
            match binding.Type with
            | ArrayElem arr -> (elemTypeToCpp arr.ElemType, [])
            | IRTScalar et -> (primTypeToCpp et, [])
            | t ->
                (elemTypeToCpp (IRTScalar ETFloat64),
                 codegenError ctx ind (sprintf "<|>: binding type is neither array nor scalar (got %A) -- likely a typechecker or IR bug" t))
        
        if rank = 0 then
            // Scalar choice
            let code = [$"{ind}{elemType} {name} = ({nameL} != 0) ? {nameL} : {nameR};"]
            let ctx' = addVarName binding.Id name ctxR
            (codeL @ [""] @ codeR @ [""] @ elemTypeErrCode @ code, ctx')
        else
            // Array choice: allocate result, element-wise combine.
            // Read the source's shape via the wrapper's .extents
            // member; populate name_extents alias for the allocate<>
            // template (which still takes a const size_t*).
            // Array choice: allocate result, element-wise combine. The
            // choice of two same-shaped arrays is rectangular at this layer;
            // pass nullptr for SYMM (a symmetric <|> would need the operand's
            // hoisted symm name, which isn't threaded here -- out of scope and
            // not currently produced). This avoids referencing a nonexistent
            // function-local `nameL_symm` after the symm-hoist refactor.
            let extentsAlias = $"{ind}const size_t* {name}_extents = {nameL}.extents;"
            let allocDecl = $"{ind}Array<{elemType}, {rank}> {name} = {{ allocate<typename promote<{elemType}, {rank}>::type, nullptr>({name}_extents), {name}_extents }};"
            // Deterministic deallocation, site 5b: `<|>` array result. rank > 0
            // here (the scalar arm returned above). The extents alias borrows
            // `<name>__lhs.extents`, so nothing is owned; the operands registered
            // first (genBinding above), so reverse order frees this result before
            // the lhs whose pointer it holds.
            (match binding.Type with
             | ArrayElem at when isFreeableDenseArrayType at ->
                 registerPoolAlloc AllocDense elemType rank "nullptr" (name + "_extents") name None
             | _ -> ())

            // Generate nested loops for element-wise choice
            let mutable loopLines = []
            let mutable depth = ctx.Indent
            let indD d = String.replicate d "    "
            for i in 0 .. rank - 1 do
                // The result's own declared type carries the pinned extents;
                // `name` is the just-allocated output, so its records are the
                // binding's.
                let bound =
                    match binding.Type with
                    | ArrayElem at -> literalOrRuntimeExtentOfArray at name i
                    | _ -> $"{name}.extents[{i}]"
                loopLines <- loopLines @ [$$"""{{(indD depth)}}for (size_t __i{{i}} = 0; __i{{i}} < {{bound}}; __i{{i}}++) {"""]
                depth <- depth + 1
            
            let idxStr = [for i in 0 .. rank - 1 -> $"[__i{i}]"] |> String.concat ""
            let lhsElem = $"{nameL}{idxStr}"
            let rhsElem = $"{nameR}{idxStr}"
            loopLines <- loopLines @ [$"{(indD depth)}{name}{idxStr} = ({lhsElem} != 0) ? {lhsElem} : {rhsElem};"]
            
            for _ in 0 .. rank - 1 do
                depth <- depth - 1
                loopLines <- loopLines @ [$"{(indD depth)}}}"]
            
            let ctx' = addVarName binding.Id name ctxR
            (codeL @ [""] @ codeR @ [""] @ elemTypeErrCode @ [extentsAlias; allocDecl; ""] @ loopLines, ctx')

    | IRFallback (left, right) ->
        // <|:> allocated-fallback materialization (storage-keyed, unlike the
        // value-keyed IRChoice arm above) -- see genFallbackMaterialize.
        // Functor wrappers are not distributed over storage fallback (f <$>
        // (A <|:> B) would need f mapped over both operands' storage): reject.
        if not functorWrappers.IsEmpty then
            let code = codegenError ctx (indentStr ctx) "<$> over a <|:> fallback is not supported; materialize the fallback with |> compute first, then map"
            (code, addVarName binding.Id (bindingCppName binding) ctx)
        else
            genFallbackMaterialize ctx binding builder left right

    | IRGuard (cond, body) ->
        // guard(p, c) |> compute: conditionally execute computation
        // Strategy: wrap the kernel body with the guard condition
        // guard(cond, L <@> f) -> L <@> (lambdaargs -> cond ? f(args) : 0)
        // This allocates the array always but fills with zeros when false
        let isComputation =
            match body with
            | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ -> true
            | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
            | _ -> false
        if isComputation then
            // Resolve the inner computation
            let resolvedBody =
                match body with
                | IRVar (id, _) -> Map.tryFind id ctx.DeferredComputations |> Option.defaultValue body
                | _ -> body
            match resolvedBody with
            | IRApplyCombinator info ->
                // Wrap kernel: lambdaparams -> cond ? kernel_body : 0
                //
                // Resolves the kernel through resolveCallable and
                // routes through the synthetic registry: a fresh
                // callable with a new builder-allocated id holds
                // the conditional-wrapped body, gets registered,
                // and is referenced via IRVar. The original
                // callable in module.Functions is unchanged -- the
                // guard wrap is per-use-site.
                let zeroForReturnType (retTy: IRType) =
                    match retTy with
                    | IRTScalar ETBool -> IRLit (IRLitBool false)
                    | IRTScalar ETInt64 | IRTScalar ETInt32 -> IRLit (IRLitInt 0L)
                    | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> IRLit (IRLitInt 0L)
                    | IRTScalar ETFloat32 -> IRLit (IRLitFloat32 0.0f)
                    | _ -> IRLit (IRLitFloat 0.0)
                let buildGuarded (c: IRCallable) : IRExpr =
                    let zeroVal = zeroForReturnType c.RetType
                    let synthetic =
                        { c with Id = builder.FreshId()
                                 Body = IRIf (cond, c.Body, zeroVal) }
                    registerSyntheticCallable synthetic
                // `mapKernelInner` peels any `IRReynolds` wrapper,
                // applies `buildGuarded` to the inner callable, and
                // re-wraps with Reynolds (preserving isAntisymmetric)
                // if it was present. Before this consolidation the
                // peel was open-coded as `resolveCallable info.Kernel`
                // which returns None on Reynolds-wrapped kernels,
                // silently dropping the guard predicate.
                let wrappedKernel = mapKernelInner buildGuarded info.Kernel
                let guardedInfo = { info with Kernel = wrappedKernel }
                // Apply any functor wrappers
                let finalInfo = applyFunctorWrappers guardedInfo functorWrappers
                let code = genApplyCombinator ctx name finalInfo builder
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
            | _ ->
                // Non-apply computation (parallel, fusion, etc.) -- fall back to scalar guard
                let guardExpr = IRGuard (cond, body)
                let code = genScalarBinding ctx name guardExpr binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        else
            // guard over a NON-computation body. An ARRAY-typed body (a CONCRETE
            // array) gets an element-wise array guard `name[idx] = cond ? body[idx]
            // : 0` -- the predicate is a scalar here (no kernel params), so it holds
            // for every cell; this mirrors the interpreter's guard-over-concrete
            // (cond ? A : zeros). A scalar body keeps the exprToCpp ternary (the
            // historical `(cond ? A : 0.0)` emission was a type error over arrays).
            match binding.Type with
            | ArrayElem arr ->
                let bodyName = $"{name}__gbody"
                let bodyId = builder.FreshId()
                let bodyBinding = { Id = bodyId; Name = bodyName; Type = binding.Type; Value = IRCompute body; IsConst = true; IsMutable = false }
                let (codeB, ctxB) = genBinding ctx bodyBinding builder
                // Materialize the scalar predicate once into its own temp.
                let condName = $"{name}__gcond"
                let condId = builder.FreshId()
                let condBinding = { Id = condId; Name = condName; Type = inferExprType cond; Value = cond; IsConst = true; IsMutable = false }
                let (codeC, ctxC) = genBinding ctxB condBinding builder
                let elemType = elemTypeToCpp arr.ElemType
                let rank = arrayRank arr
                let zeroStr =
                    match arr.ElemType with
                    | IRTScalar ETBool -> "false"
                    | IRTScalar (ETInt64 | ETInt32) -> "0L"
                    | IRTIdxTagged (IRTScalar (ETInt64 | ETInt32), _) -> "0L"
                    | _ -> "0.0"
                let extentsAlias = $"{ind}const size_t* {name}_extents = {bodyName}.extents;"
                let allocDecl = $$"""{{ind}}Array<{{elemType}}, {{rank}}> {{name}} = { allocate<typename promote<{{elemType}}, {{rank}}>::type, nullptr>({{name}}_extents), {{name}}_extents };"""
                // Deterministic deallocation, site 5c: `guard` over a CONCRETE
                // array. Extents borrowed from `<name>__gbody`, which registered
                // above via genBinding, so reverse order frees this result first.
                if isFreeableDenseArrayType arr then
                    registerPoolAlloc AllocDense elemType rank "nullptr" (name + "_extents") name None
                let mutable loopLines = []
                let mutable depth = ctx.Indent
                let indD d = String.replicate d "    "
                for i in 0 .. rank - 1 do
                    // `arr` is the guarded array's type; the result borrows its
                    // shape wholesale (aliased extents, same rank).
                    let bound = literalOrRuntimeExtentOfArray arr name i
                    loopLines <- loopLines @ [$$"""{{(indD depth)}}for (size_t __i{{i}} = 0; __i{{i}} < {{bound}}; __i{{i}}++) {"""]
                    depth <- depth + 1
                let idxStr = [for i in 0 .. rank - 1 -> $"[__i{i}]"] |> String.concat ""
                loopLines <- loopLines @ [$"{(indD depth)}{name}{idxStr} = ({condName}) ? {bodyName}{idxStr} : {zeroStr};"]
                for _ in 0 .. rank - 1 do
                    depth <- depth - 1
                    loopLines <- loopLines @ [$"{(indD depth)}}}"]
                let ctx' = addVarName binding.Id name ctxC
                (codeB @ codeC @ [""] @ [extentsAlias; allocDecl; ""] @ loopLines, ctx')
            | _ ->
                let guardExpr = IRGuard (cond, body)
                let code = genScalarBinding ctx name guardExpr binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')

    | IRSequence elems ->
        // Homogeneous n-ary parallel: each child produces same type
        // Result is array indexed by Idx<N> containing the child results
        // IMPORTANT: each child generates against the original ctx, not accumulated,
        // to prevent one child's output from contaminating another's array resolution.
        let n = elems.Length
        let childNames = elems |> List.mapi (fun i _ -> $"{name}_{i}")
        // The type each child BINDING is emitted with. Shared with the copy nest
        // below, which needs the type of the array that actually exists under
        // `<name>_i` to bake its loop bounds -- deriving it a second time from
        // `elems` would let the nest and the emission disagree.
        let childTypeOf (elem: IRExpr) =
            let wrappedElem =
                if functorWrappers.IsEmpty then elem
                else functorWrappers |> List.fold (fun acc w -> IRFunctorMap(w, acc)) elem
            let ty =
                match wrappedElem with
                | IRApplyCombinator info -> info.OutputType
                | IRComposeApply info -> info.OutputType
                | _ -> inferExprType wrappedElem
            (wrappedElem, ty)
        let childTypes = elems |> List.map (childTypeOf >> snd)
        let (allCode, mergedVarNames) =
            (elems, childNames) ||> List.map2 (fun elem childName ->
                let (wrappedElem, childType) = childTypeOf elem
                let childBinding = { Id = builder.FreshId(); Name = childName; Type = childType; Value = IRCompute wrappedElem; IsConst = true; IsMutable = false }
                genBinding ctx childBinding builder)
            |> List.fold (fun (accCode, accNames) (code, newCtx) ->
                (accCode @ code @ [""], Map.fold (fun a k v -> Map.add k v a) accNames newCtx.VarNames)
            ) ([], ctx.VarNames)
        // Determine child element type and rank
        let childType = inferExprType (List.head elems)
        let (childElemType, childRank, childTypeErrCode) =
            match childType with
            | ArrayElem arr -> (elemTypeToCpp arr.ElemType, arrayRank arr, [])
            | IRTScalar et -> (primTypeToCpp et, 0, [])
            | t ->
                (elemTypeToCpp (IRTScalar ETFloat64), 0,
                 codegenError ctx ind (sprintf "IRSequence: child has non-array, non-scalar type %A (likely a typechecker or IR bug)" t))
        let outerRank = childRank + 1
        // Build extents array: [N, child_extents...].
        //
        // Return-extent ABI (commit 7905b36). This was a frame-local
        // `size_t <name>_extents[R]`, which is fine for a binding consumed in
        // the frame that built it -- the only way an IRSequence could be
        // reached until the unified statement-shaped return arm let one be
        // RETURNED. `Array<T,R>` stores only a POINTER to its extents, so the
        // returned wrapper's shape dangled the instant the frame died, and the
        // auto-printer's first `.extents[0]` read segfaulted. `emitExtentsTable`
        // is the same helper the materialize*Form builders were moved onto:
        // `static constexpr` when every dim is literal (the SCALAR-child case,
        // where the sole extent is the child count) and `new size_t[R]`
        // otherwise -- both outlive the frame.
        //
        // Neither the heap table nor the pool below is registered for freeing.
        // The original reason was that the cells ALIASED the children's storage,
        // so a free here would race the children's own frees -- and, for a
        // sequence RETURNED from a frame, the escape analysis had to spare the
        // children's pools too or it would delete the rows out from under the
        // value that just escaped (corpus sequence-combinators/006). The copy
        // nest below retires that argument: the pool is exclusively this
        // binding's and the children are dead the moment they are copied.
        //
        // Left unregistered anyway, because that is a LEAK and this commit is a
        // correctness fix -- registering is what frees too early, and 006 is the
        // test that would crash rather than merely grow. The follow-up is to
        // report `MatPool (name, childElemType, outerRank, "nullptr", None,
        // ownedExtents)` (what materializeStackForm reports) AND to stop sparing
        // the children, which must land together: sparing them while freeing the
        // outer pool leaks strictly more than today.
        let extentsName = name + "_extents"
        let extentsDims =
            (string n, true)
            :: [for d in 0 .. childRank - 1 -> ($"{(List.head childNames)}.extents[{d}]", false)]
        let (extentsDeclLines, _ownedExtents) = emitExtentsTable ind extentsName outerRank extentsDims
        // ARRAY children: one dense pool of this binding's OWN plus a per-child
        // copy nest -- the same fresh-pool discipline materializeStackForm uses,
        // and for the same two reasons it was moved onto it.
        //
        // The pointer-aliasing assembly this replaces (`out[k] = <name>_k`, one
        // `new T*[n]` skeleton whose cells point into the CHILDREN's pools) is
        // not one contiguous pool, so it violated the invariant `pool_base`
        // documents and every consumer that reads an array through it walked off
        // the end of child 0's pool into uninitialized memory. The reachable
        // case was the assignable-let deep copy (`let b = <a sequence>`, which
        // includes the `__exprN` desugar of a bare top-level expression) and the
        // negate/conjugate transform; BLAS, CUDA and MPI streaming read the same
        // way. Copying also makes the combinator a VALUE: `let` bindings are
        // assignable in Blade, so an aliased sequence would see later writes to
        // any child.
        //
        // It was also the reason `sequence`/`replicate` were rank-1-only -- the
        // skeleton was emitted with exactly one `*` whatever the child rank, so
        // a rank-2 child failed to compile as C++ (`new double*[2]` for an
        // `Array<double,3>`). The nest below carries any child rank.
        //
        // SCALAR children keep their value array: `new T[n]` filled by
        // assignment is already one contiguous pool, which is the invariant.
        let allocDecl =
            if childRank > 0 then
                arrayAlloc { Ind = ind; Elem = childElemType; Rank = outerRank; Name = name
                             Symm = "nullptr"; Strict = None; Extents = extentsName }
            else
                $$"""{{ind}}Array<{{childElemType}}, 1> {{name}} = { new {{childElemType}}[{{n}}], {{name}}_extents };"""
        let assignLines =
            if childRank = 0 then
                childNames |> List.mapi (fun i cn ->
                    $"{ind}{name}[{i}] = {cn};")
            else
                // One nest per child. Loop variables are declared in each `for`
                // init, so sibling nests at the same level reuse the names.
                // Each nest reads its OWN child's extents (TypeCheck has proven
                // the children share a shape at runtime, but not that shape
                // monomorphization pinned them all to literals), so a child that
                // knows its own trip count bakes it whatever its siblings got.
                let loopVar d = $"__sq{name}_{d}"
                (childNames, childTypes)
                ||> List.mapi2 (fun k cn cty ->
                    let boundAt d =
                        match cty with
                        | ArrayElem st -> literalOrRuntimeExtentOfArray st cn d
                        | _ -> $"{cn}.extents[{d}]"
                    let opens =
                        [ for d in 0 .. childRank - 1 ->
                            $"""{ind}{(String.replicate d "    ")}for (size_t {(loopVar d)} = 0; {(loopVar d)} < {(boundAt d)}; {(loopVar d)}++) {{""" ]
                    let sub =
                        [ for d in 0 .. childRank - 1 -> $"[{(loopVar d)}]" ] |> String.concat ""
                    let body =
                        [ $"""{ind}{(String.replicate childRank "    ")}{name}[{k}]{sub} = {cn}{sub};""" ]
                    let closes =
                        [ for d in childRank - 1 .. -1 .. 0 -> $"""{ind}{(String.replicate d "    ")}}}""" ]
                    opens @ body @ closes)
                |> List.concat
        let ctx' = { ctx with VarNames = Map.add binding.Id name mergedVarNames }
        (allCode @ childTypeErrCode @ extentsDeclLines @ [allocDecl] @ assignLines, ctx')
    
    | _ when not functorWrappers.IsEmpty && (match inferExprType resolved with ArrayElem _ -> true | _ -> false) ->
        // `f <$> A` where A is a CONCRETE array (not a computation): materialize as
        // `method_for(A) <@> f` so the functor wrappers apply ELEMENTWISE instead of
        // being dropped (the historical `Array<T,N> name = A;` bug -- the wrappers
        // never reached the kernel). Same treatment `<@>` already gets, and the
        // value-space twin of the interpreter's applyWrappersToValue.
        let firstWrapper = List.head functorWrappers
        let restWrappers = List.tail functorWrappers
        let baseInfo = buildSimpleApplyInfo [resolved] firstWrapper binding.Type
        let finalInfo = applyFunctorWrappers baseInfo restWrappers
        let code = genApplyCombinator ctx name finalInfo builder
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

    | _ ->
        // Other compute expressions - treat as scalar
        let code = genScalarBinding ctx name resolved binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genProviderReadBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // The masked read's hidden raw half (`alias.read_present` / `.present`,
    // ProviderDesugar) is the one read exempt from the provider boundary
    // rule: every emitter it reaches consults ProviderRegistry.MissingPolicy.
    if ctx.ProviderReads.[binding.Id].AllowMissing then
        Blade.ProviderRegistry.MissingPolicy.allowing (fun () -> genProviderReadBindingRuled ctx binding builder)
    else genProviderReadBindingRuled ctx binding builder

and genProviderReadBindingRuled (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Deferred provider read materialized at the `|> alias.read` force point
    // (approach (b)): emit the provider's reader producing `name`, dispatched
    // through the registry on the spec's provider name. A compound
    // (load_compound) read carries a mask; a plain dense read does not.
    let spec = ctx.ProviderReads.[binding.Id]
    let pspec =
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | Some p -> p
        | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' is not registered -- was ProviderStatics.install () run?")))
    if spec.Streamed then
        // Streamed read: emit only the provider's hoisted stream prologue
        // (open handles, fiber extents vector). Consuming nests inline the
        // per-fiber reads via ctx.StreamedArrays; nothing named `name`
        // exists as an array, so any non-nest consumer fails to compile --
        // and the eligible-shape checks in the nest fail loudly first.
        // A RANK-1 stream has no fiber to read by site: it streams per
        // SEGMENT under a structural grouping (docs/plans/structural/07
        // §3.4), through the provider's rows prologue.
        let rank1 = spec.VarType.IndexTypes.Length = 1
        let opener =
            if rank1 then
                match pspec.GenStreamRowsOpen with
                | Some g -> Some g
                | None -> None
            else pspec.GenStreamOpen
        match opener with
        | None ->
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan (
                if rank1 then $"provider '{spec.Provider}' does not support per-segment streamed reads of a rank-1 variable ('{spec.VarName}' -- bind with .read)"
                else $"provider '{spec.Provider}' does not support streamed reads (variable '{spec.VarName}' -- bind with .read)")))
        | Some gen ->
            let code = gen spec.FilePath spec.VarName name spec.VarType
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with StreamedArrays = Map.add name spec ctx'.StreamedArrays }
            // Nothing named `name` exists: a render of this binding as a
            // value from here on is refused, not handed to g++.
            noteStreamedBinding binding.Id name
            (code |> List.map (fun s -> ind + s), ctx')
    else
    // A wreath group passes the `Symmetry <> SymNone && Rank >= 2` packed test
    // (correctly -- it IS packed) but must NOT take the Array<T,N> + pool-copy
    // materialization below: a wreath array's in-memory storage is a bare flat
    // `T*` of orb_cell_count cells (IRStorage.classifyOutputStorage's AllocWreath, the
    // same shape genWreathApply allocates), not a skeleton-backed Array. It gets
    // its own arm, and a provider that does not store wreath pools
    // (ReadWreathPool = None -- csv, netcdf) still refuses here.
    match spec.VarType.IndexTypes |> List.tryFind (fun ix -> ix.Symmetry = SymWreath) with
    | Some ix when pspec.ReadWreathPool.IsNone ->
        failwith (orbitStorageUnsupported
                      ($"provider read of '{spec.VarName}' (provider '{spec.Provider}' stores no OrbIdx pools)")
                      (orbitLevelsOf ix))
    | Some ix ->
        // The store's pool IS the storage, so the whole read is: assemble the
        // chunks into `<name>_flat` (the provider's ordinary flat walk, which
        // knows nothing about wreaths -- the layout attribute already pinned
        // the class at metadata parse) and adopt that buffer as the array.
        // No copy, no unlinearize, no reorder: spec_version 2 defines exactly
        // one on-disk order and it is the one the pool is in.
        if spec.Window.IsSome then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"z.read_window over an OrbIdx (iterated-wreath) pool ('{spec.VarName}') is not supported: a wreath class has no translated sub-class to window into")))
        match classifyOutputStorage binding.Type, pspec.GenReadPacked with
        | AllocWreath (levels, n, cells), Some gen ->
            let elemCpp =
                match binding.Type with
                | ArrayElem at -> elemTypeToCpp at.ElemType
                | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.iceCodegen ($"wreath provider read '{spec.VarName}': binding is not array-typed")))
            let opts : Blade.ProviderRegistry.PackedReadOpts = { Distribute = false; Window = None }
            let assemble = gen spec.FilePath spec.VarName name spec.VarType opts
            // Same runtime pin genWreathApply emits: the C++ section 4 fold and the
            // F# one (OrbRank.cellCountChecked, which is also what validated
            // the store's pool length at metadata parse) are independent
            // implementations, and a disagreement is a wrong-sized pool that
            // nothing on the value side could notice.
            let pin =
                [ $"if (orbit_wreath_utilities::orb_cell_count<{(orbLevelArgs levels)}>({n}) != {cells}LL) {{ blade_rt::panic(\"BL8004\", \"OrbIdx pool size disagreement: orb_cell_count vs the store's declared cardinality\", nullptr, 0); }}" ]
            registerAlloc (RawAlloc (name, None))
            let adopt =
                [ $"// OrbIdx{(ppOrbitLevels levels)} over extent {n}: {cells} pool cells adopted from the store (ascending-lex canonical order)"
                  $"{elemCpp}* {name} = {name}_flat;" ]
            ((assemble @ pin @ adopt) |> List.map (fun s -> ind + s), addVarName binding.Id name ctx)
        | AllocWreath _, None ->
            failwith (orbitStorageUnsupported
                          ($"provider read of '{spec.VarName}' (provider '{spec.Provider}' emits no packed reader)")
                          (orbitLevelsOf ix))
        | spec_, _ ->
            failwith (orbitStorageUnsupported
                          (sprintf "provider read of '%s' (%A)" spec.VarName spec_) (orbitLevelsOf ix))
    | None ->
    let isPackedVar =
        spec.VarType.IndexTypes |> List.exists (fun ix -> ix.Symmetry <> SymNone && ix.Rank >= 2)
    if isPackedVar then
        // Packed (SymIdx/AntisymIdx) read: the provider assembles the store's
        // canonical flat pool into `<name>_flat`; the materialization lives
        // HERE because the SYMM template argument must be hoisted to
        // namespace scope (hoistSymmDecl), which a provider string generator
        // cannot reach. The pool copy is linear: the allocator's flat pool
        // holds exactly the canonical cells in ascending-lex order (the same
        // pinned order the store uses), so no per-cell unlinearize is needed.
        if spec.MaskName.IsSome then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}': load_compound over a packed variable ('{spec.VarName}') is not supported")))
        match pspec.GenReadPacked with
        | None ->
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' does not support packed (symmetric/antisymmetric) variables (variable '{spec.VarName}')")))
        | Some gen ->
            (match binding.Type with
             | ArrayElem arrTy ->
                 // Distribute only whole-variable reads, and only when the
                 // program has MPI scaffolding (windows are small and local).
                 let opts : Blade.ProviderRegistry.PackedReadOpts =
                     { Distribute = mpiProgramOn () && spec.Window.IsNone
                       Window = spec.Window }
                 let assemble = gen spec.FilePath spec.VarName name spec.VarType opts
                 let elemCpp = elemTypeToCpp arrTy.ElemType
                 let componentExtents =
                     arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
                 let rank = componentExtents.Length
                 let extentTerms =
                     componentExtents |> List.map (fun e ->
                         match e with
                         | IRLit (IRLitInt n) -> string n
                         | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"packed provider read of '{spec.VarName}' requires literal extents"))))
                 let extentsName = $"{name}_extents"
                 let extentsArr = $$"""size_t {{extentsName}}[] = { {{(String.concat ", " extentTerms)}} };"""
                 let symmVec = buildSymmVec binding.Type
                 let symmArg =
                     if hasRealSymmetry symmVec then hoistSymmDecl ($"{name}_symm") symmVec
                     else "nullptr"
                 let allocLine =
                     match emitAllocRhs (classifyOutputStorage binding.Type) elemCpp rank symmArg extentsName with
                     | Ok rhs -> $"Array<{elemCpp}, {rank}> {name} = {rhs};"
                     | Error msg -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"packed provider read '{spec.VarName}': {msg}")))
                 let copy = genPackedPoolCopy arrTy name name spec.VarName false
                 ((assemble @ [extentsArr; allocLine] @ copy @ [$"delete[] {name}_flat;"])
                  |> List.map (fun s -> ind + s),
                  addVarName binding.Id name ctx)
             | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.iceCodegen ($"packed provider read '{spec.VarName}': binding is not array-typed"))))
    else
    match Map.tryFind binding.Id ctx.TileReads with
    | Some plan ->
        // Revision reuse (docs/plans/structural/04, 3.4): this read feeds a
        // tiled binding whose probe is hoisted here. The FIRST of the plan's
        // inputs (by emission order) carries the probe; each input's phase-1
        // read assembles only the chunks the unhit tiles need. The remainder
        // is read after the tiled binding (tileLoopLines).
        (tilesUsedCell ()).Value <- true
        let ip = plan.Inputs |> List.find (fun i -> i.ReadId = binding.Id)
        let first = plan.Inputs |> List.minBy (fun i -> i.ReadId)
        let lines = (if first.ReadId = binding.Id then tileProbeLines plan else []) @ ip.Phase1
        (lines |> List.map (fun s -> ind + s), addVarName binding.Id name ctx)
    | None ->
    let readCode =
        (match spec.MaskName, spec.MaskType with
         | Some maskName, Some maskType ->
             (match pspec.GenReadCompoundVar with
              | Some gen -> gen spec.FilePath spec.VarName maskName name spec.VarType maskType
              | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' does not support load_compound (variable '{spec.VarName}')"))))
         | _ ->
             let direct =
                 if ctx.TopLevelProviderRead = Some binding.Id && not (mpiProgramOn ()) then
                     Blade.ZarrProvider.CppZarr.tryGenReadVarIntoFinal spec name
                 else None
             match direct with
             | Some lines ->
                 recordCodegenDecision "provider-resident-destination" binding.Name Blade.Effects.Applied
                     [ "zarr-rank1-direct-destination: module-level ordinary dense read, final pool allocated by the existing allocator"
                       "one required resident payload plus padded disk chunk; original missing tally and short-read/fill failure order; no full flat staging/copy"
                       "allocation-failure timing and process RSS are not claimed identical" ]
                 lines
             | None -> pspec.GenReadVar spec.FilePath spec.VarName name spec.VarType)
    (readCode |> List.map (fun s -> ind + s), addVarName binding.Id name ctx)

and genProviderWriteBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    // Deferred provider write (`alias.write("path", A)`): flatten the source
    // array into `<base>_flat` (row-major Horner, the inverse of genReadVar's
    // materialization copy), hand the provider's writer that buffer, then
    // release it. `base` is the write binding's own cpp name suffixed over
    // the source's, so two writes of one array cannot collide.
    let spec = ctx.ProviderWrites.[binding.Id]
    let pspec =
        match Blade.ProviderRegistry.tryFind spec.Provider with
        | Some p -> p
        | None -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' is not registered -- was ProviderStatics.install () run?")))
    let srcCpp =
        match Map.tryFind spec.SourceId ctx.VarNames with
        | Some n -> n
        | None -> sanitizeCppName spec.VarName
    let baseName = $"{srcCpp}_wr{int binding.Id}"
    let arrTy = spec.SourceType
    let elemCpp = elemTypeToCpp arrTy.ElemType
    // The write side of the provider boundary rule (BL8015): a floating cell
    // that is NaN or infinite is not written -- every reader of the store
    // would refuse it. One scan of the flat buffer the writer is handed, for
    // every provider (CSV, NetCDF, Zarr, Icechunk) and every storage arm.
    let refuseNonFinite (count: string) =
        if elemCpp = "double" || elemCpp = "float" then
            let esc (t: string) = t.Replace("\\", "\\\\").Replace("\"", "\\\"")
            [ $"blade_rt::refuse_nonfinite_write({baseName}_flat, (size_t)({count}), \"{esc spec.VarName}\", \"{esc spec.FilePath}\");" ]
        else []
    // The wreath arm runs BEFORE the component-extent fold below: a wreath
    // record's Extent is the IROrbitClass marker, not an IRLit, so the literal
    // check would fire first and report "requires literal extents" for a class
    // whose extent is perfectly literal one level down.
    match arrTy.IndexTypes |> List.tryFind (fun ix -> ix.Symmetry = SymWreath) with
    | Some ix when pspec.ReadWreathPool.IsNone ->
        failwith (orbitStorageUnsupported
                      ($"provider write of '{spec.VarName}' (provider '{spec.Provider}' stores no OrbIdx pools)")
                      (orbitLevelsOf ix))
    | Some ix ->
        // The source array IS the flat pool in ascending-lex canonical order,
        // so the "flatten" is a straight element copy into a buffer the writer
        // owns -- the depth-1 packed path's linear pool_base copy, minus the
        // skeleton indirection (a wreath array has no skeleton). The copy is not
        // elided: the writer's cleanup deletes the buffer it was handed, and
        // that must not be the array.
        (match classifyOutputStorage (mkArrayLike arrTy) with
         | AllocWreath (levels, n, cells) when Blade.Optimize.sinkAliasEnabled () ->
             // PROVIDER SINK DESTINATION PASSING (provider-sink v1): the source
             // IS the flat pool the copy below reproduced cell for cell, so the
             // writer reads it in place through a CONST pointer (the writers and
             // the BL8015 scan only read; const makes that a compile-time fact),
             // and nothing is deleted: the pool stays the array's. The BL8004
             // pin, the BL8015 scan before any file effect and the writer's own
             // commit order are unchanged.
             let pin =
                 $"if (orbit_wreath_utilities::orb_cell_count<{(orbLevelArgs levels)}>({n}) != {cells}LL) {{ blade_rt::panic(\"BL8004\", \"OrbIdx pool size disagreement: orb_cell_count vs the compiler's iterated-binomial fold\", nullptr, 0); }}"
             let alias =
                 [ $"// Write {spec.VarName} (OrbIdx{(ppOrbitLevels levels)} pool, {cells} cells) to {spec.FilePath} (provider-sink v1: the writer reads the pool in place)"
                   pin
                   $"const {elemCpp}* {baseName}_flat = &({srcCpp}[0]);" ]
             let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
             Blade.Optimize.recordProviderSink binding.Name Blade.Effects.Applied
                 [ $"provider '{spec.Provider}' write of `{spec.VarName}` from the OrbIdx pool of `{srcCpp}` ({cells} cells): no flatten buffer, no copy, no delete"
                   "legality: the removed copy was a linear copy of exactly these cells in this order; the writer and the BL8015 scan read the buffer through a const pointer; validation (BL8004, BL8015) still precedes every file effect" ]
             (guardProviderWrite ind (alias @ refuseNonFinite (string cells) @ writeCode), ctx)
         | AllocWreath (levels, n, cells) ->
             Blade.Optimize.recordProviderSink binding.Name (Blade.Effects.Declined "disabled by BLADE_SINK_ALIAS")
                 [ $"provider '{spec.Provider}' write of `{spec.VarName}`: the OrbIdx pool is copied into a flatten buffer" ]
             let pin =
                 $"if (orbit_wreath_utilities::orb_cell_count<{(orbLevelArgs levels)}>({n}) != {cells}LL) {{ blade_rt::panic(\"BL8004\", \"OrbIdx pool size disagreement: orb_cell_count vs the compiler's iterated-binomial fold\", nullptr, 0); }}"
             let flatten =
                 [ $"// Write {spec.VarName} (OrbIdx{(ppOrbitLevels levels)} pool, {cells} cells) to {spec.FilePath}"
                   pin
                   $"{elemCpp}* {baseName}_flat = new {elemCpp}[{cells}];"
                   $$"""for (size_t __ow_i = 0; __ow_i < {{cells}}; __ow_i++) { {{baseName}}_flat[__ow_i] = {{srcCpp}}[__ow_i]; }""" ]
             let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
             let cleanup = [ $"delete[] {baseName}_flat;" ]
             (guardProviderWrite ind (flatten @ refuseNonFinite (string cells) @ writeCode @ cleanup), ctx)
         | other ->
             failwith (orbitStorageUnsupported
                           (sprintf "provider write of '%s' (%A)" spec.VarName other) (orbitLevelsOf ix)))
    | None ->
    let componentExtents =
        arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
    let rank = componentExtents.Length
    let extentTerms =
        componentExtents |> List.map (fun e ->
            match e with
            | IRLit (IRLitInt n) -> string n
            | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider write of '{spec.VarName}' requires literal extents"))))
    let isPacked =
        arrTy.IndexTypes |> List.exists (fun idx -> idx.Symmetry <> SymNone && idx.Rank >= 2)
    if isPacked then
        // Packed (SymIdx/AntisymIdx) source: the flatten is a LINEAR pool
        // copy -- the allocator's flat pool holds exactly the canonical
        // cells in ascending-lex order, which is the store's pool order.
        // GenReadPacked presence is the provider's packed-layout capability
        // flag (read and write go together: both are pool-order I/O).
        if pspec.GenReadPacked.IsNone then
            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"provider '{spec.Provider}' does not support packed (symmetric/antisymmetric) writes (variable '{spec.VarName}')")))
        let poolCount =
            deviceBufferCardinality (deviceBufferTypeOfArray arrTy) |> exprToCpp ctx.VarNames
        if Blade.Optimize.sinkAliasEnabled () then
            // PROVIDER SINK DESTINATION PASSING (provider-sink v1): the flatten
            // below is a LINEAR copy of pool_base(src.data)[0 .. total), so the
            // writer reads that pool in place, through a const pointer, with
            // nothing deleted. `packedPoolTotal` raises the copy's refusals
            // (shape, literal extents, symmetry class) exactly as before.
            let total = packedPoolTotal arrTy spec.VarName
            let alias =
                [ $"// Write {spec.VarName} (packed pool, {total} cells) to {spec.FilePath} (provider-sink v1: the writer reads the pool in place)"
                  $"const {elemCpp}* {baseName}_flat = nested_array_utilities::pool_base({srcCpp}.data);" ]
            let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
            Blade.Optimize.recordProviderSink binding.Name Blade.Effects.Applied
                [ $"provider '{spec.Provider}' write of `{spec.VarName}` from the packed pool of `{srcCpp}` ({total} cells): no flatten buffer, no copy, no delete"
                  "legality: the removed copy was a linear copy of exactly these cells in this order; the writer and the BL8015 scan read the buffer through a const pointer; validation (BL8015) still precedes every file effect" ]
            (guardProviderWrite ind (alias @ refuseNonFinite poolCount @ writeCode), ctx)
        else
        Blade.Optimize.recordProviderSink binding.Name (Blade.Effects.Declined "disabled by BLADE_SINK_ALIAS")
            [ $"provider '{spec.Provider}' write of `{spec.VarName}`: the packed pool is copied into a flatten buffer" ]
        let flatten =
            [ $"// Write {spec.VarName} (packed pool) to {spec.FilePath}"
              $"{elemCpp}* {baseName}_flat = new {elemCpp}[{poolCount}];" ]
            @ genPackedPoolCopy arrTy srcCpp baseName spec.VarName true
        let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
        let cleanup = [ $"delete[] {baseName}_flat;" ]
        (guardProviderWrite ind (flatten @ refuseNonFinite poolCount @ writeCode @ cleanup), ctx)
    else
    // DESTINATION PASSING, gate 1 (plan-fortran-killer-2.md section 4): a
    // plain dense source already IS the flat buffer the writer wants.
    // allocate<> places every scalar of a nested Array in ONE contiguous pool
    // in DFS order -- row-major, which is exactly the Horner index the copy
    // loop below used to compute -- so `<base>_flat` aliases
    // pool_base(src.data): no allocation, no copy, no delete, one full-array
    // temporary fewer at the pipeline's terminal. The copy stays for every
    // storage pool_base is not defined on (compound / sparse tabulated
    // records, ragged rows, group records) and for the packed and wreath
    // arms above, whose pool order is not the store's dense order.
    let plainDense =
        rank > 0
        && arrTy.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
    if plainDense then
        let alias =
            [ $"// Write {spec.VarName} to {spec.FilePath} (destination passing: the pool is the row-major buffer)"
              $"{elemCpp}* {baseName}_flat = pool_base({srcCpp}.data);" ]
        let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
        (guardProviderWrite ind (alias @ refuseNonFinite (String.concat " * " extentTerms) @ writeCode), ctx)
    else
    let extentNames = extentTerms |> List.mapi (fun i _ -> $"{baseName}_ext{i}")
    let extentDecls =
        List.zip extentNames extentTerms
        |> List.map (fun (n, t) -> $"size_t {n} = {t};")
    let idxVars = [ for i in 0 .. rank - 1 -> $"{baseName}_i{i}" ]
    let openLoops =
        idxVars |> List.mapi (fun d iv ->
            let indl = String.replicate d "    "
            $$"""{{indl}}for (size_t {{iv}} = 0; {{iv}} < {{extentNames.[d]}}; {{iv}}++) {""")
    let nestedSub = idxVars |> List.map (sprintf "[%s]") |> String.concat ""
    let flatIdx =
        match idxVars with
        | [] -> "0"
        | first :: _ ->
            let mutable acc = first
            for i in 1 .. rank - 1 do
                acc <- $"({acc}) * {extentNames.[i]} + {idxVars.[i]}"
            acc
    let bodyInd = String.replicate rank "    "
    let flatten =
        [ $"// Write {spec.VarName} to {spec.FilePath}" ]
        @ extentDecls
        @ [ $"""{elemCpp}* {baseName}_flat = new {elemCpp}[{(String.concat " * " extentNames)}];""" ]
        @ openLoops
        @ [ $"{bodyInd}{baseName}_flat[{flatIdx}] = {srcCpp}{nestedSub};" ]
        @ [ for d in rank - 1 .. -1 .. 0 -> $"""{(String.replicate d "    ")}}}""" ]
    let writeCode = pspec.GenWriteVar spec.FilePath spec.VarName baseName arrTy spec.DimNames
    let cleanup = [ $"delete[] {baseName}_flat;" ]
    (guardProviderWrite ind (flatten @ refuseNonFinite (String.concat " * " extentNames) @ writeCode @ cleanup), ctx)


/// rand.<fam>(key, params.., shape): allocate the dense array (self-typed from
/// the shape) and fill its flat contiguous pool with `card` deterministic draws
/// keyed by `key`, via the blade_rand runtime. All rand arrays are dense
/// SymNone, so pool_base gives the full pool and the draw count is the product
/// of extents. Mirrors the fill_random dense path but uses a flat pool fill.
///
/// The pool's C++ type is whatever `elemTypeToCpp` makes of the binding's
/// ElemType, which is how `categorical` gets an `int64_t` pool (and every other
/// family a `double` one) without a second allocation arm here: the checker
/// picked the element type, this code was already generic in it.
///
/// The family's runtime Float64 parameters follow the key as trailing
/// `(double)`-cast arguments, in surface order; a zero-parameter family
/// (uniform/normal) emits the original three-argument call unchanged, so this
/// extension is byte-compatible with the pre-existing emission for those two.
/// The array parameter channel (categorical's weights) emits in the same
/// position as a POINTER-plus-LENGTH pair, `pool_base(W.data), (size_t)k`.
and genRandGenBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let kind, keyExpr, parExprs, weightsExpr, addressExpr =
        match ctx.RandomInits.[binding.Id] with
        | RandGen (k, key, pars, weights, address) -> k, key, pars, weights, address
        | FillModulus _ -> "uniform", IRLit (IRLitInt 0L), [], None, None  // unreachable: dispatch guards this
    match binding.Type with
    | ArrayElem arrTy ->
        let elemCpp = elemTypeToCpp arrTy.ElemType
        let extents = arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
        let rank = extents.Length
        let nonLiteral = extents |> List.exists (fun e -> match e with IRLit (IRLitInt _) -> false | _ -> true)
        if nonLiteral then
            ([refusalErrorLine ind ($"rand binding '{name}' requires literal extents")], addVarName binding.Id name ctx)
        else
            let extentTerms = extents |> List.map (fun e -> match e with IRLit (IRLitInt n) -> string n | _ -> "0")
            let extentsName = $"{name}_extents"
            let extentsArr = $$"""{{ind}}size_t {{extentsName}}[] = { {{(String.concat ", " extentTerms)}} };"""
            let card = extents |> List.fold (fun acc e -> match e with IRLit (IRLitInt n) -> acc * n | _ -> acc) 1L
            let allocLine =
                $"{ind}Array<{elemCpp}, {rank}> {name} = {{ allocate<typename promote<{elemCpp}, {rank}>::type, nullptr>({extentsName}), {extentsName} }};"
            // Array parameter first (surface order puts weights before any
            // scalar par), then the scalar pars.
            let weightsArgs =
                match weightsExpr with
                | None -> ""
                | Some (wExpr, k) ->
                    $", nested_array_utilities::pool_base({(exprToCpp ctx.VarNames wExpr)}.data), (size_t){k}LL"
            // Scalar parameters with a DOMAIN (Rand.Elaborate.paramGuards) are
            // bound to locals and checked before the fill, in the emitted
            // text for the same shadow-frame reason as the address guards
            // below; the interpreter mirror raises the identical BL8001. A
            // family with no guarded parameter keeps the inline casts.
            let guards = Blade.Rand.Elaborate.paramGuards kind
            let parLines, parArgs =
                if guards.IsEmpty then
                    [],
                    (parExprs
                     |> List.map (fun p -> $", (double)({(exprToCpp ctx.VarNames p)})")
                     |> String.concat "")
                else
                    let parName i = $"{name}__par{i}"
                    let binds =
                        parExprs |> List.mapi (fun i p ->
                            $"{ind}const double {parName i} = (double)({(exprToCpp ctx.VarNames p)});")
                    let checks =
                        guards |> List.collect (fun (i, pname, dom) ->
                            let msg = Blade.Rand.Elaborate.paramGuardMessage kind pname dom
                            [ $"{ind}if (!({Blade.Rand.Elaborate.paramDomainCpp dom (parName i)})) {{"
                              $"{ind}    std::cerr << \"Blade runtime: {msg} (got \" << {parName i} << \")\" << std::endl;"
                              $"{ind}    blade_rt::panic(\"BL8001\", \"{msg}\", {(declPanicArgs ())});"
                              $"{ind}}}" ])
                    binds @ checks,
                    (parExprs |> List.mapi (fun i _ -> $", {parName i}") |> String.concat "")
            // The `_at` address channel sits right after the key: the stream
            // key and the sample offset, both int64, before the weights/pars.
            // Bound to locals and RANGE-CHECKED HERE, in the emitted text --
            // the stream key occupies one 32-bit counter word, the offset is
            // an unsigned sample id -- because a runtime header must never
            // reach blade_rt::panic on its own (codegen elides the shadow
            // frame of a body whose text cannot panic; Test_Diagnostics pins
            // that rule). The interpreter mirror carries the same two guards.
            let addressLines, addressArgs =
                match addressExpr with
                | None -> [], ""
                | Some (sExpr, oExpr) ->
                    let sName = $"{name}__stream"
                    let oName = $"{name}__offset"
                    ([ $"{ind}const int64_t {sName} = (int64_t)({(exprToCpp ctx.VarNames sExpr)});"
                       $"{ind}const int64_t {oName} = (int64_t)({(exprToCpp ctx.VarNames oExpr)});"
                       $$"""{{ind}}if ({{sName}} < 0 || (((uint64_t){{sName}}) >> 32) != 0) {"""
                       $"{ind}    std::cerr << \"Blade runtime: rand: the stream key of an _at family must be in [0, 2^32) (got \" << {sName} << \")\" << std::endl;"
                       $"{ind}    blade_rt::panic(\"BL8001\", \"rand stream key out of range\", {(declPanicArgs ())});"
                       $"{ind}}}"
                       $$"""{{ind}}if ({{oName}} < 0) {"""
                       $"{ind}    std::cerr << \"Blade runtime: rand: the sample offset of an _at family must be non-negative (got \" << {oName} << \")\" << std::endl;"
                       $"{ind}    blade_rt::panic(\"BL8001\", \"rand sample offset negative\", {(declPanicArgs ())});"
                       $"{ind}}}" ],
                     $", {sName}, {oName}")
            let fillLine =
                $"{ind}blade_rand::{kind}(nested_array_utilities::pool_base({name}.data), (size_t){card}LL, (int64_t)({(exprToCpp ctx.VarNames keyExpr)}){addressArgs}{weightsArgs}{parArgs});"
            ([extentsArr; allocLine] @ addressLines @ parLines @ [fillLine], addVarName binding.Id name ctx)
    | _ ->
        ([refusalErrorLine ind ($"rand binding '{name}' is not an array type")], addVarName binding.Id name ctx)

and genRandomInitBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Random-fill constructor (`let A: Array<..> = fill_random(mod)`):
    // allocate the nested Array (same form as a literal binding) and fill it
    // with rand() % mod via the runtime fill_random. Shape/elem come from the
    // binding's array type; the modulus from RandomInits. Rectangular, so
    // SYMM defaults to nullptr. fill_random deduces its type from the first
    // arg, so pass the raw nested pointer (.data), not the Array wrapper --
    // the wrapper would deduce as a scalar leaf and never recurse.
    let modExpr =
        match ctx.RandomInits.[binding.Id] with
        | FillModulus m -> m
        | RandGen _ -> IRLit (IRLitInt 1L)  // unreachable: dispatch routes RandGen to genRandGenBinding
    (match binding.Type with
     | ArrayElem arrTy ->
         let elemCpp = elemTypeToCpp arrTy.ElemType
         let allDenseRank1 =
             arrTy.IndexTypes |> List.forall (fun idx -> idx.Rank = 1 && idx.Symmetry = SymNone)
         let hasHermitian =
             arrTy.IndexTypes |> List.exists (fun idx -> idx.Symmetry = SymHermitian)
         if allDenseRank1 then
             // Dense-rectangular path: unchanged (byte-compatible with the
             // pre-arc-3 emission -- the runtime fill_random walks the shape).
             let rank = arrayRank arrTy
             let extentNames = arrTy.IndexTypes |> List.mapi (fun i _ -> $"{name}_extent_{i}")
             let extentDecls =
                 arrTy.IndexTypes |> List.mapi (fun i idx ->
                     match idx.Extent with
                     | IRLit (IRLitInt n) -> $"{ind}size_t {name}_extent_{i} = {n};"
                     | _ -> refusalErrorLine ind ($"fill_random binding '{name}' has a non-literal extent at dim {i}"))
             let extentsArr = $$"""{{ind}}size_t {{name}}_extents[] = { {{(String.concat ", " extentNames)}} };"""
             let allocLine =
                 $"{ind}Array<{elemCpp}, {rank}> {name} = {{ allocate<typename promote<{elemCpp}, {rank}>::type, nullptr>({name}_extents), {name}_extents }};"
             let fillLine =
                 $"{ind}fill_random({name}.data, {name}_extents, (int)({(exprToCpp ctx.VarNames modExpr)}));"
             (extentDecls @ [extentsArr; allocLine; fillLine], addVarName binding.Id name ctx)
         elif hasHermitian then
             // Hermitian stores the full n^2 cells but they are CONSTRAINT-
             // COUPLED (A(i,j) = conj(A(j,i))): independent pool draws would
             // violate the invariant, so hermitian fill needs a canonical-
             // half fill + mirrored conjugation -- not yet emitted.
             ([refusalErrorLine ind ($"fill_random binding '{name}': HermitianIdx is not supported (stored cells are constraint-coupled)")],
              addVarName binding.Id name ctx)
         else
             // GENERALIZED fill (arc 3, formalism 3.5): one draw per STORED
             // cell. Compact storage classes (SymIdx/AntisymIdx, mixed with
             // dense axes) allocate with their SYMM vector and fill the flat
             // pool linearly -- the pool holds exactly the canonical cells, so
             // symmetry holds by construction, antisym diagonals stay
             // implicit zeros, and the draw count is the storage cardinality
             // (deviceBufferCardinality -- same closed forms as allocation).
             let componentExtents =
                 arrTy.IndexTypes |> List.collect (fun idx -> List.replicate idx.Rank idx.Extent)
             let rank = componentExtents.Length
             let nonLiteral =
                 componentExtents |> List.exists (fun e -> match e with IRLit (IRLitInt _) -> false | _ -> true)
             if nonLiteral then
                 ([refusalErrorLine ind ($"fill_random binding '{name}' requires literal extents")],
                  addVarName binding.Id name ctx)
             else
                 let extentTerms =
                     componentExtents |> List.map (fun e ->
                         match e with IRLit (IRLitInt n) -> string n | _ -> "0")
                 let extentsName = $"{name}_extents"
                 let extentsArr = $$"""{{ind}}size_t {{extentsName}}[] = { {{(String.concat ", " extentTerms)}} };"""
                 let symmVec = buildSymmVec binding.Type
                 let symmArg =
                     if hasRealSymmetry symmVec then hoistSymmDecl ($"{name}_symm") symmVec
                     else "nullptr"
                 let allocLines =
                     match emitAllocRhs (classifyOutputStorage binding.Type) elemCpp rank symmArg extentsName with
                     | Ok rhs -> [$"{ind}Array<{elemCpp}, {rank}> {name} = {rhs};"]
                     | Error msg -> [refusalErrorLine ind ($"fill_random '{name}': {msg}")]
                 let poolCount =
                     deviceBufferCardinality (deviceBufferTypeOfArray arrTy)
                     |> exprToCpp ctx.VarNames
                 let fillLines =
                     [ $"{ind}{{ auto* __fr_pool = nested_array_utilities::pool_base({name}.data);"
                       sprintf "%s  for (size_t __fr_i = 0; __fr_i < %s; __fr_i++) { __fr_pool[__fr_i] = static_cast<%s>(rand() %% (int)(%s)); } }"
                           ind poolCount elemCpp (exprToCpp ctx.VarNames modExpr) ]
                 (extentsArr :: allocLines @ fillLines, addVarName binding.Id name ctx)
     | _ ->
         ([refusalErrorLine ind ($"fill_random binding '{name}' is not an array type")], addVarName binding.Id name ctx))


and genCompoundInitBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // The hash equijoin (Blade.Optimize.planEquiJoin, round 3 W3-2): an
    // admitted inline equijoin compound never builds its N x M operands.
    let joined =
        match Blade.Optimize.EquiJoinTable.tryCompound binding.Id with
        | Some jp ->
            let r = genEquiJoinBinding ctx binding jp
            if r.IsNone then
                recordCodegenDecision "hash-join" (bindingCppName binding)
                    (Blade.Effects.Declined "c++ lane: a source or the payload kernel does not render as a stored name here; the as-written six passes run") []
            r
        | None -> None
    match joined with
    | Some emitted -> emitted
    | None ->
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Compound-construction constructor (`let B = compound(dense, mask)`):
    // materialize the compound index from the bool mask (P0,
    // genCompoundIndexFromMask), then scatter the dense array's present
    // leading cells into a compact buffer and bundle a Compound<T, RANK>.
    // The mask covers a LEADING PREFIX of dense's dims (validated by
    // compoundViewType in typecheck); remaining dims fold into trailing_stride.
    //
    // dense and mask are lowered IRVar references (recorded in CompoundInits);
    // exprToCpp yields their in-scope C++ variable names. Both are Array<...>
    // wrappers, so .data is the nested pointer and pool_base flattens to the
    // contiguous row-major pool the scatter walks.
    let (denseExpr, maskExpr) = ctx.CompoundInits.[binding.Id]
    // The DENSE operand may be computed in place too -- `compound(W * S, m)`,
    // an elementwise product compounded to a sub-ensemble. exprToCpp cannot
    // run that loop mid-expression (the BL7004 "array-valued elementwise
    // kernel body" refusal; the interpreter evaluated it). Emit it as the
    // binding the user would have written (`let ws = W * S`) ahead of the
    // scatter, exactly like the computed-mask arm below; a let-bound deferred
    // computation is forced the way genMaskBinding forces its source.
    let (densePre, denseName, ctx) = materializeCompoundOperand ctx builder ($"{name}__dense") denseExpr
    // The mask operand may be written INLINE inside compound(...) --
    // `compound(A, mask(A, p))` -- in which case it lowers to a bare
    // IRMask node. exprToCpp cannot render a mask inline (it needs a
    // multi-statement materialization), so it would emit the
    // BLADE_CODEGEN_ERROR sentinel as the "name". Materialize such an
    // inline mask into a Bool presence-array temp first (the same helper
    // the method_for auto-materialize path uses), then feed that temp's
    // name to the index builder. A let-bound mask
    // (`let m = mask(...); compound(A, m)`) arrives as an IRVar and skips
    // this. maskPre is prepended to the emitted lines below.
    //
    // The inline mask's SOURCE can itself be a computation written in place
    // -- `compound(V, mask(A - B, p))`, a mask over an elementwise difference.
    // materializeMaskForm renders its source with exprToCpp, which cannot run
    // a loop mid-expression (an apply in expression position is the BL7004
    // "array-valued elementwise kernel body" refusal), so the program passed
    // `blade check`, ran under the interpreter, and was refused here. Force
    // the source to a temp first, exactly as the let-bound genMaskBinding
    // does (forceDeferredArrayInput: a no-op for a plain array).
    let (maskPre, maskName, ctx) =
        match maskExpr with
        | IRMask (srcExpr, predExpr) ->
            let tmpName = $"{name}__masksrc"
            let (forceCode, ctxF, srcExpr') = forceDeferredArrayInput ctx builder ($"{tmpName}__arr") srcExpr
            (match materializeInlineForm emptySubst ctxF.VarNames tmpName (lazy "bool") (IRMask (srcExpr', predExpr)) with
             // Deliberately NOT registered: this temp feeds the COMPOUND index
             // construction below, and compound storage / ownership is owned by
             // a separate workstream (the same reason isFreeableDenseArrayType
             // excludes compound types).
             | Some (stmts, _) -> (forceCode @ (stmts |> List.map (fun s -> ind + s)), tmpName, ctxF)
             | None -> (forceCode, exprToCpp ctxF.VarNames maskExpr, ctxF))
        // A let-bound mask (`let m = mask(...)`, or a Bool map
        // `let m = method_for(k) <@> lambda(v) -> v == 1` that is still
        // deferred) and a COMPUTED mask -- `compound(temp, qc_ok && warm)`, a
        // conjunction of masks, or a Bool map written in place. The index
        // build reads the mask BY NAME, so each is materialized first
        // (materializeCompoundOperand: a no-op for a stored mask).
        | _ -> materializeCompoundOperand ctx builder ($"{name}__masksrc") maskExpr
    (match binding.Type with
     | ArrayElem arrTy when isCompoundArrayType arrTy ->
         let leadRank =
             arrTy.IndexTypes
             |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
             |> Option.map (_.Rank)
             |> Option.defaultValue 1
         let elemCpp = elemTypeToCpp arrTy.ElemType
         // The compound array type carries leadRank (compound) + trailing
         // slots; the number of trailing dims = arrTy.IndexTypes.Length - 1.
         let trailingDimCount = arrTy.IndexTypes.Length - 1
         let idxName = $"{name}_idx"
         let (idxLines, _) = genCompoundIndexFromMask maskName leadRank idxName
         // trail = product of dense.extents[leadRank .. leadRank+trailingDimCount-1]
         let trailTerms =
             [ for d in 0 .. trailingDimCount - 1 -> $"{denseName}.extents[{leadRank + d}]" ]
         let trailExpr = match trailTerms with | [] -> "1" | xs -> String.concat " * " xs
         // The REPRESENTATION decision, for `blade plan` (rule `compound`):
         // a rank-1 mask is the compaction (kept-position table, gather by
         // position, no hash), a rank-k mask keeps the hashed index.
         let rank1 = (leadRank = 1)
         // Compaction fusion (Blade.Optimize.planCompactionFusion): the mask
         // pass counted (its emitter published the count's name), so the
         // position fill and the value gather are ONE pass here. Every fold
         // planned over this compound resets its handshake first, so a fold
         // reads an accumulator only when THIS emission fed it.
         let folds = Blade.Optimize.CompactionTable.foldsOf binding.Id
         let planHere =
             match Blade.Optimize.CompactionTable.tryCompound binding.Id, maskExpr with
             | Some p, IRVar (mId, _) when mId = p.MaskId && rank1 && trailingDimCount = 0 && maskPre.IsEmpty -> Some p
             | _ -> None
         // T4c: a companion of a shared selection -- the first one runs the
         // selection's one joint pass and builds the shared index, the others
         // adopt it (their chains were fed there: no reset here).
         let sharedLines =
             match planHere with
             | Some p when p.Shared.IsSome -> genSharedCompanion ctx ind binding name elemCpp denseName maskName p p.Shared.Value
             | _ -> None
         match sharedLines with
         | Some lines -> (densePre @ lines, addVarName binding.Id name ctx)
         | None ->
         // T4a: the mask's pass already gathered (and fed the folds it
         // published); otherwise the folds' handshakes start clean here.
         let gathered = planHere |> Option.bind (fun p -> p.GatherCpp.Value)
         if gathered.IsNone then
             for f in folds do f.EmittedCpp.Value <- None
         let fusedCount = planHere |> Option.bind (fun p -> p.CountCpp.Value)
         match fusedCount with
         | Some cnt ->
             let positions = planHere |> Option.forall (fun p -> p.PositionsNeeded)
             let lines = genFusedCompaction ctx ind name elemCpp denseName maskName cnt folds positions gathered
             registerShapedAlloc name "deallocate_compound" name
             (densePre @ lines, addVarName binding.Id name ctx)
         | None ->
         recordCompoundRepresentation name leadRank trailingDimCount
         let lines =
             densePre
             @ maskPre
             @ (idxLines |> List.map (fun l -> ind + l))
             @ [ $"{ind}size_t {name}_trail = {trailExpr};"
                 $"{ind}{elemCpp}* {name}_densepool = nested_array_utilities::pool_base({denseName}.data);"
                 // The `+ 1` is REQUIRED BY the rank >= 2 branchless scatter
                 // and only by it: that form stores unconditionally and lets
                 // the cursor sit at `cardinality` after the last selected
                 // cell, so a trailing unselected cell writes one past the
                 // logical end. One element of slack, never read, is the price
                 // of deleting a branch that mispredicts once per grid cell.
                 // The rank-1 gather by position moves exactly `cardinality *
                 // trail` cells and needs no slack.
                 (if trailTerms.IsEmpty && not rank1 then
                     $"{ind}{elemCpp}* {name}_compact = new {elemCpp}[{idxName}->cardinality * {name}_trail + 1];"
                  else
                     $"{ind}{elemCpp}* {name}_compact = new {elemCpp}[{idxName}->cardinality * {name}_trail];")
                 // rank 1: gather by kept position; rank >= 2: scatter present
                 // leading cells (row-major prefix-popcount)
                 compactScatter { Ind = ind; Name = name; IdxName = idxName; ScalarTrail = trailTerms.IsEmpty; Rank1 = rank1 }
                 $$"""{{ind}}nested_array_utilities::Compound<{{elemCpp}}, {{leadRank}}> {{name}} { {{name}}_compact, {{idxName}}, {{name}}_trail };""" ]
         // Owns BOTH the compact buffer and the freshly built index. (BL6002
         // restricts compound() to top-level lets today, where the empty
         // scope stack makes this a no-op -- registered anyway so a future
         // lifting of BL6002 inherits correct frees. The idx's grid/pool
         // locals alias the mask; only data + idx are owned.)
         registerShapedAlloc name "deallocate_compound" name
         (lines, addVarName binding.Id name ctx)
     | _ ->
         ([refusalErrorLine ind ($"compound() binding '{name}' is not a CompoundIdx array type")], addVarName binding.Id name ctx))

/// THE HASH EQUIJOIN (Blade.Optimize.planEquiJoin, round 3 W3-2, item T2;
/// plan rule `hash-join v1`): `J = compound(method_for(a1, b1) <@> pk,
/// mask(method_for(a0, b0) <@> lambda(l, r) -> l == r, lambda(e) -> e))`
/// over four stored sources, emitted as
///   1. the CSR over the right keys b0 (blade_join_build_i64, blade_member.h:
///      a counting sort for a small span / a positional key, else the
///      open-addressing key -> bucket map; bucket rows filled FORWARD, so
///      every bucket lists its right rows j ascending);
///   2. the COUNTING pass: each left key's bucket (or -1) kept per row, |J| =
///      the sum of the bucket sizes -- J sized before any store;
///   3. the PROBE: i ascending, every row of the bucket ascending,
///      `compact[q++] = pk(a1[i], b1[j])` -- left-major / right-minor, the
///      compound's lex order;
///   4. J bundled over a CARDINALITY-ONLY compound_index_t<2> (dense extents
///      N x M, cardinality |J|; no tuple table, reverse hash or mask).
/// The N x M payload / Bool / identity-mask pools, the bit vector and the
/// hashed enumeration are never built; J's consumers read its stored buffer.
/// None when a source does not render as a stored name (the as-written
/// passes then run, and the caller records the C++ lane's decline).
and genEquiJoinBinding (ctx: CodeGenContext) (binding: IRBinding) (jp: Blade.Optimize.EquiJoinPlan) : (string list * CodeGenContext) option =
    let ind = indentStr ctx
    let name = bindingCppName binding
    match binding.Type, resolveCallable jp.PayloadKernel with
    | ArrayElem arrTy, Some pk when isCompoundArrayType arrTy && arrTy.IndexTypes.Length = 1 && pk.Captures.IsEmpty ->
        let src (e: IRExpr) = exprToCpp ctx.VarNames e
        let lk, rk, la, ra = src jp.LeftKey, src jp.RightKey, src jp.LeftPay, src jp.RightPay
        let bad (s: string) = s = "" || s.Contains "BLADE_CODEGEN_ERROR" || s.Contains "("
        if bad lk || bad rk || bad la || bad ra then None
        else
        let elemCpp = elemTypeToCpp arrTy.ElemType
        let (wrapperCode, wname) = genCallableWrapper ctx.VarNames $"{name}__pk" pk
        let positional = match jp.KeyRegime with Blade.Optimize.JoinPositional _ -> 1 | Blade.Optimize.JoinDynamic -> 0
        let regime =
            match jp.KeyRegime with
            | Blade.Optimize.JoinPositional k -> $"positional right keys (Idx<{k}>): counting sort"
            | Blade.Optimize.JoinDynamic -> "dynamic right keys: counting sort for a small span, else hash (chosen at run time)"
        let i1 = ind + "    "
        let i2 = i1 + "    "
        let i3 = i2 + "    "
        // The `[join] ...` build report (blade_join_note_i64, stderr) is
        // EMITTED only under the COMPILE-TIME instrumentation gate
        // BLADE_JOIN_REPORT (Optimize.joinReportEnabled), and then printed
        // unconditionally by the executable: a run-time switch would be a
        // getenv, whose first call builds the CRT's narrow environment (~4 us
        // measured at the pilot) inside the timed region. BLADE_MEMBER_VERBOSE
        // (the membership builds' run-time switch) is not read here.
        // Emission-only (the exe cache keys on the text); the suite clears it.
        let verbose = Blade.Optimize.joinReportEnabled ()
        let lines =
            [ $"{ind}// hash-join v1 (Blade.Optimize.planEquiJoin): `{name}` = the equijoin of `{jp.LeftKeyName}` and `{jp.RightKeyName}` -- a stable CSR over the right keys ({regime}), a counting pass sizing the relation, then the left-major / right-minor probe (the compound's lex order); no N x M pool is built"
              $"{ind}const size_t {name}__n = {lk}.extents[0];"
              $"{ind}const size_t {name}__m = {rk}.extents[0];"
              $"{ind}const int64_t* {name}__lk = (const int64_t*)nested_array_utilities::pool_base({lk}.data);"
              $"{ind}const int64_t* {name}__rk = (const int64_t*)nested_array_utilities::pool_base({rk}.data);"
              $"{ind}const auto* {name}__la = nested_array_utilities::pool_base({la}.data);"
              $"{ind}const auto* {name}__ra = nested_array_utilities::pool_base({ra}.data);" ]
            @ (wrapperCode |> List.map (fun l -> ind + l))
            // The dense extents product N x M the index retains is checked
            // FIRST, ahead of the CSR build (it depends on nothing else).
            @ [ $"{ind}if ({name}__m != 0 && {name}__n > SIZE_MAX / {name}__m) throw std::length_error(\"hash join `{name}`: the dense extents N x M overflow size_t\");"
                $"{ind}blade_join_csr_i64 {name}__csr;"
                $"{ind}if (blade_join_build_i64(&{name}__csr, {name}__rk, (int64_t){name}__m, {positional}) != 0) throw std::bad_alloc();"
                // the COUNTING pass: |J| before any store (the probe looks each
                // left key up again -- no per-row bucket array to allocate).
                // CHECKED: every bucket length is added against the largest
                // element count `new T[card + 1]` can allocate (PTRDIFF_MAX
                // bytes, the slack cell included); an overflow throws
                // std::length_error BEFORE the RELATION's allocation and stores
                // (the program's ordinary BL8005 runtime failure, never UB; the
                // CSR, built just above, is the only allocation before it). The
                // probe stores exactly the counted cells.
                $"{ind}const size_t {name}__cap = (size_t)PTRDIFF_MAX / sizeof({elemCpp}) - 1;"
                $"{ind}size_t {name}__card = 0;"
                $"{ind}for (size_t {name}__i = 0; {name}__i < {name}__n; {name}__i++) {{"
                $"{i1}const int64_t {name}__b = blade_join_lookup_i64(&{name}__csr, {name}__lk[{name}__i]);"
                $"{i1}if ({name}__b < 0) continue;"
                $"{i1}const size_t {name}__len = (size_t)({name}__csr.off[{name}__b + 1] - {name}__csr.off[{name}__b]);"
                $"{i1}if ({name}__len > {name}__cap - {name}__card) throw std::length_error(\"hash join `{name}`: the relation's cardinality exceeds the largest allocatable buffer\");"
                $"{i1}{name}__card += {name}__len;"
                $"{ind}}}" ]
            @ (if verbose then [ $"{ind}blade_join_note_i64(&{name}__csr, \"{name}\", \"{jp.RightKeyName}\", (int64_t){name}__n, (int64_t){name}__card);" ] else [])
            @ [ $"{ind}{elemCpp}* {name}_compact = new {elemCpp}[{name}__card + 1];"
                $"{ind}{{"
                $"{i1}size_t {name}__q = 0;"
                $"{i1}for (size_t {name}__i = 0; {name}__i < {name}__n; {name}__i++) {{"
                $"{i2}const int64_t {name}__b = blade_join_lookup_i64(&{name}__csr, {name}__lk[{name}__i]);"
                $"{i2}if ({name}__b < 0) continue;"
                $"{i2}const int64_t {name}__hi = {name}__csr.off[{name}__b + 1];"
                $"{i2}for (int64_t {name}__t = {name}__csr.off[{name}__b]; {name}__t < {name}__hi; {name}__t++) {{"
                $"{i3}{name}_compact[{name}__q++] = {wname}({name}__la[{name}__i], {name}__ra[{name}__csr.perm[{name}__t]]);"
                $"{i2}}}"
                $"{i1}}}"
                $"{ind}}}"
                $"{ind}blade_join_free_i64(&{name}__csr);"
                $$"""{{ind}}std::array<size_t, 2> {{name}}_idx_extents = { {{name}}__n, {{name}}__m };"""
                $"{ind}compound_index_t<2>* {name}_idx = new compound_index_t<2>(\"{name}_idx\", {name}_idx_extents, {name}__card, compound_index_t<2>::cardinality_only); // hash-join v1: cardinality-only (no tuple table, reverse hash or mask)"
                $$"""{{ind}}nested_array_utilities::Compound<{{elemCpp}}, 2> {{name}} { {{name}}_compact, {{name}}_idx, 1 };""" ]
        (memberUsedCell ()).Value <- true
        jp.EmittedCpp.Value <- true
        recordCodegenDecision "compound" name Blade.Effects.Applied
            [ "representation: the equijoin relation (hash-join v1) -- the compact buffer in lex order over a cardinality-only compound_index_t<2>; no tuple table, no reverse hash, no mask"
              "rank-2 relation over (left row, right row); no trailing dims" ]
        registerShapedAlloc name "deallocate_compound" name
        Some (lines, addVarName binding.Id name ctx)
    | _ -> None

/// The FUSED rank-1 compaction (Blade.Optimize.planCompactionFusion, B4b/c):
/// `cnt` (the mask pass's count) sizes both buffers at m + 1 -- the last cell
/// is the slack the unconditional store needs after the last kept cell, never
/// exposed -- and ONE branchless pass `v = x[i]; y[q] = v; pos[q] = i;
/// q += keep[i]` gathers the values and fills the kept positions, ascending
/// and stable. The index adopts the filled table (dense extent n, cardinality
/// m, the kept positions), so keyed reads, `unhash`, `present` and
/// `prefix_range` see the table the counting constructor built.
///
/// B4c: a planned fold over `y * y` whose form here is the SERIAL chain
/// (BLADE_FP_REPRO=1, or no reorder licence) is fed inside the pass: after
/// every 256-cell block of x, the chain advances over the newly completed
/// cells (index < q -- never the slack cell), the first kept square peeled
/// as the chain's start. Same terms, same order, same operations as the fold
/// over a materialized `y * y` (each product rounded before its add, as a
/// stored product is); the fold's empty check stays at its own declaration.
/// The licensed lane form is not fed here (the fold reads `y.data` at its
/// site); its streaming twin is the cell-epilogue lane state's follow-up.
///
/// Round 3 (compaction fusion v2):
///   * `positions = false` (T4b, the demand census certified no reader): no
///     position buffer and no `pos[q] = i` store; the index is built
///     cardinality-only (dense extent n, cardinality m; every lookup through
///     the absent table is BL9002).
///   * `gathered = Some g` (T4a): the mask's pass already gathered into g's
///     n + 1 buffers (and fed the in-pass chains); this site only adopts
///     them -- unless g carries a run-time guard, when it first runs the B4
///     pass itself while the guard is false (n over the selectivity cap),
///     into the same buffer variables and the same chain accumulators.
and genFusedCompaction (ctx: CodeGenContext) (ind: string) (name: string) (elemCpp: string) (denseName: string) (maskName: string) (cnt: string) (folds: Blade.Optimize.CompactionFoldPlan list) (positions: bool) (gathered: Blade.Optimize.CompactionGatherCpp option) : string list =
    let idxName = $"{name}_idx"
    let i1 = ind + "    "
    // The B4 pass into `compact` / `pos`, feeding `feeds` (accumulator, wrapper).
    let b4Pass (ind: string) (compact: string) (pos: string option) (feeds: (string * string) list) =
        let cell (body: string) =
            [ $"{body}const {elemCpp} {name}_v = {name}_densepool[{name}_i];"
              $"{body}{compact}[{name}_q] = {name}_v;" ]
            @ (pos |> Option.map (fun p -> [ $"{body}{p}[{name}_q][0] = {name}_i;" ]) |> Option.defaultValue [])
            @ [ $"{body}{name}_q += (size_t){idxName}_pool[{name}_i];" ]
        [ $"{ind}{{"
          $"{ind}    size_t {name}_q = 0;" ]
        @ compactionPassLoop (ind + "    ") name $"{name}_i" $"{idxName}_grid" elemCpp compact $"{name}_q" feeds cell
        @ [ $"{ind}}}" ]
    let adopt (compact: string) (pos: string option) (how: string) =
        [ (match pos with
           | Some p -> $"{ind}compound_index_t<1>* {idxName} = new compound_index_t<1>(\"{idxName}\", {idxName}_grid, {cnt}, {p}); // rank-1 compaction, fused: {how}"
           | None -> $"{ind}compound_index_t<1>* {idxName} = new compound_index_t<1>(\"{idxName}\", {idxName}_grid, {cnt}, compound_index_t<1>::positions_elided); // rank-1 compaction, fused, positions elided (no consumer reads them): {how}")
          $$"""{{ind}}nested_array_utilities::Compound<{{elemCpp}}, 1> {{name}} { {{compact}}, {{idxName}}, {{name}}_trail };""" ]
    let posNote = if positions then "keyed reads by binary search over the kept positions" else "positions elided: the demand census found no consumer that reads them"
    match gathered with
    | Some g ->
        let fallback =
            match g.Guard with
            | None -> []
            | Some guard ->
                [ $"{ind}if (!{guard}) {{"
                  $"{i1}// n is over the selectivity cap (BLADE_TILE_TUNE sel_cap): the two-pass form, m + 1 cells per buffer"
                  $"{i1}bool* {idxName}_pool = nested_array_utilities::pool_base({maskName}.data);"
                  $"{i1}{elemCpp}* {name}_densepool = nested_array_utilities::pool_base({denseName}.data);" ]
                @ (g.Pos |> Option.map (fun p -> [ $"{i1}{p} = new compound_index_t<1>::IDX[{cnt} + 1];" ]) |> Option.defaultValue [])
                @ [ $"{i1}{g.Compact} = new {elemCpp}[{cnt} + 1];" ]
                @ b4Pass i1 g.Compact g.Pos g.Feeds
                @ [ $"{ind}}}" ]
        let guardNote = match g.Guard with Some _ -> " while n is within the selectivity cap (else one fill+gather pass at the compound)" | None -> ""
        let feedNote = if g.Feeds.IsEmpty then "" else $"; the serial chain of {g.Feeds.Length} fold(s) over `{name} * {name}` fed per 256-cell block in that pass"
        recordCodegenDecision "compound" name Blade.Effects.Applied
            [ $"representation: rank-1 compaction (counted and gathered in the mask pass -- compaction fusion v2; no hash table; {posNote})"
              $"rank-1 mask over one dense axis; no trailing dims; `{maskName}`'s pass sweeps the source once (per 256-cell block: the predicate, then the gather){guardNote}{feedNote}" ]
        [ $"{ind}// compaction fusion v2 (Blade.Optimize.planCompactionFusion, T4a): `{maskName}`'s own pass counted AND gathered `{name}` (n + 1 private cells per buffer, m + 1 touched); this site adopts them"
          $"{ind}size_t {idxName}_grid = {maskName}.extents[0];"
          $"{ind}size_t {name}_trail = 1;" ]
        @ fallback
        @ adopt g.Compact g.Pos "the buffers the mask pass filled"
    | None ->
    let feeds =
        folds |> List.choose (fun f ->
            if not f.InPass then None
            else
            match resolveCallable f.FoldKernel with
            | Some cl when cl.Params.Length = 2 && not (fpReassocEnabled () && foldReorderLicensed cl) ->
                Some (f, cl)
            | _ -> None)
        |> List.mapi (fun k (f, cl) ->
            let (wrapperCode, wname) = genCallableWrapper ctx.VarNames $"{name}__fold{k}" cl
            (f, $"{name}__fold{k}", wrapperCode, wname))
    if not feeds.IsEmpty then (foldLanesUsedCell ()).Value <- true
    for (f, acc, _, _) in feeds do f.EmittedCpp.Value <- Some acc
    let feedNote =
        if feeds.IsEmpty then "" else $"; the serial chain of {feeds.Length} fold(s) over `{name} * {name}` fed per 256-cell block"
    let pos = if positions then Some $"{name}_pos" else None
    recordCodegenDecision "compound" name Blade.Effects.Applied
        [ (if positions then "representation: rank-1 compaction (kept positions counted in the mask pass and filled in the gather pass -- compaction fusion; no hash table; keyed reads by binary search over the kept positions)"
           else $"representation: rank-1 compaction (counted in the mask pass, gathered in one pass -- compaction fusion; no hash table; {posNote})")
          $"rank-1 mask over one dense axis; no trailing dims; one fill+gather pass{feedNote}" ]
    let what =
        if positions then "gathers the values and fills the kept positions (m + 1 cells each, the slack never exposed)"
        else "gathers the values (m + 1 cells, the slack never exposed; positions elided)"
    [ $"{ind}// compaction fusion (Blade.Optimize.planCompactionFusion): `{maskName}` was counted in its own mask pass; ONE branchless pass {what}"
      $"{ind}size_t {idxName}_grid = {maskName}.extents[0];"
      $"{ind}bool* {idxName}_pool = nested_array_utilities::pool_base({maskName}.data);"
      $"{ind}size_t {name}_trail = 1;"
      $"{ind}{elemCpp}* {name}_densepool = nested_array_utilities::pool_base({denseName}.data);" ]
    @ (pos |> Option.map (fun p -> [ $"{ind}compound_index_t<1>::IDX* {p} = new compound_index_t<1>::IDX[{cnt} + 1];" ]) |> Option.defaultValue [])
    @ [ $"{ind}{elemCpp}* {name}_compact = new {elemCpp}[{cnt} + 1];" ]
    @ (feeds |> List.collect (fun (_, acc, wrapperCode, _) ->
        (wrapperCode |> List.map (fun s -> ind + s)) @ [ $"{ind}{elemCpp} {acc} = {elemCpp}(0);" ]))
    @ b4Pass ind $"{name}_compact" pos (feeds |> List.map (fun (_, acc, _, wn) -> (acc, wn)))
    @ adopt $"{name}_compact" pos "the table this pass filled"

/// A companion of a SHARED SELECTION (Blade.Optimize.planCompactionFusion,
/// T4c): every `y_j = compound(c_j, keep)` over one mask shares ONE
/// `compound_index_t<1>` (`<keep>__sel`) owned by the plan.
///   * the FIRST companion completes the selection: ONE branchless pass over
///     the source axis gathers every companion (`b = keep[i]; v_j[q] =
///     c_j[i]; [pos[q] = i;] q += b`, m + 1 cells per buffer, the slack never
///     exposed) and feeds each companion's in-pass serial chains (B4c) --
///     unless `keep`'s own pass already gathered (T4a, `gather`), when it
///     only runs that pass itself while the run-time cap guard is false --
///     then builds the index from the mask pass's count and the positions
///     (or cardinality-only, positions elided by every companion's census);
///   * every companion's wrapper borrows the index and owns only its own
///     compact data store (`deallocate_compound_shared_index`); in a scoped
///     frame the ONE index-only free would be registered with the last
///     companion -- registerShapedAlloc is a no-op at main's top level, where
///     compound() lives (BL6002), so no registration and no free is executed:
///     every compound keeps program lifetime.
/// None: the selection is not available here (the mask did not count, a
/// source has no name in scope, or the first companion fell back), and the
/// caller takes its ordinary path -- correct by itself, one index of its own.
and genSharedCompanion (ctx: CodeGenContext) (ind: string) (binding: IRBinding) (name: string) (elemCpp: string) (denseName: string) (maskName: string) (p: Blade.Optimize.CompactionPlan) (s: Blade.Optimize.SharedSelection) : string list option =
    let idx = $"{maskName}__sel"
    let (firstId, _, _) = List.head s.Members
    let (lastId, lastName, _) = List.last s.Members
    let isLast = binding.Id = lastId
    let companions = s.Members |> List.map (fun (_, n, _) -> $"`{n}`") |> String.concat ", "
    let posNote = if s.PositionsNeeded then "keyed reads by binary search over the shared kept positions" else "positions elided: no companion's census found a reader"
    let wrap (compact: string) (how: string) =
        registerShapedAlloc name "deallocate_compound_shared_index" name
        if isLast then registerShapedAlloc idx "deallocate_compound_index_only" idx
        recordCodegenDecision "compound" name Blade.Effects.Applied
            [ $"representation: rank-1 compaction sharing `{maskName}`'s ONE selection index with {s.Members.Length} companions ({companions}) -- compaction fusion v2, T4c; no hash table; {posNote}"
              $"{how}; `{name}` owns its compact data store only; the index is the plan's (no companion frees it; at main's top level no free is executed: program lifetime)" ]
        [ $"{ind}size_t {name}_trail = 1;"
          $"{ind}compound_index_t<1>* {name}_idx = {idx}; // the shared selection's index: owned by the plan, borrowed by `{name}`"
          $$"""{{ind}}nested_array_utilities::Compound<{{elemCpp}}, 1> {{name}} { {{compact}}, {{idx}}, {{name}}_trail };""" ]
        @ (if isLast then
               [ $"{ind}// T4c lifetime: `{idx}` is owned by the plan, never by a companion (each owns only its own data: deallocate_compound_shared_index); in a scoped frame its ONE index-only free (deallocate_compound_index_only({idx})) would be registered here, with the last companion `{name}` -- at main's top level (compound() is top-level only, BL6002) there is no scope, so no registration and no free is executed: program lifetime" ]
           else [])
    if binding.Id <> firstId then
        match s.BuiltCpp.Value with
        | Some b when b.Index.IsSome && b.Compacts.ContainsKey binding.Id ->
            Some ([ $"{ind}// compaction fusion v2, shared selection (T4c): `{name}`'s cells were gathered by the selection's one pass; its wrapper adopts its own buffer and the shared index" ]
                  @ wrap b.Compacts.[binding.Id] "gathered by the shared selection's one pass (complete before the first companion was bound)")
        | _ -> None
    else
    match p.CountCpp.Value with
    | None -> None
    | Some cnt ->
    let srcs =
        s.Members |> List.mapi (fun j (cid, _, src) ->
            let srcName = if cid = binding.Id then Some denseName else Map.tryFind src ctx.VarNames
            (cid, j, srcName, elemTypeToCpp s.ElemTypes.[cid]))
    // FAIL CLOSED: the joint pass reads every source's pool BY NAME here, so
    // a source this emission still holds as a deferred computation (a name
    // with no pool behind it) takes the ordinary per-compound path instead --
    // never forcing a later companion's operand early (its producer may fail).
    // The planner's storage proof makes this unreachable; it is checked anyway.
    if srcs |> List.exists (fun (_, _, n, _) -> n.IsNone)
       || s.Members |> List.exists (fun (_, _, src) -> ctx.DeferredComputations.ContainsKey src) then None
    else
    let built = s.BuiltCpp.Value |> Option.filter (fun b -> b.AtMask)
    let bufOf (cid: IRId) (j: int) =
        match built with
        | Some b when b.Compacts.ContainsKey cid -> b.Compacts.[cid]
        | _ -> $"{idx}_v{j}"
    let pos =
        match built with
        | Some b -> b.Pos
        | None -> if s.PositionsNeeded then Some $"{idx}_pos" else None
    let i1 = ind + "    "
    // The joint pass into the given buffers (declared or not), feeding `feeds`.
    let jointPass (ind: string) (declare: bool) (feeds: (string * string * string) list) =
        let i1 = ind + "    "
        let posTy = if declare then "compound_index_t<1>::IDX* " else ""
        let tyOf (e: string) = if declare then e + "* " else ""
        [ $"{ind}bool* {idx}_pool = nested_array_utilities::pool_base({maskName}.data);" ]
        @ (srcs |> List.map (fun (_, j, n, e) -> $"{ind}{e}* {idx}_d{j} = nested_array_utilities::pool_base({n.Value}.data);"))
        @ (pos |> Option.map (fun pv -> [ $"{ind}{posTy}{pv} = new compound_index_t<1>::IDX[{cnt} + 1];" ]) |> Option.defaultValue [])
        @ (srcs |> List.map (fun (cid, j, _, e) -> $"{ind}{tyOf e}{bufOf cid j} = new {e}[{cnt} + 1];"))
        @ [ $"{ind}{{"
            $"{i1}size_t {idx}_q = 0;" ]
        @ compactionPassLoopMulti i1 idx $"{idx}_i" $"{idx}_grid" $"{idx}_q" feeds (fun body ->
            (srcs |> List.map (fun (cid, j, _, _) -> $"{body}{bufOf cid j}[{idx}_q] = {idx}_d{j}[{idx}_i];"))
            @ (pos |> Option.map (fun pv -> [ $"{body}{pv}[{idx}_q][0] = {idx}_i;" ]) |> Option.defaultValue [])
            @ [ $"{body}{idx}_q += (size_t){idx}_pool[{idx}_i];" ])
        @ [ $"{ind}}}" ]
    let passLines, how =
        match built with
        | Some b ->
            let fallback =
                match b.Guard with
                | None -> []
                | Some guard ->
                    [ $"{ind}if (!{guard}) {{"
                      $"{i1}// n is over the selectivity cap (BLADE_TILE_TUNE sel_cap): the joint pass here, m + 1 cells per buffer" ]
                    @ jointPass i1 false b.Feeds
                    @ [ $"{ind}}}" ]
            ([ $"{ind}// compaction fusion v2, shared selection (T4c + T4a): `{maskName}`'s own pass counted AND gathered the {s.Members.Length} companions; the first companion completes the shared index"
               $"{ind}size_t {idx}_grid = {maskName}.extents[0];" ]
             @ fallback,
             (match b.Guard with
              | Some _ -> $"gathered in `{maskName}`'s mask pass (T4a) while n is within the selectivity cap, else by the joint pass at `{name}`"
              | None -> $"gathered in `{maskName}`'s mask pass (T4a)"))
        | None ->
            // every companion's in-pass serial chains (B4c), each over its own buffer
            let feeds =
                srcs |> List.collect (fun (cid, j, _, _) ->
                    Blade.Optimize.CompactionTable.foldsOf cid |> List.choose (fun f ->
                        if not f.InPass then None
                        else
                        match resolveCallable f.FoldKernel with
                        | Some cl when cl.Params.Length = 2 && not (fpReassocEnabled () && foldReorderLicensed cl) -> Some (f, cl, bufOf cid j)
                        | _ -> None))
                |> List.mapi (fun k (f, cl, buf) ->
                    let (wrapperCode, wname) = genCallableWrapper ctx.VarNames $"{idx}__fold{k}" cl
                    (f, $"{idx}__fold{k}", wrapperCode, wname, buf))
            if not feeds.IsEmpty then (foldLanesUsedCell ()).Value <- true
            for (f, acc, _, _, _) in feeds do f.EmittedCpp.Value <- Some acc
            let feedNote = if feeds.IsEmpty then "" else $"; the serial chain of {feeds.Length} fold(s) fed per 256-cell block"
            let posWhat = if pos.IsSome then " and fills the shared kept positions" else " (positions elided)"
            ([ $"{ind}// compaction fusion v2, shared selection (T4c): `{maskName}` was counted in its own mask pass; ONE branchless pass gathers its {s.Members.Length} companions ({companions}){posWhat}, m + 1 cells per buffer, the slack never exposed"
               $"{ind}size_t {idx}_grid = {maskName}.extents[0];" ]
             @ (feeds |> List.collect (fun (_, acc, wrapperCode, _, _) ->
                    (wrapperCode |> List.map (fun l -> ind + l)) @ [ $"{ind}double {acc} = double(0);" ]))
             @ jointPass ind true (feeds |> List.map (fun (_, acc, _, wn, buf) -> (acc, wn, buf))),
             $"gathered by the shared selection's one joint pass at `{name}`{feedNote}")
    let idxLine =
        match pos with
        | Some pv -> $"{ind}compound_index_t<1>* {idx} = new compound_index_t<1>(\"{idx}\", {idx}_grid, {cnt}, {pv}); // the SHARED selection index (T4c): one build for {s.Members.Length} companions, owned by the plan"
        | None -> $"{ind}compound_index_t<1>* {idx} = new compound_index_t<1>(\"{idx}\", {idx}_grid, {cnt}, compound_index_t<1>::positions_elided); // the SHARED selection index (T4c), positions elided: one build for {s.Members.Length} companions, owned by the plan"
    s.BuiltCpp.Value <-
        Some { Blade.Optimize.SharedSelectionCpp.Index = Some idx
               Blade.Optimize.SharedSelectionCpp.Compacts = srcs |> List.map (fun (cid, j, _, _) -> (cid, bufOf cid j)) |> Map.ofList
               Blade.Optimize.SharedSelectionCpp.Pos = pos
               Blade.Optimize.SharedSelectionCpp.Guard = (match built with Some b -> b.Guard | None -> None)
               Blade.Optimize.SharedSelectionCpp.Feeds = (match built with Some b -> b.Feeds | None -> [])
               Blade.Optimize.SharedSelectionCpp.AtMask = built.IsSome }
    Some (passLines @ [ idxLine ] @ wrap (bufOf binding.Id (srcs |> List.findIndex (fun (cid, _, _, _) -> cid = binding.Id))) how)

and genSparseInitBinding (ctx: CodeGenContext) (binding: IRBinding) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Sparse-construction constructor (`let S = sparse(values, keys)`):
    // materialize the sparse index from the keys source (genSparseIndexFromKeys
    // -- static literal table or runtime tuple-array loop), then copy the values
    // buffer straight into a compact pool. NO scatter: the values arrive
    // already in key order -- that is the builder's contract, and the reason
    // this is a flat pool copy where genCompoundInitBinding needs compactScatter.
    //
    // Trailing dims (values rank > 1, mirroring the compound builder's
    // leading-prefix rule): values.extents[0] is the key axis and the REMAINING
    // extents fold into trailing_stride, so each key owns a contiguous block of
    // `trail` elements. The copy walks the flattened pool (pool_base), which is
    // exactly the key-major layout the Sparse wrapper indexes.
    // A |values| != |keys| mismatch panics at runtime (BL8001).
    let valuesExpr = ctx.SparseInits.[binding.Id]
    let valuesName = exprToCpp ctx.VarNames valuesExpr
    (match binding.Type with
     | ArrayElem arrTy when isSparseArrayType arrTy ->
         let sparseIx = arrTy.IndexTypes |> List.find (fun ix -> ix.IxKind = IxKSparse)
         let leadRank = sparseIx.Rank
         let elemCpp = elemTypeToCpp arrTy.ElemType
         // Trailing dim count = slots after the sparse head (the key axis
         // consumed values dim 0, so these are values dims 1..).
         let trailingDimCount = arrTy.IndexTypes.Length - 1
         let idxName = $"{name}_idx"
         let idxLines =
             match sparseIx.Extent with
             | IRSparseKeys ((SkStatic _ | SkDomain _) as src) -> genSparseIndexFromKeys src None leadRank idxName
             | IRSparseKeys (SkRuntime (IRVar (kid, _)) as src) ->
                 genSparseIndexFromKeys src (Map.tryFind kid ctx.VarNames) leadRank idxName
             | _ -> [ refusalErrorLine "" ($"sparse() binding '{name}': keys source is not a SparseIdx extent") ]
         let trailTerms =
             [ for d in 1 .. trailingDimCount -> $"{valuesName}.extents[{d}]" ]
         let trailExpr = match trailTerms with | [] -> "1" | xs -> String.concat " * " xs
         let lines =
             (idxLines |> List.map (fun l -> ind + l))
             @ [ $"{ind}if ({valuesName}.extents[0] != {idxName}->cardinality) {{ blade_rt::panic(\"BL8001\", \"sparse(values, keys): values length does not match key count\", {(declPanicArgs ())}); }}"
                 $"{ind}size_t {name}_trail = {trailExpr};"
                 $"{ind}{elemCpp}* {name}_valpool = nested_array_utilities::pool_base({valuesName}.data);"
                 $"{ind}{elemCpp}* {name}_compact = new {elemCpp}[{idxName}->cardinality * {name}_trail];"
                 $"{ind}for (size_t __r = 0; __r < {idxName}->cardinality * {name}_trail; __r++) {name}_compact[__r] = {name}_valpool[__r];"
                 $$"""{{ind}}nested_array_utilities::Sparse<{{elemCpp}}, {{leadRank}}> {{name}} { {{name}}_compact, {{idxName}}, {{name}}_trail };""" ]
         // Owns BOTH the compact buffer and the freshly built index.
         registerShapedAlloc name "deallocate_sparse" name
         (lines, addVarName binding.Id name ctx)
     | _ ->
         ([refusalErrorLine ind ($"sparse() binding '{name}' is not a SparseIdx array type")], addVarName binding.Id name ctx))


/// Rearrangement combinators (group_by, sort, mask, transpose, decompact,
/// intersect, union, unique, array negate/conjugate) index their array
/// inputs by NAME in the emitted C++, so they need a MATERIALIZED input.
/// An input that is still an unforced computation -- a deferred binding
/// (only a "<deferred computation>" comment in the C++), or an inline
/// computation node -- is forced here first, exactly as |> compute would
/// force it. Returns (forceCode, ctx', expr') where expr' names the
/// materialized array; callers rebuild their form node from expr' before
/// rendering. `tmpBase` names the synthetic temporary when the input is an
/// inline computation (and is the fallback name for a deferred binding
/// missing from VarNames); pass a distinct base per input slot.
and forceDeferredArrayInput (ctx: CodeGenContext) (builder: IRBuilder) (tmpBase: string) (expr: IRExpr) : string list * CodeGenContext * IRExpr =
    match expr with
    | IRVar (srcId, ty) when Map.containsKey srcId ctx.DeferredComputations
                             && Map.containsKey srcId ctx.DeferredAliasOf ->
        // A DEFERRED ALIAS (`let g2 = g`; gap G60): the alias is the same
        // deferred value as its source, so forcing it forces the SOURCE under
        // its own name first (recursively, for a chain of aliases; a source
        // some earlier use already forced is simply stored), then binds the
        // alias through the ordinary alias path over the now-stored source --
        // the assignable deep copy (genVarAliasBinding), so a later element
        // write through either name is not seen through the other. At main's
        // top level both join the auto-print list, as the interpreter's force
        // and the llvm lane's markForced record them.
        let rootId = ctx.DeferredAliasOf.[srcId]
        let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue tmpBase
        let (rootCode, ctxR, _) = forceDeferredArrayInput ctx builder ($"{tmpBase}__src") (IRVar (rootId, ty))
        let ctxR = { ctxR with DeferredComputations = Map.remove srcId ctxR.DeferredComputations
                               DeferredAliasOf = Map.remove srcId ctxR.DeferredAliasOf }
        let aliasBinding = { Id = srcId; Name = srcName; Type = ty; Value = IRVar (rootId, ty); IsConst = false; IsMutable = true }
        let (code, ctx1) = genVarAliasBinding ctxR aliasBinding builder rootId
        if ctx.Indent = 0 then
            let cell = forcedDeferredIdsCell ()
            cell.Value <- Set.add srcId cell.Value
        (rootCode @ code @ [""], ctx1, expr)
    | IRVar (srcId, ty) when Map.containsKey srcId ctx.DeferredComputations ->
        // Materialize under the deferred binding's own name, then drop it
        // from the deferred map: later consumers (including a second
        // rearrangement over the same binding) must see the materialized
        // array, not re-force it into a C++ redefinition.
        let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue tmpBase
        let matBinding = { Id = srcId; Name = srcName; Type = ty; Value = IRCompute (IRVar (srcId, ty)); IsConst = true; IsMutable = false }
        let (code, ctx1) = genBinding ctx matBinding builder
        let ctx1 = { ctx1 with DeferredComputations = Map.remove srcId ctx1.DeferredComputations }
        // A module-level binding materialized here at main's top level joins
        // the auto-print list (genPrintStatements consults the cell); a
        // nested-scope force (Indent > 0) stays block-scoped and unprintable.
        if ctx.Indent = 0 then
            let cell = forcedDeferredIdsCell ()
            cell.Value <- Set.add srcId cell.Value
        (code @ [""], ctx1, expr)
    | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRComposeMeth _ | IRBind _ | IRCompute _ ->
        // Inline computation as the array argument: materialize into a
        // synthetic temporary and rearrange over that.
        let tmpId = builder.FreshId()
        let ty = inferExprType expr
        let matBinding = { Id = tmpId; Name = tmpBase; Type = ty; Value = IRCompute expr; IsConst = true; IsMutable = false }
        let (code, ctx1) = genBinding ctx matBinding builder
        (code @ [""], ctx1, IRVar (tmpId, ty))
    | _ -> ([], ctx, expr)


/// One operand of `compound(dense, mask)` (genCompoundInitBinding), which the
/// index build and the scatter read BY NAME. Returns (code, C++ name, ctx').
///   - a let-bound operand still DEFERRED is materialized under its own name
///     (forceDeferredArrayInput; a no-op for a stored array);
///   - an operand COMPUTED in place -- `compound(W * S, m)`, a conjunction of
///     masks, an inline map `compound(method_for(a, b) <@> k, m)` -- is
///     emitted as the binding the user would have written (`let ws = W * S`)
///     through the ordinary let path. That path DEFERS a computation-shaped
///     value (a map, a compose-apply, a zip): it emits only a
///     `// <name> = <deferred computation>` comment and records the value in
///     DeferredComputations, so the temp is then forced exactly like a
///     let-bound deferred operand. Both steps thread the context: genBinding
///     may itself have forced a deferred producer the operand reads, and a
///     later consumer must see that one materialized, not force it again.
/// The temp is local to this constructor: not a module binding, so it is not
/// printed and takes no name the program could see.
and materializeCompoundOperand (ctx: CodeGenContext) (builder: IRBuilder) (tmpName: string) (expr: IRExpr) : string list * string * CodeGenContext =
    match expr with
    | IRVar _ ->
        let (forceCode, ctxF, expr') = forceDeferredArrayInput ctx builder tmpName expr
        (forceCode, exprToCpp ctxF.VarNames expr', ctxF)
    | IRParam _ -> ([], exprToCpp ctx.VarNames expr, ctx)
    | computed ->
        let tmp : IRBinding =
            { Id = builder.FreshId()
              Name = tmpName
              Type = inferExprType computed
              Value = computed
              IsConst = true
              IsMutable = false }
        let (tmpLines, ctxT) = genBinding ctx tmp builder
        let (forceCode, ctxF, _) = forceDeferredArrayInput ctxT builder tmpName (IRVar (tmp.Id, tmp.Type))
        (tmpLines @ forceCode, bindingCppName tmp, ctxF)


/// Force every deferred-computation binding that `value` reads positionally
/// (see collectDeferredPositionalReads) so it is materialized in scope before
/// the by-name reads are emitted. Returns the prelude force-code plus a ctx
/// with those bindings dropped from DeferredComputations. Idempotent: a binding
/// already forced (removed from the map) is skipped by the collector, so this
/// composes cleanly with the per-input forcing the rearrangement combinators do.
and forceDeferredPositionalReads (ctx: CodeGenContext) (builder: IRBuilder) (tmpBase: string) (value: IRExpr) : string list * CodeGenContext =
    forceDeferredBindingIds ctx builder tmpBase (collectDeferredPositionalReads ctx value)

/// Materialize each deferred binding id in `ids`, threading the ctx so a
/// binding forced by an earlier id (or already forced by the caller) is
/// skipped rather than emitted twice. Shared by the positional-read pre-pass
/// and the kernel-capture forcing in genComputeBinding.
and forceDeferredBindingIds (ctx: CodeGenContext) (builder: IRBuilder) (tmpBase: string) (ids: IRId list) : string list * CodeGenContext =
    ids |> List.fold (fun (accCode, accCtx: CodeGenContext) id ->
        if not (Map.containsKey id accCtx.DeferredComputations) then (accCode, accCtx) else
        // Recover the array type from the deferred computation so the
        // materialized binding is typed correctly (forceDeferredArrayInput's
        // IRVar arm carries the IRVar's type onto the synthesized binding).
        let ty =
            match Map.tryFind id accCtx.DeferredComputations with
            | Some defExpr -> inferExprType defExpr
            | None -> IRTUnit
        let (code, ctx', _) = forceDeferredArrayInput accCtx builder ($"{tmpBase}_{id}") (IRVar (id, ty))
        (accCode @ code, ctx')
    ) ([], ctx)


and genMaskBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (predExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // mask(array, pred): a Bool PRESENCE array in the SOURCE index space --
    // m[i] = pred(A[i]), same extent as A, NO compaction (compaction happens
    // downstream in compound(A, m)). See materializeMaskForm.
    // Strict elem-type inference (emits #error if unresolvable) and
    // predicate-callable validation happen here at the call site; the
    // shared `materializeInlineForm` helper just emits the C++ template.
    //
    // Validation accepts any predicate that resolves to a
    // single-parameter callable through resolveCallable.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "mask"
    let elemStr = elemTypeToCpp elemET
    let predErrCode =
        match resolveCallable predExpr with
        | Some callable when callable.Params.Length = 1 -> []
        | _ -> codegenError ctx ind "mask: predicate must resolve to a single-parameter callable; got something else (typechecker or IR bug)"
    // Compaction fusion (Blade.Optimize.planCompactionFusion): a planned mask
    // also COUNTS its kept cells in this pass, and publishes the count's name
    // for the compound that consumes it (which then skips its count pass).
    // T4a: a plan whose gather runs at the mask site also gathers the
    // compound in this pass (and feeds its in-pass fold chains); every
    // handshake is reset first, so a fold reads an accumulator only when THIS
    // emission fed it.
    let plan = Blade.Optimize.CompactionTable.tryMask binding.Id
    plan |> Option.iter (fun p ->
        p.CountCpp.Value <- None
        p.GatherCpp.Value <- None
        for f in Blade.Optimize.CompactionTable.foldsOf p.CompoundId do f.EmittedCpp.Value <- None
        // T4c: the shared selection's handshake, and every companion's chains
        p.Shared |> Option.iter (fun s ->
            s.BuiltCpp.Value <- None
            for (cid, _, _) in s.Members do
                for f in Blade.Optimize.CompactionTable.foldsOf cid do f.EmittedCpp.Value <- None))
    let countVar = plan |> Option.map (fun _ -> $"{name}__count")
    // T4c: a shared selection's mask-site gather reads every companion source
    // by name -- FAIL CLOSED (count only; the first companion then declines
    // too) when one is still a deferred computation in this emission.
    let gatherPlan =
        plan |> Option.filter (fun p ->
            p.GatherAtMask
            && (match p.Shared with
                | Some s -> not (s.Members |> List.exists (fun (_, _, src) -> ctx.DeferredComputations.ContainsKey src))
                | None -> true))
    let matStmts =
        match materializeMaskFormCounted emptySubst ctx.VarNames name elemStr arrExpr predExpr countVar gatherPlan with
        | Some (s, allocs, counted, gathered) ->
            registerMaterializedAllocs allocs
            (match plan, countVar with
             | Some p, Some c when counted ->
                 p.CountCpp.Value <- Some c
                 p.GatherCpp.Value <- gathered
             | _ -> ())
            s
        | None -> []  // Unreachable: helper supports IRMask
    let code = forceCode @ elemErrCode @ predErrCode @ [$"{ind}// mask: count + compact"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genIntersectBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (aExpr: IRExpr) (bExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // intersect(A, B): elements present in both arrays.
    let (forceCodeA, ctx, aExpr) = forceDeferredArrayInput ctx builder ($"{name}__a") aExpr
    let (forceCodeB, ctx, bExpr) = forceDeferredArrayInput ctx builder ($"{name}__b") bExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "intersect"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) (IRIntersect (aExpr, bExpr)) with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCodeA @ forceCodeB @ elemErrCode @ [$"{ind}// intersect: build set from B, scan A"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genUnionBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (aExpr: IRExpr) (bExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // union(A, B): all elements from A, plus elements from B not in A.
    let (forceCodeA, ctx, aExpr) = forceDeferredArrayInput ctx builder ($"{name}__a") aExpr
    let (forceCodeB, ctx, bExpr) = forceDeferredArrayInput ctx builder ($"{name}__b") bExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind aExpr "union"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) (IRUnion (aExpr, bExpr)) with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCodeA @ forceCodeB @ elemErrCode @ [$"{ind}// union: all of A, plus elements from B not in A"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genUniqueBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // unique(A): dedup, preserving first-occurrence order. Two-pass:
    // first counts unique elements via std::unordered_set, then fills
    // the output array on a second pass (clearing the set in between
    // so first-occurrence membership testing repeats identically).
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "unique"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) (IRUnique arrExpr) with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCode @ elemErrCode @ [$"{ind}// unique: dedup via unordered_set, first-occurrence order"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genRangeBinding (ctx: CodeGenContext) (binding: IRBinding) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // A BARE range in binding position (`let xs = 0..n`, `let r = range<I>`,
    // or either behind `|> compute`) materializes the iota it denotes. Inside
    // a combinator a range never reaches this arm -- the nest peels it as
    // induction values, which is the whole point of a virtual array. Only the
    // single-slot plain dense form has a standalone value meaning; compound/
    // sparse/halo slots enumerate coordinate sets and a multi-slot range only
    // means anything to a loop nest, so those keep a refusal.
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy "int64_t") binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None ->
            codegenError ctx ind "a compound/sparse/halo or multi-slot range has no standalone value form; iterate it via method_for(range<...>)"
    let code = [$"{ind}// range: materialized iota"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



/// The AGGREGATE-ONLY grouping's one pass (Blade.Optimize.planGroupAggregate,
/// gap G71): the grouping's own ABI (`__ngroups`, `__offsets`, `__nsrc`,
/// `_extents`, the void* sentinel) WITHOUT a permutation, plus one
/// accumulator per peel state (`<gk>__agg<j>`, G cells, adopted by the peel)
/// and the per-group row counts (`<gk>__counts`). Per row, in source order:
/// the key, the negative-key drop and the BL8006 guards exactly as the CSR's
/// first key pass (positional / EnumIdx), the group (positional: the key;
/// EnumIdx: the declared-order lookup; dynamic: ONE find-or-insert in
/// blade_member.h's key -> group map, first-occurrence numbering), then each
/// state's step -- UNSEEDED: the first value copied (no kernel call), every
/// later one `acc = k(acc, v)`; SEEDED: `acc = k(acc, v)` from the seed
/// stored once per group (before the pass for the static regimes, at the
/// group's first row for the dynamic one); PRODSUM: `acc += a * b` from 0 --
/// and the count. None when this site cannot place the plan (the regime the
/// binding's type names differs from the plan's): today's CSR follows.
and genGroupAggKeysBinding (ctx: CodeGenContext) (binding: IRBinding) (key: IRExpr) (plan: Blade.Optimize.GroupAggPlan) : (string list * CodeGenContext) option =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let regimeHere =
        match binding.Type with
        | IRTGroupKeys (outer, _, ev) ->
            (match outer.Extent, ev with
             | IRLit (IRLitInt n), None -> Some (Blade.Optimize.GaPositional n)
             | IRLit (IRLitInt _), Some vs when vs |> List.forall (function EVInt _ -> true | _ -> false) ->
                 Some (Blade.Optimize.GaEnum (vs |> List.choose (function EVInt n -> Some n | _ -> None)))
             | IRLit _, _ -> None
             | _, None -> Some Blade.Optimize.GaDynamic
             | _ -> None)
        | _ -> None
    if regimeHere <> Some plan.Regime then None else
    let keysName = exprToCppCtx ctx key
    let (keysBound, keysAt) = compactOrDenseSource key keysName
    let elemCpp (et: ElemType) = elemTypeToCpp (IRTScalar et)
    let dynamic = (plan.Regime = Blade.Optimize.GaDynamic)
    // per-state lvalues: the static regimes' accumulators, the dynamic
    // regime's record slots (slot 0 the count, slot j + 1 state j)
    let accAt (j: int) (st: Blade.Optimize.GroupAggState) =
        if dynamic then (if st.Elem = ETInt64 then $"__rec[{j + 1}].i" else $"__rec[{j + 1}].d")
        else $"{name}__agg{j}[__g]"
    let cntAt = if dynamic then "__rec[0].i" else $"{name}__counts[__g]"
    let srcAt (e: IRExpr) =
        let (_, at) = compactOrDenseSource e (exprToCppCtx ctx e)
        at "__ki"
    let wrappers =
        plan.States |> List.mapi (fun j st ->
            match st.Kind with
            | Blade.Optimize.GaSeeded (fk, _) | Blade.Optimize.GaUnseeded fk ->
                (match resolveCallable fk with
                 | Some cl ->
                     let (code, wname) = genCallableWrapper ctx.VarNames $"{name}__ga{j}" cl
                     Some (code, wname)
                 | None -> None)
            | _ -> None)
    let stepLines =
        plan.States |> List.mapi (fun j st ->
            let acc = accAt j st
            match st.Kind, wrappers.[j] with
            | Blade.Optimize.GaUnseeded _, Some (_, w) ->
                [ $"{ind}    {{ const {elemCpp st.Elem} __v = {srcAt st.Sources.[0]}; if ({cntAt} == 0) {acc} = __v; else {acc} = {w}({acc}, __v); }}" ]
            | Blade.Optimize.GaSeeded (_, seed), Some (_, w) ->
                (if dynamic then [ $"{ind}    if ({cntAt} == 0) {acc} = {exprToCppCtx ctx seed};" ] else [])
                @ [ $"{ind}    {acc} = {w}({acc}, {srcAt st.Sources.[0]});" ]
            | Blade.Optimize.GaProdSum, _ ->
                (if dynamic then [ $"{ind}    if ({cntAt} == 0) {acc} = 0;" ] else [])
                @ [ $"{ind}    {acc} += {srcAt st.Sources.[0]} * {srcAt st.Sources.[1]};" ]
            | Blade.Optimize.GaCount, _ -> []
            | _ -> [ "\u0000" ])
        |> List.concat
    if stepLines |> List.contains "\u0000" then None else
    plan.EmittedCpp.Value <- true
    let stateNote =
        plan.States |> List.mapi (fun j st ->
            let k =
                match st.Kind with
                | Blade.Optimize.GaUnseeded _ -> "unseeded reduce"
                | Blade.Optimize.GaSeeded _ -> "seeded reduce"
                | Blade.Optimize.GaProdSum -> "prodsum"
                | Blade.Optimize.GaCount -> "count"
            $"{j}: `{st.PeelLabel}` {k}") |> String.concat ", "
    let header =
        [ $"{ind}// group-aggregate v1 (Blade.Optimize.planGroupAggregate): ONE pass over the rows in source order, no permutation, no gather; states [{stateNote}]" ]
    let wrapperLines = wrappers |> List.choose id |> List.collect (fun (code, _) -> code |> List.map (fun l -> ind + l))
    let dropNeg = negativeKeyDrop (IRTScalar ETInt64) "__k" ind
    let openRow =
        [ $$"""{{ind}}for (size_t __ki = 0; __ki < {{keysBound}}; __ki++) {""" ]
    let closeRow = [ $"{ind}    {cntAt}++;"; $"{ind}}}" ]
    let tail =
        [ $"{ind}size_t {name}__nsrc = {keysBound}; // source rows (>= offsets[ngroups]; negative keys drop)"
          $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
          $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets, {name}__agg<j> (group-aggregate: no permutation)" ]
    let code =
        match plan.Regime with
        | _ when not dynamic ->
            let ngroups =
                match plan.Regime with
                | Blade.Optimize.GaPositional n -> n
                | Blade.Optimize.GaEnum vs -> int64 vs.Length
                | Blade.Optimize.GaDynamic -> 0L
            let accDecls =
                plan.States |> List.mapi (fun j st ->
                    match st.Kind with
                    | Blade.Optimize.GaCount -> []
                    | Blade.Optimize.GaSeeded (_, seed) ->
                        [ $"{ind}{elemCpp st.Elem}* {name}__agg{j} = new {elemCpp st.Elem}[{ngroups}];"
                          $"{ind}for (size_t __g = 0; __g < {ngroups}; __g++) {name}__agg{j}[__g] = {exprToCppCtx ctx seed};" ]
                    | Blade.Optimize.GaProdSum ->
                        [ $"{ind}{elemCpp st.Elem}* {name}__agg{j} = new {elemCpp st.Elem}[{ngroups}];"
                          $"{ind}for (size_t __g = 0; __g < {ngroups}; __g++) {name}__agg{j}[__g] = 0;" ]
                    | Blade.Optimize.GaUnseeded _ ->
                        [ $"{ind}{elemCpp st.Elem}* {name}__agg{j} = new {elemCpp st.Elem}[{ngroups}];" ])
                |> List.concat
            let groupOf =
                match plan.Regime with
                | Blade.Optimize.GaEnum vs ->
                    let entries = vs |> List.mapi (fun i v -> $"{{{v}LL, (size_t){i}}}") |> String.concat ", "
                    [ $"""{ind}    size_t __g = {name}__bucket({(keysAt "__ki")});""" ],
                    [ $"{ind}static const std::unordered_map<int64_t, size_t> {name}__bucket_map = {{{entries}}};"
                      $$"""{{ind}}auto {{name}}__bucket = [](const int64_t& __v) -> size_t { auto it = {{name}}__bucket_map.find(__v); if (it == {{name}}__bucket_map.end()) blade_rt::panic("BL8006", "index out of bounds: a group_keys key outside its EnumIdx", {{declPanicArgs ()}}); return it->second; };""" ]
                | _ ->
                    [ $"""{ind}    int64_t __k = {(keysAt "__ki")};""" ] @ dropNeg
                    @ [ $$"""{{ind}}    if ((size_t)__k >= {{ngroups}}) blade_rt::panic("BL8006", "index out of bounds: a group_keys key outside 0 .. {{ngroups - 1L}}", {{declPanicArgs ()}});"""
                        $"{ind}    size_t __g = (size_t)__k;" ],
                    []
            let (rowGroup, enumDecls) = groupOf
            header
            @ [ $"{ind}size_t {name}__ngroups = {ngroups};"
                $$"""{{ind}}size_t {{name}}__counts[{{ngroups}}] = {0};""" ]
            @ enumDecls @ accDecls @ wrapperLines
            @ openRow @ rowGroup @ stepLines @ closeRow
            @ [ $"{ind}size_t {name}__offsets[{ngroups + 1L}];"
                $"{ind}{name}__offsets[0] = 0;"
                $"{ind}for (size_t __gi = 0; __gi < {ngroups}; __gi++) {name}__offsets[__gi + 1] = {name}__offsets[__gi] + {name}__counts[__gi];" ]
            @ tail
        | _ ->
            (memberUsedCell ()).Value <- true
            let slots = plan.States.Length + 1
            let copyOut =
                plan.States |> List.mapi (fun j st ->
                    match st.Kind with
                    | Blade.Optimize.GaCount -> []
                    | _ ->
                        let fld = if st.Elem = ETInt64 then "i" else "d"
                        [ $"{ind}{elemCpp st.Elem}* {name}__agg{j} = new {elemCpp st.Elem}[{name}__ngroups > 0 ? {name}__ngroups : 1];"
                          $"{ind}for (size_t __g = 0; __g < {name}__ngroups; __g++) {name}__agg{j}[__g] = blade_gkmap_record_i64(&{name}__map, (int64_t)__g)[{j + 1}].{fld};" ])
                |> List.concat
            header
            @ [ $"{ind}blade_gkmap_i64 {name}__map;"
                $"""{ind}if (!blade_gkmap_init_i64(&{name}__map, {slots})) blade_rt::panic("BL8005", "group_keys: the key -> group map could not be allocated", {declPanicArgs ()});""" ]
            @ wrapperLines
            @ openRow
            @ [ $"""{ind}    int64_t __k = {(keysAt "__ki")};""" ] @ dropNeg
            @ [ $"{ind}    int64_t __gi = blade_gkmap_group_i64(&{name}__map, __k);"
                $"""{ind}    if (__gi < 0) blade_rt::panic("BL8005", "group_keys: the key -> group map could not grow", {declPanicArgs ()});"""
                $"{ind}    blade_gk_slot* __rec = blade_gkmap_record_i64(&{name}__map, __gi);" ]
            @ stepLines @ closeRow
            @ [ $"{ind}size_t {name}__ngroups = (size_t)blade_gkmap_count_i64(&{name}__map);"
                $"{ind}size_t* {name}__counts = new size_t[{name}__ngroups > 0 ? {name}__ngroups : 1];"
                $"{ind}for (size_t __g = 0; __g < {name}__ngroups; __g++) {name}__counts[__g] = (size_t)blade_gkmap_record_i64(&{name}__map, (int64_t)__g)[0].i;" ]
            @ copyOut
            @ [ $"{ind}blade_gkmap_free_i64(&{name}__map);"
                $"{ind}size_t* {name}__offsets = new size_t[{name}__ngroups + 1];"
                $"{ind}{name}__offsets[0] = 0;"
                $"{ind}for (size_t __gi = 0; __gi < {name}__ngroups; __gi++) {name}__offsets[__gi + 1] = {name}__offsets[__gi] + {name}__counts[__gi];" ]
            @ tail
    let ctx' = addVarName binding.Id name ctx
    Some (code, ctx')

/// A PEEL of an aggregate-only grouping (Blade.Optimize.planGroupAggregate):
/// the peel statement `let <name> = method_for(g) <@> <aggregate kernel> |>
/// compute` reads its state from the grouping's one pass -- still a named,
/// stored rank-1 output over the group axis (the peel's own extents ABI and
/// ownership). The COUNT reads the offsets, exactly as the peel did; an
/// UNSEEDED reduce first checks the groups in group order and raises BL8003
/// for the FIRST empty one, at this declaration, before its output exists
/// (the peel's own empty check, at its own site); every other state adopts
/// its accumulator. None when the grouping did not emit its pass (today's
/// peel follows) or this binding is no planned peel.
and genGroupAggPeelCpp (ctx: CodeGenContext) (id: IRId) (name: string) : string list option =
    match Blade.Optimize.GroupAggTable.tryPeel id with
    | Some (plan, j) when plan.EmittedCpp.Value ->
        match Map.tryFind plan.GkId ctx.VarNames with
        | None -> None
        | Some gk ->
        let ind = indentStr ctx
        let st = plan.States.[j]
        let et = elemTypeToCpp (IRTScalar st.Elem)
        let ext = fst (emitExtentsTable ind (name + "_extents") 1 [ ($"{gk}__ngroups", false) ])
        let header = $"{ind}// group-aggregate v1 (Blade.Optimize.planGroupAggregate): `{st.PeelLabel}` reads state {j} of `{gk}`'s one pass"
        let body =
            match st.Kind with
            | Blade.Optimize.GaCount ->
                [ $"{ind}Array<{et}, 1> {name} = {{ new {et}[{gk}__ngroups], {name}_extents }};"
                  $"{ind}for (size_t __g = 0; __g < {gk}__ngroups; __g++) {name}[__g] = ({et})({gk}__offsets[__g + 1] - {gk}__offsets[__g]);" ]
            | Blade.Optimize.GaUnseeded _ ->
                [ $"""{ind}for (size_t __g = 0; __g < {gk}__ngroups; __g++) if ({gk}__counts[__g] == 0) blade_rt::panic("BL8003", "reduce: empty array, no reduction possible", {declPanicArgs ()});"""
                  $"{ind}Array<{et}, 1> {name} = {{ {gk}__agg{j}, {name}_extents }};" ]
            | Blade.Optimize.GaSeeded _ | Blade.Optimize.GaProdSum ->
                [ $"{ind}Array<{et}, 1> {name} = {{ {gk}__agg{j}, {name}_extents }};" ]
        // The peel's own ownership: a raw `new T[]` backing (the state's
        // accumulator, adopted) and an unowned heap extents table.
        registerAlloc (RawArrayData (name, None, None))
        Some (header :: ext @ body)
    | _ -> None

and genGroupByBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (vals: IRExpr) (gk: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // group_by: per-group nested pointer allocation. Each grouped[g] is a
    // separately-allocated buffer of size offsets[g+1] - offsets[g], holding
    // the values for group g in the order discovered by the keys scan.
    // Layout matches normal rank-2 nested arrays so dimensional currying
    // (kernel taking a sub-array) works without touching the loop builder.
    // Outer extent = gk__ngroups; inner is ragged. Track grouped -> gk so
    // future ragged-aware iteration can recover offsets.
    //
    // The outer pointer-array is wrapped in Array<T*, 1>. The wrapper's
    // .extents points at the 2-element local size_t array {ngroups, 0};
    // .extents[0] = ngroups, .extents[1] reads 0 (placeholder for the
    // ragged inner). Element type T* keeps `grouped[g]` as a bare row
    // pointer for downstream peeling. Print does NOT use that 0: it walks
    // the rows through the grouping's offsets (genPrintStatements via
    // groupedBindingsCell), as the interpreter prints its ragged rows.
    //
    // PER-SEGMENT STREAM (docs/plans/structural/07 §3.4): the values are a
    // rank-1 variable bound with `.stream` (no array named after it exists)
    // and the grouping is structural (static run boundaries), so each row
    // is read from the store directly into its slot of the pool -- the
    // whole variable is never materialized. Any other consumer of a
    // streamed variable, and a key grouping over one, keep the standing
    // refusals.
    let streamedVals =
        match vals with
        | IRVar (vid, _) ->
            (match Map.tryFind vid ctx.VarNames with
             | Some vn -> Map.tryFind vn ctx.StreamedArrays |> Option.map (fun s -> (vn, s))
             | None -> None)
        | _ -> None
    // A structural-group-view verdict reaching an arm that keeps its pool
    // (streamed rows, a tile gather) is recorded as declined there.
    let recordViewKept (why: string) =
        match Blade.Optimize.GroupViewTable.tryGroup binding.Id with
        | Some (Error planWhy) -> Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined planWhy) []
        | Some (Ok ev) -> Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined ("C++ lane: " + why)) ev
        | None -> ()
    match streamedVals with
    | Some (valsName, spec) ->
        recordViewKept "the values are streamed (the pool is their only materialization)"
        let gkName = exprToCppCtx ctx gk
        if not (Set.contains gkName ctx.StructuralGroupings) then
            let ctx' = addVarName binding.Id name ctx
            (codegenError ctx ind $"group_by over the streamed variable '{spec.VarName}' needs a structural grouping (`segments(..)` / `files(..)`), which reads one run at a time; a key grouping needs the whole variable -- bind it with .read", ctx')
        else
        let pspec = (Blade.ProviderRegistry.tryFind spec.Provider).Value
        match Map.tryFind gkName ctx.GridGroupings, pspec.GenStreamWindow with
        | Some bounds, Some genWindow when spec.VarType.IndexTypes.Length = 2 ->
            // TILE GATHER over a streamed rank-2 source (§4.1b + §3.4): each
            // tile is one rectangular window read straight into its slot of
            // the pool, row-major -- the member layout the grid grouping
            // defines. With inherited edges a tile is exactly one chunk file.
            let elemStr = elemTypeToCpp spec.VarType.ElemType
            let g1 = bounds.[1].Length - 1
            let extentsDecl =
                fst (emitGroupedExtentsTable ind (name + "_extents") gkName)
            let tileRead =
                genWindow spec.FilePath spec.VarName valsName ($"{name}__pool + {gkName}__offsets[__t]")
                          [ ($"{gkName}__b0[__t0]", $"{gkName}__b0[__t0 + 1]"); ($"{gkName}__b1[__t1]", $"{gkName}__b1[__t1 + 1]") ]
                          spec.VarType
                |> List.map (fun s -> ind + "    " + s)
            let code =
                [ $"{ind}// group_by: per-tile STREAMED windows of '{spec.VarName}' -- each tile read from the store into its slot; no whole-array buffer" ]
                @ extentsDecl
                @ [ $$"""{{ind}}Array<{{elemStr}}*, 1> {{name}} = { new {{elemStr}}*[{{gkName}}__ngroups], {{name}}_extents };"""
                    $"{ind}{elemStr}* {name}__pool = new {elemStr}[{gkName}__offsets[{gkName}__ngroups]];"
                    $$"""{{ind}}for (size_t __t = 0; __t < {{gkName}}__ngroups; __t++) {"""
                    $"{ind}    size_t __t0 = __t / {g1}, __t1 = __t %% {g1};"
                    $"{ind}    {name}[__t] = {name}__pool + {gkName}__offsets[__t];" ]
                @ tileRead
                @ [ $"{ind}}}" ]
            registerShapedAlloc name "deallocate_ragged_storage" ($"{name}.data, {name}__pool")
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays }
            (code, ctx')
        | _ when spec.VarType.IndexTypes.Length <> 1 ->
            let ctx' = addVarName binding.Id name ctx
            (codegenError ctx ind $"group_by over the streamed rank-{spec.VarType.IndexTypes.Length} variable '{spec.VarName}' needs its tile grouping (`segments(C0, C1)`); bind it with .read for anything else", ctx')
        | _, _ ->
        match pspec.GenStreamRows with
        | None ->
            let ctx' = addVarName binding.Id name ctx
            (codegenError ctx ind $"provider '{spec.Provider}' does not support per-segment streamed reads ('{spec.VarName}' -- bind with .read)", ctx')
        | Some genRows ->
            let elemStr = elemTypeToCpp spec.VarType.ElemType
            let extentsDecl =
                fst (emitGroupedExtentsTable ind (name + "_extents") gkName)
            let rowRead =
                genRows spec.FilePath spec.VarName valsName ($"{name}__pool + __off") $"{gkName}__offsets[__g]" $"{gkName}__offsets[__g + 1]" spec.VarType
                |> List.map (fun s -> ind + "    " + s)
            let code =
                [ $"{ind}// group_by: per-segment STREAMED rows of '{spec.VarName}' -- each run read from the store into its slot; no whole-array buffer" ]
                @ extentsDecl
                @ [ $$"""{{ind}}Array<{{elemStr}}*, 1> {{name}} = { new {{elemStr}}*[{{gkName}}__ngroups], {{name}}_extents };"""
                    $"{ind}{elemStr}* {name}__pool = new {elemStr}[{gkName}__offsets[{gkName}__ngroups]];"
                    $$"""{{ind}}for (size_t __g = 0; __g < {{gkName}}__ngroups; __g++) {"""
                    $"{ind}    size_t __off = {gkName}__offsets[__g];"
                    $"{ind}    {name}[__g] = {name}__pool + __off;" ]
                @ rowRead
                @ [ $"{ind}}}" ]
            // The same layout as the per-tile branch above: a row table whose
            // rows are slices of ONE pool, so the table and the pool are freed
            // together (and no row individually).
            registerShapedAlloc name "deallocate_ragged_storage" ($"{name}.data, {name}__pool")
            let ctx' = addVarName binding.Id name ctx
            let ctx' = { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays }
            (code, ctx')
    | None ->
    // group_by's copy loop indexes vals by name, so it needs a MATERIALIZED
    // input; the shared helper forces a still-deferred or inline vals first.
    let (forceCode, ctx, vals) = forceDeferredArrayInput ctx builder ($"{name}__vals") vals
    let gridGk =
        let gkName = exprToCppCtx ctx gk
        Map.tryFind gkName ctx.GridGroupings |> Option.map (fun b -> (gkName, b))
    match gridGk with
    | Some (gkName, bounds) ->
        recordViewKept "a tile grouping (a tile is a window of the row-major source, not a run)"
        // TILE GATHER (docs/plans/structural/07 §4.1b): the values are rank 2,
        // addressed by coordinates; tile t = (t / G1, t % G1) copies its
        // window row-major into its slot of the pool.
        let valsName = exprToCppCtx ctx vals
        let (elemType, elemErrCode) = inferElemTypeStrict ctx ind vals "group_by"
        let elemStr = elemTypeToCpp elemType
        let g1 = bounds.[1].Length - 1
        let extentsDecl =
            fst (emitGroupedExtentsTable ind (name + "_extents") gkName)
        let code =
            elemErrCode
            @ [ $"{ind}// group_by: tile gather over a rank-2 value (grid grouping {gkName})" ]
            @ extentsDecl
            @ [ $$"""{{ind}}Array<{{elemStr}}*, 1> {{name}} = { new {{elemStr}}*[{{gkName}}__ngroups], {{name}}_extents };"""
                $"{ind}{elemStr}* {name}__pool = new {elemStr}[{gkName}__offsets[{gkName}__ngroups]];"
                $$"""{{ind}}for (size_t __t = 0; __t < {{gkName}}__ngroups; __t++) {"""
                $"{ind}    size_t __t0 = __t / {g1}, __t1 = __t %% {g1};"
                $"{ind}    size_t __lo0 = {gkName}__b0[__t0], __hi0 = {gkName}__b0[__t0 + 1];"
                $"{ind}    size_t __lo1 = {gkName}__b1[__t1], __hi1 = {gkName}__b1[__t1 + 1];"
                $"{ind}    size_t __w = __hi1 - __lo1;"
                $"{ind}    {name}[__t] = {name}__pool + {gkName}__offsets[__t];"
                $$"""{{ind}}    for (size_t __i = __lo0; __i < __hi0; __i++) for (size_t __j = __lo1; __j < __hi1; __j++) {{name}}[__t][(__i - __lo0) * __w + (__j - __lo1)] = {{valsName}}[__i][__j];"""
                $"{ind}}}" ]
        registerShapedAlloc name "deallocate_ragged_storage" ($"{name}.data, {name}__pool")
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays }
        (forceCode @ code, ctx')
    | None ->
    let valsName = exprToCppCtx ctx vals
    let gkName = exprToCppCtx ctx gk
    let (elemType, elemErrCode) = inferElemTypeStrict ctx ind vals "group_by"
    let elemStr = elemTypeToCpp elemType
    // The value array may be a rank-1 COMPOUND compact view (`group_by` over
    // the surviving cells of a mask). gk__perm indexes the same COMPACT order
    // the key scan walked, so the gather just needs the matching accessor.
    let (_, valsAt) = compactOrDenseSource vals valsName
    // Heap extents, not a frame-local `size_t[2]`: `Array<T*,1>` stores only a
    // POINTER to the table, so a grouped array that is RETURNED (or otherwise
    // outlives this frame) would hand its consumer a dangling shape. Same
    // return-extent ABI the peel emitters follow; see emitExtentsTable.
    let extentsDecl =
        fst (emitGroupedExtentsTable ind (name + "_extents") gkName)
    // GATHER ELISION: every consumer of this binding reads only row LENGTHS
    // (computeExtentsOnlyGroupBys), which come from the gk offsets. The row
    // TABLE is still emitted -- the peel indexes it -- but the per-group
    // buffers and the O(n) copy are dead, so the rows stay
    // null. Legal because the peel reads the pointer without dereferencing it,
    // and deallocate_ragged_rows_owned's `delete[] rows[i]` no-ops on null.
    // AGGREGATE-ONLY GROUPING (Blade.Optimize.planGroupAggregate): every
    // consumer is a peel that reads its state from the grouping's one pass
    // (placed: the handshake flag), so the rows are not gathered either.
    let aggregated =
        match Blade.Optimize.GroupAggTable.tryGroupBy binding.Id with
        | Some p -> p.EmittedCpp.Value
        | None -> false
    let elideGather = aggregated || Set.contains binding.Id (extentsOnlyGroupBysCell ()).Value
    // STRUCTURAL GROUP VIEW (Blade.Optimize.planStructuralGroupView, rule
    // `structural-group-view v1`): the plan proved the source, the grouped
    // array and every value that may share their storage never written, so
    // the CSR pool -- a byte copy of the source under the identity accessor --
    // is replaced by row pointers INTO the source's storage. The emitter
    // re-checks what only it knows: the rows are gathered at all, the
    // grouping is a structural one, main's top level, no MPI program, and the
    // materialized values are a plain dense rank-1 array.
    let viewDecline : string option =
        match Blade.Optimize.GroupViewTable.tryGroup binding.Id with
        | None -> Some ""
        | Some (Error _) -> Some ""
        | Some (Ok _) ->
            if elideGather then Some "the rows are not gathered at all (every consumer reads extents or a group-aggregate state)"
            elif not (Set.contains gkName ctx.StructuralGroupings) then Some "the grouping is not a structural one at its emission"
            elif (currentAllocScope ()).IsSome then Some "not at main's top level (a scope frees its allocations)"
            elif mpiProgramOn () then Some "an MPI program (slab-distributed storage is not audited)"
            else
                match vals, inferExprType vals with
                | IRVar _, ArrayElem at when not at.IsVirtual
                                            && not (isCompoundArrayType at) && not (isSparseArrayType at)
                                            && (match at.IndexTypes with
                                                | [ ix ] -> ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone
                                                | _ -> false) -> None
                | _ -> Some "the materialized values are not a plain dense rank-1 array"
    let asView = viewDecline.IsNone
    (match Blade.Optimize.GroupViewTable.tryGroup binding.Id, viewDecline with
     | Some (Ok ev), None ->
         Blade.Optimize.GroupViewTable.noteEmitted binding.Id valsName
         Blade.Optimize.recordGroupView binding.Name Blade.Effects.Applied (ev @ [ $"C++ lane: rows of `{name}` point into `{valsName}`'s pool" ])
     | Some (Ok ev), Some why ->
         Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined ("C++ lane: " + why)) ev
     | Some (Error why), _ ->
         Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined why) []
     | None, _ -> ())
    let code =
        elemErrCode
        @ [ if aggregated
            then $"{ind}// group_by: rows NOT gathered -- every consumer reads its group-aggregate state (Blade.Optimize.planGroupAggregate)"
            elif elideGather
            then $"{ind}// group_by: rows NOT gathered -- every consumer reads only extents(row)"
            elif asView
            then $"{ind}// group_by: rows VIEW {valsName}'s storage -- contiguous structural runs, never written (structural-group-view v1): no pool, no copy"
            else $"{ind}// group_by: per-group nested allocation, group-contiguous via gk__perm" ]
        @ extentsDecl
        @ [ $$"""{{ind}}Array<{{elemStr}}*, 1> {{name}} = { new {{elemStr}}*[{{gkName}}__ngroups], {{name}}_extents };""" ]
        @ (if elideGather then
             [ $"{ind}for (size_t __g = 0; __g < {gkName}__ngroups; __g++) {name}[__g] = nullptr;" ]
           elif asView then
             [ $"{ind}{{ {elemStr}* __gv_base = nested_array_utilities::pool_base({valsName}.data);"
               $"{ind}  for (size_t __g = 0; __g < {gkName}__ngroups; __g++) {name}[__g] = __gv_base + {gkName}__offsets[__g]; }}" ]
           else
             // ONE CSR POOL, not one `new[]` per group. The total is already in
             // hand -- `gk__offsets[gk__ngroups]` is the CSR convention -- so the
             // per-group allocation was never buying anything, and it cost three
             // ways at once: G malloc/free pairs, G allocator headers roughly
             // doubling footprint, and the destruction of spatial locality that
             // made every later pass walk a header-interleaved heap instead of
             // one contiguous run. Measured on the segmented-reduction prototype
             // (src/microkernels/segmented_fold.c): 2.4x / 11.3x / 3.6x on
             // uniform-large / uniform-small / skewed group distributions, and
             // 62-94% of that kernel's whole recoverable win. The small-group
             // case is where it hurts most -- G mallocs for a 4-element group is
             // ~78 ns of allocator per group against a handful of ns of copy.
             //
             // Rows now SLICE the pool, so ownership changes with them: the
             // table's deallocator becomes `deallocate_ragged_storage(rows, pool)`
             // (registered below). `deallocate_ragged_rows_owned` would be
             // undefined behaviour here -- it `delete[]`s each row, and these are
             // interior pointers.
             [ $"{ind}{elemStr}* {name}__pool = new {elemStr}[{gkName}__offsets[{gkName}__ngroups]];"
               $$"""{{ind}}for (size_t __g = 0; __g < {{gkName}}__ngroups; __g++) {"""
               $"{ind}    size_t __off = {gkName}__offsets[__g];"
               $"{ind}    size_t __sz = {gkName}__offsets[__g + 1] - __off;"
               $"{ind}    {name}[__g] = {name}__pool + __off;"
               $$"""{{ind}}    for (size_t __k = 0; __k < __sz; __k++) {"""
               $"""{ind}        {name}[__g][__k] = {(valsAt (sprintf "%s__at(__off + __k)" gkName))};"""
               $$"""{{ind}}    }"""
               $"{ind}}}" ])
    // Owns the row table AND every per-group row (each a separate new[]).
    // The wrapper is Array<T*,1> whose .extents[1] is the ragged placeholder
    // 0, so the row count comes from the gk__ngroups local -- a plain size_t
    // in the same scope, guaranteed live at the scope-exit free point. The
    // gk__offsets/__perm side tables are NOT owned here (see group_keys).
    // Ownership follows the layout chosen above. The GATHERED form slices one
    // CSR pool, so the table and the pool are freed together and no row is
    // individually owned -- `deallocate_ragged_storage` is exactly that shape,
    // and its own comment already named the per-row-owned group_by layout as
    // the opposite hazard. The ELIDED form allocates no pool and leaves every
    // row null, so it keeps the per-row deallocator, which no-ops on null.
    if elideGather then
        registerShapedAlloc name "deallocate_ragged_rows_owned"
            ($"{name}.data, {gkName}__ngroups")
    elif asView then
        // The VIEW owns its row table only; the cells are the source's.
        registerShapedAlloc name "deallocate_ragged_storage"
            ($"{name}.data, static_cast<{elemStr}*>(nullptr)")
    else
        registerShapedAlloc name "deallocate_ragged_storage"
            ($"{name}.data, {name}__pool")
    let ctx' = addVarName binding.Id name ctx
    let ctx' = { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays }
    (forceCode @ code, ctx')



/// segments(A): the STRUCTURAL grouping (docs/plans/structural/07 §2.2, §3.2).
/// Same name-suffix ABI as group_keys -- `__ngroups`, `__offsets`, `__nsrc`,
/// `__at` -- but NO permutation is built: the offsets are the static run
/// boundaries and `__at` is the identity, so a grouped read is a direct
/// read. `__perm` is not declared at all; every consumer goes through
/// `__at`, which is what makes the two regimes interchangeable downstream.
and genSegmentsBinding (ctx: CodeGenContext) (binding: IRBinding) (offsets: int64 list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let ngroups = offsets.Length - 1
    let n = List.last offsets
    let table = offsets |> List.map (fun b -> $"{b}UL") |> String.concat ", "
    let code =
        [ $"{ind}// segments: {ngroups} runs over {n} cells, structural (static boundaries, identity permutation)"
          $"{ind}size_t {name}__ngroups = {ngroups};"
          $"{ind}const size_t {name}__offsets[{ngroups + 1}] = {{ {table} }};"
          $"{ind}size_t {name}__nsrc = {n};"
          $"{ind}auto {name}__at = [](size_t __p) {{ return __p; }};"
          $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
          $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets (structural: no __perm)" ]
    let ctx' = addVarName binding.Id name ctx
    let ctx' = { ctx' with StructuralGroupings = Set.add name ctx'.StructuralGroupings }
    (code, ctx')

/// segments(C0, C1): the tile grouping of two segmented slots (docs/plans/
/// structural/07 §4.1b). Same name-suffix ABI; the offsets are the
/// cumulative tile sizes, tile t = (t / G1, t % G1); the per-slot boundary
/// tables are emitted for the gather and the scatter. `__at` is the
/// identity over the tile-major member order (used by nothing today; the
/// grid gather addresses the rank-2 value by coordinates).
and genSegmentsGridBinding (ctx: CodeGenContext) (binding: IRBinding) (bounds: int64 list list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let b0, b1 = bounds.[0], bounds.[1]
    let g0, g1 = b0.Length - 1, b1.Length - 1
    let sizes =
        [ for i in 0 .. g0 - 1 do
            for j in 0 .. g1 - 1 ->
                (b0.[i + 1] - b0.[i]) * (b1.[j + 1] - b1.[j]) ]
    let offsets = sizes |> List.scan (+) 0L
    let table (xs: int64 list) = xs |> List.map (fun b -> $"{b}UL") |> String.concat ", "
    let code =
        [ $"{ind}// segments grid: {g0} x {g1} tiles over {List.last b0} x {List.last b1} cells, structural"
          $"{ind}size_t {name}__ngroups = {g0 * g1};"
          $"{ind}const size_t {name}__offsets[{g0 * g1 + 1}] = {{ {table offsets} }};"
          $"{ind}const size_t {name}__b0[{g0 + 1}] = {{ {table b0} }};"
          $"{ind}const size_t {name}__b1[{g1 + 1}] = {{ {table b1} }};"
          $"{ind}size_t {name}__nsrc = {List.last offsets};"
          $"{ind}auto {name}__at = [](size_t __p) {{ return __p; }};"
          $"{ind}size_t {name}_extents[1] = {{{name}__ngroups}};"
          $"{ind}void* {name} = nullptr; // gk: state in {name}__ngroups, {name}__offsets, {name}__b0/__b1 (grid)" ]
    let ctx' = addVarName binding.Id name ctx
    let ctx' = { ctx' with StructuralGroupings = Set.add name ctx'.StructuralGroupings
                           GridGroupings = Map.add name bounds ctx'.GridGroupings }
    (code, ctx')

/// ungroup(G) of a tile grouping: the tiles written back over the two
/// source axes into one dense rank-2 array.
and genUngroupGridBinding (ctx: CodeGenContext) (binding: IRBinding) (g: IRExpr) (srcs: IRIndexType list) (bounds: int64 list list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let gName = exprToCppCtx ctx g
    let elemStr =
        match binding.Type with
        | ArrayElem at -> elemTypeToCpp at.ElemType
        | _ -> "double"
    match Map.tryFind gName ctx.GroupedArrays with
    | None ->
        let ctx' = addVarName binding.Id name ctx
        (codegenError ctx ind $"ungroup: '{gName}' is not a group_by result this emitter can trace to its grouping (bind `let G = group_by(A, segments(X, Y))` and ungroup that name)", ctx')
    | Some gkName ->
        let b0, b1 = bounds.[0], bounds.[1]
        let n0, n1 = List.last b0, List.last b1
        let g1 = b1.Length - 1
        let (extentsDecl, ownedExtents) =
            emitExtentsTable ind (name + "_extents") 2 [($"{n0}UL", false); ($"{n1}UL", false)]
        let code =
            [ $"{ind}// ungroup: tiles of {gName} written back over the two axes ({n0} x {n1})" ]
            @ extentsDecl
            @ [ $$"""{{ind}}Array<{{elemStr}}, 2> {{name}} = { allocate<promote<{{elemStr}}, 2>::type>({{name}}_extents), {{name}}_extents };"""
                $$"""{{ind}}for (size_t __t = 0; __t < {{gkName}}__ngroups; __t++) {"""
                $"{ind}    size_t __t0 = __t / {g1}, __t1 = __t %% {g1};"
                $"{ind}    size_t __lo0 = {gkName}__b0[__t0], __hi0 = {gkName}__b0[__t0 + 1];"
                $"{ind}    size_t __lo1 = {gkName}__b1[__t1], __hi1 = {gkName}__b1[__t1 + 1];"
                $"{ind}    size_t __w = __hi1 - __lo1;"
                $$"""{{ind}}    for (size_t __i = __lo0; __i < __hi0; __i++) for (size_t __j = __lo1; __j < __hi1; __j++) {{name}}[__i][__j] = {{gName}}[__t][(__i - __lo0) * __w + (__j - __lo1)];"""
                $"{ind}}}" ]
        registerPoolAlloc AllocDense elemStr 2 "nullptr" (name + "_extents") name ownedExtents
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

/// ungroup(G): the rows of a segment-grouped array written back over the
/// source axis (§3.3). G's grouping is recovered from GroupedArrays (the
/// same registration the grouped peel uses), so the offsets are the
/// grouping's own; the destination is one dense buffer of the source
/// extent, and each row lands at its run.
and genUngroupBinding (ctx: CodeGenContext) (binding: IRBinding) (g: IRExpr) (src: IRIndexType) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let gName = exprToCppCtx ctx g
    let elemStr =
        match binding.Type with
        | ArrayElem at -> elemTypeToCpp at.ElemType
        | _ -> "double"
    match Map.tryFind gName ctx.GroupedArrays with
    | None ->
        let ctx' = addVarName binding.Id name ctx
        (codegenError ctx ind $"ungroup: '{gName}' is not a group_by result this emitter can trace to its grouping (bind `let G = group_by(A, segments(X))` and ungroup that name)", ctx')
    | Some gkName ->
        let total =
            match src.Extent with
            | IRLit (IRLitInt n) -> $"{n}UL"
            | _ -> $"{gkName}__offsets[{gkName}__ngroups]"
        let (extentsDecl, ownedExtents) =
            emitExtentsTable ind (name + "_extents") 1 [(total, false)]
        // STRUCTURAL GROUP VIEW, D1b (rule `structural-group-view v1`): G's
        // rows were emitted as a view of the source's storage and the
        // accessor is the identity, so the reassembled array IS the source
        // cell for cell; the plan's closure holds over this binding too
        // (it names G), so it shares the source's storage: no pool, no copy.
        let shared =
            match Blade.Optimize.GroupViewTable.tryUngroup binding.Id with
            | Some (gid, Ok ev) ->
                (match Blade.Optimize.GroupViewTable.tryEmitted gid with
                 | Some srcCpp when (currentAllocScope ()).IsNone ->
                     Blade.Optimize.recordGroupView binding.Name Blade.Effects.Applied (ev @ [ $"C++ lane: `{name}` shares `{srcCpp}`'s pool" ])
                     Some srcCpp
                 | Some _ ->
                     Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined "C++ lane: not at main's top level") ev
                     None
                 | None ->
                     Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined ($"C++ lane: `{gName}`'s rows were not emitted as a view")) ev
                     None)
            | Some (_, Error why) ->
                Blade.Optimize.recordGroupView binding.Name (Blade.Effects.Declined why) []
                None
            | None -> None
        match shared with
        | Some srcCpp ->
            let code =
                [ $"{ind}// ungroup: rows of {gName} ARE {srcCpp}'s storage (structural-group-view v1): {name} shares it -- no pool, no copy" ]
                @ extentsDecl
                @ [ $$"""{{ind}}Array<{{elemStr}}, 1> {{name}} = { {{srcCpp}}.data, {{name}}_extents };""" ]
            let ctx' = addVarName binding.Id name ctx
            (code, ctx')
        | None ->
        let code =
            [ $"{ind}// ungroup: rows of {gName} written back over the source axis ({total} cells)" ]
            @ extentsDecl
            @ [ $$"""{{ind}}Array<{{elemStr}}, 1> {{name}} = { allocate<promote<{{elemStr}}, 1>::type>({{name}}_extents), {{name}}_extents };"""
                $$"""{{ind}}for (size_t __g = 0; __g < {{gkName}}__ngroups; __g++) {"""
                $"{ind}    size_t __off = {gkName}__offsets[__g];"
                $"{ind}    size_t __sz = {gkName}__offsets[__g + 1] - __off;"
                $$"""{{ind}}    for (size_t __k = 0; __k < __sz; __k++) {{name}}[{{gkName}}__at(__off + __k)] = {{gName}}[__g][__k];"""
                $"{ind}}}" ]
        registerPoolAlloc AllocDense elemStr 1 "nullptr" (name + "_extents") name ownedExtents
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')

/// ungroup([r1, .., rF], A): per-file arrays copied at their file offsets
/// into one buffer over the tiled axis (§2.7). Rows are bare names by
/// construction (inferUngroupRows) and the offsets are static.
and genUngroupRowsBinding (ctx: CodeGenContext) (binding: IRBinding) (rows: IRExpr list) (offsets: int64 list) (src: IRIndexType) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let elemStr =
        match binding.Type with
        | ArrayElem at -> elemTypeToCpp at.ElemType
        | _ -> "double"
    let total = List.last offsets
    let (extentsDecl, ownedExtents) =
        emitExtentsTable ind (name + "_extents") 1 [($"{total}UL", false)]
    let copies =
        List.zip rows (List.pairwise offsets)
        |> List.collect (fun (r, (lo, hi)) ->
            let rName = exprToCppCtx ctx r
            [ $$"""{{ind}}for (size_t __k = 0; __k < {{hi - lo}}UL; __k++) {{name}}[{{lo}}UL + __k] = {{rName}}[__k];""" ])
    let code =
        [ $"{ind}// ungroup: {rows.Length} per-file array(s) assembled over the tiled axis ({total} cells)" ]
        @ extentsDecl
        @ [ $$"""{{ind}}Array<{{elemStr}}, 1> {{name}} = { allocate<promote<{{elemStr}}, 1>::type>({{name}}_extents), {{name}}_extents };""" ]
        @ copies
    registerPoolAlloc AllocDense elemStr 1 "nullptr" (name + "_extents") name ownedExtents
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')

and genGroupBucketBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (gk: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let gkName = exprToCppCtx ctx gk
    // group_bucket(gk): invert the CSR (perm, offsets) pair into a dense
    // row -> bucket map over the SOURCE index space. One pass over the members:
    //
    //   for g in [0, ngroups): for p in [offsets[g], offsets[g+1]): b[perm[p]] = g
    //
    // Rows the permutation never names are exactly the rows a negative key
    // dropped (docs/features/sql.md), so the -1 prefill IS the drop marker --
    // no separate key rescan, and no dependence on which of the three bucketing
    // regimes built the table.
    //
    // Total work is offsets[ngroups] + nsrc, and the writes are a permutation
    // (each row written at most once), so no scatter conflict to serialize.
    //
    // gk is a bare name by construction (inferGroupBucket refuses anything
    // else), which is what makes the `<gk>__` suffixed reads below resolvable.
    let elemStr = "int64_t"
    let (extentsDecl, ownedExtents) =
        emitExtentsTable ind (name + "_extents") 1 [($"{gkName}__nsrc", false)]
    let code =
        [ $"{ind}// group_bucket: invert gk's perm/offsets into row -> bucket (-1 = dropped row)" ]
        @ extentsDecl
        @ [ $$"""{{ind}}Array<{{elemStr}}, 1> {{name}} = { allocate<promote<{{elemStr}}, 1>::type>({{name}}_extents), {{name}}_extents };"""
            $"{ind}for (size_t __i = 0; __i < {gkName}__nsrc; __i++) {name}[__i] = -1;"
            $$"""{{ind}}for (size_t __g = 0; __g < {{gkName}}__ngroups; __g++) {"""
            $$"""{{ind}}    for (size_t __p = {{gkName}}__offsets[__g]; __p < {{gkName}}__offsets[__g + 1]; __p++) {"""
            $"{ind}        {name}[{gkName}__at(__p)] = ({elemStr})__g;"
            $$"""{{ind}}    }"""
            $"{ind}}}" ]
    registerPoolAlloc AllocDense elemStr 1 "nullptr" (name + "_extents") name ownedExtents
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genGroupSizesBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (gk: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let gkName = exprToCppCtx ctx gk
    // extents(gk): per-group sizes straight off the CSR row pointers,
    // sizes[g] = offsets[g+1] - offsets[g]. O(ngroups), and -- the point of the
    // spelling -- NO group_by: the values are never gathered, so a count-only
    // query stops paying for a copy of the data it ignores.
    //
    // This is the same arithmetic the ragged peel builds each row's `.len` from
    // (genGroupByBinding's RaggedRow), which is what makes `extents(gk)` and
    // `method_for(group_by(v, gk)) <@> lambda(r) -> extents(r)` the same answer.
    // Rows dropped by a negative key are absent from every group's span, so they
    // are counted nowhere -- consistent with group_bucket reading -1 for them.
    let elemStr = "int64_t"
    let (extentsDecl, ownedExtents) =
        emitExtentsTable ind (name + "_extents") 1 [($"{gkName}__ngroups", false)]
    let code =
        [ $"{ind}// extents(gk): per-group sizes from the CSR offsets (no gather)" ]
        @ extentsDecl
        @ [ $$"""{{ind}}Array<{{elemStr}}, 1> {{name}} = { allocate<promote<{{elemStr}}, 1>::type>({{name}}_extents), {{name}}_extents };"""
            $$"""{{ind}}for (size_t __g = 0; __g < {{gkName}}__ngroups; __g++) {"""
            $"{ind}    {name}[__g] = ({elemStr})({gkName}__offsets[__g + 1] - {gkName}__offsets[__g]);"
            $"{ind}}}" ]
    registerPoolAlloc AllocDense elemStr 1 "nullptr" (name + "_extents") name ownedExtents
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genSortBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (keyExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // sort(array, key): stable ascending sort by key.
    //
    // Current approach: eager materialization. Construct a permutation via
    // std::stable_sort with a comparator that calls the user's key function,
    // then write the permuted elements into a fresh contiguous buffer.
    //
    // A lazy chain handle -- recording (key_fn, permutation, source_pointer)
    // and deferring materialization to first access -- would let long chains
    // of sorts and other rearrangements be analyzed before any layout commits,
    // enabling sort-skip, merge-style joins, and other optimizations; not yet
    // implemented. Materialization caching would sit downstream of that
    // analysis, not as a substitute for it.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "sort"
    let elemStr = elemTypeToCpp elemET
    // Validate single-param key callable. Helper falls back to a 0L key
    // (preserving input order); the #error here surfaces the IR bug
    // before the silently-wrong sort runs.
    let keyErrCode =
        match resolveCallable keyExpr with
        | Some callable when callable.Params.Length = 1 -> []
        | _ -> codegenError ctx ind "sort: key must resolve to a single-parameter callable; got something else (typechecker or IR bug)"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) (IRSort (arrExpr, keyExpr)) with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCode @ elemErrCode @ keyErrCode @ [$"{ind}// sort: stable_sort on permutation, eager materialization"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genTransposeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (d1: int) (d2: int) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // transpose(array, [d1, d2]): hard transpose -- allocate a fresh pool at
    // the swapped extents and copy with axes d1/d2 exchanged. Eager
    // materialization (same phase-1 strategy as sort); the result is an
    // independent array with no aliasing back to the source. TypeCheck has
    // already verified both axes are arity-1 SymNone and in range.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "transpose"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) (IRTranspose (arrExpr, d1, d2)) with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCode @ elemErrCode @ [$"{ind}// transpose: hard (swapped-extent alloc + axis-swapped copy)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genStackJoinBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrs: IRExpr list) (joinDim: int option) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let opName = match joinDim with Some _ -> "join" | None -> "stack"
    // Eager materialization (same phase-1 strategy as transpose/decompact): a
    // fresh dense pool plus per-source copy nests. Every source is forced first
    // so a deferred producer materializes under its own name before the copy.
    let (forceCode, ctx, arrs) =
        arrs |> List.mapi (fun i a -> (i, a))
        |> List.fold (fun (accCode, accCtx, accArrs) (i, a) ->
            let (code, ctx', a') = forceDeferredArrayInput accCtx builder ($"{name}__src{i}") a
            (accCode @ code, ctx', accArrs @ [a'])) ([], ctx, [])
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind (List.head arrs) opName
    let elemStr = elemTypeToCpp elemET
    let form = match joinDim with Some d -> IRJoin (arrs, d) | None -> IRStack arrs
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) form with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let note =
        match joinDim with
        | Some d -> $"{ind}// join: dense alloc (summed extent on dim {d}) + per-source offset copy"
        | None -> $"{ind}// stack: fresh leading axis (dense alloc + per-source copy)"
    let code = forceCode @ elemErrCode @ [note] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genDecompactBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (d: int) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // decompact(array, d): pull the compact component at dim d out as a
    // free Idx. Hard materialization -- allocate a fresh dense pool and
    // scatter the canonical (triangular-packed) source elements into all
    // of the decompacted component's image positions, applying the per-
    // class transform (Sym copy / Antisym sign + zero diagonal / Hermitian
    // conj). TypeCheck has verified dim d targets a compact slot and that
    // the Antisym middle-peel case is excluded.
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "decompact"
    let elemStr = elemTypeToCpp elemET
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) (IRDecompact (arrExpr, d)) with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCode @ elemErrCode @ [$"{ind}// decompact: hard (dense alloc + symmetry-expanding scatter)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genArrayNegateConjugateBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Whole-array eager negate/conjugate (the cheap intra-group transposes).
    // Type-preserving: same-shape alloc + flat contiguous-pool transform.
    let isConj = (binding.Value.IsIRArrayConjugate)
    let label = if isConj then "conjugate" else "negate"
    let (forceCode, ctx, arrExpr) = forceDeferredArrayInput ctx builder ($"{name}__arr") arrExpr
    let (elemET, elemErrCode) = inferElemTypeStrict ctx ind arrExpr ($"array_{label}")
    let elemStr = elemTypeToCpp elemET
    let form = if isConj then IRArrayConjugate arrExpr else IRArrayNegate arrExpr
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) form with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = forceCode @ elemErrCode @ [$"{ind}// array_{label}: whole-array eager transform (same-shape alloc + pool loop)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genGramBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // gram(A, B) = A * B^H. Materialized as a triangular (same-array,
    // symmetric/Hermitian) or full (distinct, dense) scatter with an inner
    // contracted-axis reduction. The shared helper emits the statement form.
    let elemStr =
        match binding.Type with
        | ArrayElem at -> irTypeToCpp at.ElemType
        | _ -> "double"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = [$"{ind}// gram: A * B^H (Gram product)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genGramApplyBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // gram_apply(A, B, x) = A * (B^H * x): the action of gram(A, B) on x,
    // materialized as two rank-1 pools (B^H x, then A times it) and never the
    // m x p matrix. The shared helper emits the statement form.
    let ind = indentStr ctx
    let name = bindingCppName binding
    let elemStr =
        match binding.Type with
        | ArrayElem at -> irTypeToCpp at.ElemType
        | _ -> "double"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = [$"{ind}// gram_apply: A * (B^H * x) (Gram action, no m x p pool)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genMatmulBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // matmul(A, B) = A * B. Dense m x n result. Same shape as genGramBinding:
    // the shared materialize helper emits the statement form (allocation plus
    // one `blade_linalg::blade_matmul` dispatch call).
    let ind = indentStr ctx
    let name = bindingCppName binding
    let elemStr =
        match binding.Type with
        | ArrayElem at -> irTypeToCpp at.ElemType
        | _ -> "double"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = [$"{ind}// matmul: A * B (dense matrix product)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genEighBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // eigh(S) -> (Q, LAM). The one linalg binding whose value is a TUPLE, so
    // unlike gram/matmul there is no single element type to pass down: the
    // shared helper derives both from the operand (see materializeEighForm's
    // design note). `binding.Type` here is the `IRTTuple` inferEigh built; the
    // emitted `std::tuple<...>` spelling is derived from the operand instead, so
    // the two cannot drift apart through a stale binding type.
    let ind = indentStr ctx
    let name = bindingCppName binding
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy "") binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code =
        [$"{ind}// eigh: symmetric/Hermitian eigendecomposition -> (Q, LAM)"]
        @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genLuBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // lu(A) -> (LU, piv): a TUPLE value like eigh's, two pools under derived
    // names bound to one make_tuple; the shared helper derives both element
    // types itself.
    let ind = indentStr ctx
    let name = bindingCppName binding
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy "") binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code =
        [$"{ind}// lu: partial-pivoted LU factorization -> (LU, piv), kept for repeated solves"]
        @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genLuSolveBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // lu_solve(LU, piv, b) -> x: one array result, genSolveBinding's shape.
    let ind = indentStr ctx
    let name = bindingCppName binding
    let elemStr =
        match binding.Type with
        | ArrayElem at -> irTypeToCpp at.ElemType
        | _ -> "double"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code = [$"{ind}// lu_solve: apply a stored LU factorization (no refactoring)"] @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')


and genSolveBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    // solve(A, b) -> x. Same shape as genMatmulBinding: one array result, one
    // element type, and the shared materialize helper emits the statement form
    // (allocation plus either the LU loop nest or one `blade_lapack::blade_solve`
    // dispatch call, per the availability gate).
    let ind = indentStr ctx
    let name = bindingCppName binding
    let elemStr =
        match binding.Type with
        | ArrayElem at -> irTypeToCpp at.ElemType
        | _ -> "double"
    let matStmts =
        match materializeInlineForm emptySubst ctx.VarNames name (lazy elemStr) binding.Value with
        | Some (s, allocs) -> registerMaterializedAllocs allocs; s
        | None -> []
    let code =
        [$"{ind}// solve: A.x = b by partial-pivoted LU -> x"]
        @ (matStmts |> List.map (fun s -> ind + s))
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



and genReduceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (arrExpr: IRExpr) (kernelExpr: IRExpr) (initExpr: IRExpr option) : string list * CodeGenContext =
    let sparseTerminal = Blade.Optimize.certifySparseWildcardFold ctx.StreamTerminalModule binding arrExpr kernelExpr initExpr
    match arrExpr, sparseTerminal with
    | _, Ok plan when ctx.ParamIds.IsEmpty && ctx.TilePlans.IsEmpty && ctx.TileReads.IsEmpty && ctx.StreamedArrays.IsEmpty
                      && not (ctx.DeferredComputations.ContainsKey plan.ParentId) && Map.containsKey plan.ParentId ctx.VarNames ->
        let ind = indentStr ctx
        let name = bindingCppName binding
        let parent = ctx.VarNames.[plan.ParentId]
        let ordinal = name + "__sparse_r"
        let (wrapper, foldName) = genCallableWrapper ctx.VarNames name plan.Fold
        recordFoldForm name (serialFoldForm ()) "sparse wildcard terminal: supplied key order, no residual gather"
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "sparse-wildcard-fold"; Version = 1; Span = declSpanOf binding.Name
              Subject = binding.Name; Outcome = Blade.Effects.Applied
              Evidence = [ "stored sparse constructor, validation and ownership retained"
                           "one supplied-key ordinal scan; no residual ranks, copied values or extent buffer" ] }
        let lines =
            (wrapper |> List.map (fun line -> ind + line)) @
            [ $"{ind}// sparse wildcard terminal: no residual gather"
              $"{ind}double {name} = {exprToCppCtx ctx plan.Seed};"
              $$"""{{ind}}for (size_t {{ordinal}} = 0; {{ordinal}} < {{parent}}.idx->cardinality; ++{{ordinal}}) {"""
              $$"""{{ind}}    if ({{parent}}.idx->rank_to_tuple[{{ordinal}}][{{plan.PinAxis}}] == (size_t){{plan.Pin}}) {"""
              $"{ind}        {name} = {foldName}({name}, {parent}.data[{ordinal}]);"
              $"{ind}    }}"
              $"{ind}}}" ]
        (lines, addVarName binding.Id name ctx)
    | GatherTeardown routine, _ ->
        (match sparseTerminal with
         | Error why ->
             Blade.Effects.Decisions.record
                 { Blade.Effects.Rule = "sparse-wildcard-fold"; Version = 1; Span = declSpanOf binding.Name
                   Subject = binding.Name; Outcome = Blade.Effects.Declined why; Evidence = [] }
         | Ok _ -> ())
        // An inline partial sparse read as the operand: the fold below spells
        // its operand at every element read, the bound and the guard, so the
        // gather ran (and leaked) once per access. Gather once into a named
        // temp, fold the temp, and free it right after: the fold's result is
        // a scalar, and nothing else names the temp. Statement-level twin of
        // CodeGenExpr.bindInlineGathers.
        let ind = indentStr ctx
        let gname = (bindingCppName binding) + "__gath"
        let gatherLine = $"{ind}auto {gname} = {exprToCppCtx ctx arrExpr};"
        let (lines, ctx') =
            genReduceBinding ctx binding builder (IRParam (gname, 0, inferExprType arrExpr)) kernelExpr initExpr
        ([ gatherLine ] @ lines @ [ $"{ind}nested_array_utilities::{routine}({gname});" ], ctx')
    // An inline ARRAY-RETURNING CALL as the operand: the fold below spells its
    // operand at every element read, so the call ran n + 1 times and leaked
    // every pool. Bind it once right before the fold (operand before seed, the
    // interpreter's order), fold the binding, free a caller-owned pool after
    // (the guard of CodeGenExpr.bindInlineArrayCall). Statement-level twin of
    // bindInlineArrayCall; a kept user call at statement level was already
    // bound to its own let by Optimize.elideCallsModule.
    | InlineArrayCall _, _ ->
        let ind = indentStr ctx
        let cname = (bindingCppName binding) + "__call"
        let ty = inferExprType arrExpr
        let callLine = $"{ind}auto {cname} = {exprToCppCtx ctx arrExpr};"
        let (lines, ctx') = genReduceBinding ctx binding builder (IRParam (cname, 0, ty)) kernelExpr initExpr
        let free =
            match arrExpr, stripUnits ty with
            | IRApp (f, _, _), ArrayElem at
                when freshReturnOf f = FreshPool
                     && isFreeableDenseArrayType at
                     && classifyOutputStorage ty = AllocDense
                     && not (hasRealSymmetry (buildSymmVec ty)) ->
                [ $"{ind}deallocate<typename promote<{elemTypeToCpp at.ElemType}, {arrayRank at}>::type, nullptr>({cname}.data, {cname}.extents);" ]
            | _ -> []
        ([ callLine ] @ lines @ free, ctx')
    // Compaction fusion (B4a/c): the fold of a planned `y * y` reads `y`'s
    // compact buffer (CodeGenExpr.renderCompactionFold); its map let emitted
    // only a comment.
    | IRVar (mid, _), _ when initExpr.IsNone && (Blade.Optimize.CompactionTable.tryFoldMap mid).IsSome ->
        let ind = indentStr ctx
        let name = bindingCppName binding
        let plan = (Blade.Optimize.CompactionTable.tryFoldMap mid).Value
        ([ $"{ind}double {name} = {(renderCompactionFold ctx.VarNames plan kernelExpr)};" ], addVarName binding.Id name ctx)
    | _, _ ->
    let ind = indentStr ctx
    let name = bindingCppName binding
    // reduce(array, op): T/S reduction. Consumes the innermost dim by a
    // binary kernel, producing a scalar (rank-1 input only for now).
    //
    // Empty-array handling (post-extents integration):
    //   - Static extent > 0: standard loop, no runtime check
    //     (typecheck already proved non-emptiness)
    //   - Dynamic extent: emit a runtime guard that aborts cleanly on
    //     empty rather than reading uninitialized memory from arr[0]
    //   - Static extent = 0: typecheck rejects before reaching here
    //
    // Section kernels like (+) lower to callables during Lowering
    // with properly-resolved scalar types. Resolution flows
    // through `resolveCallable`.
    let arrName = exprToCppCtx ctx arrExpr
    let (elemType, elemErrCode) = inferElemTypeStrict ctx ind arrExpr "reduce"
    let elemStr = elemTypeToCpp elemType

    // Length accessor: a rank-1 ragged-family operand (a let-bound row of
    // a ragged/DepIdx array or of a group_by result) is a RaggedRow<T>,
    // which carries its length inline as `.len` -- not `.extents[0]`.
    // Same predicate the expression-form reduce, IRExtent, and the
    // sub-view binding use, so the accessor always matches the declared
    // type. Element access `%s[%s]` works for both via operator[].
    let isRaggedRowOperand =
        match inferExprType arrExpr with
        | ArrayElem at -> isRaggedRowType at
        | _ -> false
    // A compound operand reduces over its compact buffer: bound =
    // cardinality * trailing_stride (all present values), elements via
    // .data[i]. This is what makes `reduce(compound(A, mask(A, p)), (+))`
    // -- SQL's SUM(x) WHERE p -- a one-liner. Mirrors the expression-form
    // reduce's compound handling.
    let isCompoundOperand =
        match inferExprType arrExpr with
        | ArrayElem at -> isCompoundArrayType at || isSparseArrayType at
        | _ -> false
    let boundExpr =
        if isRaggedRowOperand then $"{arrName}.len"
        elif isCompoundOperand then $"({arrName}.idx->cardinality * {arrName}.trailing_stride)"
        // Dense operand: the shared literal-or-runtime rule, exactly as the
        // expression-form reduce already does. The two forms emit the same
        // fold over the same array and must agree on its trip count.
        else
            match inferExprType arrExpr with
            | ArrayElem at -> literalOrRuntimeExtentOfArray at arrName 0
            | _ -> $"{arrName}.extents[0]"
    let elemAt (i: string) =
        if isCompoundOperand then $"{arrName}.data[{i}]"
        else $"{arrName}[{i}]"

    // Decide whether to emit a runtime extent check based on whether
    // the array's innermost-dim extent is statically known.
    let isStaticallyNonEmpty =
        match inferExprType arrExpr with
        | ArrayElem at when at.IndexTypes.Length >= 1 ->
            match tryEvalIntIR at.IndexTypes.[at.IndexTypes.Length - 1].Extent with
            | Some n -> n > 0L
            | None -> false
        | _ -> false

    let guardLines =
        // The 3-arg (init) form defines the empty fold as init, so it never
        // needs an emptiness guard, static or dynamic.
        if isStaticallyNonEmpty || initExpr.IsSome then []
        else [
            $"{ind}// reduce: dynamic extent -- runtime non-emptiness guard"
            $"{ind}if ({boundExpr} == 0) {{ blade_rt::panic(\"BL8003\", \"reduce: empty array, no reduction possible\", {(declPanicArgs ())}); }}"
        ]

    // Comm-licensed parallel fold. `where ... omp` on the fold kernel is the
    // opt-in; the LICENCE (declared comm, or a builtin body) is what makes the
    // reorder sound (typecheck refuses the unlicensed combination, BL4016).
    // Two emitted shapes:
    //   Path A -- builtin `+`/`*` over a real arithmetic element type: one
    //     `#pragma omp parallel for simd reduction(<op>:acc)` over the flat
    //     sweep -- the serial fold up to reassociation, nothing else changes.
    //   Path B -- any other licensed kernel: manual contiguous chunks with a
    //     per-thread partial, each seeded from its OWN first element (chunk 0
    //     inherits the caller's seed via fixed-order combine), so no identity
    //     element is needed. Deterministic for a fixed OMP_NUM_THREADS.
    // Both are flat 1-D sweeps (`reduce` takes rank-1 arrays only); compound/
    // sparse operands walk their contiguous `.data` buffer of present cells.
    // The REPRO build (`fpReproEnabled`, BLADE_FP_REPRO=1) keeps the serial
    // association of every fold, the thread-level forms included: both paths
    // reassociate (Path A through the reduction clause, Path B through its
    // per-thread partials), so neither survives it. The fold then lands on the
    // serial arm below, and `pathBSuppressedNote` says why.
    let parallelFold =
        match resolveCallable kernelExpr with
        | Some c when c.Params.Length = 2 && c.IsOmpParallel && foldReorderLicensed c
                      && fpReassocEnabled () -> Some c
        | _ -> None
    // Path A additionally needs an element type OpenMP's built-in reduction
    // identities are defined for. Complex (and anything non-arithmetic) takes
    // Path B, which needs no identity.
    let pathAOp =
        match parallelFold with
        | Some c ->
            (match elemType with
             | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32) ->
                foldKernelBuiltinOp c |> Option.bind (fun op ->
                    ompReductionOperator op |> Option.map (fun r -> (op, r)))
             | _ -> None)
        | None -> None
    // Hoisted sweep bound. OpenMP's canonical loop form wants a loop-invariant
    // upper bound, and the compound operand's is a two-field product through a
    // pointer -- computed once here for both parallel paths. (The serial path
    // keeps using `boundExpr` inline, byte-identical to before.)
    let rnName = $"__rn_{name}"
    // BUILD KNOB, applied ONE LEVEL ABOVE the two paths because they need
    // DIFFERENT suppressions and the difference is not arbitrary:
    //
    //   Path A is `parallel for simd reduction(op:acc)` -- TWO constructs. Only
    //     `parallel for` opens a team; `simd reduction` is vectorization the
    //     `omp` licence already paid for and the knob is not about. So Path A
    //     stays Path A, minus the team: the `BLADE_OMP_SIMD_REDUCTION` form
    //     `fpReassocSimdStmts` already emits at the BLADE_FP_REASSOC sites.
    //     REUSED rather than re-spelled, so the suppressed Path A and the knob's
    //     own simd sites cannot drift into two different summation shapes.
    //   Path B is a hand-written team (`#pragma omp parallel num_threads`,
    //     `omp_get_max_threads`, per-thread partials). There is no vector half
    //     to keep -- the whole construction IS the threading -- so it is
    //     suppressed wholesale by making `parallelFold` invisible below, which
    //     lands the fold on the SAME serial/lane arms an unlicensed kernel
    //     takes. No `omp_get_*` call survives anywhere in the output.
    //
    // See `ompThreadEmissionEnabled` for why this is a build decision.
    let ompThreadsOn = ompThreadEmissionEnabled ()
    // Census line for a Path B fold this build serialized. The arms it lands on
    // (lane / plain serial) are the ones an UNLICENSED kernel takes and emit
    // nothing about omp on their own, so without this the dropped clause would
    // be invisible -- the same silence `ompSuppressedMarker` exists to prevent
    // in the loop nests. Empty for Path A (which emits its own marker beside the
    // simd form it keeps) and for a kernel that never asked for omp.
    // A FUNCTION, called only at the arms that splice it: its census phrase is
    // also the plan's decision record (ompSuppressedPhrase), so it must not be
    // computed for an arm that emits something else (Path A with the team
    // suppressed prints its own).
    let pathBSuppressedNote () =
        if parallelFold.IsSome && not ompThreadsOn
        then [ $"{ind}// {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))}" ]
        else
        // The `where repro` veto: the kernel (or the named function its eta
        // wrapper calls) demands the serial operation sequence, so
        // foldReorderLicensed said no and the fold lands on the serial arms
        // below -- which say nothing about omp on their own. Same
        // silence-prevention as the knob note above; without it a
        // comm-licensed `omp, repro` fold would drop its omp request with no
        // trace in the emitted text.
        match resolveCallable kernelExpr with
        | Some c when c.Params.Length = 2 && c.IsOmpParallel && foldKernelReproVetoed c ->
            let why = "fold kernel is `where repro` (the operation order is the contract; reorder licence vetoed)"
            [ $"{ind}// {(ompSuppressedPhrase why)}" ]
        // The BUILD-WIDE twin of the veto above: BLADE_FP_REPRO=1 is `where
        // repro` on every fold kernel at once, so an `omp` request it declines
        // gets the same census line, naming the knob.
        | Some c when c.Params.Length = 2 && c.IsOmpParallel && foldReorderLicensed c && fpReproEnabled () ->
            [ $"{ind}// {(ompSuppressedPhrase (fpReproReason ()))}" ]
        | _ -> []
    // STREAMED FOLD (docs/plans/structural/07, decision D6 as read: a fold over
    // a segmented axis is the plain flat fold at the API). The operand is a
    // rank-1 variable bound with `.stream`: no array exists, so the fold walks
    // the store one block at a time -- the block is the store's own chunk
    // edge -- and folds the cells in STORAGE ORDER. That is the same operation
    // sequence as the flat fold over a materialized copy, so the two agree
    // bitwise; nothing is reassociated and no reorder licence is asked
    // (`omp` on the kernel is noted and ignored: the store is read serially).
    // Keyed by the binding's NAME, not by `arrName`: a streamed operand
    // RENDERS as the refusal sentinel (it is not materialized), which is
    // exactly right for every non-streamed path below and never matches here.
    let streamedKey =
        match arrExpr with
        | IRVar (vid, _) -> Map.tryFind vid ctx.VarNames |> Option.defaultValue arrName
        | _ -> arrName
    let streamedFold =
        match Map.tryFind streamedKey ctx.StreamedArrays with
        | Some spec when spec.VarType.IndexTypes.Length = 1 ->
            let pspec = (Blade.ProviderRegistry.tryFind spec.Provider).Value
            match pspec.GenStreamRows, pspec.StreamRowsBlock with
            | Some genRows, Some blockOf ->
                let n =
                    match spec.VarType.IndexTypes.[0].Extent with
                    | IRLit (IRLitInt n) -> n
                    | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed fold over '{spec.VarName}' needs a static extent")))
                let block = max 1L (blockOf spec.FilePath spec.VarName)
                let blk = $"{name}__blk"
                let stepOf (acc: string) (cell: string) : string * string list =
                    match resolveCallable kernelExpr with
                    | Some callable when callable.Params.Length = 2 ->
                        (match pathAOp with
                         | Some (op, _) -> ($"{acc} {(binOpToCpp op)} {cell}", [])
                         | None ->
                             let (wrapperCode, wname) = genCallableWrapper ctx.VarNames name callable
                             ($"{wname}({acc}, {cell})", wrapperCode |> List.map (fun s -> ind + s)))
                    | _ -> raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed fold over '{spec.VarName}': the kernel must be a two-argument fold kernel")))
                let (stepExpr, wrapperLines) = stepOf name $"{blk}[__q]"
                let seedLines, firstGuard =
                    match initExpr with
                    | Some initE -> ([ $"{ind}{elemStr} {name} = {(exprToCppCtx ctx initE)};" ], "")
                    | None ->
                        if n <= 0L then
                            raise (Blade.Diagnostics.BladeDiagnosticException (Blade.Diagnostics.Codes.backendLimit Blade.Ast.noSpan ($"streamed fold over '{spec.VarName}': the variable is empty and the fold has no seed")))
                        ([ $"{ind}{elemStr} {name} = ({elemStr})0;"; $"{ind}bool {name}__first = true;" ],
                         $"if ({name}__first) {{ {name} = {blk}[__q]; {name}__first = false; }} else ")
                let ompNote =
                    match resolveCallable kernelExpr with
                    | Some c when c.IsOmpParallel ->
                        let why = "the operand is streamed from the store in storage order"
                        [ $"{ind}// {(ompSuppressedPhrase why)}" ]
                    | _ -> []
                let rowRead =
                    genRows spec.FilePath spec.VarName streamedKey blk "__lo" "__hi" spec.VarType
                    |> List.map (fun s -> ind + "    " + s)
                Some (
                    elemErrCode @ ompNote @ wrapperLines
                    @ [ $"{ind}// reduce: STREAMED fold over '{spec.VarName}' in storage order, {block} cells per block (bitwise the flat fold)"
                        $"{ind}{elemStr}* {blk} = new {elemStr}[{block}];" ]
                    @ seedLines
                    @ [ $$"""{{ind}}for (size_t __lo = 0; __lo < {{n}}UL; __lo += {{block}}UL) {"""
                        $"{ind}    size_t __hi = __lo + {block}UL; if (__hi > {n}UL) __hi = {n}UL;" ]
                    @ rowRead
                    @ [ $"{ind}    for (size_t __q = 0; __q < __hi - __lo; __q++) {firstGuard}{name} = {stepExpr};"
                        $"{ind}}}"
                        $"{ind}delete[] {blk};" ])
            | _ ->
                let ctx' = addVarName binding.Id name ctx
                Some (codegenError ctx ind $"provider '{spec.Provider}' does not support per-block streamed folds ('{spec.VarName}' -- bind with .read)")
        | _ -> None
    match streamedFold with
    | Some code -> (code, addVarName binding.Id name ctx)
    | None ->
    let code =
        match resolveCallable kernelExpr with
        | Some callable when callable.Params.Length = 2 ->
            // Wrapper-based emission. The fold's
            // accumulator and the wrapper agree on type -- both come
            // from the array's elem type via the inferReduce
            // unification -- so the call `__r = __wrap(__r, arr[i])`
            // type-checks without narrowing/conversion warnings.
            let (wrapperCode, wname) = genCallableWrapper ctx.VarNames name callable
            let wrapperLines = wrapperCode |> List.map (fun s -> ind + s)
            // Seed and loop start: without init, seed = arr[0], fold from 1;
            // with init, seed = init, fold over ALL elements from 0.
            let (seedStr, loopStart) =
                match initExpr with
                | Some initE -> (exprToCppCtx ctx initE, "0")
                | None -> (elemAt "0", "1")
            match pathAOp, (if ompThreadsOn then parallelFold else None) with
            | Some (op, redOp), _ when not ompThreadsOn ->
                // Path A, THREADS SUPPRESSED. The team is gone; the vectorized
                // reduction stays, in the shared `BLADE_OMP_SIMD_REDUCTION`
                // spelling (portable to pre-4.0 OpenMP, where it expands to
                // nothing and the loop below is the plain serial chain).
                //
                // `ompApiUsedCell` is deliberately NOT set: this form calls no
                // omp_* runtime function, so the program must not acquire an
                // <omp.h> dependency it does not have -- which is also what lets
                // "serial emission contains no `omp_get_`" be checkable by
                // grepping the output.
                recordFoldForm name "simd-reduction" "reduce over a materialized array (Path A, thread team suppressed)"
                elemErrCode @ guardLines @ [
                    $"{ind}// reduce: comm-licensed reduction (builtin '{(binOpToCpp op)}'), flat sweep"
                    $"{ind}// {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))}"
                    $"{ind}const size_t {rnName} = {boundExpr};"
                    $"{ind}{elemStr} {name} = {seedStr};"
                ]
                @ (fpReassocSimdStmts redOp name "__ri" loopStart rnName [] elemAt
                   |> List.map (fun s -> ind + s))
            | Some (op, redOp), _ ->
                // Path A. No wrapper: the reduction clause requires the update
                // statement to have the `x = x op expr` shape, so the builtin op
                // is emitted directly (and the wrapper would be dead code).
                (ompApiUsedCell ()).Value <- true
                recordOmpConstruct name $"BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION({redOp}:{name})"
                    "licensed fold with a builtin operator: OpenMP reduction over the flat sweep (Path A)"
                recordFoldForm name "threaded(parallel for simd reduction)" "reduce over a materialized array (Path A)"
                elemErrCode @ guardLines @ [
                    $"{ind}// reduce: comm-licensed OpenMP reduction (builtin '{(binOpToCpp op)}'), flat sweep"
                    $"{ind}const size_t {rnName} = {boundExpr};"
                    $"{ind}{elemStr} {name} = {seedStr};"
                    $"{ind}BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION({redOp}:{name})"
                    $$"""{{ind}}for (size_t __ri = {{loopStart}}; __ri < {{rnName}}; __ri++) {"""
                    $"""{ind}    {name} = {name} {(binOpToCpp op)} {(elemAt "__ri")};"""
                    $"{ind}}}"
                ]
            | None, Some _ ->
                // Path B. Chunk bounds and combine order are fixed functions of
                // omp_get_max_threads(), so a fixed OMP_NUM_THREADS reproduces
                // bit-for-bit. The `__rcnt > 0` guard is load-bearing: a chunk
                // seeds from its first element, and with nothing to fold the
                // result is the seed alone (which is exactly the serial answer
                // for an empty range, init form included).
                (ompApiUsedCell ()).Value <- true
                // The partition is blade_fold::chunk_lo, the ONE function every
                // Path B site spells (review M14), so the chunks here, in the
                // deferred nest and in the threaded vector arm coincide.
                (foldLanesUsedCell ()).Value <- true
                recordFoldForm name $"threaded(Path B chunks, K-lanes({foldLaneCount}) per chunk)" "reduce over a materialized array (Path B)"
                let partName = $"__rpart_{name}"
                // K independent LANE accumulators inside each chunk. Lane l owns
                // __rlo+l, __rlo+l+K, __rlo+l+2K, ... and seeds from its own
                // first element (no identity needed); lanes fold into lane 0 in
                // fixed ascending order, so the emitted order is a fixed
                // function of (team size, K) -- determinism unchanged.
                //
                // Lanes are SEPARATE NAMED LOCALS, not an array: an array kept
                // live by the seed/tail loops can defeat scalar replacement,
                // reintroducing the load-modify-store latency the lanes exist
                // to remove. Below K elements the chunk falls back to a plain
                // serial fold, byte-identical to the lane form for len < K.
                let kLanes = foldLaneCount
                let laneName (l: int) = $"__rlane{l}"
                // [omp-coverage] (codegen test mode only, gap G55's team pin):
                // each thread marks its own slot inside the region, and the
                // team size / distinct threads are reported after it -- the
                // same line the nest emitters print.
                let covOn = ompTestModeEnabled ()
                let covDecl =
                    if covOn then [ $"{ind}        /* [omp-coverage] thread observation (test mode) */ int __omp_maxth_{name} = blade_omp_get_max_threads(); bool* __omp_seen_{name} = new bool[__omp_maxth_{name} > 0 ? __omp_maxth_{name} : 1](); int* __omp_team_{name} = new int[__omp_maxth_{name} > 0 ? __omp_maxth_{name} : 1]();" ]
                    else []
                let covMark =
                    if covOn then [ $"{ind}            __omp_seen_{name}[__rt] = true; __omp_team_{name}[__rt] = __rnt;" ]
                    else []
                let covReport =
                    if covOn then [ $"{ind}        {{ int __omp_distinct = 0; int __omp_teamsz = 0; for (int __t = 0; __t < __omp_maxth_{name}; __t++) {{ if (__omp_seen_{name}[__t]) __omp_distinct++; if (__omp_team_{name}[__t] > __omp_teamsz) __omp_teamsz = __omp_team_{name}[__t]; }} std::cout << \"[omp-coverage] region={name} teamsz=\" << __omp_teamsz << \" distinct=\" << __omp_distinct << \" maxth=\" << __omp_maxth_{name} << std::endl; delete[] __omp_seen_{name}; delete[] __omp_team_{name}; }}" ]
                    else []
                let laneBody =
                    [ $$"""{{ind}}            if (__rhi - __rlo < (size_t){{kLanes}}) {"""
                      $"""{ind}                {elemStr} __racc = {(elemAt "__rlo")};"""
                      $$"""{{ind}}                for (size_t __ri = __rlo + 1; __ri < __rhi; __ri++) {"""
                      $"""{ind}                    __racc = {wname}(__racc, {(elemAt "__ri")});"""
                      $$"""{{ind}}                }"""
                      $"{ind}                {partName}[__rt] = __racc;"
                      $$"""{{ind}}            } else {""" ]
                    @ [ for l in 0 .. kLanes - 1 ->
                          $"""{ind}                {elemStr} {(laneName l)} = {(elemAt (sprintf "__rlo + %d" l))};""" ]
                    @ [ $"{ind}                size_t __ri = __rlo + {kLanes};"
                        $$"""{{ind}}                for (; __ri + {{kLanes}} <= __rhi; __ri += {{kLanes}}) {""" ]
                    @ [ for l in 0 .. kLanes - 1 ->
                          $"""{ind}                    {(laneName l)} = {wname}({(laneName l)}, {(elemAt (sprintf "__ri + %d" l))});""" ]
                    @ [ $$"""{{ind}}                }""" ]
                    // Tail: at most K-1 elements remain, and they belong to
                    // lanes 0..K-2 in order (lane K-1 can never receive one).
                    @ [ for l in 0 .. kLanes - 2 ->
                          $$"""{{ind}}                if (__ri < __rhi) { {{(laneName l)}} = {{wname}}({{(laneName l)}}, {{(elemAt "__ri")}}); __ri++; }""" ]
                    @ [ for l in 1 .. kLanes - 1 ->
                          $"{ind}                {(laneName 0)} = {wname}({(laneName 0)}, {(laneName l)});" ]
                    @ [ $"{ind}                {partName}[__rt] = {(laneName 0)};"
                        $$"""{{ind}}            }""" ]
                elemErrCode @ guardLines @ wrapperLines @ [
                    (recordOmpConstruct name "omp parallel num_threads(T) + fixed-order combine"
                        "comm-licensed fold: contiguous per-thread partials, combined in thread order (Path B)"
                     $"{ind}// reduce: comm-licensed parallel fold, contiguous chunked partials ({kLanes}-lane)")
                    $"{ind}const size_t {rnName} = {boundExpr};"
                    $"{ind}{elemStr} {name} = {seedStr};"
                    $"{ind}{{"
                    $"{ind}    const size_t __rlo0 = {loopStart};"
                    $"{ind}    const size_t __rcnt = ({rnName} > __rlo0) ? ({rnName} - __rlo0) : (size_t)0;"
                    $$"""{{ind}}    if (__rcnt > 0) {"""
                    $"{ind}        int __rT = blade_omp_get_max_threads();"
                    $"{ind}        if ((size_t)__rT > __rcnt) __rT = (int)__rcnt;"
                    $"{ind}        if (__rT < 1) __rT = 1;"
                    // num_threads(n) is a REQUEST, not a guarantee: dynamic
                    // adjustment, or landing inside an enclosing parallel region
                    // with nesting off, can hand back a SMALLER team. Splitting
                    // by the requested count would then leave the tail chunks
                    // uncomputed and silently drop elements, so the split reads
                    // the team size the region actually got, and the combine
                    // reads it back through a slot only thread 0 writes (the
                    // region's implicit barrier publishes it).
                    $"{ind}        int __rTact = __rT;"
                    $"{ind}        {elemStr}* {partName} = new {elemStr}[__rT];"
                ] @ covDecl @ [
                    $"{ind}        BLADE_OMP_PARALLEL_NUM_THREADS(__rT)"
                    $$"""{{ind}}        {"""
                    $"{ind}            const int __rnt = blade_omp_get_num_threads();"
                    $"{ind}            const int __rt = blade_omp_get_thread_num();"
                    $"{ind}            if (__rt == 0) __rTact = __rnt;"
                ] @ covMark @ [
                    $"{ind}            const size_t __rlo = __rlo0 + blade_fold::chunk_lo(__rcnt, (size_t)__rt, (size_t)__rnt);"
                    $"{ind}            const size_t __rhi = __rlo0 + blade_fold::chunk_lo(__rcnt, (size_t)__rt + 1, (size_t)__rnt);"
                ] @ laneBody @ [
                    $$"""{{ind}}        }"""
                ] @ covReport @ [
                    $"{ind}        for (int __rt = 0; __rt < __rTact; __rt++) {name} = {wname}({name}, {partName}[__rt]);"
                    $"{ind}        delete[] {partName};"
                    $$"""{{ind}}    }"""
                    $"{ind}}}"
                ]
            | None, None when fpReassocEnabled () && foldReorderLicensed callable ->
                // BLADE_FP_REASSOC. The user did not write `omp`, so there are
                // no threads here and none are added: this is Path B's flat
                // shape with the thread chunking removed -- ONE chunk covering
                // [loopStart, bound), swept by K lanes and combined in fixed
                // ascending order. The knob supplies the reproducibility opt-in
                // that `omp` supplies on the parallel paths; the LICENCE is the
                // ordinary `foldReorderLicensed` (a recognised builtin body, or
                // declared comm), so an unlicensed user kernel still falls to
                // the serial arm below with the knob on.
                //
                // LANES, NOT `omp simd reduction`, AND THAT IS A MEASUREMENT.
                // The simd form (`fpReassocSimdStmts`) is available here -- the
                // licence class that reaches this arm is usually a builtin `+`
                // -- and it is what sites `IRProdSum` and `genReduceComputeBinding`
                // now emit. It LOSES here, at both ends of the memory hierarchy
                // (sumred = `reduce(x, (+))`, f64, OMP_NUM_THREADS=1, medians
                // over 3 processes x 5 reps, `bench\blade`):
                //
                //     n = 10000019 (bandwidth-bound)  serial 8.02 ms
                //                                     8 lanes 2.78 ms  (2.88x)
                //                                     simd    3.10 ms  (2.59x)
                //     n = 300007 (cache-resident)     serial  223 us
                //                                     8 lanes 29.9 us  (7.45x)
                //                                     simd    56.7 us  (3.93x)
                //
                // and the cache-resident gap -- 1.9x, entirely outside the
                // spread -- is the one that says why. A ONE-STREAM add chain is
                // register-cheap, so all 8 named lanes stay in registers and GCC
                // additionally packs and unrolls them (the emitted lane form
                // itself vectorizes: 2 `vaddpd` per iteration, 8 doubles). The
                // simd form hands the compiler ONE 4-wide accumulator and it
                // stops there, so it runs 4 chains where the lanes run 8+.
                // Where the simd form wins is the shapes with more per-element
                // work, where the lanes spill instead (see the two sites above).
                //
                // DETERMINISM, on this arm, is therefore still the STRONG
                // property the knob originally promised: no omp_get_max_threads,
                // no team, no pragma at all, so the answer is a fixed function
                // of the data and K alone and reproduces across toolchains.
                // One operand stream (a materialized rank-1 array), so the
                // measured anchor applies directly -- see laneCountForStreams.
                let kLanes = foldLaneCount
                let (laneStmts, resultLane) =
                    fpReassocLaneStmts kLanes elemStr "__rlane" "__ri" "__rlo" "__rhi" elemAt
                        (fun acc rhs -> $"{acc} = {wname}({acc}, {rhs});")
                recordFoldForm name $"K-lanes({kLanes})" "reduce over a materialized array"
                elemErrCode @ guardLines @ wrapperLines @ pathBSuppressedNote () @ [
                    $"{ind}// reduce: accumulator loop, eager ({kLanes}-lane, licensed reorder)"
                    $"{ind}{elemStr} {name} = {seedStr};"
                    $"{ind}{{"
                    $"{ind}    const size_t __rlo = {loopStart};"
                    $"{ind}    const size_t __rhi = {boundExpr};"
                    $"{ind}    const size_t __rcnt = (__rhi > __rlo) ? (__rhi - __rlo) : (size_t)0;"
                    $$"""{{ind}}    if (__rcnt < (size_t){{kLanes}}) {"""
                    // Below K elements there is nothing to interleave, so this
                    // is the serial chain verbatim -- same order, same bits.
                    $$"""{{ind}}        for (size_t __ri = __rlo; __ri < __rhi; __ri++) {"""
                    $"""{ind}            {name} = {wname}({name}, {(elemAt "__ri")});"""
                    $$"""{{ind}}        }"""
                    $$"""{{ind}}    } else {"""
                ] @ (laneStmts |> List.map (fun s -> ind + "        " + s)) @ [
                    $"{ind}        {name} = {wname}({name}, {resultLane});"
                    $$"""{{ind}}    }"""
                    $"{ind}}}"
                ]
            | None, None ->
            recordFoldForm name
                (if foldReorderLicensed callable then serialFoldForm () else unlicensedFoldForm callable)
                "reduce over a materialized array"
            elemErrCode @ guardLines @ wrapperLines @ pathBSuppressedNote () @ [
                $"{ind}// reduce: accumulator loop, eager"
                $"{ind}{elemStr} {name} = {seedStr};"
                $$"""{{ind}}for (size_t __ri = {{loopStart}}; __ri < {{boundExpr}}; __ri++) {"""
                $"""{ind}    {name} = {wname}({name}, {(elemAt "__ri")});"""
                $"{ind}}}"
            ]
        | _ ->
            let errLines = codegenError ctx ind "reduce: kernel must resolve to a binary callable (typechecker or IR bug if not)"
            elemErrCode @ errLines
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')

and genReduceComputeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (compExpr: IRExpr) (kernelExpr: IRExpr) (seedExpr: IRExpr) : string list * CodeGenContext =
    // HOISTED-OPERAND PRELUDE. The lift pass pulls a synthesized loop
    // application out of a combinator's `Arrays` slot into its own let
    // (`liftChildIncludingLoopApp`), so a deferred computation whose operand is
    // a broadcast -- `reduce(exp <@> (i * w * ts), (+))`, i.e. units/065's shape
    // once the enclosing kernel body is emitted through the lifted form -- arrives
    // here as `IRLet(v, IRApp(IRObjectFor ...), IRApplyCombinator ...)`, not as a
    // bare combinator. The leaf check below then saw an IRLet, not an apply, and
    // refused a perfectly well-formed fold.
    //
    // Emit those bindings as a statement prelude (genBinding is the same
    // statement-form dispatch a body-level let uses) and fold over the
    // combinator underneath, with the hoisted names in scope. Only ever turns a
    // refusal into code: an IRLet-wrapped computation had no other outcome.
    let rec peelCompLets (accCtx: CodeGenContext) (accLines: string list) (e: IRExpr) =
        match e with
        | IRLet (id, value, body) ->
            let tempBinding = {
                Id = id; Name = $"__v{id}"; Type = inferExprType value
                Value = value; IsConst = false; IsMutable = true
            }
            let (code, nextCtx) = genBinding accCtx tempBinding builder
            peelCompLets nextCtx (accLines @ code) body
        | _ -> (accCtx, accLines, e)
    let (ctx, prelude, compExpr) = peelCompLets ctx [] compExpr
    // DEFERRED OPERANDS (G12, bench llvm-acceptance-B F5). A leaf map whose
    // OPERAND is itself a still-deferred map -- `let r = method_for(x) <@>
    // half` then `reduce(method_for(r) <@> sq, (+))`, at top level or in a
    // top-level block -- reached the fold nest, which reads every operand BY
    // NAME, while `r` was only a `// <deferred computation>` comment: g++
    // rejected the program (BL9002). Materialize each such operand into a
    // TEMPORARY first and fold over it. Deliberately a temporary, NOT
    // `forceDeferredArrayInput`'s materialize-under-its-own-name: the
    // interpreter's twin (Interp/Loops.resolveFusedArraySource) forces the
    // operand without registering it, so it does not join the auto-print list
    // and a later consumer still sees it deferred. The operand is restored to
    // the deferred map after the fold for the same reason. A REDUCTION JOIN
    // (tuple kernel + tuple seed) is left alone: its legs SHARE a named
    // deferred operand by design (genReduceJoinCore's join-share leaf), and an
    // id that is itself a leaf is never forced (it must stay an unforced apply).
    let isJoin =
        match kernelExpr, seedExpr with
        | IRTuple ks, IRTuple ss -> ks.Length = ss.Length && ks.Length >= 2
        | _ -> false
    let deferredOperandIds =
        if isJoin then [] else
        let rec resolve e =
            match e with
            | IRVar (id, _) ->
                (match Map.tryFind id ctx.DeferredComputations with
                 | Some d -> resolve d
                 | None -> e)
            | _ -> e
        let rec leaves e =
            match resolve e with
            | IRFusion (l, r) -> leaves l @ leaves r
            | other -> [ other ]
        leaves compExpr
        |> List.collect (function
            | IRApplyCombinator info ->
                info.Arrays |> List.choose (function
                    | IRVar (id, _) ->
                        (match Map.tryFind id ctx.DeferredComputations with
                         | Some (IRApplyCombinator _ | IRComposeApply _ | IRFunctorMap _) -> Some id
                         | _ -> None)
                    | _ -> None)
            | _ -> [])
        |> List.distinct
        |> List.filter (fun id ->
            // The ids the leaf walk itself passes through stay deferred.
            let rec onSpine e =
                match e with
                | IRVar (vid, _) when vid = id -> true
                | IRVar (vid, _) ->
                    (match Map.tryFind vid ctx.DeferredComputations with
                     | Some d -> onSpine d
                     | None -> false)
                | IRFusion (l, r) -> onSpine l || onSpine r
                | _ -> false
            not (onSpine compExpr))
    // DEFERRED CAPTURES (the G41 row's nested-kernel probe, Codex W2F-3
    // finding 13): a leaf's map kernel -- or the fold kernel -- that CLOSES
    // OVER a still-deferred binding gets it forwarded BY NAME at every call
    // site, exactly like an operand. `reduce(method_for(range<I>) <@>
    // lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> g(j), ..), ..)`
    // over a deferred `g` passed `g` to the outer kernel while `g` was only a
    // `// <deferred computation>` comment (BL9002 "'g' was not declared").
    // genComputeBinding forces its kernel's deferred captures for the same
    // reason (collectDeferredKernelCaptures), and the same way: UNDER THE
    // BINDING'S OWN NAME (forceDeferredBindingIds), not as a temporary -- the
    // interpreter materializes a captured deferred binding into the scope, so
    // it joins the auto-print list there, and must here too.
    let deferredCaptureIds =
        let rec resolve e =
            match e with
            | IRVar (id, _) ->
                (match Map.tryFind id ctx.DeferredComputations with
                 | Some d -> resolve d
                 | None -> e)
            | _ -> e
        let rec leaves e =
            match resolve e with
            | IRFusion (l, r) -> leaves l @ leaves r
            | other -> [ other ]
        let rec onSpine (id: IRId) e =
            match e with
            | IRVar (vid, _) when vid = id -> true
            | IRVar (vid, _) ->
                (match Map.tryFind vid ctx.DeferredComputations with
                 | Some d -> onSpine id d
                 | None -> false)
            | IRFusion (l, r) -> onSpine id l || onSpine id r
            | _ -> false
        let leafKernels =
            leaves compExpr |> List.collect (function
                | IRApplyCombinator info -> [ info.Kernel ]
                | _ -> [])
        let foldKernels = match kernelExpr with IRTuple ks -> ks | k -> [ k ]
        (leafKernels @ foldKernels)
        |> List.collect (collectDeferredKernelCaptures ctx)
        |> List.distinct
        |> List.filter (fun id ->
            not (List.contains id deferredOperandIds)
            && not (onSpine id compExpr)
            && (match Map.tryFind id ctx.DeferredComputations with
                | Some (IRApplyCombinator _ | IRComposeApply _ | IRFunctorMap _) -> true
                | _ -> false))
    let (capForceLines, ctx) = deferredCaptureIds |> forceDeferredBindingIds ctx builder ($"{bindingCppName binding}__cap")
    let prelude = prelude @ capForceLines
    let (ctxForced, forceLines) =
        deferredOperandIds |> List.fold (fun (accCtx: CodeGenContext, accLines) id ->
            let ty = inferExprType accCtx.DeferredComputations.[id]
            let tmp = $"{bindingCppName binding}__in{id}"
            let matBinding = { Id = id; Name = tmp; Type = ty; Value = IRCompute (IRVar (id, ty)); IsConst = true; IsMutable = false }
            let (code, c1) = genBinding accCtx matBinding builder
            ({ c1 with DeferredComputations = Map.remove id c1.DeferredComputations }, accLines @ code)) (ctx, [])
    let (code, outCtx) = genReduceComputeBindingCore ctxForced binding builder compExpr kernelExpr seedExpr
    let outCtx =
        deferredOperandIds |> List.fold (fun (c: CodeGenContext) id ->
            { c with
                DeferredComputations = Map.add id ctx.DeferredComputations.[id] c.DeferredComputations
                VarNames =
                    match Map.tryFind id ctx.VarNames with
                    | Some n -> Map.add id n c.VarNames
                    | None -> Map.remove id c.VarNames }) outCtx
    (prelude @ forceLines @ code, outCtx)

/// The orbit-class fold's C++ (Blade.OrbitFold, whose header carries the
/// regrouping proof). One pass over the packed pool in storage (lex) order:
/// the nest splits every level after the first into its EQUAL case (the
/// coordinate repeats the previous one) and its GREATER loop, so every leaf
/// has a static equality pattern -- hence a static orbit weight and a
/// static accumulator -- and the innermost greater case is a contiguous run
/// of the pool, an `omp simd` reduction into its class accumulator. The
/// class sums then combine by additions only (`OrbitFold.repSteps`), never a
/// multiply a contracting compiler could fuse.
and genOrbitFoldCpp (ctx: CodeGenContext) (ind: string) (name: string) (gName: string) (p: Blade.OrbitFold.OrbitPlan) : string list =
    let accCpp = primTypeToCpp p.AccTy
    let elemCpp = primTypeToCpp p.ElemTy
    let n = p.Extent
    let r = p.Rank
    let renderF (xName: string) =
        let c = { ctx with VarNames = Map.add Blade.OrbitFold.readVarId xName ctx.VarNames }
        exprToCppCtx c p.Body
    let accName (k: int) = $"__oa{k}"
    let weightIndex (mults: int list) =
        let w = Blade.OrbitFold.orbitWeight mults
        p.Weights |> List.findIndex ((=) w)
    let fx = renderF "__ox"
    let fxn = renderF "__oxn"
    let cellUpdates (mults: int list) (i2: string) : string list =
        if p.Strict then
            [ yield $"{i2}{accName 0} = {accName 0} + ({fx});"
              if p.AccCount = 2 then
                  yield $"{i2}{{ const {elemCpp} __oxn = -__ox; {accName 1} = {accName 1} + ({fxn}); }}" ]
        else
            let k = weightIndex mults
            [ $"{i2}{accName k} = {accName k} + ({fx});" ]
    let single (mults: int list) (i2: string) : string list =
        [ yield $"{i2}{{"
          yield $"{i2}    const {elemCpp} __ox = __opool[__oo++];"
          yield! cellUpdates mults (i2 + "    ")
          yield $"{i2}}}" ]
    let run (mults: int list) (prev: string) (i2: string) : string list =
        let accs = if p.Strict then [ 0 .. p.AccCount - 1 ] else [ weightIndex mults ]
        let red = accs |> List.map accName |> String.concat ", "
        [ yield $"{i2}{{"
          yield $"{i2}    const size_t __olen = (size_t){n} - 1 - {prev};"
          yield $"{i2}    const {elemCpp}* BLADE_RESTRICT __orow = __opool + __oo;"
          yield $"{i2}    BLADE_OMP_SIMD_REDUCTION(+:{red})"
          yield $"{i2}    for (size_t __ok = 0; __ok < __olen; __ok++) {{"
          yield $"{i2}        const {elemCpp} __ox = __orow[__ok];"
          yield! cellUpdates mults (i2 + "        ")
          yield $"{i2}    }}"
          yield $"{i2}    __oo += __olen;"
          yield $"{i2}}}" ]
    let incLast (m: int list) = List.take (m.Length - 1) m @ [ List.last m + 1 ]
    let rec gen (t: int) (prev: string) (mults: int list) (i2: string) : string list =
        if t = r then
            [ if not p.Strict then yield! single (incLast mults) i2
              yield! run (mults @ [ 1 ]) prev i2 ]
        else
            [ if not p.Strict then yield! gen (t + 1) prev (incLast mults) i2
              yield $"{i2}for (size_t __oc{t} = {prev} + 1; __oc{t} < {n}; __oc{t}++) {{"
              yield! gen (t + 1) $"__oc{t}" (mults @ [ 1 ]) (i2 + "    ")
              yield $"{i2}}}" ]
    let i1 = ind + "    "
    let (combineLines, total) = genOrbitTotalCpp ctx i1 p
    let accDecl = [ 0 .. p.AccCount - 1 ] |> List.map (fun k -> $"{accName k} = {accCpp}(0)") |> String.concat ", "
    let group = if p.Strict then "AntisymIdx" else "SymIdx"
    // THE THREADED FORM (bench G32): a `where omp` licence on any level's
    // fold is spent as a team over the OUTERMOST class level, decided by
    // `OrbitFold.threadedForm` (the plan line says the same).
    let threaded = Blade.OrbitFold.threadedForm p
    let ompMarker =
        if Blade.OrbitFold.ompRequested p && not threaded then
            let why =
                if not (ompThreadEmissionEnabled ()) then ompThreadsSuppressedReason ()
                else "the orbit-class fold's pool has fewer than two first-coordinate rows (nothing to divide)"
            [ $"{i1}// {(ompSuppressedPhrase why)}" ]
        else []
    let strictLit = if p.Strict then "true" else "false"
    [ yield $"{ind}{accCpp} {name} = {exprToCppCtx ctx p.OuterSeed};"
      yield $"{ind}{{"
      yield $"{i1}// {Blade.OrbitFold.planFormCpp p}: the logical sum over the {group}<{r}, {n}> pool of `{p.SourceName}`, regrouped (licensed); class sums combine by additions only"
      yield! ompMarker
      yield $"{i1}{accCpp} {accDecl};"
      if p.PoolCells > 0L && threaded then
          // Rows i of the first coordinate are independent work items: row i
          // starts at pool offset total - canon_count(n - i) and walks its
          // own equality patterns into its own class accumulators. Rows are
          // dealt boustrophedon (block b of T rows: thread t takes row
          // bT + t, reversed on odd blocks), which balances the triangular
          // row lengths and is a fixed function of the team size; per-thread
          // class sums then join the totals in thread order. Each class total
          // is still a sum over its disjoint terms, so the whole is one more
          // association of the licensed `+` folds -- additions only, as in
          // the serial pass -- and a fixed OMP_NUM_THREADS reproduces it.
          (ompApiUsedCell ()).Value <- true
          let parts = p.AccCount
          yield $"{i1}const {elemCpp}* BLADE_RESTRICT __opool = nested_array_utilities::pool_base({gName}.data);"
          yield $"{i1}const size_t __ototal = nested_array_utilities::canon_count({n}, {r}, {strictLit});"
          yield $"{i1}int __oT = blade_omp_get_max_threads();"
          yield $"{i1}if ((size_t)__oT > (size_t){n}) __oT = (int){n};"
          yield $"{i1}if (__oT < 1) __oT = 1;"
          // num_threads is a request: the split reads the team the region
          // got, and the combine reads it back (Path B's rule).
          yield $"{i1}int __oTact = __oT;"
          yield $"{i1}{accCpp}* __opart = new {accCpp}[(size_t)__oT * {parts}];"
          yield $"{i1}BLADE_OMP_PARALLEL_NUM_THREADS(__oT)"
          yield $"{i1}{{"
          let i2 = i1 + "    "
          yield $"{i2}const size_t __ont = (size_t)blade_omp_get_num_threads();"
          yield $"{i2}const size_t __ot = (size_t)blade_omp_get_thread_num();"
          yield $"{i2}if (__ot == 0) __oTact = (int)__ont;"
          yield $"{i2}{accCpp} {accDecl};"
          yield $"{i2}for (size_t __ob = 0; __ob * __ont < (size_t){n}; __ob++) {{"
          let i3 = i2 + "    "
          yield $"{i3}const size_t __oc1 = __ob * __ont + ((__ob & 1) ? (__ont - 1 - __ot) : __ot);"
          yield $"{i3}if (__oc1 >= (size_t){n}) continue;"
          yield $"{i3}size_t __oo = __ototal - nested_array_utilities::canon_count((size_t){n} - __oc1, {r}, {strictLit});"
          yield! gen 2 "__oc1" [ 1 ] i3
          yield $"{i2}}}"
          for k in 0 .. parts - 1 do
              yield $"{i2}__opart[__ot * {parts} + {k}] = {accName k};"
          yield $"{i1}}}"
          yield $"{i1}for (int __ot = 0; __ot < __oTact; __ot++) {{"
          for k in 0 .. parts - 1 do
              yield $"{i1}    {accName k} = {accName k} + __opart[(size_t)__ot * {parts} + {k}];"
          yield $"{i1}}}"
          yield $"{i1}delete[] __opart;"
      elif p.PoolCells > 0L then
          yield $"{i1}const {elemCpp}* BLADE_RESTRICT __opool = nested_array_utilities::pool_base({gName}.data);"
          yield $"{i1}size_t __oo = 0;"
          yield $"{i1}for (size_t __oc1 = 0; __oc1 < {n}; __oc1++) {{"
          yield! gen 2 "__oc1" [ 1 ] (i1 + "    ")
          yield $"{i1}}}"
      yield! combineLines
      yield $"{i1}{name} = {name} + {total};"
      yield $"{ind}}}" ]

/// The orbit plan's ADDITION-ONLY combine of its class accumulators
/// `__oa<k>` into the logical total (`rep(S, w)` as doubling / adding
/// steps, never a multiply a compiler could contract), plus the strict
/// group's `f(+0)` zero-class value: the lines (at indent `i1`) and the
/// total's name. Shared by the orbit-class pass and the compact row-class
/// consumer, so both combine the same way.
and genOrbitTotalCpp (ctx: CodeGenContext) (i1: string) (p: Blade.OrbitFold.OrbitPlan) : string list * string =
    let accCpp = primTypeToCpp p.AccTy
    let elemCpp = primTypeToCpp p.ElemTy
    let combineLines = ResizeArray<string>()
    if p.Strict && p.ZeroCells > 0L then
        let c = { ctx with VarNames = Map.add Blade.OrbitFold.readVarId "__ox" ctx.VarNames }
        combineLines.Add($"{i1}const {elemCpp} __ox = {elemCpp}(0);")
        combineLines.Add($"{i1}const {accCpp} __of0 = {(exprToCppCtx c p.Body)};")
    let mutable tmp = 0
    let fresh (value: string) =
        tmp <- tmp + 1
        let t = $"__ot{tmp}"
        combineLines.Add($"{i1}const {accCpp} {t} = {value};")
        t
    let rec emitC (e: Blade.OrbitFold.Combine) : string =
        match e with
        | Blade.OrbitFold.CAcc k -> $"__oa{k}"
        | Blade.OrbitFold.CZero -> "__of0"
        | Blade.OrbitFold.CNeg x -> fresh $"-{emitC x}"
        | Blade.OrbitFold.CAdd (a, b) ->
            let a' = emitC a
            let b' = emitC b
            fresh $"{a'} + {b'}"
        | Blade.OrbitFold.CRep (x, w) ->
            let b = emitC x
            let mutable acc = b
            for dbl in Blade.OrbitFold.repSteps w do
                acc <- fresh (if dbl then $"{acc} + {acc}" else $"{acc} + {b}")
            acc
    let total = emitC p.Total
    (List.ofSeq combineLines, total)

/// The compact row-class CONSUMER (Blade.Optimize.planCompactRowClasses, B3):
/// the producer's nest stored each packed row's class partials into
/// `parts` (one rank-1 array per accumulator, `n` rows); each class total is
/// their sum in ASCENDING ROW ORDER from +0, then the orbit plan's own
/// addition-only combine and outer seed -- the same multiset, regrouped by
/// rows (licensed, as the orbit pass is), and a function of the data alone.
and genCompactRowCombineCpp (ctx: CodeGenContext) (ind: string) (name: string) (p: Blade.OrbitFold.OrbitPlan) (parts: string list) : string list =
    let accCpp = primTypeToCpp p.AccTy
    let i1 = ind + "    "
    let (combineLines, total) = genOrbitTotalCpp ctx i1 p
    let classes = parts.Length
    recordCodegenDecision "compact-row-classes" "" Blade.Effects.Applied
        [ $"compact row classes ({classes} classes), per-row partials, combined in row order"
          $"`{p.SourceName}`'s packed rows each stored their class partials as they were produced; no second pass over the {p.PoolCells}-cell pool; addition-only combine (licensed regrouping); bits independent of team size" ]
    recordFoldForm "" $"compact row classes (rank {p.Rank}, {p.ClassCount} classes)"
        $"the orbit-class sums of `{p.SourceName}` (rank {p.Rank}, extent {p.Extent}) taken per packed row behind the producer, combined in row order by additions only (licensed regrouping); no pass over the pool"
    let ompMarker =
        if Blade.OrbitFold.ompRequested p then
            let phrase = ompSuppressedPhrase "compact row classes: the fold's terms were summed per row inside the producer's nest (its team, when it has one); the row combine is a serial pass over the row partials"
            [ $"{i1}// {phrase}" ]
        else []
    [ yield $"{ind}{accCpp} {name} = {exprToCppCtx ctx p.OuterSeed};"
      yield $"{ind}{{"
      yield $"{i1}// compact row classes: the class partials of each packed row of `{p.SourceName}` were stored as the row was produced; each class sums them in ascending row order, then the orbit plan's addition-only combine"
      yield! ompMarker
      for k in 0 .. classes - 1 do
          yield $"{i1}{accCpp} __oa{k} = {accCpp}(0);"
          yield $"{i1}for (size_t __ori = 0; __ori < (size_t){p.Extent}; __ori++) __oa{k} = __oa{k} + {parts.[k]}[__ori];"
          yield $"{i1}delete[] {parts.[k]};"
      yield! combineLines
      yield $"{i1}{name} = {name} + {total};"
      yield $"{ind}}}" ]

/// THE ORDERED ORBIT SCATTER's C++ (Blade.OrbitFold.planStrict, whose header
/// carries the order proof; the runtime is blade_orbit_scatter.hpp). `f` is
/// rendered ONCE as a lambda of the cell value; each level's fold kernel is
/// the builtin `acc + z` when it is exactly `p0 + p1`, else the callable's
/// own wrapper (so a `where repro` kernel keeps its BLADE_REPRO_FN body);
/// seeds are evaluated once, outer first (they are total and
/// coordinate-free). Every statement is one line with BLOCK comments only:
/// a kernel-body fold's lines may be space-joined. `ompNote`: the census
/// marker when a level asked for threads (never silent).
and genOrbitScatterCpp (ctx: CodeGenContext) (ind: string) (name: string) (gName: string)
                       (sp: Blade.OrbitFold.StrictPlan) (orbitWhy: string option) (ompNote: string option) : string list =
    let s = sp.Shape
    let accCpp = primTypeToCpp s.AccTy
    let elemCpp = primTypeToCpp s.ElemTy
    let n = s.Extent
    let tag = sanitizeCppName name
    let fLambda =
        let c = { ctx with VarNames = Map.add Blade.OrbitFold.readVarId "__sx" ctx.VarNames }
        $"[&]({elemCpp} __sx) -> {accCpp} {{ return {(exprToCppCtx c s.Body)}; }}"
    let kernels =
        s.Levels |> List.mapi (fun k (l: Blade.OrbitFold.OrbitLevel) ->
            if sp.LevelAdd.[k] then
                ([ $"auto __sk{k}_{tag} = []({accCpp} __a, {accCpp} __b) -> {accCpp} {{ return __a + __b; }};" ], $"__sk{k}_{tag}")
            else
                genCallableWrapper ctx.VarNames $"{name}__sc{k}" l.Fold)
    let kName k = snd kernels.[k]
    let seed k = exprToCppCtx ctx s.Levels.[k].Seed
    let g = sp.RowGroup
    let b = sp.ColBlock
    let vadd k = sp.LevelAdd.[k] && s.AccTy = ETFloat64 && g % 4 = 0
    let lit (x: bool) = if x then "true" else "false"
    (orbitScatterUsedCell ()).Value <- true
    Blade.OrbitFold.recordScatterTaken name sp orbitWhy
    let i1 = ind + "    "
    let call =
        if s.Rank = 2 then
            $"{name} = blade_scatter::scatter2<{g}, {b}, {lit (vadd 1)}>(__spool_{tag}, (size_t){n}, {accCpp}({seed 1}), {name}, __sf_{tag}, {kName 1}, {kName 0});"
        else
            $"{name} = blade_scatter::scatter3<{g}, {b}, {lit (vadd 2)}, {lit (vadd 1)}>(__spool_{tag}, (size_t){n}, {accCpp}({seed 2}), {accCpp}({seed 1}), {name}, __sf_{tag}, {kName 2}, {kName 1}, {kName 0});"
    [ yield $"{ind}{accCpp} {name} = {seed 0};"
      yield $"{ind}{{"
      yield $"{i1}/* {Blade.OrbitFold.scatterForm sp} (strict: no licence consumed) */"
      match ompNote with
      | Some why -> yield $"{i1}/* {(ompSuppressedPhrase why)} */"
      | None -> ()
      for (code, _) in kernels do
          for line in code do yield i1 + line
      yield $"{i1}auto __sf_{tag} = {fLambda};"
      yield $"{i1}const {elemCpp}* __spool_{tag} = nested_array_utilities::pool_base({gName}.data);"
      yield i1 + call
      yield $"{ind}}}" ]

/// THE ROW-OWNED PULL SCATTER's C++ (round 3, D-P3; blade_orbit_scatter.hpp
/// pull3 / leaf3_pull, whose header carries the ownership proof): the rank-3
/// scatter's leaf chains partitioned by H row over a team of
/// `blade_omp_get_max_threads()` (pull3 clamps it to the extent), a
/// team-completion barrier, then scatter3's own serial middle / outer tail.
/// `f`, the kernels and the seeds are rendered exactly as genOrbitScatterCpp
/// renders them (one lambda of the cell value; `acc + z` for a plain `+`,
/// else the callable's wrapper; seeds once, outer first). `matrix` is the
/// admission's five-party audit; `ompNote` the census marker for a request
/// this form does not spend (an inner level's). Under omp test mode the team
/// is observed ([omp-coverage] region=<name>). Every statement one line with
/// BLOCK comments only, like the scatter's.
and genOrbitPullCpp (ctx: CodeGenContext) (ind: string) (name: string) (gName: string)
                    (sp: Blade.OrbitFold.StrictPlan) (orbitWhy: string option) (ompNote: string option) (matrix: string list) : string list =
    let s = sp.Shape
    let accCpp = primTypeToCpp s.AccTy
    let elemCpp = primTypeToCpp s.ElemTy
    let n = s.Extent
    let tag = sanitizeCppName name
    let fLambda =
        let c = { ctx with VarNames = Map.add Blade.OrbitFold.readVarId "__sx" ctx.VarNames }
        $"[&]({elemCpp} __sx) -> {accCpp} {{ return {(exprToCppCtx c s.Body)}; }}"
    let kernels =
        s.Levels |> List.mapi (fun k (l: Blade.OrbitFold.OrbitLevel) ->
            if sp.LevelAdd.[k] then
                ([ $"auto __sk{k}_{tag} = []({accCpp} __a, {accCpp} __b) -> {accCpp} {{ return __a + __b; }};" ], $"__sk{k}_{tag}")
            else
                genCallableWrapper ctx.VarNames $"{name}__sc{k}" l.Fold)
    let kName k = snd kernels.[k]
    let seed k = exprToCppCtx ctx s.Levels.[k].Seed
    let g = sp.RowGroup
    let b = sp.ColBlock
    let vadd k = sp.LevelAdd.[k] && s.AccTy = ETFloat64 && g % 4 = 0
    let lit (x: bool) = if x then "true" else "false"
    (orbitScatterUsedCell ()).Value <- true
    (ompApiUsedCell ()).Value <- true
    Blade.OrbitFold.recordPullTaken name sp orbitWhy matrix
    recordOmpConstruct name "omp parallel num_threads(T) + ascending row claims + team-completion barrier"
        $"row-owned pull scatter (rank 3, extent {n}): the leaf chains' H rows claimed in ascending order by the team, each row's chains on one thread; the middle and outer chains serial after the barrier; the EXECUTED form is chosen at run time -- `row-pull` on an actual team of two or more, `serial-leaf3` on a requested or actual team of one -- and recorded per site with the requested and actual team in the run record's `forms` census (BLADE_RUN_RECORD), which this static record cannot observe"
    let i1 = ind + "    "
    let instrument = ompTestModeEnabled ()
    let maxth = $"__omp_maxth_{tag}"
    let seen = $"__omp_seen_{tag}"
    let team = $"__omp_team_{tag}"
    let observer =
        if instrument then $"[&](int __tn, int __nt) {{ {seen}[__tn] = true; {team}[__tn] = __nt; }}"
        else "blade_scatter::no_observe{}"
    // The EXECUTED form is chosen at run time (the requested and the actual
    // team; blade_orbit_scatter.hpp leaf3_pull) and recorded per site in the
    // runtime form census (the run record's `forms`), under this name.
    let runVar = $"__srun_{tag}"
    let call =
        $"{name} = blade_scatter::pull3<{g}, {b}, {lit (vadd 2)}, {lit (vadd 1)}>(__spool_{tag}, (size_t){n}, {accCpp}({seed 2}), {accCpp}({seed 1}), {name}, __sf_{tag}, {kName 2}, {kName 1}, {kName 0}, blade_omp_get_max_threads(), {observer}, \"{tag}\", &{runVar});"
    [ yield $"{ind}{accCpp} {name} = {seed 0};"
      yield $"{ind}{{"
      yield $"{i1}/* {Blade.OrbitFold.pullForm sp} (strict: no licence consumed) */"
      match ompNote with
      | Some why -> yield $"{i1}/* {(ompSuppressedPhrase why)} */"
      | None -> ()
      for (code, _) in kernels do
          for line in code do yield i1 + line
      yield $"{i1}auto __sf_{tag} = {fLambda};"
      yield $"{i1}const {elemCpp}* __spool_{tag} = nested_array_utilities::pool_base({gName}.data);"
      if instrument then
          yield $"{i1}/* [omp-coverage] thread observation (test mode) */ int {maxth} = blade_omp_get_max_threads(); bool* {seen} = new bool[{maxth} > 0 ? {maxth} : 1](); int* {team} = new int[{maxth} > 0 ? {maxth} : 1]();"
      yield $"{i1}blade_scatter::PullRun {runVar};"
      yield i1 + call
      if instrument then
          yield $"{i1}{{ int __omp_distinct = 0; int __omp_teamsz = 0; for (int __t = 0; __t < {maxth}; __t++) {{ if ({seen}[__t]) __omp_distinct++; if ({team}[__t] > __omp_teamsz) __omp_teamsz = {team}[__t]; }} std::cout << \"[omp-coverage] region={tag} teamsz=\" << __omp_teamsz << \" distinct=\" << __omp_distinct << \" maxth=\" << {maxth} << \" form=\" << {runVar}.form << \" requested=\" << {runVar}.requested << \" actual=\" << {runVar}.actual << std::endl; delete[] {seen}; delete[] {team}; }}"
      yield $"{ind}}}" ]

/// THE ROW ORBIT ACTION's C++ (round 3, D-P2 / gap G73; Blade.OrbitFold's
/// action header carries the role-term rule and the order proof, the runtime
/// is blade_orbit_scatter.hpp action2 / tri_action). Called for a `|> compute`
/// map binding before the ordinary apply emitter: Some lines when the action
/// is taken (the binding's own rank-1 pool allocated and filled by one pass
/// over the stored SymIdx<2, n> pool), None otherwise (a recognised action
/// that declines leaves its record). `f` is ONE lambda of (cell value, own,
/// partner) binding each weight's own / partner value from its stored pool;
/// the fold kernel is `acc + z` when it is exactly `p0 + p1`, else the
/// callable's wrapper (a `where repro` kernel keeps its BLADE_REPRO_FN body);
/// the seed is evaluated once (total, coordinate-free). Every statement is one
/// line with BLOCK comments only.
and tryGenOrbitActionCpp (ctx: CodeGenContext) (binding: IRBinding) (info: ApplyInfo) : string list option =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let rec resolveDeferred e =
        match e with
        | IRVar (id, _) ->
            (match Map.tryFind id ctx.DeferredComputations with
             | Some d -> resolveDeferred d
             | None -> e)
        | _ -> e
    let sourceName id = Map.tryFind id ctx.VarNames |> Option.defaultValue $"__v{id}"
    match Blade.OrbitFold.recogniseAction resolveDeferred resolveCallable sourceName info with
    | Blade.OrbitFold.ActionNotAction -> None
    | Blade.OrbitFold.ActionDeclined why ->
        Blade.OrbitFold.recordActionDeclined name why
        None
    | Blade.OrbitFold.ActionTaken p ->
    let s = p.Shape
    let decline (why: string) =
        Blade.OrbitFold.recordActionDeclined name why
        None
    let outOk =
        match IR.stripUnits binding.Type with
        | ArrayElem at when not at.IsVirtual && isFreeableDenseArrayType at ->
            (match at.IndexTypes, Blade.IRTotality.scalarElem at.ElemType with
             | [ ix ], Some et -> ix.Rank = 1 && ix.Symmetry = SymNone && tryEvalIntIR ix.Extent = Some s.Extent && et = s.AccTy
             | _ -> false)
        | _ -> false
    let reqs = Blade.OrbitFold.actionThreadRequests p
    let isDeferred (id: IRId) = ctx.DeferredComputations.ContainsKey id
    let inScope (id: IRId) = ctx.VarNames.ContainsKey id && not (isDeferred id)
    if not outOk then decline "the output is not a dense rank-1 array of the fold's accumulator type over the pool's index"
    elif (Blade.Optimize.HotRowTable.tryProducer binding.Id).IsSome || (Blade.Optimize.CompactRowTable.tryProducer binding.Id).IsSome
         || (Blade.Optimize.EpilogueLegTable.tryProducer binding.Id).IsSome then
        decline "another schedule (a hot-row fold, a cell epilogue, compact row classes or producer-input legs) owns this producer"
    elif not reqs.IsEmpty && ompThreadEmissionEnabled () then
        decline $"""a thread request on {String.concat ", " reqs}: this build emits threads and v1's action is serial -- the row walk keeps the request (each row's chain on one thread)"""
    elif isDeferred s.SourceId then decline "the compact array is a deferred computation (no stored pool; P1-style fusion is v2)"
    elif not (ctx.VarNames.ContainsKey s.SourceId) then decline "the compact array has no materialized pool in scope"
    elif s.Weights |> List.exists (fun w -> not (inScope w.ArrayId)) then
        decline "a weight is not a stored array in scope at the action (a deferred weight would run its initializer inside the action)"
    elif p.FreeIds |> Seq.exists (fun id -> not (inScope id)) then
        decline "a free scalar of the cell term, the fold kernel or the seed is not a materialized binding in scope at the action (capture dominance)"
    else
    match Blade.OrbitFold.orbitActionAdmissible ctx.DeferredComputations binding.Id p with
    | Error why -> decline why
    | Ok matrix ->
    let accCpp = primTypeToCpp s.AccTy
    let elemCpp = primTypeToCpp s.ElemTy
    let n = s.Extent
    let tag = sanitizeCppName name
    let names =
        s.Weights |> List.fold (fun (m: Map<IRId, string>) (w: Blade.OrbitFold.ActionWeight) ->
            m |> Map.add (Blade.OrbitFold.actionOwnId w.Index) $"__aw{w.Index}o_{tag}"
              |> Map.add (Blade.OrbitFold.actionPartnerId w.Index) $"__aw{w.Index}p_{tag}") (Map.add Blade.OrbitFold.readVarId "__sx" ctx.VarNames)
    let weightBinds =
        s.Weights |> List.collect (fun w ->
            let wCpp = primTypeToCpp w.ElemTy
            let arr = ctx.VarNames.[w.ArrayId]
            [ if w.Own then yield $"const {wCpp} __aw{w.Index}o_{tag} = {arr}[__so];"
              if w.Partner then yield $"const {wCpp} __aw{w.Index}p_{tag} = {arr}[__sp];" ])
        |> String.concat " "
    let fLambda =
        let c = { ctx with VarNames = names }
        $"[&]({elemCpp} __sx, [[maybe_unused]] size_t __so, [[maybe_unused]] size_t __sp) -> {accCpp} {{ {weightBinds} return {(exprToCppCtx c s.Body)}; }}"
    let (kCode, kName) =
        if p.LevelAdd then
            ([ $"auto __ak_{tag} = []({accCpp} __a, {accCpp} __b) -> {accCpp} {{ return __a + __b; }};" ], $"__ak_{tag}")
        else genCallableWrapper ctx.VarNames $"{name}__ac" s.Level.Fold
    let seed = exprToCppCtx ctx s.Level.Seed
    let g = p.RowGroup
    let b = p.ColBlock
    let vadd = p.LevelAdd && s.AccTy = ETFloat64 && g % 4 = 0
    let ompNote =
        if reqs.IsEmpty then None
        else Some $"""[omp] requested but emitted serial: {ompThreadsSuppressedReason ()} -- v1's action is serial (requested at: {String.concat ", " reqs})"""
    (orbitScatterUsedCell ()).Value <- true
    Blade.OrbitFold.recordActionTaken name p matrix ompNote
    registerPoolAlloc AllocDense accCpp 1 "nullptr" (name + "_extents") name None
    let i1 = ind + "    "
    Some
        [ yield $"{ind}static constexpr const size_t {name}_extents[1] = {{ {n} }};"
          yield arrayAlloc { Ind = ind; Elem = accCpp; Rank = 1; Name = name; Symm = "nullptr"; Strict = None; Extents = name + "_extents" }
          yield $"{ind}{{"
          yield $"{i1}/* {Blade.OrbitFold.actionForm p} (strict: no licence consumed) */"
          if not reqs.IsEmpty then
              yield $"{i1}/* {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))} */"
          for line in kCode do yield i1 + line
          yield $"{i1}auto __af_{tag} = {fLambda};"
          yield $"{i1}const {elemCpp}* __apool_{tag} = nested_array_utilities::pool_base({ctx.VarNames.[s.SourceId]}.data);"
          let vaddLit = if vadd then "true" else "false"
          yield $"{i1}blade_scatter::action2<{g}, {b}, {vaddLit}>(__apool_{tag}, (size_t){n}, {accCpp}({seed}), nested_array_utilities::pool_base({name}.data), __af_{tag}, {kName});"
          yield $"{ind}}}" ]

and genReduceComputeBindingCore (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (compExpr: IRExpr) (kernelExpr: IRExpr) (seedExpr: IRExpr) : string list * CodeGenContext =
    // THE JOIN ENCODING (IR.fs, IRReduceCompute): tuple kernel + tuple seed =
    // one fold per leg. Routed to its own emitter, because everything below
    // this line assumes ONE fold callable shared by every leaf.
    match kernelExpr, seedExpr with
    | IRTuple kernels, IRTuple seeds when kernels.Length = seeds.Length && kernels.Length >= 2 ->
        genReduceJoinCore ctx binding builder compExpr kernels seeds
    | _ ->
    let ind = indentStr ctx
    let name = bindingCppName binding
    // The fused reduction terminal: reduce(deferred, op[, init]). Fold every
    // cell of a deferred computation -- a single unforced apply, or an <&!>
    // fusion tree of them -- into scalar accumulator(s) through the fold
    // kernel's wrapper, WITHOUT materializing any output array. One loop
    // nest total; a fusion tree gets one accumulator per leaf and packs a
    // flat tuple (mirroring genFusionTree's make_tuple convention, so tuple
    // destructuring projects with flat get<i> indices).
    let rec resolveDeferred e =
        match e with
        | IRVar (id, _) ->
            (match Map.tryFind id ctx.DeferredComputations with
             | Some d -> resolveDeferred d
             | None -> e)
        | _ -> e
    let rec collectLeaves e =
        match resolveDeferred e with
        | IRFusion (l, r) -> collectLeaves l @ collectLeaves r
        | other -> [other]
    // THE ORBIT-CLASS FOLD (Blade.OrbitFold; bench M4): a licensed logical
    // walk over a compact array's cells becomes one pass over its pool with
    // orbit-class accumulators. A recognised walk the fold declines leaves a
    // census line and falls through to the ordinary nest below.
    // `orbitWhy`: the orbit fold's decline reason when this IS a logical walk
    // over a compact array it turned away (BLADE_FP_REPRO=1, BLADE_ORBIT_FOLD=0,
    // ...) -- the G35 row-slot record below names it, because what that form
    // threads is then the logical walk's own rows.
    let orbitPlan, orbitWhy =
        let sourceName id = Map.tryFind id ctx.VarNames |> Option.defaultValue $"__v{id}"
        match Blade.OrbitFold.recognise resolveDeferred resolveCallable sourceName compExpr kernelExpr seedExpr with
        | Blade.OrbitFold.Orbit p ->
            if ctx.DeferredComputations.ContainsKey p.SourceId then
                let why = "the compact array is a deferred computation (no pool to walk)"
                Blade.OrbitFold.recordDeclined name why
                None, Some why
            else
                match Map.tryFind p.SourceId ctx.VarNames with
                | Some gName -> Some (p, gName), None
                | None ->
                    let why = "the compact array has no materialized pool in scope"
                    Blade.OrbitFold.recordDeclined name why
                    None, Some why
        | Blade.OrbitFold.Declined why ->
            Blade.OrbitFold.recordDeclined name why
            None, Some why
        | Blade.OrbitFold.NotOrbit -> None, None
    // THE ORDERED ORBIT SCATTER (Blade.OrbitFold.planStrict): the same walk in
    // a build where every level is strict -- one pass over the pool, every
    // chain fed its source sequence. Planned only where the orbit-class fold
    // DECLINED the walk (its licensed form wins when it applies; BLADE_ORBIT_FOLD=0
    // declines only that form, so the scatter still serves a repro build).
    // Taken below, after the G35 row-slot decision: an admitted scatter wins
    // over an admitted row-slot form (round 3, W2-0a; BLADE_FOLD_ROW_SLOTS=prefer
    // gives the round-2 order, the row slots first): `(plan, pool name)`.
    let scatterPlan =
        match orbitWhy with
        | None -> None
        | Some _ ->
            let sourceName id = Map.tryFind id ctx.VarNames |> Option.defaultValue $"__v{id}"
            match Blade.OrbitFold.recogniseStrict resolveDeferred resolveCallable sourceName compExpr kernelExpr seedExpr with
            | Blade.OrbitFold.StrictNotWalk -> None
            | Blade.OrbitFold.StrictDeclined why ->
                Blade.OrbitFold.recordScatterDeclined name why
                None
            | Blade.OrbitFold.StrictScatter sp ->
                let src = sp.Shape.SourceId
                let undominated =
                    sp.FreeIds |> Seq.tryFind (fun id -> not (ctx.VarNames.ContainsKey id) || ctx.DeferredComputations.ContainsKey id)
                if ctx.DeferredComputations.ContainsKey src then
                    Blade.OrbitFold.recordScatterDeclined name "the compact array is a deferred computation (no pool to walk)"
                    None
                elif not (ctx.VarNames.ContainsKey src) then
                    Blade.OrbitFold.recordScatterDeclined name "the compact array has no materialized pool in scope"
                    None
                elif undominated.IsSome then
                    Blade.OrbitFold.recordScatterDeclined name
                        "a free scalar of the cell function, a fold kernel or a seed is not a materialized binding in scope at the fold (capture dominance)"
                    None
                else Some (sp, ctx.VarNames.[src])
    // THE ROW ORBIT ACTION's walk form (round 3, D-P2): a walk that FOLDS
    // weighted rows over a SymIdx<2, n> pool is recognised and declined in v1
    // (a mixed licence nest first; Blade.OrbitFold.recogniseActionWalk).
    (match scatterPlan, orbitWhy with
     | None, Some _ ->
         let sourceName id = Map.tryFind id ctx.VarNames |> Option.defaultValue $"__v{id}"
         match Blade.OrbitFold.recogniseActionWalk resolveDeferred resolveCallable sourceName compExpr kernelExpr seedExpr with
         | Some why -> Blade.OrbitFold.recordActionDeclined name why
         | None -> ()
     | _ -> ())
    // Compact row classes (Blade.Optimize.planCompactRowClasses): when the
    // producer's nest stored each packed row's class partials (it published
    // their arrays), the orbit pass becomes the row combine. A producer that
    // placed nothing leaves the orbit pass as it was.
    let compactRows =
        match Blade.Optimize.CompactRowTable.tryConsumer binding.Id, orbitPlan with
        | Some plan, Some (p, _) when p.SourceId = plan.ProducerId ->
            (match plan.EmittedCpp.Value with
             | Some parts when parts.Length = p.AccCount -> Some parts
             | _ ->
                 if not plan.DeclinedCpp.Value then
                     recordCodegenDecision "compact-row-classes" "" (Blade.Effects.Declined "the producer's nest did not place the per-row epilogue (a tile, dispatched or non-plain nest form)") [ $"orbit fold of `{plan.ProducerName}`" ]
                 None)
        | _ -> None
    match orbitPlan, compactRows with
    | Some (p, _), Some parts ->
        (genCompactRowCombineCpp ctx ind name p parts, addVarName binding.Id name ctx)
    | _ ->
    match orbitPlan with
    | Some (p, gName) ->
        Blade.OrbitFold.recordTaken name p
        (genOrbitFoldCpp ctx ind name gName p, addVarName binding.Id name ctx)
    | None ->
    let leaves = collectLeaves compExpr
    let infos = leaves |> List.choose (function IRApplyCombinator i -> Some i | _ -> None)
    let ctx' = addVarName binding.Id name ctx
    if infos.IsEmpty || infos.Length <> leaves.Length then
        (codegenError ctx ind "reduce over a deferred computation requires unforced method_for/object_for applications at every leaf (typechecker or IR bug if not)", ctx')
    else
        // The fold bypasses genApplyCombinator's special input paths
        // (ragged peel, grouped, compound, CUDA) -- reject what they handle.
        //
        // EXCEPT a closed-form enumerable domain (`range<R>` over a static
        // struct whose conjuncts are linear, docs/plans/structural/06): its
        // level renders as an affine nest that needs no driver index at all
        // (genDomainLoopHeader), so the fold streams the solutions with no
        // array and no key table. A TABLE-enumerated domain (class B) and a
        // SparseIdx range still need the `_cidx` key index this path never
        // builds; they keep the refusal, worded for what they are.
        let operandPairs (info: ApplyInfo) =
            info.ArrayTypes |> List.mapi (fun i at -> (List.tryItem i info.Arrays, at))
        let isClosedFormDomainOperand (arr: IRExpr option) =
            match arr with
            | Some (IRRange ([ ix ], _)) ->
                (match ix.Extent with IRSparseKeys (SkDomain _) -> true | _ -> false)
            | _ -> false
        let unsupportedInput =
            infos |> List.exists (fun info ->
                operandPairs info |> List.exists (fun (arr, at) ->
                    at.IndexTypes |> List.exists (fun ix ->
                        isRaggedFamilyKind ix.IxKind || ix.IxKind = IxKDepInner
                        || ix.IxKind = IxKGroupOuter || ix.IxKind = IxKCompound
                        // a sparse range's driver index is declared by
                        // genApplyCombinator only; this path never builds one
                        || (ix.IxKind = IxKSparse && not (isClosedFormDomainOperand arr)))))
        let sparseInput =
            infos |> List.exists (fun info ->
                operandPairs info |> List.exists (fun (arr, at) ->
                    not (isClosedFormDomainOperand arr)
                    && at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKSparse)))
        // Census for `blade plan`: which enumeration each domain leaf took.
        for info in infos do
            for (arr, _) in operandPairs info do
                match arr with
                | Some (IRRange ([ ix ], _)) ->
                    (match ix.Extent with
                     | IRSparseKeys (SkDomain plan) ->
                         // Which order the fold walks -- the arm below reads the
                         // SAME decision (domainFoldOrder), as does the LLVM lane.
                         let order =
                             if infos.Length <> 1 then DomainLexOrder "a fused join of several folds shares one lexicographic nest"
                             else
                                 domainFoldOrder (resolveCallable kernelExpr)
                                     (resolveCallable info.Kernel |> Option.map _.Body) plan
                         recordDomainEnum name plan.Name true
                             $"closed-form affine nest ({plan.Levels.Length} level(s): {domainPlanSummary plan}), {plan.Card} cell(s)"
                             $"consumer: streamed fold (no array, no key table); {(domainFoldOrderText plan order)}"
                     | IRSparseKeys (SkStatic entries) ->
                         recordDomainEnum name "(table)" false
                             $"table-enumerated domain or SparseIdx range ({entries.Length} baked key(s)): its conjuncts are not linear inequalities, so there is no closed-form nest to stream"
                             "consumer: fold (refused -- force the map with |> compute)"
                     | _ -> ())
                | _ -> ()
        if unsupportedInput && sparseInput then
            (codegenError ctx ind "reduce over a deferred computation is not streamed over a table-enumerated domain or a SparseIdx range: only a static struct whose conjuncts are all LINEAR inequalities on its fields (`i - j <= w`, `abs(i - j) <= w`, `i < j`, `i + j <= n`, `m1 + m2 == m_out`) enumerates in closed form -- bind the map with |> compute and reduce the array", ctx')
        elif unsupportedInput then
            (codegenError ctx ind "reduce over a deferred computation is not supported for ragged/grouped/compound inputs yet -- bind the map with |> compute and reduce the array", ctx')
        else
            match resolveCallable kernelExpr with
            | Some callable when callable.Params.Length = 2 ->
                let (wrapperCode, wname) = genCallableWrapper ctx.VarNames name callable
                let wrapperLines = wrapperCode |> List.map (fun s -> ind + s)
                // Accumulator C++ type: the fold callable's return type (the
                // checker unified it with every leaf's element type).
                let elemStr =
                    match callable.RetType with
                    | IRTScalar et -> primTypeToCpp et
                    | t -> irTypeToCpp t
                let seedStr = exprToCppCtx ctx seedExpr
                let arrayNamesOf (info: ApplyInfo) =
                    info.Arrays |> List.mapi (fun i arr ->
                        match arr with
                        | IRVar (id, _) -> Map.tryFind id ctx.VarNames |> Option.defaultValue ($"arr{i}")
                        | IRRange _ -> $"__range{i}"
                        | IRVirtualReverse _ -> $"__rev{i}"
                        | _ -> $"arr{i}")
                let foldCg (info: ApplyInfo) (accName: string) =
                    // S2 routing, same rule as the single-kernel site.
                    let cg = routeKernelBodyThroughCall info (buildLoopNestCodeGen info (arrayNamesOf info) accName builder)
                    // G41: a WRITING fold kernel is never reorder-licensed
                    // (foldReorderLicensed), so no Path B plan is made; the
                    // nest's marker names that, not the generic reason.
                    let writes =
                        if callableMayWrite callable then
                            Some "the fold kernel writes a captured binding; a map licence does not make the fold race-safe (its applications are ordered side effects)"
                        else None
                    { cg with OutputType = callable.RetType; FoldWrapper = Some wname; FoldOmpDecline = writes }
                match infos with
                | [single] ->
                    let cg0 = foldCg single name
                    // Path B: a comm-licensed fold kernel that asked for omp gets
                    // the outermost level chunked across an explicit team. This
                    // covers exactly the single-leaf case; the fused tree below
                    // (one accumulator per leaf) stays serial.
                    //
                    // The outermost binding must be RECTANGULAR -- no bound
                    // dependencies, no strict offset -- so [0, extent) really is
                    // the chunked range. Inner levels may be anything, including
                    // triangular: each thread runs the full inner nest for the
                    // outer indices it owns, exactly as the serial nest would.
                    //
                    // `ompThreadEmissionEnabled` is DELIBERATELY NOT consulted
                    // here. The knob suppresses this plan at its CONSUMER
                    // (`genLoopNestStreamed`, which is where the team would be
                    // opened), not at this producer, because `FoldChunk.IsSome`
                    // is also read as a FACT by the dispatch patterns
                    // (`LinAlgPatterns` L1/L2/L3 decline on it) and by the flat
                    // elementwise fast path. Clearing it here would make a
                    // serial-emission build take DIFFERENT ROUTES, not just emit
                    // different text -- e.g. handing an omp-licensed fold to a
                    // BLAS dispatch it declines today. The knob's invariant is
                    // that route selection is identical in both modes.
                    // The licence may sit on EITHER kernel (bench gap G27):
                    //   * the FOLD kernel (`lambda(u, v) where omp -> u + v`), or
                    //   * the MAP kernel, when its `omp(...)` covers the nest's
                    //     OUTERMOST level (`method_for(range<I>) <@> lambda(i)
                    //     where omp(i: 1) -> ..., (+)`). That level is exactly
                    //     the one Path B chunks, the map's licence says its
                    //     iterations are independent, and the fold's own reorder
                    //     licence (a builtin `+`/`*` body, or declared comm) is
                    //     what makes the per-thread partials sound. Before, this
                    //     placement was accepted and DROPPED with no trace: the
                    //     one-level arm below took the simd form and never
                    //     reached the nest emitter whose marker would have said so.
                    // A map licence that covers only an INNER level cannot be
                    // chunked here; the nest emitter's marker reports it.
                    let mapLicensesOuter =
                        cg0.OmpRequested
                        && (match cg0.Bindings with
                            | head :: _ -> head.IsParallel
                            | [] -> false)
                    // G41, the mirror case: a `where omp` (or builtin) FOLD
                    // licenses the fold's reassociation, not the MAP body's
                    // concurrency. Path B runs the map body on a team, and the
                    // simd arm asserts `omp simd` over it with read-only
                    // restrict operands; a body that WRITES (legal: the BL4005
                    // ban covers a parallel body, and this map need carry no
                    // omp) would race or vectorize across its own stores. Such
                    // a fold keeps the serial chain: every reordered form below
                    // reads `licensedFold`, never `foldReorderLicensed` alone.
                    let mapBodyWrites = exprMayWriteIn ctx.DeferredComputations cg0.KernelExpr
                    let licensedFold = foldReorderLicensed callable && not mapBodyWrites
                    // G51 (Codex diff review of W1-A2, finding 1): a team over
                    // ROW-VIEW folds in the LICENSED build moves every row's fold
                    // onto a worker and the outer kernel's applications into
                    // per-thread chains, so the OUTER kernel must pass the same
                    // closed totality whitelist the repro row slots apply to it
                    // (its parameters bound as plain values): a kernel that may
                    // panic would report a different failure, or none (two
                    // chunks can cancel before a cast the serial chain applies).
                    // A licence is not a totality certificate. Such a fold gets
                    // NO team -- neither the row-view helper arm nor the general
                    // Path B chunk form (whose own missing outer-kernel audit is
                    // an inherited weakness, recorded in the gap register's G51
                    // row) -- and the census line says why. The repro build is
                    // unaffected: its row-slot admission already audits the
                    // outer kernel and declines with the walk's reason.
                    let rowViewOuterRefusal =
                        if not (fpReassocEnabled ()) || not (callable.IsOmpParallel || mapLicensesOuter) then None
                        else
                        match resolveCallable single.Kernel with
                        | Some rowCl when rowCl.Params.Length = 1 && (rowViewFoldOf rowCl.Body).IsSome ->
                            let foldBound = callable.Params |> List.map (fun q -> q.VarId) |> Set.ofList
                            let foldNames = callable.Params |> List.map (fun q -> q.Name) |> Set.ofList
                            (match rowSlotWalk ctx.DeferredComputations Map.empty Map.empty foldBound foldNames (Set.singleton callable.Id) callable.Body with
                             | Ok _ -> None
                             | Error why -> Some why)
                        | _ -> None
                    let rowViewOuterCensus =
                        rowViewOuterRefusal |> Option.map (fun why ->
                            $"the outer fold kernel is outside the closed totality whitelist ({why}): a team over row-view folds -- the licensed helper arm or a Path B chunk -- would change which failure arrives first (G51)")
                    (match rowViewOuterRefusal with
                     | Some why when licensedFold ->
                         recordCodegenDecision "fold-form" name
                             (Blade.Effects.Declined $"row-view helper arm: the outer fold kernel is outside the closed totality whitelist ({why})")
                             [ "fold form: no team (the licensed row-view helper arm and the general Path B chunk form both declined; the serial licensed form runs)"
                               "reduce over a deferred computation (G51 licensed row-view arm: the outer kernel's totality audit)" ]
                     | _ -> ())
                    let chunkable =
                        (callable.IsOmpParallel || mapLicensesOuter)
                        && licensedFold
                        && rowViewOuterRefusal.IsNone
                        && (match cg0.Bindings with
                            // a closed-form domain head is a whole affine nest
                            // over its fields, not a [0, extent) sweep to split
                            | head :: _ -> head.BoundDependencies.IsEmpty && head.StrictOffset = 0
                                           && (domainPlanOfLevel head).IsNone
                            | [] -> false)
                    // THE ROW-OWNED PULL SCATTER (round 3, D-P3; Blade.OrbitFold's
                    // pull header): a rank-3 walk the strict scatter admitted,
                    // at EXACTLY the G35 request / depth gate (`chunkable`: an
                    // outer fold `where omp` or a map `omp` covering the
                    // outermost level, a licensed outer fold, a rectangular
                    // outer level) in a threaded strict build, runs its leaf
                    // chains on the team, row-owned -- ahead of the threaded
                    // complete-row form and the serial scatter (whose relative
                    // precedence below is untouched by it). Its admission
                    // (orbitPullAdmission) audits all five parties; a decline
                    // is recorded and the forms below decide as before.
                    let pullTaken =
                        match scatterPlan with
                        | Some (sp, gName) when sp.Shape.Rank = 3 && chunkable && not (fpReassocEnabled ()) && ompThreadEmissionEnabled () ->
                            (match orbitPullAdmission ctx.DeferredComputations binding.Id sp compExpr with
                             | Ok matrix -> Some (sp, gName, matrix)
                             | Error why ->
                                 Blade.OrbitFold.recordPullDeclined name why
                                 None)
                        | _ -> None
                    match pullTaken with
                    | Some (sp, gName, matrix) ->
                        // The outer level's request (its fold kernel's, or the
                        // map's covering level 1) is the one spent; any other
                        // level's reaches the census -- no licence moves
                        // between levels.
                        let unspent =
                            Blade.OrbitFold.scatterThreadRequests sp
                            |> List.filter (fun r -> r <> "level 1's fold" && r <> "level 1's map")
                        let ompNote =
                            if unspent.IsEmpty then None
                            else
                                let build = if fpReproEnabled () then fpReproReason () else "the walk's folds carry no reorder licence"
                                Some $"""{build}; the row-owned pull threads the leaf chains under the OUTER level's request only -- an inner level's request licenses its own chain, which stays serial and unsplit (requested at: {String.concat ", " unspent})"""
                        (genOrbitPullCpp ctx ind name gName sp orbitWhy ompNote matrix, ctx')
                    | None ->
                    // M14 (gap G25): how the fold runs INSIDE each thread's
                    // chunk. Decided here, where the kernel is known, and read
                    // by the nest emitter AND the `blade plan` census so the
                    // two cannot disagree. A one-level rectangular sweep under
                    // a builtin `+`/`*` takes the simd reduction per chunk (the
                    // serial licensed form's own shape, per thread); the
                    // dot-class one-level case is taken over entirely by the
                    // threaded vector arm of `laneLines` below and never
                    // reaches the nest. Everything else keeps the serial chain
                    // it had, with the reason on record.
                    // G35: the REPRO build's one chunk form -- threaded
                    // complete rows (ChunkRowSlots). Considered only where a
                    // chunk plan exists and every other form is dead at its
                    // consumer (fpReassocEnabled false); a decline is
                    // recorded with the first failed condition. The plan the
                    // producer builds still reads only source facts, except
                    // that this one form also asks whether the build opens
                    // teams at all: a slot array with no team is pure cost.
                    let mutable rowSlotWhy = None
                    // W2-0a: the row-slot form was ADMITTED but yielded to the
                    // ordered orbit scatter (the census says so).
                    let mutable rowSlotsYielded = false
                    // G51: the row-view record lines of a taken row-slot form
                    // whose rows are row-view folds (None otherwise).
                    let mutable rowViewLines : string list option = None
                    let baseNameOf (id: IRId) = Map.tryFind id ctx.VarNames |> Option.defaultValue $"#{id}"
                    let rowSlots =
                        if not chunkable || fpReassocEnabled () then None
                        else
                            match rowSlotAdmission ctx.DeferredComputations baseNameOf single callable seedExpr cg0.Bindings with
                            // THE PRECEDENCE (round 3, W2-0a): both forms
                            // admit the walk -- the scatter is taken (measured
                            // faster under mt8 repro; Blade.OrbitFold's
                            // rowSlotsYieldedToScatter has the numbers) unless
                            // BLADE_FOLD_ROW_SLOTS=prefer. Both are bitwise the
                            // serial walk under REPRO + CONTRACT=off, so this is
                            // a speed choice only; no admission is added (the
                            // scatter's is planStrict's, already passed above).
                            | Ok _ when scatterPlan.IsSome && not (foldRowSlotsPreferred ()) ->
                                rowSlotsYielded <- true
                                recordCodegenDecision "fold-form" name
                                    (Blade.Effects.Declined $"row-slot form: {Blade.OrbitFold.rowSlotsYieldedToScatter}")
                                    [ Blade.OrbitFold.rowSlotsYieldedForm
                                      "reduce over a deferred computation (threaded complete rows, G35)"
                                      "both forms admitted this walk: the row-slot admission's five-party audit passed (shared transitive write analysis + the IRTotality closed whitelist: row callable, inner and outer fold kernels, both seeds -- no write; intervening = none), and the scatter's own admission (OrbitFold.planStrict: a stored SymIdx pool, every level strict, f / every kernel / every seed on the closed total whitelist, coordinate-free seeds) passed; the scatter moves no evaluation across a binding and keeps every chain's seed, operand order and kernel" ]
                                None
                            | Ok (rows, cells, rv) ->
                                rowViewLines <- rv
                                Some (rows, cells)
                            | Error why ->
                                rowSlotWhy <- Some why
                                let form =
                                    if scatterPlan.IsSome then Blade.OrbitFold.rowSlotsScatterFormPrefix + "declined)"
                                    else "fold form: serial (repro; the threaded complete-row form declined)"
                                recordCodegenDecision "fold-form" name (Blade.Effects.Declined $"row-slot form: {why}")
                                    [ form
                                      "reduce over a deferred computation (threaded complete rows, G35)" ]
                                None
                    // An admitted scatter yields to a TAKEN row-slot form only
                    // under BLADE_FOLD_ROW_SLOTS=prefer (the round-2 order):
                    // by default the row slots are taken only where the
                    // scatter did not admit the walk (scatterPlan None), so
                    // there is nothing to yield. Everywhere else an admitted
                    // scatter is the strict cell's form (and the serial LLVM
                    // lane's, which has no threads).
                    let scatterTaken =
                        match scatterPlan with
                        | Some (sp, _) when rowSlots.IsSome ->
                            Blade.OrbitFold.recordScatterYieldedToRows name sp
                            None
                        | other -> other
                    let chunkForm =
                        match cg0.Bindings with
                        | _ when rowSlots.IsSome -> ChunkRowSlots rowSlots.Value
                        | _ when foldLanesMode () = FoldLanesOff ->
                            ChunkSerial "BLADE_FOLD_LANES=0/off (A/B knob)"
                        | _ when ompTestModeEnabled () ->
                            ChunkSerial "omp test mode: the coverage instrumentation owns the chunk's loop"
                        | [ lvl ] when not (laneableFoldLevel cg0 lvl) ->
                            ChunkSerial "the chunked level is not a plain rectangular sweep"
                        | [ _ ] ->
                            (match fpReassocSimdOp callable elemStr with
                             | Some op -> ChunkSimd op
                             | None -> ChunkSerial "the fold kernel's combine is a call (comm-declared kernel), or its element type has no simd reduction")
                        | _ -> ChunkSerial "multi-level nest: each chunk runs the inner nest per outer index"
                    let cg =
                        if chunkable then
                            { cg0 with FoldChunk = Some { ElemCpp = elemStr; Tag = sanitizeCppName name; ChunkForm = chunkForm } }
                        else cg0
                    // L1 LinAlg dispatch. `reduce(<unforced zip under *>, (+))`
                    // over two rank-1 f64 pools IS a dot product; the pattern owns
                    // the recognition, this site only spells the call. With the
                    // BLAS gate off (the default) `shimEntryPoint` yields None and
                    // the fold nest below is emitted unchanged.
                    //
                    // PRECEDENCE, enforced by the fact passed in: an `omp`-licensed
                    // fold kernel keeps the Path B chunked parallel fold. The
                    // pattern declines on `FoldRequestedOmp`, so a licensed fold
                    // can never be silently serialized by this route.
                    //
                    // Stands down in omp test mode for the same reason the flat
                    // path does -- the coverage instrumentation reports on the nest
                    // that ran, and a dispatched call has no nest.
                    let dotCall =
                        if ompTestModeEnabled () then None
                        else
                            let facts : Blade.LinAlgPatterns.DotFoldFacts =
                                { FoldIsBuiltinAdd = (foldKernelBuiltinOp callable = Some IRAdd)
                                  FoldRequestedOmp = callable.IsOmpParallel || mapLicensesOuter }
                            match (0, facts, single.ArrayTypes, cg) with
                            | Blade.LinAlgPatterns.BlasL1 call ->
                                let entry = Blade.LinAlgPatterns.shimEntryPoint Blade.LinAlgPatterns.HostBlas call
                                Blade.LinAlgPatterns.recordRoute (decisionSpan ()) name call
                                    (entry |> Option.map (fun e -> (Blade.LinAlgPatterns.HostBlas, e)))
                                entry |> Option.map (fun entry -> (call, entry))
                            | _ -> None
                    // BLADE_FP_REASSOC for the reduce-over-DEFERRED-COMPUTATION
                    // nest -- the shape `reduce(<unforced zip>, (+))` (the dot
                    // benchmark) lowers through, and the last serial arm the knob
                    // did not reach. Same K-lane shape as every other
                    // `fpReassocEnabled ()` site (fpReassocLaneStmts owns it), so
                    // the numeric answer cannot drift between them.
                    //
                    // What is different here is the ELEMENT: the flat forms fold
                    // `A[__ri + l]`, a subscript; this one folds the nest's whole
                    // per-iteration BODY at `__ri + l`. That is only meaningful
                    // when the body is a PURE EXPRESSION of the level's index --
                    // which is exactly what the gate below establishes, and what
                    // makes K independent evaluations of it legitimate. The body
                    // is rendered ONCE into a local `[&](size_t __i0) -> T`
                    // lambda and the lanes call it at their own indices; the K
                    // copies are the inliner's, not the emitter's. Rendering once
                    // is not only smaller text: the kernel emitters are not all
                    // pure (some register collected definitions), so emitting the
                    // same body twice is a hazard the single render removes.
                    //
                    // DECLINED, deliberately, in v1 (each falls through to the
                    // unchanged serial nest below):
                    //   * MULTI-LEVEL nests. A lane would have to stride the
                    //     outermost level while re-running the whole inner nest,
                    //     which is a different (and much larger) unit of work
                    //     than a body expression -- and the inner nest may be
                    //     triangular, so lane l and lane l+1 do not even do equal
                    //     work. The comm-licensed multi-level case already has
                    //     its own answer (FoldChunk, Path B).
                    //   * Non-rectangular / fused / tabulated (compound, sparse)
                    //     head levels: `__ri + l` is not an element of the
                    //     iteration space.
                    //   * Reynolds bodies, MPI slabs, and halo-window slots.
                    //   * `FoldChunk` (an `omp`-licensed fold): threads and lanes
                    //     are separate opt-ins and Path B already owns that arm.
                    //   * The FUSED TREE (several leaves, several accumulators) --
                    //     handled in the branch below, which stays serial.
                    //
                    // Licence is the ordinary one: `foldReorderLicensed callable`.
                    // On this path `(+)` resolves to a two-parameter callable whose
                    // body is exactly `p0 + p1`, so `foldKernelBuiltinOp` grants it
                    // outright (the same fact `dotCall` reads as
                    // `FoldIsBuiltinAdd`); an unlicensed user fold kernel stays
                    // serial with the knob on.
                    // A FUNCTION, not a value: it renders the kernel body, and the
                    // BLAS-dispatch arm below emits no body at all -- computing it
                    // eagerly would render (and discard) one there.
                    let laneLines () : string list option =
                        let laneable (lvl: LoopIndexBinding) = laneableFoldLevel cg lvl
                        // The dot-class recogniser (blade_fold_lanes.hpp's forms):
                        // a left-nested product of 1..3 rank-1 real-array peels
                        // of the accumulator's element type. Shared by the
                        // threaded arm (M14) and the serial arm (M9) below.
                        let dotClass (lvl: LoopIndexBinding) =
                            // double / float only (`foldVecElem`): an integer fold
                            // keeps the simd reduction -- the header has no
                            // integer vector form, and GCC's own reduction is
                            // exact there anyway.
                            if foldLanesMode () = FoldLanesOff || not (foldVecElem elemStr)
                               || fpReassocSimdOp callable elemStr <> Some "+" then None
                            else
                                dotClassFactors (fun t -> irTypeToCpp t = elemStr) cg.KernelExpr cg.KernelParams lvl
                                |> Option.bind foldVecKind
                        // G51, the licensed row-view arm's recogniser: the ONE
                        // shape `reduce(u(i), <builtin +>[, s])` as the whole
                        // row kernel body, `u` a stored (non-deferred, non-
                        // virtual) dense rank-2 array of the accumulator's
                        // element type (double / float), `i` the kernel's one
                        // parameter, bound to a plain `range<Y>` whose index is
                        // EXACTLY u's first (index identity: the row read is
                        // proven), the folded axis u's second, of static extent
                        // >= 1; the INNER kernel reorder-licensed on its own
                        // (`foldReorderLicensed`: no `where repro`, no write)
                        // with a builtin `+` body; the body (seed included)
                        // passes the closed totality whitelist the row-slot
                        // form applies; no real array rides the level.
                        let rowViewSum (lvl: LoopIndexBinding) : RowViewSumPlan option =
                            let plain (ix: IRIndexType) = ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
                            if foldLanesMode () = FoldLanesOff || not (foldVecElem elemStr)
                               || lvl.Elements |> List.exists (_.Virtual.IsRealArray)
                               // the seed party: the outer seed is evaluated
                               // once before the team either way; a writing
                               // one keeps the previous form
                               || exprMayWriteIn ctx.DeferredComputations seedExpr
                               // the outer-kernel party's totality (no chunk
                               // plan exists then; restated for the arm itself)
                               || rowViewOuterRefusal.IsSome then None
                            else
                            match single.Arrays |> List.map rowSlotRangeIx, cg.KernelParams, cg.KernelExpr with
                            | [ Some outerIx ], [ p ], IRReduce (IRIndex ((IRVar (u, uty) as uE), [ ixE ], _), k, seed)
                                when not (Map.containsKey u ctx.DeferredComputations)
                                     && (match ixE with
                                         | IRVar (id, _) -> id = p.VarId
                                         | IRParam (nm, _, _) -> nm = p.Name
                                         | _ -> false) ->
                                (match resolveCallable k, uty with
                                 | Some innerCl, ArrayElem at
                                     when innerCl.Params.Length = 2 && foldReorderLicensed innerCl
                                          && fpReassocSimdOp innerCl elemStr = Some "+"
                                          && not at.IsVirtual && irTypeToCpp at.ElemType = elemStr ->
                                     (match at.IndexTypes with
                                      | [ rowIx; colIx ] when plain rowIx && plain colIx && Blade.IRTotality.sameIndex rowIx outerIx ->
                                          (match tryEvalIntIR colIx.Extent with
                                           | Some n when n >= 1L ->
                                               let levels = Map.ofList [ p.VarId, outerIx ]
                                               let names = Map.ofList [ p.Name, outerIx ]
                                               (match rowSlotWalk ctx.DeferredComputations levels names Set.empty Set.empty Set.empty cg.KernelExpr with
                                                | Ok _ -> Some { Base = uE; Subscript = ixE; Seed = seed; Cells = n }
                                                | Error _ -> None)
                                           | _ -> None)
                                      | _ -> None)
                                 | _ -> None)
                            | _ -> None
                        match cg.Bindings with
                        // M14 / gap G25: a THREADED dot-class fold. The Path B
                        // team is opened here, and each thread's chunk is ONE
                        // call of the explicit K-lane vector form over its
                        // [__rlo, __rhi) -- the same tree the serial build
                        // takes over the whole sweep, so a chunk's bits are a
                        // function of its data and length alone. Partition by
                        // blade_fold::chunk_lo (the one function every Path B
                        // site spells), partials combined in job order through
                        // the fold's wrapper, the caller's seed first: the
                        // threaded value is deterministic for a fixed team. The
                        // nest emitter is not involved (no loop is emitted), so
                        // omp test mode -- whose coverage instrumentation needs
                        // the nest -- stands down to the nest arm.
                        | [lvl] when fpReassocEnabled () && licensedFold
                                     && cg.FoldChunk.IsSome && ompThreadEmissionEnabled ()
                                     && not (ompTestModeEnabled ())
                                     && laneable lvl
                                     && (dotClass lvl).IsSome ->
                            let (kind, ops) = (dotClass lvl).Value
                            let k = foldVecLanes kind
                            let label = foldVecLabel kind k
                            let streamsWord = if foldVecStreams kind = 1 then "1 operand stream" else $"{(foldVecStreams kind)} operand streams"
                            let boundStr = genLoopBoundExpr (compoundArrayNamesOf cg.Bindings) lvl
                            let aliasNames = ops |> List.mapi (fun i _ -> $"__rsrc{i}")
                            let aliasDecls =
                                List.map2 (fun alias nm -> $"const {elemStr}* BLADE_RESTRICT {alias} = {nm}.data;") aliasNames ops
                            let call = foldVecCall elemStr k kind (aliasNames |> List.map (fun a -> $"{a} + __rlo")) "__rhi - __rlo"
                            (ompApiUsedCell ()).Value <- true
                            (foldLanesUsedCell ()).Value <- true
                            recordOmpConstruct name "omp parallel num_threads(T) + fixed-order combine"
                                $"comm-licensed parallel fold: outer level chunked per thread (Path B), {label} per chunk"
                            recordFoldForm name $"threaded(Path B chunks, {label} per chunk)" "reduce over a deferred computation (one level, Path B)"
                            Some (
                                // BLOCK comment: a kernel-body fold's statements
                                // are space-joined into a one-line IIFE.
                                [ $"{ind}/* reduce over computation: comm-licensed parallel fold, outer level chunked per thread (Path B); {label} per chunk ({streamsWord}) */"
                                  $"{ind}{{"
                                  $"{ind}    const size_t __rn = {boundStr};"
                                  $"{ind}    int __rT = blade_omp_get_max_threads();"
                                  $"{ind}    if ((size_t)__rT > __rn) __rT = (int)__rn;"
                                  $"{ind}    if (__rT < 1) __rT = 1;"
                                  $$"""{{ind}}    if (__rn > 0) {""" ]
                                @ (aliasDecls |> List.map (fun d -> $"{ind}        {d}"))
                                @ [ $"{ind}        {elemStr}* __rpart = new {elemStr}[__rT];"
                                    $"{ind}        bool* __rhad = new bool[__rT]();"
                                    $"{ind}        BLADE_OMP_PARALLEL_NUM_THREADS(__rT)"
                                    $$"""{{ind}}        {"""
                                    $"{ind}            const int __rnt = blade_omp_get_num_threads();"
                                    $"{ind}            const int __rt = blade_omp_get_thread_num();"
                                    $"{ind}            const size_t __rlo = blade_fold::chunk_lo(__rn, (size_t)__rt, (size_t)__rnt);"
                                    $"{ind}            const size_t __rhi = blade_fold::chunk_lo(__rn, (size_t)__rt + 1, (size_t)__rnt);"
                                    $$"""{{ind}}            if (__rhi > __rlo) { __rpart[__rt] = {{call}}; __rhad[__rt] = true; }"""
                                    $$"""{{ind}}        }"""
                                    $"{ind}        for (int __rt = 0; __rt < __rT; __rt++) if (__rhad[__rt]) {name} = {wname}({name}, __rpart[__rt]);"
                                    $"{ind}        delete[] __rpart; delete[] __rhad;"
                                    $$"""{{ind}}    }"""
                                    $"{ind}}}" ])
                        // G51, THE LICENSED ROW-VIEW ARM (bench stencil's input
                        // checksum, `reduce(method_for(range<Y>) <@> lambda(i)
                        // -> reduce(u(i), (+)[, s]), <omp fold>, s0)`): the
                        // same Path B team and partition, but each row's
                        // complete fold is ONE `blade_fold::sum<T, K>` call over
                        // the row's contiguous cells -- not the expression
                        // form's 8-scalar-lane IIFE re-spelling the row view per
                        // element -- and the row loop carries NO `omp simd`
                        // (its body is a complete fold: each thread's partial
                        // is the ascending chain of its rows, seeded from its
                        // first row's value, combined in job order from the
                        // caller's seed). Licensed ONLY (rowViewSum: the INNER
                        // fold's own reorder licence, builtin `+`, never under
                        // `where repro` or BLADE_FP_REPRO=1, double / float,
                        // BLADE_FOLD_LANES not off) -- NOT identity-preserving:
                        // the helper reassociates each row. THE SEED IS
                        // COMBINED EXACTLY ONCE (Codex review A, finding 4):
                        // `reduce(u(i), +, s)` is `s + sum(row)`, s evaluated
                        // once per row in the row's scope; the unseeded fold
                        // keeps its head peel (`row[0] + sum(row[1..n))`, n = 1
                        // is `row[0]` itself). SIGNED ZERO is the licensed
                        // contract's: the tree's chains start at +0, so an
                        // all-(-0) row that reaches the helper (a seeded row,
                        // or an unseeded one of two or more cells; an unseeded
                        // one-cell row is its own cell) folds to +0 -- what the
                        // LLVM lane's licensed fold and the default build's
                        // previous chunk form (whose `omp simd` private copies
                        // start at +0) give, so the two compiled lanes print the
                        // same zero (`blade test llvm`); BLADE_FP_REPRO=1 keeps
                        // the -0 (and BLADE_FOLD_LANES=0's serial chunks can).
                        // THE OUTER KERNEL'S TOTALITY is audited before any team
                        // is planned (`rowViewOuterRefusal`; no chunk plan, so
                        // this arm is unreachable for a kernel that may panic).
                        | [lvl] when fpReassocEnabled () && licensedFold
                                     && cg.FoldChunk.IsSome && ompThreadEmissionEnabled ()
                                     && not (ompTestModeEnabled ())
                                     && laneable lvl
                                     && (rowViewSum lvl).IsSome ->
                            let rv = (rowViewSum lvl).Value
                            let k = foldVecLanes VecSum
                            let boundStr = genLoopBoundExpr (compoundArrayNamesOf cg.Bindings) lvl
                            // The level's own element peels (the range slot's
                            // coordinate), declared in the row loop exactly as
                            // the nest declares them.
                            let mutable paramFinalNames : Map<IRId, string> = Map.empty
                            let mutable peels : string list = []
                            for elem in lvl.Elements do
                                let (peelCode, newName) = genElementBindingPeel false lvl elem elem.ArrayName
                                if not (List.contains peelCode peels) then peels <- peels @ [ peelCode ]
                                match elem.Virtual with
                                | VirtualRange _ | VirtualReverse ->
                                    paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
                                | RealArray ->
                                    paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
                            let nameMap =
                                paramFinalNames |> Map.fold (fun acc key v -> Map.add key v acc) ctx.VarNames
                                |> withCaptureNames EnclosingNamesWin cg.Captures
                            let uStr = exprToCpp nameMap rv.Base
                            let ixStr = exprToCpp nameMap rv.Subscript
                            let sumOf (p: string) (cnt: int64) =
                                foldVecCall elemStr k VecSum [ p ] ("(size_t)" + string cnt)
                            let rowValue =
                                match rv.Seed with
                                | Some s ->
                                    let seedCpp = exprToCpp nameMap s
                                    let whole = sumOf "__rrow" rv.Cells
                                    $"({seedCpp}) + {whole}"
                                | None when rv.Cells = 1L -> "__rrow[0]"
                                | None ->
                                    let tail = sumOf "(__rrow + 1)" (rv.Cells - 1L)
                                    $"__rrow[0] + {tail}"
                            let seedWord =
                                match rv.Seed with
                                | Some _ -> "its seed combined once (seed + sum(row))"
                                | None when rv.Cells = 1L -> "unseeded single-cell rows: the row's one cell"
                                | None -> "unseeded: head-peeled, row[0] + sum(row[1..n))"
                            (ompApiUsedCell ()).Value <- true
                            (foldLanesUsedCell ()).Value <- true
                            // An unseeded one-cell row is its cell: no helper call.
                            let noHelper = rv.Seed.IsNone && rv.Cells = 1L
                            let label = if noHelper then "one-cell rows, no helper call" else $"blade_fold::sum<{elemStr}, {k}> per row"
                            let treeWord = if noHelper then "its one cell, no lane tree" else "one vector lane tree"
                            let rowPhrase =
                                if noHelper then "each row's fold is its one cell"
                                else $"each row's fold one blade_fold::sum<{elemStr}, {k}> call over its contiguous cells"
                            recordOmpConstruct name "omp parallel num_threads(T) + fixed-order combine"
                                $"comm-licensed parallel fold: outer level chunked per thread (Path B), licensed row-view folds: {label}, no omp simd on the row loop"
                            recordCodegenDecision "fold-form" name Blade.Effects.Applied
                                [ $"fold form: threaded(Path B chunks, {label}: licensed row-view folds, {seedWord}; no omp simd on the row loop)"
                                  "reduce over a deferred computation (one level, Path B partition)"
                                  $"row-view folds (G51, licensed arm, BLADE_FOLD_LANES): each row's reduce over the ROW VIEW {uStr}(i) of a stored dense rank-2 array ({rv.Cells} contiguous cells) is {treeWord} (K = {k}; BLADE_FOLD_LANES=4|8 sets K, 0/off keeps the previous chunk form: simd-reduction over rows of the lane IIFE); the inner fold's OWN reorder licence (builtin `+`), never a strict chain; {seedWord}; signed zero per the licensed contract (the tree's chains start at +0: an all-(-0) row the helper folds comes out +0, as in the LLVM lane and the default build's previous chunk form; an unseeded one-cell row is its own cell; BLADE_FP_REPRO=1 keeps -0); the outer kernel passed the closed totality whitelist before any team was planned; the row loop's omp simd dropped (a licensed-only choice: each thread's partial is the ascending chain of its rows)"
                                  rowViewFoldMatrix "licensed Path B team (per-thread partials combined in job order from the caller's seed)" (rowViewSeedParty + "; the inner seed combined with its row's sum exactly once") ]
                            Some (
                                [ $"{ind}/* reduce over computation: comm-licensed parallel fold, outer level chunked per thread (Path B); licensed row-view folds: {rowPhrase}, {seedWord}; no omp simd on the row loop */"
                                  $"{ind}{{"
                                  $"{ind}    const size_t __rn = {boundStr};"
                                  $"{ind}    int __rT = blade_omp_get_max_threads();"
                                  $"{ind}    if ((size_t)__rT > __rn) __rT = (int)__rn;"
                                  $"{ind}    if (__rT < 1) __rT = 1;"
                                  $$"""{{ind}}    if (__rn > 0) {"""
                                  $"{ind}        {elemStr}* __rpart = new {elemStr}[__rT];"
                                  $"{ind}        bool* __rhad = new bool[__rT]();"
                                  $"{ind}        BLADE_OMP_PARALLEL_NUM_THREADS(__rT)"
                                  $$"""{{ind}}        {"""
                                  $"{ind}            const int __rnt = blade_omp_get_num_threads();"
                                  $"{ind}            const int __rt = blade_omp_get_thread_num();"
                                  $"{ind}            const size_t __rlo = blade_fold::chunk_lo(__rn, (size_t)__rt, (size_t)__rnt);"
                                  $"{ind}            const size_t __rhi = blade_fold::chunk_lo(__rn, (size_t)__rt + 1, (size_t)__rnt);"
                                  $"{ind}            {elemStr} __racc = {elemStr}();"
                                  $"{ind}            bool __rhas = false;"
                                  $$"""{{ind}}            for (size_t {{lvl.IndexName}} = __rlo; {{lvl.IndexName}} < __rhi; {{lvl.IndexName}}++) {""" ]
                                @ (peels |> List.map (fun p -> $"{ind}                {p}"))
                                @ [ $"{ind}                const {elemStr}* __rrow = {uStr}.data[{ixStr}];"
                                    $"{ind}                const {elemStr} __rv = {rowValue};"
                                    $$"""{{ind}}                if (__rhas) __racc = {{wname}}(__racc, __rv); else { __racc = __rv; __rhas = true; }"""
                                    $$"""{{ind}}            }"""
                                    $$"""{{ind}}            if (__rhas) { __rpart[__rt] = __racc; __rhad[__rt] = true; }"""
                                    $$"""{{ind}}        }"""
                                    $"{ind}        for (int __rt = 0; __rt < __rT; __rt++) if (__rhad[__rt]) {name} = {wname}({name}, __rpart[__rt]);"
                                    $"{ind}        delete[] __rpart; delete[] __rhad;"
                                    $$"""{{ind}}    }"""
                                    $"{ind}}}" ])
                        // `FoldChunk` (an `omp`-licensed fold) keeps Path B's
                        // thread team whenever this build opens teams. When it
                        // does NOT (BLADE_OMP_THREADS=1) the chunk plan is dead
                        // at its consumer, and the licence `omp` carried is the
                        // same reorder licence this arm spends -- so the fold
                        // takes the vectorized form instead of the serial chain
                        // it used to fall to ("omp implies simd", 2026-10-17;
                        // the dot / composed-chain / checksum shapes of
                        // bench gap G20). The census line for the dropped team
                        // is emitted below (`chunkNote`), because this arm never
                        // reaches genLoopNest, where it used to be printed.
                        | [lvl] when fpReassocEnabled () && licensedFold
                                     && (cg.FoldChunk.IsNone || not (ompThreadEmissionEnabled ()))
                                     && (laneable lvl || (domainPlanOfLevel lvl).IsSome) ->
                            // The nest's own element peels and kernel expression,
                            // built exactly as genLoopNestStreamed builds them for
                            // one level. `restrictPeelSites` is provably EMPTY for
                            // a single-level nest (its chain rule needs a deeper
                            // level to peel into), so `false` here is not a
                            // simplification -- it is the value that site computes.
                            // RESTRICT SOURCE ALIASES. The peel below would read
                            // each rank-1 operand as `<name>[i]` through the
                            // Array struct, and behind that subscript the
                            // vectorizer cannot prove the sweep is
                            // dependence-free (the pool pointers could alias the
                            // accumulator's storage as far as it knows). Reading
                            // through raw restrict pointers to the pool bases is
                            // what lets it fire. Licence is Phase 1's: these
                            // operands are READ-ONLY here (the fold writes only
                            // its scalar accumulator), and read-only sharing --
                            // including `zip(x, x)` binding one array to two
                            // slots -- is permitted under C's restrict
                            // semantics; only written-through pointers must be
                            // exclusive.
                            //
                            // HISTORY, because it is the measurement that chose
                            // the form below. These aliases were added for the
                            // K-LANE form, whose independent partials live in
                            // named scalars and need GCC's SLP pass to pack them
                            // into vectors. With the aliases SLP did fire -- and
                            // spent the entire gain marshalling the named lanes
                            // through a vunpck/vpermpd permute storm, landing at
                            // PARITY with the serial chain (dot shape, n = 1e7,
                            // 1 thread: 8.63 ms serial vs 8.66 ms in 8 lanes).
                            // The simd form asks for the partials in vector
                            // registers directly, so nothing is ever marshalled;
                            // the same aliases then buy the whole win.
                            let mutable srcAliases : Map<string, string> = Map.empty
                            let mutable aliasDecls : string list = []
                            for elem in lvl.Elements do
                                match elem.Virtual with
                                | RealArray when elem.ArrayRank = 1
                                                 && not (Map.containsKey elem.ArrayName srcAliases) ->
                                    let alias = $"__rsrc{Map.count srcAliases}"
                                    srcAliases <- Map.add elem.ArrayName alias srcAliases
                                    aliasDecls <- aliasDecls
                                        @ [ $"const {(irTypeToCpp elem.ArrayElemType)}* BLADE_RESTRICT {alias} = {elem.ArrayName}.data;" ]
                                | _ -> ()
                            let mutable currentNames : Map<int, string> = Map.empty
                            let mutable paramFinalNames : Map<IRId, string> = Map.empty
                            let mutable declaredNames : Map<string, string> = Map.empty
                            let mutable peels : string list = []
                            for elem in lvl.Elements do
                                let currentName =
                                    Map.tryFind elem.ArrayPosition currentNames
                                    |> Option.defaultValue
                                           (Map.tryFind elem.ArrayName srcAliases
                                            |> Option.defaultValue elem.ArrayName)
                                let (peelCode, newName) = genElementBindingPeel false lvl elem currentName
                                // Same dedup rule as the nest: zipping an array
                                // WITH ITSELF puts two slots on one declaration.
                                if Map.tryFind newName declaredNames <> Some peelCode then
                                    peels <- peels @ [peelCode]
                                declaredNames <- Map.add newName peelCode declaredNames
                                currentNames <- Map.add elem.ArrayPosition newName currentNames
                                match elem.Virtual with
                                | VirtualRange _ | VirtualReverse ->
                                    paramFinalNames <- Map.add elem.ParamVarId elem.ParamName paramFinalNames
                                | RealArray ->
                                    paramFinalNames <- Map.add elem.ParamVarId newName paramFinalNames
                            let nameMap =
                                paramFinalNames
                                |> Map.fold (fun acc k v -> Map.add k v acc) ctx.VarNames
                            let nameMap =
                                withCaptureNames EnclosingNamesWin cg.Captures nameMap
                            // A closed-form domain's cell code renders `Float64(Int64)` as the
                            // vectorizable split (CodeGenExprSupport, "WHERE `Float64(x)`").
                            let bodyExpr =
                                withI64SplitScope (domainPlanOfLevel lvl).IsSome (fun () ->
                                    (genKernelExprWithReynolds cg.KernelExpr cg.KernelParams
                                         false false nameMap paramFinalNames).CppExpr)
                            // OPERAND STREAMS the body reads: the distinct real
                            // arrays peeled at this level (a dot reads two; a
                            // self-dot `zip(x, x)` reads one pointer and counts as
                            // one). Virtual range/reverse slots are index
                            // arithmetic, not memory streams, and are not counted.
                            let streams =
                                lvl.Elements
                                |> List.filter (_.Virtual.IsRealArray)
                                |> List.map (_.ArrayName)
                                |> List.distinct
                                |> List.length
                            let boundStr = genLoopBoundExpr (compoundArrayNamesOf cg.Bindings) lvl
                            let chunkNote =
                                if cg.FoldChunk.IsSome
                                then [ $"{ind}/* {(ompSuppressedPhrase (ompThreadsSuppressedReason ()))} */" ]
                                // G51: the request was refused its team by the
                                // outer kernel's totality audit (never silently)
                                elif rowViewOuterCensus.IsSome then
                                    [ $"{ind}/* {(ompSuppressedPhrase rowViewOuterCensus.Value)} */" ]
                                // The MAP kernel asked for omp but its licence
                                // covers no level of this one-level nest (e.g.
                                // `omp(i: 0)`), so no chunk plan exists. This
                                // arm never reaches the nest emitter, whose
                                // marker would otherwise report it.
                                elif cg.OmpRequested then
                                    let why =
                                        if ompThreadEmissionEnabled () then "the omp(...) depth licenses no level of this nest"
                                        else ompThreadsSuppressedReason ()
                                    [ $"{ind}/* {(ompSuppressedPhrase why)} */" ]
                                else []
                            // A CLOSED-FORM DOMAIN level (docs/plans/structural/06):
                            // the affine nest over the struct's fields, with the
                            // licence spent as `omp simd reduction` on the
                            // INNERMOST field's run -- the contiguous, affine-
                            // bounded inner loop the band/triangle shapes
                            // vectorize on. The nest carries no cell counter
                            // (there is no output to address), which keeps the
                            // inner loop in OpenMP canonical form. A level that
                            // also peels a REAL array needs the counter
                            // (`R.data[__iL]`) and keeps the serial nest.
                            match domainPlanOfLevel lvl with
                            | Some plan ->
                                let realOperand = lvl.Elements |> List.exists (_.Virtual.IsRealArray)
                                match fpReassocSimdOp callable elemStr with
                                | Some opStr when not realOperand ->
                                    // THE ORDER (domainFoldOrder, the decision the
                                    // plan line and the LLVM lane read): a BAND under
                                    // the licence walks diagonal by diagonal -- the
                                    // expert's `do k = -w, w; do i`, same cells, same
                                    // per-cell arithmetic, the licensed summation
                                    // order; the inner run is unit-stride in i and in
                                    // j = i - k, and a term depending only on i - j is
                                    // invariant in it, which g++ hoists once inlined.
                                    // Every other domain keeps the lex nest.
                                    let order = domainFoldOrder (Some callable) (Some cg.KernelExpr) plan
                                    // THREADS (bench gap G31): a `where omp` fold
                                    // kernel, in a build that emits teams.
                                    let mapSafe = domainMapKernelPure cg.KernelExpr
                                    let threaded = callable.IsOmpParallel && ompThreadEmissionEnabled () && mapSafe
                                    let walk =
                                        match order with
                                        | DomainDiagonalOrder dg ->
                                            $"band walked by diagonal, k = {(plan.Levels.[0].Field)} - {(plan.Levels.[1].Field)} in [{dg.KLo}, {dg.KHi}], simd on the {(plan.Levels.[0].Field)} run"
                                        | DomainLexOrder _ -> "affine nest, simd on the innermost field"
                                    let outerName =
                                        match order with
                                        | DomainDiagonalOrder _ -> "the diagonal index k"
                                        | DomainLexOrder _ -> $"the outermost field {(plan.Levels.[0].Field)}"
                                    if threaded then
                                        recordOmpConstruct name "omp parallel num_threads(T) + fixed-order combine"
                                            $"closed-form domain fold ({plan.Name}): {outerName} cut into fixed per-thread blocks (each innermost run cut instead when the team outnumbers its trips)"
                                        recordFoldForm name $"threaded(fixed blocks over {outerName}, fixed-order combine; simd within each block)" $"reduce over a closed-form domain ({plan.Name}: {walk})"
                                    else
                                        recordFoldForm name "simd-reduction" $"reduce over a closed-form domain ({plan.Name}: {walk})"
                                    // The licence asked for threads this build does not
                                    // emit: said in the C++, never silently.
                                    let ompNote =
                                        if callable.IsOmpParallel && not threaded then
                                            let why =
                                                if not (ompThreadEmissionEnabled ()) then ompThreadsSuppressedReason ()
                                                else "the domain map's kernel may assign a capture or emit output (or calls something that may), which a thread team would race on"
                                            [ $"{ind}/* {(ompSuppressedPhrase why)} */" ]
                                        else []
                                    let walkNote =
                                        match order with
                                        | DomainDiagonalOrder _ -> "band walked by diagonal (k = i - j)"
                                        | DomainLexOrder _ -> "affine nest"
                                    let teamNote = if threaded then ", one team, fixed per-thread blocks, fixed-order combine" else ""
                                    Some (
                                        chunkNote @ ompNote @
                                        [ $"{ind}/* reduce over computation: closed-form domain {plan.Name} ({plan.Card} cells), {walkNote}, omp simd reduction on the innermost run (licensed reorder){teamNote} */" ]
                                        @ (genDomainFoldCpp lvl plan order name elemStr opStr threaded aliasDecls peels bodyExpr
                                           |> List.map (fun s -> ind + s)))
                                | _ -> None
                            | None ->
                            // M9 (gap G26): a DOT-CLASS body takes the explicit
                            // K-lane vector form -- one call over the restrict
                            // pool aliases, K independent vector accumulators in
                            // the fixed tree blade_fold_lanes.hpp documents --
                            // in place of the one-accumulator simd reduction
                            // below, which GCC never interleaves. The seed is
                            // folded in through the wrapper, as every arm here
                            // does; the call's own chains start at +0.
                            match dotClass lvl with
                            | Some (kind, ops) ->
                                let k = foldVecLanes kind
                                let label = foldVecLabel kind k
                                let streamsWord = if foldVecStreams kind = 1 then "1 operand stream" else $"{(foldVecStreams kind)} operand streams"
                                let ptrs = ops |> List.map (fun nm -> Map.tryFind nm srcAliases |> Option.defaultValue $"{nm}.data")
                                let call = foldVecCall elemStr k kind ptrs "__rhi"
                                (foldLanesUsedCell ()).Value <- true
                                recordFoldForm name label "reduce over a deferred computation (one level)"
                                Some (
                                    chunkNote @
                                    [ $"""{ind}/* reduce over computation: {label} (explicit vector lane tree, licensed reorder, {streamsWord}) */"""
                                      $"{ind}{{"
                                      $"{ind}    const size_t __rhi = {boundStr};" ]
                                    @ (aliasDecls |> List.map (fun s -> ind + "    " + s))
                                    @ [ $"{ind}    {name} = {wname}({name}, {call});"
                                        $"{ind}}}" ])
                            | None ->
                            match fpReassocSimdOp callable elemStr with
                            | Some opStr ->
                                // SIMD FORM, and the site the two forms differ
                                // most sharply at. The peels go INSIDE the
                                // vectorized loop (the `prelude`), so the body
                                // expression is written straight into it -- no
                                // `__rbody` lambda, because there is only ONE
                                // index to evaluate it at and nothing to
                                // re-render.
                                //
                                // MEASURED (dot shape, `reduce(<unforced zip>,
                                // (+))`, f64, OMP_NUM_THREADS=1, medians over 3
                                // processes x 5 reps, `bench\blade`):
                                //
                                //   n = 10000019 (bandwidth-bound)
                                //       serial 8.82 ms  8 lanes 9.32 ms (0.95x)
                                //                       simd    5.38 ms (1.64x)
                                //   n = 300007 (cache-resident)
                                //       serial  224 us  8 lanes  229 us (0.97x)
                                //                       simd    58.6 us (3.81x)
                                //
                                // The lanes are worth NOTHING at this shape in
                                // either regime -- they are the permute storm
                                // described above -- and 5.4 ms is what a
                                // hand-written packed-FMA dot reaches on this
                                // machine. The whole gain was sitting behind the
                                // marshalling.
                                //
                                // NO short fallback and no `streams` count: the
                                // vectorizer emits its own scalar remainder, and
                                // the register-budget rule the lane count exists
                                // to apply (`laneCountForStreams`) is about
                                // NAMED scalar accumulators -- it has nothing to
                                // say about vector-register partials, whose
                                // width the compiler picks.
                                recordFoldForm name "simd-reduction" "reduce over a deferred computation (one level)"
                                Some (
                                    chunkNote @
                                    // BLOCK comments on this arm and the lane arm: a
                                    // kernel-body fold's statements are SPACE-JOINED
                                    // into a one-line IIFE (corpus loops/467), where
                                    // a `//` would swallow the rest of the fold.
                                    [ $"""{ind}/* reduce over computation: accumulator loop (omp simd reduction, licensed reorder, {streams} operand stream{(if streams = 1 then "" else "s")}) */"""
                                      $"{ind}{{"
                                      $"{ind}    const size_t __rhi = {boundStr};" ]
                                    @ (aliasDecls |> List.map (fun s -> ind + "    " + s))
                                    @ (fpReassocSimdStmts opStr name lvl.IndexName "0" "__rhi"
                                           peels (fun _ -> bodyExpr)
                                       |> List.map (fun s -> ind + "    " + s))
                                    @ [ $"{ind}}}" ])
                            | None ->
                            // K-LANE FORM: the fallback for a licence the simd
                            // arm cannot spell (a `comm`-declared kernel, whose
                            // combine is a call, or a non-scalar element type).
                            // Here the body IS evaluated at K different indices,
                            // so it is rendered ONCE into a local
                            // `[&](size_t) -> T` lambda and the lanes call it at
                            // their own indices; the K copies are the inliner's,
                            // not the emitter's. Rendering once is not only
                            // smaller text: the kernel emitters are not all pure
                            // (some register collected definitions), so emitting
                            // the same body twice is a hazard the single render
                            // removes.
                            let kLanes = laneCountForStreams streams
                            let bodyAt (i: string) = $"__rbody({i})"
                            let (laneStmts, resultLane) =
                                fpReassocLaneStmts kLanes elemStr "__rlane" "__ri" "0" "__rhi" bodyAt
                                    (fun acc rhs -> $"{acc} = {wname}({acc}, {rhs});")
                            recordFoldForm name $"K-lanes({kLanes})" "reduce over a deferred computation (one level)"
                            Some (
                                chunkNote @
                                [ $"""{ind}/* reduce over computation: accumulator loop ({kLanes}-lane, licensed reorder, {streams} operand stream{(if streams = 1 then "" else "s")}) */"""
                                  $"{ind}{{"
                                  $"{ind}    const size_t __rhi = {boundStr};" ]
                                @ (aliasDecls |> List.map (fun s -> ind + "    " + s))
                                @ [ $"""{ind}    auto __rbody = [&](size_t {lvl.IndexName}) -> {elemStr} {{ {(peels |> String.concat " ")} return {bodyExpr}; }};"""
                                    $$"""{{ind}}    if (__rhi < (size_t){{kLanes}}) {"""
                                    // Below K elements there is nothing to
                                    // interleave: the serial chain verbatim -- the
                                    // same bodies, folded in the same ascending
                                    // order into the same seeded accumulator, hence
                                    // the same double the nest below produces.
                                    $$"""{{ind}}        for (size_t __ri = 0; __ri < __rhi; __ri++) {"""
                                    $"{ind}            {name} = {wname}({name}, __rbody(__ri));"
                                    $$"""{{ind}}        }"""
                                    $$"""{{ind}}    } else {""" ]
                                @ (laneStmts |> List.map (fun s -> ind + "        " + s))
                                @ [ $"{ind}        {name} = {wname}({name}, {resultLane});"
                                    $$"""{{ind}}    }"""
                                    $"{ind}}}" ])
                        | _ -> None
                    // The fold nest as emitted today, and the lane form when the
                    // knob and the gate above both admit it. Shared by both
                    // non-dispatch arms so they cannot drift.
                    // A `where omp` fold kernel over a closed-form domain whose
                    // threaded form (genDomainFoldCpp, G31, in the lane arm above)
                    // declined: the thread half of the licence is dropped -- never
                    // silently, and exactly once (the Path B note below stays quiet
                    // for a domain nest, which is never chunked). A FUNCTION:
                    // ompSuppressedPhrase records the census line as it renders.
                    let isDomainFold =
                        match cg.Bindings with
                        | head :: _ -> (domainPlanOfLevel head).IsSome
                        | [] -> false
                    let domainOmpNote () =
                        if callable.IsOmpParallel && isDomainFold then
                            let why =
                                if foldKernelReproVetoed callable then
                                    "fold kernel is `where repro` (the operation order is the contract; reorder licence vetoed)"
                                elif not (fpReassocEnabled ()) then fpReproReason ()
                                else "a closed-form domain fold has a thread-level form only for a builtin `+`/`*` reduction of a scalar over the domain's own map (this fold's kernel, element type or operands do not qualify)"
                            [ ind + "/* " + ompSuppressedPhrase why + " */" ]
                        else []
                    let foldNestLines () =
                        let lane = laneLines ()
                        wrapperLines
                        @ [$"{ind}{elemStr} {name} = {seedStr};"]
                        @ (match lane with
                           | Some ls -> ls
                           | None ->
                               let domainNote = domainOmpNote ()
                               // What the ordinary nest makes of this fold, for
                               // `blade plan` (the lane arm records its own).
                               let rowSlotsForm =
                                   match cg.FoldChunk with
                                   | Some { ChunkForm = ChunkRowSlots (rows, _) } when ompThreadEmissionEnabled () ->
                                       Some $"threaded complete rows (repro): {rows} row folds into indexed slots, the outer chain serial in ascending order from its seed"
                                   | _ -> None
                               let form =
                                   if mapBodyWrites then "serial (the map kernel writes a captured binding: its evaluations are ordered side effects; no lane, simd or thread form)" elif not (foldReorderLicensed callable) then unlicensedFoldForm callable
                                   elif rowSlotsForm.IsSome then rowSlotsForm.Value
                                   elif not (fpReassocEnabled ()) then "serial (repro)"
                                   elif cg.FoldChunk.IsSome && ompThreadEmissionEnabled () then
                                       (match cg.FoldChunk.Value.ChunkForm with
                                        | ChunkSimd _ -> "threaded(Path B chunks, simd-reduction within each chunk)"
                                        | ChunkSerial why -> $"threaded(Path B chunks, serial within each chunk: {why})"
                                        // unreachable: the producer plans rows only in a repro build
                                        | ChunkRowSlots _ -> "threaded complete rows")
                                   elif cg.Bindings.Length <> 1 then
                                       "serial (multi-level nest: no lane or simd form for a fold over a nest yet)"
                                   elif (domainPlanOfLevel (List.head cg.Bindings)).IsSome then
                                       "serial (closed-form domain: affine nest; the fold kernel has no simd reduction operator, or a real array rides the level)"
                                   else "serial (head level is not a plain rectangular sweep)"
                               let formWhere =
                                   match rowSlotsForm, cg.FoldChunk with
                                   | Some _, Some { ChunkForm = ChunkRowSlots (_, cells) } ->
                                       // The orbit fold's own decline, when this is a
                                       // logical walk over a compact array: the rows
                                       // threaded here are that walk's (BLADE_ORBIT_FOLD=0
                                       // keeps the walk; it does not keep it serial).
                                       let walk =
                                           match orbitWhy with
                                           | Some why -> $"; rows of the logical walk (orbit-class fold not taken: {why})"
                                           | None -> ""
                                       let serialLanes = if scatterPlan.IsSome then Blade.OrbitFold.scatterServesSerialMarker else ""
                                       $"reduce over a deferred computation (one level, Path B partition; {cells} inner fold steps; each row's fold is its own serial chain on one thread{walk}){serialLanes}"
                                   | _ -> "reduce over a deferred computation"
                               match rowSlotsForm, rowViewLines with
                               // G51: a taken row-slot form over row-view folds
                               // carries what its rows are and the audit matrix.
                               | Some _, Some extra ->
                                   recordCodegenDecision "fold-form" name Blade.Effects.Applied
                                       ([ $"fold form: {form}"; formWhere ] @ extra)
                               | _ -> recordFoldForm name form formWhere
                               // A `where omp` FOLD kernel that got no chunk plan
                               // (`chunkable` declined) reaches the nest with
                               // nothing that says so: the nest's own marker reads
                               // the MAP's request, and the FoldChunk lines need a
                               // plan. Census line for exactly that case.
                               let unchunkedNote =
                                   if callable.IsOmpParallel && cg.FoldChunk.IsNone && not isDomainFold then
                                       let why =
                                           if foldKernelReproVetoed callable then
                                               "fold kernel is `where repro` (the operation order is the contract; reorder licence vetoed)"
                                           elif mapBodyWrites then
                                               "the map kernel writes a captured binding; a fold licence does not make the map's body race-safe (Path B would run it on a team)"
                                           elif rowViewOuterCensus.IsSome then rowViewOuterCensus.Value
                                           else "the fold's outermost level is not a plain rectangular sweep, so it cannot be chunked per thread (Path B)"
                                       // BLOCK form: a kernel-body fold's lines are
                                       // space-joined into a one-line IIFE.
                                       [ $"{ind}/* {(ompSuppressedPhrase why)} */" ]
                                   else []
                               domainNote @ unchunkedNote
                               @ withI64SplitScope isDomainFold (fun () -> genLoopNest cg ctx.VarNames ctx.Indent))
                    match scatterTaken with
                    | Some (sp, gName) ->
                        // A thread request on the walk's fold or map is not
                        // spent by the scatter (its chains thread only as a
                        // wavefront): the census marker says why.
                        // EVERY level's request (fold and map kernels, outer
                        // and inner): the nested walk this replaces is where an
                        // inner request would have been reported.
                        let requests =
                            let rs = Blade.OrbitFold.scatterThreadRequests sp
                            if mapLicensesOuter && not (rs |> List.contains "level 1's map") then "level 1's map" :: rs else rs
                        let requestedAt = $"""requested at: {String.concat ", " requests}"""
                        let ompNote =
                            if requests.IsEmpty then None
                            elif not (ompThreadEmissionEnabled ()) then Some $"{ompThreadsSuppressedReason ()} ({requestedAt})"
                            else
                                let g35 =
                                    match rowSlotWhy with
                                    | _ when rowSlotsYielded -> Blade.OrbitFold.rowSlotsYieldedCensus
                                    | Some why -> $"the threaded complete-row form declined: {why}"
                                    | None -> "no threaded complete-row form applies"
                                let build =
                                    if fpReproEnabled () then fpReproReason ()
                                    else "the walk's folds carry no reorder licence"
                                let g35 = if callable.IsOmpParallel || mapLicensesOuter then g35 else "no outer-level request for the threaded complete-row form"
                                Some $"{build}; the ordered orbit scatter runs serially (its chains thread only as a wavefront; {g35}; {requestedAt})"
                        (genOrbitScatterCpp ctx ind name gName sp orbitWhy ompNote, ctx')
                    | None ->
                    let code =
                        match dotCall with
                        | Some (call, entry) ->
                            let nameOf role =
                                call.NestOperands
                                |> List.tryPick (fun (r, src) ->
                                    if r <> role then None
                                    else match src with
                                         | Blade.LinAlgPatterns.FromNestArray n -> Some n
                                         | _ -> None)
                            match nameOf Blade.LinAlgPatterns.RoleA,
                                  nameOf Blade.LinAlgPatterns.RoleB with
                            | Some xName, Some yName ->
                                (linalgUsedCell ()).Value <- true
                                recordFoldForm name "blas-dot" "reduce over a deferred computation (L1 dispatch)"
                                let nExtent =
                                    genLoopBoundExpr (compoundArrayNamesOf cg.Bindings)
                                                     (List.head cg.Bindings)
                                // BLOCK comment -- see materializeGramForm's note.
                                [ $"{ind}/* linalg dispatch: dot({xName}, {yName}) = reduce({xName} * {yName}, (+)) */ {elemStr} {name} = {entry}({nExtent}, {xName}.data, {yName}.data, {seedStr});" ]
                            | _ -> foldNestLines ()
                        | None -> foldNestLines ()
                    (code, ctx')
                | _ :: _ ->
                    // Fused tree: ONE merged nest, one scalar accumulator per
                    // leaf. Each leaf accumulates at its OWN depth from its
                    // OWN arrays (genFusedLoopNest staggers mixed-arity
                    // trees), so incompatible loop structures are a loud
                    // diagnostic, never silently-shared loops.
                    let leafNames = infos |> List.mapi (fun i _ -> $"{name}_{i}")
                    let leafCgs = infos |> List.mapi (fun i info -> foldCg info leafNames.[i])
                    // A fused fold writes shared scalar accumulators, which race
                    // under any parallel/device backend (omp reduction clauses
                    // and device reductions are the future upgrade). Reject a
                    // device leaf loudly; host leaves fold serially.
                    let deviceLeaf = infos |> List.tryPick (fun info ->
                        match classifyLeafBackend info with
                        | BkCuda _ -> Some "cuda" | BkMpi -> Some "mpi" | _ -> None)
                    match checkMergeCompatible leafCgs, deviceLeaf with
                    | _, Some bk ->
                        (codegenError ctx ind ($"reduce over a fused computation with a {bk} leaf: device/parallel reductions over a fused tree are not supported yet -- force the leaf with |> compute and reduce the array"), ctx')
                    | Error reason, _ ->
                        (codegenError ctx ind ($"reduce over a fused computation: cannot fuse the leaves into one loop nest: {reason}"), ctx')
                    | Ok _, None ->
                        let declCode =
                            leafNames |> List.map (fun ln -> $"{ind}{elemStr} {ln} = {seedStr};")
                        let (sm, sp, sNew) = streamedNestSetup ctx.StreamedArrays ind leafCgs
                        registerStreamBufDecls sNew
                        // VECTORIZED FOLD form ("omp implies simd", 2026-10-17):
                        // the shared kernel's licence, spent as ONE
                        // `omp simd reduction` over every leaf's accumulator.
                        let effects () =
                            joinEffectAudit ctx.DeferredComputations
                                (infos |> List.mapi (fun i info -> ($"leaf {i}'s map kernel", info)))
                                [ ("the join's fold kernel", callable) ]
                        let simdPlan =
                            match effects () with
                            | Error why -> Error why
                            | Ok () -> fusedFoldSimdPlan sm leafCgs (leafNames |> List.map (fun ln -> (ln, callable, elemStr)))
                        let (simdFold, form) =
                            match simdPlan with
                            | Ok p -> (Some p, "simd-reduction")
                            | Error _ when not (fpReassocEnabled ()) ->
                                (None, $"serial (repro): {leafNames.Length} ascending original chains")
                            | Error why -> (None, why)
                        // THE THREADED JOIN (G7): the request is the shared fold
                        // kernel's `where omp`, or a leaf map's `omp(...)` that
                        // covers the outer level (it selects the traversal; it
                        // licenses no leg). Each leg's licence is the shared
                        // kernel's own, checked per leg all the same.
                        let request =
                            if callable.IsOmpParallel then Some "the join's fold kernel (`where omp`)"
                            elif leafCgs |> List.exists (fun cg -> cg.OmpRequested && (match cg.Bindings with h :: _ -> h.IsParallel | [] -> false)) then
                                Some "a leaf map's `omp(...)` covering the outer level (it selects the traversal; it licenses no leg)"
                            else None
                        let joinThread =
                            joinedFoldThreadPlan (sanitizeCppName name) request simdPlan
                                (leafNames |> List.map (fun ln -> (ln, callable, elemStr, seedStr, wname)))
                                (fun () ->
                                    match effects () with
                                    | Error why -> Error why
                                    | Ok () ->
                                    joinThreadAudit ctx.DeferredComputations
                                        (infos |> List.mapi (fun i info -> ($"leaf {i}'s map kernel", info)))
                                        [ ("the join's fold kernel", callable) ])
                        (match joinThread with
                         | Some (Ok plan) -> recordFoldForm name (joinedFoldForm plan) $"fused fold over {leafNames.Length} leaves (one traversal, one team)"
                         | _ -> recordFoldForm name form $"fused fold over {leafNames.Length} leaves (one traversal)")
                        // A `where omp` on the shared kernel that did not get the
                        // team: the thread half is declined -- never silently --
                        // with the first failed condition.
                        let ompNote =
                            match joinThread with
                            | Some (Error why) when callable.IsOmpParallel ->
                                [ ind + "/* " + ompSuppressedPhrase why + " */" ]
                            | _ -> []
                        let loopCode = sp @ ompNote @ genFusedLoopNestStreamedJoined simdFold joinThread sm leafCgs ctx.VarNames ctx.Indent false None
                        let tupleLine = $"""{ind}auto {name} = std::make_tuple({(leafNames |> String.concat ", ")});"""
                        // Destructure sub-bindings resolve through TupleChildren
                        // straight to the accumulator names (the fusion-tree
                        // convention) -- never through std::get on the nested type.
                        let ctxOut = { ctx' with TupleChildren = Map.add name leafNames ctx'.TupleChildren }
                        (wrapperLines @ declCode @ loopCode @ [tupleLine], ctxOut)
                | [] ->
                    (codegenError ctx ind "reduce over a deferred computation: no leaves (unreachable)", ctx')
            | _ ->
                (codegenError ctx ind "reduce over a deferred computation: fold kernel must resolve to a binary callable (typechecker or IR bug if not)", ctx')



/// REDUCTION JOIN (docs/plan-reduction-joins.md): k reductions, one traversal,
/// a flat tuple of k accumulators. The shape is the fused tree's -- one merged
/// nest, one scalar accumulator per leaf, `make_tuple` at the end -- with two
/// things the shared-fold tree does not have:
///
///   PER-LEG FOLDS. Each leg brings its own kernel wrapper, its own seed, and
///   its own accumulator type, because a join's legs are independent
///   reductions (`prodsum` folds `(+)` from 0; `reduce(x, max, -inf)` folds a
///   lambda from a user seed) that only agree on WHERE they iterate.
///
///   SHARING BY NAMING. A leg operand that is a named DEFERRED map -- `let ct
///   = cos <@> ph`, never forced -- becomes a per-iteration `const` computed
///   ONCE in the joint nest, and every leg reading that name reads the local.
///   The declaration is the NAME: nothing is deduced about what the legs
///   compute, only that they spell the same binding. A `|> compute`d operand
///   is an array and keeps today's behavior (read from memory).
///
/// The rewrite that makes sharing work is small and local: a consumer leg's
/// deferred operand slot is repointed at the deferred map's OWN source array
/// (so the level's extent and peel name exist in C++, and dedup with the share
/// leaf's peel), and the kernel param that slot bound is substituted for a
/// reference to the deferred binding -- whose emitted name is exactly the
/// shared local's. Nothing else in the nest emitter needs to know.
and genReduceJoinCore (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder)
                      (compExpr: IRExpr) (kernelExprs: IRExpr list) (seedExprs: IRExpr list)
                      : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let rec resolveDeferred e =
        match e with
        | IRVar (id, _) ->
            (match Map.tryFind id ctx.DeferredComputations with
             | Some d -> resolveDeferred d
             | None -> e)
        | _ -> e
    // A leaf that is a NAMED deferred map keeps its id beside the resolved
    // apply: `reduce(e, (+))` next to `prodsum(e, v)` is the sharing
    // declaration read from the leg's side (docs/plans/structural/03, D2),
    // and the id is what ties this leg to the share leaf the slot declares.
    let rec collectLeaves e =
        match e with
        | IRVar (id, _) when Map.containsKey id ctx.DeferredComputations ->
            (match resolveDeferred e with
             | IRFusion _ as f -> collectLeaves f
             | IRApplyCombinator _ as a -> [(a, Some id)]
             | other -> [(other, None)])
        | IRFusion (l, r) -> collectLeaves l @ collectLeaves r
        | other -> [(other, None)]
    let leafPairs = collectLeaves compExpr
    let leaves = leafPairs |> List.map fst
    let leafVarIds = leafPairs |> List.map snd
    let infos = leaves |> List.choose (function IRApplyCombinator i -> Some i | _ -> None)
    let ctx' = addVarName binding.Id name ctx
    if infos.Length <> leaves.Length || infos.Length <> kernelExprs.Length then
        (codegenError ctx ind "reduction join: every leg must resolve to an unforced kernel application (typechecker or IR bug if not)", ctx')
    else
    let unsupportedInput =
        infos |> List.exists (fun info ->
            info.ArrayTypes |> List.exists (fun at ->
                at.IndexTypes |> List.exists (fun ix ->
                    isRaggedFamilyKind ix.IxKind || ix.IxKind = IxKDepInner
                    || ix.IxKind = IxKGroupOuter || ix.IxKind = IxKCompound)))
    if unsupportedInput then
        (codegenError ctx ind "reduction join over ragged/grouped/compound inputs is not supported yet -- force the legs with |> compute and reduce the arrays", ctx')
    else
    let deviceLeaf =
        infos |> List.tryPick (fun info ->
            match classifyLeafBackend info with
            | BkCuda _ -> Some "cuda" | BkMpi -> Some "mpi" | _ -> None)
    match deviceLeaf with
    | Some bk ->
        (codegenError ctx ind ($"reduction join with a {bk} leg: device/parallel reductions over a joined traversal are not supported yet -- force the leg with |> compute and reduce the array"), ctx')
    | None ->
    let callables = kernelExprs |> List.map resolveCallable
    if callables |> List.exists (fun c -> match c with Some cb -> cb.Params.Length <> 2 | None -> true) then
        (codegenError ctx ind "reduction join: every leg's fold kernel must resolve to a binary callable (typechecker or IR bug if not)", ctx')
    else
    let callables = callables |> List.map Option.get
    // ---- SHARED DEFERRED OPERANDS -----------------------------------------
    // A leg operand that resolves (through the binding) to another unforced
    // apply. Distinct ids, in first-use order: that order is the order the
    // share leaves are emitted in, and a shared value must be declared before
    // the leg that reads it.
    let deferredOperand (e: IRExpr) : (IRId * ApplyInfo) option =
        match e with
        | IRVar (id, _) ->
            (match Map.tryFind id ctx.DeferredComputations with
             | Some (IRApplyCombinator dinfo) -> Some (id, dinfo)
             | _ -> None)
        | _ -> None
    // Per leg, in traversal order: the leg's own leaf when it is a named map
    // (collectLeaves), then its operand slots. A slot operand is always a
    // share (that is how a deferred slot gets a C++ definition at all); a
    // leaf name is a share only when the join spells it at least twice -- as
    // a leaf again or as a slot -- so a leg that merely folds a named map on
    // its own keeps today's inlined nest.
    let leafDeferred (v: IRId option) : (IRId * ApplyInfo) option =
        v |> Option.bind (fun id ->
            match Map.tryFind id ctx.DeferredComputations with
            | Some (IRApplyCombinator di) -> Some (id, di)
            | _ -> None)
    let leafShares = leafVarIds |> List.choose leafDeferred
    let slotShares = infos |> List.collect (fun info -> info.Arrays |> List.choose deferredOperand)
    let spelled (id: IRId) =
        (leafShares |> List.filter (fun (i, _) -> i = id) |> List.length)
        + (slotShares |> List.filter (fun (i, _) -> i = id) |> List.length)
    let sharedIds =
        List.zip leafVarIds infos
        |> List.collect (fun (lv, info) ->
            (match leafDeferred lv with
             | Some (id, di) when spelled id >= 2 -> [(id, di)]
             | _ -> [])
            @ (info.Arrays |> List.choose deferredOperand))
        |> List.fold (fun acc (id, di) -> if acc |> List.exists (fun (i, _) -> i = id) then acc else acc @ [(id, di)]) []
    let badShare =
        sharedIds |> List.tryPick (fun (id, di) ->
            let who = Map.tryFind id ctx.VarNames |> Option.defaultValue "<anon>"
            if di.Arrays.IsEmpty then
                Some ($"shared deferred operand '{who}' has no array source to take its traversal from")
            // A deferred map OVER a deferred map: the share leaf would name an
            // operand that has no C++ definition either. One level only.
            elif di.Arrays |> List.exists (fun a -> (deferredOperand a).IsSome) then
                Some ($"shared deferred operand '{who}' reads another deferred computation; only one level of sharing is supported")
            else None)
    match badShare with
    | Some reason ->
        (codegenError ctx ind ($"reduction join: {reason} -- force it with |> compute and join over the array"), ctx')
    | None ->
    let sharedName (id: IRId) = Map.tryFind id ctx.VarNames |> Option.defaultValue ($"__jshr{id}")
    let arrayNamesOf (info: ApplyInfo) =
        info.Arrays |> List.mapi (fun i arr ->
            match arr with
            | IRVar (id, _) ->
                (match sharedIds |> List.tryFind (fun (sid, _) -> sid = id) with
                 // A deferred operand keeps the SOURCE array's name: the slot
                 // has already been repointed at it below.
                 | Some (_, di) ->
                    (match di.Arrays.Head with
                     | IRVar (srcId, _) -> Map.tryFind srcId ctx.VarNames |> Option.defaultValue ($"arr{i}")
                     | _ -> $"arr{i}")
                 | None -> Map.tryFind id ctx.VarNames |> Option.defaultValue ($"arr{i}"))
            | IRRange _ -> $"__range{i}"
            | IRVirtualReverse _ -> $"__rev{i}"
            | _ -> $"arr{i}")
    /// Repoint every deferred operand slot at the deferred map's own leading
    /// source array, so the level's bound and peel name exist in C++ (and
    /// dedup with the share leaf's identical peel). Which slots were repointed
    /// is returned, so the kernel params they bound can be substituted.
    let repoint (info: ApplyInfo) : ApplyInfo * (int * IRId) list =
        let mutable moved = []
        let arrays =
            info.Arrays |> List.mapi (fun pos a ->
                match deferredOperand a with
                | Some (id, di) ->
                    moved <- moved @ [(pos, id)]
                    di.Arrays.Head
                | None -> a)
        let arrayTypes =
            info.ArrayTypes |> List.mapi (fun pos t ->
                match List.tryItem pos info.Arrays |> Option.bind deferredOperand with
                | Some (_, di) -> (match List.tryHead di.ArrayTypes with Some dt -> dt | None -> t)
                | None -> t)
        ({ info with Arrays = arrays; ArrayTypes = arrayTypes }, moved)
    /// Replace every reference to `fromId` with `toExpr`, everywhere.
    let rec substVar (fromId: IRId) (toExpr: IRExpr) (e: IRExpr) : IRExpr =
        match e with
        | IRVar (id, _) when id = fromId -> toExpr
        | ExprShape (children, rebuild) ->
            if children.IsEmpty then e else rebuild (children |> List.map (substVar fromId toExpr))
    // ---- Share leaves ------------------------------------------------------
    let shareCgs =
        sharedIds |> List.map (fun (id, di) ->
            let shName = sanitizeCppName (sharedName id)
            let elemCpp =
                match inferExprType (IRApplyCombinator di) with
                | ArrayElem at -> elemTypeToCpp at.ElemType
                | IRTScalar et -> primTypeToCpp et
                | t -> irTypeToCpp t
            let cg = routeKernelBodyThroughCall di (buildLoopNestCodeGen di (arrayNamesOf di) shName builder)
            { cg with ShareDecl = Some elemCpp })
    // ---- Leg leaves --------------------------------------------------------
    // One wrapper per leg, never deduplicated: two legs folding the same
    // operator get two identical lambdas, which costs a line of text and keeps
    // the wrapper name a pure function of the leg index.
    let wrappers =
        callables |> List.mapi (fun i cb -> genCallableWrapper ctx.VarNames ($"{name}_j{i}") cb)
    let wnames = wrappers |> List.map snd
    let sharedElemTy (id: IRId) =
        match sharedIds |> List.tryFind (fun (sid, _) -> sid = id) with
        | Some (_, di) ->
            (match inferExprType (IRApplyCombinator di) with
             | ArrayElem at -> at.ElemType
             | t -> t)
        | None -> IRTScalar ETFloat64
    let leafNames = infos |> List.mapi (fun i _ -> $"{name}_{i}")
    let leafCgs =
        List.mapi (fun i (info: ApplyInfo) ->
            match leafVarIds.[i] |> Option.bind (fun sid -> sharedIds |> List.tryFind (fun (s, _) -> s = sid)) with
            | Some (sid, di) ->
                // This leg's traversal IS the shared map: the same nest as the
                // share leaf (its peels dedup with the share's), whose per-cell
                // value is the share local itself -- `acc = wrap(acc, <share>)`,
                // never a second spelling of the producer.
                let cg0 = routeKernelBodyThroughCall di (buildLoopNestCodeGen di (arrayNamesOf di) leafNames.[i] builder)
                { cg0 with KernelExpr = IRVar (sid, sharedElemTy sid)
                           OutputType = callables.[i].RetType
                           FoldWrapper = Some wnames.[i] }
            | None ->
            let (info', moved) = repoint info
            let cg0 = routeKernelBodyThroughCall info' (buildLoopNestCodeGen info' (arrayNamesOf info') leafNames.[i] builder)
            // Every param bound by a repointed slot now reads the SHARED local
            // instead of the source cell. The deferred binding's own id renders
            // through ctx.VarNames as exactly the share leaf's output name.
            let kernelExpr =
                moved |> List.fold (fun body (pos, sid) ->
                    let paramIds =
                        cg0.Bindings
                        |> List.collect (_.Elements)
                        |> List.filter (fun el -> el.ArrayPosition = pos)
                        |> List.map (_.ParamVarId)
                    paramIds |> List.fold (fun acc pid ->
                        substVar pid (IRVar (sid, sharedElemTy sid)) acc) body) cg0.KernelExpr
            { cg0 with KernelExpr = kernelExpr
                       OutputType = callables.[i].RetType
                       FoldWrapper = Some wnames.[i] })
            infos
    match checkJoinCompatible (shareCgs @ leafCgs) with
    | Error reason ->
        (codegenError ctx ind ($"reduction join: cannot fold the legs in one traversal: {reason}"), ctx')
    | Ok _ ->
        let wrapperLines = wrappers |> List.collect fst |> List.map (fun s -> ind + s)
        let elemStrs =
            callables |> List.map (fun cb ->
                match cb.RetType with
                | IRTScalar et -> primTypeToCpp et
                | t -> irTypeToCpp t)
        // Each leg's ORIGINAL seed, rendered once and evaluated once, in leg
        // order, before the traversal (and before any team).
        let seedStrs = seedExprs |> List.map (exprToCppCtx ctx)
        let declCode =
            List.mapi (fun i ln ->
                $"{ind}{elemStrs.[i]} {ln} = {seedStrs.[i]};") leafNames
        let allCgs = shareCgs @ leafCgs
        match tryGenRank1StreamJoin ctx binding infos sharedIds leafVarIds callables seedExprs allCgs with
        | Some (Error why) ->
            recordCodegenDecision "stream-terminal" binding.Name (Blade.Effects.Declined why)
                [ "rank1-zarr-joined-scalars: complete admission failed; no producer/storage omission" ]
            (codegenError ctx ind ($"rank-1 streamed reduction join: {why} -- bind with .read for a resident fallback"), ctx')
        | Some (Ok loopCode) ->
            recordFoldForm name "serial: independent original ascending seeded chains" "rank1-zarr-joined-scalars (one arithmetic block loop)"
            let tupleLine = $"""{ind}auto {name} = std::make_tuple({(leafNames |> String.concat ", ")});"""
            let ctxOut = { ctx' with TupleChildren = Map.add name leafNames ctx'.TupleChildren }
            (wrapperLines @ declCode @ loopCode @ [tupleLine], ctxOut)
        | None ->
        let (sm, sp, sNew) = streamedNestSetup ctx.StreamedArrays ind allCgs
        registerStreamBufDecls sNew
        // VECTORIZED FOLD form for the join ("omp implies simd", 2026-10-17):
        // every leg's licence, spent as ONE `omp simd reduction` clause over
        // all the accumulators (gate: `fusedFoldSimdPlan`).
        let effects () =
            joinEffectAudit ctx.DeferredComputations
                ((infos |> List.mapi (fun i info -> ($"leg {i}'s map kernel", info)))
                 @ (sharedIds |> List.map (fun (id, di) -> ($"shared producer '{sharedName id}'", di))))
                (callables |> List.mapi (fun i cb -> ($"leg {i}'s fold kernel", cb)))
        let simdPlan =
            match effects () with
            | Error why -> Error why
            | Ok () -> fusedFoldSimdPlan sm allCgs (List.zip3 leafNames callables elemStrs)
        let (simdFold, form) =
            match simdPlan with
            | Ok p -> (Some p, "simd-reduction")
            | Error _ when not (fpReassocEnabled ()) ->
                (None, $"serial (repro): {leafNames.Length} ascending original chains")
            | Error why -> (None, why)
        // THE THREADED JOIN (G7): the request is a leg's fold kernel `where
        // omp`, or a leaf map's (a leg's or a shared producer's) `omp(...)`
        // covering the outer level -- which selects the traversal and licenses
        // no leg. Every leg's licence is its OWN (`joinedFoldThreadPlan`).
        let request =
            match callables |> List.tryFindIndex (_.IsOmpParallel) with
            | Some i -> Some $"leg {i}'s fold kernel (`where omp`)"
            | None ->
                if allCgs |> List.exists (fun cg -> cg.OmpRequested && (match cg.Bindings with h :: _ -> h.IsParallel | [] -> false)) then
                    Some "a leaf map's `omp(...)` covering the outer level (it selects the traversal; it licenses no leg)"
                else None
        let joinThread =
            joinedFoldThreadPlan (sanitizeCppName name) request simdPlan
                (List.init leafNames.Length (fun i ->
                    (leafNames.[i], callables.[i], elemStrs.[i], seedStrs.[i], wnames.[i])))
                (fun () ->
                    match effects () with
                    | Error why -> Error why
                    | Ok () ->
                    joinThreadAudit ctx.DeferredComputations
                        ((infos |> List.mapi (fun i info -> ($"leg {i}'s map kernel", info)))
                         @ (sharedIds |> List.map (fun (id, di) -> ($"shared producer '{sharedName id}'", di))))
                        (callables |> List.mapi (fun i cb -> ($"leg {i}'s fold kernel", cb))))
        (match joinThread with
         | Some (Ok plan) -> recordFoldForm name (joinedFoldForm plan) $"reduction join of {leafNames.Length} legs (one traversal, one team)"
         | _ -> recordFoldForm name form $"reduction join of {leafNames.Length} legs (one traversal)")
        // `where omp` on a leg's fold kernel that did not get the team: the
        // request's thread half is declined -- with the census line and the
        // first failed condition, never silently (bench gap G7 / F4).
        let ompNote =
            match joinThread with
            | Some (Error why) when callables |> List.exists (_.IsOmpParallel) ->
                [ ind + "/* " + ompSuppressedPhrase why + " */" ]
            | _ -> []
        let loopCode = sp @ ompNote @ genFusedLoopNestStreamedJoined simdFold joinThread sm allCgs ctx.VarNames ctx.Indent false None
        let tupleLine = $"""{ind}auto {name} = std::make_tuple({(leafNames |> String.concat ", ")});"""
        let shareNote =
            if sharedIds.IsEmpty then []
            else [ sprintf "%s// reduction join: %d leg(s), sharing %s per iteration" ind leafNames.Length
                       (sharedIds |> List.map (fst >> sharedName) |> String.concat ", ") ]
        let ctxOut = { ctx' with TupleChildren = Map.add name leafNames ctx'.TupleChildren }
        (wrapperLines @ shareNote @ declCode @ loopCode @ [tupleLine], ctxOut)

and genTupleProjBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (parentExpr: IRExpr) (projIdx: int) (isFlat: bool) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if parent is a deferred computation tuple -- if so, project and defer
    let parentDeferred =
        match parentExpr with
        | IRVar (pid, _) -> Map.tryFind pid ctx.DeferredComputations
        | _ -> None
    match parentDeferred with
    | Some (IRTuple elems) when projIdx < elems.Length ->
        // Parent is a deferred tuple -- project out the element and defer it
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id elems.[projIdx] ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred computation (tuple proj)>"], ctx')
    | Some (IRParallel _ | IRFusion _) ->
        // Parent is a deferred combinator -- defer the projection too
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred computation (proj of combinator)>"], ctx')
    | _ ->
        // Tuple projection -- resolve through TupleChildren map
        let parentName =
            match parentExpr with
            | IRVar (pid, _) -> Map.tryFind pid ctx.VarNames |> Option.defaultValue "_"
            | _ -> "_"
        let parentType = inferExprType parentExpr
        let flatChildren =
            match Map.tryFind parentName ctx.TupleChildren with
            | Some children -> children
            | None -> []

        if isFlat then
            // Flat projection: projIdx is a flat leaf index
            if projIdx < flatChildren.Length then
                let sourceName = flatChildren.[projIdx]
                let code = [$"{ind}auto& {name} = {sourceName};"]
                let extentsAlias =
                    match IR.stripUnits binding.Type with
                    | ArrayElem _ ->
                        [$"{ind}const size_t* {name}_extents = {sourceName}.extents;"]
                    | _ -> []
                let ctx' = addVarName binding.Id name ctx
                let ctx' =
                    match Map.tryFind sourceName ctx'.TupleChildren with
                    | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                    | None -> ctx'
                (code @ extentsAlias, ctx')
            else
                let code = genScalarBinding ctx name binding.Value binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')
        else
            // Structural projection: projIdx is a type-level index
            let ranges = tupleLeafRanges parentType
            let (flatStart, leafCount) =
                if projIdx < ranges.Length then ranges.[projIdx]
                else (projIdx, 1)

            if leafCount > 1 && flatChildren.Length > 0 && flatStart + leafCount <= flatChildren.Length then
                // Sub-tuple: synthesize from flat children range
                let subChildren = flatChildren.[flatStart .. flatStart + leafCount - 1]
                let tupleLine = $"""{ind}auto {name} = std::make_tuple({(subChildren |> String.concat ", ")});"""
                let ctx' = addVarName binding.Id name ctx
                let ctx' = { ctx' with TupleChildren = Map.add name subChildren ctx'.TupleChildren }
                ([tupleLine], ctx')

            elif flatStart < flatChildren.Length then
                // Single leaf at computed position
                let sourceName = flatChildren.[flatStart]
                let code = [$"{ind}auto& {name} = {sourceName};"]
                let extentsAlias =
                    match IR.stripUnits binding.Type with
                    | ArrayElem _ ->
                        [$"{ind}const size_t* {name}_extents = {sourceName}.extents;"]
                    | _ -> []
                let ctx' = addVarName binding.Id name ctx
                let ctx' =
                    match Map.tryFind sourceName ctx'.TupleChildren with
                    | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                    | None -> ctx'
                (code @ extentsAlias, ctx')

            else
                // No TupleChildren -- fall back to std::get
                let code = genScalarBinding ctx name binding.Value binding.Type
                let ctx' = addVarName binding.Id name ctx
                (code, ctx')



and genVarAliasBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (srcId: IRId) : string list * CodeGenContext =
    let (code, ctx') = genVarAliasBindingCore ctx binding builder srcId
    // An alias DECLARED under its own name at main's top level (its source
    // already stored) is a materialized binding: recorded, so the print pass's
    // alias-class rule (gap G60) prints it and does not redeclare it.
    if ctx.Indent = 0 && not (Map.containsKey binding.Id ctx'.DeferredComputations) then
        let cell = forcedDeferredIdsCell ()
        cell.Value <- Set.add binding.Id cell.Value
    (code, ctx')

and genVarAliasBindingCore (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (srcId: IRId) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Check if source is deferred -- propagate deferral. An alias of a
    // deferred map is the SAME deferred value (gap G60, formalism 10.2
    // "Aliases and printing of deferred maps"): it evaluates nothing here.
    // It used to FORCE the source when the alias is assignable (every
    // non-static let is) and deep-copy it, which printed `g` and not `g2`
    // and moved the source's evaluation -- and its first failure -- to the
    // alias. Now an ARRAY alias records its source (DeferredAliasOf): the
    // first forcing use through the alias (forceDeferredArrayInput) forces
    // the source under its own name first, then binds the alias as an
    // assignable copy of those cells, so an element write through one name is
    // never seen through the other and both print.
    // MODULE-LEVEL aliases only (main's top level, not a block or function
    // body let -- those are the MutableArrayLets): a nested-scope assignable
    // alias stays a forcing use of its source (the copy is made at the
    // alias, block-scoped), the interpreter's IRLet arm mirroring it.
    let moduleLevel = ctx.Indent = 0 && not (Set.contains binding.Id ctx.MutableArrayLets)
    match Map.tryFind srcId ctx.DeferredComputations with
    | Some _ when not moduleLevel && (binding.IsMutable || Set.contains binding.Id ctx.MutableArrayLets) ->
        let (fcode, fctx, _) = forceDeferredArrayInput ctx builder ($"{name}__src") (IRVar (srcId, binding.Type))
        let (code, ctx') = genVarAliasBinding fctx binding builder srcId
        (fcode @ code, ctx')
    | Some deferred when moduleLevel && (match binding.Type with ArrayElem _ -> true | _ -> false) ->
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id deferred ctx'.DeferredComputations
                               DeferredAliasOf = Map.add binding.Id srcId ctx'.DeferredAliasOf }
        ([$"{ind}// {name} = <deferred computation (alias of {(Map.tryFind srcId ctx.VarNames |> Option.defaultValue (string srcId))})>"], ctx')
    | Some deferred ->
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id deferred ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred computation (alias)>"], ctx')
    | None ->
        // Let-binding whose value is a reference to a lifted callable.
        // `let f = lambda(...)` lowers to `binding.Value = IRVar(callable.Id,
        // funcType)`; emit a wrapper closure bound to `f` so direct calls
        // `f(args)` work, with the wrapper's signature matching the
        // callable's regular params (captures pulled in by `[&]`, hidden from
        // the call site). Without this, genScalarBinding's fallback would emit
        // `std::function<...> f = __lambda_X;`, which fails to compile since
        // the lifted function has captures as extra positional params.
        //
        // Closure body: `return __lambda_X(regulars..., captures...);`. The
        // wrapper's declared return type defers to `auto` -- the body is a
        // trivial forwarding call, so the compiler infers it precisely from
        // the callable's signature and the user never sees the wrapper's type.
        match resolveCallable binding.Value with
        | Some callable ->
            // The wrapper forwards every capture BY NAME, so a still-DEFERRED
            // one has to be materialized first -- the same capture-boundary
            // forcing genComputeBinding does for a combinator's kernel (see
            // collectDeferredKernelCaptures). Runs before VarNames is read
            // below, since forcing can rename nothing but must be in scope.
            let (capForceCode, ctx) =
                collectDeferredKernelCaptures ctx binding.Value
                |> forceDeferredBindingIds ctx builder ($"{name}__cap")
            let safeName = sanitizeCppName callable.Name
            let paramSig =
                callable.Params
                |> List.map (fun p ->
                    match p.Type with
                    | ArrayElem arr -> $"{(cppArrayTypeStr arr)} {p.Name}"
                    | _ -> $"{(irTypeToCpp p.Type)} {p.Name}")
                |> String.concat ", "
            let regularArgs = callable.Params |> List.map (_.Name)
            let captureArgs = captureForwardArgs ctx.VarNames callable.Captures
            let allArgs = (regularArgs @ captureArgs) |> String.concat ", "
            // Wrapper type: `std::function<Ret(P1, P2, ...)>`. Explicit
            // type per the codegen convention (auto reserved for thin
            // forwarding wrappers prefixed `__wrap_*`). std::function
            // is required when the wrapper itself flows into another
            // function's capture slot -- the receiving function takes
            // captures as `std::function<...>&`, which can't bind to
            // an rvalue temporary if we emit raw closures via auto.
            // std::function gives the binding a stable lvalue type
            // that matches the capture-slot signature.
            let paramTypes =
                callable.Params
                |> List.map (fun p ->
                    match p.Type with
                    | ArrayElem arr -> cppArrayTypeStr arr
                    | _ -> irTypeToCpp p.Type)
            let retTypeStr =
                match callable.RetType with
                | ArrayElem arr -> cppArrayTypeStr arr
                | t -> irTypeToCpp t
            let funcTypeStr =
                $"""std::function<{retTypeStr}({(String.concat ", " paramTypes)})>"""
            // The binding is a closure VALUE (it may be returned or stored),
            // so it copies its captures unless one is rebound
            // (closureValueCapture, "CLOSURE VALUES CAPTURE BY VALUE").
            let (clause, spec) = closureValueCapture callable.Captures
            let code = [$$"""{{ind}}{{funcTypeStr}} {{name}} = {{clause}}({{paramSig}}){{spec}} { return {{safeName}}({{allArgs}}); };"""]
            let ctx' = addVarName binding.Id name ctx
            (capForceCode @ code, ctx')
        | None ->
            // Plain variable alias -- may be aliasing a tuple, propagate children
            let srcName = Map.tryFind srcId ctx.VarNames |> Option.defaultValue ""
            let hasTupleChildren = Map.containsKey srcName ctx.TupleChildren
            // An ASSIGNABLE binding of an existing DENSE array (`let a = Z` /
            // `let mut a = Z`; block-level via ctx.MutableArrayLets,
            // top-level via IsMutable -- TypeCheck marks every non-static let
            // assignable, so both spellings admit `a(i) = ...`) deep-copies
            // the storage: the wrapper-by-value alias below shares Z's data
            // pointer, so mutations through `a` would silently corrupt `Z`.
            // Compound/ragged/dep-idx initializers keep the historical alias
            // (no dense .extents/pool contract; no assignment path exercises
            // them today).
            match Map.tryFind srcName ctx.GroupedArrays with
            | Some gkName ->
                // A GROUPED array (group_by's row table over one CSR pool). Its
                // row LENGTHS live in the grouping's offsets, not in the value
                // -- the value's inner extent is the ragged placeholder 0 -- so
                // the dense deep copy below copied zero cells and left an EMPTY
                // rank-2 array: `let h = g` printed `h = [[], [], []]` beside
                // g's real rows, and a peel over h read nothing. Copy the pool
                // and rebuild the row table instead (a copy, not an alias, for
                // the reason the dense arm copies: the binding is assignable),
                // and carry the grouping so peels and print read h like g.
                // (The alias is a USE of g, so g's gather is never elided here;
                // see computeExtentsOnlyGroupBys.)
                let elemStr =
                    match binding.Type with
                    | ArrayElem at -> elemTypeToCpp at.ElemType
                    | _ -> "double"
                let extentsDecl =
                    fst (emitGroupedExtentsTable ind (name + "_extents") gkName)
                let code =
                    [ $"{ind}// {name} = {srcName}: copy of a grouped array (grouping {gkName})" ]
                    @ extentsDecl
                    @ [ $$"""{{ind}}Array<{{elemStr}}*, 1> {{name}} = { new {{elemStr}}*[{{gkName}}__ngroups], {{name}}_extents };"""
                        $"{ind}{elemStr}* {name}__pool = new {elemStr}[{gkName}__offsets[{gkName}__ngroups]];"
                        $$"""{{ind}}for (size_t __g = 0; __g < {{gkName}}__ngroups; __g++) {"""
                        $"{ind}    {name}[__g] = {name}__pool + {gkName}__offsets[__g];"
                        $"{ind}    for (size_t __k = 0, __n = {gkName}__offsets[__g + 1] - {gkName}__offsets[__g]; __k < __n; __k++) {name}[__g][__k] = {srcName}[__g][__k];"
                        $"{ind}}}" ]
                registerShapedAlloc name "deallocate_ragged_storage" ($"{name}.data, {name}__pool")
                noteGroupedBinding binding.Id name gkName
                let ctx' = addVarName binding.Id name ctx
                (code, { ctx' with GroupedArrays = Map.add name gkName ctx'.GroupedArrays })
            | None ->
            // The read-only alias plan (Optimize.planAliasElision, rule
            // `transpose-normal-form v1`): neither name is ever written, so
            // the copy is unobservable and the binding shares the source's
            // wrapper. Main's top level only (nothing is scope-freed there,
            // so the shared pool outlives every reader); anywhere else the
            // copy stays.
            let readOnlyAlias =
                Blade.Optimize.AliasElideTable.contains binding.Id && (currentAllocScope ()).IsNone
            let mutArrayCopy =
                if readOnlyAlias then None
                elif binding.IsMutable || Set.contains binding.Id ctx.MutableArrayLets then
                    match binding.Type with
                    | ArrayElem at when not (isCompoundArrayType at)
                                       && not (isRaggedArrayType at)
                                       && not (isDepIdxArrayType at) ->
                        materializeArrayCopyForm emptySubst ctx.VarNames name (elemTypeToCpp at.ElemType) binding.Value
                    | _ -> None
                else None
            match mutArrayCopy with
            | Some (copyStmts, copyAllocs) ->
                // Deterministic deallocation, site 6: the `let (mut) a = Z` deep
                // copy. ONE registration covers BOTH mut paths -- genFuncBody's mut
                // arm routes through genBinding to here. Site 7 folded the former
                // hand-rebuilt registration into the builder's OWN descriptor:
                // materializeArrayCopyForm picks (spec, SYMM) from the SOURCE type
                // and its rank with `max 1 ix.Rank`, and now reports exactly that,
                // so the earlier dense-and-unsymmetric restriction (which existed
                // only because the consumer had to guess) is gone. Extents are a
                // STACK array here, so nothing is owned.
                registerMaterializedAllocs copyAllocs
                let ctx' = addVarName binding.Id name ctx
                (copyStmts |> List.map (fun s -> ind + s), ctx')
            | None ->
                // Use auto& when source has flat TupleChildren to avoid type mismatch
                let code =
                    if hasTupleChildren then
                        [$"{ind}auto& {name} = {srcName};"]
                    else
                        genScalarBinding ctx name binding.Value binding.Type
                let code =
                    if readOnlyAlias then [ $"{ind}// {name} = {srcName}: read-only alias, no pool copy (transpose-normal-form v1)" ] @ code
                    else code
                let ctx' = addVarName binding.Id name ctx
                let ctx' =
                    match Map.tryFind srcName ctx'.TupleChildren with
                    | Some children -> { ctx' with TupleChildren = Map.add name children ctx'.TupleChildren }
                    | None -> ctx'
                (code, ctx')



and genChoiceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (left: IRExpr) (right: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Only defer when children are computation-level (not scalar)
    let isCompExpr e = match e with IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
    if isCompExpr left || isCompExpr right then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred choice>"], ctx')
    else
        // Scalar choice: generate directly
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



/// `let C = A <|:> B` binding site. The operands are arrays (typecheck
/// guarantees it -- scalars steer to <|>), and the combinator is lazy like the
/// rest of its family: defer, and materialize at |> compute
/// (genFallbackMaterialize via genComputeBinding's IRFallback arm).
and genFallbackBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (left: IRExpr) (right: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let ctx' = addVarName binding.Id name ctx
    let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
    ([$"{ind}// {name} = <deferred fallback>"], ctx')

/// Materialize `A <|:> B` (allocated-fallback, formalism 2.6): read A where
/// A's STORAGE holds the cell, else B. Two storage regimes, one judgment:
///   * compound-left: the CompoundIdx mask IS the allocation record. Iterate
///     the dense underlying space (B's extents = result extents); present
///     lead-tuples read A's compact buffer (linearize * trailing_stride),
///     absent ones read B. An allocated zero survives -- the distinguisher
///     from <|>'s value-keyed zero test.
///   * dense-left: allocation = the nested-pointer chain, checked per curry
///     level by the fallback_copy<> runtime helper (nullptr-robust; compiler-
///     built arrays are fully allocated, partially-allocated ones arrive via
///     the C++-level partial-depth allocation API).
/// The result is always a fully-allocated dense array.
and genFallbackMaterialize (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (left: IRExpr) (right: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Operand prep: named materialized vars pass through; anything else (a
    // deferred-computation var, an inline combinator, or a nested IRFallback
    // from the object_for(<|:>) fold) materializes into a synthetic
    // sub-binding first (the genChoiceBinding pattern).
    let prepOperand (ctxIn: CodeGenContext) (e: IRExpr) (tag: string) : string list * CodeGenContext * string * IRType =
        match e with
        | IRVar (id, ty) when Map.containsKey id ctxIn.VarNames && not (Map.containsKey id ctxIn.DeferredComputations) ->
            ([], ctxIn, Map.find id ctxIn.VarNames, ty)
        | _ ->
            let subTy = match e with IRVar (_, ty) -> ty | _ -> binding.Type
            let subName = $"{name}__{tag}"
            let subBinding = { Id = builder.FreshId(); Name = subName; Type = subTy
                               Value = IRCompute e; IsConst = true; IsMutable = false }
            let (code, ctx') = genBinding ctxIn subBinding builder
            (code, ctx', subName, subTy)
    let (codeL, ctxL, nameL, tyL) = prepOperand ctx left "lhs"
    let (codeR, ctxR, nameR, _tyR) = prepOperand ctxL right "rhs"
    match binding.Type with
    | ArrayElem resArr ->
        let rank = arrayRank resArr
        let elemType = elemTypeToCpp resArr.ElemType
        let leftCompound =
            match tyL with
            | ArrayElem aL when isCompoundArrayType aL -> Some aL
            | _ -> None
        // Result extents come from the operand that spans the dense space:
        // the left array for dense-left (operands are type-unified), the
        // RIGHT array for compound-left (the left is compact storage).
        let extentsSrc = match leftCompound with Some _ -> nameR | None -> nameL
        // Return-extent ABI (commit 7905b36, extended). This used to be a bare
        // ALIAS -- `const size_t* name_extents = <operand>.extents;` -- which
        // made the result's SHAPE a pointer into an operand's table. Fine while
        // the result was consumed in the frame that built it, and dangling the
        // moment it crossed a return: the frees run in reverse order, so the
        // lender is deleted while the escaping wrapper still points at its
        // table. That was invisible until the unified statement-shaped return
        // arm made `function f(x, y) = { x <|:> y }` compile at all.
        //
        // `emitExtentsTable` is the same helper the 14 materialize*Form
        // builders use. The dims are runtime `.extents[d]` reads, never
        // literals, so it takes the `new size_t[R]` branch and hands back an
        // owned name -- which rides on the array's own descriptor via
        // registerPoolAlloc's `ownedExtents`, exactly as 7905b36 established.
        // A separately tracked table would be freed out from under the very
        // wrapper that just escaped, since the return suppression is a
        // whole-token match on the rendered return text and can only ever see
        // `<name>`, never `<name>_extents`.
        let extentsName = name + "_extents"
        let dims = [ for d in 0 .. rank - 1 -> ($"{extentsSrc}.extents[{d}]", false) ]
        let (extentsDecl, ownedExtents) = emitExtentsTable ind extentsName rank dims
        let allocDecl = $"{ind}Array<{elemType}, {rank}> {name} = {{ allocate<typename promote<{elemType}, {rank}>::type, nullptr>({name}_extents), {name}_extents }};"
        // Deterministic deallocation, site 5d: `<|:>` result. Always a
        // fully-allocated dense array, and now the owner of its own extents
        // table rather than a borrower of an operand's.
        if isFreeableDenseArrayType resArr then
            registerPoolAlloc AllocDense elemType rank "nullptr" extentsName name ownedExtents
        let indD d = String.replicate d "    "
        let idxVar i = $"__fb{i}"
        let subscript n = [for i in 0 .. n - 1 -> $"[{(idxVar i)}]"] |> String.concat ""
        let bodyLines =
            match leftCompound with
            | None ->
                // Dense-left: one nullptr-robust recursive copy.
                [$"{ind}nested_array_utilities::fallback_copy<{elemType}, {rank}>({name}.data, {nameL}.data, {nameR}.data, {name}_extents);"]
            | Some aL ->
                let leadRank =
                    aL.IndexTypes
                    |> List.tryFind (fun ix -> ix.IxKind = IxKCompound)
                    |> Option.map (_.Rank)
                    |> Option.defaultValue 1
                let trailingCount = rank - leadRank
                // Runtime shape guard: the mask's underlying extents must
                // agree with the dense right operand (statically only ranks
                // and element types are checkable -- the mask is runtime data).
                let guards =
                    [ for d in 0 .. leadRank - 1 ->
                        $"{ind}if ({nameL}.idx->extents[{d}] != {name}_extents[{d}]) {{ blade_rt::panic(\"BL8001\", \"<|:>: compound left operand's underlying extents disagree with the dense right operand's shape\", {(declPanicArgs ())}); }}" ]
                let mutable lines = guards
                let mutable depth = ctx.Indent
                for i in 0 .. leadRank - 1 do
                    lines <- lines @ [$$"""{{(indD depth)}}for (size_t {{(idxVar i)}} = 0; {{(idxVar i)}} < {{name}}_extents[{{i}}]; {{(idxVar i)}}++) {"""]
                    depth <- depth + 1
                let leadTuple =
                    [for i in 0 .. leadRank - 1 -> idxVar i] |> String.concat ", "
                lines <- lines @ [$"{(indD depth)}std::array<size_t, {leadRank}> __fb_t{{{{ {leadTuple} }}}};"]
                // Row-major flatten of the trailing coordinate inside a
                // present cell's contiguous block.
                let trailOffsetExpr =
                    if trailingCount = 0 then ""
                    else
                        [leadRank .. rank - 1]
                        |> List.fold (fun acc j ->
                            if acc = "" then idxVar j
                            else $"({acc} * {name}_extents[{j}] + {(idxVar j)})") ""
                let readCompact =
                    if trailingCount = 0 then $"{nameL}.data[{nameL}.idx->linearize(__fb_t)]"
                    else $"{nameL}.data[{nameL}.idx->linearize(__fb_t) * {nameL}.trailing_stride + {trailOffsetExpr}]"
                let emitTrailingAssign (baseDepth: int) (rhs: string) : string list =
                    let mutable ls = []
                    let mutable d = baseDepth
                    for j in leadRank .. rank - 1 do
                        ls <- ls @ [$$"""{{(indD d)}}for (size_t {{(idxVar j)}} = 0; {{(idxVar j)}} < {{name}}_extents[{{j}}]; {{(idxVar j)}}++) {"""]
                        d <- d + 1
                    ls <- ls @ [$"{(indD d)}{name}{(subscript rank)} = {rhs};"]
                    for _ in leadRank .. rank - 1 do
                        d <- d - 1
                        ls <- ls @ [$"{(indD d)}}}"]
                    ls
                lines <- lines @ [$$"""{{(indD depth)}}if ({{nameL}}.idx->present(__fb_t)) {"""]
                lines <- lines @ emitTrailingAssign (depth + 1) readCompact
                lines <- lines @ [$"{(indD depth)}}} else {{"]
                lines <- lines @ emitTrailingAssign (depth + 1) ($"{nameR}{(subscript rank)}")
                lines <- lines @ [$"{(indD depth)}}}"]
                for _ in 0 .. leadRank - 1 do
                    depth <- depth - 1
                    lines <- lines @ [$"{(indD depth)}}}"]
                lines
        let ctx' = addVarName binding.Id name ctxR
        (codeL @ codeR @ [""; $"{ind}// <|:> allocated-fallback: {nameL} where allocated, else {nameR}"] @ extentsDecl @ [allocDecl] @ bodyLines, ctx')
    | t ->
        let code = codegenError ctx ind (sprintf "<|:>: binding type is not an array (got %A) -- likely a typechecker or IR bug" t)
        (codeL @ codeR @ code, addVarName binding.Id name ctxR)

and genGuardBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (body: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Guard wrapping a computation: defer for later materialization via |> compute
    // Recurse through nested guards to check if the leaf body is a computation
    let rec leafIsComputation e =
        match e with
        | IRGuard (_, inner) -> leafIsComputation inner
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRSequence _ -> true
        | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
        | _ -> false
    if leafIsComputation body then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred guard>"], ctx')
    else
        // Scalar guard: generate directly
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genSequenceBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (elems: IRExpr list) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Defer: sequence is a flat n-ary parallel, materialized by |> compute
    let isCompExpr e = match e with IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ | IRChoice _ | IRFallback _ | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRGuard _ | IRSequence _ -> true | IRVar _ -> true | _ -> false
    if elems |> List.exists isCompExpr then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred sequence>"], ctx')
    else
        // All scalars: generate as tuple
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genForRangeBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (vid: IRId) (lo: IRExpr) (hi: IRExpr) (body: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Force any deferred producer this loop reads positionally BEFORE the loop
    // header, so the materialization is not re-run each iteration. When this
    // for-range is nested inside a block, genLetChainBinding already forced it
    // (dropped from DeferredComputations) and the collector finds nothing.
    let (forceCode, ctx) = forceDeferredPositionalReads ctx builder ($"{name}__def") body
    // Imperative for-range loop. E-G5 (Blade.Optimize.RecStepTable): the
    // CELL loop of a threaded step runs the worker's chunk, whose bounds the
    // time loop's region handed over through the entry.
    let (loStr, hiStr) =
        match Blade.Optimize.RecStepTable.tryLoop vid with
        | Some entry when entry.ChunkBounds.Value.IsSome -> entry.ChunkBounds.Value.Value
        | _ -> (exprToCppCtx ctx lo, exprToCppCtx ctx hi)
    // `__kv`, not `__k`: clang predefines `__k8` / `__k8__` for the generic
    // x86-64 target, so a `__k8` counter failed every memcheck (clang64) build
    // whose var id reached 8 (g++ under -march=native defines neither).
    let varName = $"__kv{vid}"
    let innerCtx = addVarName vid varName ctx
    // Unroll the body IRLet chain into statements
    let (bodyLets, _bodyFinal) = unrollLetChain body
    // FreshPool calls in ARGUMENT position get their own lets here exactly as
    // in genFuncBody, so a per-iteration `g(f(x))` temporary is registered
    // and recycled instead of leaking once per trip.
    let bodyLets =
        bodyLets |> List.collect (fun (id, v) ->
            let (hv, v') = hoistFreshPoolCallArgs builder v
            hv @ [(id, v')])
    // E-G5: the TIME loop of a threaded `let rec` step -- one persistent team
    // (or the plain loop behind a census line).
    let recStep =
        match Blade.Optimize.RecStepTable.tryStep vid with
        | Some entry -> genRecStepTimeLoop ctx binding builder entry vid loStr hiStr bodyLets _bodyFinal forceCode
        | None -> Error []
    match recStep with
    | Ok result -> result
    | Error censusLines ->
    let forceCode = forceCode @ censusLines
    // Deterministic deallocation: ONE loop-body frame, pushed AFTER
    // forceDeferredPositionalReads above (whose materializations belong to the
    // OUTER scope and must not be freed per iteration). Registrations always land
    // on the top frame, so the frees emitted at the bottom of the body can only
    // name storage THIS iteration allocated -- structurally, with no id analysis.
    let escapes = computeScopeEscapes ctx LoopScope bodyLets
    // B2r for a recursion (round-2 cell epilogues; Blade.Optimize.CellFoldTable):
    // this loop is a row-fold-fusion step's slice-write-fused CELL loop whose
    // row is folded by `prodsum(row, row)` right after it. When the build
    // renders that prodsum as the serial IIFE (`T __ps = 0; __ps += r[t] *
    // r[t]`, CodeGenExpr's IRProdSum arm, !fpReassocEnabled), the same chain
    // advances here, one `+=` per cell right behind the cell's store
    // (register-consume: the stored local itself), and the fold let then binds
    // the chain instead of re-reading the row. The licensed build's prodsum is
    // the K-lane tree, which this chain does not reproduce: declined.
    let cellChain =
        match Blade.Optimize.CellFoldTable.tryLoop vid with
        | None -> None
        | Some entry ->
            let declined (why: string) =
                Blade.Optimize.recordCellEpilogue entry.Subject (Blade.Effects.Declined why)
                    [ "the row's prodsum runs on the finished row after the step" ]
                None
            // E-G5: inside a threaded step's team the row chain cannot
            // advance per cell across workers (the time loop's region folds
            // the row its own way); the serial fallbacks keep the chain.
            let threaded =
                match Blade.Optimize.RecStepTable.tryLoop vid with
                | Some e -> e.ChunkBounds.Value.IsSome
                | None -> false
            if threaded then
                declined "the step is threaded (rec-step-parallel): the row chain cannot advance per cell across workers; the row is folded per worker chunk inside the team or on complete rows after it"
            elif fpReassocEnabled () then
                declined (Blade.Optimize.cellEpilogueLicensedRowDecline "the finished row")
            else
                match List.tryLast bodyLets, _bodyFinal with
                | Some (_, IRAssign (IRIndex (IRVar (b, bufT), [ _; IRVar (k2, _) ], _), IRVar (sv, _))), IRLit IRLitUnit
                    when b = entry.BufferId && k2 = vid && sv = entry.StoredVar ->
                    (match bufT with
                     | ArrayElem at -> Some (entry, elemTypeToCpp at.ElemType)
                     | _ -> declined "the trajectory buffer's type is not an array")
                | _ -> declined "the cell loop's shape changed after planning (its last statement is not the planned store)"
    let cfcName = match cellChain with Some (e, _) -> $"__cfc_{e.FoldLetId}" | None -> ""
    let chainBlock = match cellChain with Some _ -> cellEpiBlockR () | None -> 0L
    let allocDepth = allocScopeDepth ()
    pushAllocScope SLoop escapes
    let (bodyCode, frees) =
        try
            let (code, _) =
                bodyLets |> List.fold (fun (accCode, accCtx) (id, value) ->
                    // See setAllocOwner: this let owns every allocation emitted
                    // while it renders, sub-temporaries included.
                    setAllocOwner (Some id)
                    let tempName = $"__v{id}"
                    // B2r, the fold-let half: a planned prodsum whose cell loop
                    // (emitted just above, in this body) carried the chain.
                    match Blade.Optimize.CellFoldTable.tryFoldLet id with
                    | Some entry when entry.EmittedCpp.Value.IsSome ->
                        let acc = entry.EmittedCpp.Value.Value
                        entry.EmittedCpp.Value <- None
                        let elem =
                            match inferExprType value with
                            | IRTScalar et -> primTypeToCpp et
                            | t -> irTypeToCpp t
                        let line = $"{ind}    {elem} {tempName} = {acc};   // cell epilogue: the row's prodsum chain ran inside the cell loop"
                        (accCode @ [ line ], addVarName id tempName accCtx)
                    | _ ->
                    let tempBinding = {
                        Id = id; Name = tempName; Type = inferExprType value
                        Value = value; IsConst = true; IsMutable = false
                    }
                    let (code, ctx') = genBinding { accCtx with Indent = ctx.Indent + 1 } tempBinding builder
                    // B2r, the cell-loop half: right behind the planned store.
                    let chainLine =
                        match cellChain, value with
                        | Some (entry, _), IRAssign _ when (List.last bodyLets |> fst) = id ->
                            let sv = Map.tryFind entry.StoredVar ctx'.VarNames |> Option.defaultValue $"__v{entry.StoredVar}"
                            let step =
                                match entry.Term with
                                | Blade.Optimize.Square -> $"{cfcName} += {sv} * {sv};"
                                | Blade.Optimize.Plain -> $"{cfcName} += {sv};"
                            [ $"{ind}    {step}" ]
                        | _ -> []
                    (accCode @ code @ chainLine, { ctx' with Indent = ctx.Indent })
                ) ([], innerCtx)
            setAllocOwner None
            (code, popAllocScopeFrees (ind + "    "))
        finally truncateAllocScopeStack allocDepth
    let (chainHead, loopHead, loopTail) =
        match cellChain with
        | Some (entry, elem) ->
            entry.EmittedCpp.Value <- Some cfcName
            let blocks = if chainBlock > 0L then $"mini-blocks of {chainBlock}" else "one fused loop"
            Blade.Optimize.recordCellEpilogue entry.Subject Blade.Effects.Applied
                [ $"row chain interleave ({blocks}): the step's prodsum(row, row) chain advances on each cell right behind its store (register-consume) into `{cfcName}`; the fold let binds the chain instead of re-reading the row"
                  "strict-legal: the same products of the same stored values added in the same ascending order to the same +0; the recursion and the outer fold are unchanged" ]
            let head = [ $"{ind}{elem} {cfcName} = 0;   // cell epilogue: this row's prodsum chain (Blade.Optimize.CellFoldTable)" ]
            if chainBlock > 0L then
                (head,
                 [ $"{ind}for (int64_t __cbk = {loStr}; __cbk < {hiStr}; __cbk += {chainBlock}L) {{"
                   $"{ind}const int64_t __cbe = __cbk + {chainBlock}L < {hiStr} ? __cbk + {chainBlock}L : {hiStr};"
                   forLoopFrom ind varName "__cbk" "__cbe" ],
                 [ $"{ind}}}" ])
            else (head, [ forLoopFrom ind varName loStr hiStr ], [])
        | None -> ([], [ forLoopFrom ind varName loStr hiStr ], [])
    let code =
        forceCode
        @ chainHead
        @ loopHead
        @ bodyCode
        @ frees
        @ [$"{ind}}}"]
        @ loopTail
    let ctx' = addVarName binding.Id name ctx
    (code, ctx')



/// Wave 3, E-G5 (bench gap G5; `Blade.Optimize.RecStepTable`): the TIME loop
/// of a `let rec` whose step's final map carried `where omp` -- the loop
/// whose body is `[lag row views; the slice-write-fused cell loop; (row view;
/// p = K(row); acc <- F(acc, p))?]`. Emitted as ONE team over all the steps
/// with contiguous x ownership fixed for the whole loop and NO per-step
/// synchronization: a worker writes only its own cells of row n and reads
/// only its own cells of strictly earlier rows (the optimizer's proof), so
/// its own program order is the only ordering the cells need; row 0 was
/// written before the team. A team entry or a barrier costs ~40 us on the
/// bench box (libgomp, Windows): 300 of either would be the whole budget.
///
/// The fused row fold, when present:
///   * licensed build AND K = prodsum(row, row): each worker folds its own
///     chunk of the row it just wrote (L1-hot) with the self-dot's K-lane
///     form into `part[n][t]`; after the team each row's partials are
///     combined ascending from +0 (the Path B discipline, deterministic for a
///     fixed team) and the time chain `acc <- F(acc, p_n)` runs ascending from
///     its seed -- the licence is the row's own builtin `+`;
///   * otherwise (BLADE_FP_REPRO=1, `where repro`, any other K): a strict
///     post-pass -- K applied to each COMPLETE row into an indexed slot, rows
///     in parallel, each row's chain whole and in K's own text -- then the
///     time chain over the slots in order: byte-identical to the serial
///     program for every team size (G35's row-slot discipline).
/// Ok = the emission; Error = the census lines to put before the plain loop
/// (threads suppressed, a shape the planner did not see).
and genRecStepTimeLoop (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (entry: Blade.Optimize.RecStepEntry)
                       (vid: IRId) (loStr: string) (hiStr: string) (bodyLets: (IRId * IRExpr) list) (bodyFinal: IRExpr)
                       (forceCode: string list) : Result<string list * CodeGenContext, string list> =
    let ind = indentStr ctx
    let name = bindingCppName binding
    let subject = entry.Subject
    let serial = "the recursion's steps run on the plain serial time loop"
    entry.Decided.Value <- true
    let declined (why: string) (census: string option) =
        recordCodegenDecision "rec-step-parallel" subject (Blade.Effects.Declined why) [ serial ]
        Error (match census with Some c -> [ $"{ind}// {c}" ] | None -> [])
    if not (ompThreadEmissionEnabled ()) then
        // The request was inlined away with the map: the census line is this
        // loop's (the invariant: a dropped omp is never silent).
        declined (ompThreadsSuppressedReason ()) (Some (ompSuppressedPhrase (ompThreadsSuppressedReason ())))
    else
    // The body's shape, re-verified here (fail closed): a leading run of
    // trajectory row views, the cell loop, then the fold triple or nothing.
    let isRowView (v: IRExpr) =
        match v with
        | IRIndex (IRVar (b, _), [ _ ], _) when b = entry.BufferId -> true
        | _ -> false
    let rec splitLags acc (ls: (IRId * IRExpr) list) =
        match ls with
        | (id, v) :: t when isRowView v -> splitLags ((id, v) :: acc) t
        | _ -> (List.rev acc, ls)
    let lagLets, afterLags = splitLags [] bodyLets
    // The cell loop's body: scalar lets (every worker private is a scalar),
    // ending in the one store `BUF[n][k] = local` (Codex objection 7).
    let cellBodyOk (cellBody: IRExpr) =
        let (lets, fin) = unrollLetChain cellBody
        match List.tryLast lets, fin with
        | Some (_, IRAssign (IRIndex (IRVar (b, _), [ IRVar (n, _); IRVar (k2, _) ], _), IRVar (sv, _))), IRLit IRLitUnit
            when b = entry.BufferId && n = vid && k2 = entry.CellLoopVar && lets |> List.exists (fun (id, _) -> id = sv) ->
            lets |> List.take (lets.Length - 1)
            |> List.forall (fun (_, v) -> match inferExprType v with IRTScalar _ -> true | _ -> false)
        | _ -> false
    let shape =
        match afterLags, bodyFinal with
        | (wId, (IRForRange (kv, IRLit (IRLitInt 0L), IRLit (IRLitInt cells), cellBody) as cellLoop)) :: tail, IRLit IRLitUnit
            when kv = entry.CellLoopVar && cells = entry.Cells ->
            if not (cellBodyOk cellBody) then Error "the cell loop's body is not scalar lets ending in the planned row store" else
            (match entry.Fold.Value, tail with
             | None, [] -> Ok ((wId, cellLoop), None)
             | Some f, [ (r, rv); (p, pv); (a, av) ] when r = f.RowLetId && p = f.FoldLetId && a = f.AssignLetId && isRowView rv ->
                Ok ((wId, cellLoop), Some (f, (r, rv), (p, pv), (a, av)))
             | _ -> Error "the time loop's statements after the cell loop are not the planned row fold")
        | _ -> Error "the time loop's statements are not the planned lag views, cell loop and row fold"
    match shape with
    | Error why -> declined ("the loop's shape changed after planning: " + why) (Some (ompSuppressedPhrase ("rec-step-parallel: " + why)))
    | Ok ((wId, cellLoop), fold) ->
    let bufName = Map.tryFind entry.BufferId ctx.VarNames |> Option.defaultValue $"__v{entry.BufferId}"
    let elem =
        match fold with
        | Some (_, (_, rv), _, _) ->
            (match inferExprType rv with
             | ArrayElem at -> elemTypeToCpp at.ElemType
             | _ -> "double")
        | None -> "double"
    let strict = not (fpReassocEnabled ()) || (Blade.LinAlgPatterns.reproScopeDepth ()).Value > 0
    // The in-team partial is the self-dot's K-lane form over the worker's
    // chunk: a licensed build, a Float64 / Float32 row, lanes not turned off
    // (BLADE_FOLD_LANES), and neither K nor F `where repro` (objection 11).
    let licensedSquare =
        match fold with
        | Some (f, _, _, _) ->
            not strict && not f.StrictOnly && f.Term = Some Blade.Optimize.Square
            && foldVecElem elem && foldLanesMode () <> FoldLanesOff
        | None -> false
    // The strict post-pass runs rows in PARALLEL only under the outer fold
    // kernel's own `where omp` (the row-slot gate, G35): the step's spatial
    // request never licenses the row axis (objection 10).
    let parallelRows =
        match fold with
        | Some (f, _, _, _) -> f.OuterRequest && f.Term = Some Blade.Optimize.Square
        | None -> false
    let nVar = $"__kv{vid}"   // `__kv`: `__k8` is a clang predefined macro (see the for-in counter)
    let t = $"__rsT_{vid}"
    let rn = $"__rsn_{vid}"
    let part = $"__rspart_{vid}"
    let nt = $"__rsnt_{vid}"
    let slot = $"__rsslot_{vid}"
    let i1 = ind + "    "
    let i2 = i1 + "    "
    let i3 = i2 + "    "
    (ompApiUsedCell ()).Value <- true
    (foldLanesUsedCell ()).Value <- true
    // Render a let chain at `indent` with the loop variable bound; a let in
    // `overrides` is bound to that C++ name instead of rendered.
    let renderLets (ctx0: CodeGenContext) (indent: int) (lets: (IRId * IRExpr) list) (overrides: Map<IRId, string>) =
        let (code, ctx') =
            lets |> List.fold (fun (accCode, accCtx) (id, value) ->
                match Map.tryFind id overrides with
                | Some nm -> (accCode, addVarName id nm accCtx)
                | None ->
                    setAllocOwner (Some id)
                    let tempName = $"__v{id}"
                    let tempBinding = { Id = id; Name = tempName; Type = inferExprType value; Value = value; IsConst = true; IsMutable = false }
                    let (code, ctx') = genBinding { accCtx with Indent = indent } tempBinding builder
                    (accCode @ code, { ctx' with Indent = ctx0.Indent })) ([], ctx0)
        setAllocOwner None
        (code, ctx')
    // One loop-body frame per rendered loop body, as the plain loop keeps
    // (the bodies here allocate nothing: views, scalars, the cell loop's own
    // frame); the frees are emitted where the plain loop would emit them.
    let withLoopFrame (lets: (IRId * IRExpr) list) (indent: string) (render: unit -> string list) =
        let escapes = computeScopeEscapes ctx LoopScope lets
        let allocDepth = allocScopeDepth ()
        pushAllocScope SLoop escapes
        try
            let code = render ()
            (code, popAllocScopeFrees indent)
        finally truncateAllocScopeStack allocDepth
    let innerCtx = addVarName vid nVar ctx
    // The region's loop body: lag views, the cell loop over the chunk, the
    // licensed partial.
    let (regionBody, regionFrees) =
        withLoopFrame (lagLets @ [ (wId, cellLoop) ]) i3 (fun () ->
            let (lagCode, ctx1) = renderLets innerCtx (ctx.Indent + 3) lagLets Map.empty
            entry.ChunkBounds.Value <- Some ("(int64_t)__rlo", "(int64_t)__rhi")
            let (cellCode, _) =
                try renderLets ctx1 (ctx.Indent + 3) [ (wId, cellLoop) ] Map.empty
                finally entry.ChunkBounds.Value <- None
            let partial =
                if licensedSquare then
                    // The configured lane count (BLADE_FOLD_LANES=4|8), as the
                    // prodsum emitter's own self-dot takes it.
                    let lanes = foldVecLanes VecSq
                    [ i3 + $"{part}[(size_t){nVar} * (size_t){t} + (size_t)__rt] = __rhi > __rlo ? blade_fold::sq<{elem}, {lanes}>({bufName}.data[{nVar}] + __rlo, __rhi - __rlo) : ({elem})0;   // this worker's chunk of the row it just wrote" ]
                else []
            lagCode @ cellCode @ partial)
    let region =
        [ ind + $"{{   // rec-step-parallel v1: one persistent team over the recursion's steps -- fixed contiguous x ownership, no per-step synchronization (a worker reads only its own cells of earlier rows)"
          i1 + $"const size_t {rn} = (size_t){entry.Cells}L;"
          i1 + $"int {t} = blade_omp_get_max_threads();"
          i1 + $"if ((size_t){t} > {rn}) {t} = (int){rn};"
          i1 + $"if ({t} < 1) {t} = 1;" ]
        @ (if licensedSquare then
               [ i1 + $"{elem}* {part} = new {elem}[(size_t)({hiStr}) * (size_t){t}]();   // per-row, per-worker partials of the row self-dot"
                 i1 + $"int {nt} = 1;" ]
           else [])
        @ [ i1 + $"BLADE_OMP_PARALLEL_NUM_THREADS({t})"
            i1 + "{"
            i2 + "const int __rnt = blade_omp_get_num_threads();"
            i2 + "const int __rt = blade_omp_get_thread_num();"
            i2 + $"const size_t __rlo = blade_fold::chunk_lo({rn}, (size_t)__rt, (size_t)__rnt);"
            i2 + $"const size_t __rhi = blade_fold::chunk_lo({rn}, (size_t)__rt + 1, (size_t)__rnt);" ]
        @ (if licensedSquare then [ i2 + $"if (__rt == 0) {nt} = __rnt;" ] else [])
        @ [ forLoopFrom i2 nVar loStr hiStr ]
        @ regionBody
        @ regionFrees
        @ [ i2 + "}"
            i1 + "}" ]
    // After the team: the row fold's combine.
    let post =
        match fold with
        | None -> []
        | Some (f, (r, rv), (p, pv), (a, av)) ->
            let pName = $"__v{p}"
            if licensedSquare then
                let (combine, combineFrees) =
                    withLoopFrame [ (a, av) ] i2 (fun () ->
                        let (code, _) = renderLets (addVarName p pName innerCtx) (ctx.Indent + 2) [ (a, av) ] Map.empty
                        code)
                [ i1 + "// the row partials combined ascending from +0 per row, then the time chain ascending from its seed"
                  forLoopFrom i1 nVar loStr hiStr
                  i2 + $"{elem} {pName} = ({elem})0;"
                  i2 + $"for (int __rsi = 0; __rsi < {nt}; __rsi++) {pName} = {pName} + {part}[(size_t){nVar} * (size_t){t} + (size_t)__rsi];" ]
                @ combine @ combineFrees
                @ [ i1 + "}"
                    i1 + $"delete[] {part};" ]
            elif parallelRows then
                // K on each COMPLETE row into its slot, rows in parallel
                // under F's own request (each row's chain whole, in K's own
                // text), then the time chain over the slots in order.
                let (slotBody, slotFrees) =
                    withLoopFrame [ (r, rv); (p, pv) ] i2 (fun () ->
                        let (code, _) = renderLets innerCtx (ctx.Indent + 2) [ (r, rv); (p, pv) ] Map.empty
                        code @ [ i2 + $"{slot}[{nVar}] = {pName};" ])
                let (combine, combineFrees) =
                    withLoopFrame [ (a, av) ] i2 (fun () ->
                        let (code, _) = renderLets (addVarName p pName innerCtx) (ctx.Indent + 2) [ (a, av) ] Map.empty
                        code)
                recordOmpConstruct subject "BLADE_OMP_PARALLEL_FOR" "rec-step-parallel: the row kernel on complete rows into indexed slots, rows in parallel under the outer fold kernel's `where omp` (each row's chain whole); the time chain over the slots in order"
                [ i1 + "// strict: the row kernel on each COMPLETE row into its slot (rows in parallel under the outer fold kernel's own `where omp`; each chain whole), then the time chain over the slots in order"
                  i1 + $"{elem}* {slot} = new {elem}[(size_t)({hiStr})]();"
                  i1 + "BLADE_OMP_PARALLEL_FOR"
                  forLoopFrom i1 nVar loStr hiStr ]
                @ slotBody @ slotFrees
                @ [ i1 + "}"
                    forLoopFrom i1 nVar loStr hiStr
                    i2 + $"const {elem} {pName} = {slot}[{nVar}];" ]
                @ combine @ combineFrees
                @ [ i1 + "}"
                    i1 + $"delete[] {slot};" ]
            else
                // Serial: K on each complete row, then F -- the fused fold's
                // own statements, after the team instead of between the steps.
                let (body, frees) =
                    withLoopFrame [ (r, rv); (p, pv); (a, av) ] i2 (fun () ->
                        let (code, _) = renderLets innerCtx (ctx.Indent + 2) [ (r, rv); (p, pv); (a, av) ] Map.empty
                        code)
                [ i1 + "// the row fold after the team, serial: the row kernel on each complete row, then the time chain, in order"
                  forLoopFrom i1 nVar loStr hiStr ]
                @ body @ frees
                @ [ i1 + "}" ]
    let form =
        match fold with
        | None -> "persistent team; no row fold"
        | Some _ when licensedSquare -> "persistent team; licensed row partials per worker chunk (self-dot K-lanes), combined ascending per row; time chain in order"
        | Some _ when parallelRows -> "persistent team; strict post-pass: complete-row slots in parallel under the outer fold kernel's `where omp`, time chain in order"
        | Some _ -> "persistent team; strict post-pass: complete rows serially, time chain in order"
    recordCodegenDecision "rec-step-parallel" subject Blade.Effects.Applied
        [ $"applied -- {form}"; $"the `where omp` request of `{entry.KernelName}` (the step's final map)" ]
    recordOmpConstruct subject $"BLADE_OMP_PARALLEL_NUM_THREADS({t})" "rec-step-parallel: the recursion's time loop inside one team, each worker owning a contiguous x chunk for every step"
    let code = forceCode @ region @ post @ [ ind + "}" ]
    Ok (code, addVarName binding.Id name ctx)



and genBindChainBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) (comp: IRExpr) (cont: IRExpr) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Monadic bind: defer if comp is a deferred computation
    let isCompDeferred =
        match comp with
        | IRVar (id, _) -> Map.containsKey id ctx.DeferredComputations
        | IRApplyCombinator _ | IRComposeApply _ | IRParallel _ | IRFusion _ | IRFunctorMap _ -> true
        | _ -> false
    if isCompDeferred then
        let ctx' = addVarName binding.Id name ctx
        let ctx' = { ctx' with DeferredComputations = Map.add binding.Id binding.Value ctx'.DeferredComputations }
        ([$"{ind}// {name} = <deferred bind>"], ctx')
    else
        // Scalar bind: cont(comp)
        let code = genScalarBinding ctx name binding.Value binding.Type
        let ctx' = addVarName binding.Id name ctx
        (code, ctx')



and genLetChainBinding (ctx: CodeGenContext) (binding: IRBinding) (builder: IRBuilder) : string list * CodeGenContext =
    let ind = indentStr ctx
    let name = bindingCppName binding
    // Force any deferred producer this block reads positionally (`A(i)` over a
    // still-deferred computed array) at the block's OUTER indent, before any
    // block statement is emitted -- materializing inside the block/loop body
    // would re-run the producer per iteration or dangle its loop var.
    //
    // A chain whose VALUE is a TUPLE leaves that tuple's own reads to the
    // final binding (round-3 W1-3): the tuple's members are evaluated left to
    // right AFTER the chain's lets, each forcing its own dependencies at its
    // own position (genMemberOrderedTupleBinding, or the tuple binding's own
    // pre-pass); forcing them here would run a member's producer ahead of the
    // chain's lets and of the tuple's earlier members. The final binding is
    // emitted at this chain's indent, so the outer-indent rule still holds.
    let (lets, finalExpr) = unrollLetChain binding.Value
    let (forceCode, ctx) =
        match finalExpr with
        | IRTuple _ ->
            lets |> List.fold (fun (accCode, accCtx) (_, value) ->
                let (code, ctx') = forceDeferredPositionalReads accCtx builder ($"{name}__def") value
                (accCode @ code, ctx')) ([], ctx)
        | _ -> forceDeferredPositionalReads ctx builder ($"{name}__def") binding.Value
    // Block expression: unroll the IRLet chain into sequential bindings
    let (allCode, foldCtx) =
        lets |> List.fold (fun (accCode, accCtx) (id, value) ->
            let tempName = $"__v{id}"
            let tempBinding = {
                Id = id; Name = tempName; Type = inferExprType value
                Value = value; IsConst = true; IsMutable = false
            }
            let (code, ctx') = genBinding accCtx tempBinding builder
            (accCode @ code, ctx')
        ) ([], ctx)
    // Generate the final named binding.
    //
    // Double-materialize elision: a chain whose VALUE is a bare reference to
    // one of its own inner lets (`{ let buf = zeros(...); ...; buf }` -- the
    // recursive-array elaboration's exact shape) would otherwise go through
    // genVarAliasBinding's assignability deep copy, leaving BOTH the staging
    // buffer and the copy live for the rest of the program, since module
    // bindings are never scope-freed: 2x the footprint of every `let rec`
    // trajectory (~1.5 GB of 09_qg_atmosphere's residency, measured). The
    // staging let is block-scoped -- no name outside this chain can reach
    // it -- so the final binding may ALIAS its wrapper instead, under
    // canAliasStagingLet's three conditions (sole-owned fresh pool, no assign
    // leaking it out of the chain, no rival alias).
    //
    // The original form also required an EMPTY alloc-scope stack, on the
    // reasoning that a function/loop frame's registration plus escape analysis
    // already recycled the block. That gate is gone: it is the frame case that
    // leaks hardest (a `let rec` built inside a function is re-materialized on
    // EVERY call), and the alias changes nothing the frame reasons about --
    // both the staging alloc and the elided copy carry the same owner stamp
    // (setAllocOwner is per OUTER let), so the frame simply tracks one
    // allocation instead of two.
    let aliasableStagingRef =
        match finalExpr with
        | IRVar (srcId, _) -> canAliasStagingLet lets None srcId []
        | _ -> false
    let finalBinding = {
        Id = binding.Id; Name = name; Type = binding.Type
        Value = finalExpr; IsConst = binding.IsConst
        // Dropping the mutability marks routes genVarAliasBinding to its
        // plain-alias arm; assignment through the binding then writes the
        // sole-owner staging pool, which is observationally identical.
        IsMutable = binding.IsMutable && not aliasableStagingRef
    }
    // A block-local `let rec` arrives here with its id in MutableArrayLets
    // (every block array let is assignable), which genVarAliasBinding checks
    // INDEPENDENTLY of IsMutable -- lift the membership for the final bind
    // only, restoring the caller-visible set afterwards.
    let foldCtxForFinal =
        if aliasableStagingRef
        then { foldCtx with MutableArrayLets = Set.remove binding.Id foldCtx.MutableArrayLets }
        else foldCtx
    let (finalCode, finalCtx) = genBinding foldCtxForFinal finalBinding builder
    let finalCtx =
        if aliasableStagingRef
        then { finalCtx with MutableArrayLets = foldCtx.MutableArrayLets }
        else finalCtx
    (forceCode @ allCode @ finalCode, finalCtx)


