// Slice-write fusion (with the threaded let-rec step and the fold state in place) and row-fold fusion (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeSliceWrites

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeHotRow

// --- Slice-write fusion: destination passing for a rank-1 row write --------
//
// The recursive-array desugar (TypeCheckInfer.inferRecArray) writes every
// row slice through a materialized temporary:
//
//   let s = SLICE in                               -- e.g. step(prefix(n - 1))
//   for k in 0..E { BUF[i.., k] <- s(k) }          -- the row copy
//
// so a step function returning a fresh row costs that row's allocation, a
// full write of it, the copy's read, and the row write -- and every stage
// temporary inside `step` (one per `let`, one per pointwise callee) besides.
// When SLICE is POINTWISE -- cell k of its value depends only on cell k of
// the arrays it reads -- the copy loop can compute each cell where it is
// stored: the slice's whole elementwise DAG, with pointwise callees
// (`step`, `rhs`) inlined, becomes a chain of scalar lets per cell, each
// let of the source computed ONCE per cell (k1 feeding both the k2 stage
// and the final combination is one scalar local, never a recomputation), and
// the last one is stored straight into the destination row. No temporary
// row, no copy, no stage pools: one pass per step, reading the prefix rows
// and writing the row being built (bench/notes/phase2-microkernel-plan.md (removed 2026-10-24; in git history at cf2028cc)
// 4.5b(ii) destination passing + 4.5c pointwise-body fusion).
//
// Why it is cost-only (charter rule 1):
//   - every cell's arithmetic is the source's, operation for operation, in
//     the source's association; only WHERE intermediates live changes (a
//     register instead of a pool), so a build with contraction off is bitwise
//     the unfused one (`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`, what every
//     differential pins). Under the default fast contraction the compiler
//     sees across former statement boundaries, exactly as the elementwise
//     chain fusion this extends already lets it;
//   - order of reads vs writes: unfused, all of SLICE is read before any cell
//     of the row is written; fused, cell k is read after cells 0..k-1 of the
//     destination were written. Every read of the expansion is at cell k of
//     its array, and an array that may alias the buffer is admitted only as a
//     ROW VIEW of it (`let r = BUF(i)`, whose cell k is BUF(i, k)), so the
//     cells written so far (j < k of the destination row) are never read:
//     each read sees the value it saw unfused;
//   - nothing in the region may abort (tileMayAbort over every kernel), and
//     every operand's extent is the loop's own static extent, so no
//     co-iteration extent check (BL8011) is dropped and none could fire;
//   - kernels are plain, pure, capture-free scalar kernels with no calls;
//     callees are non-static, non-recursive, capture-free functions of array
//     parameters whose bodies are themselves pointwise.
// Anything else declines and the copy stays exactly as the desugar wrote it.
// Gate: BLADE_FUSION (the same escape hatch as the chain fusion it extends;
// `blade test opt-diff` compares the two).
let fuseSliceWritesModule (modul: IRModule) (builder: IRBuilder) (programFuncs: IRCallable list) : IRModule =
    if not (Blade.Toolchain.envFlag "BLADE_FUSION" true) then modul else
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do callables.[f.Id] <- f
    for f in modul.Functions do callables.[f.Id] <- f
    // Callees are inlined only from THIS module: their kernels are spliced
    // by body, but a body from another module may name that module's
    // members, which this module's emission does not see under that id.
    let localFuncs = modul.Functions |> List.map (fun f -> f.Id) |> Set.ofList
    // Wave 3 (E-G5): the write predicate for the step-threading admission --
    // the slice and the whole time-loop body (producer and the bindings in
    // between), the G41 rule.
    let writes = scheduleMayWriteIn programFuncs modul
    // The kernel shape slice-write fusion admits MINUS the omp request: a
    // `where omp` on the slice's ROOT map (the step's final map) is the
    // step-threading request, kept by a RecStepTable entry and honoured by
    // the C++ lane's time-loop emitter. Nested stage maps keep declining.
    let recStepKernelShape (k: IRCallable) (arity: int) =
        not k.IsCommutative && k.CommGroups.IsEmpty && k.AntisymGroups.IsEmpty
        && not k.IsCudaKernel && not k.IsMpiParallel && not k.IsArityPoly && not k.IsStatic
        && k.Params.Length = arity
        && k.IsOmpParallel && not k.Parallelism.IsEmpty
        // `omp(v: 1)`: depth exactly one over a rank-1 row names its one axis.
        && k.Parallelism |> List.forall (fun (idx, dims) -> dims = 1 && idx >= 0 && idx < arity)
    let recStepKernel (k: IRCallable) (arity: int) = recStepOmpEnabled () && recStepKernelShape k arity
    let kernelName (k: IRCallable) = if System.String.IsNullOrEmpty k.SourceName then k.Name else k.SourceName
    let staticExtent (at: IRArrayType) : int64 option =
        match at.IndexTypes with
        | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 ->
            (match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
        | _ -> None
    let scalarElem (t: IRType) = match t with IRTScalar _ -> true | _ -> false
    let rowOfExtent (ext: int64) (t: IRType) =
        match t with
        | ArrayElem at -> staticExtent at = Some ext && scalarElem at.ElemType && not at.IsVirtual
        | _ -> false
    let mentions (vid: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRVar (id, _) when id = vid -> hit <- true | _ -> ()) e
        hit
    // A call, or a binder other than `let` (match patterns, loop variables):
    // only let binders are freshened per splice, and one kernel can be
    // spliced several times into one body.
    let hasCall (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRApp _ | IRMatch _ | IRForRange _ -> hit <- true | _ -> ()) e
        hit
    // A spliced kernel body's own let binders get fresh ids (the same kernel
    // is spliced once per inlined call site: `rhs` four times per step).
    let freshenLets (body: IRExpr) : IRExpr =
        let binders = System.Collections.Generic.List<IRId>()
        iterIRExpr (fun e -> match e with IRLet (id, _, _) -> binders.Add id | _ -> ()) body
        if binders.Count = 0 then body else
        let mapped = binders |> Seq.distinct |> Seq.map (fun id -> id, builder.FreshId()) |> Map.ofSeq
        mapIRExpr (fun e ->
            match e with
            | IRVar (id, t) -> (match Map.tryFind id mapped with Some n -> IRVar (n, t) | None -> e)
            | IRLet (id, v, b) -> (match Map.tryFind id mapped with Some n -> IRLet (n, v, b) | None -> e)
            | _ -> e) body
    let substParams (m: Map<IRId, IRExpr>) (body: IRExpr) =
        mapIRExpr (fun e ->
            match e with
            | IRVar (id, _) -> (match Map.tryFind id m with Some a -> a | None -> e)
            | _ -> e) body
    // The cell expander. `outsideOk x` says whether reading array variable x
    // at cell k is admissible (not the buffer, or a row view of it). Emits
    // scalar lets into `prelude` (evaluation order) and returns the cell's
    // value as an atom (a variable or a literal).
    // The requesting ROOT kernels the knob kept out of the fusion (recorded
    // per body as `rec-step-parallel` declines).
    let knobDeclines = System.Collections.Generic.List<string>()
    // The PM14 fold-state arm's extra expander case (see the IRObjectFor arm).
    let mutable objectForArm = false
    let expand (ext: int64) (kVar: IRExpr) (outsideOk: IRId -> bool)
               (prelude: System.Collections.Generic.List<IRId * IRExpr>) (root: IRExpr) : Result<IRExpr * IRCallable option * bool, string> =
        // The FIRST reason a node is not a cell expression is the decline.
        let mutable why : string option = None
        // The slice's root map when it carried the step-threading request.
        let mutable ompRoot : IRCallable option = None
        // A spliced kernel or callee was `where repro` (its demand does not
        // survive the splice -- the pre-existing fusion behaviour; the
        // step-threading admission declines on it).
        let mutable sawRepro = false
        let fail (msg: string) : IRExpr option =
            (if why.IsNone then why <- Some msg)
            None
        let bind (v: IRExpr) (t: IRType) =
            match v with
            | IRVar _ | IRLit _ -> v
            | _ ->
                let id = builder.FreshId()
                prelude.Add (id, v)
                IRVar (id, t)
        // Outside reads are bound once per cell and shared.
        let reads = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        // `atRoot`: this node's value IS the slice (the slice itself, a let's
        // body, an inlined callee's body) -- the one position whose map may
        // carry the step-threading request; an operand never is.
        let rec go (env: Map<IRId, IRExpr>) (visiting: Set<IRId>) (atRoot: bool) (e: IRExpr) : IRExpr option =
            match e with
            | IRVar (id, t) ->
                (match Map.tryFind id env with
                 | Some atom -> Some atom
                 | None ->
                    match reads.TryGetValue id with
                    | true, atom -> Some atom
                    | _ ->
                        match t with
                        | ArrayElem at when rowOfExtent ext t && outsideOk id ->
                            let atom = bind (IRIndex (e, [ kVar ], None)) at.ElemType
                            reads.[id] <- atom
                            Some atom
                        | ArrayElem _ when rowOfExtent ext t ->
                            fail "the slice reads an array that may alias the buffer other than as a row view"
                        | _ -> fail "the slice reads a value that is not a dense row of the loop's static extent")
            | IRCompute ((IRApplyCombinator _ | IRCompute _) as inner) -> go env visiting atRoot inner
            | IRCompute _ ->
                // Forcing a deferred VALUE (`dn |> compute`) materializes it
                // per step; reading it cell by cell instead would make the
                // emitters materialize the binding itself -- at module level a
                // new printed value (corpus memfree/007).
                fail "the slice forces a deferred value"
            | IRLet (id, v, body) ->
                (match go env visiting false v with
                 | Some atom -> go (Map.add id atom env) visiting atRoot body
                 | None -> None)
            | IRApplyCombinator info when IRMono.tilePlainInfo info && rowOfExtent ext info.OutputType ->
                (match IRMono.tileKernelOf callables info with
                 | Some k when (IRMono.tilePlainKernel k info.Arrays.Length || (atRoot && recStepKernel k info.Arrays.Length))
                               && k.Captures.IsEmpty && not (hasCall k.Body)
                               && not (IRMono.nestedCapturesOwnBinder (fun id -> match callables.TryGetValue id with | true, c -> Some c | _ -> None) k)
                               && IRMono.tilePureBody callables Set.empty k.Body
                               && not (IRMono.tileMayAbort callables Set.empty k.Body)
                               && scalarElem k.RetType
                               && info.ArrayTypes |> List.forall (fun at -> staticExtent at = Some ext && scalarElem at.ElemType) ->
                    if not (IRMono.tilePlainKernel k info.Arrays.Length) then ompRoot <- Some k
                    if k.IsRepro then sawRepro <- true
                    let atoms = info.Arrays |> List.map (go env visiting false)
                    if atoms |> List.exists Option.isNone then None else
                    let m = List.zip (k.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                    Some (bind (substParams m (freshenLets k.Body)) k.RetType)
                 | Some k when atRoot && not (recStepOmpEnabled ()) && recStepKernelShape k info.Arrays.Length ->
                    // The knob keeps the request out of the fusion: the map
                    // runs as its own per-step team and the row is copied,
                    // the pre-wave-3 emission (the A/B's knob-off arm).
                    knobDeclines.Add (kernelName k)
                    fail "an elementwise kernel is not a plain pure scalar kernel over rows of the loop's extent"
                 | Some k when not k.Captures.IsEmpty ->
                    fail "an elementwise kernel captures a value computed outside the cell (a fold or another whole-row quantity), so the slice is not pointwise"
                 | Some k when hasCall k.Body -> fail "an elementwise kernel calls a function or binds names by match or loop"
                 | Some k when IRMono.tileMayAbort callables Set.empty k.Body ->
                    fail "an elementwise kernel may abort (fusion never moves an evaluation that may abort)"
                 | Some _ -> fail "an elementwise kernel is not a plain pure scalar kernel over rows of the loop's extent"
                 | None -> fail "an elementwise kernel is not a resolvable callable")
            | CallOfNamed (fid, _, args, retT) when localFuncs.Contains fid && not (visiting.Contains fid) && rowOfExtent ext retT ->
                (match callables.TryGetValue fid with
                 | true, f when not f.IsStatic && f.Captures.IsEmpty && not f.IsArityPoly
                                && not f.IsOmpParallel && not f.IsCudaKernel && not f.IsMpiParallel
                                && f.Params.Length = args.Length
                                && f.Params |> List.forall (fun p -> rowOfExtent ext p.Type) ->
                    let atoms = args |> List.map (go env visiting false)
                    if atoms |> List.exists Option.isNone then None else
                    if f.IsRepro then sawRepro <- true
                    // The callee sees only its parameters (lexical scope).
                    let env' = List.zip (f.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                    go env' (visiting.Add fid) atRoot f.Body
                 | _ -> fail "a callee is not a plain function of dense row parameters (static, capturing, arity-polymorphic, or parallel)")
            // PM14 only (`objectForArm`, set by the fold-state arm; slice-write
            // fusion's own admission is unchanged): the elementwise object_for
            // application a generic `T^1` kernel body lowers `a + b` to --
            // `object_for(k) <@> (p, q)` with every input rank 0 is cell c of
            // each operand through k (Interp `materializeObjectForApp`'s
            // [0; 0] / [0] arms), each operand a dense row of the loop's
            // static extent (checked by the operand atoms below).
            | IRApp (IRObjectFor o, args, retT)
                    when objectForArm && rowOfExtent ext retT
                         && not o.InputRanks.IsEmpty && o.InputRanks |> List.forall (fun r -> r = 0) ->
                let elems = match args with [ IRTuple es ] -> es | _ -> args
                (match o.Kernel with
                 | IRVar (kid, _) ->
                    (match callables.TryGetValue kid with
                     | true, k when elems.Length = o.InputRanks.Length && k.Params.Length = elems.Length
                                    && k.Captures.IsEmpty && not (hasCall k.Body)
                                    && not (IRMono.nestedCapturesOwnBinder (fun id -> match callables.TryGetValue id with | true, c -> Some c | _ -> None) k)
                                    && not k.IsStatic && not k.IsArityPoly && not k.IsOmpParallel
                                    && not k.IsCudaKernel && not k.IsMpiParallel
                                    && IRMono.tilePureBody callables Set.empty k.Body
                                    && not (IRMono.tileMayAbort callables Set.empty k.Body)
                                    && scalarElem k.RetType ->
                        if k.IsRepro then sawRepro <- true
                        let atoms = elems |> List.map (go env visiting false)
                        if atoms |> List.exists Option.isNone then None else
                        let m = List.zip (k.Params |> List.map (fun p -> p.VarId)) (atoms |> List.map Option.get) |> Map.ofList
                        Some (bind (substParams m (freshenLets k.Body)) k.RetType)
                     | true, _ -> fail "an elementwise object_for kernel is not a plain pure capture-free scalar kernel that cannot abort"
                     | _ -> fail "an elementwise object_for kernel is not a resolvable callable")
                 | _ -> fail "an elementwise object_for kernel is not a named callable")
            | IRApp _ -> fail "the slice calls a function that is recursive, defined in another module, or not row-valued"
            | IRApplyCombinator _ -> fail "an operation over the row is not a plain elementwise map of the loop's extent (a fold, a structured index, or a different shape)"
            | IRIf _ | IRMatch _ -> fail "the slice selects between whole rows (a guarded read or a branch), which is no cell expression"
            | _ -> fail "the slice is not pointwise (an operation other than elementwise maps, lets and pointwise calls)"
        match go Map.empty Set.empty true root with
        | Some atom -> Ok (atom, ompRoot, sawRepro)
        | None -> Error (defaultArg why "the slice is not pointwise")
    // The copy loop's shape: `for k in 0..E { let _ = BUF[i.., k] <- s(k) in () }`.
    let rowCopy (sId: IRId) (loop: IRExpr) =
        match loop with
        | IRForRange (kId, IRLit (IRLitInt 0L), IRLit (IRLitInt ext),
                      IRLet (aId, IRAssign (IndexOfNamed (bufId, bufT, idxs, ident), IndexOfNamed (s, _, [ IRVar (k1, kT) ], _)), tail))
                when s = sId && k1 = kId && not idxs.IsEmpty
                     && (match List.last idxs with IRVar (k2, _) -> k2 = kId | _ -> false)
                     && not (idxs |> List.take (idxs.Length - 1) |> List.exists (fun i -> mentions kId i || mentions sId i))
                     && not (mentions sId tail) ->
            Some (kId, ext, aId, bufId, bufT, idxs, ident, IRVar (k1, kT), tail)
        | _ -> None
    let mutable fired = 0
    let declines = System.Collections.Generic.List<string>()
    // A DEFERRED value (a loop object or an unforced combinator) is
    // materialized by its consumers; a cell read would make the emitters
    // materialize the binding itself instead -- for a module binding, a new
    // printed value. Such reads are never admitted.
    let rec isDeferredForm (v: IRExpr) =
        match v with
        | IRLet (_, _, b) -> isDeferredForm b
        | IRApplyCombinator _ | IRFunctorMap _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _
        | IRFusion _ | IRParallel _ | IRBind _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _
        | IRPure _ | IRChoice _ | IRFallback _ -> true
        | _ -> false
    let moduleValues = modul.Bindings |> List.map (fun b -> b.Id, b.Value) |> Map.ofList
    let rec moduleReadable (seen: Set<IRId>) (id: IRId) =
        match Map.tryFind id moduleValues with
        | Some (IRVar (y, _)) when not (seen.Contains y) && moduleValues.ContainsKey y -> moduleReadable (seen.Add id) y
        | Some v -> not (isDeferredForm v)
        | None -> false
    let moduleIds = modul.Bindings |> List.map (fun b -> b.Id) |> List.filter (moduleReadable Set.empty) |> Set.ofList
    let mutable outer = moduleIds
    let mutable subjectName = ""
    // --- PM14 (round 4 wave 2): the leading-axis fold's state, in place ----
    //
    // TypeCheckInfer's leading-axis fold desugar (`reduce(G, lambda(a: T^1,
    // b: T^1) -> ..[, seed])`) lowers to
    //
    //   let mut acc = <private copy of G(0) / of the seed>     -- MutableArrayLets
    //   for j in lo..hi { let nxt = kernel(acc, G(j)) |> compute; acc <- nxt }
    //
    // so every step allocates a fresh slice inside the kernel and the C++
    // lane then MOVES it into `acc` by pointer, leaking the previous state
    // (2001 x 5003: 80 MB of leaked slices; the LLVM lane refuses the
    // rebinding and falls back). When the kernel is POINTWISE -- this pass's
    // own cell expander succeeds on `kernel(acc, row)` -- each cell c of the
    // next state reads the state at cell c only, so the step is computed in
    // place: `let row = G(j); for c { <cell chain>; acc[c] <- v }`.
    //   - per cell the chain is s_c <- k_c(s_c, G(j)_c), j ascending from the
    //     same seed cell -- exactly the materialized step's arithmetic (the
    //     expander inlines the kernel operation for operation), so bitwise
    //     under contraction off;
    //   - every read of `acc` in the cell chain is at cell c and precedes the
    //     store of cell c (the prelude, then the store); no other cell is read;
    //   - `acc` is the desugar's private copy (a fresh compute, a
    //     MutableArrayLets binding), named only by the call and the assign in
    //     the loop; nothing else in the loop writes; the kernel is pure,
    //     capture-free and may not abort (the expander's own admission), so
    //     no failure moves.
    // Declines (recorded, rule `fold-state-in-place`): a non-pointwise kernel
    // (the leak stays: gap register), slices that are not dense rank-1 rows of
    // the state's static extent, a seed that is not a private copy, a `where
    // repro` callee (its boundary does not survive the splice), an omp root.
    let mutable foldStateFired = 0
    let foldStateDeclines = System.Collections.Generic.List<string>()
    let rec stripCompute (e: IRExpr) = match e with IRCompute inner -> stripCompute inner | _ -> e
    let foldStateCall (accId: IRId) (jId: IRId) (nxtV: IRExpr) =
        match stripCompute nxtV with
        | CallOfNamed (fid, ft, [ IRVar (a, _); (IndexOfNamed (g, _, [ IRVar (j2, _) ], _) as rowE) ], retT)
            when a = accId && j2 = jId && g <> accId ->
            Some (fid, ft, g, rowE, retT)
        | _ -> None
    let foldStateInPlace (scope: Map<IRId, IRExpr>) (accId: IRId) (accT: IRType) (seedV: IRExpr)
                         (jId: IRId) (nxtV: IRExpr) : Result<IRExpr, string> =
        if not (foldStateEnabled ()) then Error "disabled by BLADE_FOLD_STATE" else
        let (fid, ft, g, rowE, retT) = (foldStateCall accId jId nxtV).Value
        match accT with
        | ArrayElem at when (staticExtent at).IsSome && scalarElem at.ElemType && not at.IsVirtual ->
            let m = (staticExtent at).Value
            let rowT = try typeOf rowE with _ -> IRTUnit
            // The desugar's `matCopy`: the identity map over the seed (or G(0)),
            // computed -- an IRCompute of an IRApplyCombinator always writes a
            // FRESH output pool. Any other compute may return existing storage
            // (IRCompute delegates freshness to its operand), so it declines.
            let fresh =
                match seedV with
                | IRCompute (IRApplyCombinator info) when info.Arrays.Length = 1 && not info.HasReynolds ->
                    (match info.Kernel with
                     | IRVar (kid, _) ->
                        (match callables.TryGetValue kid with
                         | true, k ->
                            (match k.Params, k.Body with
                             | [ p ], IRVar (b, _) -> b = p.VarId && k.Captures.IsEmpty
                             | _ -> false)
                         | _ -> false)
                     | _ -> false)
                | _ -> false
            if not fresh then Error "the state's seed is not the desugar's private copy (the identity map over the seed, computed)"
            elif not (rowOfExtent m rowT) then Error "the folded slices are not dense rank-1 rows of the state's static extent (v1: a dense rank-2 operand)"
            elif not (rowOfExtent m retT) then Error "the kernel's result is not a dense rank-1 row of the state's extent"
            elif (match Map.tryFind g scope with Some v -> mentions accId v | None -> false) then
                Error "the folded array may alias the state"
            else
            let kId = builder.FreshId ()
            let rvId = builder.FreshId ()
            let kVar = IRVar (kId, IRTScalar ETInt64)
            let prelude = System.Collections.Generic.List<IRId * IRExpr>()
            let root = IRApp (IRVar (fid, ft), [ IRVar (accId, accT); IRVar (rvId, rowT) ], retT)
            let expanded =
                objectForArm <- true
                try expand m kVar (fun x -> x = accId || x = rvId) prelude root
                finally objectForArm <- false
            match expanded with
            | Error why -> Error ("the kernel is not pointwise: " + why)
            | Ok (_, Some _, _) -> Error "the kernel's map carries an omp request"
            | Ok (_, None, true) -> Error "a spliced kernel or callee is `where repro` (its boundary does not survive the in-place splice)"
            | Ok (atom, None, false) ->
                let sId = builder.FreshId ()
                let store = IRLet (sId, IRAssign (IRIndex (IRVar (accId, accT), [ kVar ], None), atom), IRLit IRLitUnit)
                let body = Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) prelude store
                let wId = builder.FreshId ()
                Ok (IRLet (rvId, rowE, IRLet (wId, IRForRange (kId, IRLit (IRLitInt 0L), IRLit (IRLitInt m), body), IRLit IRLitUnit)))
        | _ -> Error "the state is not a dense rank-1 array of static extent with scalar cells"
    // Walk a body keeping the values of enclosing lets (to judge aliases of
    // the buffer). Only let-chains are walked into; the rewrite is local to
    // a `let s = SLICE in let w = <copy loop> in rest` triple. `enclosing`
    // is the innermost counted loop around the triple -- (variable, literal
    // start, body) -- the time loop a threaded step runs under.
    let rec rw (enclosing: (IRId * int64 * IRExpr) option) (scope: Map<IRId, IRExpr>) (e: IRExpr) : IRExpr =
        match e with
        | IRLet (sId, slice, IRLet (wId, loop, rest)) when (rowCopy sId loop).IsSome && not (mentions sId rest) ->
            let kId, ext, aId, bufId, bufT, idxs, ident, kVar, tail = (rowCopy sId loop).Value
            // The row views of the buffer the slice reads (their indices),
            // for the step-threading proof below.
            let rowReads = System.Collections.Generic.List<IRExpr list>()
            // Every id in scope whose value may hold the buffer (mentions it,
            // or mentions such an id): a read through one is a buffer read of
            // unknown row, never an "outside" value.
            let aliases =
                let mutable s = Set.empty
                let mutable grow = true
                while grow do
                    grow <- false
                    for KeyValue (x, v) in scope do
                        if not (s.Contains x) then
                            let mutable hit = false
                            iterIRExpr (fun n -> match n with IRVar (id, _) when id = bufId || s.Contains id -> hit <- true | _ -> ()) v
                            if hit then
                                s <- s.Add x
                                grow <- true
                s
            let mentionsAlias (v: IRExpr) =
                let mutable hit = false
                iterIRExpr (fun n -> match n with IRVar (id, _) when aliases.Contains id -> hit <- true | _ -> ()) v
                hit
            let rec outsideOk (x: IRId) =
                if x = bufId then false
                else
                    match Map.tryFind x scope with
                    // A parameter of the enclosing function or a module
                    // binding cannot alias a buffer local to this body; an
                    // id bound by any other binder (a match pattern, a
                    // destructuring) is unknown and declines.
                    | None -> outer.Contains x
                    // An alias `let x = y` is judged as y (a lane may bind it
                    // without a copy).
                    | Some (IRVar (y, _)) -> y <> x && outsideOk y
                    | Some v when not (mentions bufId v) && not (mentionsAlias v) -> not (isDeferredForm v)
                    | Some (IndexOfNamed (b, _, rowIdx, _)) when b = bufId ->
                        // A row view BUF(i): cell k is BUF(i, k).
                        (match bufT with
                         | ArrayElem at when rowIdx.Length = at.IndexTypes.Length - 1 ->
                            rowReads.Add rowIdx
                            true
                         | _ -> false)
                    | Some _ -> false
            let prelude = System.Collections.Generic.List<IRId * IRExpr>()
            // Wave 3 (E-G5): the step-threading proof for a slice whose root
            // map carried the request (Codex review, wave3/codex-review-E-G5
            // objections 1-3, 5-8). Ok = a RecStepTable entry; Error = the
            // reason, and the slice stays UNFUSED (the request is then
            // honoured by the map's own per-step team, never dropped).
            let proveStep (k: IRCallable) (sawRepro: bool) : Result<unit, string> =
                match enclosing with
                | None -> Error "the row write is not inside a counted time loop with a literal start"
                | Some (nId, lo, loopBody) ->
                    let isN (e: IRExpr) = match e with IRVar (v, _) -> v = nId | _ -> false
                    // A row the region may read without synchronization: an
                    // earlier row this worker wrote (`n - c`, 1 <= c <= lo,
                    // so never before row 0), or a seed row written before
                    // the loop (0 <= r < lo).
                    let earlierRow (r: IRExpr) =
                        match r with
                        | IRBinOp (_, IRSub, IRVar (v, _), IRLit (IRLitInt c), _) -> v = nId && c >= 1L && c <= lo
                        | IRLit (IRLitInt r) -> r >= 0L && r < lo
                        | _ -> false
                    // Every mention of the buffer in the time loop's body: a
                    // store to the CURRENT row, or a read of an admissible
                    // earlier row (a row view, or a cell of one).
                    let bodyUseOk =
                        let mutable ok = true
                        let rec walk (x: IRExpr) =
                            match x with
                            | IRAssign (IndexOfNamed (b, _, i0 :: rest, _), v) when b = bufId ->
                                if not (isN i0) then ok <- false
                                rest |> List.iter walk
                                walk v
                            | IndexOfNamed (b, _, i0 :: rest, _) when b = bufId ->
                                if not (earlierRow i0) then ok <- false
                                rest |> List.iter walk
                            | IRVar (b, _) when b = bufId -> ok <- false
                            | ExprShape (children, _) -> children |> List.iter walk
                        walk loopBody
                        ok
                    let fresh =
                        match Map.tryFind bufId scope with
                        | Some (IRUninit _ | IRCompute _ | IRArrayLit _) -> true
                        | _ -> false
                    if not (match idxs with [ i0; _ ] -> isN i0 | _ -> false) then
                        Error "the store is not `BUF[n][k]` with n the enclosing time loop's variable (v1: rank-2 trajectories)"
                    elif not fresh then Error "the trajectory is not a fresh buffer of this body (a parameter, a module binding, or an alias)"
                    elif not (rowReads |> Seq.forall (fun r -> match r with [ r0 ] -> earlierRow r0 | _ -> false)) then
                        Error "a trajectory read is not a row this worker wrote earlier (`prefix(n - c)` with a literal 1 <= c <= the loop's start) or a seed row written before the loop"
                    elif not bodyUseOk then
                        Error "the time loop reads or writes the trajectory other than storing its own row and reading earlier rows"
                    elif sawRepro then Error "a spliced kernel or callee is `where repro` (its demand does not survive the splice)"
                    elif writes slice then Error "the step may write storage outside its own row"
                    // The buffer is this body's own fresh storage: judged
                    // with it bound locally (the row-fold pass's `writes
                    // block`), so only writes OUTSIDE it count.
                    elif writes (IRLet (bufId, IRUninit bufT, loopBody)) then
                        Error "a statement of the time loop may write storage outside the trajectory"
                    elif not (RecStepTable.record
                                { CellLoopVar = kId; StepVar = nId; StepLo = lo; BufferId = bufId; Cells = ext
                                  Subject = subjectName; KernelName = kernelName k
                                  Fold = ref None; ChunkBounds = ref None; Decided = ref false }) then
                        Error "the time loop already carries a threaded step (one requesting slice per recursion)"
                    else Ok ()
            let expanded =
                match expand ext kVar outsideOk prelude slice with
                | Ok (atom, Some k, sawRepro) ->
                    (match proveStep k sawRepro with
                     | Ok () ->
                        recordRecStep subjectName Blade.Effects.Applied
                            [ $"the `where omp` request of `{(kernelName k)}` (the step's final map) names the fused cell loop: every worker reads only its own cell of strictly earlier rows, so the C++ lane may run the recursion's steps on one persistent team with fixed x ownership and no per-step synchronization (the codegen record under this rule says which form)" ]
                        Ok atom
                     | Error why ->
                        recordRecStep subjectName (Blade.Effects.Declined why)
                            [ $"the slice is not fused: the `where omp` request of `{(kernelName k)}` keeps its own per-step team and the row is copied" ]
                        Error ("the step's final map asks for omp, which the fused cell loop cannot honour: " + why))
                | Ok (atom, None, _) -> Ok atom
                | Error why -> Error why
            match expanded with
            | Ok atom ->
                fired <- fired + 1
                let store = IRLet (aId, IRAssign (IRIndex (IRVar (bufId, bufT), idxs, ident), atom), tail)
                let body = Seq.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) prelude store
                IRLet (wId, IRForRange (kId, IRLit (IRLitInt 0L), IRLit (IRLitInt ext), body), rw enclosing scope rest)
            | Error why ->
                // Only a slice that looks fusable (a call or an elementwise
                // map) is a decision; a literal seed row is just a copy.
                let mutable looksFusable = false
                iterIRExpr (fun n -> match n with IRApp _ | IRApplyCombinator _ -> looksFusable <- true | _ -> ()) slice
                if looksFusable then declines.Add why
                IRLet (sId, rw enclosing scope slice, IRLet (wId, rwInner enclosing scope loop, rw enclosing (Map.add wId loop (Map.add sId slice scope)) rest))
        | IRLet (accId, seedV, IRLet (loopId, (IRForRange (jId, jLo, jHi, IRLet (nxtId, nxtV, IRLet (asgId, IRAssign (IRVar (acc2, accT), IRVar (nxt2, _)), IRLit IRLitUnit))) as loop), rest))
                when acc2 = accId && nxt2 = nxtId && modul.MutableArrayLets.Contains accId
                     && (foldStateCall accId jId nxtV).IsSome ->
            match foldStateInPlace scope accId accT seedV jId nxtV with
            | Ok cellLoop ->
                foldStateFired <- foldStateFired + 1
                let loop' = IRForRange (jId, jLo, jHi, cellLoop)
                IRLet (accId, rw enclosing scope seedV,
                       IRLet (loopId, loop', rw enclosing (Map.add loopId loop' (Map.add accId seedV scope)) rest))
            | Error why ->
                // Exactly the generic let arm below (knob off: byte-identical).
                foldStateDeclines.Add why
                IRLet (accId, rw enclosing scope seedV, rw enclosing (Map.add accId seedV scope) (IRLet (loopId, loop, rest)))
        | IRLet (id, v, body) -> IRLet (id, rw enclosing scope v, rw enclosing (Map.add id v scope) body)
        | _ -> rwInner enclosing scope e
    and rwInner (enclosing: (IRId * int64 * IRExpr) option) (scope: Map<IRId, IRExpr>) (e: IRExpr) : IRExpr =
        match e with
        | IRForRange (k, lo, hi, body) ->
            let inner = match lo with IRLit (IRLitInt l) -> Some (k, l, body) | _ -> None
            IRForRange (k, lo, hi, rw inner scope body)
        | IRLet _ -> rw enclosing scope e
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map (rw enclosing scope))
    // One record per body: applied (with the count) and/or each decline's
    // reason -- a body can hold a fused arm and a declined one.
    let rewriteBody (subject: string) (outerIds: Set<IRId>) (body: IRExpr) =
        let before = fired
        let foldStateBefore = foldStateFired
        declines.Clear ()
        knobDeclines.Clear ()
        foldStateDeclines.Clear ()
        outer <- outerIds
        subjectName <- subject
        let body' = rw None Map.empty body
        let record outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "slice-write-fusion"; Version = 1
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = evidence }
        if fired > before then
            record Blade.Effects.Applied
                [ $"{fired - before} row write(s) computed in place: the slice's pointwise DAG (callees inlined) is one scalar let chain per cell, stored straight into the destination row -- no slice temporary, no copy, no stage pools" ]
        for why in declines do
            record (Blade.Effects.Declined why) [ "the row is materialized and copied as written" ]
        for kname in knobDeclines do
            recordRecStep subject (Blade.Effects.Declined "disabled by BLADE_REC_STEP_OMP")
                [ $"the `where omp` request of `{kname}` (the step's final map) is kept out of the fusion: the map runs as its own per-step team and the row is copied" ]
        let recordFoldState outcome evidence =
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "fold-state-in-place"; Version = 1
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = evidence }
        if foldStateFired > foldStateBefore then
            recordFoldState Blade.Effects.Applied
                [ $"{foldStateFired - foldStateBefore} leading-axis fold(s) update their state in place: per step one pass over the state's cells, each cell's chain s <- k(s, G(j)) evaluated by the kernel's own pointwise arithmetic (callees inlined) and stored back into the same cell -- no per-step slice, no move of a fresh pool into the state (the previous state is no longer leaked)"
                  "fold order: per cell, j ascending from the same seed cell, the materialized step's operations unchanged (bitwise under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off); the state is the desugar's private copy, read only at the cell being written" ]
        for why in foldStateDeclines do
            recordFoldState (Blade.Effects.Declined why)
                [ "each step materializes the kernel's result and moves it into the state, as written" ]
        body'
    let newFunctions =
        modul.Functions |> List.map (fun f ->
            let outerIds = f.Params |> List.fold (fun s p -> Set.add p.VarId s) moduleIds
            { f with Body = rewriteBody f.SourceName outerIds f.Body })
    let newBindings =
        modul.Bindings |> List.map (fun b -> { b with Value = rewriteBody b.Name moduleIds b.Value })
    { modul with Functions = newFunctions; Bindings = newBindings }

