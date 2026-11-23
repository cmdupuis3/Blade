// Producer-input legs, rule epilogue-legs, with the call-span and inline-event side tables (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeEpilogueLegs

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis

// --- Producer-input legs (round 3 A7c, rule `epilogue-legs` v1, gap G69) -----
//
// saxpy's region after the sibling-fold join:
//
//     let y2 = method_for(zip(x, y)) <@> k |> compute          (named, stored)
//     let s  = reduce(method_for(y2) <@> lambda(w) -> w * w, F, 0.0)   (B1)
//     let j  = reduce(id <@> x <&!> id <@> y, ((+), (+)), (-0.0, -0.0))
//     (s, j[0] + j[1])
//
// walks x and y twice: once in y2's producer loop (which B1 already fuses with
// s), once in j's joined pass. The legs of j fold EXACTLY the producer's input
// operands, so the producer loop can carry them as extra fold state, fed by
// the values it already loads: a STRICT leg as its own ascending chain from its
// own seed (one step per cell, the producer's order), a LICENSED leg (its OWN
// reorder licence -- never the map's, never another leg's, G7 policy) as its
// own K-lane state. Each leg's result is bound where the join was; the combine
// and everything downstream stay at their original points.
//
// The side table below is the whole interface: the IR keeps the join (the
// interpreter runs it as written), the producer's emitter carries the legs
// and publishes their accumulators, the join's emitter binds its tuple from
// them -- or, when the producer did not carry them in this lane and build,
// emits the ordinary joined pass. The admission is
// `producerEpilogueLegAdmission` (closed totality, dominating captures and
// seeds, operand / index equality, the five parties through the shared write
// analysis); the region is assembled by the sibling-fold pass, which -- when
// the producer sits in a callee the legs' statement calls (saxpy's
// `kernel(x, y)`) -- inlines that call one level ONLY for an admitted plan.

/// One input leg riding a producer's loop.
type EpilogueLeg = {
    /// The leg's position among the join's leaves (its tuple slot).
    Slot: int
    /// The producer operand the leg folds: its position in the producer's
    /// `Arrays` (its pool in the flat sweep) and its id.
    OperandPos: int
    OperandId: IRId
    /// The leg's fold kernel and seed, as the join carries them.
    Kernel: IRExpr
    Seed: IRExpr
    /// The leg's accumulator (result) type and the operand's element type.
    ElemType: IRType
    OperandElem: IRType
    /// The leg's OWN reorder licence in this build (planning view; each
    /// emitter re-derives it from its own predicate).
    Licensed: bool
    Name: string
    /// The LLVM lane's handshake: a fresh id the producer binds in its slot
    /// table and the join reads (never in the IR tree).
    SlotId: IRId
}

type EpilogueLegPlan = {
    ProducerId: IRId
    JoinId: IRId
    Legs: EpilogueLeg list
    ProducerName: string
    Subject: string
    /// The call the sibling pass inlined one level FOR this plan (`kernel(x,
    /// y)`), None when the producer was already in the region.
    InlinedCall: string option

    /// C++ handshake: per leg (slot order) the published name and whether it
    /// is a lane partial (true: the join applies seed + combine) or the
    /// finished chain (false). Set by the producer's flat sweep when it
    /// carried the legs, read and CLEARED by the join.
    EmittedCpp: (string * bool) list option ref
    /// An emitter recorded this plan's per-lane outcome already.
    DecidedCpp: bool ref
    DecidedLlvm: bool ref
}

module EpilogueLegTable =
    open System.Threading
    type State = { ByProducer: Map<IRId, EpilogueLegPlan>; ByJoin: Map<IRId, EpilogueLegPlan> }
    let private empty = { ByProducer = Map.empty; ByJoin = Map.empty }
    let private cell = new AsyncLocal<State>()
    let private get () = match box cell.Value with null -> empty | _ -> cell.Value
    let reset () = cell.Value <- empty
    /// False when the producer already carries another join's legs.
    let record (p: EpilogueLegPlan) : bool =
        let s = get ()
        if s.ByProducer.ContainsKey p.ProducerId || s.ByJoin.ContainsKey p.JoinId then false
        else
            cell.Value <- { ByProducer = Map.add p.ProducerId p s.ByProducer
                            ByJoin = Map.add p.JoinId p s.ByJoin }
            true
    let tryProducer (id: IRId) : EpilogueLegPlan option = Map.tryFind id (get ()).ByProducer
    let tryJoin (id: IRId) : EpilogueLegPlan option = Map.tryFind id (get ()).ByJoin

