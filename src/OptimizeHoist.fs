// Kernel-invariant hoisting, rule invariant-hoist (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeHoist

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis

// --- Kernel-invariant hoisting -----------------------------------------------
//
// TypeCheck desugars an array/scalar elementwise op by EMBEDDING the scalar
// operand's surface expression in the kernel (`a - mean(a)` is
// `method_for(a) <@> lambda(__bx) -> __bx - mean(a) |> compute`; the embedding
// is what lets capture analysis see the operand's names). The scalar side is
// therefore evaluated once PER CELL: centering a length-T row with the
// one-liner folded `a` T times -- O(T^2) -- and `x / sqrt(mean(x * x))` also
// allocated `x * x` per cell, while the block spelling that binds the mean
// with a `let` first is linear. Same computation, two spellings, a factor of
// T apart: a SAME-EMIT violation, and in the spelling the tutorials teach.
// A hand-written `method_for(a) <@> lambda(e) -> e - mean(a)` has the same
// shape and the same cost.
//
// This pass moves the invariant out. For a FORCED apply (`IRCompute
// (IRApplyCombinator ..)`, or the single apply a fused `reduce` folds) whose
// kernel is a lifted lambda, every maximal subexpression S of the kernel
// body that
//
//   * is a rank-0 numeric value reading NO kernel parameter and no name
//     bound inside the body -- every variable it names is one of the
//     kernel's captures, or a callable whose own captures are (so S means
//     the same thing at the apply's site as it did in the kernel), and none
//     is a DEFERRED let, whose producer runs where it is read;
//   * does real work: a call, an array traversal or a math intrinsic
//     (scalar arithmetic over captured scalars is left for the C++ compiler,
//     which already hoists it);
//   * is REPEATABLE and writes nothing (the CSE judge's facts, callees
//     resolved through the WHOLE PROGRAM's function table -- `mean` lives in
//     the stdlib's module), in a kernel that itself writes nothing S could
//     read between two cells;
//   * is evaluated UNCONDITIONALLY by the kernel (a strict position: not
//     under a branch, a match arm or the right operand of `&&` / `||`),
//
// is bound once by a `let` wrapped around the apply, and the kernel -- a
// clone, the original is left for any other reference -- reads that let as
// one more capture. IRLift, which runs next, already drains such a chain to
// the enclosing statement position (it is the shape the post-monomorphization
// broadcast in IRMono.lowerArrayBinOpsModule has always produced), so codegen
// and the interpreter see exactly what the block spelling lowers to.
//
// COST ONLY (charter rule 1). S ran at cell 0 of every non-empty nest and at
// no cell of an empty one; after the rewrite it runs once before the nest:
//
//   * an S that cannot abort is bound unconditionally -- evaluating a pure
//     total value an extra time (the empty nest) is unobservable;
//   * an S that may abort (`mean` of an empty row is BL8003) is bound under
//     the nest's own non-emptiness -- `if <every operand has a cell> then S
//     else zero` -- so an empty nest still evaluates nothing and raises
//     nothing. The guard is spelled only where it is exact: plain dense
//     operands that are literal-extent, or named values whose extents can be
//     read (parameters, forced or literal lets; an operand that is itself a
//     forced map or a call is bound to a name first, ahead of S). Anything
//     else (packed, compound, sparse, ragged, a deferred operand) leaves S
//     in the kernel;
//   * such an S also needs the kernel's remainder to be repeatable, so no
//     output the kernel would have emitted at cell 0 can end up after an
//     abort it used to precede.
//
// THE FIRST FAILURE (gap G152; it used to be an accepted residual): an S
// that may abort moves only when nothing evaluated before it at cell 0 --
// an earlier operand of the kernel, the nest's own co-iteration extent check
// -- may fail first, so the program aborts with the SAME code at the same
// site. Otherwise S stays in the kernel (the record says why).
//
// THE FRAMES AND THE LOCATION (gap G151): S fails with the frames and the
// declaration its per-cell evaluation had. The compiled lanes inline an
// expression-shaped kernel (no frame) and call any other (its frame); the
// interpreter inlines every one. The helper carries that answer
// (IRCallable.HoistFrame), and every minted callable is located as the code
// it replaces (HoistDeclTable, copied by Lowering.copyHoistDeclSpans).
//
// FLOATING POINT. A hoisted value is the same double the kernel computed, but
// g++'s default `-ffp-contract=fast` fuses a multiply into an adjacent add,
// and binding a product to a name ends that adjacency. So S is never ROOTED
// at a multiply (or `^`, or the negation of one): `e - mean(a) * 2.0` hoists
// `mean(a)` and keeps the product in the kernel, beside the add it feeds --
// the same IR the block spelling `let m = mean(a); e - m * 2.0` gives. (What
// g++ then does with an invariant product is g++'s own choice, as it is for
// the block spelling; `blade test opt-diff` compares the printed values.)
// Everything inside S keeps its own shape.
//
// An S with no expression rendering (it contains a forced map, a fold --
// anything IRLift would bind to a statement) cannot sit in the guard's arm,
// where statement-shaped values are refused; it becomes the body of a
// nullary helper lambda capturing what it reads, and the arm is a call.
//
// Runs AFTER fusion: a fused kernel carries every inner kernel's invariants
// and gets one nest, whereas a hoist first would hide the inner maps behind
// lets fusion does not look through.

