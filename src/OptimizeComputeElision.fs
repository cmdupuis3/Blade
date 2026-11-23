// Compute elision, rule compute-elision (with the fold-map and gather-terminal arms), and the fused-fold seed rule shared with call elision (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeComputeElision

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeHotRow
open Blade.OptimizeSiblingFolds

/// THE FUSED FOLD'S ADMISSION AND SEED, shared by compute elision and call
/// elision (one rule, so the two spellings of "a fold that reads a map in
/// place" can never disagree): the fold is a rank-1 fold by a resolved
/// two-parameter serial kernel; a seeded fold keeps its seed; an UNSEEDED
/// builtin `(+)` / `(*)` fold only over a LITERAL extent >= 1 (an empty or
/// run-time input keeps the stored fold and its BL8003 at the fold), its seed
/// by W4-3's ROUTE rule (Lowering.lowerUnseededSeed, gap G74), so the elided
/// fold carries exactly the seed the written-out `reduce(method_for(a) <@> f,
/// op)` gets: on a STRICT route (the repro build, a `where repro` region or
/// fold kernel) the exact identity -- the head-peeled chain of the stored
/// fold, bit for bit; on a licensed route the BASE seed (+0.0 for a float
/// `+`: the licence's signed zero, G40), the emission a licensed fold already
/// has. Ok (seed, evidence) or Error (the decline).
let elidedFoldSeed (strictRegion: bool) (outType: IRType) (fk: IRCallable option) (init: IRExpr option) : Result<IRExpr * string, string> =
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, (IRAdd | IRMul as op), IRVar (a, _), IRVar (b, _), _)
            when (a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId) -> Some op
        | _ -> None
    let requestsParallel (c: IRCallable) =
        c.IsOmpParallel || not c.Parallelism.IsEmpty || c.IsCudaKernel || c.IsMpiParallel
    let outAt = match stripUnits outType with ArrayElem at -> Some at | _ -> None
    match outAt, fk with
    | None, _ -> Error "the producer does not produce an array"
    | Some at, _ when not (at.IndexTypes.Length = 1 && at.IndexTypes.Head.Rank = 1) ->
        Error "a fold over a rank >= 2 array (the stored fold peels the innermost axis; the fused terminal folds every cell)"
    | _, None -> Error "the fold kernel does not resolve to a callable"
    | _, Some fkc when fkc.Params.Length <> 2 -> Error "the fold kernel is not a two-parameter callable"
    | _, Some fkc when requestsParallel fkc -> Error "the fold kernel requests a parallel strategy (v1 keeps its route)"
    | Some at, Some fkc ->
        match init with
        | Some s -> Ok (s, "seeded: the seed is the empty result either way")
        | None ->
            let n = match at.IndexTypes.Head.Extent with IRLit (IRLitInt n) -> Some n | _ -> None
            let et = match at.ElemType with IRTScalar et -> Some et | _ -> None
            match builtinOp fkc, et, n with
            | None, _, _ -> Error "an unseeded fold whose kernel is not a builtin `+` / `*` (no identity seed)"
            | _, None, _ -> Error "an unseeded fold over a unit-carrying or non-scalar element (no identity seed is spelled)"
            | _, _, None -> Error "an unseeded fold over an extent not known at compile time to be >= 1 (the fused terminal would return the identity where the stored fold panics BL8003 on an empty input)"
            | _, _, Some n when n < 1L -> Error "an unseeded fold over an empty extent (keeps its BL8003 at the fold)"
            | Some op, Some et, Some n ->
                let strictRoute = Blade.Toolchain.fpReproEnabled () || strictRegion || fkc.IsRepro
                match sfIdentitySeed op et with
                | Some (seed, txt) when strictRoute ->
                    Ok (seed, $"unseeded over a literal extent {n} >= 1, strict route: identity seed {txt}, bitwise the unseeded chain")
                | Some (seed, txt) ->
                    let baseSeed, baseTxt =
                        match op, et with
                        | IRAdd, ETFloat64 -> IRLit (IRLitFloat 0.0), "+0.0"
                        | IRAdd, ETFloat32 -> IRLit (IRLitFloat32 0.0f), "+0.0"
                        | _ -> seed, txt
                    Ok (baseSeed, $"unseeded over a literal extent {n} >= 1, licensed route: base seed {baseTxt} (the route-resolved seed of the written-out fused fold; the licence owns the signed zero, G40)")
                | None -> Error "an unseeded fold over an element with no exact identity seed"

