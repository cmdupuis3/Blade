// The decompact row fold and mat-vec, rule decompact-row-fold (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeDecompact

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeComputeElision

/// BLADE_DECOMPACT_FOLD=0|off keeps the dense decompaction under a row fold
/// (round 4 wave 3, PM29, rule `decompact-row-fold`,
/// `decompactRowFoldModule`): the admission still runs and records DECLINED
/// ("disabled by BLADE_DECOMPACT_FOLD"). Unset = on.
let decompactFoldEnabled () = gateOn "BLADE_DECOMPACT_FOLD"

/// The FUNCTION-BODY arm (gap G123, round 4 wave 5; body lets G167, wave 6)
/// is DEFAULT ON since round 4 wave 7 (lane c: the fourth class-closure round
/// of the parameter-storage class found no member after the capturing-callee,
/// tuple-returning and cross-module `>>=` members; measured on three mat-vec
/// calls at n = 1201). `BLADE_DECOMPACT_FOLD=top` = the old default (the
/// top-level arms only; a knob mirror); `body` reads as on. Read per call.
let decompactFoldBodyEnabled () =
    match System.Environment.GetEnvironmentVariable "BLADE_DECOMPACT_FOLD" with
    | null -> true
    | v -> v.Trim().ToLowerInvariant() <> "top"

/// The statement a decompaction row fold / mat-vec is judged at (gap G123,
/// round 4 wave 5: a module binding, or a function body's let / return):
/// its subject for the record, its value and type, whether it carries a side
/// payload, and where its pool / vector operands are bound (Locate: name,
/// mutability, the storage proof; None = not bound where v1 reads it).
type DecompactHost =
    { Subject: string
      Value: IRExpr
      Type: IRType
      SideFree: bool
      Locate: IRId -> (string * bool * (unit -> Result<string, string>)) option
      PoolNotPlaced: string
      VectorNotPlaced: string }