/// What it takes to know one apply's iteration space is non-empty: the
/// (operand, axis) extents only run time knows -- none when every extent is a
/// positive literal -- and the operands that have to be bound to a name
/// before their extent can be read.
type private NonEmptyPlan =
    { Checks: (int * int) list
      Unnamed: int list }

/// Where a callable the hoist MINTS is located (gap G151). A runtime guard
/// with no span of its own (an empty fold's BL8003, a key check) is located at
/// the declaration being emitted (CodeGenState.declPanicArgs); a minted
/// callable has no recorded span, so its guards printed no `-->` where the
/// per-cell evaluation printed one. The REWRITTEN kernel, and a helper whose
/// kernel the compiled lanes call per cell, are located as the kernel; a helper
/// whose kernel is inlined, as the declaration the nest stands in.
type HoistDeclSource =
    | LocatedAsCallable of IRId
    | LocatedAsBinding of IRId

/// (minted callable id, where it is located), in mint order. Lowering copies
/// each source's span to the minted callable's FINAL name after module
/// qualification (`Lowering.copyHoistDeclSpans`, the repro-twin pattern).
module HoistDeclTable =
    open System.Threading
    let private cell = new AsyncLocal<(IRId * HoistDeclSource) list>()
    let private get () = match box cell.Value with null -> [] | _ -> cell.Value
    let reset () = cell.Value <- []
    let record (minted: IRId) (src: HoistDeclSource) = cell.Value <- (minted, src) :: get ()
    let all () : (IRId * HoistDeclSource) list = List.rev (get ())

