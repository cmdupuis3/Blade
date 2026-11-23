// The late tree rewrites: block floating, the deferred chain to fold and the join producer (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeLateRewrites

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeComputeElision

/// BLADE_BLOCK_FLOAT=0|off keeps a let whose value is a scalar-prelude
/// block ending in a computed map as written (rule `block-float v1`, round 4
/// wave 4 B6). Unset = on.
let blockFloatEnabled () = gateOn "BLADE_BLOCK_FLOAT"

/// BLOCK FLOATING (PM47, round 4 wave 4 B6; rule `block-float v1`). In a
/// function / lambda body, `let u = { let a1 = s1; ..; check; M |> compute }`
/// -- an addressed draw's inline form (`r.uniform_at(key, stream, off, n)`:
/// operand lets, range guards, the sample map) -- becomes `let a1 = s1; ..;
/// check; let u = M |> compute`: the same statements in the same order, one
/// scope wider, so compute elision sees a single-consumer computed let (and
/// fuses the draw into its fold: no random tape). Admitted only when every
/// floated statement holds no storage (scalar / unit lets: no pool changes
/// its lifetime) and the block's value is a computed map; nothing else moves.
let floatComputedBlocksModule (modul: IRModule) : IRModule =
    if not (blockFloatEnabled ()) then modul else
    let rec chain (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, body) -> let (ls, fin) = chain body in ((id, v) :: ls, fin)
        | fin -> ([], fin)
    let scalarLet (_, v: IRExpr) = (try holdsNoStorage (typeOf v) with _ -> false)
    let floated = ResizeArray<string>()
    let rec spine (name: string) (e: IRExpr) : IRExpr =
        match e with
        | IRLet (u, (IRLet _ as blk), rest) ->
            let (ls, fin) = chain blk
            match fin with
            | IRCompute (IRApplyCombinator _) when ls |> List.forall scalarLet ->
                floated.Add name
                List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) ls (IRLet (u, fin, spine name rest))
            | _ -> IRLet (u, blk, spine name rest)
        | IRLet (id, v, rest) -> IRLet (id, v, spine name rest)
        | other -> other
    let fns = modul.Functions |> List.map (fun f -> { f with Body = spine f.Name f.Body })
    for n in floated |> Seq.distinct do
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "block-float"; Version = 1; Span = Blade.Ast.noSpan; Subject = n
              Outcome = Blade.Effects.Applied
              Evidence = [ "a let whose value is a scalar-prelude block ending in a computed map (an addressed draw's inline form) now binds the map on the body's spine, its prelude statements just before it in the same order (compute elision then sees a single-consumer computed let)" ] }
    { modul with Functions = fns }
// --- Deferred chain to fold (gap G108 (2), round 4 wave 4) --------------------
//
//     let p3 = method_for(x) <@> f          // deferred
//     let q3 = method_for(p3) <@> g         // deferred over a deferred map
//     let r3 = reduce(q3, k, s)
//
// A fold over a deferred map runs that map where it is read ("reduce over a
// deferred computation (one level)"), but a DEFERRED OPERAND of that map was
// forced into a full N-cell pool first (`r3__in<id>`) whatever g is. A
// deferred value is evaluated where it is READ (formalism 10.2), so reading
// p3's cell inside the fold's own nest instead of from a pool forced at the
// same statement moves no evaluation across anything: the rewrite turns the
// fold into the fused terminal over q3's map with p3's map as its COMPUTED
// operand (`IRReduceCompute(q3 <@> g over (p3 |> compute))`), and elementwise
// fusion splices it -- one nest, no pool. Fail closed, each a recorded
// decline under `compute-elision` (the gate is BLADE_COMPUTE_ELIDE):
//   * EVALUATION COUNT: g reads p3's cell once or twice (fusion binds a
//     twice-read operand once per cell), and p3 is read nowhere else in g's
//     map (not its kernel's captures);
//   * FAILURE SITE: every kernel that is interleaved -- f, g, the fold kernel
//     -- and the seed are closed-total (IRTotality, with the deferred-value
//     predicate), so no failure can change its place or order;
//   * ONCE-ONLY EFFECTS: nothing interleaved writes (the shared scheduling
//     write analysis over the whole fold), so no read can move across a write;
//   * SHAPE: p3 and q3 are plain dense maps / co-iterations over stored
//     arrays or ranges (literal equal extents for a co-iteration), the fold
//     rank-1 with a seed or the identity seed rule of compute elision
//     (`elidedFoldSeed`: an unseeded fold only over a literal extent >= 1).
// A chain deeper than two levels declines (p3's own operands must be stored).
let deferredChainEnabled () : bool =
    match System.Environment.GetEnvironmentVariable "BLADE_COMPUTE_ELIDE" with
    | null -> false
    | v -> v.Trim().ToLowerInvariant() = "chain"
