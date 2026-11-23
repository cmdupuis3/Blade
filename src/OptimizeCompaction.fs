// Compaction fusion (mask -> compound -> fold), rule compaction-fusion, its side table and the fused selected terminal (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeCompaction

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeMemberHoist

// --- Compaction fusion: mask -> compound -> fold (round 2 item B4) ----------
//
// The WHERE idiom of examples/01 at its smallest:
//
//   let keep = mask(x, lambda(v) -> v > 1.0)
//   let y = compound(x, keep)
//   let checksum = reduce(y * y, (+)) + Float64(extents(y))
//
// emits SIX sweeps in both lanes: the mask, a count of the mask, the
// branchless kept-position fill, the gather by position, a `y * y` compound
// pool (the elementwise operand forced because the fused fold terminal has
// no compound arm), and the fold over that pool. The plan below removes four
// of them without touching what any binding MEANS -- every named store stays
// (`keep`, `y`, its index with its kept positions), and the IR the
// interpreter runs is untouched (a side table, the HotRowTable pattern):
//
//   * B4b, the COMPACTION plan (`CompactionPlan`): the count of `keep` is
//     taken inside its own mask pass, and the position fill and the value
//     gather are one branchless pass `v = x[i]; y[q] = v; pos[q] = i;
//     q += keep[i]` (m + 1 cells in BOTH buffers, the last the slack the
//     unconditional store needs, never exposed). The index is then built
//     from the table the pass filled (compound_index_t<1>'s adopting
//     constructor): dense extent n, cardinality m, the ascending kept
//     positions -- the table the old constructor built. Stable, ascending,
//     serial, exactly as before.
//   * B4a, the FOLD plan (`CompactionFoldPlan`): the self-product's fold
//     reads `y`'s compact buffer directly (`y.data`, m contiguous cells), no
//     square pool. The fold form is the emitter's: the licensed K-lane tree
//     (`blade_fold::sq`) in the C++ lane, the reassociable chain in the LLVM
//     lane; under BLADE_FP_REPRO=1 the unseeded serial chain from the first
//     kept square, in kept order. BL8003 on an empty `y` stays at the fold's
//     own declaration, after the compaction, as before.
//   * B4c: under the serial (repro) form, when the compaction itself is
//     fused, the chain is FED INSIDE the compaction pass, block by block,
//     from cells already written (index < q, never the slack cell). Its
//     value is the same chain in the same order -- the map is the pure
//     self-product of `y`'s own cells, the fold a builtin `+`, `y` is never
//     assigned -- and the empty check still runs at the fold's own site.
//
// Round 3 (T4, `[compaction-fusion v2]`; `b4` keeps exactly the round-2 form
// above) adds two schedule changes on the same side table -- T4b is the
// DEFAULT, T4a is OPT-IN (`gather`) this round (its own measured gain is
// under the round's acceptance line):
//
//   * T4b, positions ELIDED: the kept-position table is compiler metadata,
//     not a named output. A fail-safe DEMAND CERTIFICATE over `y` (and every
//     elementwise map derived from it, which shares its index) proves that
//     nothing reads it: every use must be a fold, `extents`, a derived map,
//     or printing (which reads the compact buffer); a keyed read, an alias,
//     an argument, a tuple, an assignment, a capture, a provider write, a
//     side-table operand or ANY use the census does not recognize demands
//     the table. Certified, the index is built cardinality-only
//     (compound_index_t<1>'s positions-elided constructor: n and m intact;
//     every lookup through the absent table is an internal error, BL9002,
//     never a read of a null pointer).
//   * T4a, the gather AT THE MASK SITE: `keep`'s own pass sweeps x once:
//     per 256-cell block, the predicate loop (vectorizable, as the counted
//     mask pass is), then the branchless gather of the kept values (and
//     positions, when built) over that L1-resident block, into n + 1 private
//     cells per buffer (m + 1 touched by the n branchless stores); under the
//     serial form B4c's chain is fed after each block. (One scalar loop doing
//     both measured SLOWER than B4's two passes over a cached x: the
//     predicate loses its vector form.) `keep` and `y` stay stored.
//     Admission is `selectionAdmits`: the five parties of the motion (the producer x, the
//     predicate, every planned outer fold's kernel, the seeds, every binding
//     between the mask and the compound) through the shared write analysis
//     (`scheduleMayWriteIn`); a writing party declines to the B4 form, never
//     further. The n + 1 reservation is capped (BLADE_TILE_TUNE sel_cap):
//     above it, at compile time or by a run-time test, B4's two passes run.
//
// Every admission audits a binding's whole PAYLOAD (`bindingPayload`: its
// value and the side-table operands its site runs), never the placeholder
// value of a compound / sparse / random binding.
//
// Gate: BLADE_COMPACTION_FUSION (`0`/`off`, `b4`, default = T4b, `gather` =
// T4b + T4a).

/// T4a's C++ handshake: what the mask pass gathered into, published by the
/// mask's emission and read by the compound's.
type CompactionGatherCpp = {
    /// The value buffer (`new T[n + 1]`; nullptr until the guard holds).
    Compact: string
    /// The kept-position buffer, when the table is built.
    Pos: string option
    /// `None`: the single pass ALWAYS ran (static n within the cap). `Some
    /// g`: a run-time test -- the compound runs B4's pass itself when `g`
    /// is false.
    Guard: string option
    /// The in-pass chains (accumulator, kernel wrapper), so the compound's
    /// fallback pass feeds the same accumulators.
    Feeds: (string * string) list
}

/// The LLVM lane's twin: pool slots (alloca'd `ptr`), the count slot, the
/// run-time guard (an i1 register computed before any branch), and the
/// in-pass chain accumulators with their fold callables' plan ids.
type CompactionGatherLlvm = {
    DstSlot: string
    PosSlot: string option
    CountSlot: string
    Guard: string option
    Feeds: (IRId * string) list
}

/// T4c's C++ handshake: what the shared selection's ONE pass filled (at the
/// mask under `gather`, else at the first companion), published by that site
/// and adopted by every companion.
type SharedSelectionCpp = {
    /// The shared `compound_index_t<1>*` once the first companion built it.
    Index: string option
    /// Each companion's compact value buffer (compound id -> C++ variable).
    Compacts: Map<IRId, string>
    /// The kept-position buffer, when any companion demands the table.
    Pos: string option
    /// T4a's run-time cap guard (the first companion runs the joint B4 pass
    /// itself when it is false).
    Guard: string option
    /// The in-pass chains (accumulator, kernel wrapper, the companion buffer
    /// it reads), so a fallback pass feeds the same accumulators.
    Feeds: (string * string * string) list
    /// The single pass ran in the mask pass (T4a), not at the first companion.
    AtMask: bool
}

/// The LLVM lane's twin: pool slots (alloca'd `ptr`) per companion, the
/// position slot, the count slot, the run-time guard, the in-pass chains
/// (fold plan id, accumulator, the companion's pool slot).
type SharedSelectionLlvm = {
    DstSlots: Map<IRId, string>
    PosSlot: string option
    CountSlot: string
    Guard: string option
    Feeds: (IRId * string * IRId) list
    AtMask: bool
    /// The first companion completed the selection (the pools are filled).
    Ready: bool
}

/// T4c: ONE selection for every `y_j = compound(c_j, keep)` over the same
/// let-bound rank-1 mask (each c_j over the mask source's index space). The
/// plan OWNS the index: built once (counted in the mask pass; its kept
/// positions, when demanded, filled by the one joint pass), shared by every
/// companion's Compound wrapper, freed once -- every companion owns only its
/// own compact data store (`deallocate_compound_shared_index`), the index's
/// single index-only free is registered with the LAST companion.
type SharedSelection = {
    MaskId: IRId
    MaskName: string
    /// The companions in declaration order: (compound id, compound name,
    /// dense source id).
    Members: (IRId * string * IRId) list
    /// Each companion's element type (compound id -> element).
    ElemTypes: Map<IRId, IRType>
    /// The union of the companions' position demands (T4b census per
    /// companion: one keyed read anywhere keeps the shared table).
    PositionsNeeded: bool
    /// The joint gather runs in the mask pass (T4a, `gather`, J <= 4, every
    /// source bound before the mask, within sel_cap).
    GatherAtMask: bool
    StaticN: int64 option
    /// sel_cap as a cell count over EVERY companion buffer and the positions.
    SelCapCells: int64
    BuiltCpp: SharedSelectionCpp option ref
    BuiltLlvm: SharedSelectionLlvm option ref
}

/// The fused compaction of one `let y = compound(x, keep)` over a let-bound
/// rank-1 `keep = mask(x, pred)`.
type CompactionPlan = {
    MaskId: IRId
    MaskName: string
    CompoundId: IRId
    CompoundName: string
    SourceId: IRId
    /// T4b: build the kept-position table (a consumer demands it, the census
    /// could not prove otherwise, or the `b4` mirror).
    PositionsNeeded: bool
    /// T4a: the gather runs in the mask pass (subject to `SelCapCells`).
    GatherAtMask: bool
    /// T4a's static extent, when the source's is a literal (the cap was then
    /// decided at plan time and the emitters take the single pass outright).
    StaticN: int64 option
    /// The selectivity cap as a cell count: the single pass is taken iff
    /// n < SelCapCells, i.e. (n + 1) x (bytes per cell over every buffer)
    /// <= sel_cap -- the product checked by division, never overflowing.
    SelCapCells: int64
    /// The C++ lane's handshake: the mask emitter's count variable, written
    /// when it actually counted (its rank-1 callable arm), read by the
    /// compound's emission to choose the fused pass. Reset by the mask's
    /// emission, so a second emission of the same program starts clean.
    CountCpp: string option ref
    /// The LLVM lane's twin: the alloca slot holding the count.
    CountLlvm: string option ref
    /// T4a: what the mask pass gathered (reset with the count).
    GatherCpp: CompactionGatherCpp option ref
    GatherLlvm: CompactionGatherLlvm option ref
    /// T4c: the shared selection this compound is a companion of (every
    /// companion's plan carries the SAME record and the same count refs).
    Shared: SharedSelection option
}

/// The fused fold `reduce(y * y, (+))` of one compound `y`: the lifted map
/// let (`MapId`, the anonymous `y * y` the fold forces today) is never
/// materialized; the fold reads `y`'s buffer.
type CompactionFoldPlan = {
    MapId: IRId
    CompoundId: IRId
    CompoundName: string
    /// The binding hosting the fold (`checksum`).
    HostName: string
    FoldKernel: IRExpr
    /// The compaction of `y` is fused and `y` is never assigned, so the
    /// compaction pass MAY feed the serial chain (B4c); whether it does is
    /// the emitter's call (the serial form only), published below.
    InPass: bool
    /// Each lane's handshake: the accumulator the compaction pass fed the
    /// chain into (B4c), when it did. Reset by the compound's emission.
    EmittedCpp: string option ref
    EmittedLlvm: string option ref
}

