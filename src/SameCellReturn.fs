/// PM03's narrow same-cell RETURN certificate (round 4 A04 in the LLVM lane,
/// A06 in the C++ lane), ONE semantic proof shared by both realizations:
///
///   function f(x: Array<Float64 like I>) -> Array<Float64 like I> = {
///       let p = <closed scalar map of x> |> compute
///       let s = reduce(p, k)          // >= 1 completed scalar barrier over p
///       let b = <closed scalar arithmetic of snapshots>
///       <closed scalar map of p, snapshots>   // the return, same cell
///   }
///
/// may fill the return in p's OWN pool -- each cell's old value loaded into a
/// scalar before the same-index store -- instead of allocating a second pool
/// and freeing p. The certificate is immutable and attached to the exact
/// original callable/body; each lane re-checks its own ACTUAL allocation frame
/// (LLVM: one pool scope with one tracking slot; C++: one SFunc frame with one
/// PoolAlloc) before any tail emission, and falls back to the ordinary fresh
/// return once, with unchanged evaluation, when either check fails. It is NOT
/// a dead-pool `PoolReuseTable.ReturnDonors` entry: the C++ generic consumer
/// assumes distinct restricted input / output paths.
///
/// Gate: BLADE_POOL_REUSE (an `optimizerGates` member; `0`/`off` = the fresh
/// return in both lanes, byte for byte). Extracted from EmitLlvm (A04) with
/// its admission unchanged; placed before both backends.
module Blade.SameCellReturn

open Blade.Types
open Blade.IR

type Certificate =
    { Function: IRId
      Donor: IRId
      Body: IRExpr
      Tail: ApplyInfo
      Extent: int64
      Snapshots: Set<IRId> }

/// RECURSION IS A CYCLE, NOT A SELF-CALL: can `id` reach itself through the
/// call edges of `callables` (alias keys resolved to their callable's id)?
let reachesSelf (callables: Map<IRId, IRCallable>) (id: IRId) : bool =
    let callees (cl: IRCallable) =
        let outs = System.Collections.Generic.HashSet<IRId>()
        iterIRExpr
            (fun n ->
                match n with
                | IRApp (IRVar (fid, _), _, _) ->
                    (match Map.tryFind fid callables with
                     | Some target -> outs.Add target.Id |> ignore
                     | None -> ())
                | _ -> ())
            cl.Body
        outs
    let byId = callables |> Map.toSeq |> Seq.map snd |> Seq.distinctBy (_.Id) |> Seq.map (fun c -> c.Id, c) |> Map.ofSeq
    let seen = System.Collections.Generic.HashSet<IRId>()
    let work = System.Collections.Generic.Stack<IRId>()
    (match Map.tryFind id byId with
     | Some cl -> for t in callees cl do work.Push t
     | None -> ())
    let mutable found = false
    while not found && work.Count > 0 do
        let n = work.Pop()
        if n = id then found <- true
        elif seen.Add n then
            match Map.tryFind n byId with
            | Some cl -> for t in callees cl do work.Push t
            | None -> ()
    found

