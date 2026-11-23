// Sibling maps, rule sibling-maps: stored maps of one spine sharing one traversal, and its side table (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeSiblingMaps

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
open Blade.OptimizeStreaming
open Blade.OptimizeAssemblyDest

// ===========================================================================
// SIBLING MAPS (round 4 wave 2, bench PM04; BLADE_SIBLING_MAPS, rule
// `sibling-maps v1`). Two to eight STORED `|> compute` maps of one spine over
// one input index space each traverse their inputs again (the bench twofir:
// two FIR walks over one stored x). Every named output stays stored -- the
// required bytes do not disappear -- the win is INPUT traffic: ONE traversal
// writes every output, each cell its own kernel's arithmetic at its own
// coordinate.
//
// FUSED AT THE LAST SIBLING. Each member keeps its own site for everything
// but its loop -- its prechecks (the halo BL8009 guard), extents, allocation
// and registrations, in source order, so allocation positions and the peak
// are unchanged; the earlier members' LOOPS move DOWN to the last member,
// which runs one sweep for all. A side table: both emitters realize it (each
// preflights the WHOLE group before it suppresses any loop, so a group is
// either fully fused or fully ordinary); the interpreter runs the IR as
// written, so `blade test interp` is an independent oracle.
//
// Moving a loop down past the statements between it and the last member is
// unobservable when (the admission, fail closed): every member kernel is
// closed-total (no cell fails, so no failure moves) and writes nothing; no
// statement in the interval -- and no later member -- writes, carries a
// provider effect or a lifetime barrier, or reads an EARLIER member, directly
// or through any callable reachable from it; no earlier member's storage or
// INPUT storage changes hands before the sweep (no scratch pool reuse inside
// the interval, no return donor among them, and the early-release schedule
// treats them as owned); every member is read after the last one (an unread
// output could be released early); no member is claimed by another plan.
// ===========================================================================

type SiblingMapKind =
    /// plain dense co-iterations over one cell space of `cells` cells: one
    /// flat sweep
    | SibFlat of cells: int64
    /// rank-1 single-slot dense halo walks over ONE inner axis of `baseExtent`
    /// cells: one walk over the union of the members' centres, peeled into
    /// segments by member domain
    | SibHalo of inner: string * baseExtent: int64
    /// rank-1 single-slot dense halo walks of IDENTICAL geometry whose window
    /// reads all go to ONE streamed Zarr variable (`source`, a provider read
    /// binding) over a chunked axis (round 4 wave 4, E1 / gap G106): the C++
    /// segment route runs ONE run loop -- one window, one chunk cache, one
    /// read and BL8015 scan per run -- and every member's kernel per run
    | SibHaloStream of inner: string * baseExtent: int64 * source: IRId

type SiblingMapGroup = {
    GroupId: int
    Region: string
    /// the members' let / binding ids, in spine order
    Members: IRId list
    Names: string list
    Kind: SiblingMapKind
    /// per member: the first centre of its walk (halo; 0 for flat) and its
    /// output extent (cells)
    Starts: int64 list
    Extents: int64 list
    /// every member requests the same thread licence (all or none)
    Omp: bool
    Evidence: string list
    /// the members' applies as planned (the emitters' preflight rebuilds each
    /// member's nest record from them before suppressing any loop)
    Applies: ApplyInfo list
}

module SiblingMapTable =
    open System.Threading
    type State = { ByMember: Map<IRId, SiblingMapGroup * int>; Protected: Set<IRId>; Next: int }
    let private empty = { ByMember = Map.empty; Protected = Set.empty; Next = 0 }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let nextId () =
        let s = get ()
        cell.Value <- { s with Next = s.Next + 1 }
        s.Next
    let record (g: SiblingMapGroup) (protectedIds: Set<IRId>) =
        let s = get ()
        cell.Value <-
            { s with
                ByMember = g.Members |> List.mapi (fun j m -> (m, (g, j))) |> List.fold (fun acc (m, v) -> Map.add m v acc) s.ByMember
                Protected = Set.union s.Protected protectedIds }
    /// The group a binding belongs to, with its position.
    let tryMember (id: IRId) : (SiblingMapGroup * int) option = Map.tryFind id (get ()).ByMember
    /// A member or an input an EARLIER member's moved sweep still reads: a
    /// later owner (the early pool-release schedule) must leave it live.
    let isProtected (id: IRId) : bool = Set.contains id (get ()).Protected