/// AsyncLocal like HotRowTable; reset once per program in
/// `lowerTypedProgram`, because let ids restart per compile.
module CompactionTable =
    open System.Threading
    type State =
        { ByMask: Map<IRId, CompactionPlan>
          ByCompound: Map<IRId, CompactionPlan>
          FoldByMap: Map<IRId, CompactionFoldPlan>
          FoldsByCompound: Map<IRId, CompactionFoldPlan list> }
    let private empty = { ByMask = Map.empty; ByCompound = Map.empty; FoldByMap = Map.empty; FoldsByCompound = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    /// ONE plan per mask: the FIRST recorded (a shared selection records its
    /// companions in declaration order, so the mask's plan is the first
    /// companion's, whose `Shared` names them all). A second plan over the
    /// same mask never overwrites the mask's entry -- before T4c the planner
    /// let only one compound per mask through (the others declined), so the
    /// map's last-write-wins was unreachable; it is now refused outright.
    let record (p: CompactionPlan) =
        let s = get ()
        let byMask = if Map.containsKey p.MaskId s.ByMask then s.ByMask else Map.add p.MaskId p s.ByMask
        cell.Value <- { s with ByMask = byMask; ByCompound = Map.add p.CompoundId p s.ByCompound }
    let recordFold (f: CompactionFoldPlan) =
        let s = get ()
        let prev = Map.tryFind f.CompoundId s.FoldsByCompound |> Option.defaultValue []
        cell.Value <- { s with FoldByMap = Map.add f.MapId f s.FoldByMap
                               FoldsByCompound = Map.add f.CompoundId (prev @ [ f ]) s.FoldsByCompound }
    let tryMask (id: IRId) : CompactionPlan option = Map.tryFind id (get ()).ByMask
    let tryCompound (id: IRId) : CompactionPlan option = Map.tryFind id (get ()).ByCompound
    let tryFoldMap (id: IRId) : CompactionFoldPlan option = Map.tryFind id (get ()).FoldByMap
    let foldsOf (compoundId: IRId) : CompactionFoldPlan list =
        Map.tryFind compoundId (get ()).FoldsByCompound |> Option.defaultValue []

/// PM19's first storage-elision arm. Unlike the compaction side-table plans,
/// this rewrites the tree BOTH evaluators run, BEFORE any schedule table is
/// built. Only an explicitly unprinted, otherwise private mask/compound pair
/// and one direct seeded scalar fold, or two such folds in one adjacent tuple
/// host, qualify. `compound` is top-level-only
/// today (BL6002), hence the explicit print-selection requirement here.
///
/// Repro builds, and (G122, round 4 wave 5) the default build when no leg's
/// kernel may carry a reorder licence: the guarded kernel has no reorder
/// licence, so converting a LICENSED K-lane fold to this serial chain would
/// change its operation graph, while an unlicensed fold is already that
/// ascending chain in every build. Same association; bitwise under
/// BLADE_FP_CONTRACT=off -- under the fast default the guarded body and the
/// stored fold are different contraction contexts (a predicate sharing a
/// product with the kernel may keep it unfused), as for every schedule change.
/// No unseeded, selected-map, membership-predicate or cross-binding join arm.
let fuseSelectedTerminalModule (builder: IRBuilder) (programFuncs: IRCallable list)
                               (foreignRefs: Set<IRId>) (modul: IRModule) : IRModule =
    if modul.CompoundInits.IsEmpty then modul
    else
    let mode = compactionFusionMode ()
    let enabled = mode = CompactionFusionT4 || mode = CompactionFusionGather
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let facts = cseFactsOf funcs
    let payloadWrites b = bindingPayload modul b |> List.exists (fun e -> (facts e).MayWrite || writes e)
    // The shared print selection (Blade.PrintSelection, gap G200). Empty /
    // unset observes every named top-level mask and selection.
    let selectedNames = Blade.PrintSelection.names () |> Option.defaultValue Set.empty
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let roots = compactionRoots allCallables modul
    let arr = List.toArray modul.Bindings
    let indexOf id = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let paramRef (p: IRParam) = function
        | IRVar (id, _) -> id = p.VarId
        | IRParam (_, i, _) -> i = p.Index
        | _ -> false
    // Closed whitelist: no reads/captures/calls/casts/panic sites, and no
    // resource events. Floating arithmetic here is IEEE-total in both lanes.
    let rec total (ps: IRParam list) e =
        match e with
        | _ when ps |> List.exists (fun p -> paramRef p e) -> true
        | IRLit (IRLitFloat _ | IRLitBool _) -> true
        | IRBinOp (_, (IRAdd | IRSub | IRMul | IRDiv | IREq | IRNeq | IRLt | IRLe | IRGt | IRGe | IRAnd | IROr), a, b, _) ->
            total ps a && total ps b
            && (match typeOf a, typeOf b with
                | IRTScalar ETFloat64, IRTScalar ETFloat64
                | IRTScalar ETBool, IRTScalar ETBool -> true
                | _ -> false)
        | IRUnaryOp ((IRNeg | IRNot), x) -> total ps x
        | IRIf (c, t, f) -> total ps c && total ps t && total ps f
        | _ -> false
    let plainCallable arity (c: IRCallable) =
        c.Params.Length = arity && c.Captures.IsEmpty && not c.IsStatic && not c.IsArityPoly
        && not c.IsOmpParallel && not c.IsCudaKernel && not c.IsMpiParallel
        && (c.Params |> List.forall (fun p -> p.Type = IRTScalar ETFloat64))
        && total c.Params c.Body
    let denseFloat64 = function
        | ArrayElem at when at.ElemType = IRTScalar ETFloat64 && not at.IsVirtual ->
            match at.IndexTypes with
            | [ ix ] -> ix.IxKind = IxKPlain && ix.Rank = 1 && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty
            | _ -> false
        | _ -> false
    let minted = ResizeArray<IRCallable * IRId>()
    let removed = System.Collections.Generic.HashSet<IRId>()
    let replacements = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let decide name outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "selected-terminal"; Version = 1; Span = declSpanOf name
              Subject = name; Outcome = outcome; Evidence = evidence }
    for KeyValue (yid, (dense, mask)) in modul.CompoundInits do
        match indexOf yid, dense, mask with
        | Some yi, IRVar (xid, xt), IRVar (kid, _) ->
            let y = arr.[yi]
            let candidate =
                match indexOf xid, indexOf kid with
                | Some xi, Some ki when xi < ki && ki < yi ->
                    match arr.[ki].Value with
                    | IRMask (IRVar (src, _), pk) when src = xid -> Some (xi, ki, pk)
                    | _ -> None
                | _ -> None
            match candidate with
            | None -> ()
            | Some (xi, ki, pk) ->
                let x, k = arr.[xi], arr.[ki]
                let terminals =
                    arr |> Array.mapi (fun hi b -> hi, b) |> Array.choose (fun (hi, b) ->
                        match b.Value with
                        | ReduceOfNamed (id, _, fk, Some seed) when id = yid && hi > yi ->
                            Some (hi, b, [ fk, seed ], false)
                        | IRTuple [ ReduceOfNamed (a, _, k1, Some s1); ReduceOfNamed (bId, _, k2, Some s2) ]
                            when a = yid && bId = yid && hi > yi -> Some (hi, b, [ k1, s1; k2, s2 ], true)
                        | _ -> None)
                let decline why = decide y.Name (Blade.Effects.Declined why) []
                if terminals.Length <> 1 then decline "requires exactly one direct seeded scalar terminal or one two-terminal tuple host"
                else
                let hi, host, legs, tupleHost = terminals.[0]
                let privateIds = Set.ofList [ kid; yid ]
                let mentions e = not (Set.intersect privateIds (collectVarRefsIR e)).IsEmpty
                let exprDemand e = mentions e || (embeddedExtentExprs e |> List.exists mentions)
                let typeDemand t = irTypeExtentExprs t |> List.exists exprDemand
                let observer =
                    roots |> List.tryPick (function
                        | RootBindingValue b when b.Id = kid || b.Id = yid -> None
                        | RootBindingValue b when b.Id = host.Id ->
                            // Only the matched fold inputs are subtracted. The
                            // exact tuple grammar has no other value children;
                            // operand types are certified separately below.
                            if legs |> List.exists (fun (fk, seed) -> exprDemand fk || exprDemand seed) then
                                Some "the terminal kernel or seed observes the private arrays" else None
                        | RootBindingValue b when exprDemand b.Value -> Some $"an additional consumer in `{b.Name}`"
                        | RootSideOperand (b, _, _) when b.Id = yid -> None
                        | RootSideOperand (b, what, e) when exprDemand e -> Some $"{what} of `{b.Name}`"
                        | RootFunctionBody f when exprDemand f.Body || (f.Captures |> List.exists (fun c -> privateIds.Contains c.Id)) ->
                            Some $"a callable body or capture in `{f.SourceName}`"
                        | RootType (where, t) when where <> $"`{y.Name}`" && typeDemand t -> Some $"the type of {where}"
                        | RootSourceRef (where, id) when privateIds.Contains id -> Some where
                        | RootNameRef (where, n) when n = k.Name || n = y.Name -> Some where
                        | _ -> None)
                let producerProof = storedArrayProof modul x
                let betweenWrites = [ ki + 1 .. hi ] |> List.exists (fun i -> payloadWrites arr.[i])
                let payloadLifetime b = bindingPayload modul b |> List.exists lifetimeBarrier
                let tupleGeometryOk =
                    if not tupleHost then true else
                    let literalSource =
                        match xt with
                        | ArrayElem at ->
                            match at.IndexTypes with
                            | [ ix ] -> match ix.Extent with IRLit (IRLitInt n) -> n >= 0L | _ -> false
                            | _ -> false
                        | _ -> false
                    let maskType =
                        match xt, k.Type with
                        | ArrayElem a, ArrayElem b -> b.ElemType = IRTScalar ETBool && not b.IsVirtual && a.IndexTypes = b.IndexTypes
                        | _ -> false
                    let selectedType =
                        match y.Type with
                        | ArrayElem at when not at.IsVirtual && at.ElemType = IRTScalar ETFloat64 ->
                            match at.IndexTypes with
                            | [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1 && ix.Symmetry = SymNone
                                        && ix.Dependencies.IsEmpty && ix.Extent = IRCompoundMask (IRLit IRLitUnit)
                            | _ -> false
                        | _ -> false
                    let inputs =
                        match host.Value with
                        | IRTuple [ ReduceOfNamed (_, t1, _, _); ReduceOfNamed (_, t2, _, _) ] -> t1 = y.Type && t2 = y.Type
                        | _ -> false
                    let producerInputs =
                        match k.Value, mask with
                        | IRMask (IRVar (_, sourceType), _), IRVar (_, maskType) -> sourceType = xt && maskType = k.Type
                        | _ -> false
                    let callableTypes =
                        let plainRef expected = function IRVar (_, ty) -> ty = expected | _ -> false
                        plainRef (mkFuncArrow [ IRTScalar ETFloat64 ] (IRTScalar ETBool)) pk
                        && (legs |> List.forall (fun (fk, _) ->
                            plainRef (mkFuncArrow [ IRTScalar ETFloat64; IRTScalar ETFloat64 ] (IRTScalar ETFloat64)) fk))
                    literalSource && maskType && selectedType && inputs && producerInputs
                    && callableTypes && x.Type = xt && y.Value = IRLit IRLitUnit
                let selectedTypeOk =
                    match y.Type with
                    | ArrayElem at ->
                        at.ElemType = IRTScalar ETFloat64
                        && (match at.IndexTypes with [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1 | _ -> false)
                    | _ -> false
                // G122 (round 4 wave 5, lane B): the guarded chain is the
                // ascending serial graph, which is exactly how EVERY build folds
                // a kernel with no reorder licence -- so an all-unlicensed leg
                // set takes the route in the default build too (same
                // association; bitwise under BLADE_FP_CONTRACT=off, the
                // contraction context may differ under the fast default).
                // A leg that MAY be licensed (a `comm`
                // kernel, a comm group, or a body that is one binary operator
                // over its two parameters: a superset of foldReorderLicensed,
                // fail closed) keeps the repro-only rule below.
                let mayBeLicensed (c: IRCallable) =
                    c.IsCommutative || not c.CommGroups.IsEmpty
                    || (match c.Params, c.Body with
                        | [ p0; p1 ], IRBinOp (_, _, l, r, _) ->
                            let isP (p: IRParam) (x: IRExpr) = paramRef p x
                            (isP p0 l && isP p1 r) || (isP p1 l && isP p0 r)
                        | _ -> false)
                let legsLicensed =
                    legs |> List.exists (fun (fk, _) ->
                        match resolveCallableIn funcs fk with
                        | Some c -> mayBeLicensed c
                        | None -> true)
                if not enabled then decline "disabled by BLADE_COMPACTION_FUSION (legacy mirrors keep their original storage)"
                elif not (Blade.Toolchain.fpReproEnabled ()) && legsLicensed then
                    decline "requires the reproducible serial fold graph (BLADE_FP_REPRO=1): the selected terminal is ONE guarded ascending chain per fold, while a licensed build folds the stored compound with its K-lane tree (blade_fold::sum<double, 8>) -- a different association; v1 does not trade the licensed tree for the pool it saves (gap G108 (3): the build-independent arm is unmeasured)"
                elif selectedNames.IsEmpty || Blade.PrintSelection.mayName selectedNames k.Name || Blade.PrintSelection.mayName selectedNames y.Name then
                    decline "the mask or selected array is printed (requires an explicit print selection excluding both)"
                elif not (selectedNames |> Set.forall (fun n -> arr |> Array.exists (fun b -> b.Name = n))) then
                    decline "the print selection is not confined to this module's known binding names"
                elif foreignRefs.Contains kid || foreignRefs.Contains yid then decline "the mask or selected array is referenced by another module"
                elif tupleHost && (ki + 1 <> yi || yi + 1 <> hi) then
                    decline "the two-terminal tuple requires adjacent mask, compound and host (failure order)"
                elif tupleHost && not tupleGeometryOk then decline "the two-terminal tuple has unsupported literal-axis or selected operand/type metadata"
                elif not (denseFloat64 xt) || not selectedTypeOk
                     || host.Type <> (if tupleHost then IRTTuple [ IRTScalar ETFloat64; IRTScalar ETFloat64 ] else IRTScalar ETFloat64) then
                    decline "requires a plain stored rank-1 Float64 input and scalar Float64 terminal"
                elif not (bindingSideOperands modul k.Id).IsEmpty || not (bindingSideOperands modul host.Id).IsEmpty
                     || (bindingSideOperands modul y.Id).Length <> 2 then decline "an unsupported side-table payload survives"
                elif tupleHost && ([ kid; yid; host.Id ] |> List.exists (fun id -> modul.ProviderReads.ContainsKey id || modul.ProviderWrites.ContainsKey id)) then
                    decline "a tuple-region provider payload survives"
                elif observer.IsSome then decline observer.Value
                elif betweenWrites then decline "an intervening payload may write (source versions are not proved identical)"
                elif tupleHost && ([ ki .. hi ] |> List.exists (fun i -> payloadLifetime arr.[i])) then
                    decline "a tuple-region payload crosses a pool lifetime event"
                elif legs |> List.exists (fun (_, seed) -> not (total [] seed) || typeOf seed <> IRTScalar ETFloat64) then
                    decline "the explicit seed is not a closed pure/total Float64 scalar"
                else
                let folds = legs |> List.map (fun (fk, _) -> resolveCallableIn funcs fk)
                match producerProof, resolveCallableIn funcs pk, folds with
                | Error why, _, _ -> decline $"the input is not proved stored: {why}"
                | Ok (_, chain), _, _ when chain |> List.exists payloadWrites -> decline "a stored input initializer may write"
                | Ok (_, chain), _, _ when tupleHost && (chain |> List.exists payloadLifetime) -> decline "a stored input initializer crosses a pool lifetime event"
                | Ok _, Some pred, folds when plainCallable 1 pred && pred.RetType = IRTScalar ETBool
                                               && (folds |> List.forall (function
                                                   | Some fold -> plainCallable 2 fold && fold.RetType = IRTScalar ETFloat64
                                                                  && (modul.Functions |> List.exists (fun f -> f.Id = fold.Id))
                                                   | None -> false)) ->
                    let guardedLeg (fold: IRCallable) seed =
                        let ps = fold.Params |> List.map (fun p -> { p with VarId = builder.FreshId () })
                        let substitute (old: IRParam list) (values: IRExpr list) body =
                            mapIRExpr (fun n ->
                                List.zip old values |> List.tryPick (fun (p, v) -> if paramRef p n then Some v else None)
                                |> Option.defaultValue n) body
                        let acc, cell = IRVar (ps.[0].VarId, ps.[0].Type), IRVar (ps.[1].VarId, ps.[1].Type)
                        let body = IRIf (substitute pred.Params [ cell ] pred.Body,
                                         substitute fold.Params [ acc; cell ] fold.Body, acc)
                        let guarded = mkCallable builder defaultLambdaOptions ps body fold.RetType [] false [] [] false false 256 false
                        let guarded = { guarded with Name = $"__selected_terminal_{guarded.Id}"; SourceName = fold.SourceName }
                        minted.Add (guarded, fold.Id)
                        // Rebuild the plain function arrow too: all scheduling
                        // metadata comes from the NEW, unlicensed callable.
                        let guardedType = mkFuncArrow (ps |> List.map _.Type) guarded.RetType
                        IRReduce (dense, IRVar (guarded.Id, guardedType), Some seed)
                    let rewritten = List.map2 (fun fold (_, seed) -> guardedLeg (Option.get fold) seed) folds legs
                    replacements.[host.Id] <- if tupleHost then IRTuple rewritten else rewritten.Head
                    removed.Add kid |> ignore
                    removed.Add yid |> ignore
                    decide y.Name Blade.Effects.Applied
                        [ (if tupleHost then
                               $"two guarded stored-input folds at tuple `{host.Name}`; `{k.Name}` Bool mask and `{y.Name}` selected value/index stores eliminated; positions may already be elided"
                           else $"one stored-input scan at `{host.Name}`; `{k.Name}` Bool mask, `{y.Name}` positions/index/value stores eliminated")
                          "complete binding/type/capture/side-table/foreign/print census; no intervening writes"
                          (if tupleHost then "storage elision only at this stage; sibling traversal sharing is separately gated; predicate remains in each fold"
                           else "one guarded scalar terminal")
                          "same source for mask and selection (shape checks redundant); pure/total predicate and fold; seed once at the original terminal"
                          "ascending serial graph; false predicate returns the accumulator without a fold step; empty selection returns the exact seed"
                          (if Blade.Toolchain.fpReproEnabled () then "build: BLADE_FP_REPRO=1 (every fold keeps the ascending serial association)"
                           else "build: licensed default, every leg UNLICENSED (no `comm`, no builtin operator body): the stored selection's fold is the same ascending serial chain in every build, so the guarded chain changes no association (G122); bitwise under BLADE_FP_CONTRACT=off -- with contraction on, the guarded body is another contraction context (as for every schedule change)") ]
                | _ -> decline "the predicate or fold is not a supported capture-free pure/total scalar callable"
        | _ -> ()
    if removed.Count = 0 then modul
    else
        let byOrigin = minted |> Seq.groupBy snd |> Map.ofSeq
        { modul with
            Bindings = modul.Bindings |> List.filter (fun b -> not (removed.Contains b.Id))
                        |> List.map (fun b -> match replacements.TryGetValue b.Id with true, v -> { b with Value = v } | _ -> b)
            CompoundInits = modul.CompoundInits |> Map.filter (fun id _ -> not (removed.Contains id))
            Functions = modul.Functions |> List.collect (fun f ->
                f :: (Map.tryFind f.Id byOrigin |> Option.map (Seq.map fst >> List.ofSeq) |> Option.defaultValue []))
            DerivedFuncOrigins = minted |> Seq.fold (fun m (f, origin) -> Map.add f.Id origin m) modul.DerivedFuncOrigins }

/// The setup of `planCompactionFusion` that its group decision and its per-compound plan read, built once
/// per call (cleanup item 2). Field names are the bindings' own, so the lifted bodies read them unchanged.
type private CfContext =
    { modul: IRModule
      funcs: Map<IRId, IRCallable>
      facts: IRExpr -> CseFacts
      writes: IRExpr -> bool
      mode: CompactionFusionMode
      enabled: bool
      v2: bool
      sharedOn: bool
      gatherOn: bool
      streamedSrc: Set<IRId>
      decide: string -> Blade.Effects.DecisionOutcome -> string list -> unit
      arr: IRBinding[]
      indexOf: IRId -> int option
      assignedAnywhere: IRId -> bool
      payloadWrites: IRBinding -> bool
      payloadLifetime: IRBinding -> bool
      readsMask: IRId -> IRBinding -> bool
      transitiveRefs: IRBinding -> Set<IRId>
      squareMap: ApplyInfo -> IRId -> bool
      builtinAdd: IRCallable -> bool
      foldSites: IRId -> IRExpr -> (IRId * ApplyInfo * IRExpr * IRExpr option) list
      positionsDemand: IRId -> string -> Result<string list, string>
      elemBytes: IRType -> int64 option
      staticExtentOf: IRType -> int64 option
      fmtBytes: int64 -> string
      predicateAdmission: IRBinding -> Result<string, string>
      rank1Compound: IRBinding -> bool
      plainAxisOf: IRType -> IRIndexType option
      elemOf: IRType -> IRType
      stableExtentExpr: IRExpr -> bool
      shapeRoot: int -> IRId -> IRId }

/// T4b's DEMAND CENSUS of one compound (lifted from planCompactionFusion, cleanup item 2): the values it
/// reads arrive as parameters, in the order of first use.
let private cfPositionsDemand (peelMap: IRExpr -> ApplyInfo option) (refs: IRExpr -> Set<IRId>) (arr: IRBinding[]) (allCallables: IRCallable list) (modul: IRModule) (foreignRefs: Set<IRId>) (yId: IRId) (yName: string) : Result<string list, string> =
    let tracked = System.Collections.Generic.Dictionary<IRId, string>()
    tracked.[yId] <- yName
    let uses = ResizeArray<string>()
    let note (u: string) = if not (uses.Contains u) then uses.Add u
    let mutable demand : string option = None
    let req (why: string) = if demand.IsNone then demand <- Some why
    let isT (e: IRExpr) = match e with IRVar (v, _) -> tracked.ContainsKey v | _ -> false
    let nameOf (e: IRExpr) = match e with IRVar (v, _) when tracked.ContainsKey v -> tracked.[v] | _ -> yName
    // A CELLWISE map: every kernel input is one scalar cell (input rank
    // 0, known), the output is one scalar per cell (output rank 0) and
    // keeps the compound axis -- a kernel over the whole array (`c(0)` in
    // a whole-array parameter) or of unknown input rank is no derived map.
    let derivedMap (info: ApplyInfo) =
        not info.Arrays.IsEmpty
        && info.Arrays |> List.forall (fun a -> isT a || (match a with IRZip es -> not es.IsEmpty && List.forall isT es | _ -> false))
        && not info.HasReynolds && info.KernelOutputRank = 0
        && not info.KernelInputRanks.IsEmpty && info.KernelInputRanks |> List.forall ((=) 0)
        && (match stripUnits info.OutputType with
            | ArrayElem o -> (match o.IndexTypes with [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1 | _ -> false)
            | _ -> false)
        && (info.Arrays.Length = 1 || info.IsCoIteration)
    let rec scan (where: string) (e: IRExpr) =
        match e with
        | IRExtent (a, _) when isT a -> note "extents (the cardinality)"
        | IRReduce (a, k, init) when isT a ->
            note "folds over the compact buffer"
            scan where k
            init |> Option.iter (scan where)
        | IRLet (mid, v, body) when (match peelMap v with Some info -> derivedMap info | None -> false) ->
            let info = (peelMap v).Value
            tracked.[mid] <- $"the map over `{nameOf (List.head info.Arrays)}` in {where}"
            note "elementwise maps over it (sharing its index)"
            scanMapRest where v
            scan where body
        | IRIndex (a, _, _) when isT a -> req $"a keyed read of `{nameOf a}` in {where}"
        | IRApp (_, args, _) when List.exists isT args ->
            req $"`{nameOf (List.find isT args)}` passed as a call argument in {where}"
        | IRTuple es when List.exists isT es -> req $"`{nameOf (List.find isT es)}` stored in a tuple in {where}"
        | IRAssign (t, _) when (refs t) |> Set.exists tracked.ContainsKey -> req $"an assignment to `{yName}` (or a map of it) in {where}"
        | IRLet (_, v, _) when isT v -> req $"an alias of `{nameOf v}` in {where}"
        | IRVar (v, _) when tracked.ContainsKey v -> req $"a use of `{tracked.[v]}` the census does not certify, in {where}"
        | _ ->
            // an operand position the census does not certify names its node
            match childrenOf e |> List.tryFind isT with
            | Some a ->
                let node = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(e, typeof<IRExpr>) |> fst).Name
                req $"`{nameOf a}` as an operand of {node} (a use the census does not certify) in {where}"
            | None -> childrenOf e |> List.iter (scan where)
    // A derived map's own operands are the tracked arrays (positions-free
    // by the map's emission); everything else in it is scanned.
    // (The apply's Loop provenance repeats the operands -- `IRMethodFor`
    // over them, or over their `zip` -- and is skipped when it does.)
    and scanMapRest (where: string) (v: IRExpr) =
        match peelMap v with
        | Some info ->
            let operandOnly (a: IRExpr) = isT a || (match a with IRZip es -> List.forall isT es | _ -> false)
            (match info.Loop with
             | IRMethodFor mf when List.forall operandOnly mf.Arrays -> ()
             | IRObjectFor ofi -> scan where ofi.Kernel
             | loop -> scan where loop)
            scan where info.Kernel
        | None -> scan where v
    let scanType (where: string) (t: IRType) =
        for e in (try irTypeExtentExprs t with _ -> [ IRVar (yId, IRTUnit) ]) do scan $"the type of {where}" e
    // Every type and index expression an executable root carries (the
    // generic child walk does not enter types).
    let scanEmbedded (where: string) (e: IRExpr) =
        for x in (try embeddedExtentExprs e with _ -> [ IRVar (yId, IRTUnit) ]) do scan $"a type inside {where}" x
    // Module-level derived maps, in declaration order (a map reads only
    // earlier bindings).
    for b in arr do
        if b.Id <> yId then
            match peelMap b.Value with
            | Some info when derivedMap info ->
                tracked.[b.Id] <- b.Name
                note $"the named map `{b.Name}` (sharing its index)"
            | _ -> ()
    // THE MODULE TRAVERSAL (`compactionRoots`, shared with the foreign-
    // demand collection Lowering hands in as `foreignRefs`).
    for root in compactionRoots allCallables modul do
        match root with
        | RootBindingValue b when b.Id = yId -> ()
        | RootBindingValue b ->
            let where = $"`{b.Name}`"
            (match b.Value with
             | IRVar (v, _) when tracked.ContainsKey v -> req $"the alias `{b.Name}` of `{tracked.[v]}`"
             | _ -> if tracked.ContainsKey b.Id then scanMapRest where b.Value else scan where b.Value)
            scanEmbedded where b.Value
        | RootSideOperand (b, _, _) when b.Id = yId -> ()
        | RootSideOperand (b, what, e) ->
            let where = $"{what} of `{b.Name}`"
            scan where e
            scanEmbedded where e
        | RootFunctionBody fn ->
            let where = $"`{fn.SourceName}`"
            (match fn.Captures |> List.tryFind (fun c -> tracked.ContainsKey c.Id) with
             | Some c -> req $"`{tracked.[c.Id]}` captured by {where}"
             | None -> ())
            scan where fn.Body
            scanEmbedded where fn.Body
        | RootType (where, ty) -> scanType where ty
        | RootSourceRef (where, id) ->
            if tracked.ContainsKey id then req $"{where} of `{tracked.[id]}`"
        | RootNameRef (where, nm) ->
            if tracked.Values |> Seq.contains nm then req $"{where} `{nm}`"
    // Another module naming it (an import): the census does not see
    // that module's uses, so any reference at all demands the table.
    for KeyValue (id, nm) in tracked do
        if foreignRefs.Contains id then req $"`{nm}` named in another module"
    match demand with
    | Some why -> Error why
    | None -> Ok (List.ofSeq uses)

/// One compound's compaction-fusion plan (the body of planCompactionFusion's loop over the module's
/// compounds; lifted, cleanup item 2): the loop's values arrive as parameters, the setup through the context.
let private cfPlanCompound (ctx: CfContext) (yId: IRId) (denseExpr: IRExpr) (maskExpr: IRExpr) (groupDeclineOf: IRId -> string option) (sharedPlans: Map<IRId, CompactionPlan * string list>) : unit =
    let { indexOf = indexOf; arr = arr; decide = decide; predicateAdmission = predicateAdmission;
          payloadLifetime = payloadLifetime; readsMask = readsMask; facts = facts; writes = writes;
          payloadWrites = payloadWrites; transitiveRefs = transitiveRefs; assignedAnywhere = assignedAnywhere;
          mode = mode; enabled = enabled; modul = modul; foldSites = foldSites; funcs = funcs;
          squareMap = squareMap; builtinAdd = builtinAdd; v2 = v2; positionsDemand = positionsDemand;
          elemBytes = elemBytes; staticExtentOf = staticExtentOf; streamedSrc = streamedSrc; gatherOn = gatherOn;
          fmtBytes = fmtBytes } = ctx
    match indexOf yId with
    | None -> ()
    | Some yi ->
    let yb = arr.[yi]
    let yName = yb.Name
    match stripUnits yb.Type with
    | ArrayElem at when at.IndexTypes |> List.exists (fun ix -> ix.IxKind = IxKCompound) ->
        let rank1 =
            at.IndexTypes.Length = 1
            && (List.head at.IndexTypes).IxKind = IxKCompound && (List.head at.IndexTypes).Rank = 1
        if not rank1 then
            decide yName (Blade.Effects.Declined "v1 fuses the rank-1 compaction only: a rank-k mask (hashed index) or trailing dims keep the separate passes") []
        else
        // ---- B4b: the compaction ------------------------------------
        let compactionDecision : Result<CompactionPlan * string list, string> =
            match denseExpr, maskExpr with
            | IRVar (xId, _), IRVar (kId, _) ->
                (match indexOf kId with
                 | Some ki when ki < yi ->
                    let kb = arr.[ki]
                    (match kb.Value with
                     | IRMask (IRVar (srcId, _), _) when srcId = xId ->
                        // totalScalar, or (v2) the membership-probe certificate
                        let predAdmission = predicateAdmission kb
                        let between = [ ki + 1 .. yi - 1 ] |> List.map (fun i -> arr.[i])
                        // B4's own motion is the COUNT (taken in the mask
                        // pass, sizing the compound's buffers): sound while
                        // nothing between can change `keep`. Each binding's
                        // whole payload is audited (side-table operands
                        // included): a read of `keep`, a visibly writing
                        // value (round 2's rule), or a writing payload that
                        // can reach `keep` (transitively, through the
                        // callables it names) declines.
                        let offending =
                            between |> List.tryPick (fun b ->
                                if payloadLifetime b then Some $"`{b.Name}` contains an inline lifetime boundary"
                                elif readsMask kId b then Some $"`{b.Name}` reads `{kb.Name}` between the mask and the compound"
                                elif (facts b.Value).MayWrite || writes b.Value then Some $"`{b.Name}` may write (an assignment or a mutating call) between the mask and the compound"
                                elif payloadWrites b && Set.contains kId (transitiveRefs b) then
                                    Some $"`{b.Name}` may write `{kb.Name}` (its side-table payload writes and reaches it) between the mask and the compound"
                                else None)
                        // (IsMutable is no evidence either way: every
                        // non-static binding is assignable in its own
                        // scope; an actual assignment is.)
                        if assignedAnywhere kId then Error $"`{kb.Name}` is assigned"
                        else
                        match predAdmission with
                        | Error why -> Error why
                        | Ok _ ->
                        match offending with
                        | Some why -> Error why
                        | None ->
                            Ok ({ MaskId = kId; MaskName = kb.Name; CompoundId = yId; CompoundName = yName
                                  SourceId = xId; PositionsNeeded = true; GatherAtMask = false
                                  StaticN = None; SelCapCells = 0L
                                  CountCpp = ref None; CountLlvm = ref None
                                  GatherCpp = ref None; GatherLlvm = ref None; Shared = None },
                                [ $"`{kb.Name}`'s count is taken in its own mask pass (no count pass); the kept-position fill and the value gather are ONE branchless pass `v = x[i]; {yName}[q] = v; pos[q] = i; q += {kb.Name}[i]` (m + 1 cells in both buffers, the slack never exposed)"
                                  $"`{yName}`'s index: dense extent n, cardinality m, the ascending kept positions (keyed reads, BL8006 unchanged); positions always built (B4d deferred); `{kb.Name}` stays stored"
                                  "stable, ascending, serial: the same compaction in fewer sweeps" ])
                     | IRMask _ ->
                        (match groupDeclineOf yId with
                         | Some why -> Error $"the mask's source is not the compound's dense operand, and the shared selection (T4c) declined: {why}"
                         | None when mode = CompactionFusionUnshared -> Error "the mask's source is not the compound's dense operand (the shared selection, T4c, is disabled by BLADE_COMPACTION_FUSION=unshared)"
                         | None -> Error "the mask's source is not the compound's dense operand")
                     | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)`")
                 | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)` ahead of it")
            | _ -> Error "the mask or the dense operand is written in place (v1 fuses a let-bound mask over a stored array)"
        // A mask feeding SEVERAL compounds publishes its count to ONE plan
        // (the table keeps one plan per mask: the last compound's), so the
        // others keep their separate passes -- and say so in every mode,
        // `b4` included (the round-2 record called them applied while
        // their emission took the unfused path; the emission is unchanged).
        let sharedDecline =
            match compactionDecision with
            | Ok (plan, _) when enabled ->
                let members =
                    modul.CompoundInits |> Seq.choose (fun (KeyValue (cid, (_, m))) ->
                        match m with
                        | IRVar (v, _) when v = plan.MaskId -> Some cid
                        | _ -> None) |> List.ofSeq
                let last = List.max members
                if members.Length > 1 && yId <> last then
                    let lastName = match indexOf last with Some li -> arr.[li].Name | None -> "another compound"
                    let t4c =
                        match groupDeclineOf yId with
                        | Some why -> $"the shared selection (T4c) declined: {why}"
                        | None when mode = CompactionFusionUnshared -> "the shared selection, T4c, is disabled by BLADE_COMPACTION_FUSION=unshared"
                        | None -> "a selection shared across compounds is T4c, which this mode does not take"
                    Some $"`{plan.MaskName}` feeds {members.Length} compounds and its in-pass count serves one, the last (`{lastName}`): `{yName}` keeps the separate count / fill / gather passes ({t4c})"
                else None
            | _ -> None
        // T4c: this compound is a companion of an admitted shared selection.
        let shared = Map.tryFind yId sharedPlans
        // The compaction is planned (its decision is recorded below, once
        // the folds -- the outer kernels of T4a's matrix -- are known).
        let compaction =
            match shared, compactionDecision with
            | Some (plan, _), _ -> Some plan
            | None, Ok (plan, _) when enabled && sharedDecline.IsNone -> Some plan
            | _ -> None
        // ---- B4a (+ B4c): the folds over y's self-product -----------
        // Planned here, recorded after the compaction's decision.
        let foldRecords = ResizeArray<(unit -> unit)>()
        let foldPlans = ResizeArray<CompactionFoldPlan * IRCallable>()
        let finalPlan : CompactionPlan option ref = ref None
        for hi in yi + 1 .. arr.Length - 1 do
            let hb = arr.[hi]
            for (mid, info, k, init) in foldSites yId hb.Value do
                let what = $"fold of `{yName} * {yName}`"
                let declined (why: string) = foldRecords.Add(fun () -> decide hb.Name (Blade.Effects.Declined why) [ what ])
                let fk = resolveCallableIn funcs (fst (peelReynolds k))
                let elemOk =
                    match stripUnits at.ElemType with
                    | IRTScalar ETFloat64 -> true
                    | _ -> false
                if init.IsSome then declined "a seeded fold (v1 fuses the unseeded `reduce(y * y, op)`)"
                elif not (squareMap info yId) then declined $"the folded map is not the self-product `{yName} * {yName}` of `{yName}`'s own cells (v1 fuses only it: no outer product, no co-iteration with another array, no capture)"
                elif not elemOk then declined "the compound's element is not Float64"
                // The SNAPSHOT proof: the map materialized `y * y` at its
                // let; the fold reads `y` itself at the reduce. Equal only
                // if nothing writes between them -- and the map and the
                // reduce both sit inside `hb`'s value, so no write in that
                // value at all is the proof (a mutating call through a
                // `mut` parameter or an alias writes without naming `y`).
                elif payloadLifetime hb then
                    declined $"`{hb.Name}` contains an inline lifetime boundary"
                elif payloadWrites hb then
                    declined $"`{hb.Name}` may write (an assignment or a mutating call) between the map's materialization and the fold, so the map's snapshot could differ from `{yName}` at the fold"
                else
                match fk with
                | None -> declined "the fold kernel does not resolve to a callable"
                | Some f when f.IsOmpParallel || f.IsCudaKernel || f.IsMpiParallel ->
                    declined "the fold kernel requests a thread team or a device (`where omp` / cuda / mpi): v1 keeps the materialized route, which owns that census"
                | Some f when not (builtinAdd f) -> declined "the fold kernel is not the builtin `+` (v1 fuses `reduce(y * y, (+))`)"
                | Some f ->
                    // Feeding the chain inside the compaction pass moves
                    // its evaluation from `hb` up to `y`: sound when no
                    // one can change `y`'s cells in between. Without an
                    // alias analysis (a `let mut alias = y` shares the
                    // storage; a call through a `mut` parameter writes
                    // without naming `y`), the proof is NO write at all
                    // from `y` through `hb`, and no assignment to `y`.
                    // (T4a moves it further up, to the mask: the interval
                    // `keep`..`y` is write-free by the compaction's own
                    // admission, and the matrix below names it again.)
                    let inPass =
                        compaction.IsSome && not (assignedAnywhere yId)
                        && ([ yi + 1 .. hi ] |> List.forall (fun i -> not (payloadWrites arr.[i]) && not (payloadLifetime arr.[i])))
                    let plan =
                        { MapId = mid; CompoundId = yId; CompoundName = yName; HostName = hb.Name
                          FoldKernel = k; InPass = inPass
                          EmittedCpp = ref None; EmittedLlvm = ref None }
                    foldPlans.Add((plan, f))
                    foldRecords.Add(fun () ->
                        let site =
                            match finalPlan.Value with
                            | Some { Shared = Some s } ->
                                let firstName = match s.Members with (_, n, _) :: _ -> n | [] -> yName
                                if s.GatherAtMask && s.StaticN.IsSome then $"`{s.MaskName}`'s mask pass (T4a, the shared selection's one pass)"
                                elif s.GatherAtMask then $"`{s.MaskName}`'s mask pass (T4a) when the single pass runs, else the shared selection's joint pass at `{firstName}`"
                                else $"the shared selection's one joint pass at `{firstName}` (T4c)"
                            | Some p when p.GatherAtMask && p.StaticN.IsSome -> $"`{p.MaskName}`'s mask pass (T4a)"
                            | Some p when p.GatherAtMask -> $"`{p.MaskName}`'s mask pass (T4a) when the single pass runs, else `{yName}`'s compaction pass"
                            | _ -> $"`{yName}`'s compaction pass"
                        let evidence =
                            [ $"`{hb.Name}` = {what}: folded straight over `{yName}`'s compact buffer (m contiguous cells), no square pool; licensed: the lane form, repro: the unseeded serial chain from the first kept square, in kept order; every product rounded before its add in both lanes (C++ an asm rounding barrier, LLVM an undecorated fmul), as the stored product was; no write between the map and the fold"
                              (if inPass then $"under the serial (repro) form the chain is fed inside {site}, block by block, from completed cells only; the empty check (BL8003) stays at `{hb.Name}`"
                               else $"the fold runs at `{hb.Name}` (the compaction pass does not feed it)")
                              $"`{yName}` and every named binding stay stored" ]
                        if not enabled then
                            decide hb.Name (Blade.Effects.Declined "disabled by BLADE_COMPACTION_FUSION") evidence
                        else
                            CompactionTable.recordFold plan
                            decide hb.Name Blade.Effects.Applied evidence)
        // ---- T4: the selection (T4b census) and the gather (T4a) ------
        let compaction =
            match compaction, compactionDecision with
            | Some plan, _ when shared.IsSome ->
                // T4c: the group's record (the same evidence on every companion)
                let evidence = snd shared.Value
                CompactionTable.record plan
                decide yName Blade.Effects.Applied evidence
                Some plan
            | Some plan, Ok _ when v2 ->
                let kb = arr.[(indexOf plan.MaskId).Value]
                let ki = (indexOf plan.MaskId).Value
                let xName =
                    match indexOf plan.SourceId with
                    | Some xi -> arr.[xi].Name
                    | None -> "x"
                // T4b
                let census = positionsDemand yId yName
                let positionsNeeded = match census with Ok _ -> false | Error _ -> true
                let selection =
                    match census with
                    | Ok uses ->
                        let seen = if uses.IsEmpty then "no use" else String.concat ", " uses
                        $"selection: positions elided (census: {seen}; printing reads the compact buffer only) -- `{yName}`'s index keeps dense extent n and cardinality m, and any lookup through the absent table is an internal error (BL9002), never a read"
                    | Error why ->
                        $"selection: positions required by {why} -- `{yName}`'s index holds the ascending kept positions (keyed reads, BL8006 unchanged)"
                // T4a: selectionAdmits, the five parties of the motion
                // (the gather moves from `y` up to `keep`; an in-pass
                // chain moves from its fold's host up to `keep`).
                let predCallable =
                    match kb.Value with
                    | IRMask (_, pred) -> resolveCallableIn funcs pred
                    | _ -> None
                let wrote (e: IRExpr) = (facts e).MayWrite || writes e
                let inPassFolds = foldPlans |> Seq.filter (fun (fp, _) -> fp.InPass) |> List.ofSeq
                let between = [ ki + 1 .. yi - 1 ] |> List.map (fun i -> arr.[i])
                let sharers =
                    modul.CompoundInits |> Seq.filter (fun (KeyValue (_, (_, m))) ->
                        match m with IRVar (v, _) -> v = plan.MaskId | _ -> false) |> Seq.length
                let perCell =
                    elemBytes at.ElemType
                    |> Option.map (fun b -> b + (if positionsNeeded then 8L else 0L))
                let staticN = staticExtentOf kb.Type |> Option.orElse (staticExtentOf (match indexOf plan.SourceId with Some xi -> arr.[xi].Type | None -> IRTUnit))
                let capBytes = compactionSelCapBytes ()
                let posWord = if positionsNeeded then ", kept position" else ""
                let posStore = if positionsNeeded then " pos[q] = i;" else ""
                let posFill = " and the kept-position fill"
                let posNone = " (positions elided)"
                let producerParty =
                    match indexOf plan.SourceId with
                    | Some xi when payloadWrites arr.[xi] -> Error $"the producer `{xName}` may write (its initializer or side-table payload, through the shared write analysis)"
                    | Some _ -> Ok $"producer `{xName}`: no write"
                    | None -> Error $"the producer `{xName}` is not a module binding the write analysis can see"
                let predicateParty =
                    match predCallable with
                    | Some pc when not (wrote pc.Body) -> Ok $"predicate of `{kb.Name}`: no write (pure, total)"
                    | Some _ -> Error $"the predicate of `{kb.Name}` may write"
                    | None -> Error $"the predicate of `{kb.Name}` does not resolve to a callable"
                let foldParty =
                    match inPassFolds |> List.tryFind (fun (_, f) -> wrote f.Body) with
                    | Some (fp, _) -> Error $"the outer fold kernel of `{fp.HostName}` may write"
                    | None ->
                        if inPassFolds.IsEmpty then Ok "outer fold kernel(s): none fed in the pass"
                        else Ok ("outer fold kernel(s): " + (inPassFolds |> List.map (fun (fp, _) -> $"`{fp.HostName}` (+)") |> String.concat ", ") + ": no write")
                let seedParty = Ok "seed(s): none (the compaction has none; every planned fold is unseeded and keeps its BL8003 check at its own site)"
                // Each intervening binding's WHOLE payload (its value and
                // the side-table operands its site runs -- a random key, a
                // compound's operands) and any provider write at it: the
                // gather moves ahead of all of them.
                let betweenParty =
                    let offends (b: IRBinding) =
                        if payloadLifetime b then Some "contains an inline lifetime boundary"
                        elif payloadWrites b then Some "may write (its value or side-table payload)"
                        elif modul.ProviderWrites.ContainsKey b.Id then Some "is a provider write (an external effect)"
                        elif readsMask plan.MaskId b then Some $"reads `{kb.Name}`"
                        else None
                    match between |> List.tryPick (fun b -> offends b |> Option.map (fun why -> (b, why))) with
                    | Some (b, why) -> Error $"`{b.Name}` (between `{kb.Name}` and `{yName}`) {why}"
                    | None ->
                        if between.IsEmpty then Ok $"between `{kb.Name}` and `{yName}`: no binding"
                        else Ok ($"between `{kb.Name}` and `{yName}`: " + (between |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", ") + $": no write in any payload, no provider write, no read of `{kb.Name}`")
                let parties = [ producerParty; predicateParty; foldParty; seedParty; betweenParty ]
                let matrix =
                    "selectionAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                    + (parties |> List.map (function Ok s -> s | Error s -> "DECLINES: " + s) |> String.concat "; ")
                let srcStreamed = streamedSrc.Contains plan.SourceId
                let gatherWhy : Result<string, string> =
                    if srcStreamed then
                        Error $"the source `{xName}` is STREAMED (no array to gather from at the mask site; the gather is a sweep of the store at `{yName}`)"
                    elif not gatherOn then
                        Error "opt-in this round (BLADE_COMPACTION_FUSION=gather): T4a's own measured gain on maskagg, 0.18-0.36 ms, is under the round's 0.4 ms acceptance line"
                    else
                    match parties |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
                    | Some why -> Error why
                    | None ->
                    if sharers > 1 then Error $"`{kb.Name}` feeds {sharers} compounds (a selection shared across compounds is T4c: not taken here)"
                    else
                    match perCell with
                    | None -> Error "the compound's element has no fixed cell width the cap can bound"
                    | Some pc ->
                        let capCells = capBytes / pc
                        match staticN with
                        | Some n when n < capCells ->
                            Ok $"gather: at the mask site -- `{kb.Name}`'s pass sweeps `{xName}` once (per 256-cell block: the predicate loop, then the branchless gather of the kept value{posWord} at q over the L1-resident block): n + 1 = {n + 1L} private cells per buffer ({fmtBytes ((n + 1L) * pc)} <= sel_cap {fmtBytes capBytes}), m + 1 of them touched by the n branchless stores"
                        | Some n ->
                            Error $"(n + 1) x {pc} B = {fmtBytes ((n + 1L) * pc)} exceeds sel_cap {fmtBytes capBytes} (BLADE_TILE_TUNE sel_cap): the two-pass form reserves only m + 1 cells"
                        | None ->
                            Ok $"gather: at the mask site when n < {capCells} at run time (n + 1 private cells x {pc} B within sel_cap {fmtBytes capBytes}), else the compound-site pass -- `{kb.Name}`'s pass sweeps `{xName}` once; m + 1 cells touched by the n branchless stores"
                let gatherAtMask, gatherLine =
                    match gatherWhy with
                    | Ok line -> true, line
                    | Error why -> false, $"gather: declined -- {why}; the gather stays at `{yName}` (one fill+gather pass, B4)"
                let capCells = match perCell with Some pc -> capBytes / pc | None -> 0L
                let plan' =
                    { plan with PositionsNeeded = positionsNeeded; GatherAtMask = gatherAtMask
                                StaticN = staticN; SelCapCells = capCells }
                let passLine =
                    if srcStreamed then
                        $"`{kb.Name}`'s count is taken in its own sweep of the STREAMED `{xName}` (one block of the store's chunk edge at a time; `{kb.Name}` is stored, 1 byte per cell); the value gather{(if positionsNeeded then posFill else posNone)} is ONE more storage-order sweep of the store at `{yName}`: `v = blk[q]; {yName}[q'] = v;{posStore} q' += {kb.Name}[i]` (m + 1 cells per buffer); no array of `{xName}`'s values exists (G132 / ST06)"
                    elif gatherAtMask then
                        $"`{kb.Name}`'s count AND `{yName}`'s gather run in `{kb.Name}`'s own mask pass: `v = {xName}[i]; b = pred(v); {kb.Name}[i] = b; {yName}[q] = v;{posStore} q += b` per 256-cell block -- no count pass and one sweep of `{xName}` and `{kb.Name}` from memory (the gather loop re-reads each block's cells and bits from L1: two loads of `{xName}`, a bit store and load, and a value store per source cell); `{kb.Name}` stays stored"
                    else
                        $"`{kb.Name}`'s count is taken in its own mask pass (no count pass); the value gather{(if positionsNeeded then posFill else posNone)} is ONE branchless pass at `{yName}`: `v = {xName}[i]; {yName}[q] = v;{posStore} q += {kb.Name}[i]` (m + 1 cells per buffer, the slack never exposed); `{kb.Name}` stays stored"
                let evidence =
                    [ passLine; selection; gatherLine ]
                    @ (if gatherOn then [ matrix ] else [])
                    @ [ "stable, ascending, serial: the same compaction in fewer sweeps" ]
                CompactionTable.record plan'
                decide yName Blade.Effects.Applied evidence
                Some plan'
            | Some plan, Ok (_, evidence) ->
                CompactionTable.record plan
                decide yName Blade.Effects.Applied evidence
                Some plan
            | None, Ok _ when enabled && sharedDecline.IsSome ->
                decide yName (Blade.Effects.Declined sharedDecline.Value) []
                None
            | _, Ok (_, evidence) ->
                decide yName (Blade.Effects.Declined "disabled by BLADE_COMPACTION_FUSION") evidence
                None
            | _, Error why ->
                decide yName (Blade.Effects.Declined why) []
                None
        finalPlan.Value <- compaction
        for r in foldRecords do r ()
    | _ -> ()

/// T4c's SHARED-SELECTION decision for one mask (lifted from planCompactionFusion, cleanup item 2): the
/// setup it reads through the context.
let private cfGroupDecision (ctx: CfContext) (kId: IRId) (ms: (IRId * int) list) : Result<SharedSelection * string list, string> option =
    let { indexOf = indexOf; arr = arr; enabled = enabled; sharedOn = sharedOn; streamedSrc = streamedSrc;
          modul = modul; plainAxisOf = plainAxisOf; assignedAnywhere = assignedAnywhere;
          rank1Compound = rank1Compound; elemBytes = elemBytes; elemOf = elemOf;
          stableExtentExpr = stableExtentExpr; shapeRoot = shapeRoot; predicateAdmission = predicateAdmission;
          funcs = funcs; facts = facts; writes = writes; payloadWrites = payloadWrites; foldSites = foldSites;
          payloadLifetime = payloadLifetime; readsMask = readsMask; positionsDemand = positionsDemand;
          staticExtentOf = staticExtentOf; gatherOn = gatherOn; fmtBytes = fmtBytes } = ctx
    match indexOf kId with
    | None -> None
    | Some ki ->
    let kb = arr.[ki]
    match kb.Value with
    | IRMask (IRVar (srcId, _), _) when enabled && sharedOn && not ms.IsEmpty && not (streamedSrc.Contains srcId) ->
        let dense (cid: IRId) = fst modul.CompoundInits.[cid]
        let single =
            match ms with
            | [ (cid, _) ] -> (match dense cid with IRVar (d, _) -> d = srcId | _ -> false)
            | _ -> false
        if single then None
        else
        Some (
            let (firstId, firstI) = List.head ms
            let (lastId, lastI) = List.last ms
            let firstName = arr.[firstI].Name
            let lastName = arr.[lastI].Name
            let memberIds = ms |> List.map fst |> Set.ofList
            let xName = match indexOf srcId with Some xi -> arr.[xi].Name | None -> "x"
            let srcAxis = match indexOf srcId with Some xi -> plainAxisOf arr.[xi].Type | None -> None
            // the companions' sources, each checked; first failure wins
            // THE POSITIVE STORAGE PROOF (`storedArrayProof`, membership
            // hoisting's, through alias chains: a virtual array, a streamed
            // read, a deferred root and every unproved form decline) of the
            // mask source and of EVERY companion source -- the joint pass
            // reads each source's pool by name -- with the assignment check
            // over every binding of each alias chain.
            let storedProof = storedArrayProof modul
            let proveSource (b: IRBinding) : Result<string * IRBinding list, string> =
                match storedProof b with
                | Error why -> Error why
                | Ok (how, chain) ->
                    match chain |> List.tryFind (fun c -> assignedAnywhere c.Id) with
                    | Some c -> Error (sprintf "`%s` (its alias chain) is assigned in the program" c.Name)
                    | None -> Ok (how, chain)
            let memberWhy =
                ms |> List.tryPick (fun (cid, ci) ->
                    let yb = arr.[ci]
                    if not (rank1Compound yb) then Some $"`{yb.Name}` over `{kb.Name}` has trailing dims or a rank-k index (T4c shares a rank-1 selection only)"
                    elif assignedAnywhere cid then Some $"the companion `{yb.Name}` is written (assigned) in the program"
                    else
                    match dense cid with
                    | IRVar (cj, _) ->
                        (match indexOf cj with
                         | None -> Some $"the dense operand of `{yb.Name}` is not a top-level binding the write analysis can see"
                         | Some cjI ->
                            let cb = arr.[cjI]
                            if cjI >= firstI then Some $"the source `{cb.Name}` of `{yb.Name}` is bound after the first companion `{firstName}`, where the one joint pass runs (v1)"
                            elif (match proveSource cb with Error _ -> true | Ok _ -> false) then
                                (match proveSource cb with Error why -> Some $"the source `{cb.Name}` of `{yb.Name}` is not proved stored: {why} (the joint pass reads every source's pool by name)" | Ok _ -> None)
                            elif (elemBytes (elemOf yb.Type)).IsNone then Some $"`{yb.Name}`'s element has no fixed cell width"
                            else
                            match plainAxisOf cb.Type, srcAxis with
                            // G283: equal extent EXPRESSIONS are equal
                            // evaluated shapes only when nothing they read
                            // can change (a literal; never-assigned
                            // bindings, extent reads and arithmetic, no
                            // call) or the two sources share a SHAPE ROOT
                            // (shapeRoot). A run-time extent is often the
                            // placeholder `?` on both sides, which proves
                            // nothing (two `Float64(0..n)` with `n` rebound
                            // in between). The joint pass walks the mask's
                            // grid and reads every source at each cell, so
                            // anything else keeps each compound's own
                            // passes, whose extent guard (BL8011) runs. (The
                            // mask source itself -- never assigned:
                            // proveSource -- is its own shape.)
                            | Some a, Some s when a.Extent = s.Extent && (cj = srcId || stableExtentExpr a.Extent || shapeRoot 0 cj = shapeRoot 0 srcId) -> None
                            | Some a, Some s when a.Extent = s.Extent ->
                                Some $"the source `{cb.Name}` of `{yb.Name}` and the mask source `{xName}` have extents known only at run time and no common shape root (pointwise maps of one never-assigned binding): equal run-time extent expressions are not equal evaluated shapes (G283), so each compound keeps its own passes and its extent check"
                            | _ -> Some $"the source `{cb.Name}` of `{yb.Name}` is not provably over `{xName}`'s index space (BL3016 territory: never admitted)")
                    | _ -> Some $"the dense operand of `{yb.Name}` is computed in place (T4c gathers stored companions)")
            let sources = ms |> List.choose (fun (cid, _) -> match dense cid with IRVar (cj, _) -> Some cj | _ -> None) |> List.distinct
            let sourceNames = sources |> List.map (fun s -> match indexOf s with Some i -> $"`{arr.[i].Name}`" | None -> "?") |> String.concat ", "
            let names = ms |> List.map (fun (_, ci) -> $"`{arr.[ci].Name}`") |> String.concat ", "
            let between = [ ki + 1 .. lastI ] |> List.filter (fun i -> not (memberIds.Contains arr.[i].Id)) |> List.map (fun i -> arr.[i])
            if ki >= firstI then Error $"`{kb.Name}` is not bound before its companions"
            elif assignedAnywhere kId then Error $"`{kb.Name}` is assigned"
            else
            match memberWhy with
            | Some why -> Error why
            | None ->
            let maskSourceWhy =
                match indexOf srcId with
                | None -> Some $"the mask source of `{kb.Name}` is not a top-level binding the write analysis can see"
                | Some xi ->
                    match proveSource arr.[xi] with
                    | Error why -> Some $"the mask source `{xName}` is not proved stored: {why}"
                    | Ok _ -> None
            match maskSourceWhy with
            | Some why -> Error why
            | None ->
            match predicateAdmission kb with
            | Error why -> Error why
            | Ok predLine ->
            // ---- selectionAdmits, the five parties of T4c's motion ----
            let pc = match kb.Value with IRMask (_, pred) -> resolveCallableIn funcs pred | _ -> None
            let wrote (e: IRExpr) = (facts e).MayWrite || writes e
            let producerParty =
                // every binding of every source's alias chain, the mask
                // source's included (x is a producer even when it is no
                // companion's source)
                let chains =
                    (srcId :: sources) |> List.distinct |> List.collect (fun s ->
                        match indexOf s with
                        | Some i -> (match proveSource arr.[i] with Ok (_, ch) -> ch | Error _ -> [ arr.[i] ])
                        | None -> [])
                    |> List.distinctBy (fun b -> b.Id)
                match chains |> List.tryFind payloadWrites with
                | Some b -> Error $"the producer `{b.Name}` may write (its initializer or side-table payload, through the shared write analysis)"
                | None -> Ok $"producers {sourceNames} and the mask source `{xName}`: proved stored through their alias chains (storedArrayProof), never assigned, no write in any payload"
            let predicateParty =
                match pc with
                | Some c when wrote c.Body -> Error $"the predicate of `{kb.Name}` may write"
                | Some c when c.IsOmpParallel || c.IsCudaKernel || c.IsMpiParallel -> Error $"the predicate of `{kb.Name}` requests a thread team or a device"
                | Some _ -> Ok $"predicate of `{kb.Name}`: no write (its site is unchanged)"
                | None -> Error $"the predicate of `{kb.Name}` does not resolve to a callable"
            // The outer fold kernels and seeds of every fold over a
            // companion: only an unseeded builtin-`+` self-product fold
            // (B4a) is ever fed in the joint pass (its serial chain,
            // under the repro form); every other fold, and every seed,
            // stays at its own site.
            let foldSitesAll =
                [ for (cid, ci) in ms do
                    for hi in ci + 1 .. arr.Length - 1 do
                        for (_, _, k, init) in foldSites cid arr.[hi].Value do
                            yield (arr.[hi], k, init) ]
            let foldParty =
                let writing =
                    foldSitesAll |> List.tryFind (fun (_, k, _) ->
                        match resolveCallableIn funcs (fst (peelReynolds k)) with
                        | Some kc -> wrote kc.Body
                        | None -> true)
                match writing with
                | Some (hb, _, _) -> Ok $"outer fold kernel(s): {foldSitesAll.Length} over the companions; `{hb.Name}`'s kernel may write (or does not resolve), so it is never fed and stays at its own site"
                | None ->
                    if foldSitesAll.IsEmpty then Ok "outer fold kernel(s): none over the companions"
                    else Ok $"outer fold kernel(s): {foldSitesAll.Length} over the companions, none writes; only an unseeded builtin `+` self-product fold may have its serial chain fed in the joint pass"
            let seedParty =
                let seeded = foldSitesAll |> List.filter (fun (_, _, s) -> s.IsSome)
                match seeded |> List.tryFind (fun (_, _, s) -> match s with Some e -> wrote e | None -> false) with
                | Some (hb, _, _) -> Ok $"seed(s): `{hb.Name}`'s seed may write -- it is never moved (seeded folds stay at their own sites)"
                | None -> Ok $"seed(s): {seeded.Length} seeded fold(s) over the companions, none moved; every fed chain is unseeded and keeps its BL8003 check at its own site"
            // every binding in (mask, last companion] but the companions:
            // its WHOLE interval payload (`intervalPayloadOf`: the value,
            // its side-table operands, a provider write's source) through
            // the shared write analysis
            let payloadOf = intervalPayloadOf modul
            let betweenParty =
                let offends (b: IRBinding) =
                    if payloadLifetime b then Some "contains an inline lifetime boundary"
                    elif wrote (payloadOf b.Id b.Value) || payloadWrites b then Some "may write (its value or side-table payload)"
                    elif intervalHasProviderEffect modul b.Id then Some "is a provider write (an external effect)"
                    elif readsMask kId b then Some $"reads `{kb.Name}` between the mask and the last companion"
                    else None
                match between |> List.tryPick (fun b -> offends b |> Option.map (fun why -> (b, why))) with
                | Some (b, why) -> Error $"`{b.Name}` (between `{kb.Name}` and `{lastName}`) {why}"
                | None ->
                    if between.IsEmpty then Ok $"between `{kb.Name}` and `{lastName}`: no binding but the companions"
                    else Ok ($"between `{kb.Name}` and `{lastName}`: " + (between |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", ") + $": no write in any payload, no provider write, no read of `{kb.Name}` (a read of an earlier companion is fine: its data is its own and the index is complete)")
            let parallelParty = Ok "parallel: none -- the mask pass, the joint pass and every fed chain are serial (a predicate requesting a team or a device declines above; a `where omp` fold is never planned, B4a), so no parallel body runs moved work and callbackTable has no candidate set to audit"
            let parties = [ producerParty; predicateParty; foldParty; seedParty; betweenParty; parallelParty ]
            let matrix =
                "selectionAdmits (five parties, each through Optimize.scheduleMayWriteIn): "
                + (parties |> List.map (function Ok s -> s | Error s -> "DECLINES: " + s) |> String.concat "; ")
            match parties |> List.tryPick (function Error s -> Some s | Ok _ -> None) with
            | Some why -> Error $"{why} -- {matrix}"
            | None ->
            // ---- T4b per companion, united ----
            let census = ms |> List.map (fun (cid, ci) -> (arr.[ci].Name, positionsDemand cid arr.[ci].Name))
            let demanded = census |> List.choose (fun (n, r) -> match r with Error why -> Some (n, why) | Ok _ -> None)
            let positionsNeeded = not demanded.IsEmpty
            let selection =
                match demanded with
                | [] -> $"selection: positions elided (every companion's census: folds, `extents`, derived maps or printing only) -- the shared index keeps dense extent n and cardinality m; a lookup through the absent table is an internal error (BL9002), never a read"
                | (n, why) :: _ -> $"selection: positions required by {why} (`{n}`) -- the SHARED index holds the ascending kept positions for every companion (keyed reads, BL8006 unchanged, each at its own site)"
            // ---- T4a for the group ----
            let perCell =
                ms |> List.fold (fun acc (_, ci) ->
                    match acc, elemBytes (elemOf arr.[ci].Type) with
                    | Some a, Some b -> Some (a + b)
                    | _ -> None) (Some (if positionsNeeded then 8L else 0L))
            let staticN = staticExtentOf kb.Type |> Option.orElse (match indexOf srcId with Some xi -> staticExtentOf arr.[xi].Type | None -> None)
            let capBytes = compactionSelCapBytes ()
            let capCells = match perCell with Some pc when pc > 0L -> capBytes / pc | _ -> 0L
            let gatherWhy : Result<string, string> =
                if not gatherOn then Error "opt-in this round (BLADE_COMPACTION_FUSION=gather)"
                elif ms.Length > 4 then Error $"{ms.Length} companions (the mask-site gather takes J <= 4)"
                else
                match sources |> List.tryFind (fun s -> match indexOf s with Some i -> i >= ki | None -> true) with
                | Some s -> Error $"the source `{(match indexOf s with Some i -> arr.[i].Name | None -> string s)}` is bound after `{kb.Name}` (the mask pass cannot read it)"
                | None ->
                let andPos = if positionsNeeded then " and the positions" else ""
                match perCell, staticN with
                | None, _ -> Error "a companion's element has no fixed cell width the cap can bound"
                | Some pc, Some n when n < capCells ->
                    Ok $"gather: at the mask site -- `{kb.Name}`'s pass sweeps the sources once (per 256-cell block: the predicate loop, then the branchless gather of every companion at q): n + 1 = {n + 1L} private cells per buffer ({fmtBytes ((n + 1L) * pc)} over all {ms.Length} companions{andPos} <= sel_cap {fmtBytes capBytes}), m + 1 touched"
                | Some pc, Some n -> Error $"(n + 1) x {pc} B = {fmtBytes ((n + 1L) * pc)} over every companion buffer exceeds sel_cap {fmtBytes capBytes} (BLADE_TILE_TUNE sel_cap): the joint pass reserves only m + 1 cells per buffer"
                | Some _, None -> Ok $"gather: at the mask site when n < {capCells} at run time (n + 1 private cells per buffer over every companion within sel_cap {fmtBytes capBytes}), else the joint pass at `{firstName}`"
            let gatherAtMask, gatherLine =
                match gatherWhy with
                | Ok line -> true, line
                | Error why -> false, $"gather: declined -- {why}; the joint gather runs at the first companion `{firstName}`"
            let site = if gatherAtMask then $"`{kb.Name}`'s mask pass" else $"the first companion `{firstName}`"
            let colsShown = ms |> List.map (fun (cid, ci) -> let s = (match dense cid with IRVar (d, _) -> (match indexOf d with Some i -> arr.[i].Name | None -> "c") | _ -> "c") in $"{arr.[ci].Name}[q] = {s}[i];") |> String.concat " "
            let posStore = if positionsNeeded then " pos[q] = i;" else ""
            let posFillNote = if positionsNeeded then ", its kept positions filled by the one joint pass in " + site else ""
            let s =
                { MaskId = kId; MaskName = kb.Name; Members = ms |> List.map (fun (cid, ci) -> (cid, arr.[ci].Name, (match dense cid with IRVar (d, _) -> d | _ -> srcId)))
                  ElemTypes = ms |> List.map (fun (cid, ci) -> (cid, elemOf arr.[ci].Type)) |> Map.ofList
                  PositionsNeeded = positionsNeeded; GatherAtMask = gatherAtMask; StaticN = staticN; SelCapCells = capCells
                  BuiltCpp = ref None; BuiltLlvm = ref None }
            let evidence =
                [ (if ms.Length = 1 then $"one selection index for its single companion ({names}, whose source is not the mask's own: the shared-selection path), freed once after `{lastName}`" else $"one selection index shared by {ms.Length} companions ({names}), freed once after `{lastName}`")
                  $"the index: counted in `{kb.Name}`'s own mask pass (cardinality m){posFillNote}; COMPLETE once the first companion `{firstName}` is bound (built at its site, before its wrapper) -- the invariant a later companion relies on: a companion read before a later one is built reads its own data and the already-complete index"
                  $"lifetime: the plan owns the index; each companion's Compound wrapper borrows it and owns only its own compact data store (`deallocate_compound_shared_index`); in a scoped frame the ONE index-only free (`deallocate_compound_index_only`) would be registered with the last companion `{lastName}` -- compound() is top-level only (BL6002) and main's top level has no scope, so NO registration and NO free is executed in either lane (program lifetime); no companion frees the index"
                  $"ONE branchless pass in {site} over `{xName}`'s axis: `b = {kb.Name}[i]; {colsShown}{posStore} q += b` (m + 1 cells per buffer, the slack never exposed) -- ascending, serial; identical values, cardinality and positions to each compound's own count / fill / gather passes ({ms.Length} index builds -> 1, {ms.Length} gathers -> 1 pass)"
                  selection
                  gatherLine
                  predLine
                  matrix
                  $"`{kb.Name}` and every companion's data store ({names}) stay stored; the folds over them keep their own chains, seeds and BL8003 sites" ]
            Ok (s, evidence))
    | _ -> None

/// Plan compaction fusion over the module's top-level bindings (`compound`
/// is top-level only, BL6002). Records into `CompactionTable` and the
/// decision collector (`compaction-fusion`); the IR is untouched.
let planCompactionFusion (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let mode = compactionFusionMode ()
    let enabled = mode <> CompactionFusionOff
    let v2 = mode = CompactionFusionT4 || mode = CompactionFusionGather || mode = CompactionFusionUnshared
    // T4c (the shared selection and the membership-probe certificate): the
    // default and `gather`; `unshared` is exactly the pre-T4c v2.
    let sharedOn = mode = CompactionFusionT4 || mode = CompactionFusionGather
    let gatherOn = mode = CompactionFusionGather
    // G132 / ST06 (lane G item G3): a STREAMED source (`.stream`) has no
    // array; the C++ lane's mask sweeps the store into the stored mask (the
    // count taken there) and the compound's gather is one more sweep of the
    // store. A mask-site gather and a shared selection read the source by
    // name, so neither is planned over one (the record says so).
    let streamedSrc = streamedProviderIds modul
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "compaction-fusion"; Version = (if v2 then 2 else 1)
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let arr = List.toArray modul.Bindings
    let indexOf (id: IRId) = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let refs (e: IRExpr) = collectVarRefsIR e
    // `id` is the target of an assignment somewhere in `e` (`y = ..`, `y(k) = ..`).
    let assignsTo (id: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n ->
            match n with
            | IRAssign (target, _) when Set.contains id (refs target) -> hit <- true
            | _ -> ()) e
        hit
    let assignedAnywhere (id: IRId) =
        (arr |> Array.exists (fun b -> bindingPayload modul b |> List.exists (assignsTo id)))
        || (modul.Functions |> List.exists (fun f -> assignsTo id f.Body))
    // Each binding's ACTUAL execution payload (its value plus the side-table
    // operands its site runs -- the value alone is a placeholder for a
    // compound / sparse / random binding), through the shared write analysis
    // and the conservative facts check.
    let payloadWrites (b: IRBinding) =
        bindingPayload modul b |> List.exists (fun e -> (facts e).MayWrite || writes e)
    let payloadLifetime (b: IRBinding) = bindingPayload modul b |> List.exists lifetimeBarrier
    let payloadRefs (b: IRBinding) = bindingPayload modul b |> List.map refs |> Set.unionMany
    // Does `b`'s payload READ mask `kId`? A sibling `compound(d, kId)` names
    // the mask only as its own mask operand -- it reads the bits and writes
    // nothing through them -- so that one occurrence is not a read here.
    let readsMask (kId: IRId) (b: IRBinding) =
        let ops =
            match Map.tryFind b.Id modul.CompoundInits with
            | Some (d, IRVar (m, _)) when m = kId -> b.Value :: d :: (bindingSideOperands modul b.Id |> List.skip 2)
            | _ -> bindingPayload modul b
        ops |> List.exists (fun e -> Set.contains kId (refs e))
    // Every id a payload reaches: its references, and transitively the bodies
    // and captures of every callable it names (and of let aliases of them) --
    // what a payload could name, and therefore write through a `mut`
    // parameter, without naming it at its own site.
    let callableById =
        (modul.Functions @ programFuncs) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let letValueById = arr |> Array.map (fun b -> (b.Id, b.Value)) |> Map.ofArray
    let transitiveRefs (b: IRBinding) : Set<IRId> =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let work = System.Collections.Generic.Stack<IRId>(payloadRefs b)
        while work.Count > 0 do
            let id = work.Pop()
            if seen.Add id then
                match Map.tryFind id callableById with
                | Some fn ->
                    for r in refs fn.Body do work.Push r
                    for c in fn.Captures do work.Push c.Id
                | None ->
                    match Map.tryFind id letValueById with
                    | Some (IRVar (v, _)) -> work.Push v
                    | _ -> ()
        Set.ofSeq seen
    let isParamRef (p: IRParam) (e: IRExpr) =
        match e with
        | IRVar (v, _) -> v = p.VarId
        | IRParam (_, i, _) -> i = p.Index
        | _ -> false
    let isFloatTy (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32) -> true
        | _ -> false
    // A PURE, TOTAL scalar body over its one parameter: literals, the
    // parameter, arithmetic that cannot trap (no integer `/` `%` `^`, no
    // float->int cast), comparisons, logic, `if`. Anything else -- a call, a
    // capture, an intrinsic that may be domain-checked -- is not judged.
    // (The whitelist itself is `totalScalarOver`, shared since round 3 W3-2
    // with the hash equijoin's kernel admission.)
    let totalScalar (p: IRParam) (e: IRExpr) = totalScalarOver [ p ] e
    let squareMap (info: ApplyInfo) (yId: IRId) : bool =
        let overY = info.Arrays |> List.forall (function IRVar (a, _) -> a = yId | _ -> false)
        let shapeOk =
            not info.Arrays.IsEmpty && overY && not info.HasReynolds && info.KernelOutputRank = 0
            && (info.Arrays.Length = 1 || info.IsCoIteration)
        shapeOk
        && (match resolveCallableIn funcs (fst (peelReynolds info.Kernel)) with
            | Some k when k.Captures.IsEmpty && not k.IsCudaKernel && not k.IsMpiParallel ->
                (match k.Params, k.Body with
                 | [ p ], IRBinOp (IRElementwise, IRMul, a, b, _) -> isParamRef p a && isParamRef p b
                 | [ p0; p1 ], IRBinOp (IRElementwise, IRMul, a, b, _) when info.Arrays.Length = 2 ->
                    (isParamRef p0 a || isParamRef p1 a) && (isParamRef p0 b || isParamRef p1 b)
                 | _ -> false)
            | _ -> false)
    let builtinAdd (k: IRCallable) =
        match k.Params, k.Body with
        | [ p0; p1 ], IRBinOp (_, IRAdd, l, r, _) ->
            (isParamRef p0 l && isParamRef p1 r) || (isParamRef p1 l && isParamRef p0 r)
        | _ -> false
    // The lifted `let m = <map over y>` whose ONE reader is `reduce(m, k, init)`
    // inside a host binding's value.
    let foldSites (yId: IRId) (hostValue: IRExpr) : (IRId * ApplyInfo * IRExpr * IRExpr option) list =
        let found = ResizeArray()
        iterIRExpr (fun n ->
            match n with
            | IRLet (mid, IRCompute (IRApplyCombinator info), body)
                when info.Arrays |> List.exists (function IRVar (a, _) -> a = yId | _ -> false) ->
                let mutable count = 0
                let mutable site = None
                iterIRExpr (fun m ->
                    match m with
                    | IRVar (v, _) when v = mid -> count <- count + 1
                    | ReduceOfNamed (v, _, k, init) when v = mid -> site <- Some (k, init)
                    | _ -> ()) body
                (match site with
                 | Some (k, init) when count = 1 -> found.Add((mid, info, k, init))
                 | _ -> ())
            | _ -> ()) hostValue
        List.ofSeq found
    // ---- T4b: the positions DEMAND CERTIFICATE ---------------------------
    // Fail-safe by construction: `Ok uses` only when EVERY occurrence of `y`
    // -- and of every elementwise map derived from it, which shares its index
    // (the C++ map output `Compound { new T[card], y.idx, .. }`, the LLVM
    // output's GCompact shape) -- is one this census recognizes as reading
    // the compact buffer or the cardinality alone: a fold, `extents`, a
    // derived map (whose own uses are then certified the same way), or
    // printing (both lanes print `size()` cells of `.data`). The first other
    // occurrence -- a keyed read, an alias, an argument, a tuple, an
    // assignment, a capture, a provider write, an operand of another side
    // table, a type extent other than `extents(y)`, or anything the census
    // does not know -- is `Error <what demands the table>`. Every module
    // binding, every function body and signature (this module's and the
    // program's), every capture list and every side table is scanned.
    let peelMap (e: IRExpr) : ApplyInfo option =
        match e with
        | IRApplyCombinator info | IRCompute (IRApplyCombinator info) -> Some info
        | _ -> None
    let allCallables =
        (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let positionsDemand = cfPositionsDemand peelMap refs arr allCallables modul foreignRefs
    // The element's bytes per cell (both lanes' pool widths), for the cap.
    let elemBytes (t: IRType) : int64 option =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETInt64 | ETComplex64) -> Some 8L
        | IRTScalar (ETFloat32 | ETInt32) -> Some 4L
        | IRTScalar ETBool -> Some 1L
        | IRTScalar ETComplex128 -> Some 16L
        | _ -> None
    let staticExtentOf (t: IRType) : int64 option =
        match stripUnits t with
        | ArrayElem a ->
            (match a.IndexTypes with
             | [ ix ] -> (match ix.Extent with IRLit (IRLitInt n) when n >= 0L -> Some n | _ -> None)
             | _ -> None)
        | _ -> None
    let fmtBytes (b: int64) =
        if b >= 1048576L then $"{float b / 1048576.0:F1} MiB" elif b >= 1024L then $"{float b / 1024.0:F1} KiB" else $"{b} B"
    // ---- Part B: the MEMBERSHIP-PROBE CERTIFICATE ------------------------
    // `totalScalar` (above) judges literal arithmetic, comparisons and logic
    // over the one parameter, and rejects every call, capture and `contains`
    // -- it is NOT widened here. A predicate built from `contains(B, t)`
    // probes (`contains(B, v)`, `!contains(B, v)`, `contains(B, v) && p(v)`,
    // under `&&` / `||` / `!` / `if`) is certified pure and total SEPARATELY,
    // and only when membership hoisting ADMITTED this host's probe of B: the
    // T1 planner's own plan (`MemberHoistTable`, its five-party admission
    // record quoted as the proof) is the input -- the set is a stored,
    // never-assigned, rank-1 Int64 binding bound before the host whose
    // producer and consumer write nothing -- never a re-derivation. What
    // the certificate adds is the totality of everything else: every target
    // and every other operand passes `totalScalar` (no panic site), and the
    // lambda captures nothing but the certified set(s). A probe T1 declined
    // (or BLADE_MEMBER_HOIST=0) stays the per-probe scan and the predicate is
    // not certified. The snapshot / effect proof is unchanged: the predicate
    // party is still `scheduleMayWriteIn` over its body.
    let probeCertificate (kb: IRBinding) (pc: IRCallable) (p: IRParam) : Result<string, string> =
        let plans = MemberHoistTable.admittedOf kb.Id |> List.filter (fun hp -> hp.Kernels.Contains pc.Id)
        let planOf (sid: IRId) = plans |> List.tryFind (fun hp -> hp.SetId = sid)
        let used = ResizeArray<MemberHoistPlan>()
        let mutable why : string option = None
        let fail (s: string) = (if why.IsNone then why <- Some s); false
        let rec ok (e: IRExpr) =
            match e with
            | IRContains (IRVar (sid, _), t) ->
                (match planOf sid with
                 | None ->
                    let setName = match indexOf sid with Some si -> arr.[si].Name | None -> $"v{sid}"
                    fail $"member-hoist did not admit `{kb.Name}`'s probe of `{setName}` (its `[member-hoist v1] {kb.Name}` record says why, or BLADE_MEMBER_HOIST=0): the probe stays the per-probe scan"
                 | Some hp ->
                    if not (totalScalar p t) then fail $"the target of `{kb.Name}`'s probe of `{hp.SetName}` is not a pure, total scalar of the cell (a panic site, a call or a capture)"
                    else
                        (if not (used |> Seq.exists (fun u -> u.SetId = hp.SetId)) then used.Add hp)
                        true)
            | IRContains _ -> fail $"`{kb.Name}`'s predicate probes a set expression, not a named stored set"
            | IRBinOp (_, (IRAnd | IROr), l, r, _) -> ok l && ok r
            | IRUnaryOp (IRNot, x) -> ok x
            | IRIf (c, t, f) -> ok c && ok t && ok f
            | _ -> totalScalar p e || fail $"`{kb.Name}`'s predicate holds an operand that is not a pure, total scalar of the cell"
        let bodyOk = ok pc.Body
        let certifiedIds = used |> Seq.map (fun hp -> hp.SetId) |> Set.ofSeq
        let strayCapture = pc.Captures |> List.tryFind (fun c -> not (certifiedIds.Contains c.Id))
        if not bodyOk then Error (defaultArg why "the predicate is not certified")
        elif used.Count = 0 then Error "the predicate holds no certified `contains` probe"
        else
        match strayCapture with
        | Some c -> Error $"`{kb.Name}`'s predicate captures `{c.Name}`, which is not a certified set"
        | None ->
            let sets = used |> Seq.map (fun hp -> $"`{hp.SetName}`") |> String.concat ", "
            let proofs = used |> Seq.map (fun hp -> $"[{hp.SetName}: {hp.Admission}]") |> String.concat " "
            Ok $"membership probe certified pure/total by member-hoist's admission: `{kb.Name}`'s predicate probes {sets} through the probe(s) membership hoisting admitted for `{kb.Name}` -- a structure built before it, or, for a literal set of at most 16 keys, the as-written scan (G185) -- (proof input, the T1 planner's own record: {proofs}); every target and every other operand is a pure, total scalar of the cell (no panic site); no capture but the certified set(s); `totalScalar` itself is not widened"
    // The mask predicate's admission: `totalScalar` (B4's), or -- in the T4c
    // modes only (the default and `gather`), so `b4` stays exactly the
    // round-2 form and `unshared` the pre-T4c v2 -- the certificate above.
    let predicateAdmission (kb: IRBinding) : Result<string, string> =
        match kb.Value with
        | IRMask (_, pred) ->
            (match resolveCallableIn funcs pred with
             | Some pc ->
                (match pc.Params with
                 | [ p ] when pc.Captures.IsEmpty && totalScalar p pc.Body ->
                    Ok $"predicate of `{kb.Name}`: a pure, total scalar function of the cell (totalScalar)"
                 | [ p ] when sharedOn ->
                    (match probeCertificate kb pc p with
                     | Ok line -> Ok line
                     | Error why -> Error $"the mask predicate of `{kb.Name}` is not a pure, total scalar function of the cell (v1 judges literal arithmetic, comparisons and logic over the one parameter), and the membership probe is not certified: {why}")
                 | _ -> Error $"the mask predicate of `{kb.Name}` is not a pure, total scalar function of the cell (v1 judges literal arithmetic, comparisons and logic over the one parameter)")
             | None -> Error $"the mask predicate of `{kb.Name}` does not resolve to a callable")
        | _ -> Error "the compound's mask is not a let-bound `mask(x, pred)`"
    // ---- T4c: ONE selection shared by every compound over the same mask ----
    // `keep = mask(x, pred)` and its J companions `y_j = compound(c_j, keep)`
    // (each c_j over x's index space): ONE selection index -- counted in the
    // mask pass, its kept positions (when any companion demands them) filled
    // by the one joint pass -- owned by the plan and shared by every
    // companion's wrapper; the J gathers are ONE branchless pass at the
    // first companion (`b = keep[i]; y1[q] = c1[i]; ..; yJ[q] = cJ[i]; [pos[q]
    // = i;] q += b`), or, under `gather` with J <= 4 and every c_j bound
    // before the mask, T4a's mask pass. Stable, ascending, serial: the same
    // values, cardinality and positions each compound's own passes build.
    // Each companion's data store stays its own named array. Not a T4c case
    // (the round-2 / T4 plan decides): a single companion over x itself, the
    // `unshared` / `b4` mirrors, the gate off.
    let compoundsOfMask : Map<IRId, (IRId * int) list> =
        modul.CompoundInits
        |> Seq.choose (fun (KeyValue (cid, (_, m))) ->
            match m, indexOf cid with
            | IRVar (k, _), Some ci -> Some (k, (cid, ci))
            | _ -> None)
        |> Seq.groupBy fst
        |> Seq.map (fun (k, xs) -> (k, xs |> Seq.map snd |> Seq.sortBy snd |> List.ofSeq))
        |> Map.ofSeq
    let rank1Compound (b: IRBinding) =
        match stripUnits b.Type with
        | ArrayElem at ->
            (match at.IndexTypes with
             | [ ix ] -> ix.IxKind = IxKCompound && ix.Rank = 1
             | _ -> false)
        | _ -> false
    let plainAxisOf (t: IRType) : IRIndexType option =
        match stripUnits t with
        | ArrayElem a when not a.IsVirtual ->
            (match a.IndexTypes with
             | [ ix ] when ix.IxKind = IxKPlain && ix.Rank = 1 && ix.Symmetry = SymNone -> Some ix
             | _ -> None)
        | _ -> None
    let elemOf (t: IRType) = match stripUnits t with ArrayElem a -> a.ElemType | other -> other
    // G283: an extent expression whose value cannot change once evaluated --
    // literals, never-assigned bindings, extent reads and arithmetic over
    // them, no call.
    let rec stableExtentExpr (e: IRExpr) : bool =
        match e with
        | IRLit _ -> true
        | IRVar (id, _) -> not (assignedAnywhere id)
        | IRApp _ -> false
        // a run-time placeholder (`?`): two of them say nothing about each other
        | IRParam _ -> false
        | ExprShape (children, _) -> children |> List.forall stableExtentExpr
    // G283: the SHAPE ROOT of a stored rank-1 binding -- through POINTWISE maps
    // (`method_for(ks) <@> f |> compute`, a zip over operands of one root),
    // whose extents are their operands', down to the first binding that is
    // not one; every link never assigned. Two sources with one root have one
    // evaluated shape however their (run-time) extents are spelled.
    let rec shapeRoot (depth: int) (id: IRId) : IRId =
        let rec strip (e: IRExpr) = match e with IRCompute x -> strip x | x -> x
        match indexOf id with
        | Some i when depth < 32 && not (assignedAnywhere id) ->
            (match strip arr.[i].Value with
             | IRApplyCombinator info
                 when info.KernelInputRanks |> List.forall (fun r -> r = 0)
                      && info.KernelTDims.IsEmpty && not info.HasReynolds
                      && ((info.Arrays.Length = 1 && info.SharedIndexTypes.IsEmpty) || info.IsCoIteration) ->
                 let roots =
                     info.Arrays |> List.map (fun op ->
                         match op with
                         | IRVar (o, _) when not (assignedAnywhere o) -> Some (shapeRoot (depth + 1) o)
                         | _ -> None)
                 (match roots with
                  | Some r :: rest when rest |> List.forall (fun x -> x = Some r) -> r
                  | _ -> id)
             | _ -> id)
        | _ -> id
    // Per mask: None = not a T4c case; Some (Ok (selection, evidence)) or
    // Some (Error why) -- the reason every companion's record carries.
    let ctx : CfContext =
        { modul = modul; funcs = funcs; facts = facts; writes = writes; mode = mode; enabled = enabled; v2 = v2;
          sharedOn = sharedOn; gatherOn = gatherOn; streamedSrc = streamedSrc; decide = decide; arr = arr;
          indexOf = indexOf; assignedAnywhere = assignedAnywhere; payloadWrites = payloadWrites;
          payloadLifetime = payloadLifetime; readsMask = readsMask; transitiveRefs = transitiveRefs;
          squareMap = squareMap; builtinAdd = builtinAdd; foldSites = foldSites; positionsDemand = positionsDemand;
          elemBytes = elemBytes; staticExtentOf = staticExtentOf; fmtBytes = fmtBytes;
          predicateAdmission = predicateAdmission; rank1Compound = rank1Compound; plainAxisOf = plainAxisOf;
          elemOf = elemOf; stableExtentExpr = stableExtentExpr; shapeRoot = shapeRoot }
    let groupDecision = cfGroupDecision ctx
    let groupDecisions : Map<IRId, Result<SharedSelection * string list, string>> =
        compoundsOfMask |> Map.toSeq |> Seq.choose (fun (k, ms) -> groupDecision k ms |> Option.map (fun r -> (k, r))) |> Map.ofSeq
    // The shared plans by companion: every companion's plan carries the SAME
    // selection record and the SAME count / gather refs (the mask publishes
    // its count once; every companion reads it).
    let sharedPlans : Map<IRId, CompactionPlan * string list> =
        groupDecisions |> Map.toSeq |> Seq.collect (fun (_, r) ->
            match r with
            | Ok (s, evidence) ->
                let countCpp = ref None
                let countLlvm = ref None
                let gatherCpp = ref None
                let gatherLlvm = ref None
                s.Members |> List.map (fun (cid, cname, src) ->
                    (cid, ({ MaskId = s.MaskId; MaskName = s.MaskName; CompoundId = cid; CompoundName = cname
                             SourceId = src; PositionsNeeded = s.PositionsNeeded; GatherAtMask = s.GatherAtMask
                             StaticN = s.StaticN; SelCapCells = s.SelCapCells
                             CountCpp = countCpp; CountLlvm = countLlvm; GatherCpp = gatherCpp; GatherLlvm = gatherLlvm
                             Shared = Some s }, evidence))) |> Seq.ofList
            | Error _ -> Seq.empty) |> Map.ofSeq
    let groupDeclineOf (yId: IRId) : string option =
        match Map.tryFind yId modul.CompoundInits with
        | Some (_, IRVar (k, _)) ->
            (match Map.tryFind k groupDecisions with
             | Some (Error why) -> Some why
             | _ -> None)
        | _ -> None
    for KeyValue (yId, (denseExpr, maskExpr)) in modul.CompoundInits do
        cfPlanCompound ctx yId denseExpr maskExpr groupDeclineOf sharedPlans