// --- Row-fold fusion: consume each recursive-array row while it is hot ----
//
// A recursion whose only consumer is a row-wise fold,
//
//   let T = (let BUF = <fresh> in SEED; for n in L..N { ..row n.. }; BUF) in
//   reduce(method_for(T) <@> lambda(row) -> K(row), F)       -- seed I
//
// re-reads the whole trajectory after the recursion (recur: 48 MB from DRAM,
// ~3 ms of a ~12 ms kernel), while every row was in cache the moment it was
// written. The fold is moved INTO the recursion: an accumulator seeded with
// I, updated `acc <- F(acc, K(BUF(n)))` right after row n is final -- once
// after the seed statements (row 0) and once at the end of each step.
//
// Why it is cost-only (charter rule 1):
//   - `reduce` is a LEFT fold in ascending order (formalism; CLAUDE.md): the
//     rewrite evaluates exactly F(..F(F(I, K(row 0)), K(row 1)).., K(row N-1))
//     with K's own body on each row -- the serial association every build
//     without a reorder licence already uses (BLADE_FP_REPRO=1: bitwise the
//     unfused program). A licensed build gives up only the lane split of an
//     N-term outer sum; each row's K keeps its own (licensed) form;
//   - a row is FINAL when it is folded: the loop writes only row n (every
//     buffer store's leading index is the step variable), the statements
//     before it write only row 0, nothing follows the loop (no `while`
//     freeze epilogue, no break), and every call in the region is repeatable
//     (no `mut` parameter writes a row after it was folded);
//   - K and F are pure and capture-free, so evaluating them earlier
//     (interleaved with the steps) changes no value. Each is evaluated once
//     per row, unconditionally, as before, so an abort is never added or
//     removed; when K (a row prodsum's extent check) and a LATER step would
//     both abort, which one is reported first can change -- the residual the
//     elementwise fusion already accepts;
//   - T has no other use, and the fold is the let's whole body (or the value
//     of its first let): nothing observes the buffer after the recursion.
// Gate: BLADE_FUSION. Plan rule `row-fold-fusion`.
let fuseRowFoldsModule (modul: IRModule) (builder: IRBuilder) (programFuncs: IRCallable list) : IRModule =
    if not (Blade.Toolchain.envFlag "BLADE_FUSION" true) then modul else
    let writes = scheduleMayWriteIn programFuncs modul
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do callables.[f.Id] <- f
    for f in modul.Functions do callables.[f.Id] <- f
    let mentions (vid: IRId) (e: IRExpr) =
        let mutable hit = false
        iterIRExpr (fun n -> match n with IRVar (id, _) when id = vid -> hit <- true | _ -> ()) e
        hit
    let count (vid: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x -> match x with IRVar (id, _) when id = vid -> n <- n + 1 | _ -> ()) e
        n
    // A pure, capture-free scalar kernel. It MAY abort (a row `prodsum` or
    // `reduce` carries extent checks): every row is still folded exactly
    // once, unconditionally, so an abort still happens -- only WHICH abort is
    // reported first can change when a later step would also have aborted,
    // the residual elementwise fusion already accepts (IRMono.
    // occursOnlyUnconditionally). Nothing observable can run in between:
    // every call in the region is repeatable (no output, no writes).
    let plainScalarKernel (arity: int) (k: IRCallable) =
        k.Params.Length = arity && k.Captures.IsEmpty && not k.IsStatic
        && not k.IsOmpParallel && not k.IsCudaKernel && not k.IsMpiParallel && not k.IsArityPoly
        && (match k.RetType with IRTScalar _ -> true | _ -> false)
        && IRMono.tilePureBody callables Set.empty k.Body
        // the splice substitutes k's parameters and renames its lets in the
        // body only: a nested callable capturing one would dangle (gap G93)
        && not (IRMono.nestedCapturesOwnBinder (fun id -> match callables.TryGetValue id with | true, c -> Some c | _ -> None) k)
    let resolve (e: IRExpr) =
        match e with
        | IRVar (kid, _) -> (match callables.TryGetValue kid with | true, k -> Some k | _ -> None)
        | _ -> None
    // Every call in a region is repeatable: no assignment through a `mut`
    // parameter (which could rewrite a row already folded), no output.
    let callsRepeatable (e: IRExpr) =
        let mutable ok = true
        iterIRExpr (fun n ->
            match n with
            | CallOfNamed (fid, _, _, _) ->
                (match callables.TryGetValue fid with
                 | true, c ->
                    if c.IsStatic then ok <- false
                    elif Blade.Effects.isRepeatable c.Effects then ()
                    elif not c.Effects.Unknown then ok <- false
                    elif not (IRMono.tilePureBody callables Set.empty c.Body) then ok <- false
                 | _ -> ok <- false)
            | IRApp _ | IRBreakIf _ | IRReplayRow _ | IRDisplayEmit _ -> ok <- false
            | _ -> ()) e
        ok
    // Every mention of the buffer in a region is a read through a subscript
    // (a row view or a cell) or a store whose leading index is `rowOk`.
    let bufferUseOk (bufId: IRId) (rowOk: IRExpr -> bool) (e: IRExpr) =
        let mutable ok = true
        let rec walk (x: IRExpr) =
            match x with
            | IRAssign (IndexOfNamed (b, _, i0 :: rest, _), v) when b = bufId ->
                if not (rowOk i0) then ok <- false
                rest |> List.iter walk
                walk v
            | IRAssign (IRVar (b, _), _) when b = bufId -> ok <- false
            | IndexOfNamed (b, _, idxs, _) when b = bufId -> idxs |> List.iter walk
            | IRVar (b, _) when b = bufId -> ok <- false
            | ExprShape (children, _) -> children |> List.iter walk
        walk e
        ok
    let rec chainOf (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, b) -> let s, fin = chainOf b in (id, v) :: s, fin
        | _ -> [], e
    let rebuild (stmts: (IRId * IRExpr) list) (fin: IRExpr) =
        List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) stmts fin
    // The (row view, fold) let ids of the LAST foldUpdate built (B2r's
    // CellFoldTable entry names the step's pair).
    let mutable lastFoldIds : (IRId * IRId * IRId) option = None
    // `acc <- F(acc, K(BUF(row)))` as statements ending in `tail`.
    let foldUpdate (accId: IRId) (accT: IRType) (bufId: IRId) (bufT: IRType)
                   (k: IRCallable) (f: IRCallable) (row: IRExpr) (tail: IRExpr) =
        let rowId = builder.FreshId ()
        let pId = builder.FreshId ()
        let assignId = builder.FreshId ()
        lastFoldIds <- Some (rowId, pId, assignId)
        let kParam = k.Params.Head
        let subst (m: Map<IRId, IRExpr>) (body: IRExpr) =
            mapIRExpr (fun x -> match x with IRVar (id, _) -> (match Map.tryFind id m with Some a -> a | None -> x) | _ -> x) body
        let freshenLets (body: IRExpr) =
            let binders = System.Collections.Generic.List<IRId>()
            iterIRExpr (fun x -> match x with IRLet (id, _, _) -> binders.Add id | _ -> ()) body
            let mapped = binders |> Seq.distinct |> Seq.map (fun id -> id, builder.FreshId ()) |> Map.ofSeq
            mapIRExpr (fun x ->
                match x with
                | IRVar (id, t) -> (match Map.tryFind id mapped with Some n -> IRVar (n, t) | None -> x)
                | IRLet (id, v, b) -> (match Map.tryFind id mapped with Some n -> IRLet (n, v, b) | None -> x)
                | _ -> x) body
        let kBody = subst (Map.ofList [ kParam.VarId, IRVar (rowId, kParam.Type) ]) (freshenLets k.Body)
        let fBody =
            subst (Map.ofList [ f.Params.[0].VarId, IRVar (accId, accT); f.Params.[1].VarId, IRVar (pId, k.RetType) ])
                  (freshenLets f.Body)
        IRLet (rowId, IRIndex (IRVar (bufId, bufT), [ row ], None),
            IRLet (pId, kBody,
                IRLet (assignId, IRAssign (IRVar (accId, accT), fBody), tail)))
    let record (subject: string) outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "row-fold-fusion"; Version = 1
              Span = Blade.Ast.noSpan; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let mutable subject = ""
    // Try the rewrite at `let T = BLOCK in E`. Error = decline reason (only
    // reported when E really is a row fold over T).
    let tryAt (tId: IRId) (block: IRExpr) (e: IRExpr) : Result<IRExpr, string> option =
        // The consumer: E is the fold, or E's first let binds it.
        let consumer, rebuildE =
            match e with
            | IRReduceCompute _ -> Some e, (fun (acc: IRExpr) -> acc)
            | IRLet (x, (IRReduceCompute _ as r), rest) when not (mentions tId rest) ->
                Some r, (fun acc -> IRLet (x, acc, rest))
            | _ -> None, id
        match consumer with
        | Some (IRReduceCompute (IRApplyCombinator info, foldK, init)) when
                (match info.Arrays with [ IRVar (a, _) ] -> a = tId | _ -> false) ->
            if count tId e <> count tId (IRReduceCompute (IRApplyCombinator info, foldK, init)) then Some (Error "the recursion's result has another use beside the fold") else
            match block with
            // The buffer is FRESH: the unfilled pool of a proved recursion,
            // the zero former of a large one, or the nested zero literal the
            // desugar writes for a small one (<= 4096 cells, e.g. every small
            // `while`-guarded recursion, whose fill the freeze needs).
            | IRLet (bufId, (IRUninit _ | IRCompute _ | IRArrayLit _), chain) ->
                let stmts, fin = chainOf chain
                let bufT =
                    match block with
                    | IRLet (_, IRUninit t, _) -> Some t
                    | _ -> (match fin with IRVar (b, t) when b = bufId -> Some t | _ -> None)
                match fin, bufT, List.tryLast stmts with
                | IRVar (b, _), Some bufT, Some (loopId, IRForRange (nId, IRLit (IRLitInt lo), IRLit (IRLitInt hi), body)) when b = bufId ->
                    let pre = stmts |> List.take (stmts.Length - 1)
                    let leading =
                        match bufT with
                        | ArrayElem at ->
                            (match at.IndexTypes with
                             | ix :: _ :: _ -> (match ix.Extent with IRLit (IRLitInt n) -> Some n | _ -> None)
                             | _ -> None)
                        | _ -> None
                    let rowKernel = resolve info.Kernel
                    let foldKernel = resolve foldK
                    let isN (i: IRExpr) = match i with IRVar (v, _) -> v = nId | _ -> false
                    let isZero (i: IRExpr) = match i with IRLit (IRLitInt 0L) -> true | _ -> false
                    let rec unitTail (x: IRExpr) =
                        match x with
                        | IRLet (_, _, b) -> unitTail b
                        | IRLit IRLitUnit -> true
                        | _ -> false
                    if leading <> Some hi then Some (Error "the recursion loop does not end at the buffer's leading extent")
                    elif not (lo = 0L || lo = 1L) then Some (Error "the recursion loop starts past row 1")
                    elif info.KernelOutputRank <> 0 || info.HasReynolds || info.IsCoIteration then
                        Some (Error "the fold's map is not a plain per-row map")
                    elif not (match info.OutputType with ArrayElem at -> at.IndexTypes.Length = 1 | _ -> false) then Some (Error "the fold does not reduce the rows to one scalar") else
                    // The step's row is written by ONE slice-write-fused
                    // cell loop whose last statement stores a scalar local
                    // into BUF[n][k] (B2r's and E-G5's shape).
                    let cellLoop =
                        let stepStmts, _ = chainOf body
                        let writers =
                            stepStmts |> List.filter (fun (_, s) -> mentions bufId s && (match s with IRForRange _ -> true | _ -> false))
                        match writers with
                        | [ (_, IRForRange (kv, IRLit (IRLitInt 0L), IRLit (IRLitInt _), cb)) ] ->
                            let cstmts, cfin = chainOf cb
                            (match List.tryLast cstmts, cfin with
                             | Some (_, IRAssign (IndexOfNamed (b, _, [ i0; IRVar (k2, _) ], _), IRVar (sv, _))), IRLit IRLitUnit
                                when b = bufId && isN i0 && k2 = kv
                                     && cstmts |> List.exists (fun (id, _) -> id = sv) ->
                                Ok (kv, sv)
                             | _ -> Error "the step's cell loop does not end in one store of a scalar local into its own row")
                        | [] -> Error "the step's row is not written by a slice-write-fused cell loop"
                        | _ -> Error "the step's row is written by more than one loop"
                    // Wave 3 (E-G5): the step is THREADED (its cell loop has a
                    // RecStepTable entry under this time loop).
                    let stepEntry =
                        match cellLoop with
                        | Ok (kv, _) ->
                            (match RecStepTable.tryLoop kv with
                             | Some entry when entry.StepVar = nId && entry.BufferId = bufId -> Some entry
                             | _ -> None)
                        | Error _ -> None
                    // A kernel demanding reproducible evaluation (`where repro`
                    // itself, or the eta-wrapper of such a function).
                    let reproCallee (c: IRCallable) =
                        c.IsRepro
                        || (match c.Body with
                            | CallOfNamed (fid, _, _, _) -> (match callables.TryGetValue fid with | true, g -> g.IsRepro | _ -> false)
                            | _ -> false)
                    // A `where omp` on the OUTER fold kernel F is admitted only
                    // behind a threaded step, where it is the request the
                    // row post-pass reads (G35's gate); otherwise it declines
                    // as before (the fold then keeps its own thread forms). A
                    // `where repro` F is never admitted this way: the splice
                    // would drop its BLADE_REPRO_FN boundary (Codex diff
                    // review, finding 1).
                    let outerKernelOk (f: IRCallable) =
                        plainScalarKernel 2 f
                        || (stepEntry.IsSome && f.IsOmpParallel && not (reproCallee f)
                            && plainScalarKernel 2 { f with IsOmpParallel = false })
                    match rowKernel, foldKernel with
                    | Some k, Some f when plainScalarKernel 1 k && outerKernelOk f ->
                        let preOk = pre |> List.forall (fun (_, s) -> bufferUseOk bufId isZero s && callsRepeatable s)
                        let seedWrites = pre |> List.exists (fun (_, s) -> mentions bufId s)
                        if not (bufferUseOk bufId isN body && callsRepeatable body && unitTail body) then
                            Some (Error "a step writes a row other than its own, calls something that is not repeatable, or may stop early")
                        // G41: the fold moves INTO the recursion, between its
                        // steps -- all five parties through the shared write
                        // predicate (the recursion's own buffer is local to it).
                        elif writes block then Some (Error "the recursion (the producer) may write storage outside its own buffer")
                        elif writes (IRApplyCombinator info) then Some (Error "the row kernel may write")
                        elif writes foldK then Some (Error "the fold kernel may write")
                        elif writes init then Some (Error "the fold's seed may write")
                        elif not preOk then Some (Error "a statement before the recursion writes a row other than the seed row")
                        elif lo = 1L && not seedWrites then Some (Error "no seed statement writes row 0")
                        elif lo = 0L && seedWrites then Some (Error "a statement before a seedless recursion writes the buffer") else
                        let accId = builder.FreshId ()
                        let accT = f.RetType
                        // The step variable as the body uses it (a named
                        // recursion axis types it as that index).
                        let nVar =
                            let mutable found = None
                            iterIRExpr (fun x -> match x with IRVar (v, t) when v = nId && found.IsNone -> found <- Some (IRVar (v, t)) | _ -> ()) body
                            defaultArg found (IRVar (nId, IRTScalar ETInt64))
                        // The step body: its unit tail becomes the fold update.
                        let rec withUpdate (x: IRExpr) =
                            match x with
                            | IRLet (id, v, b) -> IRLet (id, v, withUpdate b)
                            | _ -> foldUpdate accId accT bufId bufT k f nVar x
                        let loop' =
                            match snd (List.last stmts) with
                            | IRForRange (nv, l, h, _) -> IRForRange (nv, l, h, withUpdate body)
                            | other -> other
                        let stepFoldIds = lastFoldIds
                        // Wave 3 (E-G5): a THREADED step carries the fold
                        // facts too: the C++ lane's time-loop emitter folds
                        // each row inside the team (the self-dot's licensed
                        // form per worker chunk) or after it (complete rows,
                        // strict). The B2r entry below is STILL recorded: the
                        // serial fallbacks (threads suppressed, the LLVM lane)
                        // keep the chain interleave exactly as before, and the
                        // threaded emission declines it at its own site.
                        (match stepEntry, stepFoldIds with
                         | Some entry, Some (rowId, pId, assignId) ->
                            entry.Fold.Value <-
                                Some { RowLetId = rowId; FoldLetId = pId; AccId = accId; AssignLetId = assignId
                                       Term = rowKernelCellTerm k; OuterRequest = f.IsOmpParallel
                                       StrictOnly = reproCallee k || reproCallee f }
                            recordRecStep subject Blade.Effects.Applied
                                [ (match rowKernelCellTerm k, f.IsOmpParallel with
                                   | Some _, true -> "the fused row fold rides the team: a licensed build folds each worker's own chunk of the row it just wrote (the self-dot's licensed form), the chunks combined ascending from +0 per row; a strict build folds COMPLETE rows into indexed slots after the team, rows in parallel under the outer fold kernel's own `where omp`; the time chain then runs ascending from its seed"
                                   | Some _, false -> "the fused row fold rides the team: a licensed build folds each worker's own chunk of the row it just wrote (the self-dot's licensed form), the chunks combined ascending from +0 per row; a strict build folds COMPLETE rows serially after the team (no `where omp` on the outer fold kernel licenses a parallel row pass); the time chain then runs ascending from its seed"
                                   | None, _ -> "the fused row fold runs serially after the team: the row kernel on each COMPLETE row, then the time chain ascending from its seed (only the self-dot `prodsum(row, row)` has an in-team form)") ]
                         | _ -> ())
                        // B2r (round-2 cell epilogues): the row kernel is
                        // prodsum(row, row): record the loop so an emitter
                        // may advance the row's chain per cell (the IR is
                        // unchanged).
                        (match rowKernelCellTerm k, stepFoldIds with
                         | Some term, Some (rowId, pId, _) ->
                            match cellLoop with
                            | Error why ->
                                recordCellEpilogue subject (Blade.Effects.Declined why)
                                    [ "the row's prodsum runs on the finished row after the step" ]
                            | Ok _ when not (cellEpilogueEnabled ()) ->
                                recordCellEpilogue subject (Blade.Effects.Declined "disabled by BLADE_CELL_EPILOGUE")
                                    [ "the row's prodsum runs on the finished row after the step" ]
                            | Ok (kv, sv) ->
                                CellFoldTable.record
                                    { CellLoopVar = kv; BufferId = bufId; StepVar = nId; StoredVar = sv
                                      FoldLetId = pId; RowLetId = rowId; Term = term; Subject = subject
                                      EmittedCpp = ref None; EmittedLlvm = ref None }
                         | None, _ ->
                            recordCellEpilogue subject (Blade.Effects.Declined "the row kernel is not a +0-seeded dot-class fold of the row's own cells (v1: `prodsum(row, row)`)")
                                [ "the row's fold runs on the finished row after the step" ]
                         | _ -> ())
                        let preStmts =
                            if lo = 1L then
                                let u = builder.FreshId ()
                                pre @ [ u, foldUpdate accId accT bufId bufT k f (IRLit (IRLitInt 0L)) (IRLit IRLitUnit) ]
                            else pre
                        let block' = IRLet (bufId, (match block with IRLet (_, v, _) -> v | _ -> block),
                                            rebuild (preStmts @ [ loopId, loop' ]) fin)
                        Some (Ok (IRLet (accId, init, IRLet (tId, block', rebuildE (IRVar (accId, accT))))))
                    | _ -> Some (Error "the per-row kernel or the fold operator is not a plain pure capture-free scalar kernel")
                | _ when stmts |> List.exists (fun (_, s) -> match s with IRForRange _ -> true | _ -> false) ->
                    Some (Error "the recursion's statements are not a seed followed by one step loop ending the block (a `while` freeze epilogue, or another statement after the loop)")
                | _ -> None
            | _ -> None
        | _ -> None
    let rec rw (e: IRExpr) : IRExpr =
        match e with
        | IRLet (tId, block, body) ->
            (match tryAt tId block body with
             | Some (Ok e') ->
                record subject Blade.Effects.Applied
                    [ "the row-wise fold over the recursion's result is accumulated inside the recursion: each row folded right after it is written (seed row after the seed), ascending, the fold's own left association; the trajectory is not re-read" ]
                (match e' with
                 | IRLet (a, i, IRLet (t, b, rest)) -> IRLet (a, i, IRLet (t, rwInner b, rw rest))
                 | _ -> e')
             | Some (Error why) ->
                record subject (Blade.Effects.Declined why) [ "the fold re-reads the trajectory after the recursion" ]
                IRLet (tId, rw block, rw body)
             | None -> IRLet (tId, rw block, rw body))
        | _ -> rwInner e
    and rwInner (e: IRExpr) =
        match e with
        | IRLet _ -> rw e
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map rw)
    let newFunctions =
        modul.Functions |> List.map (fun f ->
            subject <- f.SourceName
            { f with Body = rw f.Body })
    let newBindings =
        modul.Bindings |> List.map (fun b ->
            subject <- b.Name
            { b with Value = rw b.Value })
    { modul with Functions = newFunctions; Bindings = newBindings }
