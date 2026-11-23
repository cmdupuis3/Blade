// The recursive-array DESUGAR, outside the inference chain (cleanup round 2, K5): recArrayLower and
// recArrayLowerArray turn a `let rec` array binding into its checked block (the full buffer) or, under a window,
// into the ring-and-snapshot statements that replace it. The checked block goes back through the chain's
// checkExpr, reached through InferEntries (TypeCheckInferEntries.fs); the chain's inferRecArray and
// recArrayWindowDecision call these functions with its `inferEntries` value.
module Blade.TypeCheckInferRecArray

open Blade.Ast
open Blade.IR
open Blade.IRLoopStructure
open Blade.IRStorage
open Blade.IRLift
open Blade.IRMono
open Blade.IRPrint
open Blade.IRValidate
open Blade.Types
open Blade.TypedAst
open Blade.Unify
open Blade.TypeEnv
open Blade.Zonk
open Blade.TypeCheckIde
open Blade.TypeLower
open Blade.TypeCheckSupport
open Blade.TypedBuilder
open Blade.TypeCheckInferUnits
open Blade.TypeCheckInferApply
open Blade.TypeCheckInferEntries

/// The recursion's desugar. `window = None`: today's full buffer, checked and
/// returned as the binding's value. `window = Some (K, R)` (decided by
/// recArrayWindowDecision): a K+1-slot ring `__ring_NAME` -- slice i lives in
/// slot i mod (K+1) -- plus one snapshot `__snap_<p>_NAME` per row p in R,
/// returned UNCHECKED as the statements that replace the binding (NAME
/// dissolves; inferBlock checks them in place). Zero history is untouched:
/// out-of-prefix reads never reach the buffer, and the guard clamp keeps the
/// discarded branch's ring index in bounds.
let rec recArrayLower (ie: InferEntries) (env: TypeEnv) (annot: TypeExpr) (annotTy: IRType) (def: RecArrayDef) (span: Span)
                  (window: (int64 * int64 list) option) (replayRows: bool) : TypeResult<RecLowered> =
    // Freeze-idiom recognition (Blade.Optimize, plan-match-statements.md
    // section 5 R7/B): an unguarded inductive arm whose slice is
    // `if G then STEP else prefix(n - 1)` with a lag-1-only guard is
    // repartitioned into the guarded form and rides the whole `while`
    // machinery below -- EXCEPT the budget abort, which is the `while`
    // spelling's contract and not this idiom's. Cost-only: the derived
    // early exit plus the freeze epilogue write byte-identical values to
    // running the budget out. Recognition happens here, not at IR level,
    // because this is the last seam where the idiom's declarative shape
    // still exists.
    //
    // The guard may CALL only what is REPEATABLE (Blade.Effects): an
    // unshadowed scalar intrinsic -- the math table (exp/sqrt/...), abs and
    // the complex accessors, atan2/log_base, fma, the numeric casts
    // (`Float64(x)`) -- or a DECLARED function whose effect summary
    // (FuncEffects, computed from its typed body with callees resolved) has
    // no assignment, no display, no external read and no unknown call. A
    // helper that mutates a `mut` argument declines: the skipped evaluations
    // would not be repeats (Optimize.fs has the reproducer). `lookupVar` is
    // the same shadowing test the intrinsic arms of inferExpr apply; a
    // shadowing user FUNCTION is judged by its own summary, a shadowing
    // local (a lambda-valued let) is unknown and declines. Returns the
    // reason a name is refused, for the decision record.
    let calleeAdmissible (name: string) : string option =
        match lookupVar name env with
        | None ->
            if isUnaryIntrinsic name || isBinaryIntrinsic name
               || Blade.GradCommon.isTernaryMathIntrinsic name
               || (castTargetOf name).IsSome then None
            else Some $"`{name}` is neither a scalar intrinsic nor a declared function"
        | Some info when env.DeclaredFuncIds.Contains info.VarId ->
            (match env.FuncEffects.TryGetValue info.VarId with
             | true, eff when Blade.Effects.isRepeatable eff -> None
             | true, eff -> Some $"`{name}` {Blade.Effects.describe eff}"
             | _ -> Some $"`{name}` has no effect summary")
        | Some _ -> Some $"`{name}` is a local binding (a lambda-valued let or a shadowed intrinsic), whose effects are unknown"
    let def, guardIsBestEffort =
        match Blade.Optimize.recognizeFreezeIdiom calleeAdmissible def with
        | Some recognized -> recognized, true
        | None -> def, false
    let synAt k = mkExpr span k
    match annotTy with
    | ArrayElem at when not at.IndexTypes.IsEmpty ->
        recArrayLowerArray ie env at def annot synAt window span replayRows guardIsBestEffort annotTy
    | _ ->
        Error (RecArrayFormErr $"recursive array '{def.Name}': the annotation must be an Array type (`let rec {def.Name}: Array<T like Step, ...> = ...`)")

