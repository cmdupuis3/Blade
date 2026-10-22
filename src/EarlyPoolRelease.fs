/// One PM02 schedule: dispose a fresh local after closed scalar snapshots,
/// before an independent different-extent ordinary array return is allocated.
/// No side table or renderer marker is an ownership certificate. The complete
/// original prefix is retained and contains exactly one possible pool allocation.
module Blade.EarlyPoolRelease

open Blade.Types
open Blade.IR

let private plain (ty: IRType) : IRIndexType option =
    match ty with
    | ArrayElem at when at.ElemType = IRTScalar ETFloat64 && not at.IsVirtual && at.Identity.IsNone ->
        match at.IndexTypes with
        | [ix] when ix.Rank = 1 && ix.Kind = SDimension && ix.IxKind = IxKPlain &&
                    ix.Symmetry = SymNone && ix.Dependencies.IsEmpty ->
            match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some ix | _ -> None
        | _ -> None
    | _ -> None

let private sameShape (a: IRType) (b: IRType) : bool =
    match plain a, plain b with
    | Some x, Some y -> Blade.IRTotality.sameIndex x y
    | _ -> false

let private serial (f: IRCallable) : bool =
    not f.IsStatic && not f.IsOmpParallel && f.Parallelism.IsEmpty && not f.IsCudaKernel && not f.IsMpiParallel &&
    not f.IsArityPoly && f.ArityParam.IsNone && f.AntisymGroups.IsEmpty

let private scalar (known: Set<IRId>) (expr: IRExpr) : bool =
    let rec go e =
        match e with
        | IRLit (IRLitFloat _) -> true
        | IRVar (id, IRTScalar ETFloat64) -> known.Contains id
        | IRBinOp (IRElementwise, (IRAdd | IRSub | IRMul), l, r, _) -> go l && go r
        | _ -> false
    go expr

// Include index Dependencies as well as embedded expressions. Plain accepted
// types cannot contain these, but the *external* retention census can.
let rec private typeReads (id: IRId) (ty: IRType) : bool =
    let indices (xs: IRIndexType list) =
        xs |> List.exists (fun ix -> List.contains id ix.Dependencies || exprReads id ix.Extent)
    match ty with
    | IRTArrow (slots, ret, _) ->
        typeReads id ret || (slots |> List.exists (function SVal t -> typeReads id t | SIdx ix | SIdxVirt ix -> indices [ix]))
    | IRTTuple ts -> ts |> List.exists (typeReads id)
    | IRTLoop lt -> (lt.ArrayTypes @ Option.toList lt.KernelType) |> List.exists (typeReads id)
    | IRTComputation t | IRTPoly (t, _) | IRTUnitAnnotated (t, _) -> typeReads id t
    | IRTIdxTagged (t, IRefAnon (_, extent)) -> typeReads id t || exprReads id extent
    | IRTIdxTagged (t, _) -> typeReads id t
    | IRTDist (_, t, axes) -> typeReads id t || indices axes
    | IRTGroupKeys (a, b, _) -> indices [a; b]
    | IRTScalar _ | IRTUnit | IRTNat _ | IRTNamed _ | IRTInfer _ -> false
and private exprReads (id: IRId) (expr: IRExpr) : bool =
    (collectVarRefsIR expr).Contains id ||
    (Blade.IRValidate.collectTypesInExpr expr |> List.exists (typeReads id)) ||
    (Blade.Optimize.embeddedExtentExprs expr |> List.exists (fun e -> (collectVarRefsIR e).Contains id)) ||
    // collectTypesInExpr omits zero/uninit, apply operand records and some
    // structural axes. Check their Dependencies as well as extent values.
    (let mutable found = false
     let types (xs: IRType list) = if xs |> List.exists (typeReads id) then found <- true
     let indices (xs: IRIndexType list) =
         if xs |> List.exists (fun ix -> List.contains id ix.Dependencies || exprReads id ix.Extent) then found <- true
     iterIRExpr (function
        | IRZero ty | IRUninit ty -> types [ty]
        | IRArrayLit (_, at) -> types [mkArrayLike at]
        | IRApplyCombinator info ->
            types (info.OutputType :: (info.ArrayTypes |> List.map mkArrayLike))
            indices (info.SharedIndexTypes @ info.KernelTDims)
        | IRMethodFor info ->
            types (info.ArrayTypes |> List.map mkArrayLike)
            indices info.SharedIndexTypes
        | IRRange (xs, _) | IRUngroupGrid (_, xs, _) -> indices xs
        | IRVirtualReverse ix | IRUngroup (_, ix) | IRUngroupRows (_, _, ix) -> indices [ix]
        | _ -> ()) expr
     found)