let hoistKernelInvariantsModule (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    let enabled = hoistEnabled ()
    // The declaration the code being rewritten stands in (see HoistDeclSource).
    let owner : HoistDeclSource option ref = ref None
    let withOwner (o: HoistDeclSource option) (f: unit -> 'a) : 'a =
        let saved = owner.Value
        owner.Value <- o
        try f () finally owner.Value <- saved
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    // Callee resolution is whole-program (ids are program-global); this
    // module's own callables, as the earlier passes left them, win.
    let funcs =
        programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let facts = cseFactsOf funcs
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for KeyValue (id, f) in funcs do callables.[id] <- f
    let mayAbort (e: IRExpr) : bool = tileMayAbort callables Set.empty e

    // Names whose extents can be READ where an apply stands: parameters (a
    // caller hands over a value, never a deferred form) and lets / bindings
    // of a forced or literal array. A deferred let declares no storage until
    // its consumer forces it, so a guard may not ask for its extent.
    let materialized = System.Collections.Generic.HashSet<IRId>()
    // Names bound to a DEFERRED form (a bare apply, a loop object, a join
    // tree): their producer runs where they are read. A value that reads one
    // is left in the kernel -- moving the read would move the producer.
    let deferred = System.Collections.Generic.HashSet<IRId>()
    let isDeferredForm (v: IRExpr) : bool =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _
        | IRFusion _ | IRParallel _ | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _
        | IRReplicate _ | IRFunctorMap _ | IRBind _ | IRPure _ | IRZip _ | IRArrayProduct _
        | IRComposeObj _ | IRComposeMeth _ | IRCompose _ | IRReynolds _ -> true
        | _ -> false
    let note (id: IRId) (v: IRExpr) =
        match v with
        | IRCompute _ | IRArrayLit _ -> materialized.Add id |> ignore
        | v when isDeferredForm v -> deferred.Add id |> ignore
        | _ -> ()
    let noteLets (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, v, _) -> note id v
            | _ -> ()) e
    for f in modul.Functions do
        for p in f.Params do materialized.Add p.VarId |> ignore
        noteLets f.Body
    for b in modul.Bindings do
        note b.Id b.Value
        noteLets b.Value

    // None: not expressible at this seam (packed / compound / sparse / ragged
    // storage, a virtual or deferred operand), or statically empty. An
    // operand that is a forced map or a call -- a fresh array IRLift binds to
    // a `let` in front of the nest anyway -- can be bound HERE instead, ahead
    // of the hoisted value, so the guard reads its extent by name (and the
    // operand is still evaluated before the value, as it was before the
    // kernel's first cell).
    let nonEmptyOf (info: ApplyInfo) : NonEmptyPlan option =
        if info.Arrays.IsEmpty || info.Arrays.Length <> info.ArrayTypes.Length then None else
        let mutable unknown = false
        let checks = ResizeArray<int * int>()
        let unnamed = ResizeArray<int>()
        List.zip info.Arrays info.ArrayTypes
        |> List.iteri (fun i (a, at) ->
            if at.IsVirtual
               || not (at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1)) then
                unknown <- true
            else
                at.IndexTypes |> List.iteri (fun d ix ->
                    match ix.Extent, a with
                    | IRLit (IRLitInt n), _ -> if n <= 0L then unknown <- true
                    | _, IRVar (id, _) when materialized.Contains id -> checks.Add((i, d))
                    | _, IRCompute _ ->
                        checks.Add((i, d))
                        if not (unnamed.Contains i) then unnamed.Add i
                    | _, CallOfNamed (fid, _, _, _) when Map.containsKey fid funcs ->
                        checks.Add((i, d))
                        if not (unnamed.Contains i) then unnamed.Add i
                    | _ -> unknown <- true))
        if unknown then None else Some { Checks = List.ofSeq checks; Unnamed = List.ofSeq unnamed }

    let isNumericScalar (t: IRType) : bool =
        match t with
        | AnyPrimElem (ETInt32 | ETInt64 | ETFloat32 | ETFloat64 | ETBool | ETComplex64 | ETComplex128) -> true
        | _ -> false
    // The guard's other arm: never read (the nest is empty), but typed like S.
    let zeroOf (t: IRType) : IRExpr option =
        match t with
        | AnyPrimElem ETInt32 ->
            Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt 0L)))
        | _ -> zeroLiteralOf t
    // A call, an array traversal, or a libm intrinsic (g++ does not move a
    // call that may set errno out of a loop). Plain arithmetic is not.
    let hasWork (e: IRExpr) : bool =
        let mutable w = false
        iterIRExpr (fun n ->
            match n with
            | IRApp _ | IRReduce _ | IRReduceCompute _ | IRProdSum _ | IRCompute _ | IRContains _ -> w <- true
            | IRUnaryOp (IRMath _, _) | IRBinOp (_, IRMath2 _, _, _, _) -> w <- true
            | _ -> ()) e
        w
    let rec mulRooted (e: IRExpr) : bool =
        match e with
        | IRBinOp (_, (IRMul | IRCaret), _, _, _) -> true
        | IRUnaryOp (IRNeg, x) -> mulRooted x
        | _ -> false
    // Does `e` mean the same thing outside the kernel? Every name is a
    // capture, or a callable that itself closes over captures only; no
    // binder inside (ids bound in the kernel body are not captures, so a
    // read of one already fails the first test -- refusing the binders too
    // keeps the judgment first-order).
    let closedOver (captureIds: System.Collections.Generic.HashSet<IRId>) (e: IRExpr) : bool =
        let mutable ok = true
        iterIRExpr (fun n ->
            if ok then
                match n with
                | IRVar (id, (IRTLoop _ | IRTComputation _)) when captureIds.Contains id -> ok <- false
                | IRVar (id, _) when captureIds.Contains id -> if deferred.Contains id then ok <- false
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some g -> if not (g.Captures |> List.forall (fun c -> captureIds.Contains c.Id)) then ok <- false
                     | None -> ok <- false)
                | IRParam _ | IRNth | IRArity _ | IRPolyIndex _ | IRPolyTail _ | IRZero _ | IRUnseededSeed _ | IRUninit _
                | IRLet _ | IRMatch _ | IRForRange _ | IRAssign _ | IRBreakIf _ | IRReplayRow _ | IRConstraintCheck _ -> ok <- false
                | _ -> ()) e
        // G209: an expression a TYPE carries -- a range bound `0..g(v)` --
        // runs too: a kernel parameter read there makes `e` vary per cell
        // (it was hoisted, leaving the parameter free in the hoisted copy)
        ok && (embeddedExtentExprs e |> List.forall (fun x ->
            Set.isEmpty (Set.difference (collectVarRefsIR x) (Set.ofSeq captureIds))
            || (collectVarRefsIR x |> Set.forall (fun id -> captureIds.Contains id || Map.containsKey id funcs))))
    // G97 (round 4 wave 4): a direct call of a module / program function that
    // returns a plain dense array (`prep(A)`) -- the one array-valued form the
    // pass hoists (its arguments' invariance is `closedOver`'s judgment).
    let isInvariantArrayCall (e: IRExpr) : bool =
        match e with
        | CallOfNamed (fid, _, _, retTy) when Map.containsKey fid funcs ->
            (match stripUnits retTy with
             | ArrayElem at ->
                not at.IsVirtual
                && at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
                && (match stripUnits at.ElemType with IRTScalar _ -> true | _ -> false)
             | _ -> false)
        | _ -> false
    // Renders as ONE C++ expression (so it can stand in the guard's arm).
    let rec inlineSafe (e: IRExpr) : bool =
        match e with
        | IRLit _ | IRVar _ -> true
        | IRBinOp (_, _, l, r, _) -> inlineSafe l && inlineSafe r
        | IRUnaryOp (_, x) -> inlineSafe x
        | IRFma (a, b, c) -> inlineSafe a && inlineSafe b && inlineSafe c
        | IRComplex (re, im) -> inlineSafe re && inlineSafe im
        | CallOfNamed (fid, _, args, _) when Map.containsKey fid funcs -> args |> List.forall inlineSafe
        | IRExtent (IRVar _, _) -> true
        | _ -> false

    // A hoisted value's type is what the C++ it renders to yields, i.e. the
    // checker's rule (exprTypeIfKnown), not typeOf's IR-level one where they
    // part: `abs` of an Int64 stays Int64 (`std::abs`), which typeOf calls
    // Float64 -- and a `double` let fed back into an Int64 kernel was a g++
    // float-conversion rejection.
    let valueType (e: IRExpr) : IRType =
        match exprTypeIfKnown e with
        | Some t -> t
        | None -> typeOf e

    let show (k: IRCallable) (e: IRExpr) : string =
        let nameOf (id: IRId) =
            match k.Captures |> List.tryFind (fun c -> c.Id = id) with
            | Some c -> c.Name
            | None ->
                match Map.tryFind id funcs with
                // The callee as the program wrote it, not its emitted
                // specialization (`mean`, not `mean_HM_<id>_double`).
                | Some f -> f.SourceName
                | None -> $"__v{id}"
        let rec go (depth: int) (e: IRExpr) : string =
            if depth > 3 then ".." else
            match e with
            | IRVar (id, _) -> nameOf id
            | IRApp (f, args, _) ->
                let shown = args |> List.map (go (depth + 1)) |> String.concat ", "
                $"{go (depth + 1) f}({shown})"
            | IRUnaryOp (IRMath m, x) -> $"{m}({go (depth + 1) x})"
            | IRReduce (a, _, _) -> $"reduce({go (depth + 1) a}, ..)"
            | IRProdSum args ->
                let shown = args |> List.map (go (depth + 1)) |> String.concat ", "
                $"prodsum({shown})"
            | IRCompute _ -> "<elementwise map>"
            | _ -> ".."
        go 0 e

    // Minted callables, by the kernel they derive from: each is placed right
    // after that kernel and recorded in DerivedFuncOrigins, so it is emitted
    // at the kernel's program point (the IRModule.DerivedFuncOrigins
    // contract -- a fresh id alone would sort it after every use).
    let minted = System.Collections.Generic.Dictionary<IRId, ResizeArray<IRCallable>>()
    let mint (origin: IRId) (c: IRCallable) =
        match minted.TryGetValue origin with
        | true, l -> l.Add c
        | _ ->
            let l = ResizeArray<IRCallable>()
            l.Add c
            minted.[origin] <- l
    let hoistedIds = System.Collections.Generic.HashSet<IRId>()
    let decided = System.Collections.Generic.HashSet<IRId>()
    // G97: true while rewriting a NAMED function body (see `admit`)
    let arrayHoistScope = ref false

    let rec rw (e: IRExpr) : IRExpr =
        match e with
        // A conditional arm is not a drain point: IRLift leaves an arm's
        // lets inside the arm, where statement-shaped values are refused.
        // Nothing is hoisted there (the apply keeps today's shape).
        | IRIf (c, t, f) -> IRIf (rw c, t, f)
        | IRMatch (scrut, cases) -> IRMatch (rw scrut, cases)
        | IRBinOp (m, (IRAnd | IROr as op), l, r, loc) -> IRBinOp (m, op, rw l, r, loc)
        | IRCompute (IRApplyCombinator info) ->
            let info1 = rwOperands info
            (match planFor info1 with
             | Some (lets, info2) ->
                List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) lets (IRCompute (IRApplyCombinator info2))
             | None -> IRCompute (IRApplyCombinator info1))
        // The fused reduction terminal over ONE apply forces it just the
        // same: one nest, the kernel once per cell, nothing when empty (the
        // fold answers its seed). A join tree of several applies is left.
        | IRReduceCompute (IRApplyCombinator info, kernel, init) ->
            let info1 = rwOperands info
            let kernel' = rw kernel
            let init' = rw init
            (match planFor info1 with
             | Some (lets, info2) ->
                List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) lets
                    (IRReduceCompute (IRApplyCombinator info2, kernel', init'))
             | None -> IRReduceCompute (IRApplyCombinator info1, kernel', init'))
        // A DEFERRED apply runs its kernel where it is read, not here:
        // nothing is hoisted out of it, only its operands are rewritten.
        | IRApplyCombinator info -> IRApplyCombinator (rwOperands info)
        | IRLet (id, v, body) ->
            // A hoist out of a let's VALUE goes in front of the let: the
            // straight-line shape the block spelling has.
            let rec peel acc v =
                match v with
                | IRLet (hid, hv, rest) when hoistedIds.Contains hid -> peel ((hid, hv) :: acc) rest
                | _ -> (List.rev acc, v)
            let (hs, inner) = peel [] (rw v)
            List.foldBack (fun (hid, hv) acc -> IRLet (hid, hv, acc)) hs (IRLet (id, inner, rw body))
        | _ -> rwChildren e
    and rwChildren (e: IRExpr) : IRExpr =
        match e with
        | ExprShape ([], _) -> e
        | ExprShape (children, rebuild) -> rebuild (children |> List.map rw)
    // The Loop slot is PROVENANCE -- a second copy of the operand tree (with
    // its own lifted kernels) that no emitter runs. Rewriting it would mint
    // a let and a kernel per copy that nothing reads; it is left as written.
    and rwOperands (info: ApplyInfo) : ApplyInfo =
        { info with Arrays = info.Arrays |> List.map rw }
    and planFor (info: ApplyInfo) : ((IRId * IRExpr) list * ApplyInfo) option =
        match info.Kernel with
        | IRVar (kid, kty) ->
            (match Map.tryFind kid own with
             | Some k when k.Name.StartsWith "__lambda_" && not k.IsStatic && not k.IsArityPoly
                           && not k.IsCudaKernel && not k.IsMpiParallel ->
                let kf = facts k.Body
                if kf.MayWrite then None else
                let captureIds = System.Collections.Generic.HashSet<IRId>(k.Captures |> List.map (fun c -> c.Id))
                let nonEmpty = lazy (nonEmptyOf info)
                let kernelAssigns =
                    let mutable hit = false
                    iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ -> hit <- true | _ -> ()) k.Body
                    hit
                let admit (e: IRExpr) : bool =
                    if mulRooted e || not (hasWork e) || not (closedOver captureIds e) then false else
                    let t = valueType e
                    if isInvariantArrayCall e then
                        // (a kernel that assigns anything -- a `let mut` copy of the
                        // value written per cell among them -- keeps the call: the
                        // hoisted pool is shared by every cell and thread)
                        if kernelAssigns then false
                        // (Codex review of the C7 diff): only inside a NAMED FUNCTION
                        // body, whose scope frees the hoisted pool at its exit (a
                        // top-level host would keep it to program end, a kernel body
                        // is another lifetime regime), and only over a STATICALLY
                        // non-empty nest -- every extent a positive literal -- so the
                        // call ran at cell 0 anyway: evaluating it once before the
                        // nest cannot add a failure or a non-terminating call
                        elif not arrayHoistScope.Value then false
                        elif (match nonEmpty.Value with Some plan -> not plan.Checks.IsEmpty | None -> true) then false
                        else
                        // G97: an ARRAY-valued call of invariant arguments
                        // (`prep(A)` inside `reduce(prep(A), (+), w)`): bound
                        // once before the nest (one evaluation, one pool,
                        // read-only in the kernel -- also in a thread team),
                        // instead of one call and one fresh pool per cell.
                        // It has no zero for the guard's other arm, so it
                        // must not abort: then evaluating it before an
                        // EMPTY nest is unobservable (pure, total).
                        let f = facts e
                        f.Repeatable && not f.MayWrite && not (mayAbort e)
                    elif not (isNumericScalar t) then false else
                    let f = facts e
                    if not f.Repeatable || f.MayWrite then false
                    elif not (mayAbort e) then true
                    else
                        kf.Repeatable && (zeroOf t).IsSome && nonEmpty.Value.IsSome
                // Maximal candidates in strict positions, in evaluation order.
                let found = ResizeArray<IRExpr>()
                // THE FAILURE ORDER (gap G152): a value that may abort moves
                // ahead of everything the kernel evaluates before it at cell 0,
                // and ahead of the nest's own co-iteration extent check. So it
                // moves only while NOTHING evaluated before it -- outside what
                // is itself moving, in the same order -- may fail: the first
                // failure the program reports, and its code, stay the
                // per-cell ones. `abortSeen` is that earlier failure point.
                let coIterationCheck =
                    (info.IsCoIteration || not info.SharedIndexTypes.IsEmpty) && info.Arrays.Length >= 2
                    && (let exts = info.ArrayTypes |> List.map (fun at -> at.IndexTypes |> List.map (fun ix -> ix.Extent))
                        not (exts |> List.forall (fun x ->
                                x = exts.Head && x |> List.forall (function IRLit (IRLitInt _) -> true | _ -> false))))
                // A plain zip (every operand of one rank, every axis shared):
                // the check is "each operand's extent on axis d is operand 0's"
                // (genApplyCombinator's guard, Interp/Loops.fs' twin), so the
                // hoisted value's guard can carry it -- S then runs only where
                // the check passes and a cell runs, which is where S ran.
                // Any other co-iteration keeps a value that may fail in the kernel.
                let coIterationGuardable =
                    coIterationCheck
                    && (let ranks = info.ArrayTypes |> List.map (fun at -> at.IndexTypes.Length)
                        ranks |> List.forall ((=) ranks.Head) && info.SharedIndexTypes.Length = ranks.Head)
                let abortSeen = ref (coIterationCheck && not coIterationGuardable)
                let keptForOrder = ResizeArray<IRExpr>()
                // `e` with every value already moving replaced by a name: what
                // still fails in it, in place, per cell.
                let maskFound (e: IRExpr) : IRExpr =
                    let rec go (x: IRExpr) =
                        if found.Contains x then IRVar (-1, typeOf x)
                        else
                            match x with
                            | ExprShape ([], _) -> x
                            | ExprShape (cs, rebuild) -> rebuild (cs |> List.map go)
                    go e
                let rec walk (e: IRExpr) =
                    if admit e then
                        if mayAbort e && abortSeen.Value then
                            (if not (keptForOrder.Contains e) then keptForOrder.Add e)
                        elif not (found.Contains e) then found.Add e
                    else
                        match e with
                        | IRBinOp (_, (IRAnd | IROr), l, r, _) ->
                            walk l
                            if mayAbort r then abortSeen.Value <- true
                        | IRIf (c, t, f) ->
                            walk c
                            if mayAbort t || mayAbort f then abortSeen.Value <- true
                        | IRMatch (scrut, _) ->
                            walk scrut
                            if mayAbort (maskFound e) then abortSeen.Value <- true
                        | IRBinOp _ | IRUnaryOp _ | IRFma _ | IRComplex _ | IRTuple _ | IRTupleProj _
                        | IRLet _ | IRApp _ | IRIndex _ | IRFieldAccess _ | IRStructLit _ ->
                            childrenOf e |> List.iter walk
                            if mayAbort (maskFound e) then abortSeen.Value <- true
                        // G97: a fold's OPERAND is evaluated unconditionally
                        // (before its first combine), so an invariant array
                        // call standing there is a strict position too.
                        | IRReduce (a, _, _) when isInvariantArrayCall a ->
                            walk a
                            if mayAbort (maskFound e) then abortSeen.Value <- true
                        | _ -> if mayAbort e then abortSeen.Value <- true
                walk k.Body
                let orderEvidence =
                    keptForOrder
                    |> Seq.map (fun s -> $"`{show k s}` may fail, and something evaluated before it may fail first (an earlier operand, or the nest's co-iteration extent check): left in the kernel, evaluated per cell, so the first failure is the per-cell one (G152)")
                    |> List.ofSeq
                if found.Count = 0 && keptForOrder.Count > 0 && decided.Add kid then
                    Blade.Effects.Decisions.record
                        { Blade.Effects.Rule = "invariant-hoist"; Version = 1
                          Span = Blade.Ast.noSpan; Subject = k.SourceName
                          Outcome = Blade.Effects.Declined "every invariant that may fail follows an earlier failure point"
                          Evidence = orderEvidence }
                if found.Count = 0 then None
                elif not enabled then
                    if decided.Add kid then
                        Blade.Effects.Decisions.record
                            { Blade.Effects.Rule = "invariant-hoist"; Version = 1
                              Span = Blade.Ast.noSpan; Subject = k.SourceName
                              Outcome = Blade.Effects.Declined "disabled by BLADE_HOIST"
                              Evidence = (found |> Seq.map (fun s -> $"`{show k s}` does not read the element: left in the kernel, evaluated per cell") |> List.ofSeq) @ orderEvidence }
                    None
                else
                    let table = System.Collections.Generic.Dictionary<IRExpr, IRExpr>(HashIdentity.Structural)
                    let lets = ResizeArray<IRId * IRExpr>()
                    let caps = ResizeArray<CaptureInfo>()
                    let evidence = ResizeArray<string>()
                    // The guard, when some value needs one and run time has
                    // to answer: its unnamed operands are bound first.
                    let operandLets, arrays, guard =
                        match nonEmpty.Value with
                        | Some plan when not plan.Checks.IsEmpty && (found |> Seq.exists mayAbort) ->
                            let named = ResizeArray<IRId * IRExpr>()
                            let arrays =
                                info.Arrays |> List.mapi (fun i a ->
                                    if List.contains i plan.Unnamed then
                                        let t = builder.FreshId()
                                        hoistedIds.Add t |> ignore
                                        materialized.Add t |> ignore
                                        named.Add((t, a))
                                        IRVar (t, typeOf a)
                                    else a)
                            // The plain zip's extent check, as the nest makes it
                            // (G152): a mismatch evaluates nothing here and
                            // fails at the nest, BL8011, as it did.
                            let extentOf (i: int) (d: int) =
                                match info.ArrayTypes.[i].IndexTypes.[d].Extent with
                                | IRLit (IRLitInt n) -> IRLit (IRLitInt n)
                                | _ -> IRExtent (arrays.[i], d)
                            let agree =
                                if not coIterationGuardable then []
                                else
                                    [ for i in 1 .. arrays.Length - 1 do
                                        for d in 0 .. info.ArrayTypes.[0].IndexTypes.Length - 1 do
                                            match extentOf 0 d, extentOf i d with
                                            | IRLit a, IRLit b when a = b -> ()
                                            | e0, ei -> yield IRBinOp (IRElementwise, IREq, ei, e0, SrcLoc.Nowhere) ]
                            let g =
                                agree
                                @ (plan.Checks
                                   |> List.map (fun (i, d) ->
                                      IRBinOp (IRElementwise, IRGt, IRExtent (arrays.[i], d), IRLit (IRLitInt 0L), SrcLoc.Nowhere)))
                                |> List.distinct
                                |> List.reduce (fun l r -> IRBinOp (IRElementwise, IRAnd, l, r, SrcLoc.Nowhere))
                            (List.ofSeq named, arrays, Some g)
                        | _ -> ([], info.Arrays, None)
                    // THE FRAME (gap G151): a value that may abort has to fail
                    // with the frames its per-cell evaluation had. The compiled
                    // lanes CALL a kernel whose body is not expression-shaped
                    // (pushing its frame on every cell) and inline any other;
                    // the interpreter inlines every one. Read on the ORIGINAL
                    // body -- the value moving out may be the very part that
                    // made it a call.
                    let compiledLanesCall = not (Blade.IRLift.kernelBodyIsExpressionShaped k.Body)
                    for s in found do
                        let sTy = valueType s
                        let sId = builder.FreshId()
                        hoistedIds.Add sId |> ignore
                        // What moves out is rewritten like any other code: it
                        // may hold applies with invariants of their own. Bound
                        // directly, it runs in the enclosing frame -- the
                        // per-cell frame only where the kernel is inlined, so
                        // a value that may abort out of a CALLED kernel goes
                        // through the helper, which carries the frame.
                        let direct =
                            if inlineSafe s && not (compiledLanesCall && mayAbort s) then rw s
                            else
                                // The helper is a nullary lambda CAPTURING
                                // what S reads -- directly, or through a
                                // callable it names -- under the kernel's
                                // own capture records: S is its body
                                // verbatim, a nested lambda still finds its
                                // captures by id, and the call site forwards
                                // them exactly as it does for the kernel (a
                                // grouped capture's side state included,
                                // which no ARGUMENT could carry). Captures,
                                // never parameters: a parameter sharing a
                                // module binding's id would make codegen
                                // read that binding as a parameter
                                // everywhere else it is captured.
                                let needed = System.Collections.Generic.HashSet<IRId>()
                                iterIRExpr (fun n ->
                                    match n with
                                    | IRVar (id, _) when captureIds.Contains id -> needed.Add id |> ignore
                                    | IRVar (id, _) ->
                                        (match Map.tryFind id funcs with
                                         | Some g -> for c in g.Captures do needed.Add c.Id |> ignore
                                         | None -> ())
                                    | _ -> ()) s
                                let used = k.Captures |> List.filter (fun c -> needed.Contains c.Id)
                                // The hoisted part of kernel `k`: it prints as `k`
                                // (guard locations included), and its frame is
                                // the per-cell evaluation's (HoistFrame).
                                // LOCATED where the per-cell evaluation is: in
                                // the kernel when the compiled lanes call it,
                                // else in the declaration the nest stands in;
                                // an apply inside S stood there too.
                                let at = if compiledLanesCall then Some (LocatedAsCallable kid) else owner.Value
                                let body = withOwner at (fun () -> rw s)
                                let helper = { mkLambdaCallable builder [] body sTy used false [] [] false false 256 false
                                                 with SourceName = k.SourceName
                                                      HoistFrame = Some compiledLanesCall }
                                at |> Option.iter (HoistDeclTable.record helper.Id)
                                mint kid helper
                                IRApp (IRVar (helper.Id, mkFuncArrow [] sTy), [], sTy)
                        let value, how =
                            match mayAbort s, guard with
                            | true, Some g -> IRIf (g, direct, (zeroOf sTy).Value), "before the nest, when the iteration space is non-empty"
                            | _ -> direct, "before the nest"
                        lets.Add((sId, value))
                        table.[s] <- IRVar (sId, sTy)
                        caps.Add { Id = sId; Name = $"__v{sId}"; Type = sTy; IsMutable = false }
                        evidence.Add $"`{show k s}` does not read the element: bound once as __v{sId}, {how}"
                    // Every occurrence reads the let -- also one in a
                    // conditional position, which can no longer fail anew.
                    let rec replaceAll (e: IRExpr) : IRExpr =
                        match table.TryGetValue e with
                        | true, v -> v
                        | _ ->
                            match e with
                            | ExprShape ([], _) -> e
                            | ExprShape (children, rebuild) -> rebuild (children |> List.map replaceAll)
                    let newId = builder.FreshId()
                    // The rewritten kernel is the kernel: located as it is
                    // (a guard in its body, called per cell, printed `k`'s
                    // declaration and must still), and so is an apply inside it.
                    HoistDeclTable.record newId (LocatedAsCallable kid)
                    mint kid
                        { k with
                            Id = newId
                            Name = $"__lambda_{newId}"
                            Body = withOwner (Some (LocatedAsCallable kid)) (fun () -> rw (replaceAll k.Body))
                            Captures = k.Captures @ List.ofSeq caps }
                    let kernel' = IRVar (newId, kty)
                    let loop' =
                        match info.Loop with
                        | IRObjectFor o when o.Kernel = info.Kernel -> IRObjectFor { o with Kernel = kernel' }
                        | l -> l
                    if decided.Add kid then
                        Blade.Effects.Decisions.record
                            { Blade.Effects.Rule = "invariant-hoist"; Version = 1
                              Span = Blade.Ast.noSpan; Subject = k.SourceName
                              Outcome = Blade.Effects.Applied
                              Evidence = List.ofSeq evidence @ orderEvidence }
                    Some (operandLets @ List.ofSeq lets, { info with Arrays = arrays; Kernel = kernel'; Loop = loop' })
             | _ -> None)
        | _ -> None

    let functions' =
        modul.Functions |> List.map (fun f ->
            arrayHoistScope.Value <- not (f.Name.StartsWith "__lambda_") && not f.IsStatic
            let b = withOwner (Some (LocatedAsCallable f.Id)) (fun () -> rw f.Body)
            arrayHoistScope.Value <- false
            { f with Body = b })
    let bindings' = modul.Bindings |> List.map (fun b -> { b with Value = withOwner (Some (LocatedAsBinding b.Id)) (fun () -> rw b.Value) })
    if minted.Count = 0 then modul
    else
        let derived (f: IRCallable) : IRCallable list =
            match minted.TryGetValue f.Id with
            | true, l -> List.ofSeq l
            | _ -> []
        { modul with
            Functions = functions' |> List.collect (fun f -> f :: derived f)
            Bindings = bindings'
            DerivedFuncOrigins =
                minted |> Seq.fold (fun acc (KeyValue (origin, l)) ->
                    l |> Seq.fold (fun a c -> Map.add c.Id origin a) acc) modul.DerivedFuncOrigins }
