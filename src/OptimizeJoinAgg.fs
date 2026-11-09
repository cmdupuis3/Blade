// JOIN TO AGGREGATE (round 4 wave 4, lane D item D2; plan card PM23;
// streaming plan section 5.3 "Join to aggregate"): plan rule
// `join-aggregate v1`, gate BLADE_JOIN_AGG (an optimizer gate).
//
// The hash equijoin (`hash-join v1`, Optimize.planEquiJoin) already skips the
// six N x M passes and keeps J STORED (its compact buffer, in the compound's
// lex order: left-major / right-minor) over a cardinality-only index. When J
// is not an OUTPUT and its only readers are folds and extents, J's storage is
// itself a temporary: this plan keeps the CSR over the right keys and the
// counting pass (|J| for `extents(J)` and the empty test) at J's site, emits
// NO relation buffer, and has each fold re-walk the probe at its OWN site,
// feeding `pk(a1[i], b1[j])` into its state in exactly J's order -- the same
// pair sequence, the same chain from the same seed.
//
// ADMISSION (fail closed, the first failed condition recorded):
//   * the hash-join plan for J applied (its census: every use of J is
//     `extents(J)` or `reduce(J, k[, s])` with k resolved, capture-free, pure
//     and total, plus printing; the four sources stored and never assigned
//     through their alias chains; pk write-free, capture-free, pure total);
//   * J is NOT printed: a print selection (`BLADE_PRINT` / `--print`) is set
//     and excludes J (the rows of a printed J are an output), and no other
//     module names J;
//   * every fold of J is the WHOLE value of a top-level binding (a fold inside
//     an expression, a function body or a kernel declines);
//   * every fold keeps the SERIAL chain in this build: an integer builtin
//     `+` / `*` (exact under any association), or a kernel with no reorder
//     licence, or a reproducible build (BLADE_FP_REPRO=1): a LICENSED float
//     fold declines -- its stored-J form is the K-lane tree, which a per-pair
//     chain does not reproduce.
// Nothing MOVES: J's site still runs the CSR build and the counting pass
// (its BL8005 size checks included), each fold runs at its own site, its seed
// evaluated there, its BL8003 (unseeded, |J| = 0) raised there. What changes is
// how often pk runs (once per pair PER FOLD, a total pure kernel) and that the
// CSR lives from J's site to the last fold instead of J's buffer.
//
// LANES: C++ only, like the hash join (the LLVM lane refuses the rank-2
// compound and falls back to C++). The interpreter runs the as-written tree.
module Blade.OptimizeJoinAgg

open Blade.Types
open Blade.IR

type JoinAggPlan = {
    JoinId: IRId
    JoinName: string
    /// The fold bindings, in program order (the last one frees the CSR).
    Folds: IRId list
    /// ST15 (lane H, G128): a fold over `compound(J, mask(J, p))` -- fold id
    /// -> the payload predicate p, applied to each pair's payload on the
    /// probe (the selected cells in J's lex order).
    Filtered: Map<IRId, IRExpr>
    /// The masks over J and the compounds over them that the filtered folds
    /// replace: never emitted (nothing else reads them; not printed).
    Elided: IRId list
}

module JoinAggTable =
    open System.Threading
    let private cell = new AsyncLocal<Map<IRId, JoinAggPlan>>()
    let private get () = match box cell.Value with null -> Map.empty | _ -> cell.Value
    let reset () = cell.Value <- Map.empty
    let record (p: JoinAggPlan) = cell.Value <- Map.add p.JoinId p (get ())
    let tryJoin (id: IRId) : JoinAggPlan option = Map.tryFind id (get ())
    let tryFold (id: IRId) : JoinAggPlan option =
        get () |> Map.toSeq |> Seq.map snd |> Seq.tryFind (fun p -> List.contains id p.Folds)
    let elidedBy (id: IRId) : JoinAggPlan option =
        get () |> Map.toSeq |> Seq.map snd |> Seq.tryFind (fun p -> List.contains id p.Elided)