/// THE GATHER TERMINAL CERTIFICATE (round 4 wave 3, PM28; compute elision's
/// `gather-terminal v1` arm, gate BLADE_GATHER_ELIDE). A gather producer
/// `method_for(idx) <@> lambda(i) -> A(i)` is NOT closed-total -- its
/// subscript is checked at run time (BL8006) -- so compute elision's failure
/// rule declines it. The certificate admits it under the OTHER rule, "no
/// observable event between", narrowed so the failure is the same one:
///   * ONE SITE. Exactly one subscript `A(e..)` of the kernel accounts for
///     every possible failure: replacing that node (and only it) by a literal
///     makes the body pass the closed totality whitelist, and each of its
///     index expressions passes it too. A's index types are plain dense rank-1
///     NAMED records (the subscript is a checked BL8006 site), A is stored (not
///     deferred) and not an operand of the gather (alias overlap declines).
///     With one site, a failing cell raises ONE panic text (code, message,
///     location; the frames are the enclosing body's in both forms), so WHICH
///     failing cell trips first is unobservable, and the stored map and the
///     fused fold fail identically.
///   * NOTHING CONSUMED IS OBSERVABLE. The consumer is a FOLD whose kernel and
///     seed are closed-total and write-free (the caller checks), so cells the
///     fold folded before the failing one leave no trace; and the fold is the
///     ADJACENT statement (the caller checks): no statement runs between the
///     gather's old position and its new one.
/// Ok (evidence lines) or Error (the first failed condition).
let gatherTerminalCertificate (resolve: IRExpr -> IRCallable option) (isDeferred: IRId -> bool)
                              (p: ApplyInfo) (pkc: IRCallable) : Result<string list, string> =
    let levels =
        if pkc.Params.Length <> p.Arrays.Length then []
        else List.zip pkc.Params p.Arrays |> List.choose (fun (prm, a) -> IRTotality.rangeIx a |> Option.map (fun ix -> (prm, ix)))
    let audit (body: IRExpr) = IRTotality.auditCallable resolve (fun _ -> None) isDeferred { pkc with Body = body } levels
    let operandIds = p.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None) |> Set.ofList
    let rec sites (e: IRExpr) : IRExpr list =
        let here = match e with IRIndex (IRVar _, _, _) -> [ e ] | _ -> []
        let (ExprShape (ch, _)) = e
        here @ (ch |> List.collect sites)
    let rec replaceOnce (target: IRExpr) (repl: IRExpr) (e: IRExpr) : IRExpr =
        if obj.ReferenceEquals(e, target) then repl
        else
            let (ExprShape (ch, rebuild)) = e
            if ch.IsEmpty then e else rebuild (ch |> List.map (replaceOnce target repl))
    let placeholder (site: IRExpr) =
        match (try IRTotality.scalarElem (typeOf site) with _ -> None) with
        | Some ETFloat32 -> Some (IRLit (IRLitFloat32 0.0f))
        | Some ETFloat64 -> Some (IRLit (IRLitFloat 0.0))
        | Some (ETInt64 | ETInt32) -> Some (IRLit (IRLitInt 0L))
        | Some ETBool -> Some (IRLit (IRLitBool false))
        | _ -> None
    let checkedDense (aty: IRType) (nIdx: int) =
        match stripUnits aty with
        | ArrayElem at when not at.IsVirtual ->
            at.IndexTypes.Length = nIdx
            && at.IndexTypes |> List.forall (fun ix -> ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain && ix.Tag.IsSome)
        | _ -> false
    // A CHECKED INDEX CAST `A((e : I))` is the subscript's failure point
    // instead: the cast binds e once and guards `0 <= v < n` (BL8006,
    // TypeCheckSupport.checkedIndexConversion), which proves the subscript.
    // Admitted for a rank-1 A only (one index, so one failure point: the
    // cast's guard or the subscript's own check, never both); the index
    // expression is audited with the guard replaced by a literal.
    let rec stripCastChecks (n: int ref) (e: IRExpr) : IRExpr =
        match e with
        | IRConstraintCheck (_, "BL8006", _, _) -> n.Value <- n.Value + 1; IRLit (IRLitBool true)
        | _ ->
            let (ExprShape (ch, rebuild)) = e
            if ch.IsEmpty then e else rebuild (ch |> List.map (stripCastChecks n))
    let indexOk (idxs: IRExpr list) =
        let n = ref 0
        let stripped = idxs |> List.map (stripCastChecks n)
        (n.Value = 0 || idxs.Length = 1)
        && n.Value <= 1
        && stripped |> List.forall (fun x -> (audit x) = Ok ())
    let candidates =
        sites pkc.Body |> List.choose (fun site ->
            match site with
            | IndexOfNamed (a, aty, idxs, _) when not (isDeferred a) && checkedDense aty idxs.Length ->
                (match placeholder site with
                 | Some ph when indexOk idxs
                                && (audit (replaceOnce site ph pkc.Body)) = Ok () ->
                     Some (a, aty)
                 | _ -> None)
            | _ -> None)
    match candidates with
    | [] -> Error "no single checked subscript accounts for every possible failure of the producer (a second possibly failing operation, a subscript into a deferred, anonymous or structured array, or an index expression that may itself fail)"
    | [ (a, _) ] when operandIds.Contains a -> Error "the gathered array is also an operand of the gather (alias overlap)"
    | [ (_, aty) ] ->
        let ixNames =
            match stripUnits aty with
            | ArrayElem at -> at.IndexTypes |> List.map (fun ix -> defaultArg ix.Tag "?") |> String.concat ", "
            | _ -> "?"
        Ok [ $"gather-terminal v1: the producer's one possibly failing operation is a checked subscript into a stored array over {ixNames} (BL8006); every failing cell raises the same panic text in the stored and the fused form, so the failing cell's position is unobservable"
             "the gathered array is not an operand of the gather (no alias overlap)" ]
    | _ -> Error "more than one subscript could each be the producer's only failure (ambiguous)"

/// THE PROGRAM-WIDE VALUE-REFERENCE CENSUS (wave 3 B, P4 remainder (b): a
/// callee in ANOTHER module). `elideCallsModule` sees one module's bindings,
/// so a callable another module passes AS A VALUE (an alias, an argument, a
/// kernel) would be invisible to its `referencedAsValue` check. Lowering
/// installs, before the per-module optimizer runs, the ids of every callable
/// some module's bindings or some function body names outside the head of a
/// call (`programValueRefs`); None (not installed: a caller that optimizes a
/// lone module) makes every foreign callee decline, fail closed.
module CallElideProgram =
    open System.Threading
    let private cell = new AsyncLocal<Set<IRId> option>()
    let install (ids: Set<IRId>) = cell.Value <- Some ids
    let reset () = cell.Value <- None
    let valueRefs () : Set<IRId> option = match box cell.Value with null -> None | _ -> cell.Value

let recordComputeElision (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = "compute-elision"; Version = 1; Span = Blade.Ast.noSpan
          Subject = subject; Outcome = outcome; Evidence = evidence }

/// A pending map-consumer record under its own rule (`compute-elision` or
/// `call-elision`).
let recordPendingElision (rule: string) (subject: string) (outcome: Blade.Effects.DecisionOutcome) (evidence: string list) =
    Blade.Effects.Decisions.record
        { Blade.Effects.Rule = rule; Version = 1; Span = Blade.Ast.noSpan
          Subject = subject; Outcome = outcome; Evidence = evidence }

/// A MAP-consumer elision, recorded once elementwise fusion has run: applied
/// iff no application of the host kernel still carries a computed operand.
type ComputeElisionPending = { PendingRule: string; PendingSubject: string; HostKernel: IRId; PendingEvidence: string list
                               /// fold-map elision: the PRODUCER's kernel -- the record is applied only
                               /// when no computed operand over it survives fusion (a host whose other
                               /// operand fused may get a new id while this producer stays unspliced)
                               SplicedKernel: IRId option }

