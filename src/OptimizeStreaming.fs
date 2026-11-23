// Streamed provider plans: the stream group fold, the stream mask replay, the keyed-cell projection, the halo chunk cache gates and the streamed provider sink (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeStreaming

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeHotRow
open Blade.OptimizeEpilogueLegs
open Blade.OptimizeMemberHoist
open Blade.OptimizeCompaction
open Blade.OptimizeCompactRows
open Blade.OptimizeGroupAgg
open Blade.OptimizeTranspose
open Blade.OptimizeInPlace

// ===========================================================================
// STREAM GROUP FOLD (BLADE_STREAM_GROUP_FOLD, rule `stream-group-fold v1`;
// round 4 wave 4, lane G item G4, gap G133 / ST07). `g = group_by(A, seg)`
// over a STREAMED rank-1 A under a structural grouping read every run into
// its row of ONE pool holding all N cells (the CSR), and the per-group fold
// `rs = reduce(g, k[, s])` (a row peel) then read the pool. Admitted, g is
// never stored: the peel streams each run into ONE window of the largest run
// (the bound: one huge group makes it that group -- the record names the
// length) and folds it with the peel's own emission, so every group's value
// is the stored peel's bit for bit (the same fold text over the same cells
// in the same order). The READ moves from g's site to rs's, so v1 admits rs
// only as the binding IMMEDIATELY after g (no statement between: nothing a
// moved read could reorder), the row kernel only as `lambda(row) ->
// reduce(row, k[, seed])` with k a resolved closed-total two-parameter
// callable and the seed a literal (the per-group fold cannot fail between two
// runs' reads -- an unseeded fold over an EMPTY run is the one failure, and a
// structural run is never empty: offsets strictly ascending), and g read
// NOWHERE else (a second reader, a capture, a row read, a return, another
// module, a provider write's source: the pool stays) and NOT PRINTED (a
// single-module program under a print selection that excludes g -- printing
// reads every cell). C++ lane (the LLVM lane refuses streamed programs; the
// program falls back to C++); the interpreter runs the as-written CSR.
// ===========================================================================

/// DEFAULT ON since round 4 wave 6: the allocation-ordering member of earlier
/// rounds is not a member under R0 (docs/formalism.md 2.4: BL8005 is an
/// environment outcome), and a fresh class-closure round under R0 found none
/// (G198). BLADE_STREAM_GROUP_FOLD=0|off keeps the CSR pool. Read per call.
let streamGroupFoldEnabled () = gateOn "BLADE_STREAM_GROUP_FOLD"

/// group binding id -> (its one peel's binding id, the largest run's length,
/// the plan evidence); the emitters' handshake: the group site sets
/// `Emitted` when it skipped the pool (the peel then streams).
module StreamGroupFoldTable =
    open System.Threading
    /// `Extra`: the LATER peels (D2 / G143 item 1), folded in the first
    /// peel's run loop; `ExtraDone`: their C++ names, published by the first
    /// peel's emission (each later peel then emits a comment only).
    type Plan = { GroupId: IRId; SourceId: IRId; PeelId: IRId; MaxRun: int64; Evidence: string list; Emitted: bool ref
                  Extra: (IRBinding * ApplyInfo) list; ExtraDone: Set<string> ref }
    let private cell = new AsyncLocal<Map<IRId, Plan>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: Plan) = cell.Value <- Map.add p.GroupId p (get ())
    let tryGroup (id: IRId) : Plan option = Map.tryFind id (get ())