and recArrayLowerArray (ie: InferEntries) (env: TypeEnv) (at: IRArrayType) (def: RecArrayDef) (annot: TypeExpr) (synAt: ExprKind -> Expr) (window: (int64 * int64 list) option) (span: Span) (replayRows: bool) (guardIsBestEffort: bool) (annotTy: IRType) : TypeResult<RecLowered> =
    let checkExpr = ie.CheckExpr
    // Static leading extent (a deliberate bound).
    let extentOf (ix: IRIndexType) =
        match ix.Extent with
        | IRLit (IRLitInt n) -> Some n
        | _ -> None
    match extentOf at.IndexTypes.Head with
    | None ->
        Error (RecArrayFormErr $"recursive array '{def.Name}': the leading (recursion) axis must have a static extent -- dynamic-extent recurrences are not supported")
    | Some n when n < 1L ->
        Error (RecArrayFormErr $"recursive array '{def.Name}': the recursion axis extent must be >= 1 (got {n})")
    | Some n ->
    // Element zero expression for the buffer pre-fill: Float/Int
    // literals, complex(0, 0) for complex elements. (Record/tuple
    // slices land with the IR-level alloc.)
    // The zero is ASCRIBED to the declared element type: a bare `0` types
    // Int64, and the zero-history `if` that alternates it with an Int32
    // buffer read used to pass only because the `if` arm discarded its
    // branch-unification failure (the C++ ternary widened silently). The
    // arm now checks its branches, so the zero has to say which width it
    // is.
    let ascribeElem (lit: Expr) =
        match annot with
        | TyArray (elemT, _) -> synAt (ExprTyped (lit, elemT))
        | _ -> lit
    let zeroElem () =
        match env.Subst.Resolve at.ElemType with
        | IRTScalar ETFloat64 | IRTScalar ETFloat32 -> Ok (ascribeElem (synAt (ExprLit (LitFloat 0.0))))
        | IRTScalar ETInt64 | IRTScalar ETInt32 -> Ok (ascribeElem (synAt (ExprLit (LitInt 0L))))
        | IRTScalar ETComplex128 | IRTScalar ETComplex64 ->
            Ok (ascribeElem (synAt (ExprApp (synAt (ExprVar "complex"),
                                             [synAt (ExprLit (LitFloat 0.0)); synAt (ExprLit (LitFloat 0.0))]))))
        | _ -> Error (RecArrayFormErr $"recursive array '{def.Name}': only Float/Int/Complex element types are supported (record/tuple slices land with the IR-level alloc)")
    // Trailing (slice) axes must be static too -- they drive the
    // desugared copy nest and the zero-fill literal.
    let trailingOpt = at.IndexTypes.Tail |> List.map extentOf
    if trailingOpt |> List.exists Option.isNone then
        Error (RecArrayFormErr $"recursive array '{def.Name}': all slice-axis extents must be static")
    else
    let trailing = trailingOpt |> List.map Option.get
    zeroElem () |> Result.bind (fun zed ->
    // ZERO-FILL ELISION (formalism 7.5; bench/notes/microkernel-sketches-review.md (removed 2026-10-24; in git history at cf2028cc),
    // M3). The buffer's zero fill exists for exactly two kinds
    // of read: a WHOLE-array use of `prefix` (which sees the unbuilt rows
    // as zero) and the freeze epilogue of a `while`-guarded recurrence
    // (which, stopping at n = 0, copies row 0 before anything wrote it).
    // Every other read is a row/cell read `prefix(i, ...)`, and the
    // desugar below routes each one through `guardsFor`: provably inside
    // the built prefix [0, n) -- a row the seed or an earlier step already
    // wrote -- or guarded, answering zero (the element zero or a zero
    // slice) outside it without the buffer's value ever being selected.
    // The seed writes row 0, the inductive arm row n for every n in
    // [loopStart, N), so after the recursion every row has been written.
    // Hence: when there is no `while` guard and EVERY occurrence of the
    // prefix in the inductive arm is the head of a non-empty application,
    // no read can observe a cell before its write, and the fill is dead.
    // The check is syntactic and conservative (shadowing ignored, an
    // alias `let h = prefix` counts as a whole use; the array's own name
    // or the buffer's anywhere declines); the implicit-zero semantics of
    // reads past the built prefix are untouched -- they never read the
    // buffer. Lowering binds a `__recu_` buffer to IRUninit (no fill) when
    // its shape is dense and static, else lowers the zero former as before.
    let elideZeroFill =
        let ok = ref true
        let rec walk (e: Expr) =
            Blade.Unfold.mapExprPre (fun x ->
                match x.Kind with
                | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar p }, args) when p = def.PrefixVar && not args.IsEmpty ->
                    for a in args do walk a
                    Some x
                | ExprKind.ExprVar v when v = def.PrefixVar || v = def.Name || v.StartsWith "__rec" ->
                    ok.Value <- false
                    None
                | _ -> None) e |> ignore
        walk def.SliceExpr
        // The seed is not rewritten by the guard discipline, so there ANY
        // mention of the prefix (or the array, or the buffer) declines.
        (match def.SeedArm with
         | Some (_, seedExpr) ->
             Blade.Unfold.mapExprPre (fun x ->
                 match x.Kind with
                 | ExprKind.ExprVar v when v = def.PrefixVar || v = def.Name || v.StartsWith "__rec" ->
                     ok.Value <- false
                     None
                 | _ -> None) seedExpr |> ignore
         | None -> ())
        def.Guard.IsNone && ok.Value
    // The ring keeps the elision: every read of it is a guarded lag read
    // (rows n - c, written at step n - c and not overwritten before step
    // n - c + K + 1 > n) or a snapshot copy of a row just written.
    let ringSize = match window with Some (k, _) -> k + 1L | None -> n
    let snaps = match window with Some (_, r) -> r | None -> []
    let bufName =
        match window, elideZeroFill with
        | Some _, true -> $"{Blade.IR.recUninitBufferPrefix}{recRingName def.Name}"
        | Some _, false -> recRingName def.Name
        | None, true -> $"{Blade.IR.recUninitBufferPrefix}{def.Name}"
        | None, false -> $"__rec_{def.Name}"
    let bufVar = synAt (ExprVar bufName)
    let iLit (v: int64) = synAt (ExprLit (LitInt v))
    // Leading subscript into the buffer. Full: the ordinal itself.
    // Windowed: slot `i mod (K+1)`, applied AFTER the guard clamp so the
    // branch a guard discards still forms an in-bounds index. The cast:
    // a position of a named step axis is an index value, the ring's head
    // slot an anonymous Idx<K+1>.
    let ringIdx (e: Expr) : Expr =
        match window with
        | None -> e
        | Some _ when ringSize = 1L -> iLit 0L
        | Some _ -> synAt (ExprBinOp (Elementwise, OpMod, synAt (ExprUnaryOp (OpCast "Int64", e)), iLit ringSize))
    // Buffer pre-fill, checked against the DECLARED annotation (keeps
    // named index types / units authoritative). Small buffers use a
    // nested zero literal; large ones (QG-scale trajectories) would
    // explode the AST as literals (millions of nodes), so they
    // materialize through a zero FORMER over the annotation's own
    // slots -- method_for(range<slots...>) <@> lambda(...) -> 0.
    // Zero fill over a list of axes -- the whole buffer (leading :: trailing)
    // or, for the zero-history slices below, the trailing axes alone.
    let annotSlots = match annot with TyArray (_, slotTys) -> Some slotTys | _ -> None
    let zerosOver (tag: string) (slotsOpt: TypeExpr list option) (exts: int64 list) =
        let total = List.fold (fun a b -> a * b) 1L exts
        match slotsOpt with
        | Some slotTys when total > 4096L ->
            let ps : LambdaParam list =
                slotTys |> List.mapi (fun i _ -> { Name = $"__{tag}{i}_{def.Name}"; Type = None; Default = None; NameSpan = noSpan })
            synAt (ExprCompute (synAt (ExprBinOp (Elementwise, OpApply,
                synAt (ExprMethodFor [synAt (ExprRange slotTys)]),
                synAt (ExprLambda (ps, None, zed))))))
        | _ ->
            List.foldBack (fun ext inner -> synAt (ExprArrayLit (List.replicate (int ext) inner)))
                          exts zed
    // The ring's head slot is an ANONYMOUS Idx<K+1>: a named tag there
    // would make every `i mod (K+1)` subscript a tag mismatch (BL4003's
    // error branch, which the `__` buffer-name gate does not suppress).
    // Trailing slots and the element type (units included) are copied
    // verbatim, so slice writes check exactly as against the full buffer.
    let bufSlots =
        match window, annotSlots with
        | Some _, Some (_ :: rest) -> Some (TyIdx (iLit ringSize) :: rest)
        | Some _, _ -> None
        | None, s -> s
    let bufAnnot =
        match window, annot, bufSlots with
        | Some _, TyArray (elemT, _), Some slots -> TyArray (elemT, slots)
        | _ -> annot
    if window.IsSome && bufSlots.IsNone then
        // Unreachable: recArrayWindowDecision demotes a non-`Array<...>`
        // annotation, since the ring's head slot is rebuilt from its slots.
        Error (InternalErr $"recursive array '{def.Name}': internal error -- cannot window an annotation without literal Array slots")
    else
    let zerosValue = zerosOver "z" bufSlots (ringSize :: trailing)
    let bufLet = StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar bufName); Type = Some bufAnnot; Value = zerosValue }
    // Write one slice into the buffer at step position `stepIdxE`.
    // Rank-1: direct scalar assign. Rank >= 2: materialize the slice
    // once (`let __slice = e`), then a nested elementwise copy over the
    // trailing axes -- the same shape the imperative double-buffer
    // corpus used, so the whole machinery downstream is proven.
    let sliceCopyStmts (stepIdxE: Expr) (srcName: string) (srcExpr: Expr) : Stmt list =
        match trailing with
        | [] -> [ StmtExpr (synAt (ExprAssign (synAt (ExprApp (bufVar, [stepIdxE])), srcExpr))) ]
        | _ ->
            let ivars = trailing |> List.mapi (fun k _ -> $"__ri{k}_{def.Name}")
            let idxEs = ivars |> List.map (fun v -> synAt (ExprVar v))
            let srcVar = synAt (ExprVar srcName)
            let assign =
                StmtExpr (synAt (ExprAssign (
                    synAt (ExprApp (bufVar, stepIdxE :: idxEs)),
                    synAt (ExprApp (srcVar, idxEs)))))
            let nest =
                List.foldBack2 (fun ivar ext inner ->
                    [ StmtForIn (ivar, synAt (ExprKind.ExprDotDot (iLit 0L, iLit ext)), inner) ])
                    ivars trailing [assign]
            StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar srcName); Type = None; Value = srcExpr } :: nest
    // Seed (extent-1) arm: slice write at step 0 with the step ordinal
    // substituted to the literal 0.
    let seedStmts, loopStart =
        match def.SeedArm with
        | Some (seedStep, seedExpr) ->
            let seeded = Blade.Unfold.substFree (Map.ofList [seedStep, iLit 0L]) seedExpr
            sliceCopyStmts (iLit 0L) $"__seed_{def.Name}" seeded, 1L
        | None -> [], 0L
    // Implicit zero history (formalism 7.5; monadic zero 10.4): a prefix
    // read OUTSIDE the prefix built so far resolves to the element
    // type's zero (mirrors the identity base case of recursive kernels,
    // 8.2), so `prefix(n - 3)` at n < 3 IS zero by specification and
    // callers don't hand-guard it. Must be ENFORCED, not inherited from
    // the buffer's zero pre-fill: the desugared read `__rec_x[n - 3]`
    // indexes BEFORE the buffer at n < 3, yielding garbage. Reads
    // provably inside [0, n) keep their bare, branch-free form.
    let stepVarE = synAt (ExprVar def.StepVar)
    let isStepVar (e: Expr) =
        match e.Kind with ExprKind.ExprVar v -> v = def.StepVar | _ -> false
    // Which of the two bounds can this leading index actually violate?
    // Recognises the index shapes a recursion produces (n, n - c, n + c, a
    // constant); anything else is guarded on both sides.
    let guardsFor (idx: Expr) : bool * bool =
        match idx.Kind with
        | _ when isStepVar idx -> false, true
        | ExprKind.ExprBinOp (_, OpSub, l, { Kind = ExprKind.ExprLit (LitInt c) }) when isStepVar l ->
            // idx = n - c over n in [loopStart, N): least value is
            // loopStart - c, and idx < n exactly when c > 0.
            (loopStart - c < 0L), (c <= 0L)
        | ExprKind.ExprBinOp (_, OpAdd, l, { Kind = ExprKind.ExprLit (LitInt c) }) when isStepVar l ->
            (loopStart + c < 0L), (c >= 0L)
        | ExprKind.ExprLit (LitInt c) ->
            // Constant index: inside the prefix at every step iff
            // 0 <= c < loopStart.
            (c < 0L), (c >= loopStart)
        | _ -> true, true
    // Nest the (at most two) bounds rather than conjoining them, so every
    // emitted condition stays a single comparison.
    let guardWrap needsLo needsHi (idx: Expr) (inPrefix: Expr) (outside: Expr) =
        let hi =
            if needsHi then synAt (ExprIf (synAt (ExprBinOp (Elementwise, OpLt, idx, stepVarE)), inPrefix, outside))
            else inPrefix
        if needsLo then synAt (ExprIf (synAt (ExprBinOp (Elementwise, OpGe, idx, iLit 0L)), hi, outside))
        else hi
    // Zero SLICES for guarded partial reads, one per read depth (a rank-3
    // family can be read as a rank-2 row or a rank-1 line). Loop-invariant,
    // so they bind at block level; allocated only if some read needs one.
    let zeroSlices = ResizeArray<int * string>()
    let zeroSliceFor (dropped: int) : Expr =
        let name =
            match zeroSlices |> Seq.tryFind (fun (d, _) -> d = dropped) with
            | Some (_, nm) -> nm
            | None ->
                let nm = $"__zs{dropped}_{def.Name}"
                zeroSlices.Add (dropped, nm)
                nm
        synAt (ExprVar name)
    // Rank >= 2: PARTIAL prefix reads (lag rows, e.g. prefix(n-1) on a
    // rank-2 family) must flow through a BINDING so CodeGen's dense
    // row-view wrap (densePartialSubview) fires -- a bare partial read in
    // argument position renders as a raw row pointer. Hoist each
    // distinct partial application into a loop-body let; scalar reads
    // (full applications) stay in place.
    // The lag-hoist table is SHARED between the slice and the `while`
    // guard: both read the prefix through the same rewrite, so a lag row
    // they both touch hoists once, and the guard rides the identical
    // bounds discipline and zero-history clamps for free.
    let hoisted = ResizeArray<string * string * Expr>()  // key, name, value
    let emit (key: string) (value: unit -> Expr) =
        match hoisted |> Seq.tryFind (fun (k2, _, _) -> k2 = key) with
        | Some (_, nm, _) -> nm
        | None ->
            let nm = $"__lag{hoisted.Count}_{def.Name}"
            hoisted.Add (key, nm, value ())
            nm
    let rewritePrefixReads (slice: Expr) : Expr =
        let slice' =
            Blade.Unfold.mapExprPre (fun x ->
                match x.Kind with
                | ExprKind.ExprApp ({ Kind = ExprKind.ExprVar p }, args)
                        when p = def.PrefixVar && not args.IsEmpty ->
                    let idx = args.Head
                    let rest = args.Tail
                    let needsLo, needsHi = guardsFor idx
                    let isPartial = args.Length <= trailing.Length
                    let key = sprintf "%A" (args |> List.map (_.Kind))
                    if not (needsLo || needsHi) then
                        // Provably inside the prefix -- unchanged (full),
                        // or redirected to its ring slot (windowed; the
                        // classifier rejected prefix reads nested in
                        // `rest`, so not descending into it loses none).
                        if isPartial then Some (synAt (ExprVar (emit key (fun () -> synAt (ExprApp (bufVar, ringIdx idx :: rest))))))
                        elif window.IsSome then Some (synAt (ExprApp (bufVar, ringIdx idx :: rest)))
                        else None
                    else
                        // Read through a CLAMPED index: merely FORMING an
                        // out-of-range row view is undefined, so the
                        // discarded branch must stay in bounds too.
                        // (`Int64(idx)`: a position of a named step axis
                        // is an index value; the clamp's `0` is not.)
                        let safeIdx = guardWrap needsLo needsHi idx (synAt (ExprUnaryOp (OpCast "Int64", idx))) (iLit 0L)
                        let read = synAt (ExprApp (bufVar, ringIdx safeIdx :: rest))
                        if isPartial then
                            // Only the READ is hoisted (that is what the
                            // row-view wrap keys off); the select stays
                            // INLINE. Whichever consumer needs the select
                            // under a name binds it itself -- IR's lift
                            // pass hoists an array-valued select out of a
                            // loop form's `Arrays` slot
                            // (`isArrayValuedSelect`), and CodeGen declares
                            // such a binding on the Array<T,N> wrapper path
                            // (`producesWrapperOf`). Hoisting it here
                            // instead would name it even where it is only
                            // ever read inline.
                            let raw = emit key (fun () -> read)
                            Some (guardWrap needsLo needsHi idx (synAt (ExprVar raw)) (zeroSliceFor rest.Length))
                        else Some (guardWrap needsLo needsHi idx read zed)
                | _ -> None) slice
        slice'
    // ALIASES OF THE PREFIX ARE INLINED FIRST. `let history = prefix`
    // followed by `history(n - 2)` is a prefix READ, and the rewrite
    // above recognizes reads by the callee's spelling; left as an alias,
    // the read fell through `substBuf` to the raw buffer with no bounds
    // discipline, and `history(n - 2)` at n = 1 read cell -1 (an
    // uninitialized double where the zero-history rule says 0). The
    // alias binding is dropped and its name substituted through its
    // scope (shadowing-aware, via substFree's binder threading); a
    // WHOLE-ARRAY use of `prefix` needs no rewrite, because the buffer
    // is zero beyond the built prefix by construction (`zerosValue`).
    let isPrefixAlias (b: Binding) =
        match b.Pattern.Kind, b.Value.Kind with
        | PatVar _, ExprKind.ExprVar p -> p = def.PrefixVar
        | _ -> false
    let aliasName (b: Binding) = match b.Pattern.Kind with PatVar v -> v | _ -> ""
    let rec inlinePrefixAliases (e: Expr) : Expr =
        let rec mapStmt (s: Stmt) : Stmt =
            match s with
            | StmtSpanned (i, sp) -> StmtSpanned (mapStmt i, sp)
            | StmtLet b -> StmtLet { b with Value = inlinePrefixAliases b.Value }
            | StmtExpr x -> StmtExpr (inlinePrefixAliases x)
            | StmtAssign (l, op, r) -> StmtAssign (l, op, inlinePrefixAliases r)
            | StmtForIn (v, r, body) -> StmtForIn (v, inlinePrefixAliases r, body |> List.map mapStmt)
        let rec unwrap (s: Stmt) = match s with StmtSpanned (i, _) -> unwrap i | _ -> s
        Blade.Unfold.mapExprPre (fun x ->
            match x.Kind with
            // An inner binder of the prefix name shadows it: that scope
            // is left alone.
            | ExprKind.ExprLambda (ps, _, _) when ps |> List.exists (fun p -> p.Name = def.PrefixVar) -> Some x
            | ExprKind.ExprLet (b, body) when isPrefixAlias b ->
                let sub = Blade.Unfold.substFree (Map.ofList [ aliasName b, synAt (ExprVar def.PrefixVar) ])
                Some (inlinePrefixAliases (sub body))
            | ExprKind.ExprBlock (stmts, fin) ->
                // Statements bind sequentially: an alias let is dropped
                // and its name substituted through the REST of the block
                // (substFree threads statement binders the same way).
                let rec go (acc: Stmt list) (rest: Stmt list) (fin: Expr option) =
                    match rest with
                    | [] -> (List.rev acc, fin)
                    | s :: tail ->
                        match unwrap s with
                        | StmtLet b when isPrefixAlias b ->
                            let sub = Blade.Unfold.substFree (Map.ofList [ aliasName b, synAt (ExprVar def.PrefixVar) ])
                            (match (sub (synAt (ExprBlock (tail, fin)))).Kind with
                             | ExprKind.ExprBlock (tail', fin') -> go acc tail' fin'
                             | _ -> go acc tail fin)
                        | _ -> go (s :: acc) tail fin
                let stmts', fin' = go [] stmts fin
                Some { x with Kind = ExprBlock (stmts' |> List.map mapStmt, fin' |> Option.map inlinePrefixAliases) }
            | _ -> None) e
    // Inductive arm: for n in start..N, prefix reads become buffer reads
    // (partials via hoisted row-view lets, scalars in place).
    let sliceHoisted = rewritePrefixReads (inlinePrefixAliases def.SliceExpr)
    let guardHoisted = def.Guard |> Option.map (inlinePrefixAliases >> rewritePrefixReads)
    let substBuf = Blade.Unfold.substFree (Map.ofList [def.PrefixVar, bufVar])
    let slice' = substBuf sliceHoisted
    let guard' = guardHoisted |> Option.map substBuf
    let lagStmts =
        hoisted |> Seq.map (fun (_, nm, rd) ->
            StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar nm); Type = None; Value = rd })
        |> List.ofSeq
    // `while` guard scaffolding (plan-match-statements.md 3). Everything
    // surface-expressible is desugared HERE; the break itself and the
    // budget abort are TExpr-only nodes injected into the checked block
    // below. Loop-body order: lag lets -> guard value -> stop-ordinal
    // record -> [injected break] -> slice write.
    let grdName = $"__grd_{def.Name}"
    let stopName = $"__stop_{def.Name}"
    let guardStmts =
        match guard' with
        | None -> []
        | Some g ->
            [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar grdName); Type = None; Value = g }
              // The stopping ordinal: n on the breaking iteration, the
              // "never stopped" sentinel N re-written on every other.
              // (`Int64(n)`: the step ordinal is an index value of the
              // recursion axis when that axis is named.)
              StmtExpr (synAt (ExprAssign (synAt (ExprVar stopName),
                          synAt (ExprIf (synAt (ExprVar grdName), iLit n,
                                         synAt (ExprUnaryOp (OpCast "Int64", stepVarE))))))) ]
    // A loop lower bound: typed by the step axis when that axis is named,
    // so the step variable is a position of it.
    let stepBound (v: int64) =
        match at.IndexTypes.Head, annotSlots with
        | { Tag = Some tag; IxKind = IxKPlain; Symmetry = SymNone; Rank = 1 }, Some (slot0 :: _)
                when not (tag.StartsWith "__") ->
            synAt (ExprTyped (iLit v, slot0))
        | _ -> iLit v
    // PRINT REPLAY (module scope, recArrayModuleDecision): after each
    // completed row, a marker `let __replay_row_<x> = ring(slot)` that
    // Lowering turns into the effectful IRReplayRow naming x.
    let rowMarker (slot: Expr) : Stmt list =
        if not replayRows then []
        else
            [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar (Blade.IR.recReplayRowPrefix + def.Name))
                        Type = None; Value = synAt (ExprApp (bufVar, [ slot ])) } ]
    let loopBody =
        lagStmts @ guardStmts @ sliceCopyStmts (ringIdx (synAt (ExprVar def.StepVar))) $"__slice_{def.Name}" slice'
        @ rowMarker (ringIdx (synAt (ExprVar def.StepVar)))
    let stepRange = synAt (ExprKind.ExprDotDot (stepBound loopStart, iLit n))
    let loop = StmtForIn (def.StepVar, stepRange, loopBody)
    // SNAPSHOTS (windowed). One per row the rest of the block reads, typed
    // from the annotation's TRAILING slots -- exactly the type `NAME(p)`
    // had -- so the rewritten tail checks identically, tags and units
    // preserved. Assignable: the sweep copies into it.
    let snapTrailingSlots =
        annotSlots |> Option.bind (fun s -> if s.Length > 1 then Some (List.tail s) else None)
    let snapStmts =
        snaps |> List.map (fun p ->
            let tyOpt =
                match annot with
                | TyArray (elemT, _) ->
                    Some (match snapTrailingSlots with
                          | Some slots -> TyArray (elemT, slots)
                          | None -> elemT)
                | _ -> None
            StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar (recSnapName def.Name p))
                      Type = tyOpt; Value = zerosOver "sn" snapTrailingSlots trailing })
    // Copy ring slot `p mod (K+1)` out to snapshot p, at the one moment
    // that slot holds row p: written at step p, not overwritten before
    // step p + K + 1.
    let snapCopyStmts (p: int64) : Stmt list =
        let slot = if ringSize = 1L then 0L else p % ringSize
        let snapVar = synAt (ExprVar (recSnapName def.Name p))
        match trailing with
        | [] -> [ StmtExpr (synAt (ExprAssign (snapVar, synAt (ExprApp (bufVar, [iLit slot]))))) ]
        | _ ->
            let ivars = trailing |> List.mapi (fun k _ -> $"__pi{k}_{p}_{def.Name}")
            let idxEs = ivars |> List.map (fun v -> synAt (ExprVar v))
            let assign =
                StmtExpr (synAt (ExprAssign (
                    synAt (ExprApp (snapVar, idxEs)),
                    synAt (ExprApp (bufVar, iLit slot :: idxEs)))))
            List.foldBack2 (fun ivar ext inner ->
                [ StmtForIn (ivar, synAt (ExprKind.ExprDotDot (iLit 0L, iLit ext)), inner) ])
                ivars trailing [assign]
    // THE SWEEP. Full: one loop. Windowed: the same loop SPLIT after each
    // snapshot row, the copy between the segments -- an unconditional copy
    // at exactly the right moment, no per-step branch, no repeated work.
    // A row the sweep never reaches (row 0 under a seed arm) is copied
    // before it, straight off the seeded slot.
    let sweepStmts =
        match window with
        | None -> [ loop ]
        | Some _ ->
            let acc = ResizeArray<Stmt>()
            for p in snaps |> List.filter (fun p -> p < loopStart) do acc.AddRange (snapCopyStmts p)
            let boundaries = snaps |> List.filter (fun p -> p >= loopStart) |> List.map (fun p -> p + 1L) |> List.distinct |> List.sort
            let mutable cur = loopStart
            for b in boundaries do
                if b > cur then
                    acc.Add (StmtForIn (def.StepVar, synAt (ExprKind.ExprDotDot (stepBound cur, iLit b)), loopBody))
                    cur <- b
                for p in snaps |> List.filter (fun p -> p + 1L = b) do acc.AddRange (snapCopyStmts p)
            if cur < n then
                acc.Add (StmtForIn (def.StepVar, synAt (ExprKind.ExprDotDot (stepBound cur, iLit n)), loopBody))
            List.ofSeq acc
    // Zero slices consumed by out-of-prefix lag reads. Annotated from the
    // declared slot list so their index tags match the row views they
    // alternate with.
    let zeroSliceStmts =
        zeroSlices
        |> Seq.map (fun (dropped, nm) ->
            let slotsOpt =
                annotSlots |> Option.bind (fun s ->
                    if s.Length > 1 + dropped then Some (List.skip (1 + dropped) s) else None)
            let tyOpt =
                match annot, slotsOpt with
                | TyArray (elemT, _), Some slots -> Some (TyArray (elemT, slots))
                | _ -> None
            StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar nm); Type = tyOpt
                      Value = zerosOver "zs" slotsOpt (List.skip dropped trailing) })
        |> List.ofSeq
    // Guard prologue (the mut stop ordinal, sentinel N = "never stopped")
    // and epilogue (the FREEZE loop: the last written slice repeats over
    // [stop, N)). Freeze source row = max(stop - 1, 0): at stop = 0
    // nothing was ever written, the buffer is zero everywhere, and the
    // copy is zero-onto-zero -- still correct, still in bounds.
    let guardPre, guardPost =
        match guard' with
        | None -> [], []
        | Some _ ->
            let stopE = synAt (ExprVar stopName)
            let frzName = $"__frz_{def.Name}"
            let fkName = $"__fk_{def.Name}"
            let pre =
                [ StmtLet { Mutability = BindMut; Pattern = mkPat span (PatVar stopName); Type = None; Value = iLit n } ]
            let post =
                [ StmtLet { Mutability = BindLet; Pattern = mkPat span (PatVar frzName); Type = None
                            Value = synAt (ExprIf (synAt (ExprBinOp (Elementwise, OpGt, stopE, iLit 0L)),
                                                   synAt (ExprBinOp (Elementwise, OpSub, stopE, iLit 1L)),
                                                   iLit 0L)) }
                  StmtForIn (fkName,
                             synAt (ExprKind.ExprDotDot (stopE, iLit n)),
                             sliceCopyStmts (synAt (ExprVar fkName)) $"__frzslice_{def.Name}"
                                            (synAt (ExprApp (bufVar, [synAt (ExprVar frzName)])))) ]
            pre, post
    match window with
    | Some _ when guard'.IsSome || guardIsBestEffort ->
        // Unreachable: recArrayWindowDecision demotes a `while` guard and
        // the freeze-idiom shape (its epilogue repeats the last row).
        Error (InternalErr $"recursive array '{def.Name}': internal error -- a guarded recursion reached the windowed desugar")
    | Some _ ->
        let seedMarker = if loopStart > 0L then rowMarker (iLit 0L) else []
        Ok (RecWindowStmts ((bufLet :: zeroSliceStmts) @ snapStmts @ seedStmts @ seedMarker @ sweepStmts))
    | None ->
    let block = synAt (ExprBlock ((bufLet :: zeroSliceStmts) @ seedStmts @ guardPre @ [loop] @ guardPost, Some bufVar))
    // Post-check injection for the `while` guard: the break (TExprBreakIf,
    // not surface-expressible) after the stop-ordinal record inside the
    // recursion loop, and the budget abort (BL8010) after the freeze
    // epilogue. Both reference bindings by the IRIds the check just
    // minted, so this must run on the TYPED tree.
    let injectGuardControl (tv: TypedExpr) : TypeResult<TypedExpr> =
        match tv.Kind with
        | TExprBlock (tstmts, fin) ->
            let stopId =
                tstmts |> List.tryPick (fun st ->
                    match st with
                    | TStmtLet tb when tb.Name = stopName -> Some tb.VarId
                    | _ -> None)
            let grdBinding =
                tstmts |> List.tryPick (fun st ->
                    match st with
                    | TStmtForIn (vn, _, _, _, body) when vn = def.StepVar ->
                        body |> List.tryPick (fun bs ->
                            match bs with
                            | TStmtLet tb when tb.Name = grdName -> Some tb
                            | _ -> None)
                    | _ -> None)
            match stopId, grdBinding with
            | Some stopId, Some grdB ->
                // A guard is a predicate: it must be Bool, not merely
                // truthy (mirrors the match-arm guard rule).
                unify env.Subst grdB.Type (IRTScalar ETBool)
                |> Result.mapError (fun _ ->
                    RecArrayFormErr $"recursive array '{def.Name}': a `while` guard must be Bool -- the condition after `while` is a predicate (comparisons and boolean operators), not a value")
                |> Result.bind (fun () ->
                    let boolT = IRTScalar ETBool
                    let grdVarT = mkTypedSpan (TExprVar (grdName, grdB.VarId, None)) boolT span
                    let breakT =
                        TStmtExpr (mkTypedSpan (TExprBreakIf (mkTypedSpan (TExprUnaryOp (OpNot, grdVarT)) boolT span)) IRTUnit span)
                    // Insert the break AFTER the guard let and the
                    // stop-ordinal assignment that immediately follows it.
                    // A MISS must not be silent: without the break the
                    // guard would be computed, the loop would run the full
                    // budget, and the stop ordinal would end up recording
                    // the last iteration rather than the stopping one --
                    // a wrong answer with no diagnostic.
                    let mutable inserted = false
                    let tstmts' =
                        tstmts |> List.map (fun st ->
                            match st with
                            | TStmtForIn (vn, vid, lo, hi, body) when vn = def.StepVar ->
                                let rec ins acc rest =
                                    match rest with
                                    | (TStmtLet (tb: TypedBinding)) :: assignStmt :: tail when tb.Name = grdName ->
                                        inserted <- true
                                        List.rev acc @ [TStmtLet tb; assignStmt; breakT] @ tail
                                    | s :: tail -> ins (s :: acc) tail
                                    | [] -> List.rev acc
                                TStmtForIn (vn, vid, lo, hi, ins [] body)
                            | st -> st)
                    if not inserted then
                        Error (InternalErr $"recursive array '{def.Name}': internal error -- the while-guard early exit could not be placed in the recursion loop")
                    else
                    // The budget abort is the `while` spelling's CONTRACT.
                    // A recognized freeze idiom gets the same break and
                    // freeze but keeps if/else's best-effort meaning:
                    // running out of budget is not an error there --
                    // aborting would change what the user's program
                    // means, which an optimization may never do.
                    if guardIsBestEffort then
                        Ok { tv with Kind = TExprBlock (tstmts', fin) }
                    else
                    let stopVarT = mkTypedSpan (TExprVar (stopName, stopId, None)) (IRTScalar ETInt64) span
                    let budgetT = mkTypedSpan (TExprLit (LitInt n)) (IRTScalar ETInt64) span
                    let convT = mkTypedSpan (TExprBinOp (Elementwise, OpNeq, stopVarT, budgetT)) boolT span
                    let abortMsg = $"recursive array '{def.Name}': the while guard is still true after the full budget of {n} steps -- the recurrence did not converge within its declared extent"
                    let abortT = TStmtExpr (mkTypedSpan (TExprConstraintCheck (convT, "BL8010", abortMsg)) IRTUnit span)
                    Ok { tv with Kind = TExprBlock (tstmts' @ [abortT], fin) })
            | _ ->
                Error (InternalErr $"recursive array '{def.Name}': internal error -- while-guard scaffolding missing from the checked block")
        | _ -> Error (InternalErr $"recursive array '{def.Name}': internal error -- checked recursive-array value is not a block")
    checkExpr env annotTy block |> Result.bind (fun tv ->
        match guard' with
        | None -> Ok (RecFullTyped { tv with Type = annotTy })
        | Some _ -> injectGuardControl tv |> Result.map (fun tv2 -> RecFullTyped { tv2 with Type = annotTy })))