let recordSiblingMaps (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "sibling-maps"; Version = 1; Span = Blade.Ast.noSpan
          Subject = subject; Outcome = outcome; Evidence = evidence }

/// The planner (Lowering, after CSE, scratch pool reuse and every other
/// side-table plan; before the early pool-release schedule, which reads the
/// table's protected ids).
let planSiblingMaps (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (singleModule: bool) (modul: IRModule) : unit =
    if modul.Functions |> List.exists (fun f -> f.IsCudaKernel) then () else
    let enabled = siblingMapsEnabled ()
    // revision reuse (BLADE_TILE_CACHE) plans tiled bindings at emission: v1
    // leaves every map its own loop under it
    let tileCache = match System.Environment.GetEnvironmentVariable "BLADE_TILE_CACHE" with null | "" | "0" | "off" -> false | _ -> true
    if tileCache then () else
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs = programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let resolve (k: IRExpr) = resolveCallableIn funcs k
    let writes = scheduleMayWriteWithCallbacksIn programFuncs modul
    let barrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    // ---- the module census --------------------------------------------------
    let deferred = System.Collections.Generic.HashSet<IRId>()
    // the census widened by the program's deferred-value classifier (gap G98
    // class audit) for the totality audits below
    let deferredW = widenDeferred modul (modul.Functions @ programFuncs) deferred.Contains
    let letOwners = System.Collections.Generic.Dictionary<IRId, int>()
    for f in modul.Functions do
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, v, _) ->
                if isDeferredForm v then deferred.Add id |> ignore
                letOwners.[id] <- (match letOwners.TryGetValue id with | true, c -> c + 1 | _ -> 1)
            | _ -> ()) f.Body
    for b in modul.Bindings do
        if isDeferredForm b.Value then deferred.Add b.Id |> ignore
    // the program's own deferred classifier (G98: every totality audit carries it)
    let moduleDeferred = IRLift.computeDeferredIds modul.Bindings
    let reuse = Blade.Types.PoolReuseTable.donors ()
    let reusers = reuse |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let donorsOf = reuse |> Map.toSeq |> Seq.map snd |> Set.ofSeq
    let returnDonors =
        modul.Functions |> List.choose (fun f -> Blade.Types.PoolReuseTable.tryReturnDonor f.Id) |> Set.ofList
    /// Every id `e` reads, through every callable reachable from it (called,
    /// named as a kernel, captured) -- the dependency census.
    let reachRefs (e: IRExpr) : Set<IRId> =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let acc = System.Collections.Generic.HashSet<IRId>()
        let rec go (x: IRExpr) =
            for id in collectVarRefsIR x do
                acc.Add id |> ignore
                match Map.tryFind id funcs with
                | Some c when seen.Add c.Id ->
                    for cap in c.Captures do acc.Add cap.Id |> ignore
                    go c.Body
                | _ -> ()
        go e
        Set.ofSeq acc
    let claimedBy (id: IRId) : string option =
        if (HotRowTable.tryConsumer id).IsSome then Some "the hot-row / cell-epilogue plan (as its consumer)"
        elif (CompactRowTable.tryProducer id).IsSome || (CompactRowTable.tryConsumer id).IsSome then Some "the compact-row-classes plan"
        elif (CompactionTable.tryMask id).IsSome || (CompactionTable.tryCompound id).IsSome || (CompactionTable.tryFoldMap id).IsSome then Some "the compaction-fusion plan"
        elif (GroupAggTable.tryPeel id).IsSome || (GroupAggTable.tryGroupBy id).IsSome then Some "the group-aggregate plan"
        elif not (MemberHoistTable.plansOf id).IsEmpty then Some "the membership-hoist plan"
        elif (EpilogueLegTable.tryProducer id).IsSome || (EpilogueLegTable.tryJoin id).IsSome then Some "the epilogue-legs plan"
        elif (RecStepTable.tryStep id).IsSome then Some "the threaded recursion step"
        elif AliasElideTable.contains id then Some "the read-only alias plan"
        elif SinkStreamTable.contains id then Some "the provider-sink-stream plan (a streamed write's producer)"
        elif AssemblyDestTable.contains id then Some "the assembly-dest plan (a never-stored assembly operand)"
        elif GroupRowMapTable.contains id then Some "the group-rowmap plan (a shared row-map pool)"
        elif reusers.Contains id || donorsOf.Contains id || returnDonors.Contains id then Some "scratch pool reuse (a donor, a reuser or a return donor)"
        elif foreignRefs.Contains id then Some "another module (a foreign reader)"
        elif modul.ProviderReads.ContainsKey id || modul.ProviderWrites.ContainsKey id || not (bindingSideOperands modul id).IsEmpty then Some "a provider read / write or side-table operands"
        elif (match letOwners.TryGetValue id with | true, c -> c > 1 | _ -> false) then Some "shape specializations (its let id is copied into several callables)"
        else None
    let staticExtents (at: IRArrayType) =
        at.IndexTypes |> List.map (fun ix -> match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some n | _ -> None)
    let denseLiteral (at: IRArrayType) =
        not at.IsVirtual && not at.IndexTypes.IsEmpty
        && at.IndexTypes |> List.forall (fun ix -> ix.Rank = 1 && ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Dependencies.IsEmpty)
        && staticExtents at |> List.forall Option.isSome
    let kernelPlain (k: IRCallable) =
        not k.IsCommutative && k.CommGroups.IsEmpty && k.AntisymGroups.IsEmpty
        && not k.IsCudaKernel && not k.IsMpiParallel && not k.IsArityPoly && not k.IsStatic
    let ompOf (k: IRCallable) = k.IsOmpParallel || not k.Parallelism.IsEmpty
    // the exact licence (a bare `omp` and `omp(w: 0)` thread differently)
    let licenceOf (k: IRCallable) = (k.IsOmpParallel, k.Parallelism |> List.sort)
    // ---- one candidate member ------------------------------------------------
    // None: not a candidate (silent); Some (Error why): a stored map that
    // cannot join a group; Some (Ok ...): its shape key and facts.
    let classify (id: IRId) (v: IRExpr) : Result<SiblingMapKind * int64 * int64 * Set<IRId> * bool * IRCallable, string> option =
        match v with
        | IRCompute (IRApplyCombinator info) ->
            (match stripUnits info.OutputType, resolve info.Kernel with
             | ArrayElem outAt, Some k when denseLiteral outAt && not info.HasReynolds && info.KernelTDims.IsEmpty && info.KernelOutputRank = 0 ->
                 let outExt = staticExtents outAt |> List.map Option.get
                 let cells = outExt |> List.fold (*) 1L
                 let kernelOk () =
                     if not (kernelPlain k) then Error "the kernel declares symmetry, a device or MPI strategy, arity polymorphism or is static"
                     elif writes (IRApplyCombinator info) then Error "the kernel may write (its writes would move with the loop)"
                     else Ok ()
                 match info.Arrays with
                 | [ IRRange ([ ix ], _) ] ->
                     // ---- kind H: a rank-1 single-slot dense halo walk ----------
                     (match ix.Tag |> Option.bind Blade.Types.haloAccessOfTag, ix.Extent, k.Params with
                      | Some h, IRLit (IRLitInt m), [ p ] when not h.IsCompound && outExt = [ m ] && m > 0L
                                                            && (ix.Tag |> Option.exists (fun t -> t.StartsWith (Blade.Types.haloWinTagPrefix + "d:"))) ->
                          match kernelOk () with
                          | Error why -> Some (Error why)
                          | Ok () ->
                          let baseN = m + h.Shrink
                          let isWin (wv: IRExpr) = match wv with IRVar (wid, _) -> wid = p.VarId | _ -> false
                          let reads = Blade.IRAccess.windowReadsOf isWin k.Body
                          let refsP = (let c = ref 0 in iterIRExpr (function IRVar (q, _) when q = p.VarId -> c.Value <- c.Value + 1 | _ -> ()) k.Body; c.Value)
                          let readOk (r: Blade.IRAccess.WindowRead) =
                              r.Rank = 1 && r.Dim = 0 && not (deferred.Contains r.ArrayId)
                              && (match r.Offset with Some o -> Blade.Types.haloOffsetInReach h o | None -> false)
                              && (match r.Node with
                                  | IndexOfNamed (_, aty, _, _) ->
                                      (match stripUnits aty with
                                       | ArrayElem at -> denseLiteral at && staticExtents at = [ Some baseN ]
                                       | _ -> false)
                                  | _ -> false)
                          if reads.IsEmpty then Some (Error "the halo kernel reads no stored array through its window")
                          elif not (reads |> List.forall readOk) then
                              Some (Error "a window read is not a literal in-reach offset of a stored rank-1 array over the halo's inner axis (the window certificate)")
                          elif refsP <> reads.Length then Some (Error "the window variable is used outside its window reads")
                          else
                          // THE WINDOW CERTIFICATE: every array read is a declared
                          // in-reach offset of an array whose declared extent is
                          // the halo's inner extent -- checked at run time by the
                          // member's own BL8009 guard, which stays at its site,
                          // ahead of the sweep. With those reads proven, the rest
                          // of the body must pass the closed totality whitelist.
                          let isRead (n: IRExpr) =
                              match n with
                              | IndexOfNamed (aid, _, [ IRBinOp (IRElementwise, IRAdd, wv, _, _) ], _) when isWin wv ->
                                  reads |> List.exists (fun r -> r.ArrayId = aid)
                              | _ -> false
                          // a certified read becomes an OPAQUE immutable scalar of the
                          // element's type (an unbound name the whitelist reads as a free
                          // scalar): the certificate proves the read safe, never its VALUE
                          // (a literal would prove, e.g., an integer exponent nonnegative)
                          let opaqueSeq = ref 0
                          let opaqueOf (n: IRExpr) =
                              opaqueSeq.Value <- opaqueSeq.Value + 1
                              match n with
                              | IndexOfNamed (_, aty, _, _) ->
                                  (match stripUnits aty with
                                   | ArrayElem at -> IRVar (-1000000 - opaqueSeq.Value, at.ElemType)
                                   | _ -> IRVar (-1000000 - opaqueSeq.Value, IRTScalar ETFloat64))
                              | _ -> IRVar (-1000000 - opaqueSeq.Value, IRTScalar ETFloat64)
                          let body' = mapIRExpr (fun n -> if isRead n then opaqueOf n else n) k.Body
                          match Blade.IRTotality.auditCallable resolve (fun _ -> None) deferredW { k with Body = body' } [] with
                          | Error why -> Some (Error $"the halo kernel is not proved total beyond its certified window reads ({why})")
                          | Ok () ->
                          let inputs = reads |> List.map (fun r -> r.ArrayId) |> Set.ofList
                          if inputs |> Set.exists modul.ProviderReads.ContainsKey then
                              // E1 (G106): every window read goes to ONE streamed rank-1
                              // Zarr variable read through the certified uniform cursor
                              // (the halo chunk cache's route): the shared RUN LOOP kind
                              match Set.toList inputs with
                              | [ src ] ->
                                  let spec = modul.ProviderReads.[src]
                                  if not spec.Streamed then Some (Error "an input is a provider read that is not streamed (`.read` has no pool to share in this plan)")
                                  elif spec.Provider <> "zarr" then Some (Error $"an input is a streamed '{spec.Provider}' read (v1 shares the Zarr segment route only)")
                                  elif spec.VarType.IndexTypes.Length <> 1 then Some (Error "an input is a streamed variable of rank >= 2 (v1 shares rank-1 run loops)")
                                  elif spec.AllowMissing || spec.Window.IsSome || spec.MaskName.IsSome || spec.MaskType.IsSome then
                                      Some (Error "an input is a masked, windowed or raw streamed read (off the uniform cursor route)")
                                  elif not (siblingMapsStreamedEnabled ()) then
                                      Some (Error "a streamed source: the shared segment run loop is disabled by BLADE_SIBLING_MAPS=nostream (the pre-wave-7 default: one run loop per binding)")
                                  else Some (Ok (SibHaloStream (h.Inner, baseN, src), h.Start, m, inputs, ompOf k, k))
                              | _ -> Some (Error "the window reads go to a streamed source and another array (v1 shares one streamed source)")
                          else
                          Some (Ok (SibHalo (h.Inner, baseN), h.Start, m, inputs, ompOf k, k))
                      | _ -> None)
                 | arrays ->
                     // ---- kind F: a plain dense flat co-iteration -----------------
                     if not (tilePlainInfo info) then None
                     elif info.Arrays.Length > 1 && not info.IsCoIteration then None
                     else
                     let opIds = arrays |> List.choose (function IRVar (aid, _) -> Some aid | _ -> None)
                     if opIds.Length <> arrays.Length then Some (Error "an operand is not a stored array (a range, a computed map or a view)")
                     elif opIds |> List.exists deferred.Contains then Some (Error "an operand is a deferred map (formalism 10.2: it has no storage to share)")
                     elif not (info.ArrayTypes |> List.forall (fun at -> denseLiteral at && (staticExtents at |> List.map Option.get) = outExt)) then
                         Some (Error "an operand's extents differ from the output's (the flat sweep needs one cell space)")
                     elif k.Params.Length <> arrays.Length then Some (Error "the kernel's arity differs from its operands")
                     elif ompOf k && outExt.Length > 1 then Some (Error "a threaded rank >= 2 map (v1 fuses a thread licence on rank 1 only: a partial licence keeps the nest)")
                     else
                     match kernelOk () with
                     | Error why -> Some (Error why)
                     | Ok () ->
                     match Blade.IRTotality.auditApply resolve (fun _ -> None) deferredW info with
                     | Error why -> Some (Error $"the kernel is not proved total ({why}): interleaving the sweeps would move its first failing cell")
                     | Ok () when opIds |> List.exists modul.ProviderReads.ContainsKey -> Some (Error "an input is a provider read (a streamed source has no pool to share)")
                     | Ok () -> Some (Ok (SibFlat cells, 0L, cells, Set.ofList opIds, ompOf k, k))
             | _ -> None)
        | _ -> None
    // ---- one spine -------------------------------------------------------------
    // `stmts`: (id, name, value) in order; `after`: everything that runs after
    // the spine's last statement and may read it (the tail, the print list).
    let processSpine (region: string) (stmts: (IRId * string * IRExpr) array) (laterReaders: IRId -> int -> bool) =
        let n = stmts.Length
        let payload (i: int) = let (id, _, v) = stmts.[i] in payloadOf id v
        let reach = Array.init n (fun i -> lazy (reachRefs (payload i)))
        let classified = stmts |> Array.map (fun (id, _, v) -> classify id v)
        let finalize (members: (int * (SiblingMapKind * int64 * int64 * Set<IRId> * bool * IRCallable)) list) =
            // drop members nothing reads after the last one (an unread output
            // could be released before the sweep that writes it)
            let lastIdx = members |> List.map fst |> List.max
            let readLater (i: int) = let (id, _, _) = stmts.[i] in laterReaders id lastIdx
            let kept = members |> List.filter (fun (i, _) -> readLater i)
            for (i, _) in members do
                if not (readLater i) then
                    let (_, nm, _) = stmts.[i]
                    recordSiblingMaps $"{region}.{nm}" (Blade.Effects.Declined "nothing reads it after the last sibling (an unread output may be released before a moved sweep writes it)") []
            match kept with
            | _ :: _ :: _ ->
                let firstI = kept |> List.head |> fst
                let lastI = kept |> List.last |> fst
                let ids = kept |> List.map (fun (i, _) -> let (id, _, _) = stmts.[i] in id)
                let names = kept |> List.map (fun (i, _) -> let (_, nm, _) = stmts.[i] in nm)
                let subject = $"""{region}: {String.concat ", " names}"""
                let delayedInputs = kept |> List.take (kept.Length - 1) |> List.map (fun (_, (_, _, _, ins, _, _)) -> ins) |> Set.unionMany
                // scratch pool reuse decided before this plan: nothing in the
                // interval (the last member included) may take over storage
                let intervalIds = [ firstI + 1 .. lastI ] |> List.map (fun i -> let (id, _, _) = stmts.[i] in id)
                let reuserInside = intervalIds |> List.tryFind reusers.Contains
                let donatedInput = delayedInputs |> Set.toList |> List.tryFind (fun d -> donorsOf.Contains d || returnDonors.Contains d)
                match reuserInside, donatedInput with
                | Some r, _ ->
                    recordSiblingMaps subject (Blade.Effects.Declined $"scratch pool reuse hands a pool to __v{r} inside the group's interval (a moved sweep could read storage that changed hands)") []
                | _, Some d ->
                    recordSiblingMaps subject (Blade.Effects.Declined $"an input of an earlier sibling (__v{d}) donates its pool to scratch reuse (the moved sweep reads it later)") []
                | None, None ->
                let (kind, _, _, _, omp, _) = snd kept.Head
                let starts = kept |> List.map (fun (_, (_, s, _, _, _, _)) -> s)
                let exts = kept |> List.map (fun (_, (_, _, e, _, _, _)) -> e)
                let inputs = kept |> List.map (fun (_, (_, _, _, ins, _, _)) -> ins) |> Set.unionMany
                let kindText =
                    match kind with
                    | SibFlat cells -> $"one flat sweep of {cells} cells"
                    | SibHalo (inner, baseN) ->
                        let lo = List.min starts
                        let hi = List.map2 (+) starts exts |> List.max
                        let axis = if inner = "" then "anonymous" else inner
                        $"one halo walk over the {axis} axis ({baseN} cells), centres [{lo}, {hi}) peeled into segments by member domain"
                    | SibHaloStream (inner, baseN, src) ->
                        let axis = if inner = "" then "anonymous" else inner
                        $"ONE segment run loop over the streamed {axis} axis ({baseN} cells) of __v{src}: one window, one chunk cache, one read and BL8015 scan per run, every member's kernel per run (identical halo geometry)"
                let namesText = String.concat ", " names
                let inputsText = inputs |> Set.toList |> List.map (fun i -> "__v" + string i) |> String.concat ", "
                let ev =
                    [ $"{kept.Length} sibling outputs {{{namesText}}} share {kindText}; inputs {{{inputsText}}} read once per sweep instead of once per output"
                      "every output stays stored (its own allocation, extents and prechecks at its own site, in source order); each cell is its own kernel's arithmetic at its own coordinate"
                      "fused at the LAST sibling: the earlier siblings' loops move down to it (allocation positions, and so the peak, unchanged)"
                      "kernels closed-total (IRTotality whitelist; a halo kernel's window reads by the window certificate, guarded by each member's own BL8009 check) and write-free (scheduleMayWriteIn with callbacks): no cell fails, so no failure moves"
                      $"interval ({lastI - firstI - 1} intervening statement(s) and every later sibling): no write, provider effect or lifetime barrier, no read of an earlier sibling directly or through a reachable callable"
                      "no storage changes hands before the sweep: no scratch pool reuse inside the interval, no donor among the moved inputs, members and moved inputs protected from the early pool-release schedule"
                      (if omp then "every sibling requests the thread licence: the sweep's shared segment carries one team" else "no sibling requests a thread licence")
                      Blade.CostModel.legalityLine "sibling-map fusion (values: each cell its own kernel over unchanged inputs; failure order: closed-total kernels, prechecks and allocations at their own sites; observation: nothing reads, frees, donates or releases a member or a moved input before the sweep; folds: none move)" ]
                    @ (match kind with
                       | SibHaloStream _ ->
                           [ "streamed source (E1 / G106): the moved run loops CAN fail (a short chunk, the per-window BL8015 scan), so every intervening statement is closed-total (IRTotality with the deferred-value predicate) and no provider read binding sits in the interval: no failure is reordered against a moved read; the fused loop reads the SAME window sequence (identical geometry: equal reach, runs, window capacity), so the first failing window is the same window"
                             "run record: ONE provider_io site for the group (its members joined with '+') instead of one per member; a failing read leaves no per-member row (the designated difference)" ]
                       | _ -> [])
                if not enabled then recordSiblingMaps subject (Blade.Effects.Declined "disabled by BLADE_SIBLING_MAPS") ev
                else
                    let g = { GroupId = SiblingMapTable.nextId (); Region = region; Members = ids; Names = names
                              Kind = kind; Starts = starts; Extents = exts; Omp = omp; Evidence = ev
                              Applies = kept |> List.map (fun (i, _) -> match stmts.[i] with (_, _, IRCompute (IRApplyCombinator info)) -> info | _ -> failwith "sibling-maps: a member is not a forced apply") }
                    SiblingMapTable.record g (Set.union (Set.ofList ids) delayedInputs)
                    recordSiblingMaps subject Blade.Effects.Applied ev
            | _ -> ()
        // member-level declines are recorded only for a stored map that shares an
        // input with another candidate of the spine (a group it could have joined)
        let pendingDecline = System.Collections.Generic.List<int * string>()
        let rawInputs (i: int) : Set<IRId> =
            let (_, _, v) = stmts.[i]
            match v with
            | IRCompute (IRApplyCombinator info) ->
                let ops = info.Arrays |> List.choose (function IRVar (a, _) -> Some a | _ -> None) |> Set.ofList
                let caps = match resolve info.Kernel with Some k -> k.Captures |> List.map (fun c -> c.Id) |> Set.ofList | None -> Set.empty
                Set.union ops caps
            | _ -> Set.empty
        // greedy grouping in spine order
        let mutable group : (int * (SiblingMapKind * int64 * int64 * Set<IRId> * bool * IRCallable)) list = []
        let close () =
            if group.Length >= 2 then finalize (List.rev group)
            group <- []
        let memberIds () = group |> List.map (fun (i, _) -> let (id, _, _) = stmts.[i] in id) |> Set.ofList
        let streams (extra: Set<IRId>) =
            (group |> List.map (fun (_, (_, _, _, ins, _, _)) -> ins) |> Set.unionMany |> Set.union extra).Count + group.Length + 1
        // E1 (G106): a streamed group's moved run loops can fail, so an
        // intervening NON-member statement must not fail (closed-total under
        // the deferred-value predicate) and must not be a provider read (a
        // read binding lowers to a unit literal yet can exit at its site)
        let streamGroup () = match group with (_, ((SibHaloStream _), _, _, _, _, _)) :: _ -> true | _ -> false
        let streamIntervalFails (id: IRId) (p: IRExpr) =
            modul.ProviderReads.ContainsKey id
            // Codex review E1-1: a non-scalar statement may ALLOCATE (a string, an
            // assignable array copy) -- its BL8005 would overtake the moved read
            || (match (try Blade.IRTotality.scalarElem (typeOf p) with _ -> None) with
                | Some et -> not (Blade.IRTotality.isReal et || et = ETBool)
                | None -> true)
            // wave 6 lane b, Codex R0 round (continuation d6837177): the
            // program-wide classifier (deferredW), not only this module's
            // census -- an imported deferred map forced by an interval
            // statement could fail (BL8013) ahead of the moved reads
            || (match Blade.IRTotality.walk resolve (fun _ -> None) (fun d -> deferredW d || moduleDeferred.Contains d)
                        Map.empty Map.empty Set.empty Set.empty Set.empty p with
                | Ok _ -> false
                | Error _ -> true)
        for i in 0 .. n - 1 do
            let (id, nm, _) = stmts.[i]
            let p = payload i
            let intervalBreak () =
                writes p || intervalHasProviderEffect modul id || barrier p
                || not (Set.isEmpty (Set.intersect (reach.[i].Force ()) (memberIds ())))
            let nonMemberBreak () =
                intervalBreak () || (streamGroup () && streamIntervalFails id p)
            match classified.[i] with
            | Some (Ok ((SibFlat _, _, _, _, _, _))) when (HotRowTable.tryProducer id).IsSome ->
                // a flat producer's cell epilogue (B1) / hot-row schedule is
                // realized by both emitters on the flat path: it owns the member
                pendingDecline.Add((i, "claimed by the hot-row / cell-epilogue plan (it folds inside this producer's own flat sweep in both lanes)"))
                if nonMemberBreak () then close ()
            | Some (Ok ((kind, st, ex, ins, omp, kk) as facts)) when (claimedBy id).IsNone ->
                match group with
                | [] -> group <- [ (i, facts) ]
                | _ ->
                    let (hkind, hst, hex, hins, homp, hk) = snd (List.last group)
                    // a streamed group shares ONE window: identical geometry
                    let geometryOk = match kind with SibHaloStream _ -> st = hst && ex = hex | _ -> true
                    if kind = hkind && not geometryOk then
                        recordSiblingMaps $"{region}.{nm}" (Blade.Effects.Declined "its halo geometry differs from the streamed group's (v1 shares one window: equal reach and centres)") []
                    let compatible =
                        kind = hkind && geometryOk && omp = homp && licenceOf kk = licenceOf hk && not (Set.isEmpty (Set.intersect ins hins))
                        && group.Length < 8 && streams ins <= 16
                    if compatible && not (intervalBreak ()) then group <- (i, facts) :: group
                    else
                        if kind = hkind && geometryOk && not (Set.isEmpty (Set.intersect ins hins)) && not compatible then
                            recordSiblingMaps $"{region}.{nm}"
                                (Blade.Effects.Declined
                                    (if omp <> homp || licenceOf kk <> licenceOf hk then "its thread licence differs from the group's (v1 shares one team only when every sibling requests it the same way)"
                                     elif group.Length >= 8 then "the group already holds 8 outputs (register-pressure cap)"
                                     else "the group's input and output streams would exceed 16 (the stream cap)")) []
                        close ()
                        group <- [ (i, facts) ]
            | Some (Error why) ->
                pendingDecline.Add((i, why))
                if nonMemberBreak () then close ()
            | Some (Ok _) ->
                pendingDecline.Add((i, $"claimed by {(claimedBy id).Value}"))
                if nonMemberBreak () then close ()
            | None ->
                if nonMemberBreak () then close ()
        close ()
        let okInputs =
            [ for j in 0 .. n - 1 do
                match classified.[j] with
                | Some (Ok _) -> yield (j, rawInputs j)
                | _ -> () ]
        for (i, why) in pendingDecline do
            let mine = rawInputs i
            if okInputs |> List.exists (fun (j, ins) -> j <> i && not (Set.isEmpty (Set.intersect mine ins))) then
                let (_, nm, _) = stmts.[i]
                recordSiblingMaps $"{region}.{nm}" (Blade.Effects.Declined why) []
    // ---- the regions -----------------------------------------------------------------
    for f in modul.Functions do
        if not (f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || hasTypeVarsInSignature f) then
            let rec spine (e: IRExpr) acc =
                match e with
                | IRLet (id, v, rest) -> spine rest ((id, $"__v{id}", v) :: acc)
                | tail -> (List.rev acc |> Array.ofList, tail)
            let (stmts, tail) = spine f.Body []
            if stmts.Length >= 2 then
                let tailRefs = lazy (reachRefs tail)
                let laterReaders (id: IRId) (lastIdx: int) =
                    (tailRefs.Force ()).Contains id
                    || [ lastIdx + 1 .. stmts.Length - 1 ] |> List.exists (fun j ->
                        let (sid, _, sv) = stmts.[j]
                        (reachRefs (payloadOf sid sv)).Contains id)
                let region = if System.String.IsNullOrEmpty f.SourceName then f.Name else f.SourceName
                processSpine region stmts laterReaders
    if singleModule && modul.Bindings.Length >= 2 then
        let stmts = modul.Bindings |> List.map (fun b -> (b.Id, b.Name, b.Value)) |> Array.ofList
        let printSel =
            Blade.PrintSelection.names ()
        let byId = modul.Bindings |> List.map (fun b -> (b.Id, b.Name)) |> Map.ofList
        let laterReaders (id: IRId) (lastIdx: int) =
            // a top-level binding is printed at the end unless a print
            // selection leaves it out
            (match printSel, Map.tryFind id byId with
             | None, Some _ -> true
             | Some sel, Some nm -> Blade.PrintSelection.mayName sel nm
             | _ -> false)
            || [ lastIdx + 1 .. stmts.Length - 1 ] |> List.exists (fun j ->
                let (sid, _, sv) = stmts.[j]
                (reachRefs (payloadOf sid sv)).Contains id)
        processSpine "top level" stmts laterReaders
