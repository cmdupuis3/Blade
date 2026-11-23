// The assembly destination fill, rule assembly-dest, and its side table (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeAssemblyDest

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeHotRow
open Blade.OptimizeEpilogueLegs
open Blade.OptimizeCompaction
open Blade.OptimizeTranspose
open Blade.OptimizeInPlace
open Blade.OptimizeStreaming

// ===========================================================================
// ASSEMBLY DESTINATION FILL (BLADE_ASSEMBLY_DEST, plan rule `assembly-dest
// v1`; round 4 wave 4, lane F item F2, PM26). A required transpose / stack /
// join output must exist, but the operand a CELLWISE map produces only for it
// need not: `transpose(A * 2.0, [0, 1])` lowered to a private temporary
// `__vN = A * 2.0 |> compute` (a whole pool), then the transpose's copy into
// its own pool. Admitted, the temporary is never stored: the assembly's copy
// nest evaluates the map's kernel at each source cell and writes the
// destination ONCE -- for a rank-2 transpose in bounded 32 x 32 tiles of the
// swapped axes (both the source rows and the destination rows a tile touches
// stay in cache). Same pattern as provider-sink-stream: the producer moves
// from its own site to the assembly's, so the producer must be total and
// write-free (no failure or effect moves, and the tiled order is not the
// map's), nothing between the two sites may write (the snapshot), the
// assembly must be the temporary's ONLY observer and the temporary must not
// print (a compiler temporary `__v..`, or excluded by a print selection).
// Every cell is the same kernel on the same operand cells: values bitwise the
// stored form under BLADE_FP_CONTRACT=off. The C++ emitter records the
// applied decision; the LLVM lane keeps the stored temporary (recorded); the
// interpreter runs the unchanged IR.
// ===========================================================================

/// BLADE_ASSEMBLY_DEST=0|off keeps the stored temporary and the copy; unset =
/// on. Read per call.
let assemblyDestEnabled () = gateOn "BLADE_ASSEMBLY_DEST"

type AssemblyDestPlan =
    { ProducerId: IRId
      ProducerName: string
      AssemblyId: IRId
      AssemblyName: string
      /// The producer's operands (named stored arrays of the producer's shape)
      /// and its resolved cellwise kernel.
      Operands: IRExpr list
      Kernel: IRCallable
      Evidence: string list
      /// Set by the C++ emitter at the producer's site when it omitted the
      /// temporary (it re-checks renderability there); the assembly fuses only then.
      Taken: bool ref }