let spliceDeferredFoldChainsModule (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    // a module holding a CUDA kernel is skipped (v1), but each candidate chain
    // RECORDS that decline (round 4 wave 9, lane A, A4: the class-closure round
    // of BLADE_COMPUTE_ELIDE=chain, Codex a8651902 -- an unused `where cuda`
    // function made an eligible chain vanish from `blade plan` with no record)
    let hasCuda = modul.Functions |> List.exists (fun f -> f.IsCudaKernel)
    let own =modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs = programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let resolve (k: IRExpr) = resolveCallableIn funcs k
    let localCallables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in modul.Functions do localCallables.[f.Id] <- f
    let resolver = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do resolver.[f.Id] <- f
    for f in modul.Functions do resolver.[f.Id] <- f
    let mayWrite = scheduleMayWriteIn programFuncs modul
    let deferredIds = IRLift.computeDeferredIds modul.Bindings
    // the totality audits' DEFERRED-VALUE predicate: the program classifier,
    // fail closed (round 4 wave 6, the class-closure round: a kernel reading an
    // IMPORTED deferred map, `H.d(i)` inside p's kernel, was certified total
    // under this module's census alone -- fusion then interleaved its failure)
    let deferredW = widenDeferred modul (modul.Functions @ programFuncs) deferredIds.Contains
    let deferredValue =
        modul.Bindings |> List.choose (fun b ->
            if Set.contains b.Id deferredIds then
                match b.Value with
                | IRApplyCombinator info -> Some (b.Id, (b.Name, info))
                | _ -> None
            else None) |> Map.ofList
    let enabled = computeElideEnabled ()
    let refsOf (x: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun m -> match m with IRVar (id, _) when id = x -> n <- n + 1 | _ -> ()) e
        n
    let staticExts (at: IRArrayType) =
        at.IndexTypes |> List.map (fun ix -> match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
    let plainOver (info: ApplyInfo) : string option =
        match tileKernelOf localCallables info with
        | None -> Some "its kernel does not resolve to a callable"
        | Some kc ->
            if not (tilePlainInfo info) then Some "it is not a plain dense map or co-iteration"
            elif not (tilePlainKernel kc info.Arrays.Length) then Some "its kernel declares symmetry, a parallel strategy or arity polymorphism"
            elif not (tilePureBody resolver Set.empty kc.Body) then Some "its kernel is not repeatable"
            elif info.Arrays.Length > 1
                 && (let exts = info.ArrayTypes |> List.map staticExts
                     exts |> List.exists (List.exists Option.isNone) || (exts |> List.distinct |> List.length) > 1) then
                Some "it co-iterates over run-time or unequal extents (its agreement check would move)"
            else
                match Blade.IRTotality.auditApply resolve (fun _ -> None) deferredW info with
                | Error why -> Some $"its kernel is not proved total ({why})"
                | Ok () -> None
    // WHO NAMES A DEFERRED BINDING (Codex review of the C2 diff, finding 1): a
    // deferred value is evaluated where it is read only until a read FORCES it
    // -- the interpreter then memoizes it and the C++ lane reads its pool -- so
    // the rewrite needs p (and q) named by no binding but the chain itself and
    // the host fold, and by no function body or capture.
    // (Codex review round 2: every route a read can take -- the payload, the
    // extents its nodes' TYPES carry (`0..p(0)`), the binding's own type, and
    // a provider write's SOURCE, which the write forces into storage)
    let refsWithExtents (e: IRExpr) =
        Set.union (collectVarRefsIR e) (embeddedExtentExprs e |> List.map collectVarRefsIR |> Set.unionMany)
    let bindingRefs =
        modul.Bindings |> List.map (fun b ->
            let payload = intervalPayloadOf modul b.Id b.Value
            let refs =
                Set.unionMany
                    [ refsWithExtents payload
                      (irTypeExtentExprs b.Type |> List.map refsWithExtents |> Set.unionMany)
                      (match Map.tryFind b.Id modul.ProviderWrites with Some w -> Set.singleton w.SourceId | None -> Set.empty) ]
            (b.Id, refs)) |> Map.ofList
    let funcRefs =
        modul.Functions |> List.fold (fun acc f ->
            Set.unionMany
                [ acc; refsWithExtents f.Body; f.Captures |> List.map (fun c -> c.Id) |> Set.ofList
                  (f.Params |> List.collect (fun p -> irTypeExtentExprs p.Type) |> List.map refsWithExtents |> Set.unionMany)
                  (irTypeExtentExprs f.RetType |> List.map refsWithExtents |> Set.unionMany) ]) Set.empty
    let namedOnlyBy (id: IRId) (allowed: Set<IRId>) =
        not (funcRefs.Contains id)
        && bindingRefs |> Map.forall (fun bid refs -> allowed.Contains bid || not (refs.Contains id))
    let rewriteFold (subject: string) (hostName: string) (hostId: IRId) (v: IRExpr) : IRExpr =
        let candidate =
            match v with
            | ReduceOfNamed (q, _, k, init) when deferredValue.ContainsKey q -> Some (deferredValue.[q], k, init)
            | IRReduceCompute (IRVar (q, _), (IRVar _ as k), s) when deferredValue.ContainsKey q -> Some (deferredValue.[q], k, Some s)
            // Lowering's spelling: the fused terminal over the deferred map's
            // own apply (its seed already the lowered one), named after the
            // deferred binding it came from when one matches
            | IRReduceCompute (IRApplyCombinator qinfo, (IRVar _ as k), s) ->
                let qName =
                    deferredValue |> Map.tryPick (fun _ (nm, info) -> if obj.ReferenceEquals(info, qinfo) || info.Kernel = qinfo.Kernel then Some nm else None)
                    |> Option.defaultValue hostName
                Some ((qName, qinfo), k, Some s)
            | _ -> None
        match candidate with
        | None -> v
        | Some ((qName, qinfo), k, init) ->
        let inner =
            qinfo.Arrays |> List.choose (fun a ->
                match a with
                | IRVar (p, _) when deferredValue.ContainsKey p -> Some p
                | _ -> None) |> List.distinct
        if inner.IsEmpty then v else
        let subj = $"{subject}.{qName}"
        let decline (why: string) =
            recordComputeElision subj (Blade.Effects.Declined $"deferred chain to fold: {why}") []
            v
        if inner.Length > 1 then decline "more than one operand of the folded map is a deferred map (v1 splices one)" else
        let p = inner.Head
        let (pName, pinfo) = deferredValue.[p]
        // q: the folded map's own deferred binding. Lowering inlines it into the
        // fold with a cloned kernel, so it is found as THE deferred binding (not
        // the host) whose map reads p as an operand -- exactly one, else decline.
        let qId =
            match deferredValue |> Map.tryPick (fun id (nm, _) -> if nm = qName then Some id else None) with
            | Some q -> Some q
            | None ->
                let readers =
                    deferredValue |> Map.toList |> List.filter (fun (id, (_, info)) ->
                        id <> hostId && info.Arrays |> List.exists (function IRVar (x, _) -> x = p | _ -> false))
                match readers with
                | [ (q, _) ] -> Some q
                | _ -> None
        let qName = match qId with Some q -> fst deferredValue.[q] | None -> qName
        let subj = $"{subject}.{qName}"
        let decline (why: string) =
            recordComputeElision subj (Blade.Effects.Declined $"deferred chain to fold: {why}") []
            v
        let chainIds = Set.ofList ([ p; hostId ] @ (qId |> Option.toList))
        if hasCuda then
            decline "the module holds a CUDA kernel (v1 leaves every deferred chain of such a module as written)"
        elif not (Blade.Toolchain.envFlag "BLADE_FUSION" true) then
            decline "elementwise fusion, the rewrite's realization, is disabled (BLADE_FUSION)"
        elif qinfo.Arrays.Length > 1 && not qinfo.IsCoIteration then
            decline $"`{qName}` is an outer product (v1 splices into a map or a co-iteration only)"
        elif qinfo.Arrays.Length - 1 + pinfo.Arrays.Length > 8 then
            decline "the fused operand count would exceed elementwise fusion's fan-in cap (8)"
        elif not (namedOnlyBy p (chainIds |> Set.remove p)) then
            decline $"`{pName}` is named outside the chain (another binding, a function body or a capture could force it earlier, and a forced deferred value is a snapshot)"
        elif (match qId with Some q -> not (namedOnlyBy q (Set.ofList [ hostId; q ])) | None -> false) then
            decline $"`{qName}` is named outside the chain (another binding, a function body or a capture could force it earlier)"
        else
        let fk = resolve k
        match plainOver qinfo with
        | Some why -> decline $"`{qName}`: {why}"
        | None ->
        match plainOver pinfo with
        | Some why -> decline $"`{pName}`: {why}"
        | None ->
        if pinfo.Arrays |> List.exists (function IRVar (id, _) -> Set.contains id deferredIds | IRRange _ -> false | _ -> true) then
            decline $"an operand of `{pName}` is not a stored array or a range (a deeper chain declines)"
        // Round 4 wave 7 (lane B, the B3 class-closure round, Codex): a RANGE
        // operand of RUN-TIME extent (`method_for(0..n) <@> ..` with n a
        // variable) has its descriptor built where p is forced; the fused
        // nest read an undeclared `__range0` (invalid C++). Only a range of
        // literal extents and offset is admitted.
        elif pinfo.Arrays |> List.exists (function
                                             | IRRange (ixs, off) ->
                                                 (ixs |> List.exists (fun ix -> match ix.Extent with IRLit (IRLitInt _) -> false | _ -> true))
                                                 || (match off with None | Some (IRLit (IRLitInt _)) -> false | Some _ -> true)
                                             | _ -> false) then
            decline $"a range operand of `{pName}` has a run-time extent or offset (its descriptor is built where `{pName}` is forced)"
        // Round 4 wave 6 (the class-closure round): POSITIVE storage evidence
        // for every array operand of p -- a binding of THIS module proved
        // stored at its own site through its alias chain (storedArrayProof).
        // An IMPORTED operand (`method_for(H.d)`, H.d a deferred map of
        // another module) is absent from this module's deferred census and
        // was taken as stored: a hidden third level, forced privately inside
        // the nest instead of persistently at the fold (a later write to its
        // input then changed a later read, its print and a BL8013).
        elif pinfo.Arrays |> List.exists (function
                                             | IRVar (id, _) ->
                                                 (match modul.Bindings |> List.tryFind (fun b -> b.Id = id) with
                                                  | Some b -> Result.isError (storedArrayProof modul b)
                                                  | None -> true)
                                             | _ -> false) then
            decline $"an operand of `{pName}` is not proved stored by this module (an imported value, or one storedArrayProof does not prove: its cells may be a deferred computation forced elsewhere)"
        else
        let pos = qinfo.Arrays |> List.findIndex (function IRVar (id, _) -> id = p | _ -> false)
        let qk = (tileKernelOf localCallables qinfo).Value
        let occ = refsOf qk.Params.[pos].VarId qk.Body
        if (qinfo.Arrays |> List.filter (function IRVar (id, _) -> id = p | _ -> false)).Length <> 1 then
            decline $"`{pName}` is read at more than one operand position of `{qName}`"
        elif occ < 1 || occ > 2 then
            decline $"`{qName}`'s kernel reads `{pName}`'s cell {occ} times (once or twice is required: fusion binds a twice-read operand once per cell)"
        elif refsOf p qinfo.Kernel > 0 || refsOf p k > 0 || (init |> Option.exists (fun s -> refsOf p s > 0)) then
            decline $"`{pName}` is read again by a kernel or the seed"
        else
        match fk with
        | None -> decline "the fold kernel does not resolve to a callable"
        | Some fkc ->
        match Blade.IRTotality.auditCallable resolve (fun _ -> None) deferredW fkc [] with
        | Error why -> decline $"the fold kernel is not proved total ({why}); interleaving could change which failure comes first"
        | Ok () ->
        let seedTotal =
            match init with
            | None -> Ok 0L
            | Some s -> Blade.IRTotality.walk resolve (fun _ -> None) deferredW Map.empty Map.empty Set.empty Set.empty Set.empty s
        match seedTotal with
        | Error why -> decline $"the seed is not proved total ({why})"
        | Ok _ ->
        if mayWrite v || mayWrite (IRApplyCombinator qinfo) || mayWrite (IRApplyCombinator pinfo) then
            decline "a kernel, the seed or a producer may write (the shared scheduling write analysis): interleaving would move reads across it"
        else
        match elidedFoldSeed false qinfo.OutputType fk init with
        | Error why -> decline why
        | Ok (seed, seedNote) ->
        let ev =
            [ $"`{pName}` is deferred: evaluated where it is read, so reading its cell in the fold's own nest instead of a pool forced at the same statement moves no evaluation"
              $"`{qName}`'s kernel reads the cell {occ} time(s) (fusion binds it once per cell)"
              "every interleaved kernel (both producers, the fold) and the seed closed-total (IRTotality) and write-free (the shared scheduling write analysis)"
              seedNote
              "realized by elementwise fusion: no N-cell pool for the inner map" ]
        if not enabled then
            recordComputeElision subj (Blade.Effects.Declined "deferred chain to fold: disabled by BLADE_COMPUTE_ELIDE") ev
            v
        // OPT-IN (the review stop rule: the second review of the forced-snapshot
        // class found two more members -- a provider write source, a read in a
        // type-carried extent -- both now in the census): only under the
        // `optimizerGateMirrors` arm BLADE_COMPUTE_ELIDE=chain
        elif not (deferredChainEnabled ()) then
            recordComputeElision subj (Blade.Effects.Declined "deferred chain to fold: opt-in (BLADE_COMPUTE_ELIDE=chain); the forced-snapshot audit is not closed by review") ev
            v
        else
            recordComputeElision subj Blade.Effects.Applied ([ $"deferred chain to fold: `{pName}` -> `{qName}` -> the fold" ] @ ev)
            let repl = IRCompute (IRApplyCombinator pinfo)
            let subst (e: IRExpr) = match e with IRVar (id, _) when id = p -> repl | e -> e
            let q' = { qinfo with Arrays = qinfo.Arrays |> List.map subst; Loop = mapIRExpr subst qinfo.Loop }
            IRReduceCompute (IRApplyCombinator q', k, seed)
    if deferredValue.IsEmpty then modul
    else { modul with Bindings = modul.Bindings |> List.map (fun b -> { b with Value = rewriteFold "top level" b.Name b.Id b.Value }) }
// ===========================================================================
// JOIN PRODUCER (round 4 wave 4, E2 / streaming plan ST04; BLADE_JOIN_PRODUCER,
// rule `join-producer v1`). An EXPENSIVE producer feeding k synchronized
// terminal branches: `let p = method_for(a) <@> f |> compute` (or S2's force)
// whose ONLY readers are the operand slots of ONE reduction join (the
// sibling-fold pass's `<&!>` join of k >= 2 fold legs over p). Each branch
// advances one cell per step in lockstep, so the region needs ONE cell of p
// at a time: p is rebound to the bare map and the join's shared leaf (both
// lanes' existing share: the producer's cell evaluated ONCE per coordinate
// and fed to every leg) replaces the N-cell pool and its separate fill.
//
// THE REGION RECORD (streaming plan 2.2): producer (its kernel and inputs),
// consumers (the join's legs), block (one cell: scalar fusion is the
// strongest legal storage), demand (k scalar terminals), storage (the pool's
// bytes removed), scheduling proof. Admission, fail closed, each a recorded
// decline: a function / lambda body (the module-level deferred protocol keeps
// its binding); p's every reference an operand slot of ONE join statement and
// no callable captures p (a sort, a keyed read, a whole-array consumer, a
// second statement -- an UNBOUNDED-LAG branch -- keeps the stored strategy);
// >= 2 legs read p; the producer a plain dense rank-1 map of literal extent
// >= 1 over STORED, non-deferred, non-streamed inputs (one level of sharing),
// closed-total (`IRTotality`, deferred-value predicate) and write-free
// (`scheduleMayWriteIn` with callbacks), no thread / device / Reynolds
// request; every statement from p's let to the join (the join included)
// write-free with no provider effect (the SNAPSHOT: p's inputs are the values
// the stored pool would have read). No failure moves (a total producer
// evaluated at the same coordinates), no fold's order, seed or licence
// changes (the legs are the join's own). The interpreter runs the IR the
// pass leaves (`test interp` sees it); `opt-diff` compares off vs on.
// ===========================================================================

/// BLADE_JOIN_PRODUCER=0|off keeps p stored (round 4 wave 4, E2 / ST04; the
/// decision still records `declined -- disabled by BLADE_JOIN_PRODUCER`).
let joinProducerEnabled () = gateOn "BLADE_JOIN_PRODUCER"

let recordJoinProducer (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "join-producer"; Version = 1; Span = Blade.Ast.noSpan
          Subject = subject; Outcome = outcome; Evidence = evidence }

let elideJoinProducersModule (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    if modul.Functions |> List.exists (fun f -> f.IsCudaKernel) then modul else
    let enabled = joinProducerEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs = programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let resolve (k: IRExpr) = resolveCallableIn funcs k
    let writes = scheduleMayWriteWithCallbacksIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    let moduleDeferred = IRLift.computeDeferredIds modul.Bindings
    // G186 (round 4 wave 6): the FAIL-CLOSED program classifier
    // (`widenDeferred`: aliases, tuple projections, let chains, streamed reads,
    // and an id bound nowhere this module shows -- another module's binding)
    // for the producer's inputs and its totality audit: a module binding's
    // block (the inlined copy below) runs under the deferred protocol, where
    // the node census alone misses deferred forms (Codex design review)
    let programDeferred = widenDeferred modul (modul.Functions @ programFuncs) moduleDeferred.Contains
    let capturedIds = funcs |> Map.fold (fun acc _ f -> f.Captures |> List.fold (fun a c -> Set.add c.Id a) acc) Set.empty
    let streamed (id: IRId) = match Map.tryFind id modul.ProviderReads with Some s -> s.Streamed | None -> false
    let isBareMap (v: IRExpr) =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _ | IRFusion _ | IRZip _ -> true
        | _ -> false
    /// The leaves of a reduction join's fused tree.
    let rec leavesOf (x: IRExpr) = match x with IRFusion (l, r) -> leavesOf l @ leavesOf r | other -> [ other ]
    /// Join operand references to `p` in a join statement: operand slots,
    /// their Loop provenance, and a leaf that is the name itself. A leaf
    /// naming p must be the plain pointwise shape the emitters' share leaf
    /// repoints (fable review F2): one plain rank-1 level per operand of p's
    /// literal extent, a co-iteration when it has several operands, every
    /// kernel input a cell. Otherwise -1 legs (not admissible).
    let joinRefs (pExtent: int64 option) (p: IRId) (v: IRExpr) : int * int =
        match v with
        | IRReduceCompute (comp, IRTuple ks, IRTuple ss) when ks.Length = ss.Length && ks.Length >= 2 ->
            let mutable refs = 0
            let mutable legs = 0
            let rec bumpVars x =
                match x with
                | IRVar (id, _) when id = p -> refs <- refs + 1
                | ExprShape (children, _) -> children |> List.iter bumpVars
            for leaf in leavesOf comp do
                match leaf with
                | IRApplyCombinator info ->
                    let before = refs
                    bumpVars info.Loop
                    for a in info.Arrays do
                        match a with
                        | IRVar (id, _) when id = p -> refs <- refs + 1
                        | _ -> ()
                    if refs > before then
                        let plainSlot (at: IRArrayType) =
                            not at.IsVirtual
                            && (match at.IndexTypes with
                                | [ ix ] -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 && ix.Dependencies.IsEmpty
                                            && (match ix.Extent, pExtent with IRLit (IRLitInt n), Some m -> n = m | _ -> false)
                                | _ -> false)
                        let shapeOk =
                            tilePlainInfo info && not info.Arrays.IsEmpty && info.Arrays.Length = info.ArrayTypes.Length
                            && (info.Arrays.Length = 1 || info.IsCoIteration)
                            && info.ArrayTypes |> List.forall plainSlot
                            && info.KernelInputRanks |> List.forall (fun r -> r = 0)
                            && not info.HasReynolds && info.KernelOutputRank = 0
                        if shapeOk && legs >= 0 then legs <- legs + 1 else legs <- -1
                | IRVar (id, _) when id = p -> refs <- refs + 1; (if legs >= 0 then legs <- legs + 1)
                | _ -> ()
            (refs, legs)
        | _ -> (0, 0)
    let countRefs (p: IRId) (e: IRExpr) =
        let mutable c = 0
        iterIRExpr (fun n -> match n with IRVar (id, _) when id = p -> c <- c + 1 | _ -> ()) e
        c
    let processBody (note: string option) (region: string) (body: IRExpr) : IRExpr =
        let recordJoinProducer subject outcome (ev: string list) = recordJoinProducer subject outcome (ev @ Option.toList note)
        let rec spine (e: IRExpr) acc =
            match e with
            | IRLet (id, v, rest) -> spine rest ((id, v) :: acc)
            | tail -> (List.rev acc |> Array.ofList, tail)
        let (stmts, tail) = spine body []
        if stmts.Length < 2 then body else
        let localDeferred = stmts |> Array.choose (fun (id, v) -> if isBareMap v then Some id else None) |> Set.ofArray
        let isDeferred (id: IRId) = localDeferred.Contains id || programDeferred id
        let rewrites = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        let extentExprs = lazy (embeddedExtentExprs body)
        for i in 0 .. stmts.Length - 1 do
            match stmts.[i] with
            | (p, IRCompute (IRApplyCombinator info)) ->
                let outAt = match stripUnits info.OutputType with ArrayElem at -> Some at | _ -> None
                let literalRank1 =
                    match outAt with
                    | Some at when not at.IsVirtual ->
                        (match at.IndexTypes with
                         | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 && ix.Dependencies.IsEmpty ->
                             (match ix.Extent with IRLit (IRLitInt n) when n >= 1L -> Some n | _ -> None)
                         | _ -> None)
                    | _ -> None
                // a reference in a lifetime region's RELEASE list (the inlined
                // copy's pool, listed for its free) is not a reader: elided, p
                // owns no pool and leaves the list (G186)
                let releaseRefs =
                    let mutable c = 0
                    iterIRExpr (fun n ->
                        match n with
                        | IRReleasePools (_, pools, _) -> for e in pools do c <- c + countRefs p e
                        | _ -> ()) body
                    c
                let total = countRefs p body - releaseRefs
                let joins =
                    [ for j in i + 1 .. stmts.Length - 1 do
                        let (jid, jv) = stmts.[j]
                        let (r, legs) = joinRefs literalRank1 p jv
                        if r > 0 then yield (j, jid, r, legs) ]
                let (tailJoinRefs, _) = joinRefs literalRank1 p tail
                // the source name a map's array identity carries, for the record
                let mutable pName : string option = None
                iterIRExpr (fun n ->
                    match n with
                    | IRApplyCombinator li when pName.IsNone && li.Arrays.Length = li.Identities.Length ->
                        List.iter2 (fun a ident ->
                            match a, ident with
                            | IRVar (aid, _), AIDVariable nm when aid = p && not (nm.StartsWith "__") -> pName <- Some nm
                            | _ -> ()) li.Arrays li.Identities
                    | _ -> ()) body
                let shown = match pName with Some nm -> nm | None -> "__v" + string p
                let subject = $"{region}.{shown}"
                let decline (why: string) = recordJoinProducer subject (Blade.Effects.Declined why) []
                if joins.IsEmpty && tailJoinRefs > 0 then
                    decline "the join that reads it is the body's return value, not a statement of the spine (v1)"
                // only a producer some join reads is a candidate (silent otherwise)
                if not joins.IsEmpty then
                    let (j, jid, r, legs) = joins.Head
                    let k = resolve info.Kernel
                    let inputIds = info.Arrays |> List.choose (function IRVar (a, _) -> Some a | _ -> None)
                    let extentUses = extentExprs.Force () |> List.sumBy (countRefs p)
                    if joins.Length > 1 then
                        decline $"{joins.Length} join statements read it (each would evaluate the producer: one synchronized region only)"
                    elif total <> r || capturedIds.Contains p || extentUses > 0 then
                        decline "a consumer is not a synchronized fold leg of the join (a whole-array, keyed or later read, a capture, an extent a type carries -- an unbounded-lag branch keeps the stored strategy)"
                    elif legs < 0 then decline "a join leg reads it through a non-pointwise or non-plain slot (the share leaf repoints plain rank-1 cells only)"
                    elif legs < 2 then decline "one join leg reads it (a single consumer is compute elision's)"
                    elif literalRank1.IsNone then decline "the producer is not a plain dense rank-1 map of literal extent >= 1 (v1)"
                    elif info.HasReynolds || info.KernelOutputRank <> 0 || not info.KernelTDims.IsEmpty then
                        decline "the producer's kernel is a Reynolds / rank-raising / tdim kernel"
                    elif inputIds.Length <> info.Arrays.Length then decline "a producer operand is not a named stored array (a range, a view or a computed map)"
                    elif inputIds |> List.exists isDeferred then decline "a producer operand is a deferred map (one level of sharing only)"
                    elif inputIds |> List.exists streamed then decline "a producer operand is a streamed provider read"
                    // Codex review E2-1: the share leaf repoints every consumer slot at the
                    // producer's FIRST operand, so every operand must supply the producer's one
                    // pointwise axis (a whole-array argument would leave a leg with no level)
                    elif not (tilePlainInfo info && (info.Arrays.Length = 1 || info.IsCoIteration)
                              && info.Arrays.Length = info.ArrayTypes.Length
                              && info.KernelInputRanks |> List.forall (fun r -> r = 0)
                              && info.ArrayTypes |> List.forall (fun at ->
                                    not at.IsVirtual
                                    && (match at.IndexTypes with
                                        | [ ix ] -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 && ix.Dependencies.IsEmpty
                                                    && (match ix.Extent with IRLit (IRLitInt e) -> Some e = literalRank1 | _ -> false)
                                        | _ -> false))) then
                        decline "the producer is not a pointwise co-iteration whose every operand supplies its one rank-1 axis (a whole-array or mixed-rank argument: the share leaf repoints the legs at its first operand)"
                    else
                    match k with
                    | None -> decline "the producer's kernel does not resolve"
                    | Some kc when kc.IsCudaKernel || kc.IsMpiParallel || kc.IsOmpParallel || not kc.Parallelism.IsEmpty || kc.IsStatic ->
                        decline "the producer requests a thread, device or MPI strategy (v1 keeps its own traversal)"
                    | Some _ ->
                    match Blade.IRTotality.auditApply resolve (fun _ -> None) isDeferred info with
                    | Error why -> decline $"the producer is not proved total ({why}): its first failing cell would move into the join"
                    | Ok () ->
                    if writes (IRApplyCombinator info) then decline "the producer may write (its writes would move into the join)"
                    else
                    let interval = [ i + 1 .. j ]
                    // the extents an intervening statement's TYPES carry run too
                    // (`method_for(0..bump(x))`: a mutating call in a range bound;
                    // Codex diff review of G186) -- audited like its payload
                    match interval |> List.tryFind (fun t -> let (tid, tv) = stmts.[t] in writes (payloadOf tid tv) || (embeddedExtentExprs tv |> List.exists writes) || intervalHasProviderEffect modul tid) with
                    | Some t -> decline $"__v{fst stmts.[t]} between the producer and the join (or the join) may write or has a provider effect (the snapshot)"
                    | None ->
                    let n = literalRank1.Value
                    let elemBytes = match outAt with Some at -> (match stripUnits at.ElemType with IRTScalar (ETFloat32 | ETInt32) -> 4L | _ -> 8L) | None -> 8L
                    let inputsText = inputIds |> List.map (fun a -> "__v" + string a) |> String.concat ", "
                    let ev =
                        [ $"region plan: producer __v{p} = map over {{{inputsText}}} (closed-total, write-free); consumers: {legs} fold legs of the join __v{jid} (synchronized: each advances one cell per step); block: ONE cell (the join's shared leaf, evaluated once per coordinate and fed to every leg); demand: {legs} scalar terminals, nothing else (no capture, keyed read, whole-array or later reader)"
                          $"storage: the {n}-cell pool ({n * elemBytes} bytes) and its separate fill pass are removed; resource bound: one cell + the legs' accumulators"
                          $"scheduling proof: {j - i - 1} intervening statement(s) and the join write nothing and carry no provider effect (snapshot); the producer is total (no failure moves) and runs at the same coordinates once each; the legs' seeds, orders and licences are the join's own"
                          "lanes: C++ and LLVM read the join's shared producer once per coordinate; the interpreter runs the IR as left"
                          Blade.CostModel.legalityLine "join-producer elision (values: each cell the producer's arithmetic once, fed to every leg; failure order: total producer; observation: the join is the only reader)" ]
                    if not enabled then recordJoinProducer subject (Blade.Effects.Declined "disabled by BLADE_JOIN_PRODUCER") ev
                    else
                        recordJoinProducer subject Blade.Effects.Applied ev
                        rewrites.[p] <- IRApplyCombinator info
            | _ -> ()
        if rewrites.Count = 0 then body
        else
            let rec rebuild (e: IRExpr) =
                match e with
                | IRLet (id, v, rest) ->
                    (match rewrites.TryGetValue id with
                     | true, v' -> IRLet (id, v', rebuild rest)
                     | _ -> IRLet (id, v, rebuild rest))
                | tail -> tail
            ignore tail
            // an elided producer owns no pool: it leaves its lifetime region's
            // release list (the region pair itself stays, even when its list
            // empties -- IRValidate derives ownership from the forced lets left)
            let isElided (e: IRExpr) = match e with IRVar (id, _) -> rewrites.ContainsKey id | _ -> false
            rebuild body
            |> mapIRExpr (fun n ->
                match n with
                | IRReleasePools (scope, pools, call) when pools |> List.exists isElided ->
                    IRReleasePools (scope, pools |> List.filter (isElided >> not), call)
                | other -> other)
    // G186 (round 4 wave 6): a MODULE BINDING whose value is a block -- the
    // site the sibling-fold pass inlines a callee into (`kernel(x, y)` in the
    // tuple of `let (checksum, input_checksum) = { .. }`) -- is planned too:
    // the inlined copy of the function's producer runs THERE, so the plan
    // must describe it (the user rule: the plan describes the executed
    // program). Its lets are locals of main in both lanes; the binding itself
    // (and a top-level `let p`) stays under the deferred protocol, unplanned.
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            match b.Value with
            | IRLet _ when not (modul.ProviderWrites.ContainsKey b.Id) && (bindingSideOperands modul b.Id).IsEmpty ->
                { b with Value = processBody None b.Name b.Value }
            | _ -> b)
    // A function no call or value reference of THIS module names any more
    // (the sibling-fold pass inlined its call sites) still carries its body
    // record; it says so, and that the inlined copy has its own record.
    let referencedHere =
        let acc = System.Collections.Generic.HashSet<IRId>()
        // every name the module's code, the extents its types carry and its
        // declared types reach (a call in a range bound is a call)
        let addExpr (e: IRExpr) =
            for id in collectVarRefsIR e do acc.Add id |> ignore
            for x in embeddedExtentExprs e do
                for id in collectVarRefsIR x do acc.Add id |> ignore
        let addTy (t: IRType) = for x in irTypeExtentExprs t do addExpr x
        for b in bindings' do
            for pl in bindingPayload modul b do addExpr pl
            addExpr b.Value
            addTy b.Type
        for f in modul.Functions do
            addExpr f.Body
            for c in f.Captures do acc.Add c.Id |> ignore
            for prm in f.Params do addTy prm.Type
            addTy f.RetType
        acc
    let functions' =
        modul.Functions |> List.map (fun f ->
            if f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || hasTypeVarsInSignature f then f
            else
                let region = if System.String.IsNullOrEmpty f.SourceName then f.Name else f.SourceName
                let note =
                    if referencedHere.Contains f.Id then None
                    else Some $"no call of `{region}` remains in this module (its code, types and extents; a call the sibling-fold pass inlined is planned at its own site, under its binding's name); another module may still call it"
                { f with Body = processBody note region f.Body })
    { modul with Functions = functions'; Bindings = bindings' }