/// PM29: THE ROW FOLD OF A DECOMPACTED POOL READS THE POOL. A top-level
/// `let r = reduce(decompact(S, d), k[, s])` over a stored rank-2 real
/// SymIdx / AntisymIdx pool S (d = 0 or 1: for a rank-2 group both
/// decompactions are the full logical matrix, D(i, j) = S(i, j)) allocates
/// the n x n dense buffer, scatters the pool into it with its mirror / sign,
/// and folds its rows. Rewritten here to the written-out row walk the orbit
/// action takes (`method_for(range<I>) <@> lambda(i) -> reduce(method_for(
/// range<I>) <@> lambda(j) -> S(i, j), k, s)`): every logical cell is the
/// canonical read with its sign applied per cell (the antisymmetric diagonal
/// reads 0.0, as the decompaction writes it), so the row's cells are the
/// decompaction's cells, in its order, from the same seed -- no n x n buffer.
/// Admission (fail closed): S a top-level binding before r, proved stored
/// (`storedArrayProof`); the fold kernel a resolved serial two-parameter
/// Float64 callable, closed-total (IRTotality: the walk / action may visit
/// cells in another GLOBAL order, so a kernel failure must be impossible)
/// and write-free (the decompaction was a snapshot; nothing may write S
/// while the fold reads it in place), the seed likewise; an unseeded fold
/// takes the fused fold's route seed (`elidedFoldSeed`: a builtin `+` / `*`
/// over a literal extent >= 1). Strict builds: bitwise (the action and the
/// walk feed each row chain its source order; the dense row fold is the same
/// ascending chain). Licensed builds may take another licensed form.
let decompactRowFoldModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let resolve = resolveCallableIn funcs
    let writes = scheduleMayWriteIn programFuncs modul
    let minted = ResizeArray<IRCallable * IRId option>()
    let scalar64 = IRTScalar ETFloat64
    let record subject outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "decompact-row-fold"; Version = 1; Span = declSpanOf subject
              Subject = subject; Outcome = outcome; Evidence = evidence }
    let owned = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    // the module's deferred-let census (as compute elision's): a kernel
    // reading a deferred computation is not audited as total
    let deferredIds =
        let s = System.Collections.Generic.HashSet<IRId>()
        let census (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRLet (id, v, _) when isDeferredForm v -> s.Add id |> ignore
                | _ -> ()) e
        for f in modul.Functions do census f.Body
        for b in modul.Bindings do
            census b.Value
            if isDeferredForm b.Value then s.Add b.Id |> ignore
        s
    // the census widened by the program's deferred-value classifier (gap G98
    // class audit) for the totality audits below
    let deferredW = widenDeferred modul (modul.Functions @ programFuncs) deferredIds.Contains
    let coordinateType (ix: IRIndexType) =
        let tag = match ix.Tag with Some n -> IRefNamed n | None -> IRefAnon (ix.Id, ix.Extent)
        IRTIdxTagged (IRTScalar ETInt64, tag)
    let mapOver (ix: IRIndexType) (kernel: IRCallable) (outputType: IRType) : ApplyInfo =
        let range = IRRange ([ix], None)
        let identity = AIDLiteral (builder.FreshId ())
        let a = { ElemType = coordinateType ix; IndexTypes = [ix]; IsVirtual = true; Identity = None }
        { Loop = IRMethodFor { Arrays = [range]; Identities = [identity]; ArrayTypes = [a]
                               SDimsPerArray = [1]; TotalSDims = 1; SharedIndexTypes = [] }
          Kernel = IRVar (kernel.Id, mkFuncArrow (kernel.Params |> List.map _.Type) scalar64)
          Arrays = [range]; Identities = [identity]; ArrayTypes = [a]; SharedIndexTypes = []
          SymcomStates = [SCNeither]; TriangularLevels = [false]; SDimsPerArray = [1]
          KernelInputRanks = [0]; KernelOutputRank = 0; KernelTDims = []
          SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
          OutputType = outputType; IsCoIteration = false }
    // G102 (round 4 wave 4): the MAT-VEC arm, `y = method_for(decompact(S, d))
    // <@> lambda(row) -> prodsum(row, v)` -> the row walk weighted by v(j)
    // (the orbit action's weighted form for a SymIdx pool): no n x n buffer.
    let rewriteMatVec (h: DecompactHost) (sid: IRId) (sty: IRType) (d: int) (vid: IRId) (vty: IRType) : IRExpr option =
        let unchanged why = record h.Subject (Blade.Effects.Declined $"mat-vec: {why}") [ "the n x n decompaction buffer stays" ]; None
        match stripUnits sty, stripUnits h.Type, stripUnits vty with
        | ArrayElem at, ArrayElem outAt, ArrayElem vat
            when at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1
                 && outAt.ElemType = scalar64 && outAt.IndexTypes.Length = 1
                 && vat.ElemType = scalar64 && vat.IndexTypes.Length = 1 && not vat.IsVirtual ->
            let gix = at.IndexTypes.Head
            let oix = outAt.IndexTypes.Head
            let vix = vat.IndexTypes.Head
            if gix.Rank <> 2 then unchanged "v1 reads a rank-2 compact group only"
            elif not (gix.Symmetry = SymSymmetric || gix.Symmetry = SymAntisymmetric) then
                unchanged "a real SymIdx / AntisymIdx pool only (a Hermitian, wreath or plain group keeps the decompaction)"
            elif d <> 0 && d <> 1 then unchanged "the decompacted dimension is not one of the group's two"
            elif not (oix.Rank = 1 && oix.Symmetry = SymNone && oix.IxKind = IxKPlain && IRTotality.evalInt oix.Extent = IRTotality.evalInt gix.Extent) then
                unchanged "the result's axis is not the group's plain axis"
            elif not (vix.Rank = 1 && vix.Symmetry = SymNone && vix.IxKind = IxKPlain && IRTotality.evalInt vix.Extent = IRTotality.evalInt gix.Extent) then
                unchanged "the vector is not a plain rank-1 Float64 array over the group's extent"
            elif not h.SideFree then
                unchanged "the host has an additional execution payload"
            else
            match IRTotality.evalInt gix.Extent with
            | Some n when n >= 1L ->
                (match h.Locate sid, h.Locate vid with
                 | Some (sName, sMut, sProof), Some (vName, vMut, vProof) ->
                    match sProof (), vProof () with
                    | Error why, _ -> unchanged $"the pool is not proved stored: {why}"
                    | _, Error why -> unchanged $"the vector is not proved stored: {why}"
                    | Ok how, Ok vhow ->
                    let symTxt = if gix.Symmetry = SymSymmetric then "SymIdx" else "AntisymIdx"
                    let ev =
                        [ $"method_for(decompact({sName}, {d})) <@> prodsum(row, {vName}) -> the row walk over the pool weighted by {vName}(j): no n x n buffer ({n} x {n} Float64 cells, {n * n * 8L} bytes)"
                          $"every logical cell is the canonical read of the {symTxt}<2, {n}> pool with its sign applied per cell (the antisymmetric diagonal reads 0.0): the decompaction's row, in its order"
                          $"the pool `{sName}`: {how}; the vector `{vName}`: {vhow}; both read at the host statement, as the decompaction and the row map read them"
                          "prodsum's strict definition is the ascending chain of the rounded products from +0.0: exactly each row walk's `(+)` chain from +0.0 (bitwise under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off -- under contraction the walk's product may fuse into its add, which prodsum's rounded products never do; a licensed build may take another licensed form, as prodsum's own K-lane form is)" ]
                    if not (decompactFoldEnabled ()) then
                        record h.Subject (Blade.Effects.Declined "mat-vec: disabled by BLADE_DECOMPACT_FOLD") ev
                        None
                    else
                    let axis = { oix with Kind = SDimension }
                    let param name : IRParam =
                        let id = builder.FreshId ()
                        { Name = $"{name}_{id}"; VarId = id; Index = 0; Type = coordinateType axis }
                    let ip, jp = param "__dcf_i", param "__dcf_j"
                    let iv, jv = IRVar (ip.VarId, ip.Type), IRVar (jp.VarId, jp.Type)
                    let poolCapture: CaptureInfo = { Id = sid; Name = sName; Type = sty; IsMutable = sMut }
                    let vecCapture: CaptureInfo = { Id = vid; Name = vName; Type = vty; IsMutable = vMut }
                    let rowCapture: CaptureInfo = { Id = ip.VarId; Name = ip.Name; Type = ip.Type; IsMutable = false }
                    let read = IRIndex (IRVar (sid, sty), [ iv; jv ], None)
                    let weight = IRIndex (IRVar (vid, vty), [ jv ], None)
                    let cellBody = IRBinOp (IRElementwise, IRMul, read, weight, SrcLoc.Nowhere)
                    let cell = mkCallable builder defaultLambdaOptions [ jp ] cellBody scalar64 [ poolCapture; vecCapture; rowCapture ] false [] [] false false 256 false
                    let cell = { cell with Name = $"__decompact_cell_{cell.Id}"; SourceName = anonymousCallableName (Some h.Subject) "lambda" }
                    let pa, pb = builder.FreshId (), builder.FreshId ()
                    let addParms : IRParam list =
                        [ { Name = "__dcf_a"; Type = scalar64; Index = 0; VarId = pa }; { Name = "__dcf_b"; Type = scalar64; Index = 1; VarId = pb } ]
                    let add =
                        { mkLambdaCallable builder addParms (IRBinOp (IRElementwise, IRAdd, IRVar (pa, scalar64), IRVar (pb, scalar64), SrcLoc.Nowhere)) scalar64 [] false [] [] false false 256 false
                            with SourceName = anonymousCallableName (Some h.Subject) "(+)" }
                    let rowType = mkArrayLike { ElemType = scalar64; IndexTypes = [ oix ]; IsVirtual = false; Identity = None }
                    let rowFold = IRReduceCompute (IRApplyCombinator (mapOver axis cell rowType), IRVar (add.Id, mkFuncArrow [ scalar64; scalar64 ] scalar64), IRLit (IRLitFloat 0.0))
                    let row = mkCallable builder defaultLambdaOptions [ ip ] rowFold scalar64 [ poolCapture; vecCapture ] false [] [] false false 256 false
                    let row = { row with Name = $"__decompact_rows_{row.Id}"; SourceName = anonymousCallableName (Some h.Subject) "lambda" }
                    minted.Add((add, None))
                    minted.Add((cell, None))
                    minted.Add((row, None))
                    record h.Subject Blade.Effects.Applied ev
                    Some (IRCompute (IRApplyCombinator (mapOver axis row h.Type)))
                 | None, _ -> unchanged h.PoolNotPlaced
                 | _, None -> unchanged h.VectorNotPlaced)
            | _ -> unchanged "the compact group's extent is not a literal >= 1"
        | _ -> unchanged "the pool, the vector or the result is not a Float64 array of one index group"
    let rewrite (h: DecompactHost) : IRExpr option =
        let inner = match h.Value with IRCompute e -> e | e -> e
        // Lowering binds the decompaction to a private let read only by the
        // fold: `let p = decompact(S, d); reduce(p, k[, s])`
        // -- or, the partial fold lowered to its ROW MAP:
        // `let p = decompact(S, d); method_for(p) <@> lambda(row) -> reduce(row, k[, s]) |> compute`
        let rowFoldOf (consumer: ApplyInfo) =
            match consumer.Arrays, resolve consumer.Kernel with
            | [ IRVar (a, _) ], Some row
                when row.Params.Length = 1 && row.Captures.IsEmpty
                     // the rewrite keeps no strategy or licence of the row
                     // kernel: a request, a repro licence or arity
                     // polymorphism declines (PM25's `rowFold` rule)
                     && not row.IsOmpParallel && row.Parallelism.IsEmpty && not row.IsCudaKernel
                     && not row.IsMpiParallel && not row.IsStatic && not row.IsArityPoly && not row.IsRepro
                     && consumer.KernelInputRanks = [ 1 ] && consumer.KernelOutputRank = 0
                     && not consumer.IsCoIteration && not consumer.HasReynolds && consumer.KernelTDims.IsEmpty
                     && (match consumer.Loop with
                         | IRMethodFor mf -> (match mf.Arrays with [ IRVar (v, _) ] -> v = a | _ -> false)
                         | _ -> false) ->
                (match row.Body with
                 | ReduceOfNamed (rp, _, kExpr, init)
                     when rp = row.Params.Head.VarId
                          && not ((collectVarRefsIR kExpr).Contains rp)
                          && not (init |> Option.exists (fun s -> (collectVarRefsIR s).Contains rp)) ->
                     Some (a, kExpr, init)
                 | _ -> None)
            | _ -> None
        // G102 (round 4 wave 4): THE MAT-VEC -- the row map
        // `method_for(D) <@> lambda(row) -> prodsum(row, v)` over the
        // decompaction, v a captured rank-1 array (either operand order).
        // prodsum's strict definition is the ascending chain of the rounded
        // products from +0.0, which is exactly the row walk's
        // `reduce(method_for(range<I>) <@> lambda(j) -> S(i, j) * v(j), (+), 0.0)`.
        let matVecOf (consumer: ApplyInfo) =
            match consumer.Arrays, resolve consumer.Kernel with
            | [ arr ], Some row
                when row.Params.Length = 1
                     && not row.IsOmpParallel && row.Parallelism.IsEmpty && not row.IsCudaKernel
                     && not row.IsMpiParallel && not row.IsStatic && not row.IsArityPoly && not row.IsRepro
                     && consumer.KernelInputRanks = [ 1 ] && consumer.KernelOutputRank = 0
                     && not consumer.IsCoIteration && not consumer.HasReynolds && consumer.KernelTDims.IsEmpty
                     && (match consumer.Loop with
                         | IRMethodFor mf -> (match mf.Arrays with [ x ] -> x = arr | _ -> false)
                         | _ -> false) ->
                let rp = row.Params.Head.VarId
                (match row.Body with
                 | IRProdSum [ IRVar (x, _); IRVar (y, yty) ] when x = rp && y <> rp && (row.Captures |> List.exists (fun c -> c.Id = y)) -> Some (arr, y, yty)
                 | IRProdSum [ IRVar (y, yty); IRVar (x, _) ] when x = rp && y <> rp && (row.Captures |> List.exists (fun c -> c.Id = y)) -> Some (arr, y, yty)
                 | _ -> None)
            | _ -> None
        let matVec =
            match inner with
            // the decompaction let-bound by Lowering, read only by the row map
            | IRLet (pid, IRDecompact (IRVar (sid, sty), d), IRCompute (IRApplyCombinator consumer)) ->
                (match matVecOf consumer with
                 | Some (IRVar (a, _), vid, vty) when a = pid -> Some (sid, sty, d, vid, vty)
                 | _ -> None)
            // or inline as the row map's operand
            | IRApplyCombinator consumer ->
                (match matVecOf consumer with
                 | Some (IRDecompact (IRVar (sid, sty), d), vid, vty) -> Some (sid, sty, d, vid, vty)
                 | _ -> None)
            | _ -> None
        match matVec with
        | Some (sid, sty, d, vid, vty) -> rewriteMatVec h sid sty d vid vty
        | None ->
        let inner =
            match inner with
            | IRLet (pid, (IRDecompact _ as dc), ReduceOfNamed (p, _, kExpr, init))
                when p = pid && not ((collectVarRefsIR kExpr).Contains pid)
                     && not (init |> Option.exists (fun s -> (collectVarRefsIR s).Contains pid)) ->
                IRReduce (dc, kExpr, init)
            | IRLet (pid, (IRDecompact _ as dc), IRCompute (IRApplyCombinator consumer)) ->
                (match rowFoldOf consumer with
                 | Some (a, kExpr, init) when a = pid -> IRReduce (dc, kExpr, init)
                 | _ ->
                     // a decompaction consumed by a row map that is NOT the
                     // plain partial fold (a capturing row kernel -- a seed
                     // or kernel naming a value --, a strategy, a licence):
                     // recorded, the buffer stays
                     record h.Subject (Blade.Effects.Declined "the decompaction's consumer is not the plain partial fold's row map (a row kernel that captures a value -- a named seed or kernel --, requests a strategy, carries a repro licence or is not exactly `reduce(row, k[, s])`)") [ "the n x n decompaction buffer stays" ]
                     IRLit IRLitUnit)
            | e -> e
        match inner with
        | IRReduce (IRDecompact (IRVar (sid, sty), d), kExpr, init) ->
            let unchanged why = record h.Subject (Blade.Effects.Declined why) [ "the n x n decompaction buffer stays" ]; None
            match stripUnits sty, stripUnits h.Type with
            | ArrayElem at, ArrayElem outAt when at.ElemType = scalar64 && not at.IsVirtual && at.IndexTypes.Length = 1
                                                  && outAt.ElemType = scalar64 && outAt.IndexTypes.Length = 1 ->
                let gix = at.IndexTypes.Head
                let oix = outAt.IndexTypes.Head
                if gix.Rank <> 2 then unchanged "v1 reads a rank-2 compact group only (a rank >= 3 decompaction keeps a compact remainder)"
                elif not (gix.Symmetry = SymSymmetric || gix.Symmetry = SymAntisymmetric) then
                    unchanged "v1 reads a real SymIdx / AntisymIdx pool only (a Hermitian, wreath or plain group keeps the decompaction)"
                elif d <> 0 && d <> 1 then unchanged "the decompacted dimension is not one of the group's two"
                elif not (oix.Rank = 1 && oix.Symmetry = SymNone && oix.IxKind = IxKPlain && IRTotality.evalInt oix.Extent = IRTotality.evalInt gix.Extent) then
                    unchanged "the row result's axis is not the group's plain axis"
                elif not h.SideFree then
                    unchanged "the host has an additional execution payload"
                else
                match IRTotality.evalInt gix.Extent with
                | Some n when n >= 1L ->
                    (match h.Locate sid with
                     | Some (sName, sMut, sProof) ->
                        match sProof () with
                        | Error why -> unchanged $"the pool is not proved stored: {why}"
                        | Ok how ->
                        match resolve kExpr with
                        | None -> unchanged "the fold kernel does not resolve to a callable"
                        | Some fold ->
                        if fold.Params.Length <> 2 || fold.RetType <> scalar64 || (fold.Params |> List.exists (fun p -> p.Type <> scalar64)) then
                            unchanged "the fold kernel is not a two-parameter Float64 callable"
                        elif fold.IsOmpParallel || not fold.Parallelism.IsEmpty || fold.IsCudaKernel || fold.IsMpiParallel || fold.IsStatic then
                            unchanged "the fold kernel requests a parallel strategy or is static (v1 keeps its route)"
                        elif writes fold.Body || (init |> Option.exists writes) then
                            unchanged "the fold kernel or seed may write (the decompaction is a snapshot taken before the fold reads it)"
                        // the minted row callable embeds the kernel reference
                        // and the seed verbatim and captures only the pool: a
                        // kernel named through a binding, a capturing kernel or
                        // a seed naming a value would be a reference without a
                        // capture (fail closed, as PM25's literal-seed rule)
                        elif (match kExpr with IRVar (kid, _) -> kid <> fold.Id | _ -> true) || not fold.Captures.IsEmpty then
                            unchanged "the fold kernel is not a capture-free callable named directly (v1 embeds only such a kernel in the minted row walk)"
                        elif (match init with
                              | None | Some (IRLit (IRLitFloat _)) | Some (IRUnaryOp (IRNeg, IRLit (IRLitFloat _))) -> false
                              | Some _ -> true) then
                            unchanged "the seed is not a Float64 literal (v1 embeds only a literal seed in the minted row walk)"
                        else
                        match IRTotality.auditCallable resolve (fun _ -> None) deferredW fold [] with
                        | Error why -> unchanged $"the fold kernel is not proved total ({why}): the pool walk may visit the cells in another global order"
                        | Ok () ->
                        let rowType = mkArrayLike { ElemType = scalar64; IndexTypes = [ oix ]; IsVirtual = false; Identity = None }
                        let seedR =
                            match init with
                            | Some s -> Ok (s, "seeded (a literal): every row chain starts from the seed, as the dense row fold's does")
                            | None -> elidedFoldSeed false rowType (Some fold) None
                        match seedR with
                        | Error why -> unchanged why
                        | Ok (seed, seedNote) ->
                        let symTxt = if gix.Symmetry = SymSymmetric then "SymIdx" else "AntisymIdx"
                        let ev =
                            [ $"reduce(decompact({sName}, {d})) -> the row walk over the pool: no n x n buffer ({n} x {n} Float64 cells, {n * n * 8L} bytes)"
                              $"every logical cell is the canonical read of the {symTxt}<2, {n}> pool with its sign applied per cell (the antisymmetric diagonal reads 0.0): the decompaction's cells, row by row, in its order"
                              $"the pool `{sName}`: {how}"
                              "the fold kernel and seed are closed-total and write-free: the walk's global cell order is unobservable, and nothing writes the pool while it is read in place"
                              seedNote
                              "strict builds bitwise (each row chain from its seed in source order); a licensed build may take another licensed form" ]
                        if not (decompactFoldEnabled ()) then
                            record h.Subject (Blade.Effects.Declined "disabled by BLADE_DECOMPACT_FOLD") ev
                            None
                        else
                        let axis = { oix with Kind = SDimension }
                        let param name : IRParam =
                            let id = builder.FreshId ()
                            { Name = $"{name}_{id}"; VarId = id; Index = 0; Type = coordinateType axis }
                        let ip, jp = param "__dcf_i", param "__dcf_j"
                        let iv, jv = IRVar (ip.VarId, ip.Type), IRVar (jp.VarId, jp.Type)
                        let poolCapture: CaptureInfo = { Id = sid; Name = sName; Type = sty; IsMutable = sMut }
                        let rowCapture: CaptureInfo = { Id = ip.VarId; Name = ip.Name; Type = ip.Type; IsMutable = false }
                        let read = IRIndex (IRVar (sid, sty), [ iv; jv ], None)
                        let cell = mkCallable builder defaultLambdaOptions [ jp ] read scalar64 [ poolCapture; rowCapture ] false [] [] false false 256 false
                        let cell = { cell with Name = $"__decompact_cell_{cell.Id}"; SourceName = anonymousCallableName (Some h.Subject) "lambda" }
                        let rowFold = IRReduceCompute (IRApplyCombinator (mapOver axis cell rowType), kExpr, seed)
                        let row = mkCallable builder defaultLambdaOptions [ ip ] rowFold scalar64 [ poolCapture ] false [] [] false false 256 false
                        let row = { row with Name = $"__decompact_rows_{row.Id}"; SourceName = anonymousCallableName (Some h.Subject) "lambda" }
                        let origin = if owned.Contains fold.Id then Some fold.Id else None
                        minted.Add((cell, origin))
                        minted.Add((row, origin))
                        record h.Subject Blade.Effects.Applied ev
                        Some (IRCompute (IRApplyCombinator (mapOver axis row h.Type)))
                     | None -> unchanged h.PoolNotPlaced)
                | _ -> unchanged "the compact group's extent is not a literal >= 1"
            | _ -> unchanged "the pool is not a stored Float64 array of one compact group, or the result is not a rank-1 Float64 array"
        | _ -> None
    let moduleHost (i: int) (b: IRBinding) : DecompactHost =
        { Subject = b.Name; Value = b.Value; Type = b.Type
          SideFree =
            (bindingSideOperands modul b.Id).IsEmpty
            && not (modul.ProviderReads.ContainsKey b.Id) && not (modul.ProviderWrites.ContainsKey b.Id)
          Locate = fun id ->
            match modul.Bindings |> List.tryFindIndex (fun x -> x.Id = id) with
            | Some si when si < i ->
                let src = modul.Bindings.[si]
                Some (src.Name, src.IsMutable, fun () -> storedArrayProof modul src |> Result.map fst)
            | _ -> None
          PoolNotPlaced = "the pool is not a top-level binding defined before this one (v1 reads top-level pools only)"
          VectorNotPlaced = "the vector is not a top-level binding defined before this one" }
    let bindings' = modul.Bindings |> List.mapi (fun i b -> match rewrite (moduleHost i b) with Some v -> { b with Value = v } | None -> b)
    // G123 (round 4 wave 5): the same rewrite inside a FUNCTION BODY, the
    // pool (and the mat-vec vector) a PARAMETER of the function -- an
    // argument arrives stored, and the decompaction read it at the host
    // statement exactly where the row walk now reads it (the kernel and seed
    // are write-free, so nothing writes it in between). Hosts: every value of
    // the body's let spine and its return. A pool bound by a body let is not
    // read (v1; gap G167). The same function filter as the other body passes.
    // Codex review (wave 5, round 1): "an array argument arrives stored" is
    // a fact of the ORDINARY call ABI only. The LLVM lane INLINES a capturing
    // callee and may bind a parameter to a still-deferred producer (its cells
    // then run inside the walk, interleaved with the vector's: another
    // failure order), and a callable used as a VALUE (a kernel, a callback,
    // an alias) is invoked through routes this pass does not see. So a host
    // function is admitted only when it captures nothing, is not a lambda,
    // and every reference to it in this module is a direct call head.
    let callHeads = System.Collections.Generic.Dictionary<IRId, int>()
    let varRefs = System.Collections.Generic.Dictionary<IRId, int>()
    let bump (d: System.Collections.Generic.Dictionary<IRId, int>) (id: IRId) =
        d.[id] <- (match d.TryGetValue id with | true, n -> n + 1 | _ -> 1)
    let countRefs (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | CallOfNamed (g, _, _, _) -> bump callHeads g
            | IRVar (g, _) -> bump varRefs g
            | _ -> ()) e
    for g in (programFuncs @ modul.Functions) |> List.distinctBy (fun g -> g.Id) do countRefs g.Body
    for b in modul.Bindings do
        for pl in bindingPayload modul b do countRefs pl
    let countOf (d: System.Collections.Generic.Dictionary<IRId, int>) (id: IRId) =
        match d.TryGetValue id with
        | true, n -> n
        | _ -> 0
    // Round 2 (the class's second member): the LLVM lane also INLINES a
    // tuple-returning callee (`emitTupleParts`) whatever its captures, binding
    // its array parameters with no materialization -- declined too.
    let returnsTuple (f: IRCallable) =
        match stripUnits f.RetType with
        | IRTTuple _ -> true
        | _ -> false
    // Wave 6 class-closure round (gap G123): the census above sees this
    // module's bindings only, so a function ANOTHER module names as a value
    // -- a `>>=` continuation, which the LLVM lane splices through
    // `bindContinuationBody` with its parameter bound to the still-deferred
    // producer -- counted zero value references here. The program-wide value
    // census Lowering installs (CallElideProgram, as call elision's) closes
    // it; not installed (a lone module optimized outside the driver) = every
    // parameter route declines, fail closed.
    let directCallsOnly (f: IRCallable) =
        f.Captures.IsEmpty && not (f.Name.StartsWith "__lambda_") && not (returnsTuple f)
        && countOf varRefs f.Id = countOf callHeads f.Id
        && (match CallElideProgram.valueRefs () with
            | Some refs -> not (refs.Contains f.Id)
            | None -> false)
    // G167 (round 4 wave 6): the module's assignment census (an IRAssign
    // rooted at a name, element or whole, in any body, lambdas included) --
    // a body let it names is not read in place. (A capture's IsMutable means
    // ASSIGNABLE -- every plain `let` is -- so it is not consulted; a write
    // through a `mut` parameter is the interval audit's.)
    let assignedAnywhere =
        let s = System.Collections.Generic.HashSet<IRId>()
        let rec rootOf (t: IRExpr) =
            match t with
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> rootOf b
            | IRVar (id, _) -> Some id
            | _ -> None
        let census (e: IRExpr) =
            iterIRExpr (fun n ->
                match n with
                | IRAssign (t, _) -> (match rootOf t with Some id -> s.Add id |> ignore | None -> ())
                | _ -> ()) e
        for g in (programFuncs @ modul.Functions) |> List.distinctBy (fun g -> g.Id) do
            census g.Body
        for b in modul.Bindings do
            for pl in bindingPayload modul b do census pl
        s
    let funcRewrite (f: IRCallable) : IRCallable =
        if f.IsStatic || f.IsCudaKernel || f.IsMpiParallel || f.IsArityPoly || hasTypeVarsInSignature f then f
        else
        let region = if System.String.IsNullOrEmpty f.SourceName then f.Name else f.SourceName
        let paramsById = f.Params |> List.map (fun p -> (p.VarId, p)) |> Map.ofList
        let direct = directCallsOnly f
        let bodyOptIn = "the function-body arm is disabled by BLADE_DECOMPACT_FOLD=top (the pre-wave-7 default: the top-level arms only)"
        let paramLocate (id: IRId) =
            match Map.tryFind id paramsById with
            | Some p ->
                let isVirtual = match stripUnits p.Type with ArrayElem at -> at.IsVirtual | _ -> true
                Some (p.Name, false, fun () ->
                    if isVirtual then Error $"`{p.Name}` is not a stored array parameter"
                    elif not direct then Error $"`{region}` captures a value, is a lambda, returns a tuple or is referenced other than by a direct call (a parameter is proved stored only on the ordinary call ABI; the LLVM lane inlines a capturing or tuple-returning callee over a possibly deferred argument)"
                    elif not (decompactFoldBodyEnabled ()) then Error bodyOptIn
                    else Ok $"a parameter of `{region}`, a capture-free function reached only by direct calls (an array argument arrives stored)")
            | None -> None
        // G167 (round 4 wave 6): a pool / vector bound by a BODY LET of the
        // host's own spine, before the host. `earlier` = the spine lets
        // before the host, in order; `hostValue` = the host statement. The
        // let is materialized at its own site in every route that runs the
        // body (the ordinary call, the LLVM capturing-callee and tuple
        // inlines, the interpreter), so its proof does not lean on the call
        // ABI: its value is a `|> compute` or an array literal (never a
        // deferred form), it is never assigned nor captured mutably anywhere,
        // and every statement from it to the host -- each intervening let
        // value and the host value, each its own root -- writes nothing
        // (scheduleMayWriteIn: a mut-parameter callee, a write through a view
        // or a deferred alias). An alias `let P = Q` chains to Q's own proof.
        let locate (earlier: (IRId * IRExpr) list) (hostValue: IRExpr) (id: IRId) =
            let rec go (seen: Set<IRId>) (id: IRId) =
                match paramLocate id with
                | Some r -> Some r
                | None ->
                match earlier |> List.tryFindIndex (fun (lid, _) -> lid = id) with
                | None -> None
                | Some li ->
                    let name = $"__v{id}"
                    let v = snd earlier.[li]
                    let interval = (earlier |> List.skip (li + 1) |> List.map snd) @ [ hostValue ]
                    let intervalProof () =
                        if assignedAnywhere.Contains id then Error $"the body let `{name}` is assigned in the program"
                        elif interval |> List.exists writes then Error $"a statement between the body let `{name}` and the host may write (scheduleMayWriteIn: the pool is read in place at the host)"
                        else Ok ()
                    match v with
                    | IRVar (aid, _) when not (seen.Contains aid) ->
                        (match go (seen.Add id) aid with
                         | Some (an, _, aproof) ->
                             // the capture is the alias itself (its own id
                             // and emitted name); the proof is its root's
                             Some (name, false, fun () ->
                                 match intervalProof () with
                                 | Error why -> Error why
                                 | Ok () -> aproof () |> Result.map (fun how -> $"a body let aliasing `{an}`, {how}"))
                         | None -> Some (name, false, fun () -> Error $"the body let `{name}` aliases a value that is not a parameter or an earlier let of `{region}`"))
                    | _ ->
                    Some (name, false, fun () ->
                        let form =
                            if isDeferredForm v then None
                            else
                            match v with
                            | IRCompute _ -> Some "a `|> compute`"
                            | IRArrayLit _ -> Some "an array literal"
                            | _ -> None
                        match form with
                        | None -> Error $"the body let `{name}` is not a `|> compute` or an array literal (v1 proves only those stored at their own site)"
                        | Some how ->
                        match intervalProof () with
                        | Error why -> Error why
                        | Ok () ->
                        if not (decompactFoldBodyEnabled ()) then Error bodyOptIn
                        else Ok $"a body let of `{region}`, {how} materialized at its own site in every route that runs the body; never assigned, nothing between it and the host writes (scheduleMayWriteIn)")
            go Set.empty id
        let host (earlier: (IRId * IRExpr) list) (subject: string) (value: IRExpr) (ty: IRType) : DecompactHost =
            { Subject = subject; Value = value; Type = ty; SideFree = true; Locate = locate earlier value
              PoolNotPlaced = $"the pool is not a parameter or an earlier body let of `{region}`'s spine (a pool bound inside a nested block or captured from outside is not read)"
              VectorNotPlaced = $"the vector is not a parameter or an earlier body let of `{region}`'s spine" }
        let isDecompactTail (e: IRExpr) =
            match e with
            | IRLet (_, IRDecompact _, (IRReduce _ | IRCompute _)) -> true
            | IRReduce (IRDecompact _, _, _) | IRCompute (IRApplyCombinator _) -> true
            | IRApplyCombinator _ -> true
            | _ -> false
        let mentionsDecompact (e: IRExpr) =
            let mutable hit = false
            iterIRExpr (fun n -> match n with IRDecompact _ -> hit <- true | _ -> ()) e
            hit
        // the spine lets before the host, NEWEST first (reversed only at a host)
        let rec walk (earlierRev: (IRId * IRExpr) list) (e: IRExpr) : IRExpr =
            match e with
            | IRLet (_, IRDecompact _, (IRReduce _ | IRCompute _)) ->
                (match rewrite (host (List.rev earlierRev) region e f.RetType) with Some v -> v | None -> e)
            | IRLet (id, v, rest) ->
                let v' =
                    if mentionsDecompact v then
                        (match rewrite (host (List.rev earlierRev) region v (typeOf v)) with Some nv -> nv | None -> v)
                    else v
                IRLet (id, v', walk ((id, v') :: earlierRev) rest)
            | tail when isDecompactTail tail && mentionsDecompact tail ->
                (match rewrite (host (List.rev earlierRev) region tail f.RetType) with Some v -> v | None -> tail)
            | tail -> tail
        { f with Body = walk [] f.Body }
    let modul = { modul with Functions = modul.Functions |> List.map funcRewrite }
    if minted.Count = 0 then { modul with Bindings = bindings' }
    else
        let byOrigin = minted |> Seq.choose (fun (c, o) -> o |> Option.map (fun oid -> (oid, c))) |> Seq.groupBy fst |> Map.ofSeq
        let loose = minted |> Seq.filter (fun (_, o) -> o.IsNone) |> Seq.map fst |> List.ofSeq
        { modul with
            Functions =
                (modul.Functions |> List.collect (fun f ->
                    match Map.tryFind f.Id byOrigin with
                    | Some cs -> f :: (cs |> Seq.map snd |> List.ofSeq)
                    | None -> [ f ])) @ loose
            Bindings = bindings'
            DerivedFuncOrigins =
                minted |> Seq.fold (fun acc (c, o) ->
                    match o with
                    | Some oid -> Map.add c.Id oid acc
                    | None -> acc) modul.DerivedFuncOrigins }