/// Source spans of user calls (W2-4 round 9, BL7005): Lowering records each
/// `f(args)` it lowers to an IRApp, keyed by the callee and its argument
/// atoms, so the sibling pass can name the span of the call it inlined for
/// legs. A lookup that does not resolve to exactly one span gives noSpan (the
/// warning then still names the call by its text). Per async flow.
module CallSpanTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId * string list, Blade.Ast.Span list>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let argKey (a: IRExpr) = match a with IRVar (id, _) -> $"v{id}" | IRLit l -> sprintf "l%A" l | _ -> "?"
    let record (fid: IRId) (args: IRExpr list) (span: Blade.Ast.Span) =
        let k = (fid, args |> List.map argKey)
        let m = get ()
        cell.Value <- Map.add k (span :: (match Map.tryFind k m with Some l -> l | None -> [])) m
    /// The call's span: its exact key's one span, else the callee's only span.
    let tryFind (call: IRExpr) : Blade.Ast.Span option =
        match call with
        | CallOfNamed (fid, _, args, _) ->
            let m = get ()
            let one (l: Blade.Ast.Span list) = match List.distinct l with [ s ] -> Some s | _ -> None
            match Map.tryFind (fid, args |> List.map argKey) m |> Option.bind one with
            | Some s -> Some s
            | None -> m |> Map.toList |> List.filter (fun ((f, _), _) -> f = fid) |> List.collect snd |> one
        | _ -> None

/// The opt-in A7c kept inlines: each one's lifetime region, keyed by its first
/// owned pool's binder (unique per region; a pool let is never a join member,
/// so the rebuild never drops it), with its call's span when Lowering
/// recorded exactly one. Only these regions warn BL7005; the older
/// join-driven inline regions and the early-release regions never do.
module InlineEventSpans =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, Blade.Ast.Span option>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (start: IRId) (span: Blade.Ast.Span option) = cell.Value <- Map.add start span (get ())
    let isKept (start: IRId) = Map.containsKey start (get ())
    let tryFind (start: IRId) = Map.tryFind start (get ()) |> Option.flatten

/// BL7005 (W2-4 round 9, the user's decision): a lane's emitter RETAINED the
/// opt-in kept one-level inline of `call` (it reached the call's
/// return-boundary event, whether or not the lane's fused form carried the
/// legs). One warning per event per lane; the default build declines the
/// inline before any event exists, so it never warns.
let noteKeptInlineRetained (lane: string) (call: string) (start: IRId) =
    let span = match InlineEventSpans.tryFind start with Some s -> s | None -> Blade.Ast.noSpan
    Blade.Diagnostics.EmitterWarnings.add $"{lane}:{start}"
        (Blade.Diagnostics.mkWarning "BL7005" Blade.Diagnostics.PhBackend span
            $"experimental: the kept one-level inline (BLADE_EPILOGUE_LEGS=1) of {call} has an INCOMPLETE lifetime audit (G69: allocations made by the kernels it names may survive its return); the default build declines it")
/// The emitters' one entry (each lane's IRReleasePools arm): a release that
/// closes an opt-in A7c kept inline's region warns BL7005 once per lane.
let noteReleaseRetained (lane: string) (pools: IRExpr list) (call: string) =
    match pools with
    | IRVar (start, _) :: _ when InlineEventSpans.isKept start -> noteKeptInlineRetained lane call start
    | _ -> ()
/// One `epilogue-legs` v1 decision (the planner's admission with its
/// five-party matrix; each emitter records its per-lane outcome under the
/// same rule).
let recordEpilogueLegs (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "epilogue-legs"; Version = 1
          Span = Blade.Ast.noSpan; Subject = subject
          Outcome = outcome; Evidence = evidence }

/// One candidate leg as the region presents it.
type EpilogueLegSpec = {
    Name: string
    OperandId: IRId
    OperandName: string
    Kernel: IRExpr
    Seed: IRExpr
    SeedText: string
    /// The leg's own reorder licence in this build (repro: false).
    Licensed: bool
}

/// The region the admission judges: its statements in order, the producer's
/// statement, the join point (the statement the legs' results are bound
/// before) and every statement holding a leg (whose remainder is the legs'
/// OUTER combine).
type EpilogueLegRegion = {
    Subject: string
    ProducerName: string
    ProducerId: IRId
    ProducerInfo: ApplyInfo
    Stmts: (IRId option * string * IRExpr) array
    ProducerAt: int
    JoinAt: int
    MemberAts: int list
    Legs: EpilogueLegSpec list
}