let private owns (id: IRId) : bool =
    (Blade.Optimize.HotRowTable.tryProducer id).IsSome || (Blade.Optimize.HotRowTable.tryConsumer id).IsSome ||
    (Blade.Optimize.CellFoldTable.tryLoop id).IsSome || (Blade.Optimize.CellFoldTable.tryFoldLet id).IsSome ||
    (Blade.Optimize.RecStepTable.tryLoop id).IsSome || (Blade.Optimize.RecStepTable.tryStep id).IsSome ||
    (Blade.Optimize.EpilogueLegTable.tryProducer id).IsSome || (Blade.Optimize.EpilogueLegTable.tryJoin id).IsSome ||
    (Blade.Optimize.CompactionTable.tryMask id).IsSome || (Blade.Optimize.CompactionTable.tryCompound id).IsSome ||
    (Blade.Optimize.CompactionTable.tryFoldMap id).IsSome || not (Blade.Optimize.CompactionTable.foldsOf id).IsEmpty ||
    (Blade.Optimize.CompactRowTable.tryProducer id).IsSome || (Blade.Optimize.CompactRowTable.tryConsumer id).IsSome ||
    not (Blade.Optimize.MemberHoistTable.plansOf id).IsEmpty

/// Private worker: callers cannot substitute the older extent-only foreign
/// census for A05's dependency-aware final whole-program roots.
let private scheduleModule (builder: IRBuilder) (modules: IRModule list) (modul: IRModule) : IRModule =
    let functions = modules |> List.collect _.Functions
    let funcs = functions |> List.map (fun f -> f.Id, f) |> Map.ofList
    let roots = modules |> List.collect (fun m -> Blade.Optimize.compactionRoots m.Functions m)
    let rec spine e =
        match e with
        | IRLet (id, value, rest) -> let ls, tail = spine rest in (id, value) :: ls, tail
        | tail -> [], tail
    let used = System.Collections.Generic.HashSet<IRId>()
    let declarations (e: IRExpr) =
        for id in collectVarRefsIR e do used.Add id |> ignore
        for id in (Blade.IRPrint.exprAttrs e).BoundVars do used.Add id |> ignore
    for m in modules do
        for id in Blade.Optimize.moduleReferencedIds m do used.Add id |> ignore
        for b in m.Bindings do
            used.Add b.Id |> ignore
            for payload in Blade.Optimize.bindingPayload m b do declarations payload
        let sideIds: IRId list =
            (m.CompoundInits |> Map.toList |> List.map fst) @
            (m.SparseInits |> Map.toList |> List.map fst) @
            (m.RandomInits |> Map.toList |> List.map fst)
        for id in sideIds do
            used.Add id |> ignore
            for payload in Blade.Optimize.bindingSideOperands m id do declarations payload
    for root in roots do
        match root with
        | Blade.Optimize.RootType (_, ty) -> for extent in Blade.Optimize.irTypeExtentExprs ty do declarations extent
        | _ -> ()
    for f in funcs |> Map.toSeq |> Seq.map snd do
        used.Add f.Id |> ignore
        for p in f.Params do used.Add p.VarId |> ignore
        declarations f.Body
    let minimumFresh = used |> Seq.fold max 0
    builder.EnsureAtLeast(minimumFresh + 1)
    let fresh () =
        let id = builder.FreshId()
        used.Add id |> ignore
        id
    let kernel (arity: int) (snapshots: Set<IRId>) (e: IRExpr) : bool =
        match e with
        | IRVar (kid, ty) ->
            match Map.tryFind kid funcs with
            | Some k ->
                let vars = (k.Params |> List.map _.VarId) @ (k.Captures |> List.map _.Id)
                // (+)'s existing binary comm licence is retained exactly; it
                // is not a host parallel strategy and grants no new reordering.
                serial k && (k.CommGroups.IsEmpty || (arity = 2 && k.CommGroups = [[0; 1]])) &&
                ty = IRTArrow (List.replicate arity (SVal (IRTScalar ETFloat64)), IRTScalar ETFloat64, None) &&
                k.Params.Length = arity && k.RetType = IRTScalar ETFloat64 &&
                (k.Params |> List.mapi (fun i p -> p.Index = i && p.Type = IRTScalar ETFloat64) |> List.forall id) &&
                (Set.ofList vars).Count = vars.Length &&
                // Ordinary local lets are marked mutable even when never
                // assigned. The closed whole spine proves no write exists.
                (k.Captures |> List.forall (fun c -> c.Type = IRTScalar ETFloat64 && snapshots.Contains c.Id)) &&
                scalar (Set.ofList vars) k.Body
            | None -> false
        | _ -> false
    let map (ty: IRType) (info: ApplyInfo) : bool =
        sameShape ty info.OutputType && info.KernelInputRanks = [0] && info.KernelOutputRank = 0 &&
        info.SDimsPerArray = [1] && info.TriangularLevels = [false] && info.SymcomStates = [SCNeither] &&
        info.SharedIndexTypes.IsEmpty && info.KernelTDims.IsEmpty && not info.IsCoIteration && not info.HasReynolds &&
        info.SpeedupFactor = 1L && info.ReynoldsSpeedup = 1L && info.Identities.Length = 1 &&
        (match info.ArrayTypes with [at] -> sameShape ty (mkArrayLike at) | _ -> false) &&
        (match info.Loop with
         | IRMethodFor loop ->
             loop.Arrays = info.Arrays && loop.ArrayTypes = info.ArrayTypes && loop.Identities = info.Identities &&
             loop.SDimsPerArray = [1] && loop.TotalSDims = 1 && loop.SharedIndexTypes.IsEmpty
         | _ -> false)
    let rewrite (f: IRCallable) : IRCallable =
        let lets, tail = spine f.Body
        // Do not add noise for scalar kernels or already scheduled lifetimes.
        match lets, tail with
        | (pool, IRCompute (IRApplyCombinator producer)) :: observations, IRCompute (IRApplyCombinator output)
            when (plain producer.OutputType).IsSome && (plain f.RetType).IsSome ->
            let mutable blocked: string option = None
            let require ok why = if not ok && blocked.IsNone then blocked <- Some why
            require (serial f && f.CommGroups.IsEmpty && f.Captures.IsEmpty && not f.Effects.Mutates && not f.Effects.EmitsOutput && not f.Effects.ReadsExternal)
                    "not an ordinary capture-free serial callable"
            require (not (containsPoolLifetimeEvent f.Body)) "existing lifetime region"
            let localIds = lets |> List.map fst
            let declared = (f.Params |> List.map _.VarId) @ localIds
            require ((Set.ofList declared).Count = declared.Length && not (List.contains f.Id declared) &&
                     (f.Params |> List.mapi (fun i p -> p.Index = i) |> List.forall id)) "stale or duplicate parameter/local identity"
            require (funcs.Count = functions.Length) "duplicate callable identity"
            let rebound =
                (modules |> List.exists (fun m -> m.Bindings |> List.exists (fun b -> b.Id = f.Id || List.contains b.Id declared))) ||
                (funcs |> Map.exists (fun fid k ->
                    if fid = f.Id then false
                    else
                        let mutable collision = List.contains k.Id declared || (k.Params |> List.exists (fun p -> List.contains p.VarId declared))
                        iterIRExpr (function IRLet (id, _, _) when List.contains id declared -> collision <- true | _ -> ()) k.Body
                        collision))
            require (not rebound) "local identity is rebound outside this function"
            require (f.Params.Length = 2 && (f.Params |> List.forall (fun p -> (plain p.Type).IsSome))) "requires two stored plain array formals"
            let ids = Set.ofList (f.Id :: declared) |> Set.union (collectVarRefsIR f.Body)
            let generic =
                (PoolReuseTable.currentReturnDonor ()).IsSome ||
                (PoolReuseTable.donors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b)) ||
                (PoolReuseTable.returnDonors () |> Map.exists (fun a b -> ids.Contains a || ids.Contains b))
            let memberOwner = Blade.Optimize.MemberHoistTable.all () |> List.exists (fun p -> ids.Contains p.HostId || ids.Contains p.SetId || not (Set.intersect ids p.Kernels).IsEmpty)
            require (not generic && not memberOwner && not (ids |> Set.exists owns) &&
                     not (ids |> Set.exists (fun a -> ids |> Set.exists (fun b -> (Blade.Optimize.OrbitPullTable.tryAudit a b).IsSome)))) "another planner owns this body"
            let retained =
                (roots |> List.exists (function
                    | Blade.Optimize.RootFunctionBody k when k.Id = f.Id -> false
                    | Blade.Optimize.RootFunctionBody k -> exprReads pool k.Body || (k.Captures |> List.exists (fun c -> c.Id = pool || typeReads pool c.Type))
                    | Blade.Optimize.RootBindingValue b -> exprReads pool b.Value
                    | Blade.Optimize.RootSideOperand (_, _, e) -> exprReads pool e
                    | Blade.Optimize.RootType (_, ty) -> typeReads pool ty
                    | Blade.Optimize.RootSourceRef (_, id) -> id = pool
                    | Blade.Optimize.RootNameRef _ -> true))
            require (not retained) "pool retained by a capture/type/side payload or foreign reference"
            // Statement events cannot be installed in a function subsequently
            // rendered as an expression/kernel value. Only ordinary direct
            // calls are covered, in every final module.
            let rec directCallsOnly expr =
                match expr with
                | IRApp (IRVar (fid, _), args, _) when fid = f.Id -> args |> List.forall directCallsOnly
                | IRVar (fid, _) when fid = f.Id -> false
                | ExprShape (children, _) -> children |> List.forall directCallsOnly
            let valueUse =
                (roots |> List.exists (function
                    | Blade.Optimize.RootFunctionBody k -> not (directCallsOnly k.Body)
                    | Blade.Optimize.RootBindingValue b -> not (directCallsOnly b.Value)
                    | Blade.Optimize.RootSideOperand (_, _, e) -> not (directCallsOnly e)
                    | Blade.Optimize.RootType (_, ty) -> typeReads f.Id ty
                    | Blade.Optimize.RootSourceRef (_, id) -> id = f.Id
                    | Blade.Optimize.RootNameRef _ -> true))
            require (not valueUse) "function has an expression/kernel use without a statement lifetime proof"
            let formal (ty: IRType) (info: ApplyInfo) : IRId option =
                match info.Arrays with
                | [IRVar (id, actual)] when sameShape ty actual ->
                    f.Params |> List.tryPick (fun p ->
                        let identity = info.Identities = [AIDVariable p.Name] || info.Identities = [AIDParameter (p.Name, p.Index)]
                        if p.VarId = id && p.Type = actual && identity then Some id else None)
                | _ -> None
            let input = formal producer.OutputType producer
            let resultInput = formal f.RetType output
            require (map producer.OutputType producer && input.IsSome && kernel 1 Set.empty producer.Kernel)
                    "pool is not one fresh closed total scalar map of an original stored formal"
            require (map f.RetType output && resultInput.IsSome && resultInput <> input)
                    "return is not a map of an independent stored formal"
            require (match plain producer.OutputType, plain f.RetType with
                     | Some a, Some b -> a.Extent <> b.Extent
                     | _ -> false) "requires different positive static extents"
            let mutable snapshots = Set.empty
            let mutable folds = 0
            for id, value in observations do
                let observation =
                    match value with
                    | IRReduce (IRVar (source, ty), fold, None) when source = pool && sameShape ty producer.OutputType ->
                        if kernel 2 snapshots fold then
                            folds <- folds + 1
                            true
                        else false
                    | IRIndex (IRVar (source, ty), [IRLit (IRLitInt index)], _) when source = pool && sameShape ty producer.OutputType ->
                        match plain ty with
                        | Some ix -> match ix.Extent with IRLit (IRLitInt n) -> index >= 0L && index < n | _ -> false
                        | None -> false
                    | _ -> scalar snapshots value
                require (typeOf value = IRTScalar ETFloat64 && observation) "unsupported observation, alias/deferred reader, failure or extra allocation"
                snapshots <- Set.add id snapshots
            require (folds > 0) "no completed nonempty scalar fold"
            require (kernel 1 snapshots output.Kernel) "return kernel retains a read or is not closed total scalar arithmetic"
            let record outcome evidence =
                Blade.Effects.Decisions.record
                    { Rule = "pool-early-release"; Version = 1; Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = outcome; Evidence = ($"function v{f.Id}, owned local v{pool}" :: evidence) }
            match blocked with
            | Some why -> record (Blade.Effects.Declined why) []; f
            | None when not (Blade.Optimize.earlyPoolReleaseEnabled ()) ->
                record (Blade.Effects.Declined "disabled by BLADE_POOL_EARLY_RELEASE") []; f
            | None ->
                let beginId, releaseId = fresh (), fresh ()
                let label = $"early-pool-release: {f.SourceName} v{f.Id}, pool v{pool}"
                let release = IRReleasePools (IRVar (beginId, IRTUnit), [IRVar (pool, producer.OutputType)], label)
                let prefix = List.foldBack (fun (id, value) rest -> IRLet (id, value, rest)) lets (IRLet (releaseId, release, tail))
                record Blade.Effects.Applied ["one fresh backing; closed scalar snapshots complete; release before independent different-extent return allocation"; "original producer, observations and tail retained in order; exact nested scope contains only the named pool"]
                { f with Body = IRLet (beginId, IRBeginPoolScope label, prefix) }
        | _ -> f
    { modul with Functions = modul.Functions |> List.map rewrite }

/// LAST scheduling stage, after all modules' planners. Root identities and
/// retention are checked against final executable/type/side payloads from the
/// whole program. The existing shared optimizer census remains unchanged.
let internal scheduleModules (builder: IRBuilder) (modules: IRModule list) : IRModule list =
    modules |> List.map (scheduleModule builder modules)