module AssemblyDestTable =
    open System.Threading
    type State = { Producers: Map<IRId, AssemblyDestPlan>; Inputs: Set<IRId> }
    let private empty = { Producers = Map.empty; Inputs = Set.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (p: AssemblyDestPlan) (inputs: Set<IRId>) =
        let s = get ()
        cell.Value <- { Producers = Map.add p.ProducerId p s.Producers; Inputs = Set.union inputs s.Inputs }
    let tryProducer (id: IRId) = Map.tryFind id (get ()).Producers
    /// The plans whose assembly is `id`, by producer id.
    let forAssembly (id: IRId) = (get ()).Producers |> Map.filter (fun _ p -> p.AssemblyId = id)
    let remove (producerId: IRId) =
        let s = get () in cell.Value <- { s with Producers = Map.remove producerId s.Producers }
    let contains (id: IRId) =
        let s = get ()
        s.Producers.ContainsKey id || s.Inputs.Contains id
        || (s.Producers |> Map.exists (fun _ p -> p.AssemblyId = id))

let recordAssemblyDest (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "assembly-dest"; Version = 1
          Span = declSpanOf subject; Subject = subject
          Outcome = outcome; Evidence = evidence }

let planAssemblyDest (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    let arr = List.toArray modul.Bindings
    // An assembly's operands are usually LOCAL temporaries: `transpose(A * 2.0,
    // [0, 1])` lowers to the binding value `let __vN = A * 2.0 |> compute in
    // transpose(__vN, [0, 1])`, whose chain lets the C++ lane emits as main
    // statements named `__v<id>` (genLetChainBinding). So the assembly is the
    // TAIL of a binding's let spine, and a producer is a let of that spine or
    // an earlier module binding.
    let spine (e: IRExpr) =
        let rec go e acc =
            match e with
            | IRLet (id, v, rest) -> go rest ((id, v) :: acc)
            | t -> (List.rev acc, t)
        go e []
    let assemblies =
        arr |> Array.choose (fun b ->
            let (lets, tail) = spine b.Value
            match tail with
            | IRTranspose (IRVar _, _, _) | IRStack _ | IRJoin _ -> Some (b, lets, tail)
            | _ -> None)
    if assemblies.Length = 0 then () else
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
    let hasCuda = callables |> List.exists (fun f -> f.IsCudaKernel)
    let hasMpi = callables |> List.exists (fun f -> f.IsMpiParallel)
    let printSelection =
        Blade.PrintSelection.names ()
    let tileCacheOn =
        match Blade.Toolchain.envSwitch "BLADE_TILE_CACHE" with
        | Blade.Toolchain.SwitchUnset | Blade.Toolchain.SwitchOff -> false
        | _ -> true
    /// A plain dense array with literal, NON-ZERO extents: its element and
    /// extents (an empty axis declines: the fill takes pool base pointers,
    /// which an empty skeleton does not have).
    let denseLit (t: IRType) : (ElemType * int64 list) option =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual && not at.IndexTypes.IsEmpty ->
            let exts =
                at.IndexTypes |> List.map (fun ix ->
                    if ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty then
                        match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some n | _ -> None
                    else None)
            match stripUnits at.ElemType with
            | IRTScalar et when exts |> List.forall Option.isSome -> Some (et, exts |> List.map Option.get)
            | _ -> None
        | _ -> None
    let ownedBy (id: IRId) : string option =
        if SinkStreamTable.contains id then Some "the provider-sink-stream plan"
        elif (HotRowTable.tryProducer id).IsSome || (HotRowTable.tryConsumer id).IsSome then Some "the hot-row / cell-epilogue plan"
        elif (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome
             || (CompactionTable.tryFoldMap id).IsSome || not (CompactionTable.foldsOf id).IsEmpty then Some "the compaction-fusion plan"
        elif (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome then Some "the epilogue-legs plan"
        elif AliasElideTable.contains id then Some "the read-only alias plan"
        elif (GroupViewTable.tryGroup id).IsSome || (GroupViewTable.tryUngroup id).IsSome then Some "the structural group view"
        elif GroupRowMapTable.contains id then Some "the group-rowmap plan"
        elif (Blade.Types.PoolReuseTable.tryDonor id).IsSome
             || (Blade.Types.PoolReuseTable.donors () |> Map.exists (fun k v -> k = id || v = id))
             || (Blade.Types.PoolReuseTable.returnDonors () |> Map.exists (fun k v -> k = id || v = id)) then Some "scratch pool reuse"
        else None
    for (a, lets, tail) in assemblies do
        let ai = indexOf.[a.Id]
        let chainIdx = lets |> List.mapi (fun i (id, _) -> (id, i)) |> Map.ofList
        let chainVal = lets |> Map.ofList
        let operands =
            match tail with
            | IRTranspose (x, _, _) -> [ x ]
            | IRStack xs | IRJoin (xs, _) -> xs
            | _ -> []
        let operandIds = operands |> List.choose (function IRVar (y, _) -> Some y | _ -> None)
        let kindName =
            match tail with
            | IRTranspose _ -> "transpose"
            | IRStack _ -> "stack"
            | _ -> "join"
        let operandType (id: IRId) : IRType option =
            match Map.tryFind id byId with
            | Some b -> Some b.Type
            | None -> Map.tryFind id chainVal |> Option.map typeOf
        let chainWriter (fromIdx: int) =
            lets |> List.indexed |> List.tryPick (fun (i, (id, v)) ->
                if i >= fromIdx && (writes v || (facts v).MayWrite) then Some $"__v{id}" else None)
        for yid in operandIds |> List.distinct do
            // (name, value, local chain position or module index)
            let producer : (string * IRExpr * Choice<int, int>) option =
                match Map.tryFind yid chainIdx with
                | Some ci -> Some ($"__v{yid}", chainVal.[yid], Choice1Of2 ci)
                | None ->
                    match Map.tryFind yid byId, Map.tryFind yid indexOf with
                    | Some y, Some yi -> Some (y.Name, y.Value, Choice2Of2 yi)
                    | _ -> None
            match producer with
            | Some (yName, yValue, pos) when (match yValue with IRCompute (IRApplyCombinator _) | IRApplyCombinator _ -> true | _ -> false) ->
                let h = match yValue with IRCompute (IRApplyCombinator h) | IRApplyCombinator h -> h | _ -> failwith "unreachable"
                let yType = match operandType yid with Some t -> t | None -> typeOf yValue
                // the module index before which the producer's inputs must be bound
                let inputBound = match pos with Choice1Of2 _ -> ai | Choice2Of2 yi -> yi
                let fail (s: string) : Result<AssemblyDestPlan, string> = Error s
                let verdict : Result<AssemblyDestPlan, string> =
                    if not singleModule then fail "a multi-module program (the print selection and the census are single-module)"
                    elif hasMpi then fail "an MPI build"
                    elif hasCuda then fail "the module holds a CUDA kernel"
                    elif tileCacheOn then fail "revision reuse (BLADE_TILE_CACHE) plans tiled bindings at emission"
                    elif (match pos with Choice2Of2 yi -> yi >= ai | _ -> false) then fail $"`{yName}` is not bound before the {kindName}"
                    elif (operandIds |> List.filter ((=) yid) |> List.length) > 1 then fail $"the {kindName} names `{yName}` more than once"
                    else
                    let otherOperands =
                        match tail with
                        | IRStack xs | IRJoin (xs, _) ->
                            xs |> List.tryPick (fun x ->
                                match x with
                                | IRVar (oid, _) ->
                                    (match operandType oid with
                                     | Some t when (denseLit t).IsSome -> None
                                     | Some _ -> Some $"a {kindName} operand is not a plain dense array with literal non-zero extents (the fused fill copies every operand as a flat pool)"
                                     | None -> Some $"a {kindName} operand is not a binding")
                                | _ -> Some $"a {kindName} operand is an inline form")
                        | _ -> None
                    match otherOperands with
                    | Some why -> fail why
                    | None ->
                    match denseLit yType with
                    | None -> fail $"`{yName}` is not a plain dense array with literal non-zero extents"
                    | Some (et, exts) ->
                    if h.HasReynolds then fail "the kernel is a Reynolds symmetrization"
                    elif h.KernelOutputRank <> 0 || h.KernelInputRanks |> List.exists (fun r -> r <> 0) then fail "the kernel does not map one cell of each operand to one cell"
                    else
                    let operandProof =
                        h.Arrays |> List.tryPick (fun o ->
                            match o with
                            | IRVar (oid, _) ->
                                (match Map.tryFind oid byId, Map.tryFind oid indexOf with
                                 | Some ob, Some oi when oi < inputBound ->
                                     (match denseLit ob.Type with
                                      | Some (_, oe) when oe = exts ->
                                          (match stored ob with
                                           | Ok _ -> None
                                           | Error why -> Some $"the operand `{ob.Name}` is not proved stored: {why}")
                                      | _ -> Some $"the operand `{ob.Name}` is not a plain dense array of the extents of `{yName}`")
                                 | _ -> Some "an operand is not an earlier top-level binding (a temporary of the same chain is v1.1)")
                            | _ -> Some "an operand is an inline form, not a named stored array")
                    match operandProof with
                    | Some why -> fail why
                    | None ->
                    match resolve h.Kernel with
                    | None -> fail "the kernel does not resolve to a callable"
                    | Some c ->
                    let scalarOf (t: IRType) = IRTotality.scalarElem (stripUnits t)
                    if c.Params.Length <> h.Arrays.Length then fail "the kernel parameters are not one per operand"
                    elif c.Params |> List.exists (fun p -> (scalarOf p.Type).IsNone) then fail "a kernel parameter is not a scalar"
                    elif scalarOf c.RetType <> Some et then fail "the kernel result is not the array scalar element"
                    elif c.IsOmpParallel || not c.Parallelism.IsEmpty || c.IsCudaKernel || c.IsMpiParallel then
                        fail "the kernel requests a parallel strategy (v1's fill is serial)"
                    else
                    let captureProof =
                        c.Captures |> List.tryPick (fun cap ->
                            match Map.tryFind cap.Id byId, Map.tryFind cap.Id indexOf with
                            | Some cb, Some ci when ci < inputBound ->
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
                    match IRTotality.auditApply resolve (fun _ -> None) deferredIds.Contains h with
                    | Error why -> fail $"the producer kernel is not total (a failure would move to the {kindName}, and the tiled order is not the map order): {why}"
                    | Ok () ->
                    if writes yValue || (facts yValue).MayWrite then fail $"the producer may write (its effects would move to the {kindName})"
                    else
                    let mentions (e: IRExpr) =
                        (collectVarRefsIR e).Contains yid
                        || (embeddedExtentExprs e |> List.exists (fun x -> (collectVarRefsIR x).Contains yid))
                    // THE DEMAND CENSUS: the assembly tail is the only observer --
                    // no other let of the chain, no other root.
                    let chainObserver =
                        lets |> List.tryPick (fun (id, v) -> if id <> yid && mentions v then Some $"`__v{id}` reads it" else None)
                    let typeDemand (t: IRType) = irTypeExtentExprs t |> List.exists mentions
                    let observer =
                        match chainObserver with
                        | Some _ -> chainObserver
                        | None ->
                            roots.Force() |> List.tryPick (function
                                | RootBindingValue b when b.Id = yid || b.Id = a.Id -> None
                                | RootBindingValue b when mentions b.Value -> Some $"`{b.Name}` reads it"
                                | RootSideOperand (b, what, e) when mentions e -> Some $"{what} of `{b.Name}`"
                                | RootFunctionBody f when mentions f.Body || (f.Captures |> List.exists (fun cp -> cp.Id = yid)) ->
                                    Some $"a callable body or capture in `{f.SourceName}`"
                                | RootType (where, t) when where <> $"`{yName}`" && typeDemand t -> Some $"the type of {where}"
                                | RootNameRef (where, nm) when nm = yName -> Some where
                                | _ -> None)
                    if foreignRefs.Contains yid then fail $"`{yName}` is referenced by another module"
                    elif modul.ProviderWrites |> Map.exists (fun _ s -> s.SourceId = yid) then fail $"`{yName}` is written to a store"
                    else
                    match observer with
                    | Some why -> fail $"`{yName}` has another observer: {why}"
                    | None ->
                    let printed =
                        match pos with
                        | Choice1Of2 _ -> None
                        | Choice2Of2 _ ->
                            // an explicit selection first: it may name an
                            // internal `__` binding, which then prints (gap
                            // G200); without one, an internal name does not
                            // print (`__expr<N>` does: PrintSelection.isInternal)
                            match printSelection with
                            | Some sel when Blade.PrintSelection.mayName sel yName -> Some $"the print selection names `{yName}`"
                            | Some _ -> None
                            | None when Blade.PrintSelection.isInternal yName -> None
                            | None -> Some $"`{yName}` is printed (every top-level binding prints unless a print selection, BLADE_PRINT / --print, excludes it)"
                    match printed with
                    | Some why -> fail why
                    | None ->
                    let inputIds =
                        (h.Arrays |> List.collect (fun o -> collectVarRefsIR o |> Set.toList))
                        @ (c.Captures |> List.map (fun cp -> cp.Id))
                    let ownerOf =
                        (yid :: a.Id :: inputIds) |> List.tryPick (fun id ->
                            ownedBy id |> Option.map (fun who ->
                                let nm = match Map.tryFind id byId with Some b -> b.Name | None -> $"__v{id}"
                                $"`{nm}` is scheduled by {who}"))
                    match ownerOf with
                    | Some why -> fail why
                    | None ->
                    // THE SNAPSHOT: nothing between the producer site and the
                    // assembly writes -- the module bindings in between (a module
                    // producer) and the chain lets after it (or all of them).
                    let between =
                        match pos with
                        | Choice1Of2 _ -> []
                        | Choice2Of2 yi -> [ yi + 1 .. ai - 1 ] |> List.map (fun i -> arr.[i])
                    let writer =
                        between |> List.tryFind (fun b ->
                            let p = payloadOf b.Id b.Value
                            writes p || (facts p).MayWrite)
                        |> Option.map (fun b -> b.Name)
                    let chainW =
                        match pos with
                        | Choice1Of2 ci -> chainWriter (ci + 1)
                        | Choice2Of2 _ -> chainWriter 0
                    match (match writer with Some w -> Some w | None -> chainW) with
                    | Some w -> fail $"`{w}`, between `{yName}` and the {kindName}, may write (the producer inputs would differ at the {kindName})"
                    | None ->
                    let cells = exts |> List.fold (*) 1L
                    let tiled =
                        match tail with
                        | IRTranspose (_, d1, d2) when exts.Length = 2 && ((d1 = 0 && d2 = 1) || (d1 = 1 && d2 = 0)) -> true
                        | _ -> false
                    Ok { ProducerId = yid; ProducerName = yName; AssemblyId = a.Id; AssemblyName = a.Name
                         Operands = h.Arrays; Kernel = c; Taken = ref false
                         Evidence =
                            [ $"`{yName}` ({cells} cells) is never stored: the {kindName} `{a.Name}` evaluates its cellwise kernel at each source cell and writes its destination once"
                              (if tiled then "fill: 32 x 32 tiles of the two swapped axes (the source rows and the destination rows a tile touches stay in cache)"
                               else "fill: one flat or source-ordered run per operand, each cell computed in place of its read")
                              "legality: the producer is IRTotality-total and write-free (no failure or effect moves; the fill order is not the map order); nothing between its site and the assembly writes (the snapshot); the assembly is its only observer (chain lets, values, side operands, callables, captures, types, other modules, stores) and it does not print; every extent is a non-zero literal"
                              $"cost: peak -{cells} cells of `{yName}`; work unchanged (one kernel evaluation per cell)" ] }
                match verdict with
                | Error why ->
                    recordAssemblyDest yName (Blade.Effects.Declined why) [ $"`{yName}` is stored, then copied by the {kindName} `{a.Name}`" ]
                | Ok plan when not (assemblyDestEnabled ()) ->
                    recordAssemblyDest yName (Blade.Effects.Declined "disabled by BLADE_ASSEMBLY_DEST") plan.Evidence
                | Ok plan ->
                    let inputs =
                        (plan.Operands |> List.collect (fun o -> collectVarRefsIR o |> Set.toList))
                        @ (plan.Kernel.Captures |> List.map (fun cp -> cp.Id))
                    AssemblyDestTable.record plan (Set.ofList inputs)
            | _ -> ()
