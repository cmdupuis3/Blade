// One final-IR storage certificate, not a general external-memory planner.
module Blade.OptimizePrivateSpool

open Blade.Types
open Blade.IR
open Blade.Optimize

type Plan = {
    Module: IRModule
    Source: IRBinding
    Producer: IRBinding
    Statistic: IRBinding
    Barrier: IRBinding
    Terminal: IRBinding
    SourceSpec: ProviderReadSpec
    ProducerKernel: IRCallable
    TransformKernel: IRCallable
    Kernels: IRCallable list
    FirstSeed: IRExpr
    SecondSeed: IRExpr
    Cells: int64
    Policy: PrivateSpoolPolicy
}

let private scalar64 ty = ty = IRTScalar ETFloat64
let private literal64 = function
    | IRLit (IRLitFloat _) | IRUnaryOp (IRNeg, IRLit (IRLitFloat _)) -> true
    | _ -> false

/// Deliberately smaller than IRTotality: no calls, effects, reads, casts or
/// implicit prelude. Unknown effect summaries and mutable capture *flags* are
/// not evidence of mutation; the actual closed bodies and all assignments are.
let rec private scalarBody (ids: Set<IRId>) = function
    | IRLit (IRLitFloat _) -> true
    | IRVar (id, ty) -> ids.Contains id && scalar64 ty
    | IRBinOp (IRElementwise, (IRAdd | IRSub | IRMul | IRDiv), a, b, _) -> scalarBody ids a && scalarBody ids b
    | IRUnaryOp (IRNeg, a) -> scalarBody ids a
    | _ -> false

let private flags (f: IRCallable) =
    not f.IsStatic && not f.IsArityPoly && f.ArityParam.IsNone && f.Parallelism.IsEmpty
    && not f.IsOmpParallel && not f.IsCudaKernel && not f.IsMpiParallel && f.AntisymGroups.IsEmpty
    && scalar64 f.RetType && (f.Params |> List.forall (fun p -> scalar64 p.Type))