/// The certificate over the ORIGINAL callable `cl`, with `callables` the
/// module's callables table (alias keys included) and `selfRecursive` the
/// lane's recursion fact. Error = the first failed condition.
let certify (callables: Map<IRId, IRCallable>) (selfRecursive: IRId -> bool) (cl: IRCallable) : Result<Certificate, string> =
    let plain ty =
        match ty with
        | ArrayElem at when not at.IsVirtual && at.Identity.IsNone && at.ElemType = IRTScalar ETFloat64 ->
            match at.IndexTypes with
            | [ix] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 &&
                        ix.Kind = SDimension && ix.Dependencies.IsEmpty ->
                match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some ix | _ -> None
            | _ -> None
        | _ -> None
    let rec spine e =
        match e with
        | IRLet (id, v, body) ->
            let ls, tail = spine body
            (id, v) :: ls, tail
        | tail -> [], tail
    let lets, tail = spine cl.Body
    let mutable blocked = None
    let require ok why = if not ok && blocked.IsNone then blocked <- Some why
    let ordinary (k: IRCallable) =
        not k.IsStatic && not k.IsOmpParallel && k.Parallelism.IsEmpty && not k.IsCudaKernel && not k.IsMpiParallel &&
        not k.IsArityPoly && k.ArityParam.IsNone && k.AntisymGroups.IsEmpty
    let rec scalar (vars: Set<IRId>) e =
        match e with
        | IRLit (IRLitFloat _) -> true
        | IRVar (id, IRTScalar ETFloat64) -> Set.contains id vars
        | IRBinOp (IRElementwise, (IRAdd | IRSub | IRMul), l, r, _) -> scalar vars l && scalar vars r
        | _ -> false
    // Unknown effect summaries on synthesized lambdas are discharged by this
    // closed syntax, never by assuming their summary means pure/total.
    let kernel arity snapshots expr =
        match expr with
        | IRVar (kid, _) when callables.ContainsKey kid ->
            let k = callables.[kid]
            let vars = Set.ofList ((k.Params |> List.map (_.VarId)) @ (k.Captures |> List.map (_.Id)))
            ordinary k && typeOf expr = IRTArrow (List.replicate arity (SVal (IRTScalar ETFloat64)), IRTScalar ETFloat64, None) &&
            k.Params.Length = arity && k.RetType = IRTScalar ETFloat64 &&
            (k.Params |> List.mapi (fun i p -> p.Index = i) |> List.forall id) &&
            vars.Count = k.Params.Length + k.Captures.Length &&
            (k.Params |> List.forall (fun p -> p.Type = IRTScalar ETFloat64)) &&
            (k.Captures |> List.forall (fun cap -> cap.Type = IRTScalar ETFloat64 && Set.contains cap.Id snapshots)) &&
            scalar vars k.Body
        | _ -> false
    let shape = plain cl.RetType
    let sameShape ty =
        match shape, plain ty with Some a, Some b -> Blade.IRTotality.sameIndex a b | _ -> false
    let map (info: ApplyInfo) =
        sameShape info.OutputType &&
        info.KernelInputRanks = [0] && info.KernelOutputRank = 0 && info.SDimsPerArray = [1] &&
        info.KernelTDims.IsEmpty && info.SharedIndexTypes.IsEmpty && info.TriangularLevels = [false] &&
        info.SymcomStates = [SCNeither] && not info.IsCoIteration && not info.HasReynolds &&
        info.SpeedupFactor = 1L && info.ReynoldsSpeedup = 1L && info.Identities.Length = 1 &&
        (match info.ArrayTypes with [a] -> sameShape (mkArrayLike a) | _ -> false) &&
        (match info.Loop with
         | IRMethodFor loop ->
             loop.Arrays = info.Arrays && loop.ArrayTypes = info.ArrayTypes && loop.Identities = info.Identities &&
             loop.SDimsPerArray = [1] && loop.TotalSDims = 1 && loop.SharedIndexTypes.IsEmpty
         | _ -> false)
    require (Blade.Optimize.poolReuseEnabled ()) "disabled by BLADE_POOL_REUSE"
    require (ordinary cl && cl.Captures.IsEmpty && not (selfRecursive cl.Id))
            "not an ordinary capture-free nonrecursive serial callable"
    require ((cl.Params |> List.mapi (fun i p -> p.Index = i) |> List.forall id) &&
             ((cl.Params |> List.map (_.VarId)) @ (lets |> List.map fst) |> Set.ofList |> Set.count) = cl.Params.Length + lets.Length)
            "parameter/local identities or parameter positions are not unique"
    require (not (containsPoolLifetimeEvent cl.Body)) "inline lifetime region remains excluded"
    require shape.IsSome "return is not unitless static plain rank-one Float64 storage"
    require (cl.Params |> List.forall (fun p -> p.Type = IRTScalar ETFloat64 || sameShape p.Type))
            "parameter type/index provenance does not match the return"
    let ids =
        Set.union (collectVarRefsIR cl.Body)
            (Set.ofList (cl.Id :: ((lets |> List.map fst) @ (cl.Params |> List.map (_.VarId)))))
    let owns id =
        (Blade.Optimize.HotRowTable.tryProducer id).IsSome || (Blade.Optimize.HotRowTable.tryConsumer id).IsSome ||
        (Blade.Optimize.CellFoldTable.tryLoop id).IsSome || (Blade.Optimize.CellFoldTable.tryFoldLet id).IsSome ||
        (Blade.Optimize.RecStepTable.tryLoop id).IsSome || (Blade.Optimize.RecStepTable.tryStep id).IsSome ||
        (Blade.Optimize.EpilogueLegTable.tryProducer id).IsSome || (Blade.Optimize.EpilogueLegTable.tryJoin id).IsSome ||
        (Blade.Optimize.CompactionTable.tryMask id).IsSome || (Blade.Optimize.CompactionTable.tryCompound id).IsSome ||
        (Blade.Optimize.CompactionTable.tryFoldMap id).IsSome || not (Blade.Optimize.CompactionTable.foldsOf id).IsEmpty ||
        (Blade.Optimize.CompactRowTable.tryProducer id).IsSome || (Blade.Optimize.CompactRowTable.tryConsumer id).IsSome ||
        not (Blade.Optimize.MemberHoistTable.plansOf id).IsEmpty
    let generic =
        (Blade.Types.PoolReuseTable.tryReturnDonor cl.Id).IsSome ||
        (Blade.Types.PoolReuseTable.currentReturnDonor ()).IsSome ||
        (Blade.Types.PoolReuseTable.returnDonors () |> Map.exists (fun id donor -> ids.Contains id || ids.Contains donor)) ||
        (Blade.Types.PoolReuseTable.donors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b))
    let memberOwner = Blade.Optimize.MemberHoistTable.all () |> List.exists (fun p -> ids.Contains p.HostId || ids.Contains p.SetId || not (Set.intersect ids p.Kernels).IsEmpty)
    require (not generic && not memberOwner && not (ids |> Set.exists owns) &&
             not (ids |> Set.exists (fun a -> ids |> Set.exists (fun b -> (Blade.Optimize.OrbitPullTable.tryAudit a b).IsSome))))
            "another planner owns this body"
    let paramScalars = cl.Params |> List.filter (fun p -> p.Type = IRTScalar ETFloat64) |> List.map (_.VarId) |> Set.ofList
    let mutable snapshots = paramScalars
    let mutable barriers = 0
    let mutable donor = None
    let rec typeReads (id: IRId) (ty: IRType) : bool =
        let indices (xs: IRIndexType list) = xs |> List.exists (fun ix -> List.contains id ix.Dependencies || exprReads id ix.Extent)
        match ty with
        | IRTArrow (slots, ret, _) -> typeReads id ret || (slots |> List.exists (function SVal t -> typeReads id t | SIdx ix | SIdxVirt ix -> indices [ix]))
        | IRTTuple ts -> ts |> List.exists (typeReads id)
        | IRTLoop lt -> (lt.ArrayTypes @ Option.toList lt.KernelType) |> List.exists (typeReads id)
        | IRTComputation t | IRTPoly (t, _) | IRTUnitAnnotated (t, _) -> typeReads id t
        | IRTIdxTagged (t, IRefAnon (_, extent)) -> typeReads id t || exprReads id extent
        | IRTIdxTagged (t, _) -> typeReads id t
        | IRTDist (_, t, axes) -> typeReads id t || indices axes
        | IRTGroupKeys (outer, source, _) -> indices [outer; source]
        | IRTScalar _ | IRTUnit | IRTNat _ | IRTNamed _ | IRTInfer _ -> false
    and exprReads (id: IRId) (expr: IRExpr) : bool =
        Set.contains id (collectVarRefsIR expr) || (Blade.IRValidate.collectTypesInExpr expr |> List.exists (typeReads id))
    match lets with
    | (id, IRCompute (IRApplyCombinator producer)) :: scalars ->
        donor <- Some id
        // Even a callable not named in this body's expression tree must not
        // retain p through its capture list or embedded type dependencies.
        let retained =
            callables.Values |> Seq.exists (fun k ->
                (k.Captures |> List.exists (fun cap -> cap.Id = id || typeReads id cap.Type)) ||
                typeReads id k.RetType || (k.Params |> List.exists (fun p -> typeReads id p.Type)))
        require (not retained) "donor is retained by a callable capture/type payload"
        require (map producer && kernel 1 snapshots producer.Kernel) "donor producer is not a closed total plain scalar map"
        require (match producer.Arrays with
                 | [IRVar (input, ty)] -> sameShape ty && (cl.Params |> List.exists (fun p -> p.VarId = input && p.Type = ty))
                 | _ -> false) "donor is not a fresh map of an original formal array"
        for sid, value in scalars do
            let sum =
                match value with
                | IRReduce (IRVar (input, ty), fold, None) when input = id && sameShape ty ->
                    // Positive literal extent discharges unseeded-empty failure;
                    // preserve the existing fold order and exact kernel body.
                    kernel 2 snapshots fold
                | _ -> false
            require (typeOf value = IRTScalar ETFloat64 && (sum || scalar snapshots value))
                    "body retains an array/alias/deferred read or has an unsupported scalar observation"
            if sum then barriers <- barriers + 1
            snapshots <- Set.add sid snapshots
        require (barriers > 0) "no completed scalar barrier over the donor"
    | _ -> require false "donor is not one direct fresh computed local map"
    let info = match tail with IRCompute (IRApplyCombinator info) -> Some info | _ -> None
    match info, donor with
    | Some info, Some id ->
        require (map info && kernel 1 snapshots info.Kernel) "return kernel/metadata is not closed total same-cell scalar arithmetic"
        require (match info.Arrays with [IRVar (input, ty)] -> input = id && sameShape ty | _ -> false)
                "return does not read exactly the named donor cell"
    | _ -> require false "return is not one computed same-cell scalar map"
    match blocked, donor, info, shape with
    | None, Some id, Some info, Some ix ->
        Ok { Function = cl.Id; Donor = id; Body = cl.Body; Tail = info; Extent = (Blade.IRTotality.evalInt ix.Extent).Value; Snapshots = snapshots }
    | Some why, _, _, _ -> Error why
    | _ -> Error "incomplete same-cell return certificate"