/// What the admission asks of its caller's analyses.
type EpilogueLegFacts = {
    Resolve: IRExpr -> IRCallable option
    /// THE shared transitive write analysis (`scheduleMayWriteIn programFuncs
    /// modul`; an unresolved callee counts as a write).
    Writes: IRExpr -> bool
    /// The same analysis with every callback (a function-valued parameter)
    /// joined over the arguments its call sites pass (`callbackTable`): the
    /// producer's parallel body when it requests omp.
    CallbackWrites: IRExpr -> bool
    /// The CSE facts' repeatability (no display output, no effect).
    Repeatable: IRExpr -> bool
    /// The seed judge (the sibling pass's closed total whitelist).
    SeedTotal: IRExpr -> bool
    /// A statement's complete payload (`intervalPayloadOf`).
    PayloadOf: IRId -> IRExpr -> IRExpr
    ProviderEffect: IRId -> bool
    /// An IRAssign anywhere in the module (bindings' payloads, function
    /// bodies, the region) targets this id.
    AssignedAnywhere: IRId -> bool
    /// This build emits thread-level OpenMP.
    ThreadsBuild: bool
    /// The TRANSITIVE free names of an expression: its references, and for
    /// every callable it names (a kernel, a seed's callee, a nested function)
    /// that callable's captures and body, cycle-safe (the sibling pass's
    /// `freeVarsOf`). Dominance at the producer is judged on this closure,
    /// never on the immediate body alone (Codex W2-4 review finding 1).
    FreeVarsOf: IRExpr -> Set<IRId>
    /// Some why when an operand is not FRESH storage of its own -- a named
    /// view or alias of another array (`let xv = X(0)`, `let xa = x`): v1
    /// admits fresh operands only (a `|> compute`, a function result, a
    /// literal, a parameter), so operand identity is identity of storage.
    OperandNotFresh: IRId -> string option
    /// THE DEFERRED-VALUE PREDICATE (`programDeferredId`, gap G98): a leg
    /// kernel rides the producer's loop, so a read of a deferred value there
    /// would run its producer (and its failure) at the producer instead of
    /// at the leg's own statement -- IRTotality refuses it.
    IsDeferred: IRId -> bool
}

