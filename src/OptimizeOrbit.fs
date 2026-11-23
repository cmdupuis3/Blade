// The five-party audits of the row-owned pull scatter and the row orbit action (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeOrbit

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage

// --- The row-owned pull scatter's five-party audit (round 3, D-P3) ---------
//
// The C++ lane's rank-3 ordered orbit scatter may run its LEAF stage on a
// thread team (CodeGenBinding.genOrbitPullCpp; the ownership proof is in
// blade_orbit_scatter.hpp leaf3_pull). Nothing is MOVED across bindings: the
// pool is a STORED array materialized before the fold, and the team evaluates
// only the fold's own pure, total sub-computations (OrbitFold.planStrict's
// closed whitelist). The admission still audits every party with the shared
// transitive write analysis (builder preamble rule 4) -- this planner records
// what the IR shows; the codegen side adds the callback-aware analysis of the
// team's bodies, the request gate and the knobs:
//   producer    -- the pool's binding, by its COMPLETE payload
//                  (intervalPayloadOf): no write, no provider effect. It ran
//                  to completion at its own site and is neither re-run nor
//                  moved;
//   consumer    -- the walk (`comp`: every level's map kernel, every inner
//                  level's fold kernel and seed, the cell function): no write;
//   outer       -- the outer fold kernel: no write;
//   seed        -- the outer seed: no write;
//   intervening -- every binding between the pool's binding and the fold (by
//                  its payload: side-table operands and a provider write's
//                  source included): no write, no provider effect.
// A writing party DECLINES (the walk's other strict forms). A pool bound
// OUTSIDE the fold's chain -- a parameter of the enclosing function, or a
// top-level binding ahead of the enclosing binding (a block's let chain) --
// was materialized before the chain ran: a parameter has no producer in this
// scope, a top-level binding is audited by its payload, and every statement
// between it and the fold is intervening. Recorded per (consumer, source); a
// walk with no record (any other placement) DECLINES: unknown = refuse.
module OrbitPullTable =
    open System.Threading
    type private State = { Audits: Map<IRId * IRId, Result<string list, string>> }
    let private empty = { Audits = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (consumer: IRId) (source: IRId) (audit: Result<string list, string>) =
        cell.Value <- { Audits = Map.add (consumer, source) audit (get ()).Audits }
    /// Ok (the planner's matrix lines) or Error (the first writing party);
    /// None = no audit for this (walk, pool).
    let tryAudit (consumer: IRId) (source: IRId) : Result<string list, string> option =
        Map.tryFind (consumer, source) (get ()).Audits

/// Plan the pull scatter's audits over the same three scopes as the compact
/// row classes: function-body let chains, the top-level binding sequence, and
/// the let chain nested inside a top-level binding's value (comoment3's
/// `let (checksum, input_checksum) = { let x ..; let t ..; let ss .. }`).
/// Records facts only (no IR change, no decision record: the emitter records
/// the outcome); no gate of its own (the emitter's BLADE_ORBIT_PULL decides).
let planOrbitPull (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let lifetimeBarrier = scheduleHasLifetimeBarrierIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    let providerEffect = intervalHasProviderEffect modul
    let mayWrite (e: IRExpr) = writes e || (facts e).MayWrite
    let bindingTypes = modul.Bindings |> List.map (fun b -> (b.Id, b.Type)) |> Map.ofList
    // every id `e` reads, directly or through a callable it names (a lifted
    // kernel lambda reads its array by id inside its own body)
    let readsOf (e: IRExpr) : Set<IRId> =
        let visited = System.Collections.Generic.HashSet<IRId>()
        let acc = System.Collections.Generic.HashSet<IRId>()
        let rec go (e: IRExpr) =
            for id in collectVarRefsIR e do
                acc.Add id |> ignore
                match Map.tryFind id funcs with
                | Some cl when visited.Add id -> go cl.Body
                | _ -> ()
        go e
        Set.ofSeq acc
    let isSymPool (t: IRType) =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            (match at.IndexTypes with
             | [ ix ] -> ix.Symmetry = SymSymmetric && ix.Rank >= 2
             | _ -> false)
        | _ -> false
    let typeOfItem (id: IRId) (v: IRExpr) =
        match Map.tryFind id bindingTypes with
        | Some t -> t
        | None -> (try typeOf v with _ -> IRTUnit)
    let walkOf (v: IRExpr) =
        match v with
        | IRReduceCompute ((IRApplyCombinator oinfo as comp), k, seed) ->
            (match oinfo.Arrays with
             | [ IRRange ([ _ ], None) ] -> Some (comp, k, seed)
             | _ -> None)
        | _ -> None
    let walkWhy (comp: IRExpr) (k: IRExpr) (seed: IRExpr) =
        if mayWrite comp then Some "the walk (consumer: every level's map kernel, an inner level's fold kernel or seed, the cell function) may write (shared transitive write analysis)"
        elif mayWrite k then Some "the outer fold kernel may write (shared transitive write analysis)"
        elif mayWrite seed then Some "the outer seed may write (shared transitive write analysis)"
        else None
    let interveningWhy (poolName: string) (items: (IRId * string * IRExpr) list) =
        items |> List.tryPick (fun (iId, iName, iVal0) ->
            let iVal = payloadOf iId iVal0
            if lifetimeBarrier iVal then
                Some $"`{iName}` is an inline lifetime boundary between the pool and the fold"
            elif mayWrite iVal || providerEffect iId then
                Some $"`{iName}` may write between `{poolName}`'s materialization and the fold (an assignment, a mutating call or a provider write -- its side-table payload included)"
            else None)
    let matrix (poolName: string) (producer: string) (between: int) =
        [ "five-party audit (planner: Optimize.scheduleMayWriteIn over the module + cseFactsOf, intervening payloads by intervalPayloadOf): "
          + $"producer = `{poolName}` {producer}; "
          + "consumer = the walk (every level's map kernel, every inner level's fold kernel and seed, the cell function) -- no write; "
          + "outer kernel = the outer fold kernel -- no write; "
          + "seeds = the outer seed -- no write (the inner seeds are the consumer's); "
          + $"intervening = {between} binding(s) between the pool's materialization and the fold -- no write, no provider effect (none is moved: the pull reorders only the fold's own sub-computations)" ]
    let audit (poolName: string) (producerWhy: string option) (producer: string)
              (comp: IRExpr) (k: IRExpr) (seed: IRExpr) (between: (IRId * string * IRExpr) list) =
        match producerWhy with
        | Some why -> Error why
        | None ->
        match walkWhy comp k seed with
        | Some why -> Error why
        | None ->
        match interveningWhy poolName between with
        | Some why -> Error why
        | None -> Ok (matrix poolName producer between.Length)
    let stored = "STORED at its own binding before the fold (its payload write-free; it ran to completion and is neither re-run nor moved)"
    let producerWhyOf (sId: IRId) (sName: string) (sVal: IRExpr) =
        let pv = payloadOf sId sVal
        if mayWrite pv || providerEffect sId then
            Some $"the pool's producer `{sName}` may write (its complete payload; shared transitive write analysis)"
        else None
    /// `outer`: pools dominating the chain -- (id, name, producer decline,
    /// producer phrase, the statements between it and the chain).
    let planChain (outer: (IRId * string * string option * string * (IRId * string * IRExpr) list) list)
                  (items: (IRId * string * IRExpr) list) =
        let arr = List.toArray items
        for ci in 0 .. arr.Length - 1 do
            let (cId, _, cVal) = arr.[ci]
            match walkOf cVal with
            | None -> ()
            | Some (comp, k, seed) ->
                let reads = readsOf comp
                for si in 0 .. ci - 1 do
                    let (sId, sName, sVal) = arr.[si]
                    if reads.Contains sId && isSymPool (typeOfItem sId sVal) then
                        let between = [ for i in si + 1 .. ci - 1 -> arr.[i] ]
                        OrbitPullTable.record cId sId
                            (audit sName (producerWhyOf sId sName sVal) stored comp k seed between)
                for (sId, sName, pWhy, producer, ahead) in outer do
                    if reads.Contains sId then
                        let between = ahead @ [ for i in 0 .. ci - 1 -> arr.[i] ]
                        OrbitPullTable.record cId sId (audit sName pWhy producer comp k seed between)
    let rec chain (e: IRExpr) : (IRId * string * IRExpr) list =
        match e with
        | IRLet (id, v, body) -> (id, $"__v{id}", v) :: chain body
        | _ -> []
    for f in modul.Functions do
        let pools =
            f.Params |> List.filter (fun p -> isSymPool p.Type)
            |> List.map (fun p -> (p.VarId, p.Name, None, "a parameter of the enclosing function (materialized by the caller; no producer in this scope)", []))
        planChain pools (chain f.Body)
    let top = modul.Bindings |> List.map (fun b -> (b.Id, b.Name, b.Value))
    planChain [] top
    let bArr = List.toArray modul.Bindings
    let topArr = List.toArray top
    bArr |> Array.iteri (fun bi b ->
        match b.Value with
        | IRLet _ ->
            let pools =
                [ for j in 0 .. bi - 1 do
                    let pb = bArr.[j]
                    if isSymPool pb.Type then
                        let ahead = [ for i in j + 1 .. bi - 1 -> topArr.[i] ]
                        yield (pb.Id, pb.Name, producerWhyOf pb.Id pb.Name pb.Value, stored, ahead) ]
            planChain pools (chain b.Value)
        | _ -> ())

// --- The row orbit action's five-party audit (round 3, D-P2; gap G73) -------
//
// A ROW ACTION `u = method_for(range<I>) <@> lambda(i) -> reduce(method_for(
// range<I>) <@> lambda(j) -> g(S(i, j), V(i), V(j), ..), k, s) |> compute`
// over a STORED SymIdx<2, n> pool S may be emitted as ONE pass over S's
// canonical cells feeding every row chain its source sequence
// (OrbitFold.planAction; blade_orbit_scatter.hpp tri_action;
// EmitLlvm.emitOrbitAction). Nothing is MOVED across bindings: the action runs
// at its own site and reads S, the weights V and its free scalars there,
// exactly as the walk does; only the order of the action's OWN pure, total
// sub-computations (its row chains) is interleaved. This planner records what
// the IR shows for every TOP-LEVEL action binding (v1: the storage proof is
// `storedArrayProof`, which proves top-level bindings) -- the emitters join it
// to the recogniser's pool / weights / free scalars and decline whatever it
// cannot prove (unknown = refuse):
//   producer    -- every earlier top-level binding the action reads (the pool,
//                  each weight, each free scalar) by its COMPLETE payload
//                  (intervalPayloadOf): no write, no provider effect; an
//                  ARRAY must also be proved stored at its own site
//                  (storedArrayProof: a `|> compute`, a literal, ...; a
//                  deferred map would execute its initializer inside the
//                  action and is refused);
//   consumer    -- the action's map kernel (the whole binding value) and the
//                  inner map (the cell term g): no write;
//   fold kernel -- the inner fold kernel: no write;
//   seed        -- the inner fold seed: no write;
//   intervening -- EVERY top-level binding between the read binding and the
//                  action (by payload; a provider effect included): no write
//                  -- the conservative W check, not a conflict analysis.
// The action is serial (no team), so no parallel body exists and the callback
// table is not consulted; the emitters add the callback-aware codegen write
// analysis over the term, the kernel and the seed.
module OrbitActionTable =
    open System.Threading
    /// What the planner proved about one earlier top-level binding the
    /// action reads: its producer (`Ok` how it is stored, or the first
    /// failure) and the bindings between it and the action (`Ok` their count,
    /// or the first writer).
    type ReadAudit =
        { Name: string
          Producer: Result<string, string>
          Between: Result<int, string> }
    type Audit =
        { /// The consumer / fold-kernel / seed verdicts (None = no write), in
          /// that order of report.
          Consumer: string option
          Reads: Map<IRId, ReadAudit> }
    type private State = { Audits: Map<IRId, Audit> }
    let private empty = { Audits = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    let record (action: IRId) (audit: Audit) =
        cell.Value <- { Audits = Map.add action audit (get ()).Audits }
    /// The audit of a top-level action binding; None = the planner saw no
    /// top-level action there (a block-local or function-body action, or no
    /// action shape): the emitters decline.
    let tryAudit (action: IRId) : Audit option = Map.tryFind action (get ()).Audits

/// Plan the row orbit action's audits over the TOP-LEVEL binding sequence
/// (v1). Facts only: no IR change, no decision record (the emitters record
/// the outcome); no gate of its own (the emitters read BLADE_ORBIT_ACTIONS).
let planOrbitActions (programFuncs: IRCallable list) (modul: IRModule) : unit =
    let funcs = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let facts = cseFactsOf funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    let providerEffect = intervalHasProviderEffect modul
    let mayWrite (e: IRExpr) = writes e || (facts e).MayWrite
    let storedProof = storedArrayProof modul
    let readsOf (e: IRExpr) : Set<IRId> =
        let visited = System.Collections.Generic.HashSet<IRId>()
        let acc = System.Collections.Generic.HashSet<IRId>()
        let rec go (e: IRExpr) =
            for id in collectVarRefsIR e do
                acc.Add id |> ignore
                match Map.tryFind id funcs with
                | Some cl when visited.Add id -> go cl.Body
                | _ -> ()
        go e
        Set.ofSeq acc
    let rec peel (e: IRExpr) = match e with IRCompute i -> peel i | e -> e
    let isArray (t: IRType) = match stripUnits t with ArrayElem _ -> true | _ -> false
    let arr = List.toArray modul.Bindings
    arr |> Array.iteri (fun ci b ->
        match b.Value with
        | IRCompute _ ->
            (match peel b.Value with
             | IRApplyCombinator oinfo ->
                 (match oinfo.Arrays, oinfo.Kernel with
                  | [ IRRange ([ _ ], None) ], IRVar (kid, _) ->
                      (match Map.tryFind kid funcs with
                       | Some cl when cl.Params.Length = 1 ->
                           (match cl.Body with
                            | IRReduceCompute (comp, k, seed) ->
                                let consumer =
                                    if mayWrite b.Value || mayWrite comp then
                                        Some "the consumer (the action's map kernel or its cell term) may write (shared transitive write analysis)"
                                    elif mayWrite k then Some "the inner fold kernel may write (shared transitive write analysis)"
                                    elif mayWrite seed then Some "the inner fold seed may write (shared transitive write analysis)"
                                    else None
                                let reads = readsOf b.Value
                                let audits =
                                    [ for si in 0 .. ci - 1 do
                                        let s = arr.[si]
                                        if reads.Contains s.Id then
                                            let pv = payloadOf s.Id s.Value
                                            let producer =
                                                if mayWrite pv || providerEffect s.Id then
                                                    Error $"the producer of `{s.Name}` may write (its complete payload; shared transitive write analysis)"
                                                elif isArray s.Type then
                                                    match storedProof s with
                                                    | Ok (how, _) -> Ok $"stored at its own binding ({how})"
                                                    | Error why -> Error $"`{s.Name}` is not proved stored before the action ({why}): a deferred array would run its initializer inside the action"
                                                else Ok "a scalar binding"
                                            let between =
                                                [ for i in si + 1 .. ci - 1 -> arr.[i] ]
                                                |> List.tryPick (fun x ->
                                                    if mayWrite (payloadOf x.Id x.Value) || providerEffect x.Id then
                                                        Some $"`{x.Name}` may write between `{s.Name}` and the action (an assignment, a mutating call or a provider write -- its side-table payload included)"
                                                    else None)
                                            let ra : OrbitActionTable.ReadAudit =
                                                { Name = s.Name
                                                  Producer = producer
                                                  Between = (match between with
                                                             | Some why -> Error why
                                                             | None -> Ok (ci - si - 1)) }
                                            yield (s.Id, ra) ]
                                let audit : OrbitActionTable.Audit = { Consumer = consumer; Reads = Map.ofList audits }
                                OrbitActionTable.record b.Id audit
                            | _ -> ())
                       | _ -> ())
                  | _ -> ())
             | _ -> ())
        | _ -> ())