let ownerConflict (ids: Set<IRId>) =
    (ids |> Set.exists (fun id ->
        (HotRowTable.tryProducer id).IsSome
        || (HotRowTable.tryConsumer id).IsSome || (CellFoldTable.tryLoop id).IsSome
        || (CellFoldTable.tryFoldLet id).IsSome || (RecStepTable.tryLoop id).IsSome
        || (RecStepTable.tryStep id).IsSome || (EpilogueLegTable.tryProducer id).IsSome
        || (EpilogueLegTable.tryJoin id).IsSome || not (MemberHoistTable.plansOf id).IsEmpty
        || (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome
        || (CompactionTable.tryFoldMap id).IsSome || not (CompactionTable.foldsOf id).IsEmpty
        || (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome
        || (PoolReuseTable.tryReturnDonor id).IsSome
        || (ids |> Set.exists (fun source -> (OrbitPullTable.tryAudit id source).IsSome))))
    || (PoolReuseTable.donors () |> Map.exists (fun k v -> ids.Contains k || ids.Contains v))
    || (PoolReuseTable.returnDonors () |> Map.exists (fun k v -> ids.Contains k || ids.Contains v))
    || (PoolReuseTable.currentReturnDonor () |> Option.exists ids.Contains)
    || (MemberHoistTable.all () |> List.exists (fun p ->
            ids.Contains p.HostId || ids.Contains p.SetId || not (Set.intersect ids p.Kernels).IsEmpty))

let regionIds (p: Plan) = Set.ofList [p.Source.Id; p.Producer.Id; p.Statistic.Id; p.Barrier.Id; p.Terminal.Id]

let private refs e =
    Set.union (collectVarRefsIR e)
        (embeddedExtentExprs e |> List.map collectVarRefsIR |> Set.unionMany)

let private typeRefs ty = irTypeExtentExprs ty |> List.map refs |> Set.unionMany

let private noEscapes (p: Plan) =
    let region = regionIds p
    // v1 publishes only the selected final print: even a terminal consumer in
    // another binding/callable/type would introduce hoist/lifetime obligations.
    let privateIds = region |> Set.union (p.Kernels |> List.map _.Id |> Set.ofList)
    let selectedFunctions = p.Kernels |> List.map _.Id |> Set.ofList
    let privateNames =
        p.Module.Bindings |> List.filter (fun b -> privateIds.Contains b.Id) |> List.map _.Name |> Set.ofList
    let touches ids = not (Set.intersect privateIds ids).IsEmpty
    compactionRoots p.Module.Functions p.Module |> List.forall (function
        | RootBindingValue b -> region.Contains b.Id || not (touches (refs b.Value))
        | RootSideOperand (_, _, e) -> not (touches (refs e))
        | RootFunctionBody f ->
            selectedFunctions.Contains f.Id
            || (not (touches (refs f.Body)) && not (f.Captures |> List.exists (fun c -> privateIds.Contains c.Id)))
        | RootType (_, ty) -> not (touches (typeRefs ty))
        | RootSourceRef (_, id) -> not (privateIds.Contains id)
        | RootNameRef (_, name) -> not (privateNames.Contains name))

let private hasAssignmentOrLifetime (m: IRModule) =
    let bad e =
        let mutable found = false
        iterIRExpr (function IRAssign _ | IRBeginPoolScope _ | IRReleasePools _ -> found <- true | _ -> ()) e
        found
    compactionRoots m.Functions m |> List.exists (function
        | RootBindingValue b -> bad b.Value
        | RootSideOperand (_, _, e) -> bad e
        | RootFunctionBody f -> bad f.Body
        | RootType (_, ty) -> irTypeExtentExprs ty |> List.exists bad
        | _ -> false)

let private plain n (at: IRArrayType) =
    not at.IsVirtual && scalar64 at.ElemType
    && (match at.IndexTypes with
        | [ix] -> ix.Id >= 0 && ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain && ix.Kind = SDimension
                  && ix.Extent = IRLit (IRLitInt n) && ix.Dependencies.IsEmpty
        | _ -> false)

let private unaryMap n (source: IRBinding) (info: ApplyInfo) =
    (match source.Type, info.Arrays, info.ArrayTypes, info.OutputType with
        | ArrayElem input, [IRVar (id, ty)], [at], ArrayElem output ->
            id = source.Id && ty = source.Type && at = input
            && plain n input && plain n output && output.Identity.IsNone
            && info.Identities = [AIDVariable source.Name]
            // deduceOutputType's size-one group copies the input index
            // verbatim and refreshes only Id. Each role keeps exact type
            // provenance; the scalar unary map gives ordinal correspondence,
            // not anonymous sameIndex equality across its fresh output.
            && output.IndexTypes.Head.Id <> input.IndexTypes.Head.Id
            && output.IndexTypes.Head = {input.IndexTypes.Head with Id=output.IndexTypes.Head.Id}
        | _ -> false)
    && info.KernelInputRanks = [0] && info.KernelOutputRank = 0
    && info.SDimsPerArray = [1] && info.KernelTDims.IsEmpty && info.SharedIndexTypes.IsEmpty
    && not info.IsCoIteration && not info.HasReynolds && info.SpeedupFactor = 1L && info.ReynoldsSpeedup = 1L
    && info.SymcomStates = [SCNeither] && info.TriangularLevels = [false] && info.Identities.Length = 1
    && (match info.Loop with
        | IRMethodFor loop ->
            loop.Arrays = info.Arrays && loop.ArrayTypes = info.ArrayTypes && loop.Identities = info.Identities
            && loop.SDimsPerArray = [1] && loop.TotalSDims = 1 && loop.SharedIndexTypes.IsEmpty
        | _ -> false)

/// Whole-program proof: v1 requires exactly one module, so foreign-module
/// consumers cannot be hidden behind a per-module demand census. No ID/order
/// assumption; candidates are adjacent entries in the actual binding list.
let tryPlan (policy: PrivateSpoolPolicy) (printDemand: Set<string> option) (program: IRProgram)
            : Result<Plan, string> =
    let fail why = Error why
    match program.Modules with
    | [m] when m.ProviderReads.Count = 1 && m.ProviderWrites.IsEmpty && m.RandomInits.IsEmpty
               && m.CompoundInits.IsEmpty && m.SparseInits.IsEmpty && m.MutableArrayLets.IsEmpty ->
        let funcs = m.Functions |> List.map (fun f -> f.Id, f) |> Map.ofList
        let resolve = function
            | IRVar (id, FuncElem (args, ret)) ->
                Map.tryFind id funcs |> Option.filter (fun f -> args = (f.Params |> List.map _.Type) && ret = f.RetType)
            | _ -> None
        let addKernel e =
            resolve e |> Option.filter (fun f ->
                flags f && f.Captures.IsEmpty && f.Params.Length = 2
                && (match f.Body with
                    | IRBinOp (IRElementwise, IRAdd, IRVar (a, at), IRVar (b, bt), _) ->
                        a = f.Params.[0].VarId && b = f.Params.[1].VarId && scalar64 at && scalar64 bt
                        && a <> b && f.Params.[0].Index = 0 && f.Params.[1].Index = 1
                    | _ -> false))
        let candidates =
            m.Bindings |> List.windowed 4 |> List.choose (function
                | [p;t;b;r] ->
                    match p.Value, t.Value, b.Value, r.Value with
                    | IRCompute (IRApplyCombinator pi), IRReduce (IRVar (pid, pt), fk, Some fs),
                      IRBinOp (IRElementwise, IRDiv, IRVar (tid, tt), IRUnaryOp (IRCast (ETFloat64, _), IRExtent (IRVar (ep, ept), 0)), _),
                      IRReduceCompute (IRApplyCombinator ri, sk, ss)
                        when pid = p.Id && tid = t.Id && ep = p.Id && literal64 fs && literal64 ss
                             && scalar64 t.Type && scalar64 tt && scalar64 b.Type && scalar64 r.Type ->
                        match pi.Arrays, resolve pi.Kernel, resolve ri.Kernel, addKernel fk, addKernel sk with
                        | [IRVar (sid, _)], Some pk, Some rk, Some firstFold, Some secondFold ->
                            match Map.tryFind sid m.ProviderReads, m.Bindings |> List.tryFindIndex (fun x -> x.Id = sid),
                                  m.Bindings |> List.tryFindIndex (fun x -> x.Id = p.Id) with
                            | Some spec, Some si, Some start when si < start ->
                                match spec.VarType.IndexTypes with
                                | [ix] ->
                                    match ix.Extent with
                                    | IRLit (IRLitInt n) when n >= 0L && plain n spec.VarType
                                                                         && unaryMap n m.Bindings.[si] pi && unaryMap n p ri
                                                                         && p.Type = pi.OutputType && pt = p.Type && ept = p.Type
                                                                         && m.Bindings.[si].Type = mkArrayLike spec.VarType
                                                                         && m.Bindings.[si].Value = IRLit IRLitUnit
                                                                         && flags pk && flags rk && pk.Params.Length = 1 && rk.Params.Length = 1
                                                                         && pk.Captures.IsEmpty && rk.Captures.Length = 1
                                                                         && rk.Captures.Head.Id = b.Id && scalar64 rk.Captures.Head.Type
                                                                         && pk.CommGroups.IsEmpty && rk.CommGroups.IsEmpty
                                                                         && not pk.IsCommutative && not rk.IsCommutative
                                                                         && pk.Params.Head.Index = 0 && rk.Params.Head.Index = 0
                                                                         && scalarBody (Set.singleton pk.Params.Head.VarId) pk.Body
                                                                         && scalarBody (Set.ofList [rk.Params.Head.VarId;b.Id]) rk.Body ->
                                        Some {
                                            Module = m
                                            Source = m.Bindings.[si]
                                            Producer = p
                                            Statistic = t
                                            Barrier = b
                                            Terminal = r
                                            SourceSpec = spec
                                            ProducerKernel = pk
                                            TransformKernel = rk
                                            Kernels = [pk;rk;firstFold;secondFold]
                                            FirstSeed = fs
                                            SecondSeed = ss
                                            Cells = n
                                            Policy = policy
                                        }
                                    | _ -> None
                                | _ -> None
                            | _ -> None
                        | _ -> None
                    | _ -> None
                | _ -> None)
        match candidates with
        | [p] when printDemand = Some (Set.singleton p.Terminal.Name) ->
            let ids = Set.union (regionIds p) (p.Kernels |> List.map _.Id |> Set.ofList)
            let allParams = p.Kernels |> List.collect _.Params |> List.map _.VarId
            let declaredIds = m.Bindings |> List.map _.Id |> Set.ofList
            if allParams |> List.exists declaredIds.Contains then fail "callable parameter collides with a module binding"
            elif hasAssignmentOrLifetime m then fail "assignment or explicit pool lifetime exists in the whole module"
            elif not (noEscapes p) then fail "private source/statistic/producer escapes through a value, capture, type, extent or side payload"
            elif ownerConflict ids then fail "an existing storage/schedule owner overlaps the complete region"
            else Ok p
        | [_] -> fail "the entire private producer/statistic region requires print selection of only its terminal"
        | _ -> fail "requires exactly one adjacent rank-one Float64 producer, seeded sum, extent barrier and seeded transformed sum"
    | _ -> fail "requires a single whole-program module and one ordinary provider source without hidden initialization/write payloads"

/// The emitter carries the exact immutable module/callable objects. A changed
/// graph cannot inherit authority merely because it reused the same IDs.
let stillValid (p: Plan) (m: IRModule) =
    obj.ReferenceEquals(p.Module, m)
    && (p.Kernels |> List.forall (fun f -> m.Functions |> List.exists (fun g -> obj.ReferenceEquals(f,g))))
    && not (ownerConflict (Set.union (regionIds p) (p.Kernels |> List.map _.Id |> Set.ofList)))

/// CodeGen's derived-origin order can differ from Bindings. Recheck its actual
/// binding sequence by object identity before granting omission authority.
let emissionOrderMatches (p: Plan) (bindings: IRBinding list) =
    match bindings |> List.tryFindIndex (fun b -> obj.ReferenceEquals(b,p.Source)),
          bindings |> List.tryFindIndex (fun b -> obj.ReferenceEquals(b,p.Producer)) with
    | Some source, Some start when source < start ->
        let region = bindings |> List.skip start |> List.truncate 4
        region.Length = 4
        && List.forall2 (fun a b -> obj.ReferenceEquals(a,b)) region [p.Producer;p.Statistic;p.Barrier;p.Terminal]
    | _ -> false