let recordStreamGroupFold (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "stream-group-fold"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

let planStreamGroupFold (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    let streamed = streamedProviderIds modul
    if streamed.IsEmpty then () else
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve = resolveCallableIn funcs
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let deferred = programDeferredId modul allCallables Seq.empty
    let arr = List.toArray modul.Bindings
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let printSelection =
        Blade.PrintSelection.names ()
    let refsOf (x: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun m -> match m with IRVar (id, _) when id = x -> n <- n + 1 | _ -> ()) e
        n
    // the C++ EMISSION ORDER is IRId order through DerivedFuncOrigins
    // (CodeGen.emissionOrderedItems' orderKey, followed transitively)
    let orderKey (id: IRId) =
        let rec follow (cur: IRId) (fuel: int) =
            if fuel <= 0 then cur
            else
                match Map.tryFind cur modul.DerivedFuncOrigins with
                | Some originId when originId <> cur -> follow originId (fuel - 1)
                | _ -> cur
        follow id (Map.count modul.DerivedFuncOrigins + 1)
    // an eager row peel over g: its apply, when `b` is one
    let peelOf (gId: IRId) (b: IRBinding) : ApplyInfo option =
        match b.Value with
        | IRCompute (IRApplyCombinator info) when (match info.Arrays with [ IRVar (v, _) ] -> v = gId | _ -> false) -> Some info
        | _ -> None
    // one peel's shape: `lambda(row) -> reduce(row, k[, literal])`, k closed
    // (reads only its two parameters: nothing declared later, no module
    // value) and allocation-free (no string intermediate), total
    let peelShape (rb: IRBinding) (info: ApplyInfo) : Result<string, string> =
        match resolve info.Kernel with
        | Some rowK when rowK.Params.Length = 1 && rowK.Captures.IsEmpty ->
            let p = rowK.Params.Head
            (match rowK.Body with
             | IRReduce (rowRef, k, seed)
                 when (match rowRef with IRVar (v', _) -> v' = p.VarId | IRParam (_, i, _) -> i = p.Index | _ -> false) ->
                 (match resolve k with
                  | Some kc when kc.Params.Length = 2 && kc.Captures.IsEmpty && not kc.IsOmpParallel && kc.Parallelism.IsEmpty && not kc.IsCudaKernel && not kc.IsMpiParallel ->
                      let isP (x: IRExpr) =
                          kc.Params |> List.exists (fun q ->
                              match x with
                              | IRVar (v, _) -> v = q.VarId
                              | IRParam (_, i, _) -> i = q.Index
                              | _ -> false)
                      if not (totalScalarOver kc.Params kc.Body && allocFreeScalar isP kc.Body) then
                          Error $"the fold kernel of `{rb.Name}` is not a closed allocation-free total scalar of its two parameters (literal arithmetic, comparisons, logic, `if`; no call, no string, no module value)"
                      else
                      (match IRTotality.auditCallable resolve (fun _ -> None) deferred kc [], seed with
                       | Error why, _ -> Error $"the fold kernel of `{rb.Name}` is not proved total ({why}): a failing fold between two runs would overtake a later run's read failure"
                       | Ok (), None -> Ok "unseeded"
                       | Ok (), Some (IRLit (IRLitInt _ | IRLitFloat _ | IRLitFloat32 _ | IRLitBool _)) -> Ok "a literal seed"
                       | Ok (), Some _ -> Error $"the fold seed of `{rb.Name}` is not a non-string literal (v1)")
                  | _ -> Error $"the fold kernel of `{rb.Name}` is not a resolved, capture-free, serial two-parameter callable")
             | _ -> Error $"the row kernel of `{rb.Name}` is not `lambda(row) -> reduce(row, k[, seed])` (v1)")
        | _ -> Error $"the row kernel of `{rb.Name}` does not resolve to a capture-free one-parameter callable"
    // an INTERVENING statement (D2 item 2): allocation-free, closed-total
    // scalar over earlier, non-deferred, non-streamed real / Bool module values
    let statementWhy (gId: IRId) (b: IRBinding) : string option =
        let okVar (x: IRExpr) =
            match x with
            | IRVar (v, _) ->
                (match Map.tryFind v byId with
                 | Some vb -> orderKey vb.Id < orderKey b.Id && not (deferred v) && not (modul.ProviderReads.ContainsKey v) && v <> gId
                 | None -> false)
            | _ -> false
        let ps = bindingPayload modul b
        if modul.ProviderReads.ContainsKey b.Id then Some "is a provider read (a read binding can exit at its site)"
        elif modul.ProviderWrites.ContainsKey b.Id || intervalHasProviderEffect modul b.Id then Some "is a provider write or carries a provider effect"
        elif ps.Length <> 1 then Some "runs side-table operands at its site"
        elif not (List.isEmpty (embeddedExtentExprs b.Value)) && (embeddedExtentExprs b.Value |> List.exists (fun x -> match x with IRLit _ -> false | _ -> true)) then Some "carries a computed extent in a type"
        elif (facts b.Value).MayWrite || writes b.Value || lifetimeBarrier b.Value then Some "may write or holds a lifetime boundary"
        elif not (allocFreeScalar okVar b.Value) then Some "is not an allocation-free closed scalar (non-string literals, earlier real / Bool module values, arithmetic, comparisons, logic, `if`; no call)"
        else
        match IRTotality.walk resolve (fun _ -> None) deferred Map.empty Map.empty Set.empty Set.empty Set.empty b.Value with
        | Error why -> Some $"is not proved total ({why})"
        | Ok _ -> None
    for gi in 0 .. arr.Length - 1 do
        let gb = arr.[gi]
        match gb.Value with
        | IRGroupBy (IRVar (aId, _), IRVar (segId, _)) when streamed.Contains aId ->
            let spec = modul.ProviderReads.[aId]
            let segOffsets =
                modul.Bindings |> List.tryPick (fun b ->
                    if b.Id = segId then (match b.Value with IRSegments (offs, _) -> Some offs | _ -> None) else None)
            let decision : Result<(IRBinding * ApplyInfo) list * int64 * string list, string> =
                if spec.VarType.IndexTypes.Length <> 1 then Error "the streamed variable is not rank 1 (v1 streams a rank-1 run fold)"
                else
                match segOffsets with
                | None -> Error "the grouping is not a structural `segments(..)` binding with static run boundaries"
                | Some offs ->
                let runs = List.pairwise offs
                if runs |> List.exists (fun (a, b) -> b <= a) then Error "a run is empty or the boundaries are not strictly ascending"
                else
                let maxRun = runs |> List.map (fun (a, b) -> b - a) |> List.fold max 0L
                match printSelection with
                | None -> Error $"`{gb.Name}` is printed (no print selection: printing reads every cell of the grouped value)"
                | Some sel when Blade.PrintSelection.mayName sel gb.Name -> Error $"`{gb.Name}` is printed (the print selection names it)"
                | Some _ when not singleModule -> Error "a multi-module program (v1 plans a single module)"
                | Some _ ->
                if foreignRefs.Contains gb.Id then Error $"another module names `{gb.Name}`"
                else
                // the peels: every LATER binding (emission order) whose value
                // is an eager row peel over g
                let later = arr |> Array.filter (fun b -> orderKey b.Id > orderKey gb.Id) |> Array.sortBy (fun b -> orderKey b.Id)
                let peels = later |> Array.choose (fun b -> peelOf gb.Id b |> Option.map (fun info -> (b, info))) |> List.ofArray
                // every reference to g anywhere (a peel's loop object repeats its operand)
                let total =
                    (arr |> Array.sumBy (fun b -> bindingPayload modul b |> List.sumBy (refsOf gb.Id)))
                    + (modul.Functions |> List.sumBy (fun f -> refsOf gb.Id f.Body + (f.Captures |> List.filter (fun c -> c.Id = gb.Id) |> List.length)))
                    + (modul.ProviderWrites |> Map.toList |> List.filter (fun (_, w) -> w.SourceId = gb.Id) |> List.length)
                    - (peels |> List.sumBy (fun (_, info) -> refsOf gb.Id info.Loop))
                let typeRefs =
                    (arr |> Array.sumBy (fun b -> (refsOf gb.Id (IRSequence (irTypeExtentExprs b.Type))) + (bindingPayload modul b |> List.sumBy (fun p -> refsOf gb.Id (IRSequence (embeddedExtentExprs p))))))
                    + (modul.Functions |> List.sumBy (fun f ->
                        refsOf gb.Id (IRSequence (embeddedExtentExprs f.Body))
                        + refsOf gb.Id (IRSequence (irTypeExtentExprs f.RetType @ (f.Params |> List.collect (fun p -> irTypeExtentExprs p.Type))))))
                if typeRefs > 0 then Error $"a type carries a reference to `{gb.Name}` (a range bound or an extent that renders it by name)"
                elif peels.IsEmpty then Error $"what reads `{gb.Name}` is not an eager row peel `reduce({gb.Name}, k[, seed])` (a deferred map keeps the pool)"
                elif total <> peels.Length then
                    Error $"`{gb.Name}` is read {total} time(s), not once by each of its {peels.Length} eager row peel(s) (a second reader, a capture, a row read or an escape keeps the pool)"
                else
                match peels |> List.tryPick (fun (rb, info) -> match peelShape rb info with Error why -> Some why | Ok _ -> None) with
                | Some why -> Error why
                | None ->
                let lastKey = peels |> List.map (fun (b, _) -> orderKey b.Id) |> List.max
                let peelIds = peels |> List.map (fun (b, _) -> b.Id) |> Set.ofList
                let between = later |> Array.filter (fun b -> orderKey b.Id < lastKey && not (peelIds.Contains b.Id)) |> List.ofArray
                // an emitted FUNCTION in the interval (a main-local lambda's
                // construction may allocate): fail closed on the emission order
                let funcBetween =
                    modul.Functions |> List.tryFind (fun f -> let k = orderKey f.Id in k > orderKey gb.Id && k < lastKey)
                match funcBetween with
                | Some f -> Error $"a function (`{f.SourceName}`) is emitted between `{gb.Name}` and its last peel (its construction is not certified allocation-free)"
                | None ->
                match between |> List.tryPick (fun b -> statementWhy gb.Id b |> Option.map (fun why -> (b, why))) with
                | Some (b, why) -> Error $"`{b.Name}` (between `{gb.Name}` and its last peel) {why}"
                | None ->
                let names = peels |> List.map (fun (b, _) -> $"`{b.Name}`") |> String.concat ", "
                Ok (peels, maxRun,
                    [ $"{names} fold each run of `{spec.VarName}` straight from ONE window of {maxRun} cell(s) (the largest run; {runs.Length} runs), ONE run loop at `{(fst peels.Head).Name}`: `{gb.Name}`'s pool of {(List.last offs)} cells is never built"
                      "each peel's fold text, its cells and their order are the stored peel's (bitwise the CSR form); the run reads move from the group's site to the first peel's, and the later peels' folds and output allocations to the first peel's site"
                      (if between.IsEmpty then "interval: no statement between the group and its last peel"
                       else "interval: " + (between |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", ") + ": allocation-free closed-total scalars over earlier values (no call, no string, no provider read or write, no write), so no failure or allocation of theirs is reordered against a moved read")
                      $"every fold kernel is a closed allocation-free total scalar of its two parameters (no module value: nothing it names is declared later); every run is non-empty (strictly ascending boundaries); no function is emitted between `{gb.Name}` and its last peel; `{gb.Name}` read only by its peels, not printed, no other module, no provider write" ])
            match decision with
            | Error why -> recordStreamGroupFold gb.Name (Blade.Effects.Declined why) []
            | Ok (peels, maxRun, ev) ->
                if not (streamGroupFoldEnabled ()) then
                    recordStreamGroupFold gb.Name (Blade.Effects.Declined "disabled by BLADE_STREAM_GROUP_FOLD") ev
                else
                    let (first, _) = peels.Head
                    StreamGroupFoldTable.record
                        { GroupId = gb.Id; SourceId = aId; PeelId = first.Id; MaxRun = maxRun; Evidence = ev; Emitted = ref false
                          Extra = peels.Tail; ExtraDone = ref Set.empty }
        | _ -> ()

// ===========================================================================
// STREAM MASK REPLAY (BLADE_STREAM_MASK_REPLAY, rule `stream-mask-replay v1`;
// round 4 wave 5, lane D item D1, gap G142 item 1). `keep = mask(A, p)` over
// a STREAMED rank-1 A whose ONLY reader is `y = compound(A, keep)` (the same
// source) stored an N-byte Bool mask that the compound's gather sweep read
// once. Admitted, keep is never stored: the keep site sweeps the store
// COUNTING only (`count += p(v)`), and y's gather sweep evaluates p again on
// the cell it reads. Both sweeps stay where they were (same reads, same order,
// same sites); only the N-byte allocation disappears and p runs once more per
// cell -- p is compaction fusion's `totalScalar` predicate (capture-free, a
// pure total scalar of its one parameter), emitted ONCE as a BLADE_ONE_BODY
// function both sweeps call. The gather re-checks the count it reaches
// against the first sweep's (BL9001: the read-session contract -- a store
// another process mutates during the run is outside the contract -- and a
// clamped cursor keeps a mismatch memory-safe). A removed allocation may let
// a program that died BL8005 at keep succeed or reach a later failure (the
// BLADE_SINK_STREAM precedent). Priced by CostModel (the recompute v2 rule).
// C++ lane; the LLVM lane refuses streamed programs (the program falls back
// to C++); the interpreter runs the stored mask (the oracle).
// ===========================================================================

/// BLADE_STREAM_FOLD_FUSE (D4 / G145 item 2): DEFAULT ON since round 4 wave 6
/// (a fresh class-closure round under R0 found no member, G195); `0` / `off`
/// stages the map through its segment route. Read per call.
let streamFoldFuseEnabled () = gateOn "BLADE_STREAM_FOLD_FUSE"

/// BLADE_STREAM_MASK_REPLAY=0|off keeps the stored mask; unset = on.
let streamMaskReplayEnabled () = gateOn "BLADE_STREAM_MASK_REPLAY"

/// The replay plan (keyed by the mask AND the compound) and the emitters'
/// handshake: the mask site publishes `Replayed = Some (count, predicate)`
/// ONLY when it actually skipped the storage; the compound site then MUST
/// replay (a stale fallback to the stored mask would read an undeclared
/// array: it is a codegen error instead).
module MaskReplayTable =
    open System.Threading
    type Plan = { MaskId: IRId; CompoundId: IRId; SourceId: IRId; Predicate: IRExpr; Evidence: string list; Replayed: (string * string) option ref }
    type private State = { ByMask: Map<IRId, Plan>; ByCompound: Map<IRId, Plan> }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> { ByMask = Map.empty; ByCompound = Map.empty } | _ -> cell.Value
    let reset () = cell.Value <- { ByMask = Map.empty; ByCompound = Map.empty }
    let record (p: Plan) =
        let s = get ()
        cell.Value <- { ByMask = Map.add p.MaskId p s.ByMask; ByCompound = Map.add p.CompoundId p s.ByCompound }
    let tryMask (id: IRId) : Plan option = Map.tryFind id (get ()).ByMask
    let tryCompound (id: IRId) : Plan option = Map.tryFind id (get ()).ByCompound

let recordStreamMaskReplay (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "stream-mask-replay"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

let planStreamMaskReplay (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    let streamed = streamedProviderIds modul
    if streamed.IsEmpty then () else
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve = resolveCallableIn funcs
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let arr = List.toArray modul.Bindings
    let indexOf (id: IRId) = arr |> Array.tryFindIndex (fun b -> b.Id = id)
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let printSelection =
        Blade.PrintSelection.names ()
    let payloadWrites (b: IRBinding) =
        let ps = bindingPayload modul b
        let exprs = ps @ (ps |> List.collect embeddedExtentExprs) @ irTypeExtentExprs b.Type
        exprs |> List.exists (fun e -> (facts e).MayWrite || writes e || lifetimeBarrier e)
    for kb in arr do
        match kb.Value with
        | IRMask (IRVar (aId, _), pred) when streamed.Contains aId ->
            let decision : Result<IRId * string list, string> =
                match CompactionTable.tryMask kb.Id with
                | None -> Error $"compaction fusion did not plan `{kb.Name}` (its count must be taken in its own sweep for the compound to size its buffer)"
                | Some plan when plan.Shared.IsSome -> Error "a shared selection (never planned over a stream)"
                | Some plan ->
                let yId = plan.CompoundId
                match indexOf kb.Id, indexOf yId, Map.tryFind yId modul.CompoundInits with
                | Some ki, Some yi, Some (IRVar (dId, _), IRVar (mId, _)) when ki < yi && mId = kb.Id ->
                    let yb = arr.[yi]
                    if dId <> aId then Error $"`{yb.Name}` compounds another array than the mask source (the replay evaluates the predicate on the gathered cell)"
                    else
                    match stripUnits yb.Type with
                    | ArrayElem at when at.IndexTypes.Length <> 1 -> Error $"`{yb.Name}` has trailing dims (v1 replays a rank-1 compaction)"
                    | _ ->
                    match printSelection with
                    | None -> Error $"`{kb.Name}` is printed (no print selection: printing reads every cell of the mask)"
                    | Some sel when Blade.PrintSelection.mayName sel kb.Name -> Error $"`{kb.Name}` is printed (the print selection names it)"
                    | Some _ when not singleModule -> Error "a multi-module program (v1 plans a single module)"
                    | Some _ ->
                    if foreignRefs.Contains kb.Id then Error $"another module names `{kb.Name}`"
                    else
                    // exactly ONE reference: the mask operand of y (y's own
                    // TYPE may name the mask it is compounded under; the
                    // replay form renders y's grid from the literal extent)
                    let found = referenceCensus allCallables modul kb.Id kb.Name
                    let others = found |> List.filter (fun (who, _) -> who <> $"`{yb.Name}` (a compound() operand)" && who <> $"the type of `{yb.Name}`")
                    let maskOperand = found |> List.filter (fun (who, _) -> who = $"`{yb.Name}` (a compound() operand)") |> List.sumBy snd
                    if not others.IsEmpty then
                        let (who, n) = others.Head
                        Error $"`{kb.Name}` has another reader than `{yb.Name}`: {who} names it {n} time(s) (the stored mask stays)"
                    elif maskOperand <> 1 then Error $"`{kb.Name}` is named {maskOperand} time(s) by `{yb.Name}`'s operands, not once as its mask"
                    else
                    match resolve pred with
                    | None -> Error "the predicate does not resolve to a callable"
                    | Some pc ->
                    match pc.Params with
                    | [ p ] when pc.Captures.IsEmpty && not pc.IsOmpParallel && pc.Parallelism.IsEmpty && not pc.IsCudaKernel && not pc.IsMpiParallel && totalScalarOver [ p ] pc.Body ->
                        let deferred = programDeferredId modul allCallables Seq.empty
                        match IRTotality.auditCallable resolve (fun _ -> None) deferred pc [] with
                        | Error why -> Error $"the predicate is not proved total ({why})"
                        | Ok () ->
                        if (facts pc.Body).MayWrite || writes pc.Body then Error "the predicate may write"
                        else
                        let between = [ ki + 1 .. yi - 1 ] |> List.map (fun i -> arr.[i])
                        match between |> List.tryFind (fun b -> payloadWrites b || modul.ProviderWrites.ContainsKey b.Id) with
                        | Some b -> Error $"`{b.Name}` (between `{kb.Name}` and `{yb.Name}`) may write, is a provider write or holds a lifetime boundary"
                        | None ->
                        // THE PRICE: materialize = ops x n + mask write n + read n
                        // (peak n + y); replay = 2 x ops x n (peak y); the two
                        // store sweeps are the same in both
                        match Blade.CostModel.kernelOps resolve pc with
                        | Error why -> Error $"materialized -- the predicate is not priced ({why})"
                        | Ok ops ->
                        try
                            let nSym =
                                match stripUnits kb.Type with
                                | ArrayElem a ->
                                    (match a.IndexTypes with
                                     | [ ix ] -> (match ix.Extent with IRLit (IRLitInt n) when n >= 0L -> Blade.CostModel.Poly.konst n | _ -> Blade.CostModel.Poly.sym "n")
                                     | _ -> Blade.CostModel.Poly.sym "n")
                                | _ -> Blade.CostModel.Poly.sym "n"
                            let src = (match indexOf aId with Some ai -> arr.[ai].Name | None -> "A")
                            let sweep = { Blade.CostModel.emptyStep with STraffic = [ (src, Blade.CostModel.Poly.scale 2L nSym) ] }
                            let m =
                                Blade.CostModel.seqSteps
                                    [ sweep
                                      { Blade.CostModel.SWork = Blade.CostModel.Poly.scale ops.Count nSym
                                        Blade.CostModel.STraffic = [ (kb.Name, Blade.CostModel.Poly.scale 2L nSym) ]
                                        Blade.CostModel.SPeak = Blade.CostModel.Peak.ofPoly nSym } ]
                            let r =
                                Blade.CostModel.seqSteps
                                    [ sweep
                                      { Blade.CostModel.emptyStep with SWork = Blade.CostModel.Poly.scale (2L * ops.Count) nSym } ]
                            let cm, cr = Blade.CostModel.costOf m, Blade.CostModel.costOf r
                            let d = Blade.CostModel.Poly.sub cr.Work cm.Work
                            let t = Blade.CostModel.Poly.sub cm.Traffic cr.Traffic
                            let basis = $"{Blade.CostModel.cellsText nSym}; predicate {Blade.CostModel.opsText ops}; baseline: `{kb.Name}` (1 byte per cell) written in the count sweep and read in the gather sweep; bandwidth weight 1 (a Bool pool); the two sweeps of `{src}` are unchanged"
                            if not (Blade.CostModel.Poly.leq d t) then
                                Error $"materialized -- the predicate replay is not dominant: work +{Blade.CostModel.Poly.render d} > traffic -{Blade.CostModel.Poly.render t} ({basis})"
                            else
                            Ok (yId,
                                [ $"`{kb.Name}` (mask over the STREAMED `{src}`) is never stored: its sweep only COUNTS (`count += p(v)`), and `{yb.Name}`'s gather sweep evaluates the predicate again on the cell it reads (`q += p(v)`) -- the same two sweeps of the store, at the same sites, in the same order; the N-byte Bool mask is gone"
                                  $"the predicate is compaction fusion's `totalScalar` (capture-free, a pure total scalar of its one parameter; IRTotality with the program's deferred-value predicate: ok; no write), emitted ONCE as a BLADE_ONE_BODY function both sweeps call"
                                  $"census: `{kb.Name}` is named once, as `{yb.Name}`'s mask (every module root: values, side operands, function bodies and captures, declared and carried types, provider writes and masked reads); not printed, single module, no other module"
                                  (if between.IsEmpty then $"interval: no binding between `{kb.Name}` and `{yb.Name}`"
                                   else "interval: " + (between |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", ") + ": no write (payload and carried extents), no provider write, no lifetime boundary")
                                  $"the gather re-checks the count it reaches against the count sweep's (BL9001; a clamped cursor keeps a mismatch memory-safe): the read-session contract -- a store another process mutates during the run is outside it"
                                  "a removed allocation may let a program that died BL8005 at the mask succeed or reach a later failure (the BLADE_SINK_STREAM precedent)"
                                  Blade.CostModel.legalityLine "no read moves and no allocation moves earlier; the replayed predicate is total and pure"
                                  Blade.CostModel.renderTransition m r basis
                                  "llvm lane: the program falls back to C++ (the LLVM lane refuses streamed provider reads)" ])
                        with :? System.OverflowException -> Error "materialized -- the replay is not priced (a count overflows a 64-bit integer)"
                    | [ _ ] when not pc.Captures.IsEmpty -> Error "the predicate captures a value (v1 replays a capture-free predicate)"
                    | _ -> Error "the predicate is not compaction fusion's `totalScalar` of its one parameter (a membership probe's hoisted structure lives only at the mask site)"
                | _ -> Error "the compound is not a later top-level `compound(A, keep)` over the same let-bound mask"
            match decision with
            | Error why -> recordStreamMaskReplay kb.Name (Blade.Effects.Declined why) []
            | Ok (yId, ev) ->
                if not (streamMaskReplayEnabled ()) then
                    recordStreamMaskReplay kb.Name (Blade.Effects.Declined "disabled by BLADE_STREAM_MASK_REPLAY") ev
                else
                    MaskReplayTable.record { MaskId = kb.Id; CompoundId = yId; SourceId = aId; Predicate = pred; Evidence = ev; Replayed = ref None }
                    recordStreamMaskReplay kb.Name Blade.Effects.Applied ev
        | _ -> ()

// ===========================================================================
// KEYED-CELL PROJECTION (BLADE_KEYED_PROJECTION, rule `keyed-cell-projection
// v1`; round 4 wave 4, lane G item G6, PM44 / gap G135 item 3). A module-level
// `let A = s.vars.A |> z.read` (an ordinary dense rank-1 Zarr read) whose ONLY
// uses are constant keyed reads `A(5)`, `A(999000)` materialized every cell
// (8 MB at 1,000,001 Float64 cells) to serve two. Admitted, the read keeps
// its whole-variable sweep -- every chunk fetched in the same order through
// one chunk buffer, the short / missing-without-fill checks where they fire
// today, every in-bounds cell converted and tallied, BL8015 reported after
// the sweep (Codex design review, PM44: BUILD A; skipping the validation of
// unread chunks would turn a failing program into a succeeding one, so it is
// not built) -- and keeps only the demanded cells, one scalar each; each
// `A(k)` renders as its scalar. Fail closed (first failed condition
// recorded): A not printed (a single-module program under a print selection
// excluding A), no other module, never assigned, every reference to A is
// `A(<integer literal>)` in a MODULE binding's payload (a function body, a
// capture, an alias, `extents(A)`, a call argument, a provider write's source,
// a non-literal subscript all keep the resident read), each literal in
// [0, N). The C++ emitter re-checks the read's shape (ordinary dense rank-1
// identity-scalar Zarr) and records the outcome; the LLVM lane refuses
// provider reads (the program falls back to C++); the interpreter reads A
// whole (the oracle).
// ===========================================================================

/// BLADE_KEYED_PROJECTION=0|off keeps the resident read; unset = on.
let keyedProjectionEnabled () = gateOn "BLADE_KEYED_PROJECTION"

/// read binding id -> the demanded flat cells and the plan evidence; the
/// emitter publishes the cell names (`Cells`) when it took the sweep, which
/// the expression renderer reads.
module KeyedProjectionTable =
    open System.Threading
    type Plan = { ReadId: IRId; Cells: int64 list; Evidence: string list; Emitted: Map<int64, string> ref }
    let private cell = new AsyncLocal<Map<IRId, Plan>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: Plan) = cell.Value <- Map.add p.ReadId p (get ())
    let tryRead (id: IRId) : Plan option = Map.tryFind id (get ())
    /// The scalar a keyed read `A(k)` renders as, once the read emitted its sweep.
    let tryCell (id: IRId) (k: int64) : string option =
        match Map.tryFind id (get ()) with
        | Some p -> Map.tryFind k p.Emitted.Value
        | None -> None

let recordKeyedProjection (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "keyed-cell-projection"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

let planKeyedCellProjection (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    let printSelection =
        Blade.PrintSelection.names ()
    for b in modul.Bindings do
        match Map.tryFind b.Id modul.ProviderReads with
        | Some spec when not spec.Streamed && spec.Provider = "zarr" && spec.MaskName.IsNone && spec.Window.IsNone
                         && spec.VarType.IndexTypes.Length = 1 && spec.VarType.IndexTypes.Head.Rank = 1 ->
            let n = match spec.VarType.IndexTypes.Head.Extent with IRLit (IRLitInt n) -> Some n | _ -> None
            // every reference: (keyed literal cells, other references)
            let keyed = System.Collections.Generic.List<int64>()
            let mutable others = 0
            let mutable assigned = false
            let scan (e: IRExpr) =
                iterIRExpr (fun m ->
                    match m with
                    | IndexOfNamed (id, _, [ IRLit (IRLitInt k) ], _) when id = b.Id -> keyed.Add k
                    | IRVar (id, _) when id = b.Id -> others <- others + 1
                    | IRAssign (t, _) ->
                        let rec root (x: IRExpr) =
                            match x with
                            | IRIndex (r, _, _) | IRFieldAccess (r, _) | IRTupleProj (r, _, _) -> root r
                            | IRVar (id, _) -> id = b.Id
                            | _ -> false
                        if root t then assigned <- true
                    | _ -> ()) e
            for o in modul.Bindings do
                for p in bindingPayload modul o do scan p
            let inFunctions =
                modul.Functions |> List.exists (fun f ->
                    f.Captures |> List.exists (fun c -> c.Id = b.Id)
                    || (let mutable hit = false
                        iterIRExpr (fun m -> match m with IRVar (id, _) when id = b.Id -> hit <- true | _ -> ()) f.Body
                        hit))
            let written = modul.ProviderWrites |> Map.exists (fun _ w -> w.SourceId = b.Id)
            // a reference carried by a TYPE (`0..Int64(floor(A(0)))`) renders by
            // name, outside the keyed-read renderer (Codex review lane G round 1)
            let refsIn (e: IRExpr) =
                let mutable n = 0
                iterIRExpr (fun m -> match m with IRVar (id, _) when id = b.Id -> n <- n + 1 | _ -> ()) e
                n
            let typeRefs =
                (modul.Bindings |> List.sumBy (fun o ->
                    refsIn (IRSequence (irTypeExtentExprs o.Type))
                    + (bindingPayload modul o |> List.sumBy (fun p -> refsIn (IRSequence (embeddedExtentExprs p))))))
                + (modul.Functions |> List.sumBy (fun f ->
                    refsIn (IRSequence (irTypeExtentExprs f.RetType @ (f.Params |> List.collect (fun p -> irTypeExtentExprs p.Type))))))
            // each keyed read holds the var once: the IRVar visit counts it too
            let nonKeyed = others - keyed.Count
            let cells = keyed |> Seq.distinct |> Seq.sort |> List.ofSeq
            let decision : Result<string list, string> =
                match printSelection with
                | None -> Error $"`{b.Name}` is printed (no print selection: printing reads every cell)"
                | Some sel when Blade.PrintSelection.mayName sel b.Name -> Error $"`{b.Name}` is printed (the print selection names it)"
                | Some _ when not singleModule -> Error "a multi-module program (v1 plans a single module)"
                | Some _ ->
                if foreignRefs.Contains b.Id then Error $"another module names `{b.Name}`"
                elif assigned then Error $"`{b.Name}` is assigned"
                elif inFunctions then Error $"a function body or capture reads `{b.Name}` (v1 projects module-level keyed reads only)"
                elif written then Error $"`{b.Name}` is a provider write's source (the writer reads every cell)"
                elif typeRefs > 0 then Error $"a type carries a reference to `{b.Name}` (a range bound or an extent renders it by name)"
                elif nonKeyed > 0 then Error $"`{b.Name}` has {nonKeyed} use(s) that are not a keyed read at an integer literal (an alias, a call argument, `extents`, a fold, a non-literal subscript keep the resident read)"
                elif cells.IsEmpty then Error $"nothing reads `{b.Name}` by a keyed literal"
                else
                match n with
                | None -> Error "the extent is not static"
                | Some n when cells |> List.exists (fun k -> k < 0L || k >= n) -> Error "a literal subscript is outside [0, N) (the read keeps its bounds check)"
                | Some n ->
                    let cellsText = cells |> List.map string |> String.concat ", "
                    Ok [ $"`{b.Name}` keeps its whole-variable sweep -- every chunk fetched in order through one chunk buffer, the short / missing-without-fill checks where they fire today, every in-bounds cell converted and tallied, BL8015 after the sweep -- but retains only {cells.Length} of its {n} cells ({cellsText}): no destination array"
                         $"every use of `{b.Name}` is a module-level keyed read at a literal in [0, {n}); not printed, not assigned, no other module, no function, no provider write"
                         "the validation of unread chunks is KEPT (Codex design review PM44: skipping it would turn a failing program into a succeeding one); the LLVM lane refuses provider reads (C++ fallback); the interpreter reads the variable whole" ]
            match decision with
            | Error why -> recordKeyedProjection b.Name (Blade.Effects.Declined why) []
            | Ok ev ->
                if not (keyedProjectionEnabled ()) then recordKeyedProjection b.Name (Blade.Effects.Declined "disabled by BLADE_KEYED_PROJECTION") ev
                else KeyedProjectionTable.record { ReadId = b.Id; Cells = cells; Evidence = ev; Emitted = ref Map.empty }
        | _ -> ()

// ===========================================================================
// PROVIDER SINK STREAM (BLADE_SINK_STREAM, rule `provider-sink-stream v1`;
// round 4 wave 3, lane D item D1, gap G91). `let y = method_for(x) <@> f
// |> compute` (or the deferred spelling) whose ONLY consumer is one Zarr
// write `z.write(.., y)` held all N cells of y so that the writer could
// validate the whole buffer (BL8015, before any file effect) and then write
// its one chunk. Admitted, y is never stored: the write site runs a
// VALIDATION pass that recomputes the cells block by block (Float elements
// only: the BL8015 count and first flat cell, before any file effect), then
// the writer's metadata, then the chunk produced and written block by block
// through one bounded buffer. Both passes fill the SAME buffer with the SAME
// loop (textual identity, not a proof: the write pass re-checks finiteness,
// BL9001); the bytes are y's, bitwise under BLADE_FP_CONTRACT=off. The producer
// moves from y's site to the write's site, so the interval between them must
// write nothing (the snapshot) and the producer must be total and write-free
// (no failure or effect moves). Whether the recompute pays is priced by the
// static cost model (`src/CostModel.fs`, model v1): stream iff the extra
// work per cell <= the traffic it saves per cell (materialized: read the k
// operands, write y, read y twice = k + 3; streamed: read the operands twice
// = 2k, or k without a validation pass); `always` streams regardless (the
// peak-memory arm, an optimizerGateMirrors entry). The C++ emitter records
// the applied decision; the LLVM lane refuses provider writes (the program
// falls back to C++); the interpreter skips any program with a write.
// ===========================================================================

/// BLADE_HALO_CACHE (rule `halo-chunk-cache v1`; round 4 wave 3, D2 / PM42):
/// the certified rank-1 halo stencil over a chunked streamed Zarr axis keeps
/// its chunks in a direct-mapped ring of S slots for the run loop, so every
/// chunk is fetched once per evaluation instead of once per window it
/// touches. `0`/`off` = a fetch per window per chunk. Read per call by the
/// C++ emitter (CodeGenCuda's segment run loop), which records the decision.
let haloCacheEnabled () = gateOn "BLADE_HALO_CACHE"
/// The ELEMENTWISE segment route of the halo chunk cache (round 4 wave 4, E6 /
/// ST03; DEFAULT ON since round 4 wave 6, a fresh class-closure round under R0
/// found no member): the cache also takes the elementwise segment route (a zip
/// of differently chunked streams), per source. `BLADE_HALO_CACHE=stencil` =
/// the old default (the certified stencil route only; a knob mirror), `0` /
/// `off` = no cache at all.
let haloCacheElementwiseEnabled () =
    haloCacheEnabled ()
    && (match System.Environment.GetEnvironmentVariable "BLADE_HALO_CACHE" with
        | null -> true
        | v -> v.Trim().ToLowerInvariant() <> "stencil")

type SinkStreamMode =
    | SinkStreamOff
    | SinkStreamCost
    | SinkStreamAlways

/// BLADE_SINK_STREAM: unset/`1`/`on` = the cost-gated stream, `always` =
/// stream every admitted shape, `0`/`off` = the whole-buffer write. Per call.
let sinkStreamMode () : SinkStreamMode =
    match Blade.Toolchain.envSwitch "BLADE_SINK_STREAM" with
    | Blade.Toolchain.SwitchOff -> SinkStreamOff
    | Blade.Toolchain.SwitchOther v when v.Trim().ToLowerInvariant() = "always" -> SinkStreamAlways
    | _ -> SinkStreamCost

type SinkStreamPlan =
    { ProducerId: IRId
      ProducerName: string
      WriteId: IRId
      Apply: ApplyInfo
      Kernel: IRCallable
      Extent: int64
      /// Cells per block of the bounded buffer.
      Block: int64
      /// A Float element: the BL8015 validation pass runs first.
      Validate: bool
      Evidence: string list
      /// Set by the C++ emitter at the producer's site when it omitted y
      /// (it re-checks renderability there); the write site streams only then.
      Taken: bool ref }

module SinkStreamTable =
    open System.Threading
    type State = { Producers: Map<IRId, SinkStreamPlan>; Writes: Map<IRId, IRId>; Inputs: Set<IRId> }
    let private empty = { Producers = Map.empty; Writes = Map.empty; Inputs = Set.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    /// Records a plan; `inputs` are the operands and captures the moved
    /// producer reads at the write's site (they must stay live until then).
    let record (p: SinkStreamPlan) (inputs: Set<IRId>) =
        let s = get ()
        cell.Value <- { Producers = Map.add p.ProducerId p s.Producers; Writes = Map.add p.WriteId p.ProducerId s.Writes
                        Inputs = Set.union inputs s.Inputs }
    let tryProducer (id: IRId) = Map.tryFind id (get ()).Producers
    let tryWrite (id: IRId) = Map.tryFind id (get ()).Writes |> Option.bind (fun p -> Map.tryFind p (get ()).Producers)
    /// The C++ emitter's decline at the producer's site: both bindings then
    /// take their ordinary emission (the write finds no plan).
    let remove (producerId: IRId) =
        let s = get ()
        cell.Value <- { s with Producers = Map.remove producerId s.Producers
                               Writes = s.Writes |> Map.filter (fun _ pid -> pid <> producerId) }
    /// A producer, write or moved-producer input this plan owns (other
    /// plans' claim registries; the early pool-release schedule).
    let contains (id: IRId) =
        let s = get ()
        s.Producers.ContainsKey id || s.Writes.ContainsKey id || s.Inputs.Contains id

let recordSinkStream (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "provider-sink-stream"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// Plan the streamed sinks. Every module binding that is the source of a
/// provider write gets a verdict (declines recorded here; an admitted plan is
/// recorded by the C++ emitter, which knows whether it streamed).
let planProviderSinkStream (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    if modul.ProviderWrites.IsEmpty then () else
    let arr = List.toArray modul.Bindings
    let byId = arr |> Array.map (fun b -> (b.Id, b)) |> Map.ofArray
    let indexOf = arr |> Array.mapi (fun i b -> (b.Id, i)) |> Map.ofArray
    let callables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let funcs = callables |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve = resolveCallableIn funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let facts = cseFactsOf funcs
    let payloadOf = intervalPayloadOf modul
    let stored = storedArrayProof modul
    let roots = lazy (compactionRoots callables modul)
    let deferredIds = arr |> Array.filter (fun b -> isDeferredForm b.Value) |> Array.map (fun b -> b.Id) |> Set.ofArray
    // the census widened by the program's deferred-value classifier (gap G98
    // class audit) for the totality audits below
    let deferredW = widenDeferred modul callables deferredIds.Contains
    let hasCuda = callables |> List.exists (fun f -> f.IsCudaKernel)
    let hasMpi = callables |> List.exists (fun f -> f.IsMpiParallel)
    let printSelection =
        Blade.PrintSelection.names ()
    let tileCacheOn =
        match Blade.Toolchain.envSwitch "BLADE_TILE_CACHE" with
        | Blade.Toolchain.SwitchUnset | Blade.Toolchain.SwitchOff -> false
        | _ -> true
    let rank1Lit (t: IRType) : (ElemType * int64) option =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            match at.IndexTypes with
            | [ ix ] when ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty ->
                (match ix.Extent, stripUnits at.ElemType with
                 | IRLit (IRLitInt n), IRTScalar et -> Some (et, n)
                 | _ -> None)
            | _ -> None
        | _ -> None
    let writable (et: ElemType) = et = ETFloat64 || et = ETFloat32 || et = ETInt64 || et = ETInt32
    // THE OWNER REGISTRY: a binding another plan schedules is not this one's.
    let ownedBy (id: IRId) : string option =
        if (HotRowTable.tryProducer id).IsSome || (HotRowTable.tryConsumer id).IsSome then Some "the hot-row / cell-epilogue plan"
        elif (CellFoldTable.tryLoop id).IsSome || (CellFoldTable.tryFoldLet id).IsSome then Some "the cell-fold plan"
        elif (RecStepTable.tryLoop id).IsSome || (RecStepTable.tryStep id).IsSome then Some "the threaded recursion step"
        elif (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome then Some "the epilogue-legs plan"
        elif (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome
             || (CompactionTable.tryFoldMap id).IsSome || not (CompactionTable.foldsOf id).IsEmpty then Some "the compaction-fusion plan"
        elif (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome then Some "the compact-row-classes plan"
        elif (GroupAggTable.tryPeel id).IsSome || (GroupAggTable.tryGroupBy id).IsSome then Some "the group-aggregate plan"
        elif not (MemberHoistTable.plansOf id).IsEmpty
             || (MemberHoistTable.all () |> List.exists (fun p -> p.HostId = id || p.SetId = id)) then Some "the membership-hoist plan"
        elif AliasElideTable.contains id then Some "the read-only alias plan"
        elif (GroupViewTable.tryGroup id).IsSome || (GroupViewTable.tryUngroup id).IsSome then Some "the structural group view"
        elif GroupRowMapTable.contains id then Some "the group-rowmap plan"
        elif (Blade.Types.PoolReuseTable.tryDonor id).IsSome
             || (Blade.Types.PoolReuseTable.donors () |> Map.exists (fun k v -> k = id || v = id))
             || (Blade.Types.PoolReuseTable.returnDonors () |> Map.exists (fun k v -> k = id || v = id)) then Some "scratch pool reuse"
        else None
    for KeyValue (wid, w) in modul.ProviderWrites do
        match Map.tryFind w.SourceId byId, Map.tryFind w.SourceId indexOf, Map.tryFind wid indexOf with
        | Some y, Some yi, Some wi ->
            let yid = y.Id
            let verdict : Result<SinkStreamPlan, string> =
                let fail (s: string) : Result<SinkStreamPlan, string> = Error s
                let h =
                    match y.Value with
                    | IRCompute (IRApplyCombinator h) | IRApplyCombinator h -> Some h
                    | _ -> None
                if w.Provider <> "zarr" then fail $"provider '{w.Provider}' has no streamed writer (v1: Zarr only; NetCDF / CSV keep the whole-buffer write)"
                elif not singleModule then fail "a multi-module program (the print selection and the census are single-module)"
                elif hasMpi then fail "an MPI build (the write is rank 0's, the producer every rank's)"
                elif hasCuda then fail "the module holds a CUDA kernel"
                elif tileCacheOn then fail "revision reuse (BLADE_TILE_CACHE) plans tiled bindings at emission"
                elif wi <= yi then fail "the write is not after its source"
                else
                match h with
                | None -> fail $"`{y.Name}` is not a map or co-iteration (`method_for(..) <@> f`, computed or deferred)"
                | Some h ->
                match rank1Lit y.Type with
                | None -> fail $"`{y.Name}` is not a plain dense rank-1 array with a literal extent (v1)"
                | Some (et, _) when not (writable et) -> fail $"`{y.Name}`'s element is not Float64, Float32, Int64 or Int32"
                | Some (_, n) when n < 1L -> fail $"`{y.Name}` is empty"
                | Some (et, n) ->
                if h.HasReynolds then fail "the kernel is a Reynolds symmetrization"
                elif h.KernelOutputRank <> 0 || h.KernelInputRanks |> List.exists (fun r -> r <> 0) then fail "the kernel does not map one cell of each operand to one cell"
                else
                let operandProof =
                    h.Arrays |> List.tryPick (fun a ->
                        match a with
                        | IRVar (oid, _) ->
                            (match Map.tryFind oid byId, Map.tryFind oid indexOf with
                             | Some ob, Some oi when oi < yi ->
                                 (match rank1Lit ob.Type with
                                  | Some (_, on) when on = n ->
                                      (match stored ob with
                                       | Ok _ -> None
                                       | Error why -> Some $"the operand `{ob.Name}` is not proved stored: {why}")
                                  | _ -> Some $"the operand `{ob.Name}` is not a plain dense rank-1 array of extent {n}")
                             | _ -> Some "an operand is not an earlier top-level binding")
                        | _ -> Some "an operand is an inline form, not a named stored array")
                match operandProof with
                | Some why -> fail why
                | None ->
                match resolve h.Kernel with
                | None -> fail "the kernel does not resolve to a callable"
                | Some c ->
                let scalarOf (t: IRType) = IRTotality.scalarElem (stripUnits t)
                if c.Params.Length <> h.Arrays.Length then fail "the kernel's parameters are not one per operand"
                elif c.Params |> List.exists (fun p -> (scalarOf p.Type).IsNone) then fail "a kernel parameter is not a scalar"
                elif scalarOf c.RetType <> Some et then fail "the kernel's result is not the array's scalar element"
                elif c.IsOmpParallel || not c.Parallelism.IsEmpty || c.IsCudaKernel || c.IsMpiParallel then
                    fail "the kernel requests a parallel strategy (v1's stream is serial)"
                else
                let captureProof =
                    c.Captures |> List.tryPick (fun cap ->
                        match Map.tryFind cap.Id byId, Map.tryFind cap.Id indexOf with
                        | Some cb, Some ci when ci < yi ->
                            (match stripUnits cb.Type with
                             | ArrayElem _ ->
                                 (match stored cb with
                                  | Ok _ -> None
                                  | Error why -> Some $"the captured array `{cb.Name}` is not proved stored: {why}")
                             | _ -> None)
                        | _ -> Some $"the kernel captures `{cap.Name}`, which is not an earlier top-level binding")
                match captureProof with
                | Some why -> fail why
                | None ->
                match IRTotality.auditApply resolve (fun _ -> None) deferredW h with
                | Error why -> fail $"the producer kernel is not total (a failure would move to the write): {why}"
                | Ok () ->
                if writes y.Value || (facts y.Value).MayWrite then fail "the producer may write (its effects would move to the write)"
                else
                // THE DEMAND CENSUS: the one write is y's only observer.
                let mentions (e: IRExpr) =
                    (collectVarRefsIR e).Contains yid
                    || (embeddedExtentExprs e |> List.exists (fun x -> (collectVarRefsIR x).Contains yid))
                let typeDemand (t: IRType) = irTypeExtentExprs t |> List.exists mentions
                let sourceRefs = modul.ProviderWrites |> Map.filter (fun _ s -> s.SourceId = yid) |> Map.count
                let observer =
                    roots.Force() |> List.tryPick (function
                        | RootBindingValue b when b.Id = yid -> None
                        | RootBindingValue b when mentions b.Value -> Some $"`{b.Name}` reads it"
                        | RootSideOperand (b, what, e) when mentions e -> Some $"{what} of `{b.Name}`"
                        | RootFunctionBody f when mentions f.Body || (f.Captures |> List.exists (fun cp -> cp.Id = yid)) ->
                            Some $"a callable body or capture in `{f.SourceName}`"
                        | RootType (where, t) when where <> $"`{y.Name}`" && typeDemand t -> Some $"the type of {where}"
                        | RootNameRef (where, nm) when nm = y.Name -> Some where
                        | _ -> None)
                if sourceRefs <> 1 then fail $"`{y.Name}` is written by {sourceRefs} provider writes"
                elif foreignRefs.Contains yid then fail $"`{y.Name}` is referenced by another module"
                else
                match observer with
                | Some why -> fail $"`{y.Name}` has another observer: {why}"
                | None ->
                match printSelection with
                | None -> fail $"`{y.Name}` is printed (every top-level binding prints unless a print selection, BLADE_PRINT / --print, excludes it)"
                | Some sel when Blade.PrintSelection.mayName sel y.Name -> fail $"the print selection names `{y.Name}`"
                | Some _ ->
                // every party the moved producer reads is checked too: a plan that
                // leaves an operand unstored at its own site would leave the
                // write reading nothing (fail closed, not by coincidence)
                let inputIds =
                    (h.Arrays |> List.collect (fun a -> collectVarRefsIR a |> Set.toList))
                    @ (c.Captures |> List.map (fun cp -> cp.Id))
                let ownerOf =
                    (yid :: inputIds) |> List.tryPick (fun id ->
                        ownedBy id |> Option.map (fun who ->
                            let nm = match Map.tryFind id byId with Some b -> b.Name | None -> $"__v{id}"
                            $"`{nm}` is scheduled by {who}"))
                match ownerOf with
                | Some why -> fail why
                | None ->
                // THE SNAPSHOT: nothing between y's site and the write's site writes.
                let between = [ yi + 1 .. wi - 1 ] |> List.map (fun i -> arr.[i])
                let writer =
                    between |> List.tryFind (fun b ->
                        let p = payloadOf b.Id b.Value
                        writes p || (facts p).MayWrite)
                let sameStore =
                    between |> List.tryFind (fun b ->
                        match Map.tryFind b.Id modul.ProviderWrites with
                        | Some s -> s.FilePath = w.FilePath
                        | None -> false)
                match writer, sameStore with
                | Some b, _ -> fail $"`{b.Name}`, between `{y.Name}` and the write, may write (the producer's inputs would differ at the write)"
                | _, Some b -> fail $"`{b.Name}`, between `{y.Name}` and the write, writes the same store"
                | None, None ->
                // THE PRICE (CostModel v1, per cell).
                match Blade.CostModel.kernelOps resolve c with
                | Error why -> fail $"the kernel is not priced: {why}"
                | Ok ops ->
                let validate = (et = ETFloat64 || et = ETFloat32)
                let k = int64 h.Arrays.Length
                let workDelta = if validate then ops.Count else 0L
                let matTraffic = k + 3L
                let streamTraffic = if validate then 2L * k else k
                let trafficDelta = matTraffic - streamTraffic
                let dominant = workDelta <= trafficDelta
                let rel = if dominant then "<=" else ">"
                let reads = if validate then "twice (validation + write)" else "once"
                let heavy = if ops.Heavy.IsEmpty then "" else " (" + String.concat ", " ops.Heavy + ")"
                let selection =
                    $"selection: work +{workDelta} {rel} traffic -{trafficDelta} per cell (model v1: materialized reads the {k} operand(s), writes {y.Name}, reads it for the BL8015 scan and the write = {matTraffic}; streamed reads the operand(s) {reads} = {streamTraffic}; kernel ops {ops.Count}{heavy})"
                if sinkStreamMode () = SinkStreamCost && not dominant then
                    fail $"materialized -- the validation pass's recompute is not dominant: {selection}; BLADE_SINK_STREAM=always streams anyway"
                else
                let block = min n 8192L
                let elemBytes = match et with ETFloat64 | ETInt64 -> 8L | _ -> 4L
                Ok { ProducerId = yid; ProducerName = y.Name; WriteId = wid; Apply = h; Kernel = c
                     Extent = n; Block = block; Validate = validate; Taken = ref false
                     Evidence =
                        [ $"`{y.Name}` ({n} cells) is never stored: the write `{w.VarName}` to {w.FilePath} produces it in blocks of {block} cells through one buffer ({block * elemBytes} bytes instead of {n * elemBytes})"
                          (if validate then "validation: a first pass recomputes every cell, block by block, by the same fill loop the write pass runs, and tallies the non-finite ones (BL8015: count and first flat cell) before any file effect"
                           else "validation: none (an integer element has no non-finite cell)")
                          "the writer's metadata (.zgroup, .zarray, .zattrs) unchanged, then its one chunk file written block by block: cell i at offset i, bitwise the stored map's bytes under BLADE_FP_CONTRACT=off (under the fast default the streamed fill and the stored nest are different contraction contexts: last-ULP differences are allowed, as for every schedule change); the two passes run textually identical fill loops, and the write pass's BL9001 re-check (after the metadata and a prefix) is the fail-closed invariant that they agreed on finiteness; a failed block write exits with today's message, leaving a prefix of the chunk"
                          "legality: the producer is IRTotality-total and write-free; nothing between its site and the write's writes (the snapshot); the write is its only observer (values, side operands, callables, captures, types, other writes, other modules) and the print selection excludes it"
                          (if sinkStreamMode () = SinkStreamAlways && not dominant then selection + " -- streamed anyway (BLADE_SINK_STREAM=always)" else selection)
                          $"cost: peak {n} -> {block} cells of `{y.Name}`" ] }
            match verdict with
            | Error why ->
                recordSinkStream y.Name (Blade.Effects.Declined why)
                    [ $"provider '{w.Provider}' write of `{w.VarName}`: `{y.Name}` is stored whole, validated, then written" ]
            | Ok plan when sinkStreamMode () = SinkStreamOff ->
                recordSinkStream y.Name (Blade.Effects.Declined "disabled by BLADE_SINK_STREAM") plan.Evidence
            | Ok plan ->
                let inputs =
                    (plan.Apply.Arrays |> List.collect (fun a -> collectVarRefsIR a |> Set.toList))
                    @ (plan.Kernel.Captures |> List.map (fun cp -> cp.Id))
                SinkStreamTable.record plan (Set.ofList inputs)
        | _ -> ()