let elideSingleConsumerComputesModule (builder: IRBuilder) (programFuncs: IRCallable list) (singleModule: bool) (modul: IRModule) : IRModule * ComputeElisionPending list =
    if modul.Functions |> List.exists (fun f -> f.IsCudaKernel) then (modul, []) else
    let pending = ResizeArray<ComputeElisionPending>()
    let enabled = computeElideEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs = programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    // IRMono's fusion predicates resolve kernels module-locally and purity
    // program-wide, exactly as fuseElementwiseChainsModule does.
    let localCallables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in modul.Functions do localCallables.[f.Id] <- f
    let resolver = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do resolver.[f.Id] <- f
    for f in modul.Functions do resolver.[f.Id] <- f
    let resolve (k: IRExpr) = resolveCallableIn funcs k
    let mayWrite = scheduleMayWriteIn programFuncs modul
    let payloadOf = intervalPayloadOf modul
    // ---- the module census (conservative: a shape specialization copies a
    // body WITH its ids, so an id assigned, deferred or captured in any copy
    // counts in all of them; READERS are counted per region, below) ---------
    let assigned = System.Collections.Generic.HashSet<IRId>()
    let deferredIds = System.Collections.Generic.HashSet<IRId>()
    // the census widened by the program's deferred-value classifier (gap G98
    // class audit) for the totality audits below
    let deferredW = widenDeferred modul (modul.Functions @ programFuncs) deferredIds.Contains
    let rec rootVar (t: IRExpr) =
        match t with
        | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> rootVar b
        | IRVar (id, _) -> Some id
        | _ -> None
    let census (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRAssign (t, _) -> (match rootVar t with Some id -> assigned.Add id |> ignore | None -> ())
            | IRLet (id, v, _) when isDeferredForm v -> deferredIds.Add id |> ignore
            | _ -> ()) e
    for f in modul.Functions do census f.Body
    for b in modul.Bindings do
        for p in bindingPayload modul b do census p
    let captured =
        funcs |> Map.fold (fun acc _ f -> f.Captures |> List.fold (fun a c -> Set.add c.Id a) acc) Set.empty
    let refsOf (x: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun m -> match m with IRVar (id, _) when id = x -> n <- n + 1 | _ -> ()) e
        n
    let isX (x: IRId) (e: IRExpr) = match e with IRVar (id, _) -> id = x | _ -> false
    /// References to x inside the extent expressions types carry.
    let extentRefsOf (x: IRId) (e: IRExpr) =
        embeddedExtentExprs e |> List.sumBy (refsOf x)
    /// An extent expression that cannot fail or write: a literal, a name (not
    /// a callable, not deferred), an extent read of a name, and wrapping
    /// integer `+ - *` / negation of those.
    let rec plainExtent (e: IRExpr) : bool =
        match e with
        | IRLit _ -> true
        | IRVar (id, ty) -> not (Map.containsKey id funcs) && not (isFunctionTyped ty) && not (deferredIds.Contains id)
        | IRParam (_, _, ty) -> not (isFunctionTyped ty)
        | IRExtent (IRVar (id, _), _) -> not (deferredIds.Contains id)
        | IRExtent (IRParam (_, _, ty), _) -> not (isFunctionTyped ty)
        | IRBinOp (_, (IRAdd | IRSub | IRMul), a, b, _) -> plainExtent a && plainExtent b
        | IRUnaryOp (IRNeg, a) -> plainExtent a
        | _ -> false
    let staticExtents (at: IRArrayType) =
        at.IndexTypes |> List.map (fun ix -> match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
    // What the ONE reader does with x, when it is not a consumer this pass
    // takes: the node holding x as a direct child names it (None when no
    // node, or more than one, does -- the caller then speaks of readers).
    let describeRead (x: IRId) (isTail: bool) (sv: IRExpr) : string option =
        if isX x sv then Some (if isTail then "is returned" else "is aliased by another name")
        else
            let found = ResizeArray<string>()
            iterIRExpr (fun n ->
                let (ExprShape (children, _)) = n
                // an extent read (a bounds check's) names no consumer, and a
                // method_for loop repeats its apply's operands
                if children |> List.exists (isX x) && not n.IsIRExtent && not n.IsIRMethodFor then
                    found.Add (
                        match n with
                        | IRIndex _ | IRSlice _ | IRShift _ | IRReverse _ | IRCurry _ -> "is read by a keyed subscript or a view"
                        | IRApp _ -> "is passed to a call"
                        | IRTuple _ -> "is a tuple member"
                        | IRReduce _ -> "is folded by a reduce that is not the whole value of its statement"
                        | IRApplyCombinator _ -> "is read by a map that is not the whole computed value of its statement, or reads it more than once"
                        | IRProdSum _ -> "is a prodsum operand (v1 elides into a reduce or a pointwise map only)"
                        | IRAssign _ -> "is assigned"
                        | _ -> "")) sv
            if found.Count = 1 && found.[0] <> "" then Some found.[0] else None
    // ---- FOLD-OF-MAP (round 4 wave 2, PM12; BLADE_FOLD_MAP_ELIDE) -----------
    // The operand MAP of a fold node: `reduce(<map>, k[, s])` or the fused
    // terminal `IRReduceCompute(<map>, k, s)`.
    let foldOperandMap (n: IRExpr) : ApplyInfo option =
        match n with
        | IRReduce ((IRApplyCombinator h | IRCompute (IRApplyCombinator h)), _, _)
        | IRReduceCompute ((IRApplyCombinator h | IRCompute (IRApplyCombinator h)), _, _) -> Some h
        | _ -> None
    /// The ONE fold in `sv` whose operand map reads x at an operand position,
    /// when every reference to x in `sv` is in that map's operand lists (its
    /// Arrays and their Loop-provenance copy): not the fold's kernel or seed,
    /// not the map's kernel, nothing else in the statement.
    let foldMapConsumerOf (x: IRId) (sv: IRExpr) : ApplyInfo option =
        let found = ResizeArray<ApplyInfo>()
        iterIRExpr (fun n ->
            match foldOperandMap n with
            | Some h when h.Arrays |> List.exists (isX x) -> found.Add h
            | _ -> ()) sv
        if found.Count <> 1 then None
        else
            let h = found.[0]
            // every read is a DIRECT operand position (not inside a nested map
            // operand, whose own evaluation would then repeat the producer)
            let direct = h.Arrays |> List.filter (isX x) |> List.length
            if refsOf x h.Kernel = 0 && refsOf x sv = refsOf x (IRApplyCombinator h)
               && (h.Arrays |> List.sumBy (refsOf x)) = direct
               // v1: SEVERAL positions (`c * c`) only; one position is the B1
               // cell epilogue's shape and keeps today's record (collapseOperand)
               && direct >= 2 then Some h else None
    /// Collapse the duplicate operand positions of x in a plain co-iteration
    /// (`zip(c, c)` -> one operand): the duplicates are the SAME stored array
    /// at the SAME shared coordinate, so their kernel parameters always hold
    /// the same cell; the collapsed kernel reads the kept parameter for each.
    /// Every positional list is rebuilt in lockstep. One position: unchanged.
    let collapseOperand (x: IRId) (h: ApplyInfo) (hk: IRCallable) : Result<ApplyInfo * IRCallable * IRCallable option, string> =
        let posns = h.Arrays |> List.indexed |> List.filter (fun (_, a) -> isX x a) |> List.map fst
        match posns with
        | [] -> Error "the fold's map does not read the compute"
        // ONE position (`reduce(method_for(y) <@> lambda(w) -> w * w, ..)`) is the
        // B1 cell epilogue's shape: that plan keeps the producer's store and
        // folds inside its loop, so v1 leaves it to the epilogue (both must
        // not claim one producer)
        | [ _ ] -> Error "the fold's map reads the compute at one operand position: the cell epilogue's (B1) shape, which keeps the producer stored and folds inside its loop (v1 elides only a co-iteration reading it at several positions, `c * c`)"
        | keep :: rest ->
        let n = h.Arrays.Length
        let drop = Set.ofList rest
        let lengthsOk =
            h.Identities.Length = n && h.ArrayTypes.Length = n && h.SDimsPerArray.Length = n
            && h.SymcomStates.Length = n && h.TriangularLevels.Length = n && h.KernelInputRanks.Length = n
            && hk.Params.Length = n
        if not lengthsOk then Error "the fold's map carries positional records of another length than its operands"
        elif rest |> List.exists (fun j ->
                 h.ArrayTypes.[j] <> h.ArrayTypes.[keep] || h.SDimsPerArray.[j] <> h.SDimsPerArray.[keep]
                 || h.KernelInputRanks.[j] <> 0 || h.KernelInputRanks.[keep] <> 0
                 || hk.Params.[j].Type <> hk.Params.[keep].Type) then
            Error "the duplicate operand positions do not read the same cell the same way (types, peels or parameter types differ)"
        else
        let keepIdx (l: 'a list) = l |> List.indexed |> List.filter (fun (i, _) -> not (drop.Contains i)) |> List.map snd
        let kp = hk.Params.[keep]
        let body =
            rest |> List.fold (fun b j ->
                let dv = hk.Params.[j].VarId
                mapIRExpr (fun e -> match e with IRVar (id, _) when id = dv -> IRVar (kp.VarId, kp.Type) | e -> e) b) hk.Body
        let ps = keepIdx hk.Params |> List.mapi (fun i p -> { p with Index = i })
        let lam =
            { mkLambdaCallable builder ps body hk.RetType hk.Captures false [] [] false false 256 false
                with SourceName = hk.SourceName; IsRepro = hk.IsRepro }
        let kv = IRVar (lam.Id, IRTArrow (ps |> List.map (fun p -> SVal p.Type), hk.RetType, None))
        let arrays = keepIdx h.Arrays
        let single = arrays.Length = 1
        let shared = if single then [] else h.SharedIndexTypes
        let sdims = keepIdx h.SDimsPerArray
        let loop =
            match h.Loop with
            | IRMethodFor mf when mf.Arrays.Length = n && mf.Identities.Length = n && mf.ArrayTypes.Length = n && mf.SDimsPerArray.Length = n ->
                let mfs = keepIdx mf.SDimsPerArray
                Ok (IRMethodFor { mf with Arrays = keepIdx mf.Arrays; Identities = keepIdx mf.Identities
                                          ArrayTypes = keepIdx mf.ArrayTypes; SDimsPerArray = mfs
                                          TotalSDims = List.sum mfs
                                          SharedIndexTypes = (if single then [] else mf.SharedIndexTypes) })
            | IRObjectFor o when o.InputRanks.Length = n ->
                Ok (IRObjectFor { o with Kernel = kv; InputRanks = keepIdx o.InputRanks })
            | _ -> Error "the fold's map loop does not carry one record per operand"
        match loop with
        | Error why -> Error why
        | Ok loop' ->
        Ok ({ h with Loop = loop'; Kernel = kv; Arrays = arrays; Identities = keepIdx h.Identities
                     ArrayTypes = keepIdx h.ArrayTypes; SharedIndexTypes = shared
                     SymcomStates = keepIdx h.SymcomStates; TriangularLevels = keepIdx h.TriangularLevels
                     SDimsPerArray = sdims; KernelInputRanks = keepIdx h.KernelInputRanks
                     IsCoIteration = not single },
            lam, Some lam)
    let mintedKernels = ResizeArray<IRCallable>()
    let bindingById = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    let nameOfLet (xid: IRId) (p: ApplyInfo) =
        match Map.tryFind xid bindingById, resolve p.Kernel with
        | Some b, _ -> b.Name
        | _, Some c when c.SourceName.StartsWith "lambda in " && c.SourceName.Length > 10 -> c.SourceName.Substring 10
        | _ -> $"__v{xid}"
    // P5 step 2: the names a record's cost line spells (module bindings, the
    // callables' parameters; x itself by nameOfLet), and its legality clause.
    let costNames = System.Collections.Generic.Dictionary<IRId, string>()
    for b in modul.Bindings do costNames.[b.Id] <- b.Name
    for KeyValue (_, f) in funcs do
        for pp in f.Params do
            if not (costNames.ContainsKey pp.VarId) then costNames.[pp.VarId] <- pp.Name
    let costNameOf (id: IRId) = match costNames.TryGetValue id with | true, n -> n | _ -> $"__v{id}"
    let ceLegality =
        Blade.CostModel.legalityLine "single-consumer compute elision (snapshot: no write or provider effect from the let through its consumer; failure site: an IRTotality-total producer over plain extents; once-only effects: a write-free producer; evaluation count: one reader, each cell once; empty input: the fused fold's seed by W4-3's route rule)"

    // the region's own repro demand (a `where repro` body): a strict seed route
    let regionRepro = ref false
    // TOP-LEVEL MODE (P3 remainder, item 2): the module's binding list run as
    // ONE spine when a print selection (`BLADE_PRINT` / `--print`) is set:
    // Some (the selected names). A top-level `|> compute` the selection
    // EXCLUDES is materialized only for printing it -- which no longer
    // happens -- so it is a candidate like a body let; a selected (printed)
    // one, one another module names (`foreignRefs`), one with side-table
    // operands or a provider read / write is not (silently: today's form).
    let topMode : Set<string> option ref = ref None
    let topCandidate (xid: IRId) =
        match topMode.Value with
        | None -> true
        | Some sel ->
            match Map.tryFind xid bindingById with
            | Some b ->
                not (Blade.PrintSelection.mayName sel b.Name)
                && (bindingSideOperands modul xid).IsEmpty
                && not (modul.ProviderReads.ContainsKey xid) && not (modul.ProviderWrites.ContainsKey xid)
            | None -> false
    // ONE LEVEL DEEPER (P3 remainder, item 1): a producer whose operand is a
    // computation this pass already elided into it (`x0 -> x1 -> fold`: x0's
    // map is x1's operand after x0's elision) is admitted when that inner map
    // passes the producer admission itself -- plain dense, its operands stored
    // names or ranges, a kernel declaring nothing, total (IRTotality), write-
    // free, extents plain. Its own interval was audited when it was elided;
    // the outer interval is audited below, so x0's inputs are unwritten from
    // x0's let through the fold.
    let streamedIds = streamedProviderIds modul
    let innerProducerOk (q: ApplyInfo) =
        q.Arrays |> List.forall (function IRVar (id, _) -> not (deferredIds.Contains id) && not (streamedIds.Contains id) | IRRange _ -> true | _ -> false)
        && tilePlainInfo q
        && (match resolve q.Kernel with
            | Some qk -> tilePlainKernel qk q.Arrays.Length
            | None -> false)
        && embeddedExtentExprs (IRApplyCombinator q) |> List.forall plainExtent
        && (match Blade.IRTotality.auditApply resolve (fun _ -> None) deferredW q with Ok () -> true | Error _ -> false)
        && not (mayWrite (IRApplyCombinator q))
    // ---- one statement spine -----------------------------------------------
    let rec descend (subject: string) (root: IRExpr) (e: IRExpr) : IRExpr =
        match e with
        | IRLet _ -> processSpine subject root e
        | IRIf (c, t, f) -> IRIf (c, descend subject root t, descend subject root f)
        | IRMatch (s, cases) -> IRMatch (s, cases |> List.map (fun c -> { c with Body = descend subject root c.Body }))
        | _ -> e
    and processSpine (subject: string) (root: IRExpr) (e: IRExpr) : IRExpr =
        let rec flatten acc e =
            match e with
            | IRLet (id, v, b) -> flatten ((id, v) :: acc) b
            | t -> (List.rev acc, t)
        let (letList, tail0) = flatten [] e
        // nested blocks and branches first: each is its own spine
        // (top-level mode: the bindings' own blocks were processed already)
        let nested (v: IRExpr) = if topMode.Value.IsSome then v else descend subject root v
        let lets = letList |> List.map (fun (id, v) -> (id, nested v)) |> Array.ofList
        let mutable tail = nested tail0
        let dropped = System.Collections.Generic.HashSet<int>()
        for i in 0 .. lets.Length - 1 do
            let (xid, xv) = lets.[i]
            match xv with
            | IRCompute (IRApplyCombinator p) when refsOf xid root > 0 && topCandidate xid ->
                let xn = nameOfLet xid p
                let subj = $"{subject}.{xn}"
                // the statement after x that first reads it (index or tail)
                let later = [ for j in i + 1 .. lets.Length - 1 -> (Some j, snd lets.[j]) ] @ [ (None, tail) ]
                match later |> List.tryFind (fun (_, v) -> refsOf xid v > 0) with
                | None when topMode.Value.IsSome && (modul.ProviderWrites |> Map.exists (fun _ w -> w.SourceId = xid)) ->
                    recordComputeElision subj (Blade.Effects.Declined $"`{xn}` is the source of a provider write (the write reads its stored cells)") []
                | None ->
                    // read only outside its spine: a lifted kernel's capture
                    recordComputeElision subj (Blade.Effects.Declined $"`{xn}` is read only outside its own block (a capture by a kernel or closure)") []
                | Some (at, sv) ->
                // counted per REGION: shape specializations copy a body with its ids
                let total = refsOf xid root
                let here = refsOf xid sv
                let isTail = at.IsNone
                let between = [ for j in i + 1 .. (match at with Some j -> j - 1 | None -> lets.Length - 1) -> lets.[j] ]
                let decline (why: string) (ev: string list) = recordComputeElision subj (Blade.Effects.Declined why) ev
                // ---- readers ---------------------------------------------------
                if Set.contains xid captured then decline $"`{xn}` is captured by a kernel or closure" []
                elif assigned.Contains xid then decline $"`{xn}` is assigned" []
                // a reader hidden in a TYPE (a range extent `0..extents(x)`, an
                // index record's extent) is invisible to the child walk
                elif extentRefsOf xid root > 0 then decline $"`{xn}` is read by an extent expression a type carries (`extents({xn})`)" []
                else
                // ---- the consumer --------------------------------------------
                let consumer =
                    match sv with
                    | _ when here <> total -> Error $"`{xn}` has more than one reader ({total} references; a single consumer is required)"
                    | ReduceOfNamed (y, _, k, init) when y = xid ->
                        // the fold's OWN operand is its one read: another in the
                        // kernel or the seed would outlive the dropped let
                        if refsOf xid k + (init |> Option.map (refsOf xid) |> Option.defaultValue 0) > 0 then
                            Error $"`{xn}` is read again by the fold's kernel or seed"
                        else Ok (Choice1Of3 (k, init))
                    | IRCompute (IRApplyCombinator h)
                        when (h.Arrays |> List.filter (isX xid) |> List.length) = 1
                             && refsOf xid h.Kernel = 0 -> Ok (Choice2Of3 h)
                    // FOLD-OF-MAP (round 4 wave 2, PM12, rule `fold-map-elision`):
                    // x's only reads are operand positions of the MAP one fold
                    // folds (`reduce(c * c, (+)) / n`), anywhere in the statement.
                    // Off: today's decline, byte for byte.
                    | _ when foldMapElideEnabled () && (foldMapConsumerOf xid sv).IsSome ->
                        Ok (Choice3Of3 (foldMapConsumerOf xid sv).Value)
                    | _ ->
                        match describeRead xid isTail sv with
                        | Some what -> Error $"`{xn}` {what}"
                        | None when total > 1 -> Error $"`{xn}` has more than one reader ({total} references; a single consumer is required)"
                        | None -> Error $"`{xn}` is read by an expression that is not a fold or a pointwise map"
                match consumer with
                | Error why -> decline why []
                | Ok kind ->
                // a fold-of-map candidate's decisions are its own rule's
                let decline (why: string) (ev: string list) =
                    match kind with
                    | Choice3Of3 _ -> recordPendingElision "fold-map-elision" subj (Blade.Effects.Declined why) ev
                    | _ -> decline why ev
                // ---- the producer --------------------------------------------
                let pk = resolve p.Kernel
                // PM28: a gather producer admitted by its certificate instead
                // of the totality whitelist (fold consumers only; see the
                // FOLD branch for the fold-side half of the certificate)
                let gatherEv : string list option ref = ref None
                let operandOk (a: IRExpr) =
                    match a with
                    | IRVar (id, _) -> not (deferredIds.Contains id)
                    | IRRange _ -> true
                    | IRCompute (IRApplyCombinator q) -> innerProducerOk q
                    | _ -> false
                let producerProblem =
                    match pk with
                    | None -> Some "the producer kernel does not resolve to a callable"
                    | Some pkc ->
                        if not (tilePlainInfo p) then Some "the producer is not a plain dense map or co-iteration (structured storage, symmetry, or a non-scalar kernel)"
                        elif not (tilePlainKernel pkc p.Arrays.Length) then Some "the producer kernel declares symmetry, a parallel strategy or arity polymorphism"
                        elif p.Arrays |> List.exists (function IRVar (id, _) -> streamedIds.Contains id | _ -> false) then Some streamedOperandWhy
                        elif not (p.Arrays |> List.forall operandOk) then Some "an operand of the producer is not a stored array, a range, or an elided map that itself passes the producer admission (a deferred operand, or a deeper chain, declines)"
                        // the extents the producer's types carry (a range bound
                        // `0..(10 / z)`) are evaluated where it runs: they must
                        // be as total and write-free as its kernel
                        elif not (embeddedExtentExprs (IRApplyCombinator p) |> List.forall plainExtent) then
                            Some "an extent of the producer is not a literal, a scalar name or an extent read (it could fail or write where the consumer runs)"
                        else
                        let totality =
                            match Blade.IRTotality.auditApply resolve (fun _ -> None) deferredW p with
                            | Ok () -> Ok ()
                            | Error why ->
                                let notTotal = $"the producer kernel is not proved total ({why})"
                                match kind with
                                | Choice1Of3 _ when why.Contains "BL8006" ->
                                    (match gatherTerminalCertificate resolve deferredW p pkc with
                                     | Ok ev -> gatherEv.Value <- Some ev; Ok ()
                                     | Error g -> Error $"{notTotal}; gather terminal: {g}")
                                | _ -> Error notTotal
                        match totality with
                        | Error why -> Some why
                        | Ok () ->
                        if mayWrite (IRApplyCombinator p) then Some "the producer may write (a writing producer is evaluated once, at its definition: G45)"
                        elif p.Arrays.Length > 1
                             && (let exts = p.ArrayTypes |> List.map staticExtents
                                 exts |> List.exists (List.exists Option.isNone) || (exts |> List.distinct |> List.length) > 1) then
                            Some "a co-iteration producer over run-time or unequal extents (its agreement check would move to the consumer)"
                        else None
                match producerProblem with
                | Some why -> decline why []
                | None ->
                // ---- the interval: nothing may write ---------------------------
                let interval =
                    (between |> List.map (fun (id, v) -> ("an intervening statement", Some id, payloadOf id v)))
                    @ [ ("the consumer statement", at |> Option.map (fun j -> fst lets.[j]),
                         (match at with Some j -> payloadOf (fst lets.[j]) sv | None -> sv)) ]
                let writer =
                    interval |> List.tryPick (fun (who, idOpt, v) ->
                        let prov = match idOpt with Some id -> intervalHasProviderEffect modul id | None -> false
                        // the extents a statement's types carry run there too
                        // (`0..bump(a)`): audited like its value
                        if prov || mayWrite v || (embeddedExtentExprs v |> List.exists mayWrite) then Some who else None)
                match writer with
                | Some who -> decline $"{who} may write between the compute and its consumer (the producer would read the inputs after the write)" []
                | None ->
                let common =
                    [ (match gatherEv.Value with
                       | None -> "producer total (IRTotality whitelist) and write-free"
                       | Some _ -> "producer write-free; total but for the one checked subscript the gather certificate names")
                      $"inputs unwritten between ({between.Length} intervening statement(s) and the consumer write-free, the shared write analysis)" ]
                    @ (if p.Arrays |> List.exists (function IRCompute _ -> true | _ -> false) then
                           [ "one level deeper: the producer's operand is an elided map that passes the producer admission itself (its own interval audited at its elision)" ]
                       else [])
                    @ (match topMode.Value with
                       | Some _ -> [ $"top level: `{xn}` is not in the print selection (BLADE_PRINT / --print), so it was materialized only to be printed; the print selection licensed the elision" ]
                       | None -> [])
                match kind with
                | Choice1Of3 (k, init) ->
                    // ---- FOLD -----------------------------------------------
                    let fold = elidedFoldSeed regionRepro.Value p.OutputType (resolve k) init
                    // PM28: the fold-side half of the gather certificate --
                    // the fold is the ADJACENT statement, its kernel and seed
                    // closed-total (and write-free: the interval audit above)
                    let gatherFold =
                        match gatherEv.Value with
                        | None -> Ok []
                        | Some gev ->
                            let pre = "the producer kernel is not proved total (body may panic (a subscript not proven in range: BL8006)); gather terminal: "
                            if not between.IsEmpty then
                                Error $"{pre}{between.Length} statement(s) between the gather and its fold (v1 requires the fold to be the adjacent statement: no observable event between)"
                            else
                            match resolve k with
                            | None -> Error $"{pre}the fold kernel does not resolve to a callable"
                            | Some fkc ->
                            match Blade.IRTotality.auditCallable resolve (fun _ -> None) deferredW fkc [] with
                            | Error why -> Error $"{pre}the fold kernel is not proved total ({why}); a fold failure could precede the gather failure"
                            | Ok () ->
                            let seedTotal =
                                match init with
                                | None -> Ok 0L
                                | Some s -> Blade.IRTotality.walk resolve (fun _ -> None) deferredW Map.empty Map.empty Set.empty Set.empty Set.empty s
                            match seedTotal with
                            | Error why -> Error $"{pre}the fold seed is not proved total ({why}); a seed failure would precede the gather failure"
                            | Ok _ ->
                            Ok (gev @ [ "the fold is the adjacent statement; its kernel and seed are closed-total (IRTotality) and write-free, so the cells it folded before a failing one leave no trace" ])
                    match fold, gatherFold with
                    | Error why, _ -> decline why common
                    | _, Error why -> decline why common
                    | Ok (seed, seedNote), Ok gatherNotes ->
                        let stored (e: IRExpr) = match e with IRVar (id, _) when id = xid -> Some xn | _ -> Blade.CostModel.storedArrayName costNameOf e
                        let costLine = Blade.CostModel.foldElisionLine resolve costNameOf stored xn [] p (resolve k)
                        let ev = [ "single consumer fold `reduce` (the whole value of its statement)" ] @ common @ gatherNotes @ [ seedNote; "the fold reads the map in place: no pool, no separate loop"; ceLegality; costLine ]
                        if not enabled then decline "disabled by BLADE_COMPUTE_ELIDE" ev
                        elif not gatherNotes.IsEmpty && not (gatherElideEnabled ()) then decline "disabled by BLADE_GATHER_ELIDE" ev
                        else
                            recordComputeElision subj Blade.Effects.Applied ev
                            let folded = IRReduceCompute (IRApplyCombinator p, k, seed)
                            dropped.Add i |> ignore
                            match at with
                            | Some j -> lets.[j] <- (fst lets.[j], folded)
                            | None -> tail <- folded
                | Choice2Of3 h ->
                    // ---- MAP (realized by elementwise fusion) ---------------
                    let pos = h.Arrays |> List.findIndex (isX xid)
                    let mapProblem =
                        match tileKernelOf localCallables h with
                        | _ when not (Blade.Toolchain.envFlag "BLADE_FUSION" true) ->
                            Some "elementwise fusion, the map consumer's realization, is disabled (BLADE_FUSION)"
                        | None -> Some "the consumer's kernel is not a resolvable callable"
                        | Some hk ->
                            if not (tilePlainInfo h) then Some "the consumer is not a plain dense map or co-iteration"
                            elif not (tilePlainKernel hk h.Arrays.Length) then Some "the consumer kernel declares symmetry, a parallel strategy or arity polymorphism"
                            elif not (tilePureBody resolver Set.empty hk.Body) then Some "the consumer kernel is not repeatable"
                            elif (match pk with Some pkc -> not (tilePureBody resolver Set.empty pkc.Body) | None -> true) then Some "the producer kernel is not repeatable"
                            // A kernel that NAMES its parameter twice (`v * v`) still reads
                            // the CELL once per evaluation (gap G108 (1)): fusion binds a
                            // twice-named spliced operand ONCE per cell (a let around the
                            // body, IRMono.fuseElementwiseChainsModule), exactly as the
                            // fold-of-map arm below admits; the producer is pure and total
                            // (admitted above), so binding it unconditionally is unobservable.
                            elif (let occ = refsOf hk.Params.[pos].VarId hk.Body in occ < 1 || occ > 2) then
                                Some $"the consumer kernel reads `{xn}`'s cell {refsOf hk.Params.[pos].VarId hk.Body} times (once or twice is required: fusion binds a twice-read operand once per cell)"
                            elif h.Arrays.Length + p.Arrays.Length - 1 > 8 then Some "the fused operand count would exceed elementwise fusion's fan-in cap (8)"
                            elif h.SharedIndexTypes.IsEmpty && h.Arrays.Length + p.Arrays.Length - 1 > 1 && p.SharedIndexTypes.IsEmpty then
                                Some "the fused node would read as an outer product (no shared index records)"
                            else None
                    match mapProblem with
                    | Some why -> decline why common
                    | None ->
                        let repl = IRCompute (IRApplyCombinator p)
                        let subst (e: IRExpr) = mapIRExpr (fun n -> match n with IRVar (y, _) when y = xid -> repl | n -> n) e
                        let h' = { h with Arrays = h.Arrays |> List.map subst; Loop = subst h.Loop }
                        let stored (e: IRExpr) = match e with IRVar (id, _) when id = xid -> Some xn | _ -> Blade.CostModel.storedArrayName costNameOf e
                        let outName = match at with Some j -> costNameOf (fst lets.[j]) | None -> "the returned value"
                        let costLine = Blade.CostModel.mapElisionLine resolve costNameOf stored stored xn outName [] p h h'
                        let ev = [ "single consumer pointwise map (it reads the cell once, at its own coordinate)" ] @ common @ [ "realized by elementwise fusion: the producer is spliced into the consumer's nest, no pool for it"; ceLegality; costLine ]
                        if not enabled then decline "disabled by BLADE_COMPUTE_ELIDE" ev
                        else
                            let hkId = match h.Kernel with IRVar (kid, _) -> kid | _ -> -1
                            pending.Add { PendingRule = "compute-elision"; PendingSubject = subj; HostKernel = hkId; PendingEvidence = ev; SplicedKernel = None }
                            dropped.Add i |> ignore
                            let v' = IRCompute (IRApplyCombinator h')
                            match at with
                            | Some j -> lets.[j] <- (fst lets.[j], v')
                            | None -> tail <- v'
                | Choice3Of3 h ->
                    // ---- FOLD-OF-MAP (realized by elementwise fusion) -------
                    // The fold node is kept exactly (kernel, seed, extent, axes):
                    // only its operand map's x operands change, collapsed to one
                    // and replaced by x's producer map, which fusion splices into
                    // the fold's map kernel (bound once per cell when read twice).
                    let shaped =
                        match tileKernelOf localCallables h with
                        | _ when not (Blade.Toolchain.envFlag "BLADE_FUSION" true) ->
                            Error "elementwise fusion, the fold map's realization, is disabled (BLADE_FUSION)"
                        | None -> Error "the fold's map kernel is not a resolvable callable"
                        | Some hk ->
                            if not (tilePlainInfo h) then Error "the fold's map is not a plain dense map or co-iteration"
                            elif not (tilePlainKernel hk h.Arrays.Length) then Error "the fold's map kernel declares symmetry, a parallel strategy or arity polymorphism"
                            elif not (tilePureBody resolver Set.empty hk.Body) then Error "the fold's map kernel is not repeatable"
                            // the minted kernels (the collapse's, fusion's) do not carry a
                            // repro policy: a strict region keeps its maps as written
                            elif regionRepro.Value || hk.IsRepro then Error "a `where repro` region (v1: the collapsed and fused map kernels would not carry the repro policy)"
                            // a lifted callable reachable from a kernel this rewrite or fusion
                            // renames -- the fold's map kernel (collapse, fusion), the
                            // producer's and any inner producer's (fusion freshens their
                            // parameters and lets) -- that CAPTURES one of that kernel's
                            // binders would keep naming a binder that no longer exists
                            elif (let kernels =
                                      [ Some hk; pk ] @ (p.Arrays |> List.map (function IRCompute (IRApplyCombinator q) -> resolve q.Kernel | _ -> None))
                                      |> List.choose id
                                  kernels |> List.exists (fun (k: IRCallable) ->
                                      let binders =
                                          let s = System.Collections.Generic.HashSet<IRId>(k.Params |> List.map (fun q -> q.VarId))
                                          iterIRExpr (function IRLet (lid, _, _) -> s.Add lid |> ignore | _ -> ()) k.Body
                                          s
                                      let rec reach (seen: Set<IRId>) (body: IRExpr) : bool =
                                          let hit = ref false
                                          iterIRExpr (fun n ->
                                              if not hit.Value then
                                                  match n with
                                                  | IRVar (cid, _) when not (seen.Contains cid) ->
                                                      match resolver.TryGetValue cid with
                                                      | true, cc ->
                                                          if cc.Captures |> List.exists (fun cap -> binders.Contains cap.Id) then hit.Value <- true
                                                          elif reach (seen.Add cid) cc.Body then hit.Value <- true
                                                      | _ -> ()
                                                  | _ -> ()) body
                                          hit.Value
                                      reach (Set.singleton k.Id) k.Body)) then
                                Error "a lifted callable reachable from the fold's map kernel or the producer's kernel captures one of that kernel's parameters or lets (the collapse and fusion rename those binders)"
                            elif (match pk with Some pkc -> not (tilePureBody resolver Set.empty pkc.Body) | None -> true) then Error "the producer kernel is not repeatable"
                            else
                            match collapseOperand xid h hk with
                            | Error why -> Error why
                            | Ok (h1, hk1, minted) ->
                                let pos = h1.Arrays |> List.findIndex (isX xid)
                                let occ = refsOf hk1.Params.[pos].VarId hk1.Body
                                if occ < 1 || occ > 2 then
                                    Error $"the fold's map kernel reads `{xn}`'s cell {occ} times (once or twice is required: fusion binds a twice-read operand once per cell)"
                                // fusion splices left to right under one fan-in cap: v1 admits
                                // a fold map whose ONLY computed operand is x's producer (another
                                // computed operand could exhaust the cap first), counting the
                                // producer's own operands after ITS inner fusion
                                elif h1.Arrays |> List.exists (function IRCompute _ -> true | _ -> false) then
                                    Error "another operand of the fold's map is a computed map (v1 splices only the elided producer, so fusion's fan-in order cannot leave it unspliced)"
                                elif h1.Arrays.Length - 1 + (p.Arrays |> List.sumBy (function IRCompute (IRApplyCombinator q) -> q.Arrays.Length | _ -> 1)) > 8 then
                                    Error "the fused operand count would exceed elementwise fusion's fan-in cap (8)"
                                elif h1.SharedIndexTypes.IsEmpty && h1.Arrays.Length + p.Arrays.Length - 1 > 1 && p.SharedIndexTypes.IsEmpty then
                                    Error "the fused node would read as an outer product (no shared index records)"
                                else Ok (h1, minted, h.Arrays.Length - h1.Arrays.Length)
                    match shaped with
                    | Error why -> decline why common
                    | Ok (h1, minted, collapsed) ->
                        let repl = IRCompute (IRApplyCombinator p)
                        let subst (e: IRExpr) = mapIRExpr (fun n -> match n with IRVar (y, _) when y = xid -> repl | n -> n) e
                        let h' = { h1 with Arrays = h1.Arrays |> List.map subst; Loop = subst h1.Loop }
                        let foldKernel =
                            let mutable k = None
                            iterIRExpr (fun n ->
                                match n, foldOperandMap n with
                                | (IRReduce (_, fk, _) | IRReduceCompute (_, fk, _)), Some hh when hh.Arrays |> List.exists (isX xid) -> k <- Some fk
                                | _ -> ()) sv
                            k
                        let stored (e: IRExpr) = match e with IRVar (id, _) when id = xid -> Some xn | _ -> Blade.CostModel.storedArrayName costNameOf e
                        // the pricing resolver must see the collapsed kernel it mints
                        let resolveMinted (k: IRExpr) =
                            match k, minted with
                            | IRVar (kid, _), Some lam when kid = lam.Id -> Some lam
                            | _ -> resolve k
                        let costLine = Blade.CostModel.foldMapElisionLine resolveMinted costNameOf stored stored xn p h h' (foldKernel |> Option.bind resolve)
                        let ev =
                            [ "single consumer: the operand map of one fold in its statement (the fold node -- kernel, seed, extent -- is kept as written)" ]
                            @ (if collapsed > 0 then [ $"`{xn}` read at {collapsed + 1} operand positions of one co-iteration: collapsed to one (the same stored cell at the same coordinate), so the producer runs once per cell" ] else [])
                            @ common
                            @ [ "realized by elementwise fusion: the producer is spliced into the fold's map kernel, no pool for it"
                                Blade.CostModel.legalityLine "fold-of-map compute elision (snapshot: no write or provider effect from the let through the consumer statement; failure site: an IRTotality-total producer over plain extents, the fold's own failures unchanged; evaluation count: one reader, each cell once after the operand collapse; fold order and seed: the fold node is unchanged, bitwise under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off; a licensed fold may take another licensed form)"
                                costLine ]
                        if not enabled then decline "disabled by BLADE_COMPUTE_ELIDE" ev
                        else
                            minted |> Option.iter mintedKernels.Add
                            let hkId = match h'.Kernel with IRVar (kid, _) -> kid | _ -> -1
                            let producerKernel = match p.Kernel with IRVar (kid, _) -> Some kid | _ -> None
                            pending.Add { PendingRule = "fold-map-elision"; PendingSubject = subj; HostKernel = hkId; PendingEvidence = ev; SplicedKernel = producerKernel }
                            dropped.Add i |> ignore
                            let rebuild (op: IRExpr) =
                                match op with
                                | IRCompute (IRApplyCombinator _) -> IRCompute (IRApplyCombinator h')
                                | _ -> IRApplyCombinator h'
                            let isTarget (op: IRExpr) =
                                match op with
                                | IRApplyCombinator hh | IRCompute (IRApplyCombinator hh) -> hh.Arrays |> List.exists (isX xid)
                                | _ -> false
                            let v' =
                                mapIRExpr (fun n ->
                                    match n with
                                    | IRReduce (op, fk, init) when isTarget op -> IRReduce (rebuild op, fk, init)
                                    | IRReduceCompute (op, fk, seed) when isTarget op -> IRReduceCompute (rebuild op, fk, seed)
                                    | n -> n) sv
                            match at with
                            | Some j -> lets.[j] <- (fst lets.[j], v')
                            | None -> tail <- v'
            | _ -> ()
        let kept = lets |> Array.indexed |> Array.filter (fun (i, _) -> not (dropped.Contains i)) |> Array.map snd
        Array.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) kept tail

    let functions' =
        modul.Functions |> List.map (fun f ->
            if f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || hasTypeVarsInSignature f then f
            else
                let subject = if System.String.IsNullOrEmpty f.SourceName then f.Name else f.SourceName
                regionRepro.Value <- f.IsRepro
                let body' = descend subject f.Body f.Body
                regionRepro.Value <- false
                { f with Body = body' })
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            match b.Value with
            | IRLet _ | IRIf _ | IRMatch _ -> { b with Value = descend b.Name b.Value b.Value }
            | _ -> b)
    // ---- top level under a print selection (P3 remainder, item 2) -----------
    let printSelection =
        Blade.PrintSelection.names ()
    let bindings'' =
        match printSelection with
        | Some sel when singleModule ->
            // the binding list as ONE spine (its tail reads nothing); readers
            // are counted over every binding payload and every function body
            // -- and every provider write's SOURCE (gap G105: a write binding's
            // own value is a unit placeholder, so a map read by a fold AND
            // written to a store counted one reader, was elided, and the write
            // then named an undeclared array)
            let root =
                IRSequence (
                    (bindings' |> List.collect (fun b -> b.Value :: bindingSideOperands modul b.Id))
                    @ (modul.ProviderWrites |> Map.toList |> List.map (fun (_, w) -> IRVar (w.SourceId, IRTUnit)))
                    @ (functions' |> List.map (fun f -> f.Body)))
            let spine = List.foldBack (fun (b: IRBinding) acc -> IRLet (b.Id, b.Value, acc)) bindings' (IRLit (IRLitBool false))
            topMode.Value <- Some sel
            let spine' = processSpine "top level" root spine
            topMode.Value <- None
            let rec flatten (e: IRExpr) =
                match e with
                | IRLet (id, v, rest) -> (id, v) :: flatten rest
                | _ -> []
            let values = flatten spine' |> Map.ofList
            bindings' |> List.choose (fun b -> Map.tryFind b.Id values |> Option.map (fun v -> { b with Value = v }))
        | _ -> bindings'
    // the fold-of-map elisions' collapsed map kernels (each referenced by its
    // rewritten map; fusion resolves them module-locally)
    ({ modul with Functions = functions' @ List.ofSeq mintedKernels; Bindings = bindings'' }, List.ofSeq pending)

/// The map-consumer elisions' records, once elementwise fusion has run: the
/// host kernel survives with a computed operand only where fusion declined.
let recordComputeElisionMapOutcomes (pending: ComputeElisionPending list) (modul: IRModule) : unit =
    if not pending.IsEmpty then
        let unfused = System.Collections.Generic.HashSet<IRId>()
        // the kernels of the computed OPERANDS still standing (unspliced producers)
        let unspliced = System.Collections.Generic.HashSet<IRId>()
        let scan (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRApplyCombinator info ->
                    (match info.Kernel with
                     | IRVar (kid, _) when info.Arrays |> List.exists (function IRCompute (IRApplyCombinator _) -> true | _ -> false) ->
                         unfused.Add kid |> ignore
                     | _ -> ())
                    for a in info.Arrays do
                        match a with
                        | IRCompute (IRApplyCombinator q) ->
                            (match q.Kernel with IRVar (qk, _) -> unspliced.Add qk |> ignore | _ -> ())
                        | _ -> ()
                | _ -> ()) e
        for f in modul.Functions do scan f.Body
        for b in modul.Bindings do scan b.Value
        for p in pending do
            if unfused.Contains p.HostKernel || (match p.SplicedKernel with Some pk -> unspliced.Contains pk | None -> false) then
                // the cost line priced the FUSED form, which was not realized
                let evidence =
                    p.PendingEvidence |> List.collect (fun e ->
                        if e.StartsWith "cost: " && not (e.StartsWith "cost: not priced") then
                            [ "cost: not realized (fusion declined: the producer is still built, as the consumer's operand; the planned line follows)"; "planned " + e ]
                        else [ e ])
                recordPendingElision p.PendingRule p.PendingSubject
                    (Blade.Effects.Declined "elementwise fusion did not splice the producer into the consumer (its own decline is recorded); the producer is evaluated as the consumer's operand, still once, still at the consumer")
                    evidence
            else recordPendingElision p.PendingRule p.PendingSubject Blade.Effects.Applied p.PendingEvidence