/// THE A7c ADMISSION (rule `epilogue-legs` v1). Ok = the evidence (the
/// five-party matrix); Error = (the first failed condition, the matrix rows
/// established before it). Codex review A finding 11 is the checklist:
///   * the producer: a forced (`|> compute`) dense rank-1 literal-extent map,
///     plain one-level co-iteration over stored named arrays, no Reynolds,
///     cuda or mpi, never assigned; its kernel writes nothing (both the
///     shared analysis and, under an omp request, the callback-joined one);
///     NOT threaded in this build (a strict chain split per thread would not
///     be the serial chain; v1 builds no per-chunk licensed leg either);
///   * each leg: its operand is EXACTLY one of the producer's operands (the
///     same id: not a view, alias, slice or deferred map of it), over the
///     same index space and extent, never assigned; its kernel a resolved
///     two-parameter callable, CLOSED-TOTAL (Blade.IRTotality: no BL8013 /
///     BL8006 / BL8003 site, so running it at the producer cannot move a
///     failure ahead of an intervening binding's), writing nothing (G43);
///     its seed total and writing nothing; every capture / free name of the
///     kernel and the seed bound BEFORE the producer (it runs there);
///   * the outer combine (the remainder of every statement holding a leg)
///     writes nothing -- it stays where it is, reading the same values;
///   * every intervening binding (the producer .. the join point, and the
///     legs' own statements) by its complete payload: no write (an operand
///     array, a capture, a seed input or anything else), no provider effect,
///     repeatable.
let producerEpilogueLegAdmission (facts: EpilogueLegFacts) (r: EpilogueLegRegion) : Result<string list, string * string list> =
    let rows = ResizeArray<string>()
    let fail (why: string) = Error (why, List.ofSeq rows)
    let pinfo = r.ProducerInfo
    let legNames = r.Legs |> List.map (fun l -> l.Name) |> String.concat ", "
    let refs (e: IRExpr) = collectVarRefsIR e
    let opIds = pinfo.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
    let opNames =
        List.zip pinfo.Arrays (if pinfo.Identities.Length = pinfo.Arrays.Length then pinfo.Identities else List.replicate pinfo.Arrays.Length (AIDVariable "?"))
        |> List.map (fun (_, ident) -> match ident with AIDVariable n | AIDAlias (_, n) -> n | _ -> "?")
    let plainAxis (t: IRType) : (int64 * IRIndexType) option =
        match stripUnits t with
        | ArrayElem at when not at.IsVirtual ->
            (match at.IndexTypes with
             | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 ->
                (match ix.Extent with IRLit (IRLitInt n) when n > 0L -> Some (n, ix) | _ -> None)
             | _ -> None)
        | _ -> None
    match facts.Resolve (fst (peelReynolds pinfo.Kernel)) with
    | None -> fail "the producer's kernel does not resolve to a callable"
    | Some pk ->
    // ---- party 1: the producer kernel ----------------------------------
    let pAxis = plainAxis pinfo.OutputType
    if pAxis.IsNone || pinfo.KernelOutputRank <> 0 then fail $"`{r.ProducerName}` is not a dense rank-1 literal-extent producer"
    elif pinfo.HasReynolds || pk.IsCudaKernel || pk.IsMpiParallel then fail $"`{r.ProducerName}`'s kernel carries a Reynolds body or a cuda/mpi strategy"
    elif opIds.Length <> pinfo.Arrays.Length || pinfo.Arrays.IsEmpty || (pinfo.Arrays.Length > 1 && not pinfo.IsCoIteration) then
        fail $"`{r.ProducerName}` is not a plain co-iteration over stored named arrays"
    elif facts.AssignedAnywhere r.ProducerId then fail $"`{r.ProducerName}` is assigned after its binding (its stores are not the producer's alone)"
    elif facts.Writes (IRApplyCombinator pinfo) then
        fail $"the producer kernel of `{r.ProducerName}` may write (scheduleMayWriteIn): the legs' reads would interleave with its writes"
    elif pk.IsOmpParallel && facts.CallbackWrites (IRApplyCombinator pinfo) then
        fail $"the producer's parallel body may write through a callback (callbackTable)"
    else
    let (pn, pix) = pAxis.Value
    let ompNote = if pk.IsOmpParallel then "; its omp body audited with callbackTable's callbacks too" else ""
    let opsTxt = String.concat ", " opNames
    rows.Add $"producer kernel `{r.ProducerName}` = map over {{{opsTxt}}} ({pn} cells): no write (scheduleMayWriteIn){ompNote}; forced, stored, never assigned"
    if pk.IsOmpParallel && facts.ThreadsBuild then
        let strict = r.Legs |> List.filter (fun l -> not l.Licensed)
        if not strict.IsEmpty then
            let names = strict |> List.map (fun l -> l.Name) |> String.concat ", "
            fail $"the producer runs a thread team; the strict legs {names} keep their serial pass"
        else
            fail $"the producer runs a thread team; the licensed legs {legNames} keep their own pass (v1 builds no per-chunk leg partial: the standalone fold's chunk partition and combine are not shown to be the producer's)"
    else
    // ---- party 2: the leg kernels (operands, totality, writes) -----------
    let legProblem =
        r.Legs |> List.tryPick (fun l ->
            if not (List.contains l.OperandId opIds) then
                if l.OperandId = r.ProducerId then
                    Some $"leg {l.Name} folds the producer's OUTPUT `{r.ProducerName}` (the cell epilogue owns the output; an input leg reads one of the producer's operands)"
                else
                    Some $"leg {l.Name} folds `{l.OperandName}`, which is not one of `{r.ProducerName}`'s operands (an input leg reads exactly the producer's operand -- not an alias, view, slice or deferred map of it)"
            else
            let pos = opIds |> List.findIndex (fun id -> id = l.OperandId)
            match plainAxis (typeOf pinfo.Arrays.[pos]) with
            | Some (n, ix) when n = pn && IRTotality.sameIndex ix pix ->
                if facts.AssignedAnywhere l.OperandId then Some $"leg {l.Name}'s operand `{l.OperandName}` is assigned somewhere in the module"
                else
                match facts.OperandNotFresh l.OperandId with
                | Some why -> Some $"leg {l.Name}'s operand `{l.OperandName}` is {why} (v1 admits fresh operands only: a `|> compute`, a function result, a literal or a parameter -- a shared named view is not proved to be the same storage at both traversals)"
                | None ->
                match facts.Resolve l.Kernel with
                | None -> Some $"leg {l.Name}'s fold kernel does not resolve to a callable"
                | Some kc when kc.Params.Length <> 2 || kc.IsCudaKernel || kc.IsMpiParallel -> Some $"leg {l.Name}'s fold kernel is not a plain two-parameter callable"
                | Some kc when kc.IsOmpParallel && facts.ThreadsBuild ->
                    Some $"leg {l.Name}'s fold kernel requests omp in a build that emits threads: riding the serial producer loop would drop its thread team (the joined pass keeps it)"
                | Some kc ->
                    match IRTotality.auditCallable facts.Resolve (fun _ -> None) facts.IsDeferred kc [] with
                    | Error why -> Some $"leg {l.Name}'s fold kernel is not closed-total ({why}): running it at the producer could move its failure ahead of an intervening binding's"
                    | Ok () ->
                        if facts.Writes l.Kernel then Some $"leg {l.Name}'s fold kernel may write (G43)"
                        else None
            | _ -> Some $"leg {l.Name}'s operand `{l.OperandName}` is not over `{r.ProducerName}`'s index space and extent")
    match legProblem with
    | Some why -> fail why
    | None ->
    rows.Add $"leg kernels {legNames}: each folds one of the producer's own operands (same id, index space and extent; never assigned); closed-total (IRTotality); no write (scheduleMayWriteIn)"
    // ---- party 3: the seeds (and the captures) dominate the producer -----
    let insideIds =
        [ r.ProducerAt .. r.JoinAt - 1 ]
        |> List.choose (fun i -> let (b, n, _) = r.Stmts.[i] in b |> Option.map (fun id -> (id, n)))
    let kernelFree (k: IRExpr) =
        match facts.Resolve k with
        | Some kc -> Set.union (refs kc.Body) (kc.Captures |> List.map (fun c -> c.Id) |> Set.ofList)
        | None -> Set.empty
    let seedProblem =
        r.Legs |> List.tryPick (fun l ->
            if not (facts.SeedTotal l.Seed) then Some $"leg {l.Name}'s seed may fail or has effects"
            elif facts.Writes l.Seed then Some $"leg {l.Name}'s seed may write"
            else
            // TRANSITIVE (finding 1): the kernel's and the seed's callees, their
            // captures and their bodies, followed through every resolved callable.
            let kf = Set.union (kernelFree l.Kernel) (facts.FreeVarsOf l.Kernel)
            let sf = Set.union (refs l.Seed) (facts.FreeVarsOf l.Seed)
            match insideIds |> List.tryFind (fun (id, _) -> Set.contains id kf) with
            | Some (_, n) -> Some $"leg {l.Name}'s fold kernel reads `{n}` (directly or through a callee it names), which is bound at or after the producer (the leg would run before it exists)"
            | None ->
            match insideIds |> List.tryFind (fun (id, _) -> Set.contains id sf) with
            | Some (_, n) -> Some $"leg {l.Name}'s seed reads `{n}` (directly or through a callee it names), which is bound at or after the producer (a strict chain starts at the producer)"
            | None -> None)
    match seedProblem with
    | Some why -> fail why
    | None ->
    let seedTxt = r.Legs |> List.map (fun l -> $"{l.Name}: {l.SeedText}") |> String.concat ", "
    rows.Add $"seeds ({seedTxt}): total, no write, every input (and every capture of the leg kernels, transitively through every callee they name) bound before the producer; each evaluated once, at its chain's start"
    // ---- parties 4-5: the outer combine and the intervening bindings ------
    let between = [ r.ProducerAt + 1 .. r.JoinAt - 1 ] @ r.MemberAts |> List.distinct |> List.sort
    let stmtProblem =
        between |> List.tryPick (fun i ->
            let (b, n, v0) = r.Stmts.[i]
            let v = match b with Some id -> facts.PayloadOf id v0 | None -> v0
            let isMember = List.contains i r.MemberAts
            let what = if isMember then $"the legs' own statement `{n}` (their outer combine)" else $"`{n}`"
            if facts.Writes v then Some $"{what} may write between the producer and the legs (scheduleMayWriteIn): the legs would read their operands before it"
            elif (match b with Some id -> facts.ProviderEffect id | None -> false) then Some $"{what} has a provider effect between the producer and the legs"
            elif not (facts.Repeatable v) then Some $"{what} is not repeatable (display output or a call the module cannot judge) between the producer and the legs"
            else None)
    match stmtProblem with
    | Some why -> fail why
    | None ->
    let memberNames = r.MemberAts |> List.map (fun i -> let (_, n, _) = r.Stmts.[i] in $"`{n}`") |> String.concat ", "
    rows.Add $"outer kernel ({memberNames}: the combine of the legs' results): no write; it stays at its original point"
    let interNames =
        [ r.ProducerAt + 1 .. r.JoinAt - 1 ] |> List.map (fun i -> let (_, n, _) = r.Stmts.[i] in $"`{n}`")
    let interTxt = if interNames.IsEmpty then "none" else String.concat ", " interNames
    rows.Add $"intervening ({interTxt}; the legs' statements included): no write (by complete payload), no provider effect, repeatable"
    Ok (List.ofSeq rows)
