// Partial-row terminals and the sparse wildcard fold certificate (split out of Optimize.fs, cleanup K19).
module Blade.OptimizePartialRow

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

/// PM10: one private scoped rank-2 scalar map, immediately consumed by a
/// literal-seeded partial fold. Reuse the existing row apply + scalar fused
/// terminal; neither a producer pool nor a new backend operation is needed.
/// Top-level named producers are deliberately outside this first admission.
let internal fusePartialRowTerminalsWithCensusModule (builder: IRBuilder) (programFuncs: IRCallable list)
                                                    (foreignCensus: Set<IRId> option) (modul: IRModule) : IRModule =
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let resolve = resolveCallableIn funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let allCallables = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id)
    let roots = compactionRoots allCallables modul
    let minted = ResizeArray<IRCallable * IRId>()
    let scalar64 = IRTScalar ETFloat64
    let plain (ix: IRIndexType) =
        ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty
        && (match IRTotality.evalInt ix.Extent with Some n -> n >= 0L | None -> false)
    let arrayOf rank ty =
        match ty with
        | ArrayElem at when at.ElemType = scalar64 && not at.IsVirtual
                            && at.IndexTypes.Length = rank && List.forall plain at.IndexTypes -> Some at
        | _ -> None
    let sameAxes (a: IRArrayType) (b: IRArrayType) =
        a.IndexTypes.Length = b.IndexTypes.Length && List.forall2 IRTotality.sameIndex a.IndexTypes b.IndexTypes
    let unstrategized (c: IRCallable) =
        not c.IsStatic && not c.IsArityPoly && not c.IsOmpParallel && c.Parallelism.IsEmpty
        && not c.IsCudaKernel && not c.IsMpiParallel
    // Totality alone permits immutable free scalar reads. This motion also
    // requires a CLOSED callable graph: params/local lets/callees only, no
    // captures, module variables, callbacks, recursion or unknown functions.
    // The deferred-value predicate (gap G98): the closed-graph rule below
    // already refuses every free reference that is not a callable, so no
    // deferred value is reachable today; the predicate keeps the totality
    // audit sound should that rule widen.
    let isDeferredId = programDeferredId modul allCallables Seq.empty
    let closedTotal (root: IRCallable) : Result<unit, string> =
        let rec audit visiting (c: IRCallable) =
            if visiting |> Set.contains c.Id then Error "a recursive callable"
            elif not (unstrategized c) then Error "a parallel/device/static/arity strategy"
            elif not c.Captures.IsEmpty then Error "a callable capture"
            elif writes c.Body then Error "a callable body may write"
            else
            match IRTotality.auditCallable resolve (fun _ -> None) isDeferredId c [] with
            | Error why -> Error why
            | Ok () ->
                let mutable bound = c.Params |> List.map _.VarId |> Set.ofList
                iterIRExpr (function IRLet (id, _, _) -> bound <- bound.Add id | _ -> ()) c.Body
                let refs = collectVarRefsIR c.Body |> Set.filter (fun id -> not (bound.Contains id)) |> Set.toList
                let rec check = function
                    | [] -> Ok ()
                    | id :: rest ->
                        match Map.tryFind id funcs with
                        | None -> Error "an unbound/global/callback reference"
                        | Some callee ->
                            match audit (visiting.Add c.Id) callee with Error why -> Error why | Ok () -> check rest
                check refs
        audit Set.empty root
    let literalSeed = function
        | IRLit (IRLitFloat _) | IRUnaryOp (IRNeg, IRLit (IRLitFloat _)) -> true
        | _ -> false
    let rowFold (info: ApplyInfo) =
        match resolve info.Kernel with
        | Some row when row.Params.Length = 1 && row.RetType = scalar64 && row.Captures.IsEmpty && unstrategized row ->
            match row.Params.Head.Type, row.Body with
            | ArrayElem at, ReduceOfNamed (id, ty, k, Some seed)
                when id = row.Params.Head.VarId && ty = row.Params.Head.Type
                     && at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1
                     && at.IndexTypes.Head.Rank = 1 && at.IndexTypes.Head.IxKind = IxKPlain
                     && at.IndexTypes.Head.Symmetry = SymNone && at.IndexTypes.Head.Dependencies.IsEmpty -> Some (row, k, seed)
            | _ -> None
        | _ -> None
    let record subject outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "partial-row-fold"; Version = 1; Span = declSpanOf subject
              Subject = subject; Outcome = outcome; Evidence = evidence }
    let peel = function IRCompute (IRApplyCombinator i) | IRApplyCombinator i -> Some i | _ -> None
    // PM25: the private hard transpose has no observer besides this exact
    // row apply. Manufacture the already-supported C range / R fused-fold
    // idiom, rather than changing the ownership or layout of IRTranspose.
    let transposeRows (host: IRBinding) pid sourceId sourceType (consumer: ApplyInfo) =
        let decide outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "transpose-row-fold"; Version = 1; Span = declSpanOf host.Name
                  Subject = host.Name; Outcome = outcome; Evidence = evidence }
        let unchanged why = decide (Blade.Effects.Declined why) []; host
        let exprDemand e =
            (collectVarRefsIR e).Contains pid
            || (embeddedExtentExprs e |> List.exists (fun x -> (collectVarRefsIR x).Contains pid))
        let typeDemand ty = irTypeExtentExprs ty |> List.exists exprDemand
        let residual =
            // The only exempted value is this host's two certified array
            // input occurrences (Arrays and MethodFor.Arrays). Keep their
            // types and every other payload, including reference-equal
            // expressions rooted in a different binding.
            mapIRExpr (function IRVar (id, ty) when id = pid -> IRZero ty | n -> n) host.Value
        let hasDemand =
            roots |> List.exists (function
                | RootBindingValue b -> exprDemand (if b.Id = host.Id then residual else b.Value)
                | RootSideOperand (_, _, e) -> exprDemand e
                | RootFunctionBody f -> exprDemand f.Body || (f.Captures |> List.exists (fun c -> c.Id = pid))
                | RootType (_, ty) -> typeDemand ty
                | RootSourceRef (_, id) -> id = pid
                | RootNameRef _ -> true)
        let mutable uses = 0
        iterIRExpr (function IRVar (id, _) when id = pid -> uses <- uses + 1 | _ -> ()) host.Value
        let sourceIndex = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = sourceId)
        let hostIndex = modul.Bindings |> List.findIndex (fun b -> b.Id = host.Id)
        match foreignCensus with
        | None -> unchanged "requires explicit complete foreign-demand census evidence"
        | Some refs when refs.Contains pid -> unchanged "the private transpose is referenced by another module"
        | Some _ when not (gateOn "BLADE_FUSION") -> unchanged "disabled by BLADE_FUSION"
        | Some _ when not (Blade.Toolchain.fpReproEnabled ()) -> unchanged "requires the reproducible serial column graph (BLADE_FP_REPRO=1)"
        | Some _ when hasDemand || uses <> 2 -> unchanged "the private transpose has an additional value/type/capture/side-table demand"
        | Some _ when not (bindingSideOperands modul host.Id).IsEmpty
                      || modul.ProviderReads.ContainsKey host.Id || modul.ProviderWrites.ContainsKey host.Id ->
            unchanged "the host has an additional execution payload"
        | Some _ ->
            match arrayOf 2 sourceType, arrayOf 1 consumer.OutputType, consumer.Arrays, rowFold consumer, sourceIndex with
            | Some input, Some output, [IRVar (p, privateType)], Some (row, foldExpr, seed), Some si when p = pid && si < hostIndex ->
                let source = modul.Bindings.[si]
                let transposed = { input with IndexTypes = [input.IndexTypes.[1]; input.IndexTypes.[0]] }
                let storedRoles (a: IRArrayType) = a.IndexTypes |> List.forall (fun ix -> ix.Kind = SDimension)
                let knownArray roles expected ty =
                    match arrayOf 2 ty with
                    | Some a -> sameAxes a expected && (a.IndexTypes |> List.map _.Kind) = roles
                    | None -> false
                let metadataKnown =
                    consumer.KernelInputRanks = [1] && consumer.KernelOutputRank = 0 && not consumer.IsCoIteration
                    && not consumer.HasReynolds && consumer.KernelTDims.IsEmpty && consumer.SharedIndexTypes.IsEmpty
                    && consumer.SpeedupFactor = 1L && consumer.ReynoldsSpeedup = 1L
                    && consumer.TriangularLevels = [false] && consumer.SymcomStates = [SCNeither]
                    && consumer.SDimsPerArray = [1] && consumer.Identities.Length = 1
                    && consumer.ArrayTypes.Length = 1
                    && knownArray [SDimension; TDimension] transposed (mkArrayLike consumer.ArrayTypes.Head)
                    && knownArray [SDimension; SDimension] transposed privateType
                    && source.Type = sourceType && storedRoles input && storedRoles output
                    && (match arrayOf 1 host.Type with Some h -> sameAxes h output && storedRoles h | None -> false)
                    && IRTotality.sameIndex output.IndexTypes.Head input.IndexTypes.[1]
                    && (match consumer.Kernel with
                        | IRVar (id, ty) -> id = row.Id && ty = mkFuncArrow [row.Params.Head.Type] scalar64
                        | _ -> false)
                    && (match consumer.Loop with
                        | IRMethodFor mf ->
                            mf.TotalSDims = 2 && mf.SDimsPerArray = [2] && mf.SharedIndexTypes.IsEmpty
                            && mf.Identities = consumer.Identities && mf.ArrayTypes.Length = 1
                            && knownArray [SDimension; SDimension] transposed (mkArrayLike mf.ArrayTypes.Head)
                            && (match mf.Arrays with [IRVar (v, t)] -> v = pid && t = privateType | _ -> false)
                        | _ -> false)
                let payloadUnsafe b = bindingPayload modul b |> List.exists (fun e -> writes e || lifetimeBarrier e)
                if not metadataKnown then unchanged "the transpose/consumer/index metadata is outside the known plain column geometry"
                elif not (literalSeed seed) || typeOf seed <> scalar64 then unchanged "requires an explicit literal Float64 seed per column"
                elif not (modul.Functions |> List.exists (fun f -> f.Id = row.Id)) then unchanged "the row callable is not owned by this module"
                elif payloadUnsafe host then unchanged "a host payload may write or cross a pool lifetime event"
                else
                match resolve foldExpr, storedArrayProof modul source with
                | None, _ -> unchanged "the scalar fold is unresolved"
                | _, Error why -> unchanged $"source is not proved stored: {why}"
                | Some fold, Ok (_, chain) ->
                    if fold.Params.Length <> 2 || (fold.Params |> List.exists (fun p -> p.Type <> scalar64)) || fold.RetType <> scalar64
                       || (match foldExpr with IRVar (_, ty) -> ty <> mkFuncArrow [scalar64; scalar64] scalar64 | _ -> true) then
                        unchanged "the fold is outside the concrete scalar admission"
                    elif chain |> List.exists (fun b -> modul.ProviderReads.ContainsKey b.Id || payloadUnsafe b) then
                        unchanged "a stored source initializer/alias is a provider, may write or has a lifetime event"
                    else
                    match closedTotal fold with
                    | Error why -> unchanged $"the fold is not closed pure/total: {why}"
                    | Ok () ->
                        let rAxis = { input.IndexTypes.[0] with Kind = SDimension }
                        let cAxis = { input.IndexTypes.[1] with Kind = SDimension }
                        let coordinateType (ix: IRIndexType) =
                            let tag = match ix.Tag with Some n -> IRefNamed n | None -> IRefAnon (ix.Id, ix.Extent)
                            IRTIdxTagged (IRTScalar ETInt64, tag)
                        let param name (ix: IRIndexType) : IRParam =
                            let id = builder.FreshId ()
                            { Name = $"{name}_{id}"; VarId = id; Index = 0; Type = coordinateType ix }
                        let cp, rp = param "__transpose_c" cAxis, param "__transpose_r" rAxis
                        let cv, rv = IRVar (cp.VarId, cp.Type), IRVar (rp.VarId, rp.Type)
                        let sourceCapture: CaptureInfo = { Id = sourceId; Name = source.Name; Type = sourceType; IsMutable = source.IsMutable }
                        let columnCapture: CaptureInfo = { Id = cp.VarId; Name = cp.Name; Type = cp.Type; IsMutable = false }
                        // Bounds are established by construction: each coordinate
                        // ranges over the original source's EXACT index space.
                        let read = IRIndex (IRVar (sourceId, sourceType), [rv; cv], None)
                        let gather = mkCallable builder defaultLambdaOptions [rp] read scalar64 [sourceCapture; columnCapture] false [] [] false false 256 false
                        let gather = { gather with Name = $"__transpose_cell_{gather.Id}"; SourceName = row.SourceName }
                        let map (ix: IRIndexType) (kernel: IRCallable) outputType : ApplyInfo =
                            let range = IRRange ([ix], None)
                            let identity = AIDLiteral (builder.FreshId ())
                            let a = { ElemType = coordinateType ix; IndexTypes = [ix]; IsVirtual = true; Identity = None }
                            { Loop = IRMethodFor { Arrays = [range]; Identities = [identity]; ArrayTypes = [a]
                                                   SDimsPerArray = [1]; TotalSDims = 1; SharedIndexTypes = [] }
                              Kernel = IRVar (kernel.Id, mkFuncArrow (kernel.Params |> List.map _.Type) scalar64)
                              Arrays = [range]; Identities = [identity]; ArrayTypes = [a]; SharedIndexTypes = []
                              SymcomStates = [SCNeither]; TriangularLevels = [false]; SDimsPerArray = [1]
                              KernelInputRanks = [0]; KernelOutputRank = 0; KernelTDims = []
                              SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
                              OutputType = outputType; IsCoIteration = false }
                        let innerType = mkArrayLike { ElemType = scalar64; IndexTypes = [rAxis]; IsVirtual = false; Identity = None }
                        let terminal = IRReduceCompute (IRApplyCombinator (map rAxis gather innerType), foldExpr, seed)
                        let column = mkCallable builder defaultLambdaOptions [cp] terminal scalar64 [sourceCapture] false [] [] false false 256 false
                        let column = { column with Name = $"__transpose_rows_{column.Id}"; SourceName = row.SourceName }
                        minted.Add (gather, row.Id)
                        minted.Add (column, row.Id)
                        decide Blade.Effects.Applied
                            [ "private hard-transpose pool removed; required stored source and original C output retained"
                              "C outer range / R inner fused terminal; exact original fold and literal seed once per column"
                              "same index identity, static in-bounds source reads; ascending reproducible association"
                              "complete value/type/capture/side-table/foreign census; immediate scope; no writes or lifetime motion" ]
                        { host with Value = IRCompute (IRApplyCombinator (map cAxis column host.Type)) }
            | _ -> unchanged "requires a dominating stored rank-2 Float64 source and one immediate seeded row fold"
    let rewrite (host: IRBinding) =
        let unchanged why = record host.Name (Blade.Effects.Declined why) []; host
        match host.Value with
        | IRLet (pid, IRTranspose (IRVar (sourceId, sourceType), 0, 1), IRCompute (IRApplyCombinator consumer)) ->
            transposeRows host pid sourceId sourceType consumer
        | IRLet (_, IRTranspose _, _) ->
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "transpose-row-fold"; Version = 1; Span = declSpanOf host.Name; Subject = host.Name
                  Outcome = Blade.Effects.Declined "requires one private transpose immediately consumed by its sole row fold"; Evidence = [] }
            host
        | IRLet (pid, value, IRCompute (IRApplyCombinator consumer)) when (peel value).IsSome ->
            let producer = (peel value).Value
            match producer.Arrays, consumer.Arrays, rowFold consumer with
            | [ (IRVar (sourceId, sourceType) as source) ], [ IRVar (p, privateType) ], Some (row, foldExpr, seed) when p = pid ->
                let loopsKnown =
                    match producer.Loop, consumer.Loop with
                    | IRMethodFor a, IRMethodFor b ->
                        (match a.Arrays, b.Arrays with
                         | [IRVar (s, _)], [IRVar (v, _)] -> s = sourceId && v = pid
                         | _ -> false)
                    | _ -> false
                let sourceBinding = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = sourceId)
                let hostIndex = modul.Bindings |> List.findIndex (fun b -> b.Id = host.Id)
                let hasDemand =
                    let mentions e = (collectVarRefsIR e).Contains pid
                    let expr e = mentions e || (embeddedExtentExprs e |> List.exists mentions)
                    let ty t = irTypeExtentExprs t |> List.exists expr
                    // Remove precisely this expression by identity, rather
                    // than exempting a name, an entire host or all its types.
                    let rec outside e =
                        if obj.ReferenceEquals(e, host.Value) then IRLit IRLitUnit
                        else rebuildWith e (childrenOf e |> List.map outside)
                    roots |> List.exists (function
                        | RootBindingValue b when b.Id = host.Id -> expr (outside b.Value)
                        | RootBindingValue b -> expr b.Value
                        | RootSideOperand (_, _, e) -> expr e
                        | RootFunctionBody f -> expr f.Body || (f.Captures |> List.exists (fun c -> c.Id = pid))
                        | RootType (_, t) -> ty t
                        | RootSourceRef (_, id) -> id = pid
                        // Local let names do not survive IR. A name-only
                        // provider operand cannot be certified by this census.
                        | RootNameRef _ -> true)
                let privateMentions =
                    let mutable n = 0
                    iterIRExpr (function IRVar (id, _) when id = pid -> n <- n + 1 | _ -> ()) host.Value
                    n
                let privateTypeDemand =
                    embeddedExtentExprs host.Value |> List.exists (fun e -> (collectVarRefsIR e).Contains pid)
                if not (gateOn "BLADE_FUSION") then unchanged "disabled by BLADE_FUSION"
                elif not (Blade.Toolchain.fpReproEnabled ()) then unchanged "requires the reproducible serial row graph (BLADE_FP_REPRO=1)"
                elif hasDemand || privateTypeDemand || privateMentions <> 2 then
                    unchanged "the private producer has an additional type/capture/side-table demand"
                elif not (literalSeed seed) then unchanged "requires an explicit literal Float64 seed per row"
                elif not loopsKnown then unchanged "an unsupported loop provenance payload survives"
                elif producer.KernelInputRanks <> [0] || producer.KernelOutputRank <> 0 || producer.IsCoIteration
                     || producer.HasReynolds || not producer.KernelTDims.IsEmpty
                     || producer.SpeedupFactor <> 1L || producer.ReynoldsSpeedup <> 1L
                     || producer.SymcomStates <> [SCNeither; SCNeither]
                     || producer.TriangularLevels <> [false; false] then
                    unchanged "the producer is not one plain scalar map"
                elif consumer.KernelInputRanks <> [1] || consumer.KernelOutputRank <> 0 || consumer.HasReynolds
                     || consumer.IsCoIteration || not consumer.KernelTDims.IsEmpty
                     || consumer.SpeedupFactor <> 1L || consumer.ReynoldsSpeedup <> 1L
                     || consumer.TriangularLevels <> [false] || consumer.SymcomStates <> [SCNeither] then
                    unchanged "the consumer is not one plain row fold"
                else
                match arrayOf 2 sourceType, arrayOf 2 producer.OutputType, arrayOf 1 consumer.OutputType,
                      resolve producer.Kernel, resolve foldExpr, sourceBinding with
                | Some input, Some mapped, Some result, Some map, Some fold, Some si when si < hostIndex ->
                    let sourceB = modul.Bindings.[si]
                    let proof = storedArrayProof modul sourceB
                    // Every omitted metadata payload is either the known
                    // source/row role or a static, identical plain axis. In
                    // particular no hidden shared/kernel axis may disappear.
                    let storedRoles (at: IRArrayType) = at.IndexTypes |> List.forall (fun ix -> ix.Kind = SDimension)
                    let denseMetadata (at: IRArrayType) =
                        at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 2
                        && List.forall plain at.IndexTypes
                    let loopMetadata (info: ApplyInfo) expected =
                        match info.Loop with
                        | IRMethodFor mf ->
                            mf.TotalSDims = 2 && mf.SDimsPerArray = [2] && mf.SharedIndexTypes.IsEmpty
                            && mf.Identities = info.Identities && mf.ArrayTypes.Length = 1
                            && denseMetadata mf.ArrayTypes.Head
                            && sameAxes mf.ArrayTypes.Head expected && storedRoles mf.ArrayTypes.Head
                        | _ -> false
                    let metadataKnown =
                        producer.SharedIndexTypes.IsEmpty && consumer.SharedIndexTypes.IsEmpty
                        && producer.SDimsPerArray = [2] && consumer.SDimsPerArray = [1]
                        && producer.Identities.Length = 1 && consumer.Identities.Length = 1
                        && producer.ArrayTypes.Length = 1 && consumer.ArrayTypes.Length = 1
                        && denseMetadata producer.ArrayTypes.Head && denseMetadata consumer.ArrayTypes.Head
                        && sameAxes producer.ArrayTypes.Head input && storedRoles producer.ArrayTypes.Head
                        && sameAxes consumer.ArrayTypes.Head mapped
                        && (consumer.ArrayTypes.Head.IndexTypes |> List.map _.Kind) = [SDimension; TDimension]
                        && storedRoles input && storedRoles mapped && storedRoles result
                        && loopMetadata producer input && loopMetadata consumer mapped
                        && (match arrayOf 2 privateType with Some at -> sameAxes at mapped && storedRoles at | None -> false)
                    if not (sameAxes input mapped) || not (IRTotality.sameIndex result.IndexTypes.Head input.IndexTypes.Head) then
                        unchanged "source/map/result index provenance disagrees"
                    elif not (bindingSideOperands modul host.Id).IsEmpty
                         || modul.ProviderReads.ContainsKey host.Id || modul.ProviderWrites.ContainsKey host.Id then
                        unchanged "the host has an additional execution payload"
                    elif not metadataKnown then unchanged "the apply/method_for metadata is outside the known plain row geometry"
                    elif map.Params.Length <> 1 || map.Params.Head.Type <> scalar64 || map.RetType <> scalar64
                         || fold.Params.Length <> 2 || (fold.Params |> List.exists (fun p -> p.Type <> scalar64))
                         || fold.RetType <> scalar64 || not (modul.Functions |> List.exists (fun f -> f.Id = row.Id)) then
                        unchanged "the map/fold/row callable is outside the concrete scalar admission"
                    else
                    match proof, closedTotal map, closedTotal fold with
                    | Error why, _, _ -> unchanged $"source is not proved stored: {why}"
                    | _, Error why, _ | _, _, Error why -> unchanged $"a producer/fold is not closed pure/total: {why}"
                    | Ok (_, chain), _, _ when chain |> List.exists (fun b -> modul.ProviderReads.ContainsKey b.Id) ->
                        unchanged "provider sources are outside the initial stored-array admission"
                    | Ok (_, chain), _, _ when chain |> List.exists (fun b -> bindingPayload modul b |> List.exists writes) ->
                        unchanged "a stored source initializer or alias payload may write"
                    | Ok _, Ok (), Ok () ->
                        // The old row param may carry __reduce_inferred_n.
                        // Build its real C axis from the stored source, never
                        // from that abstract signature. Keep output R EXACTLY.
                        let column = { input.IndexTypes.[1] with Kind = SDimension }
                        let rowAt = { ElemType = scalar64; IndexTypes = [column]; IsVirtual = false; Identity = None }
                        let rowType = mkArrayLike rowAt
                        let rid = builder.FreshId ()
                        let rp = { Name = $"__partial_row_{rid}"; Index = 0; VarId = rid; Type = rowType }
                        let rv = IRVar (rid, rowType)
                        let rowIdentity = AIDVariable rp.Name
                        let inner =
                            { producer with
                                Loop = IRMethodFor { Arrays = [rv]; Identities = [rowIdentity]; ArrayTypes = [rowAt]
                                                     SDimsPerArray = [1]; TotalSDims = 1; SharedIndexTypes = [] }
                                Arrays = [rv]; Identities = [rowIdentity]; ArrayTypes = [rowAt]
                                SharedIndexTypes = []; SymcomStates = [SCNeither]; TriangularLevels = [false]
                                SDimsPerArray = [1]; KernelInputRanks = [0]; KernelTDims = []; OutputType = rowType }
                        let fid = builder.FreshId ()
                        let terminal = IRReduceCompute (IRApplyCombinator inner, foldExpr, seed)
                        let body = IRLet (fid, terminal, IRVar (fid, scalar64))
                        let fresh = mkCallable builder defaultLambdaOptions [rp] body scalar64 [] false [] [] false false 256 false
                        let fresh = { fresh with Name = $"__partial_rows_{fresh.Id}"; SourceName = row.SourceName }
                        let outerAt =
                            { input with IndexTypes = [ { input.IndexTypes.[0] with Kind = SDimension }
                                                        { input.IndexTypes.[1] with Kind = TDimension } ] }
                        let outer =
                            { consumer with
                                Loop = IRMethodFor { Arrays = [source]; Identities = producer.Identities; ArrayTypes = [input]
                                                     SDimsPerArray = [2]; TotalSDims = 2; SharedIndexTypes = [] }
                                Kernel = IRVar (fresh.Id, mkFuncArrow [rowType] scalar64)
                                Arrays = [source]; Identities = producer.Identities; ArrayTypes = [outerAt] }
                        minted.Add (fresh, row.Id)
                        record host.Name Blade.Effects.Applied
                            [ "one private rank-2 mapped pool removed; stored input and original rank-1 result retained"
                              "concrete source C row view; original R output type/identity; literal seed once per row"
                              "closed pure/total producer/fold/callee graph; immediate sole-use scope and complete residual demand census"
                              "reproducible ascending row chains; C=0 returns each seed, R=0 executes no row seeds" ]
                        { host with Value = IRCompute (IRApplyCombinator outer) }
                | _ -> unchanged "requires a dominating stored plain rank-2 Float64 source and a rank-1 Float64 result"
            | _ -> unchanged "requires one source and an immediate sole seeded row-fold consumer"
        | IRLet (_, value, _) when (peel value).IsSome ->
            match arrayOf 2 (peel value).Value.OutputType with
            | Some _ -> unchanged "the private producer is not consumed immediately by its sole row fold"
            | None -> host
        | IRCompute (IRApplyCombinator info) when (rowFold info).IsSome ->
            unchanged "requires a private scoped producer; top-level named/printed producers retain their storage"
        | _ -> host
    // PM09: the observed rank-k desugar has ONE scalar state, with three
    // unit-valued statement wrappers. Recognize that exact private rank-2
    // region; do not flatten a partial/outer fold or normalize arbitrary lets.
    let removedFullPools = System.Collections.Generic.HashSet<IRId>()
    let fullFold (host: IRBinding) =
        let decide outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "full-map-fold"; Version = 1; Span = declSpanOf host.Name
                  Subject = host.Name; Outcome = outcome; Evidence = evidence }
        let unchanged why = decide (Blade.Effects.Declined why) []; Some host
        match host.Type, host.Value with
        | IRTScalar ETFloat64, IRLet (pid, IRApplyCombinator info, rest) when (arrayOf 2 info.OutputType).IsSome ->
            let int64Type = IRTScalar ETInt64
            let step acc r c = function
                | IRLet (u, IRAssign (IRVar (target, tt),
                    IRApp (fold, [IRVar (a, at); IndexOfNamed (p, pt, [IRVar (ri, rt); IRVar (ci, ct)], _)], ret)), IRLit IRLitUnit)
                    when target = acc && a = acc && p = pid && ri = r && ci = c
                         && tt = scalar64 && at = scalar64 && ret = scalar64 && rt = int64Type && ct = int64Type -> Some (u, fold, pt)
                | _ -> None
            let shape =
                match rest with
                | IRLet (acc, seed,
                    IRLet (outerUnit, IRForRange (r, loR, hiR,
                        IRLet (innerUnit, IRForRange (c, loC, hiC, body), IRLit IRLitUnit)), IRVar (last, ty)))
                    when last = acc && ty = scalar64 ->
                    step acc r c body |> Option.map (fun (u, fold, pt) ->
                        acc, seed, [outerUnit; innerUnit; u], r, c, [loR; loC], [hiR; hiC], fold, pt)
                | _ -> None
            match shape, info.Arrays, resolve info.Kernel, foreignCensus with
            | Some (acc, seed, units, r, c, lows, highs, foldExpr, privateType), [IRVar (sid, sty)], Some map, Some foreign ->
                let removed = Set.ofList (pid :: acc :: r :: c :: units)
                let residual = IRReduceCompute (IRApplyCombinator info, foldExpr, seed)
                let refs e =
                    Set.union (collectVarRefsIR e)
                        (embeddedExtentExprs e |> List.map collectVarRefsIR |> Set.unionMany)
                let demanded e = not (Set.intersect removed (refs e)).IsEmpty
                let bad e = writes e || lifetimeBarrier e
                let badPayload e = bad e || (embeddedExtentExprs e |> List.exists bad)
                let extraDemand =
                    roots |> List.exists (function
                        | RootBindingValue b -> demanded (if b.Id = host.Id then residual else b.Value)
                        | RootSideOperand (_, _, e) -> demanded e
                        | RootFunctionBody f -> demanded f.Body || (f.Captures |> List.exists (fun x -> removed.Contains x.Id))
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists demanded
                        | RootSourceRef (_, id) -> removed.Contains id
                        | RootNameRef _ -> true)
                // Count definitions too: a malformed second scope may reuse a
                // removed id without containing a variable read of it.
                let definitions = System.Collections.Generic.Dictionary<IRId, int>()
                let add id =
                    let n = match definitions.TryGetValue id with true, n -> n | _ -> 0
                    definitions.[id] <- n + 1
                let note e =
                    iterIRExpr (function
                        | BinderShape (_, scopes) -> for ids, _ in scopes do for id in ids do add id
                        | _ -> ()) e
                for b in modul.Bindings do add b.Id
                for f in allCallables do
                    add f.Id
                    for p in f.Params do add p.VarId
                for root in roots do
                    match root with
                    | RootBindingValue b -> note b.Value; embeddedExtentExprs b.Value |> List.iter note
                    | RootSideOperand (_, _, e) | RootFunctionBody { Body = e } -> note e; embeddedExtentExprs e |> List.iter note
                    | RootType (_, ty) -> irTypeExtentExprs ty |> List.iter note
                    | _ -> ()
                let unique =
                    removed.Count = 7 && (removed |> Set.forall (fun id ->
                        match definitions.TryGetValue id with true, 1 -> true | _ -> false))
                    && (modul.Bindings |> List.filter (fun b -> b.Id = host.Id) |> List.length) = 1
                    && (modul.Functions |> List.map _.Id |> Set.ofList |> Set.count) = modul.Functions.Length
                let metadata (input: IRArrayType) (output: IRArrayType) =
                    let stored (at: IRArrayType) = at.Identity.IsNone && (at.IndexTypes |> List.forall (fun ix -> ix.Kind = SDimension))
                    let known at =
                        match arrayOf 2 (mkArrayLike at) with
                        | Some a -> sameAxes a input && stored a
                        | None -> false
                    sameAxes input output && stored input && stored output
                    && (match arrayOf 2 privateType with Some a -> sameAxes a output && stored a | _ -> false)
                    && info.KernelInputRanks = [0] && info.KernelOutputRank = 0 && info.KernelTDims.IsEmpty
                    && info.SDimsPerArray = [2] && info.SharedIndexTypes.IsEmpty && not info.IsCoIteration
                    && info.SymcomStates = [SCNeither; SCNeither] && info.TriangularLevels = [false; false]
                    && info.SpeedupFactor = 1L && info.ReynoldsSpeedup = 1L && not info.HasReynolds
                    && info.ArrayTypes.Length = 1 && known info.ArrayTypes.Head && info.Identities.Length = 1
                    && (match info.Loop with
                        | IRMethodFor mf ->
                            mf.TotalSDims = 2 && mf.SDimsPerArray = [2] && mf.SharedIndexTypes.IsEmpty
                            && mf.Identities = info.Identities && mf.ArrayTypes.Length = 1 && known mf.ArrayTypes.Head
                            && (match mf.Arrays with [IRVar (id, ty)] -> id = sid && ty = sty | _ -> false)
                        | _ -> false)
                    && lows = [IRLit (IRLitInt 0L); IRLit (IRLitInt 0L)]
                    && List.forall2
                        (fun (hi: IRExpr) (ix: IRIndexType) ->
                            IRTotality.evalInt hi = IRTotality.evalInt ix.Extent
                            && (match hi with IRLit (IRLitInt _) -> true | _ -> false))
                        highs output.IndexTypes
                let signature arity (cl: IRCallable) reference =
                    cl.Params.Length = arity && cl.RetType = scalar64
                    && (cl.Params |> List.mapi (fun i p -> p.Index = i && p.Type = scalar64) |> List.forall id)
                    && (cl.Params |> List.map _.VarId |> Set.ofList |> Set.count) = arity
                    && typeOf reference = mkFuncArrow (List.replicate arity scalar64) scalar64
                let rec serialGraph seen (cl: IRCallable) =
                    if seen |> Set.contains cl.Id then true
                    else
                        let scalar = function IRTScalar (ETFloat64 | ETInt64 | ETBool) -> true | _ -> false
                        unstrategized cl && cl.ArityParam.IsNone && not cl.IsCommutative
                        && cl.CommGroups.IsEmpty && cl.AntisymGroups.IsEmpty && cl.SignParities.IsEmpty
                        && scalar cl.RetType && (cl.Params |> List.mapi (fun i p -> p.Index = i && scalar p.Type) |> List.forall id)
                        && (cl.Params |> List.map _.VarId |> Set.ofList |> Set.count) = cl.Params.Length
                        && not cl.Effects.Mutates && not cl.Effects.EmitsOutput && not cl.Effects.ReadsExternal && not cl.Effects.MayFail
                        && not (lifetimeBarrier cl.Body)
                        && (collectVarRefsIR cl.Body |> Set.toList |> List.choose (fun id -> Map.tryFind id funcs)
                            |> List.forall (serialGraph (seen.Add cl.Id)))
                let unsafeRoots =
                    roots |> List.exists (function
                        | RootBindingValue b -> badPayload (if b.Id = host.Id then residual else b.Value)
                        | RootSideOperand (_, _, e) | RootFunctionBody { Body = e } -> badPayload e
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists badPayload
                        | _ -> false)
                match arrayOf 2 sty, arrayOf 2 info.OutputType, resolve foldExpr,
                      modul.Bindings |> List.tryFind (fun b -> b.Id = sid) with
                | Some input, Some output, Some fold, Some source when source.Type = sty ->
                    match storedArrayProof modul source, closedTotal map, closedTotal fold with
                    | Ok (_, chain), Ok (), Ok () ->
                        let owned = Set.union removed (chain |> List.map _.Id |> Set.ofList)
                        let ownerIds = owned.Add host.Id
                        let planned id =
                            (HotRowTable.tryProducer id).IsSome || (HotRowTable.tryConsumer id).IsSome
                            || (CellFoldTable.tryLoop id).IsSome || (CellFoldTable.tryFoldLet id).IsSome
                            || (RecStepTable.tryLoop id).IsSome || (RecStepTable.tryStep id).IsSome
                            || (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome
                            || (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome || (CompactionTable.tryFoldMap id).IsSome
                            || (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome
                        let ownerConflict =
                            (ownerIds |> Set.exists planned)
                            || (PoolReuseTable.donors () |> Map.exists (fun a b -> ownerIds.Contains a || ownerIds.Contains b))
                            || (PoolReuseTable.returnDonors () |> Map.exists (fun a b -> ownerIds.Contains a || ownerIds.Contains b))
                            || (MemberHoistTable.all () |> List.exists (fun p -> ownerIds.Contains p.HostId || ownerIds.Contains p.SetId || not (Set.intersect ownerIds p.Kernels).IsEmpty))
                        let hiddenExecution =
                            (modul.SparseInits |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.CompoundInits |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.RandomInits |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.ProviderReads |> Map.exists (fun id _ -> removed.Contains id))
                            || (modul.ProviderWrites |> Map.exists (fun id _ -> removed.Contains id))
                        let position id = modul.Bindings |> List.tryFindIndex (fun b -> b.Id = id)
                        let dominates =
                            match position sid, position host.Id with Some s, Some h -> s < h | _ -> false
                        let sourceIdentity =
                            match info.Identities with [AIDVariable name] -> name = source.Name | _ -> false
                        if not (gateOn "BLADE_FUSION") || not (Blade.Toolchain.fpReproEnabled ()) then unchanged "requires FUSION and reproducible serial folding"
                        elif not unique || not dominates then unchanged "nonunique local identities or source does not dominate"
                        elif not (literalSeed seed) || typeOf seed <> scalar64 then unchanged "requires a literal Float64 seed"
                        elif not (metadata input output && sourceIdentity && signature 1 map info.Kernel && signature 2 fold foldExpr) then
                            unchanged "unsupported map/loop/fold metadata or index provenance"
                        elif not (serialGraph Set.empty map && serialGraph Set.empty fold) then unchanged "a callable has effect/strategy metadata"
                        elif not (Set.intersect owned foreign).IsEmpty || extraDemand then unchanged "additional local/source/foreign demand"
                        elif unsafeRoots || (chain |> List.exists (fun b -> modul.ProviderReads.ContainsKey b.Id)) then unchanged "a source/payload may write or cross a lifetime/provider boundary"
                        elif hiddenExecution || not (bindingSideOperands modul host.Id).IsEmpty || modul.ProviderReads.ContainsKey host.Id || modul.ProviderWrites.ContainsKey host.Id then unchanged "host/local execution side payload"
                        elif ownerConflict || (Set.intersect removed modul.MutableArrayLets |> Set.exists ((<>) pid))
                             || (modul.DerivedFuncOrigins |> Map.exists (fun a b -> removed.Contains a || removed.Contains b)) then unchanged "overlapping storage/identity owner"
                        else
                            removedFullPools.Add pid |> ignore
                            decide Blade.Effects.Applied ["one private rank-2 mapped pool removed; stored source retained"; "one literal seed and original ascending scalar fold graph; complete residual/foreign/write census"]
                            Some { host with Value = residual }
                    | _ -> unchanged "source is not stored or callable graph is not closed pure/total"
                | _ -> unchanged "requires stored rank-2 Float64 input and resolved scalar fold"
            | _ -> unchanged "requires the exact unit-wrapped full-fold region and complete foreign census"
        | _ -> None
    let bindings = modul.Bindings |> List.map (fun b -> match fullFold b with Some full -> full | None -> rewrite b)
    if minted.Count = 0 && removedFullPools.Count = 0 then modul
    else
        let byOrigin = minted |> Seq.groupBy snd |> Map.ofSeq
        { modul with
            Bindings = bindings
            MutableArrayLets = Set.difference modul.MutableArrayLets (Set.ofSeq removedFullPools)
            Functions = modul.Functions |> List.collect (fun f ->
                f :: (Map.tryFind f.Id byOrigin |> Option.map (Seq.map fst >> List.ofSeq) |> Option.defaultValue []))
            DerivedFuncOrigins = minted |> Seq.fold (fun m (f, origin) -> Map.add f.Id origin m) modul.DerivedFuncOrigins }

// Existing callers have no complete foreign-binding/type census. C02 map
// admission stays as before; the new transpose arm must decline explicitly.
let fusePartialRowTerminalsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    fusePartialRowTerminalsWithCensusModule builder programFuncs None modul

/// C05's emitter-only certificate. The IR and the interpreter retain the
/// canonical gather; no planner table or shared stored-array rule is changed.
type SparseWildcardFoldPlan = {
    ParentId: IRId
    PinAxis: int
    Pin: int64
    Fold: IRCallable
    Seed: IRExpr
}

let certifySparseWildcardFold (wholeProgram: IRModule option) (binding: IRBinding)
                              (operand: IRExpr) (kernel: IRExpr) (seed: IRExpr option)
                              : Result<SparseWildcardFoldPlan, string> =
    let scalar64 = IRTScalar ETFloat64
    let literal = function
        | IRLit (IRLitFloat _) | IRUnaryOp (IRNeg, IRLit (IRLitFloat _)) -> true
        | _ -> false
    let plain (ix: IRIndexType) =
        ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone
        && ix.Kind = SDimension && ix.Dependencies.IsEmpty
    let candidate =
        match operand with
        | IndexOfNamed (_, ArrayElem at, [IRTuple _], _) ->
            at.IndexTypes.Length = 1 && at.IndexTypes.Head.IxKind = IxKSparse && at.IndexTypes.Head.Rank = 2
        | _ -> false
    match wholeProgram with
    | None -> Error "requires complete whole-program emission evidence"
    | Some _ when not (gateOn "BLADE_FUSION") -> Error "disabled by BLADE_FUSION"
    | Some _ when not (Blade.Toolchain.fpReproEnabled ()) -> Error "requires the reproducible serial graph"
    | Some _ when not candidate -> Error "not a direct rank-2 sparse partial operand"
    | Some m ->
        let funcs = m.Functions |> List.map (fun f -> f.Id, f) |> Map.ofList
        let resolve = resolveCallableIn funcs
        let writes = scheduleMayWriteIn m.Functions m
        let lifetime = scheduleHasLifetimeBarrierIn m.Functions m
        let isDeferredId = programDeferredId m m.Functions Seq.empty
        let same a b = obj.ReferenceEquals(a, b)
        let host = m.Bindings |> List.filter (fun b -> b.Id = binding.Id)
        let ordinary =
            match host with
            | [h] when h.Name = binding.Name && h.Type = scalar64 && binding.Type = scalar64
                       && same h.Value binding.Value ->
                match h.Value with
                | IRReduce (a, k, s) when same a operand && same k kernel && s = seed -> true
                | _ -> false
            | _ -> false
        let closedTotal root =
            let scalarType = function IRTScalar (ETFloat64 | ETInt64 | ETBool) -> true | _ -> false
            let rec audit visiting (f: IRCallable) =
                if Set.contains f.Id visiting then Error "a recursive fold/callee"
                elif f.IsStatic || f.IsArityPoly || f.IsOmpParallel || not f.Parallelism.IsEmpty
                     || f.IsCudaKernel || f.IsMpiParallel || not f.Captures.IsEmpty then
                    Error "a captured or strategized fold/callee"
                elif not (scalarType f.RetType) || (f.Params |> List.exists (fun p -> not (scalarType p.Type)))
                     || (f.Params |> List.mapi (fun i p -> p.Index <> i) |> List.exists id)
                     || (f.Params |> List.map _.VarId |> Set.ofList |> Set.count) <> f.Params.Length then
                    Error "a non-scalar or malformed fold/callee signature"
                elif writes f.Body || lifetime f.Body then Error "a writing/resource fold/callee"
                else
                    // the deferred-value predicate (gap G98); the closed-graph
                    // rule below refuses every non-callable free reference too
                    match IRTotality.auditCallable resolve (fun _ -> None) isDeferredId f [] with
                    | Error why -> Error why
                    | Ok () ->
                        let mutable bound = f.Params |> List.map _.VarId |> Set.ofList
                        iterIRExpr (function IRLet (id, _, _) -> bound <- bound.Add id | _ -> ()) f.Body
                        let refs = collectVarRefsIR f.Body |> Set.filter (fun id -> not (bound.Contains id)) |> Set.toList
                        let rec check = function
                            | [] -> Ok ()
                            | id :: rest ->
                                match Map.tryFind id funcs with
                                | None -> Error "an unknown/global/callback fold reference"
                                | Some callee ->
                                    match audit (visiting.Add f.Id) callee with
                                    | Error why -> Error why
                                    | Ok () -> check rest
                        check refs
            audit Set.empty root
        if not ordinary || (m.Bindings |> List.map _.Id |> Set.ofList |> Set.count) <> m.Bindings.Length
                        || funcs.Count <> m.Functions.Length then
            Error "requires the exact ordinary host and unique binding/callable identities"
        elif not m.ProviderReads.IsEmpty || not m.ProviderWrites.IsEmpty || not m.RandomInits.IsEmpty
             || not m.CompoundInits.IsEmpty || not m.MutableArrayLets.IsEmpty then
            Error "additional execution/ownership side payloads"
        else
        match operand, resolve kernel, seed with
        | IndexOfNamed (sid, sty, [IRTuple coords], _), Some fold, Some init when literal init ->
            let parent = m.Bindings |> List.tryFind (fun b -> b.Id = sid)
            let pin =
                match classifyCompoundIndexTuple 2 coords with
                | CompoundPartial ([(axis, IRLit (IRLitInt n))], [_]) when coords.Length = 2 && n >= 0L -> Some (axis, n)
                | _ -> None
            match parent, sty, pin, Map.tryFind sid m.SparseInits with
            | Some source, ArrayElem sparse, Some (axis, pinValue), Some (IRVar (vid, vty))
                when source.Type = sty && source.Value = IRLit IRLitUnit
                     && sparse.ElemType = scalar64 && not sparse.IsVirtual && sparse.IndexTypes.Length = 1
                     && m.SparseInits.Count = 1 ->
                let ix = sparse.IndexTypes.Head
                let values = m.Bindings |> List.tryFind (fun b -> b.Id = vid)
                let order = m.Bindings |> List.mapi (fun i b -> b.Id, i) |> Map.ofList
                let dominates =
                    match Map.tryFind vid order, Map.tryFind sid order, Map.tryFind binding.Id order with
                    | Some v, Some s, Some h -> v < s && s < h
                    | _ -> false
                let geometry =
                    match ix.Extent, values, vty with
                    | IRSparseKeys (SkStatic keys), Some vb, ArrayElem vt
                        when ix.Rank = 2 && ix.IxKind = IxKSparse && ix.Symmetry = SymNone
                             && ix.Tag = Some "__sparseidx" && ix.Kind = SDimension && ix.Dependencies.IsEmpty
                             && vb.Type = vty && vt.ElemType = scalar64 && not vt.IsVirtual && vt.IndexTypes.Length = 1
                             && plain vt.IndexTypes.Head && (keys |> List.forall (fun k -> k.Length = 2 && List.forall (fun n -> n >= 0L) k))
                             && (Set.ofList keys).Count = keys.Length ->
                        match vb.Value with
                        | IRArrayLit (cells, at) when at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1
                                                      && plain at.IndexTypes.Head && IRTotality.sameIndex at.IndexTypes.Head vt.IndexTypes.Head ->
                            cells.Length = keys.Length && List.forall literal cells
                            && IRTotality.evalInt vt.IndexTypes.Head.Extent = Some (int64 keys.Length)
                        | _ -> false
                    | _ -> false
                let signature =
                    fold.Params.Length = 2 && fold.RetType = scalar64
                    && (fold.Params |> List.mapi (fun i p -> p.Index = i && p.Type = scalar64) |> List.forall id)
                    && fold.Params.[0].VarId <> fold.Params.[1].VarId
                    && typeOf kernel = IRTArrow ([SVal scalar64; SVal scalar64], scalar64, None)
                let residualType =
                    match typeOf operand with
                    | ArrayElem at when at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1 ->
                        let r = at.IndexTypes.Head
                        plain r && r.Extent = IRCompoundProject (IRVar (sid, sty), 1)
                    | _ -> false
                let ids = Set.ofList [vid; sid; binding.Id]
                let planned id =
                    (HotRowTable.tryProducer id).IsSome || (HotRowTable.tryConsumer id).IsSome
                    || (CellFoldTable.tryLoop id).IsSome || (CellFoldTable.tryFoldLet id).IsSome
                    || (RecStepTable.tryLoop id).IsSome || (RecStepTable.tryStep id).IsSome
                    || (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome
                    || (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome
                    || (CompactionTable.tryFoldMap id).IsSome
                    || (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome
                let ownerConflict =
                    (ids |> Set.exists planned)
                    || (PoolReuseTable.donors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b))
                    || (PoolReuseTable.returnDonors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b))
                    || (MemberHoistTable.all () |> List.exists (fun p -> ids.Contains p.HostId || ids.Contains p.SetId || not (Set.intersect ids p.Kernels).IsEmpty))
                let demand e =
                    let mutable found = false
                    iterIRExpr (function
                        | IndexOfNamed (id, _, (IRTuple cs) :: _, _) when id = sid ->
                            match classifyCompoundIndexTuple 2 cs with CompoundPartial _ -> found <- true | _ -> ()
                        | IRCompoundProject (IRVar (id, _), _) when id = sid -> found <- true
                        | IRLet (_, IRVar (id, _), _) when id = sid -> found <- true
                        | IRVar (id, ty) when id = sid && ty <> sty -> found <- true
                        | _ -> ()) e
                    found
                let exprDemand e = demand e || (embeddedExtentExprs e |> List.exists demand)
                // The exact operand's complete type/coordinate metadata is
                // certified above. No other root gets this exemption.
                let residual = IRReduce (IRZero scalar64, kernel, seed)
                let extraDemand =
                    compactionRoots m.Functions m |> List.exists (function
                        | RootBindingValue b ->
                            exprDemand (if b.Id = binding.Id then residual else b.Value)
                            || (b.Id <> sid && b.Id <> binding.Id && (match b.Value with IRVar (id, _) -> id = sid | _ -> false))
                        | RootSideOperand (b, _, e) -> b.Id <> sid || exprDemand e
                        | RootFunctionBody f -> exprDemand f.Body || (f.Captures |> List.exists (fun c -> c.Id = sid || c.Id = vid))
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists exprDemand
                        | RootSourceRef (_, id) -> ids.Contains id
                        | RootNameRef _ -> true)
                let effect =
                    (m.Bindings |> List.exists (fun b -> bindingPayload m b |> List.exists (fun e -> writes e || lifetime e)))
                    || (m.Functions |> List.exists (fun f -> writes f.Body || lifetime f.Body))
                    || (compactionRoots m.Functions m |> List.exists (function
                        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists (fun e -> writes e || lifetime e)
                        | RootBindingValue b -> embeddedExtentExprs b.Value |> List.exists (fun e -> writes e || lifetime e)
                        | RootSideOperand (_, _, e) | RootFunctionBody { Body = e } ->
                            embeddedExtentExprs e |> List.exists (fun x -> writes x || lifetime x)
                        | _ -> false))
                if not dominates || not geometry || not signature || not residualType then Error "unsupported source/operand/fold geometry or provenance"
                elif ownerConflict then Error "overlapping storage/schedule owner"
                elif extraDemand then Error "additional residual/alias/capture/type/side-payload demand"
                elif effect then Error "a whole-program payload may write or cross a resource boundary"
                else
                    match closedTotal fold with
                    | Error why -> Error why
                    | Ok () -> Ok { ParentId = sid; PinAxis = axis; Pin = pinValue; Fold = fold; Seed = init }
            | _ -> Error "requires one known stored sparse constructor and a literal pin"
        | _ -> Error "requires a direct sparse partial operand, resolved fold and literal seed"

// ===========================================================================
// COMPUTE ELISION (BLADE_COMPUTE_ELIDE, plan rule `compute-elision`;
// docs/plans/plan-identity-transpose-compute.md section 6, the P3 entry).
//
// Inside a function, lambda or block body every computed let is STORED: the
// user's `|> compute` and S2's force (Lowering.forceCallableBody) both leave
// `let x = IRCompute (map)`, and a fold over `x` stays the ordinary `IRReduce`
// over the pool (tryInferReduceCompute's `alreadyMaterializedLet` declines the
// splice there, because the let is built anyway). The same two lines at top
// level without `|> compute` fuse with no pool. When `x` has exactly ONE
// reader and that reader consumes it cell by cell, the pool buys nothing:
//
//   FOLD  `reduce(x, k[, s])`, the WHOLE value of a statement on x's own
//         spine (a later let, or the spine's tail) -> the fused terminal
//         `IRReduceCompute (map, k, seed)` -- exactly the node the checker
//         builds for the written-out `reduce(method_for(a) <@> f, k)`, which
//         both emitters already take inside a body -- and x's let is dropped.
//   MAP   a pointwise map / co-iteration `IRCompute (method_for(zip(.., x, ..))
//         <@> g)`, the whole value of such a statement, whose kernel reads x's
//         cell exactly once -> x's map becomes that operand in place and the
//         let is dropped; elementwise fusion, which runs NEXT, is the
//         realization (one nest, no pool for x). Recorded after fusion ran,
//         saying whether it spliced.
//
// The commitments `compute` makes (plan section 4), and how each is kept:
//   * SNAPSHOT. The consumer reads the producer's inputs where IT runs, so
//     nothing between the let and the consumer -- each intervening statement
//     and the consumer statement itself (fold kernel, seed, host kernel), and
//     the extents their types carry (`0..bump(a)`) -- may write: the shared
//     fail-closed analysis (`scheduleMayWriteIn`, which
//     charges a write through any name not bound inside the statement as
//     observable) plus provider effects.
//   * FAILURE SITE AND ORDER. Chosen rule: the producer kernel passes the
//     closed totality whitelist (`IRTotality.auditApply`), and a co-iteration
//     producer has literal, equal operand extents (its agreement check is the
//     apply's one other failure), and every extent the producer's types carry
//     is a literal, a name, an extent read or `+ - *` of those (a range bound
//     `0..(10 / z)` declines). A total producer has nothing to fail, so
//     moving it past output, writes-free statements or another failure is
//     unobservable. (The alternative, "no observable event in between", was
//     NOT taken.)
//   * ONCE-ONLY EFFECTS. A producer that may write declines (G45).
//   * EVALUATION COUNT. One reader, consuming each cell exactly once. A second
//     reader, a capture, an assignment, a keyed read / view, an argument, a
//     tuple member, a return or alias of x, a read inside a larger expression
//     or a nested block, the fold's own seed or kernel, an extent a type
//     carries (`0..extents(x)`): all decline. Readers are counted per REGION
//     (a shape specialization copies a body with its let ids).
//   * EMPTY INPUT AND THE SEED. A seeded fold returns its seed either way.
//     An UNSEEDED builtin `(+)` / `(*)` fold is admitted only over a LITERAL
//     extent >= 1, so an empty (or run-time) input keeps the stored fold and
//     its BL8003 at the fold; its seed follows W4-3's route rule
//     (Lowering.lowerUnseededSeed, G74), the seed the written-out fused fold
//     gets: on a STRICT route (`BLADE_FP_REPRO=1`, a `where repro` region or
//     fold kernel) the exact identity (`sfIdentitySeed`: -0.0 / 1.0, 0 / 1 --
//     bitwise the stored, head-seeded chain); on a licensed route the BASE
//     seed (+0.0 for a float `+`; the licence owns the signed zero, G40).
//
// A tree rewrite BEFORE the lane split, like elementwise fusion (charter rule
// 2): the interpreter runs the rewritten tree too, so `test interp` cannot see
// a miscompile here -- `blade test opt-diff` (gate off vs on) is the gate.
// A producer whose operand is itself an elided computation is admitted ONE
// level deeper (`x0 -> x1 -> fold`) when that inner map passes the producer
// admission (`innerProducerOk`); a deeper chain declines. Top level: a
// top-level binding's BLOCK is in; the binding list itself is run as one
// spine only in a single-module program under a print selection
// (`BLADE_PRINT` / `--print`), for bindings the selection EXCLUDES (without
// a selection every top-level binding prints, so it is materialized anyway).
// A module that holds a CUDA kernel is skipped whole (device bodies).
// ===========================================================================