/// OPT-IN (round 4 wave 4 stop rule: the second review of the census class --
/// Codex round 1 -- found further members, a J read in a type extent and a
/// source written between J and a fold): BLADE_JOIN_AGG=1|on plans it; unset /
/// 0 / off = J stays stored (the hash join's relation buffer). Read per call.
let joinAggEnabled () = Blade.Toolchain.envFlag "BLADE_JOIN_AGG" false

let private printSelectionNames () : Set<string> option =
    match System.Environment.GetEnvironmentVariable "BLADE_PRINT" with
    | null -> None
    | s ->
        let names = s.Split([| ','; ' '; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun n -> n.Trim()) |> Array.filter ((<>) "") |> Set.ofArray
        if names.IsEmpty then None else Some names

/// Does a print selection name this binding? An IMPORTED module's bindings
/// are qualified AFTER this plan runs (`Lib.J`), so an entry matches by its
/// last dotted segment too (fail closed: any module's `J` counts; round 4
/// wave 6 class-closure round -- `--print Lib.J` read a never-built buffer).
let private selects (names: Set<string>) (name: string) : bool =
    names.Contains name
    || names |> Set.exists (fun n -> match n.LastIndexOf '.' with -1 -> false | i -> n.Substring(i + 1) = name)

let planJoinAggregate (programFuncs: IRCallable list) (foreignRefs: Set<IRId>) (modul: IRModule) : unit =
    let funcs = (modul.Functions @ programFuncs) |> List.distinctBy (fun f -> f.Id) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let resolve (k: IRExpr) = match k with IRVar (fid, _) -> Map.tryFind fid funcs | _ -> None
    let decide (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "join-aggregate"; Version = 1
              Span = declSpanOf subject; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    // `foldReorderLicensed`'s rule (CodeGenExprSupport), restated as the
    // group-aggregate plan restates it: a `where repro` kernel (or the eta
    // wrapper of one) vetoes.
    let licensed (c: IRCallable) =
        let vetoed =
            c.IsRepro
            || (match c.Body with
                | IRApp (IRVar (fid, _), _, _) -> (match Map.tryFind fid funcs with Some g -> g.IsRepro | None -> false)
                | _ -> false)
        not vetoed && (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome)
    let enabled = joinAggEnabled ()
    let selection = printSelectionNames ()
    let llvmNote = "llvm lane: the rank-2 compound is refused there (C++ fallback), as for the hash join"
    for jp in Blade.Optimize.EquiJoinTable.all () do
        let jid, jname = jp.CompoundId, jp.CompoundName
        if not (modul.Bindings |> List.exists (fun b -> b.Id = jid)) then () else
        let refsJ (e: IRExpr) = Set.contains jid (collectVarRefsIR e)
        // ST15 (lane H, G128): the payload predicate. A mask over J (`let keep
        // = mask(J, p)`) and the compound over it (`compound(J, keep)`, or
        // `compound(J, mask(J, p))` inline) are the SELECTION; a fold over the
        // selection walks the probe and feeds only the pairs whose payload p
        // keeps -- the selected cells in J's lex order, the same chain.
        let maskOfJ : Map<IRId, IRExpr> =
            modul.Bindings |> List.choose (fun b ->
                match b.Value with
                | IRMask (IRVar (s, _), p) when s = jid -> Some (b.Id, p)
                | _ -> None) |> Map.ofList
        let selections : Map<IRId, IRExpr> =
            modul.Bindings |> List.choose (fun b ->
                match Map.tryFind b.Id modul.CompoundInits with
                | Some (IRVar (d, _), IRVar (k, _)) when d = jid && maskOfJ.ContainsKey k -> Some (b.Id, maskOfJ.[k])
                | Some (IRVar (d, _), IRMask (IRVar (s, _), p)) when d = jid && s = jid -> Some (b.Id, p)
                | _ -> None) |> Map.ofList
        let derived = Set.union (maskOfJ |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (selections |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
        let family = Set.add jid derived
        let refsDerived (e: IRExpr) = not (Set.isEmpty (Set.intersect derived (collectVarRefsIR e)))
        let refsFamily (e: IRExpr) = not (Set.isEmpty (Set.intersect family (collectVarRefsIR e)))
        let nameOf (id: IRId) = modul.Bindings |> List.tryFind (fun b -> b.Id = id) |> Option.map (fun b -> b.Name) |> Option.defaultValue "?"
        // every reader of J, top level and callables
        let extentOnly (e: IRExpr) =
            // every reference to J sits inside an `extents(J, d)`
            let stripped = mapIRExpr (function IRExtent (IRVar (id, _), _) when id = jid -> IRLit (IRLitInt 0L) | x -> x) e
            not (refsJ stripped)
        let readers =
            modul.Bindings |> List.filter (fun b -> not (family.Contains b.Id) && (Blade.Optimize.bindingPayload modul b |> List.exists refsFamily))
        // A fold is a binding whose WHOLE value is `reduce(J, k[, s])` and whose
        // seed (and every side payload) names J only inside `extents(J)`: a seed
        // folding J again would read J's absent buffer (fable review, fix 1).
        // A fold over a SELECTION names no member of the family in its kernel,
        // seed or side payload (the selection's count is not built: v1).
        let sidePayload (b: IRBinding) =
            Blade.Optimize.bindingPayload modul b |> List.filter (fun e -> not (obj.ReferenceEquals(e, b.Value)))
        let foldOnly (b: IRBinding) =
            match b.Value with
            | IRReduce (IRVar (id, _), k, seed) when id = jid ->
                extentOnly k && (seed |> Option.forall extentOnly)
                && not (refsDerived k) && not (seed |> Option.exists refsDerived)
                && (sidePayload b |> List.forall (fun e -> extentOnly e && not (refsDerived e)))
            | IRReduce (IRVar (id, _), k, seed) when selections.ContainsKey id ->
                not (refsFamily k) && not (seed |> Option.exists refsFamily)
                && (sidePayload b |> List.forall (refsFamily >> not))
            | _ -> false
        let folds, others = readers |> List.partition foldOnly
        let others = others |> List.filter (fun b -> not (Blade.Optimize.bindingPayload modul b |> List.forall (fun e -> extentOnly e && not (refsDerived e))))
        // a named mask is read by its selection(s) only (a second reader --
        // a mask over it, a keyed read -- stays with the stored form)
        let maskReader =
            maskOfJ |> Map.toSeq |> Seq.tryPick (fun (k, _) ->
                modul.Bindings
                |> List.tryFind (fun b ->
                    b.Id <> k && not (selections.ContainsKey b.Id && (match Map.tryFind b.Id modul.CompoundInits with Some (_, IRVar (m, _)) -> m = k | _ -> false))
                    && (Blade.Optimize.bindingPayload modul b |> List.exists (fun e -> Set.contains k (collectVarRefsIR e))))
                |> Option.map (fun b -> (nameOf k, b.Name)))
        // the payload predicate: closed, pure and total (it runs on every pair
        // at each fold's site instead of on every cell at the mask's site)
        // EVERY elided member's predicate -- each named mask over J (one no
        // selection reads included: it is elided too, Codex round 1) and each
        // inline selection's
        let predicateWhy =
            Seq.append (Map.toSeq maskOfJ) (Map.toSeq selections) |> Seq.tryPick (fun (sid, p) ->
                match resolve p with
                | Some c when c.Captures.IsEmpty && c.Params.Length = 1 && not c.IsOmpParallel && not c.IsCudaKernel && not c.IsMpiParallel
                              && Blade.Optimize.totalScalarOver c.Params c.Body -> None
                | _ -> Some $"the payload predicate of `{nameOf sid}` (a mask over J or a selection) is not a resolved, capture-free, pure total scalar of one parameter")
        let fnReader = modul.Functions |> List.tryFind (fun f -> refsFamily f.Body)
        // J named inside ANY type extent (a range bound, a declared type, a
        // binding type): the ordinary renderers would read J's absent buffer
        // there (Codex review round 1, finding 1). The members of the family
        // are checked the same way; a fold over a selection is checked on its
        // kernel, seed and type (the selection's own compound index type names
        // its mask -- the index a stored selection would build at its site).
        let typeReader =
            modul.Bindings |> List.tryFind (fun b ->
                if derived.Contains b.Id then false
                else
                let payloadTypes =
                    match b.Value with
                    | IRReduce (IRVar (id, _), k, seed) when selections.ContainsKey id ->
                        (k :: Option.toList seed @ sidePayload b) |> List.collect Blade.Optimize.embeddedExtentExprs
                    | _ -> Blade.Optimize.bindingPayload modul b |> List.collect Blade.Optimize.embeddedExtentExprs
                (Blade.Optimize.irTypeExtentExprs b.Type @ payloadTypes)
                |> List.exists refsFamily)
            |> Option.map (fun b -> b.Name)
            |> Option.orElse (modul.Functions |> List.tryFind (fun f -> Blade.Optimize.embeddedExtentExprs f.Body |> List.exists refsFamily) |> Option.map (fun f -> f.SourceName))
            |> Option.orElse (modul.Types |> List.tryPick (fun td ->
                match td with
                | IRTDAlias (n, t) when Blade.Optimize.irTypeExtentExprs t |> List.exists refsFamily -> Some $"type {n}"
                | _ -> None))
        let familyPrinted =
            match selection with
            | Some names -> derived |> Seq.tryFind (fun id -> selects names (nameOf id))
            | None -> derived |> Seq.tryHead
        // the four sources must hold from J's site to the last fold: no binding
        // in between may write ANYTHING (the shared write analysis; a callee
        // writing through a `mut` parameter is a write -- Codex round 1, finding 2)
        let writes = Blade.Optimize.scheduleMayWriteIn programFuncs modul
        let arr = List.toArray modul.Bindings
        let jIdx = arr |> Array.findIndex (fun b -> b.Id = jid)
        let lastFold = folds |> List.map (fun b -> arr |> Array.findIndex (fun x -> x.Id = b.Id)) |> List.fold max jIdx
        let writer =
            arr.[jIdx + 1 .. lastFold] |> Array.tryFind (fun b ->
                // the payload AND every executable extent it or its type carries
                // (a write in a range bound -- Codex round 2)
                let payload = Blade.Optimize.bindingPayload modul b
                (payload @ Blade.Optimize.irTypeExtentExprs b.Type @ (payload |> List.collect Blade.Optimize.embeddedExtentExprs))
                |> List.exists writes)
        let floatFold =
            folds |> List.exists (fun b -> match stripUnits b.Type with IRTScalar (ETFloat64 | ETFloat32) -> true | _ -> false)
        let contractOff =
            System.String.Equals(System.Environment.GetEnvironmentVariable "BLADE_FP_CONTRACT", "off", System.StringComparison.OrdinalIgnoreCase)
        let strictWhy (b: IRBinding) : string option =
            match b.Value with
            | IRReduce (_, k, _) ->
                (match resolve k with
                 | None -> Some $"the fold `{b.Name}`'s kernel does not resolve to a callable"
                 | Some c ->
                     let exactInt =
                         (match stripUnits b.Type with IRTScalar (ETInt64 | ETInt32) -> true | _ -> false)
                         && (builtinOp c).IsSome
                     if exactInt || Blade.Toolchain.fpReproEnabled () || not (licensed c) then None
                     else Some $"the fold `{b.Name}` is LICENSED in this build (its stored-J form is the K-lane tree, which a per-pair chain does not reproduce); BLADE_FP_REPRO=1 or an unlicensed kernel keeps the serial chain")
            | _ -> Some $"`{b.Name}` is not a fold"
        let why =
            if not enabled then Some "disabled by BLADE_JOIN_AGG"
            elif selection.IsNone then Some "J is printed (no print selection excludes it): its rows are an output"
            elif selects selection.Value jname then Some "the print selection names J: its rows are an output"
            elif foreignRefs.Contains jid then Some "another module names J"
            elif familyPrinted.IsSome then Some $"the selection member `{nameOf familyPrinted.Value}` (a mask over J or the compound over it) is printed: its cells are an output"
            elif derived |> Seq.exists foreignRefs.Contains then Some "another module names a mask over J or the compound over it"
            elif maskReader.IsSome then Some $"the mask `{fst maskReader.Value}` over J is read by `{snd maskReader.Value}` (only its selection may read it)"
            elif predicateWhy.IsSome then Some predicateWhy.Value
            elif fnReader.IsSome then Some $"a callable (`{fnReader.Value.SourceName}`) reads J (v1 plans top-level folds only)"
            elif typeReader.IsSome then Some $"J is named inside a type extent (in `{typeReader.Value}`): the ordinary renderers would read its absent buffer"
            elif writer.IsSome then Some $"`{writer.Value.Name}`, between J and its last fold, may write (the shared write analysis): the probe re-reads the four sources at each fold"
            elif floatFold && not contractOff then Some "a float fold under contraction (BLADE_FP_CONTRACT is not off): the probe hands pk's value straight to the fold, so g++ may fuse the pair product into the fold's add where the stored J rounded it first"
            elif not others.IsEmpty then Some $"`{others.Head.Name}` reads J (or a selection over it) other than as a whole-binding fold or an extent"
            elif folds.IsEmpty then Some "no fold reads J (its storage is already only what printing would read)"
            else folds |> List.tryPick strictWhy
        let evidence =
            [ $"J = the equijoin of `{jp.LeftKeyName}` and `{jp.RightKeyName}` (hash-join v1: CSR over the right keys, counting pass at J's site)"
              (match folds with
               | [] -> "folds: none"
               | fs -> "folds fed straight from the probe, each at its own site: " + (fs |> List.map (fun b -> $"`{b.Name}`") |> String.concat ", "))
              "order: left-major / right-minor (the compound's lex order), every pair, duplicates on both sides kept -- the stored J's cell sequence; each fold's chain from its own seed (unseeded: the first pair's value, BL8003 at the fold when |J| = 0)"
              "parties: the four sources stored and never assigned (hash-join v1's census); pk and every fold kernel pure, total, write-free (hash-join v1's census); seeds evaluated at their folds' own sites; nothing moves"
              "contraction: a float fold reads pk's value straight from the probe, so under the default -ffp-contract=fast g++ may fuse the pair's product into the fold's add where the stored J rounded it first; bit identity with the stored form is the BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off build (the differential gates' pin), as for every contraction difference"
              "cost: pk runs once per pair per fold; the CSR (right side) lives from J's site to the last fold instead of J's |J|-cell buffer"
              llvmNote ]
            @ (if selections.IsEmpty then []
               else
                   let sels = selections |> Map.toSeq |> Seq.map (fun (id, _) -> $"`{nameOf id}`") |> String.concat ", "
                   [ $"payload predicate (H1, ST15): the selection(s) {sels} over J are never stored -- a fold over one feeds only the pairs whose payload its predicate keeps, in J's lex order (the stored selection's cell sequence); the predicate is closed, pure and total, so evaluating it per pair at each fold's site instead of per cell at the mask's site is unobservable; the masks' bit buffers and the selections' compact buffers and indexes are never built" ])
        match why with
        | Some w -> decide jname (Blade.Effects.Declined w) evidence
        | None ->
            let filtered =
                folds |> List.choose (fun b ->
                    match b.Value with
                    | IRReduce (IRVar (id, _), _, _) when selections.ContainsKey id -> Some (b.Id, selections.[id])
                    | _ -> None) |> Map.ofList
            JoinAggTable.record { JoinId = jid; JoinName = jname; Folds = folds |> List.map (fun b -> b.Id)
                                  Filtered = filtered; Elided = Set.toList derived }
            decide jname Blade.Effects.Applied evidence
