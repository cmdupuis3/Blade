// Sibling-fold traversal sharing, rule sibling-folds (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeSiblingFolds

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeCse
open Blade.OptimizeHotRow
open Blade.OptimizeEpilogueLegs

// --- Sibling-fold traversal sharing (rule `sibling-folds` v1) -----------------
//
// Two scalar folds over the same dense axis in one straight-line region --
//
//     let (checksum, input_checksum) = {
//         let a = inputs(..); let b = inputs(..)
//         (kernel(a, b), reduce(a, (+)) + reduce(b, (+)))
//     }
//
// -- each walk the arrays once. The language already has the one-traversal
// spelling, the `<&!>` reduction JOIN (`IRReduceCompute` with an `IRTuple` of
// per-leg kernels and an `IRTuple` of per-leg seeds): every leg keeps its OWN
// accumulator, seed and ascending operand order, so sharing the traversal only
// interleaves independent chains. This pass rewrites sibling folds into one
// LET-BOUND join plus one projection let per leg, before the lane split, so
// the C++ lane (genReduceJoinCore), the LLVM lane and the interpreter all
// consume the existing, already-differential join support. No emitter change.
//
// REGION: one named function's body, or one module binding's value -- a
// straight-line `let` spine (lambda bodies are a v1.1 item). CANDIDATES sit in
// UNCONDITIONAL positions of a spine statement (a let value or the final
// expression, descending through tuples, arithmetic and call arguments; never
// a branch, a match arm, the right of `&&`/`||`, or a nested `let`):
//
//   * `reduce(a, k[, s])` over a stored rank-1 array;
//   * `IRReduceCompute` over one plain dense map leaf, or an existing let-bound
//     join / shared-fold tree whose tuple is only ever projected;
//   * `prodsum(x1, .., xk)` over Float64 rank-1 arrays (seed +0.0, the
//     left-nested product, as Interp.prodSum and the C++ IIFE fold it);
//   * a call `f(atoms)` to a module function that is inlined ONE level when
//     its body is a pure-total let spine with a scalar return -- and only when
//     one of its folds lands in a joined group (otherwise the call stays).
//
// LEGALITY, each a `declined -- <reason>` when it fails:
//   * every leg pure-total (`sfTotal`, a CLOSED typed whitelist: typed
//     floating `/` is discharged, integer `/` `%`, a non-literal integer `^`,
//     float->int casts, subscripts, guards, lgamma/digamma, output, external
//     reads and unknown or recursive callees decline; seeds are judged too),
//     every array a literal-extent plain dense axis (no runtime agreement
//     check), equal extents across the group;
//   * nothing between the join point and any member may write (the region
//     declines on any IRAssign / IRForRange; every crossed statement is judged
//     by the CSE facts, calls resolved through the whole program, deferred
//     reads inheriting their producer's facts);
//   * every free name of a leg (operands, seeds, captures) bound before the
//     join point;
//   * in a REORDERING build every leg agrees on the reorder licence, and
//     licensed legs fold with ONE builtin operator (the C++ join's
//     `fusedFoldSimdPlan` intersects licences and names one operator);
//   * a THREAD GATE: in a build that emits thread-level OpenMP and reorders,
//     a leg whose fold kernel requests omp under a licence (or whose map
//     kernel requests omp) joins only when EVERY leg carries its own reorder
//     licence and BLADE_JOIN_THREADS is on -- the policy of the threaded
//     reduction join (G7), under which the synthesized join (each leg keeping
//     its OWN request; none is manufactured) opens one team over the shared
//     traversal. Otherwise the group declines, so the requesting leg keeps
//     its own team. Request-only, so it may over-decline.
//   * an UNSEEDED builtin `(+)` / `(*)` leg over a literal extent >= 1 is
//     seeded with its exact identity (`sfIdentitySeed`); any other unseeded
//     kernel declines.
//   * a leg over a fresh `|> compute`d rank-1 map of the same region with no
//     use but folds is LEFT FOR THE PRODUCER EPILOGUE (the cell-epilogue item
//     owns that shape).

/// THE seed of an UNSEEDED builtin leg, in ONE place (round-2 arbitration Q1:
/// docs/formalism.md section 2.4 places NaN payloads and signalling state
/// outside the contract). `-0.0 + x` / `1.0 * x` is the exact identity under
/// round-to-nearest for every finite, infinite, signed-zero and quiet-NaN x,
/// so the seeded chain is bit-identical to the unseeded `a0 op a1 op ..` when
/// the extent is a literal >= 1 (the caller's check). The negative zero is the
/// node `IRUnaryOp(IRNeg, IRLit 0.0)` (renders `-(0.0)` / `fneg`), never a
/// float literal printer's spelling. Complex, unit-carrying, Bool and every
/// other kernel: None (decline).
let internal sfIdentitySeed (op: IRBinOp) (et: ElemType) : (IRExpr * string) option =
    match op, et with
    | IRAdd, ETFloat64 -> Some (IRUnaryOp (IRNeg, IRLit (IRLitFloat 0.0)), "-0.0")
    | IRAdd, ETFloat32 -> Some (IRUnaryOp (IRNeg, IRLit (IRLitFloat32 0.0f)), "-0.0")
    | IRMul, ETFloat64 -> Some (IRLit (IRLitFloat 1.0), "1.0")
    | IRMul, ETFloat32 -> Some (IRLit (IRLitFloat32 1.0f), "1.0")
    | IRAdd, ETInt64 -> Some (IRLit (IRLitInt 0L), "0")
    | IRMul, ETInt64 -> Some (IRLit (IRLitInt 1L), "1")
    | IRAdd, ETInt32 -> Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt 0L)), "0")
    | IRMul, ETInt32 -> Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt 1L)), "1")
    | _ -> None

/// THE seed of an UNSEEDED SELECT leg (gap G107 (1), round 4 wave 4): a fold
/// kernel `lambda(a, b) -> if <a vs b> then a else b` whose comparison makes
/// `a` the larger (`a > b`, `a >= b`, `b < a`, `b <= a`: a running maximum) or
/// the smaller (`a < b`, `a <= b`, `b > a`, `b >= a`: a running minimum). Its
/// seed e is a LEFT IDENTITY for every element value x, bit for bit:
/// `f(e, x) = if cmp(e, x) then e else x`, and with e = -inf (the max family)
/// or +inf (the min family) the comparison is true only when x IS e (`>=` /
/// `<=` at x = e, returning e -- the same bits as x) and false for every other
/// x, NaN included (an ordered comparison with NaN is false, so x is
/// returned). Integers take the type's MIN / MAX the same way. So the seeded
/// chain f(..f(f(e, a0), a1)..) is the unseeded a0-first chain exactly, for
/// a literal extent >= 1 (the caller's check). The THEN branch must be the
/// accumulator (`a`): `if a > b then b else a` (a running minimum written the
/// other way round) has no exact identity -- a NaN cell would return e -- and
/// declines, as does any other body. Returns the seed, its text, and the
/// family ("max" / "min").
let private sfSelectIdentitySeed (c: IRCallable) (et: ElemType) : (IRExpr * string * string) option =
    match c.Params, c.Body with
    | [ p0; p1 ], IRIf (IRBinOp (_, cmp, IRVar (l, _), IRVar (r, _), _), IRVar (t, _), IRVar (f, _))
        when t = p0.VarId && f = p1.VarId && p0.VarId <> p1.VarId ->
        // Normalize to `p0 <cmp> p1`.
        let norm =
            if l = p0.VarId && r = p1.VarId then Some cmp
            elif l = p1.VarId && r = p0.VarId then
                match cmp with
                | IRLt -> Some IRGt | IRLe -> Some IRGe | IRGt -> Some IRLt | IRGe -> Some IRLe
                | _ -> None
            else None
        let family =
            match norm with
            | Some (IRGt | IRGe) -> Some "max"
            | Some (IRLt | IRLe) -> Some "min"
            | _ -> None
        match family, et with
        | Some "max", ETFloat64 -> Some (IRLit (IRLitFloat System.Double.NegativeInfinity), "-inf", "max")
        | Some "min", ETFloat64 -> Some (IRLit (IRLitFloat System.Double.PositiveInfinity), "+inf", "min")
        | Some "max", ETFloat32 -> Some (IRLit (IRLitFloat32 System.Single.NegativeInfinity), "-inf", "max")
        | Some "min", ETFloat32 -> Some (IRLit (IRLitFloat32 System.Single.PositiveInfinity), "+inf", "min")
        | Some "max", ETInt64 -> Some (IRLit (IRLitInt System.Int64.MinValue), "Int64 MIN", "max")
        | Some "min", ETInt64 -> Some (IRLit (IRLitInt System.Int64.MaxValue), "Int64 MAX", "min")
        | Some "max", ETInt32 -> Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt (int64 System.Int32.MinValue))), "Int32 MIN", "max")
        | Some "min", ETInt32 -> Some (IRUnaryOp (IRCast (ETInt32, SrcLoc.Nowhere), IRLit (IRLitInt (int64 System.Int32.MaxValue))), "Int32 MAX", "min")
        | _ -> None
    | _ -> None

/// Where a region statement came from: an original spine statement, or the
/// j-th let of a call body inlined at (statement, call ordinal).
type private SfUnitKey =
    | SfOrig of int
    | SfInl of int * int * int

type private SfUnit = { Key: SfUnitKey; Binder: IRId option; Value: IRExpr }

/// One leg of a join. `Build` mints any synthesized kernel on demand (only a
/// rewrite that happens calls it).
type private SfLeg =
    { Build: unit -> IRExpr * IRExpr * IRExpr
      SeedNote: (string * string) option
      Licensed: bool
      Op: IRBinOp option
      Threads: bool
      /// The leg's map kernel against the C++ threaded join's OWN purity /
      /// totality admission (Blade.IRTotality, the predicate
      /// `joinedFoldThreadPlan`'s audit applies): Ok for a synthesized
      /// identity / product leaf.
      Audit: Result<unit, string>
      ResultType: IRType
      /// A leg made from `reduce(a, k[, s])` over a stored array: (a, k, the
      /// leg's seed -- the identity seed for an unseeded builtin leg). The
      /// producer-input admission (A7c) reads it; None for every other leg.
      Source: (IRId * IRExpr * IRExpr) option }

type private SfMember =
    { Unit: int
      UnitKey: SfUnitKey
      Ordinal: int
      Legs: SfLeg list
      Join: IRId option
      Extent: int64
      Axis: string
      Operands: IRId list
      Key: IRExpr option
      Desc: string
      FreeVars: Set<IRId> }

/// The candidate walk over one statement value, shared by discovery and the
/// rewrite so fold / call ordinals agree: `onFold` sees every fold node in an
/// unconditional position (not descended), `onCall` every call `isCall` admits
/// (after its arguments).
let private sfWalk (isCall: IRExpr -> bool) (onFold: int -> IRExpr -> IRExpr) (onCall: int -> IRExpr -> IRExpr) (e: IRExpr) : IRExpr =
    let mutable fo = 0
    let mutable co = 0
    let rec go (e: IRExpr) : IRExpr =
        match e with
        | IRReduce _ | IRReduceCompute _ | IRProdSum _ ->
            let o = fo
            fo <- fo + 1
            onFold o e
        | IRApp (f, args, t) ->
            let args' = args |> List.map go
            let e' = IRApp (f, args', t)
            if isCall e' then
                let o = co
                co <- co + 1
                onCall o e'
            else e'
        | IRTuple es -> IRTuple (es |> List.map go)
        | IRBinOp (m, (IRAnd | IROr as op), l, r, loc) -> IRBinOp (m, op, go l, r, loc)
        | IRBinOp (m, op, l, r, loc) ->
            let l' = go l
            let r' = go r
            IRBinOp (m, op, l', r', loc)
        | IRUnaryOp (op, x) -> IRUnaryOp (op, go x)
        | IRTupleProj (x, i, fl) -> IRTupleProj (go x, i, fl)
        | IRFma (a, b, c) ->
            let a' = go a
            let b' = go b
            IRFma (a', b', go c)
        | IRComplex (a, b) ->
            let a' = go a
            IRComplex (a', go b)
        | IRIf (c, t, f) -> IRIf (go c, t, f)
        | _ -> e
    go e

let shareSiblingFoldsModule (blasGateProbe: unit -> bool) (builder: IRBuilder) (programFuncs: IRCallable list) (modul: IRModule) : IRModule =
    let enabled = siblingFoldsEnabled ()
    let own = modul.Functions |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let funcs =
        programFuncs |> List.fold (fun m f -> if Map.containsKey f.Id m then m else Map.add f.Id f m) own
    let facts = cseFactsOf funcs
    let threadsBuild = Blade.Toolchain.ompThreadEmissionEnabled () && not (Blade.Toolchain.fpReproEnabled ())
    let reorderingBuild = not (Blade.Toolchain.fpReproEnabled ())

    // Names for the plan text: parameters, bindings, and the source names a
    // map's array identities carry.
    let names = System.Collections.Generic.Dictionary<IRId, string>()
    for KeyValue (_, f) in funcs do
        for p in f.Params do names.[p.VarId] <- p.Name
    for b in modul.Bindings do names.[b.Id] <- b.Name
    let noteIdentityNames (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRApplyCombinator info when info.Arrays.Length = info.Identities.Length ->
                List.iter2 (fun a ident ->
                    match a, ident with
                    | IRVar (id, _), AIDVariable nm when not (nm.StartsWith "__") && not (names.ContainsKey id) -> names.[id] <- nm
                    | IRVar (id, _), AIDAlias (_, nm) when not (nm.StartsWith "__") && not (names.ContainsKey id) -> names.[id] <- nm
                    | _ -> ()) info.Arrays info.Identities
            | _ -> ()) e
    let nameOf (id: IRId) = match names.TryGetValue id with | true, n -> n | _ -> $"__v{id}"
    // A variable renders as a MARKER resolved when the record is written: the
    // names an inlined body's map identities carry are only known after the
    // tentative inlining that judges the call.
    let showAtom (e: IRExpr) =
        match e with
        | IRVar (id, _) -> $"\u0002{id}\u0003"
        | IRLit (IRLitInt n) -> string n
        | IRLit (IRLitFloat f) -> string f
        | _ -> ".."

    // Names bound to a DEFERRED form (their producer runs where they are read).
    let isDeferredForm (v: IRExpr) : bool =
        match v with
        | IRApplyCombinator _ | IRComposeApply _ | IRMethodFor _ | IRObjectFor _
        | IRFusion _ | IRParallel _ | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _
        | IRReplicate _ | IRFunctorMap _ | IRBind _ | IRPure _ | IRZip _ | IRArrayProduct _
        | IRComposeObj _ | IRComposeMeth _ | IRCompose _ | IRReynolds _ -> true
        | _ -> false
    let deferred = System.Collections.Generic.HashSet<IRId>()
    let deferredValue = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let noteDeferred (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, v, _) when isDeferredForm v ->
                deferred.Add id |> ignore
                deferredValue.[id] <- v
            | _ -> ()) e
    for f in modul.Functions do noteDeferred f.Body
    for b in modul.Bindings do
        if isDeferredForm b.Value then
            deferred.Add b.Id |> ignore
            deferredValue.[b.Id] <- b.Value
        noteDeferred b.Value
    let capturedIds =
        funcs |> Map.fold (fun acc _ f -> f.Captures |> List.fold (fun a c -> Set.add c.Id a) acc) Set.empty
    // Names that merely ALIAS another name (`let w2 = w`): a read of one is a
    // read of the other, for the write closure and the provider test below.
    let aliasOf = System.Collections.Generic.Dictionary<IRId, IRId>()
    let noteAliases (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, IRVar (y, _), _) when not (Map.containsKey y funcs) -> aliasOf.[id] <- y
            | _ -> ()) e
    for f in modul.Functions do noteAliases f.Body
    for b in modul.Bindings do
        (match b.Value with
         | IRVar (y, _) when not (Map.containsKey y funcs) -> aliasOf.[b.Id] <- y
         | _ -> ())
        noteAliases b.Value
    let rec resolveAlias (fuel: int) (id: IRId) =
        match aliasOf.TryGetValue id with
        | true, y when fuel > 0 -> resolveAlias (fuel - 1) y
        | _ -> id
    // The program's deferred-value classifier (gap G98 class audit): a
    // wholly-deferred tuple's projection and a let chain ending deferred,
    // which the node census above misses, for the leg totality audits.
    let classifiedDeferred = widenDeferredOpen modul (funcs |> Map.toSeq |> Seq.map snd) (fun _ -> false)
    // the fail-closed variant (an unseen id -- another module binding -- is
    // deferred), read by the extent arm of the leg totality judge only
    let closedDeferred = widenDeferred modul (funcs |> Map.toSeq |> Seq.map snd) (fun _ -> false)
    // the closed classifier of the region being judged (G219: its census
    // includes the region's inlined lets); the module-wide one between regions
    let regionClosed : (IRId -> bool) ref = ref closedDeferred
    // ids minted from here on (a tentatively inlined region's fresh binders)
    // are this pass's own; an id below the limit that the closed classifier
    // calls deferred MAY be deferred -- an imported deferred binding, or any
    // pre-census id the classifier cannot see (an imported STORED binding
    // too: a conservative decline, Codex 0d750baf round 3)
    let censusLimit = builder.CurrentId()
    let foreignDeferred (id: IRId) = id < censusLimit && closedDeferred id
    // the top-level payload resolver, built ONCE (it indexes every binding;
    // per statement it made the top-level region quadratic)
    let topPayloadOf = intervalPayloadOf modul
    // A STREAMED provider read (`s.vars.A |> z.stream`) has an ordinary dense
    // array type but no storage a join can share: it is walked one block at a
    // time by its own fold, and it is an external read, not a pure value.
    let streamedIds =
        modul.ProviderReads |> Map.toSeq |> Seq.filter (fun (_, spec) -> spec.Streamed) |> Seq.map fst |> Set.ofSeq
    let isStreamed (id: IRId) = streamedIds.Contains id || streamedIds.Contains (resolveAlias 64 id)
    let ownBindingIds = modul.Bindings |> List.map (fun b -> b.Id) |> Set.ofList
    let moduleValues = modul.Bindings |> List.map (fun b -> (b.Id, b.Value)) |> Map.ofList
    // MAY EVALUATING `e` WRITE? The CSE facts (callees resolved through the
    // whole program), CLOSED over what a read evaluates elsewhere: a deferred
    // let's producer runs where it is read, an alias reads its target, and a
    // callable's captures may themselves be deferred -- transitively, cycle-safe
    // (a name met again on the current path adds nothing new).
    let rec writesIn (visiting: Set<IRId>) (e: IRExpr) : bool =
        (facts e).MayWrite
        || (collectVarRefsIR e |> Seq.exists (fun x -> writesThrough visiting x))
    and writesThrough (visiting: Set<IRId>) (x: IRId) : bool =
        if Set.contains x visiting then false
        else
            let v = Set.add x visiting
            match deferredValue.TryGetValue x with
            | true, dv -> writesIn v dv
            | _ ->
            match aliasOf.TryGetValue x with
            | true, y -> writesThrough v y
            | _ ->
            match Map.tryFind x funcs with
            | Some c ->
                (c.Captures |> List.exists (fun cap -> writesThrough v cap.Id))
                || (collectVarRefsIR c.Body |> Seq.exists (fun r ->
                        (deferredValue.ContainsKey r || aliasOf.ContainsKey r || Map.containsKey r funcs)
                        && writesThrough v r))
            | None -> false
    let writeMemo = System.Collections.Generic.Dictionary<IRExpr, bool>(HashIdentity.Reference)
    let mayWrite (e: IRExpr) : bool =
        match writeMemo.TryGetValue e with
        | true, r -> r
        | _ ->
            let r = writesIn Set.empty e
            writeMemo.[e] <- r
            r
    // Mirror of the rank-1 producer-epilogue switch (BLADE_CELL_EPILOGUE,
    // the cell-epilogue item's gate): unset/anything else = on, 0/off/false/no
    // = off. Read per call.
    let cellEpilogueOn () =
        match System.Environment.GetEnvironmentVariable "BLADE_CELL_EPILOGUE" with
        | null -> true
        | v -> not (List.contains (v.Trim().ToLowerInvariant()) [ "0"; "off"; "false"; "no" ])

    // ---- the totality judge (closed, typed whitelist) ----------------------
    let isFloatish (t: IRType) = match t with AnyPrimElem (ETFloat32 | ETFloat64 | ETComplex64 | ETComplex128) -> true | _ -> false
    let isIntish (t: IRType) = match t with AnyPrimElem (ETInt32 | ETInt64 | ETBool) -> true | _ -> false
    let rec sfTotal (visited: Set<IRId>) (e: IRExpr) : bool =
        let tot = sfTotal visited
        match e with
        | IRLit _ -> true
        | IRVar (id, t) ->
            // a read of a deferred value FORCES it, so it is not total for a
            // moved leg -- through an ALIAS of one too (`let y = d` read in a
            // kernel: round 4 wave 9, lane A, A4, Codex 0d750baf member 2,
            // BL8013 moved ahead of an intervening BL8014), and by the
            // program classifier (an imported or let-chained deferred value)
            not (Map.containsKey id funcs) && not (isFunctionTyped t) && not (deferred.Contains id)
            && not (deferred.Contains (resolveAlias 64 id)) && not (classifiedDeferred id)
            && not (classifiedDeferred (resolveAlias 64 id))
            // an IMPORTED deferred map read through an alias (let y = d, d
            // from another module: Codex 0d750baf diff review) -- the open
            // classifier cannot see its producer; fail closed
            && not (foreignDeferred id) && not (foreignDeferred (resolveAlias 64 id))
            && (match t with IRTLoop _ | IRTComputation _ -> false | _ -> true)
        | IRBinOp (_, (IRDiv | IRMod as op), l, r, _) ->
            tot l && tot r
            && (not (irNodeMayAbort e) || (op = IRDiv && isFloatish (typeOf l) && isFloatish (typeOf r)))
        | IRBinOp (_, IRCaret, l, r, _) -> tot l && tot r && (not (irNodeMayAbort e) || isFloatish (typeOf l))
        | IRBinOp (_, _, l, r, _) -> tot l && tot r
        | IRUnaryOp (IRCast ((ETInt64 | ETInt32), _), x) -> tot x && isIntish (typeOf x)
        | IRUnaryOp (IRMath ("lgamma" | "digamma"), _) -> false
        | IRUnaryOp (_, x) -> tot x
        | IRFma (a, b, c) -> tot a && tot b && tot c
        | IRComplex (a, b) -> tot a && tot b
        | IRIf (c, t, f) -> tot c && tot t && tot f
        | IRLet (_, v, b) -> tot v && tot b
        | IRTuple es -> es |> List.forall tot
        | IRTupleProj (x, _, _) -> tot x
        // round 4 wave 7 (lane c, Codex 18a2f03d, G107): the interpreter FORCES a
        // deferred value to read its extent (the compiled lanes read the static
        // extent), so an extent read of a deferred map moved with a leg would move
        // that force (and its failure) ahead of an intervening one
        // (Codex 27945c90: an IMPORTED deferred map is unseen by the open
        // classifier -- the CLOSED one answers deferred for an id this module
        // and its callables do not bind)
        | IRExtent (IRVar (id, _), _) -> not (deferred.Contains id) && not (classifiedDeferred id) && not (regionClosed.Value id)
        | CallOfNamed (fid, _, args, _) ->
            args |> List.forall tot
            && (match Map.tryFind fid funcs with
                | Some c when not (Set.contains fid visited) && not c.IsStatic && not c.IsCudaKernel
                              && not c.IsMpiParallel && c.Params.Length = args.Length ->
                    sfTotal (Set.add fid visited) c.Body
                | _ -> false)
        | _ -> false
    let kernelOf (k: IRExpr) : IRCallable option =
        match k with
        | IRVar (kid, _) -> Map.tryFind kid funcs
        | _ -> None
    let kernelTotal (c: IRCallable) (arity: int) =
        c.Params.Length = arity && not c.IsCudaKernel && not c.IsMpiParallel && not c.IsStatic
        && sfTotal (Set.singleton c.Id) c.Body
    let builtinOp (c: IRCallable) : IRBinOp option =
        match c.Params, c.Body with
        | [ p0; p1 ], IRBinOp (_, op, IRVar (a, _), IRVar (b, _), _)
            when (match op with IRAdd | IRMul | IRAnd | IROr -> true | _ -> false)
                 && ((a = p0.VarId && b = p1.VarId) || (a = p1.VarId && b = p0.VarId)) -> Some op
        | _ -> None
    // `foldReorderLicensed`'s rule (CodeGenExprSupport), restated over this
    // module's table: a `where repro` kernel (or the eta wrapper of one) vetoes.
    let licensed (c: IRCallable) =
        let vetoed =
            c.IsRepro
            || (match c.Body with
                | CallOfNamed (fid, _, _, _) -> (match Map.tryFind fid funcs with Some g -> g.IsRepro | None -> false)
                | _ -> false)
        not vetoed && (c.IsCommutative || not c.CommGroups.IsEmpty || (builtinOp c).IsSome)
    let plainAxis (at: IRArrayType) : (int64 * IRIndexType) option =
        if at.IsVirtual then None else
        match at.IndexTypes with
        | [ ix ] when ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1 ->
            (match ix.Extent with IRLit (IRLitInt n) -> Some (n, ix) | _ -> None)
        | _ -> None
    let axisName (ix: IRIndexType) = match ix.Tag with Some t when not (t.StartsWith "__") -> t | _ -> "axis"
    let bareElem (t: IRType) : ElemType option =
        match t with
        | IRTScalar ((ETFloat64 | ETFloat32 | ETInt64 | ETInt32) as et) -> Some et
        | _ -> None
    let rec leavesOf (e: IRExpr) =
        match e with
        | IRFusion (l, r) -> leavesOf l @ leavesOf r
        | x -> [ x ]
    // A plain dense map leaf over stored literal-extent arrays of one extent:
    // Some (extent, axis, operand ids, kernel).
    let rec plainLeaf (info: ApplyInfo) = plainLeafWith true info
    and plainLeafWith (requireTotal: bool) (info: ApplyInfo) : (int64 * IRIndexType * IRId list * IRCallable) option =
        // The fusion pass's plain-map predicate; a Loop slot that NAMES the
        // loop object (`let l = method_for(x)`, a composed chain binding) is
        // the same provenance, one let away.
        let info' =
            match info.Loop with
            | IRVar _ -> { info with Loop = IRObjectFor { Kernel = info.Kernel; CommGroups = []; InputRanks = []; OutputRank = 0 } }
            | _ -> info
        if not (tilePlainInfo info') || info.Arrays.IsEmpty || info.Arrays.Length <> info.ArrayTypes.Length then None
        elif info.Arrays.Length > 1 && not info.IsCoIteration then None
        else
            let axes = info.ArrayTypes |> List.map plainAxis
            let ids = info.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
            if axes |> List.exists Option.isNone || ids.Length <> info.Arrays.Length then None
            else
                let axes = axes |> List.map Option.get
                let n0 = fst axes.Head
                if axes |> List.exists (fun (n, _) -> n <> n0) then None
                else
                    match kernelOf info.Kernel with
                    | Some k when (not requireTotal || kernelTotal k info.Arrays.Length) && bareElem k.RetType |> Option.isSome ->
                        Some (n0, snd axes.Head, ids, k)
                    | _ -> None
    // Pure-total fold NODE (an inlined body's statement): operands stored,
    // literal extents equal, kernels and seeds total, unseeded only over n >= 1.
    // A streamed provider read is neither stored nor pure (see isStreamed).
    let foldTotal (e: IRExpr) : bool =
        match e with
        | ReduceOfNamed (aid, ArrayElem at, k, init) when not (isStreamed aid) ->
            (match plainAxis at, kernelOf k with
             | Some (n, _), Some c ->
                kernelTotal c 2 && (match init with Some s -> sfTotal Set.empty s | None -> n >= 1)
             | _ -> false)
        | IRReduceCompute (comp, k, s) ->
            let leaves = leavesOf comp
            let ls = leaves |> List.map (function IRApplyCombinator i -> plainLeaf i | _ -> None)
            let ks = match k with IRTuple ks -> ks | k -> [ k ]
            let ss = match s with IRTuple ss -> ss | s -> [ s ]
            ls |> List.forall Option.isSome
            && ls |> List.forall (fun l -> let (_, _, ids, _) = l.Value in ids |> List.forall (isStreamed >> not))
            && (ls |> List.map (fun l -> let (n, _, _, _) = l.Value in n) |> List.distinct |> List.length) = 1
            && ks |> List.forall (fun k -> match kernelOf k with Some c -> kernelTotal c 2 | None -> false)
            && ss |> List.forall (sfTotal Set.empty)
        | IRProdSum args ->
            let axes = args |> List.map (function IRVar (aid, ArrayElem at) when not (isStreamed aid) -> plainAxis at | _ -> None)
            axes |> List.forall Option.isSome
            && (axes |> List.map (fun a -> fst a.Value) |> List.distinct |> List.length) = 1
        | _ -> false

    // ---- one-level inliner ---------------------------------------------------
    // Expression/kernel bodies do not open an allocation tracking frame in
    // C++. Only ordinary statement-rendered functions may retain an inline
    // owning pools. A function used as a value (including a kernel slot or
    // alias) is therefore excluded; direct calls remain statement-rendered.
    let expressionRendered = System.Collections.Generic.HashSet<IRId>()
    let rec noteFunctionUses e =
        match e with
        | IRApp (IRVar _, args, _) -> args |> List.iter noteFunctionUses
        | IRVar (id, _) when Map.containsKey id funcs -> expressionRendered.Add id |> ignore
        | ExprShape (children, _) -> children |> List.iter noteFunctionUses
    for f in programFuncs do noteFunctionUses f.Body
    for f in modul.Functions do noteFunctionUses f.Body
    for b in modul.Bindings do noteFunctionUses b.Value
    // TOP-LEVEL MODE (gap G107 (3), round 4 wave 4): the module's binding
    // sequence judged as ONE region (see `topLevelRegion` below). No call is
    // inlined there (an inlined spine would have to become new top-level
    // bindings), no existing join is dissolved (its binding is the user's),
    // and the producer-input legs (A7c) are not planned.
    let topLevel = ref false
    let topBarrier = ref (Set.empty : Set<IRId>)
    let schedWritesTop = lazy (scheduleMayWriteIn programFuncs modul)
    let isOwnCall (e: IRExpr) =
        match e with
        | IRApp _ when topLevel.Value -> false
        | CallOfNamed (fid, _, _, _) ->
            (match Map.tryFind fid own with
             | Some c -> not (c.Name.StartsWith "__lambda_")
             | None -> false)
        | _ -> false
    let rec unrollSpine (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, b) ->
            let (ls, r) = unrollSpine b
            ((id, v) :: ls, r)
        | _ -> ([], e)
    let countFolds (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x -> match x with IRReduce _ | IRReduceCompute _ | IRProdSum _ -> n <- n + 1 | _ -> ()) e
        n
    let inlineValueOk (v: IRExpr) : bool =
        match v with
        | IRReduce _ | IRReduceCompute _ | IRProdSum _ -> foldTotal v
        | IRMethodFor mf -> mf.Arrays |> List.forall (function IRVar _ -> true | _ -> false)
        | IRObjectFor _ -> true
        | IRCompute (IRApplyCombinator info) ->
            (match plainLeaf info with
             | Some (_, _, ids, _) -> ids |> List.forall (isStreamed >> not)
             | None -> false)
        | IRTupleProj (IRVar _, _, _) -> true
        | _ -> sfTotal Set.empty v
    /// Ok (lets, result atom, display) or Error why.
    let inlineJudge (call: IRExpr) : Result<(IRId * IRExpr) list * IRExpr * string, string> =
        match call with
        | CallOfNamed (fid, _, args, retTy) ->
            let c = own.[fid]
            let disp = $"""{c.SourceName}({args |> List.map showAtom |> String.concat ", "})"""
            let refused why = Error $"{disp} not inlined: {why}"
            if c.IsRepro then refused "a `where repro` callee keeps its per-function veto"
            elif c.IsStatic || c.IsArityPoly || hasTypeVarsInSignature c then refused "a static, arity-polymorphic or generic callee"
            elif c.IsCudaKernel || c.IsMpiParallel || c.IsOmpParallel then refused "a callee with a parallel strategy"
            elif not c.Captures.IsEmpty then refused "the callee captures"
            elif c.Params.Length <> args.Length then refused "an argument-count mismatch"
            elif args |> List.exists (function IRVar (_, t) -> isFunctionTyped t | IRLit _ -> false | _ -> true) then
                refused "an argument is not a variable or a literal"
            elif args |> List.exists (function IRVar (id, _) -> isStreamed id | _ -> false) then
                refused "an argument is a streamed provider read (walked one block at a time; not a stored, pure value)"
            elif bareElem c.RetType |> Option.isNone then refused "the callee does not return a bare scalar"
            elif Set.contains c.Id (collectVarRefsIR c.Body) then refused "a recursive callee"
            else
            let mutable shapeOk = true
            iterIRExpr (fun n ->
                match n with
                | IRParam _ | IRNth | IRArity _ | IRPolyIndex _ | IRPolyTail _ | IRAssign _ | IRForRange _ -> shapeOk <- false
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some g when not g.Captures.IsEmpty -> shapeOk <- false
                     | _ -> ())
                | _ -> ()) c.Body
            if not shapeOk then refused "the callee body names a capturing lambda, a pack form or an assignment"
            else
            let (lets0, r0) = unrollSpine c.Body
            let releaseSupported v =
                match v with
                | IRCompute (IRApplyCombinator info) ->
                    match stripUnits info.OutputType with
                    | ArrayElem at -> (plainAxis at).IsSome && (bareElem at.ElemType).IsSome
                    | _ -> false
                | _ -> true
            if not ((lets0 |> List.forall (snd >> inlineValueOk)) && inlineValueOk r0) then
                refused "the callee body is not a pure-total let spine of folds, maps and scalar arithmetic"
            elif not (lets0 |> List.forall (snd >> releaseSupported)) then
                refused "lifetime event not honoured by the common dense rank-one pool renderer"
            else
            let (ps', body') = freshenCallable builder c
            let subst = List.zip ps' args |> List.map (fun (p, a) -> (p.VarId, a)) |> Map.ofList
            let body'' =
                mapIRExpr (fun n ->
                    match n with
                    | IRVar (id, _) when Map.containsKey id subst -> subst.[id]
                    | _ -> n) body'
            let (lets, r) = unrollSpine body''
            let lets, atom =
                match r with
                | IRVar _ | IRLit _ -> lets, r
                | _ ->
                    let t = builder.FreshId()
                    lets @ [ (t, r) ], IRVar (t, retTy)
            Ok (lets, atom, disp)
        | _ -> Error "not a call"

    let minted = ResizeArray<IRCallable * IRId option>()
    let mint (origin: IRId option) (c: IRCallable) =
        minted.Add((c, origin))
        c

    // ---- one region ----------------------------------------------------------
    let processRegion (subject: string) (origin: IRId option) (regionParams: Set<IRId>) (body: IRExpr) : IRExpr =
        let lifetimeProblem =
            if (Blade.Toolchain.ompCoverageModeCell ()).Value then Some "OpenMP coverage renderer"
            elif origin |> Option.exists expressionRendered.Contains then Some "expression/kernel renderer"
            else None
        let (spineLets, final) = unrollSpine body
        let stmts = (spineLets |> List.map (fun (id, v) -> (Some id, v))) @ [ (None, final) ]
        // An inlined call's spine is placed BEFORE its whole statement, ahead
        // of whatever the statement evaluates before (or after) the call; so a
        // statement that may write anywhere keeps its calls as calls.
        let stmtWrites = stmts |> List.map (fun (_, v) -> mayWrite v) |> Array.ofList
        let inlineCache = System.Collections.Generic.Dictionary<int * int, Result<(IRId * IRExpr) list * IRExpr * string, string>>()
        let callNodes = System.Collections.Generic.Dictionary<int * int, IRExpr>()
        let judgeAt (i: int) (ord: int) (call: IRExpr) =
            callNodes.[(i, ord)] <- call
            match inlineCache.TryGetValue((i, ord)) with
            | true, r -> r
            | _ ->
                let r =
                    match inlineJudge call with
                    | Ok (_, _, disp) when lifetimeProblem.IsSome ->
                        Error $"{disp} not inlined: lifetime event not honoured by {lifetimeProblem.Value}; retained inline declined"
                    | Ok _ when stmtWrites.[i] ->
                        let disp =
                            match call with
                            | CallOfNamed (fid, _, args, _) -> $"""{own.[fid].SourceName}({args |> List.map showAtom |> String.concat ", "})"""
                            | _ -> "the call"
                        Error $"{disp} not inlined: its statement may write (inlining moves the call's body ahead of the whole statement, across that write)"
                    | r -> r
                inlineCache.[(i, ord)] <- r
                r
        let refusedCallFolds = System.Collections.Generic.Dictionary<int * int, int>()
        let buildUnits (keep: int * int -> bool) : SfUnit list =
            let units = ResizeArray<SfUnit>()
            stmts |> List.iteri (fun i (binder, v) ->
                let here = ResizeArray<int * (IRId * IRExpr) list>()
                let v' =
                    sfWalk isOwnCall (fun _ n -> n) (fun ord call ->
                        match judgeAt i ord call with
                        | Ok (lets, atom, _) when keep (i, ord) ->
                            here.Add((ord, lets))
                            atom
                        | Ok _ -> call
                        | Error _ ->
                            (match call with
                             | CallOfNamed (fid, _, _, _) -> refusedCallFolds.[(i, ord)] <- countFolds own.[fid].Body
                             | _ -> ())
                            call) v
                for (ord, lets) in here do
                    lets |> List.iteri (fun j (id, lv) -> units.Add { Key = SfInl (i, ord, j); Binder = Some id; Value = lv })
                units.Add { Key = SfOrig i; Binder = binder; Value = v' })
            List.ofSeq units
        regionClosed.Value <- closedDeferred
        let tentative = buildUnits (fun _ -> true) |> Array.ofList
        for u in tentative do noteIdentityNames u.Value
        // G219, round 4 wave 9: the region as the pass sees it (its inlined
        // call bodies under FRESH ids) joins the closed classifier's census,
        // so a just-inlined `let y = .. |> compute` is judged stored, not an
        // unseen (imported) binder; an id bound nowhere stays deferred
        // (rebuilt only for a region that INLINED something: a region with no
        // fresh binder is judged by the module-wide census, so the cost is one
        // census per inlining region, Codex diff review 5f64f352 finding 6)
        if tentative |> Array.exists (fun u -> match u.Key with SfInl _ -> true | SfOrig _ -> false) then
            regionClosed.Value <-
                widenDeferredWith modul (funcs |> Map.toSeq |> Seq.map snd)
                    (tentative |> Seq.map (fun u ->
                        match u.Binder with
                        | Some id -> IRLet (id, u.Value, IRLit IRLitUnit)
                        | None -> u.Value))
                    (fun _ -> false)

        // Fold candidates in unconditional positions.
        let found = ResizeArray<int * int * IRExpr>()
        tentative |> Array.iteri (fun ui u ->
            sfWalk isOwnCall (fun ord n -> found.Add((ui, ord, n)); n) (fun _ c -> c) u.Value |> ignore)
        let refusals =
            // Only a refused callee that HOLDS folds is a decision worth naming.
            inlineCache |> Seq.choose (fun (KeyValue (k, r)) ->
                match r with
                | Error why when (match refusedCallFolds.TryGetValue k with | true, n -> n > 0 | _ -> false) -> Some why
                | _ -> None)
            |> Seq.distinct |> List.ofSeq
        let refusedFolds = refusedCallFolds.Values |> Seq.sum
        if found.Count + refusedFolds < 2 then body else

        let boundAt = System.Collections.Generic.Dictionary<IRId, int>()
        tentative |> Array.iteri (fun ui u -> match u.Binder with Some id -> boundAt.[id] <- ui | None -> ())
        let imperative =
            tentative |> Array.exists (fun u ->
                let mutable hit = false
                iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ | IRBreakIf _ | IRReplayRow _ -> hit <- true | _ -> ()) u.Value
                hit)
        // At top level each binding is judged by its whole PAYLOAD through the
        // shared scheduling write analysis too (a provider write's value is a
        // unit placeholder: its effect lives in the side table), and an
        // assigning / looping binding is a barrier rather than a reason to
        // give up on the whole module.
        let unitWrites =
            tentative |> Array.map (fun u ->
                mayWrite u.Value
                || (topLevel.Value
                    && (match u.Binder with
                        | Some id ->
                            Set.contains id topBarrier.Value
                            || intervalHasProviderEffect modul id
                            || (let pl = topPayloadOf id u.Value in schedWritesTop.Force () pl || mayWrite pl)
                        | None -> false)
                    || (topLevel.Value
                        && (let mutable hit = false
                            iterIRExpr (fun n -> match n with IRAssign _ | IRForRange _ | IRBreakIf _ -> hit <- true | _ -> ()) u.Value
                            hit))))
        // LEFT FOR THE PRODUCER EPILOGUE: exactly the shape the rank-1
        // cell-epilogue planner admits (Optimize.planHotRowFolds' rank-1 arm,
        // mirrored textually, not shared): a let-bound
        // `IRReduceCompute(method_for(y) <@> k, F, seed)` whose cell kernel is
        // `w` or `w * w` of its own cell, `y` a let-bound dense rank-1
        // literal-extent `|> compute` map of this region, the fold the FIRST
        // such consumer of `y`, nothing between them reading `y` or what the
        // cell kernel captures, everything between repeatable, the fold
        // kernel a two-parameter callable, no cuda/mpi or Reynolds -- and the
        // epilogue gate on. Anything else is an ordinary candidate.
        let cellTermOk (ck: IRCallable) =
            match ck.Params with
            | [ p ] ->
                let isP (e: IRExpr) =
                    match e with
                    | IRVar (id, _) -> id = p.VarId
                    | IRParam (_, idx, _) -> idx = p.Index
                    | _ -> false
                (match ck.Body with
                 | IRBinOp (IRElementwise, IRMul, a, b, _) when isP a && isP b -> true
                 | e -> isP e)
            | _ -> false
        let oneCellConsumerOf (v: IRExpr) : (IRId * ApplyInfo * IRCallable) option =
            match v with
            | IRReduceCompute (IRApplyCombinator cinfo, _, _) ->
                (match cinfo.Arrays, kernelOf cinfo.Kernel with
                 | [ IRVar (a, _) ], Some ck when ck.Params.Length = 1 && cinfo.KernelOutputRank = 0
                                                 && (match stripUnits ck.Params.Head.Type with IRTScalar _ -> true | _ -> false) ->
                    Some (a, cinfo, ck)
                 | _ -> None)
            | _ -> None
        let epilogueReserved (ui: int) (ord: int) (node: IRExpr) : bool =
            if not (cellEpilogueOn ()) || ord <> 0 then false else
            let u = tentative.[ui]
            match u.Binder, oneCellConsumerOf u.Value, node with
            | Some _, Some (pId, cinfo, ck), IRReduceCompute (_, k, _) when obj.ReferenceEquals(u.Value, node) ->
                (match boundAt.TryGetValue pId with
                 | true, pu when pu < ui ->
                    let rec collapse (e: IRExpr) = match e with IRCompute i -> collapse i | e -> e
                    (match collapse tentative.[pu].Value, tentative.[pu].Value with
                     | IRApplyCombinator pinfo, IRCompute _ ->
                        let rank1 =
                            match stripUnits pinfo.OutputType with
                            | ArrayElem at ->
                                not at.IsVirtual && pinfo.KernelOutputRank = 0
                                && (match at.IndexTypes with
                                    | [ ix ] -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1
                                                && (match ix.Extent with IRLit (IRLitInt n) -> n > 0L | _ -> false)
                                    | _ -> false)
                            | _ -> false
                        let pkOk =
                            match kernelOf pinfo.Kernel with
                            | Some pk -> not pk.IsCudaKernel && not pk.IsMpiParallel
                            | None -> false
                        let firstConsumer =
                            seq { pu + 1 .. ui - 1 }
                            |> Seq.forall (fun i -> match oneCellConsumerOf tentative.[i].Value with Some (a, _, _) -> a <> pId | None -> true)
                        let captureIds = ck.Captures |> List.map (fun c -> c.Id) |> Set.ofList
                        let betweenOk =
                            seq { pu + 1 .. ui - 1 }
                            |> Seq.forall (fun i ->
                                let t = tentative.[i]
                                let r = collectVarRefsIR t.Value
                                (match t.Binder with Some b -> not (captureIds.Contains b) | None -> true)
                                && not (Set.contains pId r)
                                && Set.isEmpty (Set.intersect r captureIds)
                                && (facts t.Value).Repeatable)
                        let fkOk =
                            match kernelOf k with
                            | Some fk -> fk.Params.Length = 2 && not fk.IsCudaKernel && not fk.IsMpiParallel
                                         && (match stripUnits fk.RetType with IRTScalar _ -> true | _ -> false)
                            | None -> false
                        rank1 && pkOk && firstConsumer && betweenOk && fkOk
                        && not pinfo.HasReynolds && not cinfo.HasReynolds
                        && not (Set.contains pId captureIds) && not (Set.contains pId (collectVarRefsIR ck.Body))
                        && cellTermOk ck
                     | _ -> false)
                 | _ -> false)
            | _ -> false
        let operandProblem (id: IRId) : string option =
            // (A module binding is not refused for being reassignable: inside
            // one region only a CALL or an assignment can write it, and both
            // are barriers above.)
            // round 4 wave 6 (the top-level class-closure round): the
            // program's deferred-value classifier too -- a projection of a
            // tuple holding a deferred map (`let y = pack[0]`) is deferred,
            // and joining it would force its producer at the join (earlier
            // than its first reader: another failure first, and its print).
            if deferred.Contains id || deferred.Contains (resolveAlias 64 id)
               || classifiedDeferred id || classifiedDeferred (resolveAlias 64 id) then
                Some "an operand is a deferred computation (one level of sharing only)"
            // round 4 wave 10 (lane a, A3, the fresh class-closure round of
            // `top`, Codex af3eedf4): a LOCAL alias of an IMPORTED binding
            // (`from Lib import d; let y = d`) resolves to an id this module's
            // open classifier never saw, so an imported deferred map passed as
            // stored and its force moved to the join (BL8013 ahead of an
            // intervening BL8014). The fail-closed classifier judges the
            // alias's target, as the kernel-read arm already does (m11).
            // Followed through every STRUCTURAL step of the operand's value --
            // an alias, a tuple construction, a tuple projection (`let pair =
            // (d, d); let y = pair[0]`, the diff review's second spelling) --
            // to a root this module does not bind; a region parameter is a
            // root the region owns.
            elif (// each binding is judged ONCE per operand (memo) and the whole
                  // walk has one work budget: shared tuple members made it
                  // exponential (the diff review, round 5, Codex 00f0ebe8: eight
                  // copies of an alias per tuple, ten levels deep)
                  let memo = System.Collections.Generic.Dictionary<IRId, bool>()
                  let work = ref 0
                  let rec reachesForeign (fuel: int) (x: IRId) : bool =
                    match memo.TryGetValue x with
                    | true, r -> r
                    | _ ->
                      work.Value <- work.Value + 1
                      // an exhausted budget is UNKNOWN, so it declines (the diff
                      // review, round 4: 66 chained aliases were admitted)
                      let r =
                          if fuel <= 0 || work.Value > 4096 then true
                          elif regionParams.Contains x then false
                          else
                              let valueOf =
                                  match boundAt.TryGetValue x with
                                  | true, ui -> Some tentative.[ui].Value
                                  | _ -> Map.tryFind x moduleValues
                              match valueOf with
                              | Some v ->
                                  let rec structural (e: IRExpr) =
                                      match e with
                                      | IRVar (y, _) -> reachesForeign (fuel - 1) y
                                      | IRTuple es -> List.exists structural es
                                      | IRTupleProj (inner, _, _) -> structural inner
                                      | _ -> false
                                  structural v
                              | None -> foreignDeferred x
                      memo.[x] <- r
                      r
                  (boundAt.ContainsKey id || ownBindingIds.Contains id) && reachesForeign 64 id) then
                Some "an operand is (an alias of) a binding of another module the fail-closed deferred classifier cannot prove stored"
            elif isStreamed id then Some "an operand is a streamed provider read (walked one block at a time by its own fold; not a stored, pure value)"
            elif not (boundAt.ContainsKey id || regionParams.Contains id || ownBindingIds.Contains id) then
                Some "an operand is bound outside this region and module (its provider metadata is not visible here)"
            else None
        let freeVarsOf (e: IRExpr) : Set<IRId> =
            let acc = System.Collections.Generic.HashSet<IRId>()
            let rec addExpr (x: IRExpr) = for id in collectVarRefsIR x do addId id
            and addId (id: IRId) =
                if acc.Add id then
                    match Map.tryFind id funcs with
                    | Some c ->
                        for cap in c.Captures do addId cap.Id
                        addExpr c.Body
                    | None -> ()
            addExpr e
            Set.ofSeq acc
        // Canonical form for the duplicate-leg test: callables by structure,
        // floats by bit pattern.
        let callableKey (f: IRCallable) : IRExpr =
            let subst = f.Params |> List.mapi (fun i p -> (p.VarId, -(i + 1))) |> Map.ofList
            let b =
                mapIRExpr (fun e ->
                    match e with
                    | IRVar (id, t) when Map.containsKey id subst -> IRVar (subst.[id], t)
                    | _ -> floatBitsLit e) f.Body
            IRTuple [ b; IRLit (IRLitString $"{f.IsOmpParallel}|{f.IsCommutative}|{f.IsRepro}|%A{f.CommGroups}") ]
        let canon (e: IRExpr) : IRExpr =
            mapIRExpr (fun n ->
                match n with
                | IRVar (id, _) ->
                    (match Map.tryFind id funcs with
                     | Some f -> callableKey f
                     | None -> n)
                | _ -> floatBitsLit n) e

        let mkLeg (leafF: unit -> IRExpr) (k: IRExpr) (c: IRCallable) (seed: IRExpr) (note: (string * string) option) (leafOmp: bool) (audit: Result<unit, string>) (resTy: IRType) =
            let lic = licensed c
            { Build = (fun () -> (leafF (), k, seed)); SeedNote = note; Licensed = lic; Op = builtinOp c
              Threads = (c.IsOmpParallel && lic) || leafOmp; Audit = audit; ResultType = resTy; Source = None }

        // Member admission.
        let admit (ui: int) (ord: int) (node: IRExpr) : Result<SfMember, string> =
            let u = tentative.[ui]
            let baseM legs n ix ids key desc join =
                { Unit = ui; UnitKey = u.Key; Ordinal = ord; Legs = legs; Join = join; Extent = n
                  Axis = axisName ix; Operands = ids; Key = key; Desc = desc; FreeVars = freeVarsOf node }
            match node with
            | IRReduce (arr, k, init) ->
                let desc = $"reduce({showAtom arr}, ..)"
                (match arr with
                 | IRVar (aid, (ArrayElem at as aty)) ->
                    (match plainAxis at, bareElem at.ElemType, kernelOf k with
                     | None, _, _ -> Error $"{desc}: not over one plain dense literal-extent axis"
                     | _, None, _ -> Error $"{desc}: the element type is not a bare Float64/Float32/Int64/Int32 (complex and unit-carrying legs are a v1.1 item)"
                     | _, _, None -> Error $"{desc}: the fold kernel is not a resolvable callable"
                     | Some (n, ix), Some et, Some c ->
                        match operandProblem aid with
                        | Some why -> Error $"{desc}: {why}"
                        | None ->
                        if not (kernelTotal c 2) then Error $"{desc}: the fold kernel may fail or has effects"
                        else
                        let seedR =
                            match init with
                            | Some s -> if sfTotal Set.empty s then Ok (s, None) else Error "the seed may fail or has effects"
                            | None ->
                                match builtinOp c with
                                | Some op when n >= 1L ->
                                    (match sfIdentitySeed op et with
                                     | Some (s, txt) -> Ok (s, Some (operatorFunctionName op, txt))
                                     | None -> Error "unseeded with no identity seed for its operator and element in v1")
                                | Some _ -> Error "an unseeded leg's extent is not a literal >= 1 (the empty-fold BL8003 is kept)"
                                | None ->
                                    match sfSelectIdentitySeed c et with
                                    | Some _ when n < 1L -> Error "an unseeded leg's extent is not a literal >= 1 (the empty-fold BL8003 is kept)"
                                    | Some (s, txt, fam) -> Ok (s, Some ($"{fam}-select", txt))
                                    | None -> Error "unseeded with a non-builtin kernel that is not a running max / min select (`if a > b then a else b`; no exact identity seed)"
                        match seedR with
                        | Error why -> Error $"{desc}: {why}"
                        | Ok (seed, note) ->
                            let elemTy = at.ElemType
                            let leafF () =
                                let pid = builder.FreshId()
                                let parms : IRParam list = [ { Name = "__sfx"; Type = elemTy; Index = 0; VarId = pid } ]
                                let lam =
                                    { mkLambdaCallable builder parms (IRVar (pid, elemTy)) elemTy [] false [] [] false false 256 false
                                        with SourceName = anonymousCallableName (Some subject) "lambda" }
                                    |> mint origin
                                let outTy =
                                    match aty with
                                    | IRTArrow (slots, _, id2) -> IRTArrow (slots, elemTy, id2)
                                    | t -> t
                                mapOneArrayApply builder arr at (IRVar (lam.Id, mkFuncArrow [ elemTy ] elemTy)) outTy
                            let leg = { mkLeg leafF k c seed note false (Ok ()) (typeOf node) with Source = Some (aid, k, seed) }
                            Ok (baseM [ leg ] n ix [ aid ] (Some (canon node)) desc None))
                 | _ -> Error $"{desc}: the operand is not a stored array named by a variable")
            | IRProdSum args ->
                let desc = $"""prodsum({args |> List.map showAtom |> String.concat ", "})"""
                let typed =
                    args |> List.map (fun a ->
                        match a with
                        | IRVar (id, ArrayElem at) ->
                            (match plainAxis at with
                             | Some (n, ix) when at.ElemType = IRTScalar ETFloat64 -> Some (id, at, n, ix)
                             | _ -> None)
                        | _ -> None)
                if args.Length < 1 || typed |> List.exists Option.isNone then
                    Error $"{desc}: not Float64 rank-1 stored arrays over one plain literal-extent axis"
                else
                let typed = typed |> List.map Option.get
                let (_, _, n0, ix0) = typed.Head
                if typed |> List.exists (fun (_, _, n, _) -> n <> n0) then Error $"{desc}: operand extents differ"
                else
                match typed |> List.tryPick (fun (id, _, _, _) -> operandProblem id) with
                | Some why -> Error $"{desc}: {why}"
                | None ->
                let f64 = IRTScalar ETFloat64
                let addLam () =
                    let a = builder.FreshId()
                    let b = builder.FreshId()
                    let parms : IRParam list =
                        [ { Name = "__sfa"; Type = f64; Index = 0; VarId = a }; { Name = "__sfb"; Type = f64; Index = 1; VarId = b } ]
                    { mkLambdaCallable builder parms (IRBinOp (IRElementwise, IRAdd, IRVar (a, f64), IRVar (b, f64), SrcLoc.Nowhere)) f64 [] false [] [] false false 256 false
                        with SourceName = anonymousCallableName (Some subject) "(+)" }
                let leafF () =
                    let pids = args |> List.map (fun _ -> builder.FreshId())
                    let parms : IRParam list = pids |> List.mapi (fun i p -> { Name = $"__sfp{i}"; Type = f64; Index = i; VarId = p })
                    let prod =
                        pids |> List.map (fun p -> IRVar (p, f64))
                        |> List.reduce (fun l r -> IRBinOp (IRElementwise, IRMul, l, r, SrcLoc.Nowhere))
                    let lam =
                        { mkLambdaCallable builder parms prod f64 [] false [] [] false false 256 false
                            with SourceName = anonymousCallableName (Some subject) "lambda" }
                        |> mint origin
                    let ats = typed |> List.map (fun (_, at, _, _) -> at)
                    let ids = args |> List.map (coIterIdentityOf builder)
                    let k = args.Length
                    let mf : MethodForInfo =
                        { Arrays = args; Identities = ids; ArrayTypes = ats; SDimsPerArray = List.replicate k 1
                          TotalSDims = k; SharedIndexTypes = [ ix0 ] }
                    IRApplyCombinator
                        { Loop = IRMethodFor mf
                          Kernel = IRVar (lam.Id, mkFuncArrow (List.replicate k f64) f64)
                          Arrays = args; Identities = ids; ArrayTypes = ats; SharedIndexTypes = [ ix0 ]
                          SymcomStates = List.replicate k SCNeither; TriangularLevels = List.replicate k false
                          SDimsPerArray = List.replicate k 1; KernelInputRanks = List.replicate k 0; KernelOutputRank = 0
                          KernelTDims = []; SpeedupFactor = 1L; ReynoldsSpeedup = 1L; HasReynolds = false
                          OutputType = IRTArrow ([ SIdx ix0 ], f64, None); IsCoIteration = k > 1 }
                let leg =
                    { Build = (fun () ->
                        let leaf = leafF ()
                        let add = addLam () |> mint origin
                        (leaf, IRVar (add.Id, mkFuncArrow [ f64; f64 ] f64), IRLit (IRLitFloat 0.0)))
                      SeedNote = None; Licensed = true; Op = Some IRAdd; Threads = false; Audit = Ok (); ResultType = f64; Source = None }
                Ok (baseM [ leg ] n0 ix0 (typed |> List.map (fun (id, _, _, _) -> id)) (Some (canon node)) desc None)
            | IRReduceCompute (comp, k, s) ->
                let leaves = leavesOf comp
                let infos = leaves |> List.choose (function IRApplyCombinator i -> Some i | _ -> None)
                let ks = match k with IRTuple ks -> ks | k -> List.replicate leaves.Length k
                let ss = match s with IRTuple ss -> ss | s -> List.replicate leaves.Length s
                let isJoin = leaves.Length > 1
                let desc =
                    let ops = infos |> List.collect (fun i -> i.Arrays) |> List.map showAtom |> List.distinct |> String.concat ", "
                    if isJoin then $"a join of {leaves.Length} folds over {{{ops}}}" else $"reduce(<map over {ops}>, ..)"
                if infos.Length <> leaves.Length then Error $"{desc}: a leaf is a named deferred map (one level of sharing only)"
                elif ks.Length <> leaves.Length || ss.Length <> leaves.Length then Error $"{desc}: an unexpected join encoding"
                elif not isJoin && epilogueReserved ui ord node then Error $"{desc}: left for the producer epilogue"
                else
                let pls = infos |> List.map plainLeaf
                if pls |> List.exists Option.isNone then
                    if infos |> List.forall (fun i -> (plainLeafWith false i).IsSome) then
                        Error $"{desc}: a summand may fail or has effects (integer `/` `%%`, a float->int cast, a subscript, lgamma, output, an unknown callee): interleaving it could change which failure comes first"
                    else
                        Error $"{desc}: a leaf is not a plain dense map over stored arrays on one literal-extent axis"
                else
                let pls = pls |> List.map Option.get
                let (n0, ix0, _, _) = pls.Head
                if pls |> List.exists (fun (n, _, _, _) -> n <> n0) then Error $"{desc}: leaf extents differ"
                else
                let ids = pls |> List.collect (fun (_, _, ids, _) -> ids) |> List.distinct
                match ids |> List.tryPick operandProblem with
                | Some why -> Error $"{desc}: {why}"
                | None ->
                let kcs = ks |> List.map kernelOf
                if kcs |> List.exists (function Some c -> not (kernelTotal c 2) | None -> true) then
                    Error $"{desc}: a fold kernel may fail, has effects or does not resolve"
                elif ss |> List.exists (sfTotal Set.empty >> not) then Error $"{desc}: a seed may fail or has effects"
                else
                // A join must be let-bound, its tuple only ever projected. A
                // PER-LEG join types as a FLAT tuple (structural index = leaf
                // index); a SHARED-fold tree types as NESTED PAIRS, where a
                // structural projection `t[1]` names a subtree, not leg 1 --
                // v1 admits a shared tree only when every projection is a
                // flat leaf index.
                let perLeg = match s with IRTuple ss when ss.Length >= 2 -> true | _ -> false
                let joinId =
                    if not isJoin then Ok None
                    else
                        match u.Binder with
                        | Some jid when ord = 0 && (match u.Value with IRReduceCompute _ -> true | _ -> false) ->
                            let mutable all = 0
                            let mutable proj = 0
                            let mutable structural = 0
                            for v in tentative do
                                iterIRExpr (fun x ->
                                    match x with
                                    | IRVar (y, _) when y = jid -> all <- all + 1
                                    | ProjOfNamed (y, _, k, isFlat) when y = jid ->
                                        proj <- proj + 1
                                        // (two leaves: the pair IS the flat tuple)
                                        if (not isFlat && leaves.Length > 2) || k >= leaves.Length then structural <- structural + 1
                                    | _ -> ()) v.Value
                            if all <> proj || capturedIds.Contains jid then
                                Error $"{desc}: the join's tuple is used whole, not only projected"
                            elif not perLeg && structural > 0 then
                                Error $"{desc}: a shared-fold tree read by a structural (nested-pair) projection; v1 joins only flat leaf projections"
                            else Ok (Some jid)
                        | _ -> Error $"{desc}: an existing join that is not let-bound on its own"
                match joinId with
                | Error why -> Error why
                | Ok join ->
                let legs =
                    List.zip3 infos (List.zip ks (kcs |> List.map Option.get)) (List.zip ss pls)
                    |> List.map (fun (info, (kx, c), (sx, (_, _, _, leafK))) ->
                        let resTy = if isJoin then typeOf sx else typeOf node
                        let audit =
                            Blade.IRTotality.auditApply (resolveCallableIn funcs) (fun _ -> None)
                                (fun id -> deferred.Contains id || deferred.Contains (resolveAlias 64 id) || classifiedDeferred id) info
                        mkLeg (fun () -> IRApplyCombinator info) kx c sx None leafK.IsOmpParallel audit resTy)
                Ok (baseM legs n0 ix0 ids (if isJoin then None else Some (canon node)) desc join)
            | _ -> Error "not a fold"

        let admitted = ResizeArray<SfMember>()
        let rejected = ResizeArray<string>()
        if imperative && not topLevel.Value then
            rejected.Add "the region assigns or loops imperatively (a write could fall between the folds)"
        else
            for (ui, ord, node) in found do
                match (if topLevel.Value && (match node with IRReduceCompute (comp, _, _) -> (leavesOf comp).Length > 1 | _ -> false)
                       then Error "an existing top-level join keeps its own binding (the top-level region dissolves no join in v1)"
                       else admit ui ord node) with
                | Ok m -> admitted.Add m
                | Error why -> rejected.Add why

        // Grouping: greedy from the earliest member, by extent, availability
        // and the write barrier over the whole interval.
        let groups = ResizeArray<SfMember list>()
        // A group SPLIT by reorder licence (gap G107 (2)): the note its
        // record carries.
        let splitNote = System.Collections.Generic.Dictionary<SfMember list, string>(HashIdentity.Reference)
        let licenceArm = siblingFoldsLicenceArm ()
        let mutable remaining = admitted |> List.ofSeq |> List.sortBy (fun m -> (m.Unit, m.Ordinal))
        while not remaining.IsEmpty do
            let first = remaining.Head
            let p = first.Unit
            let availableAt (m: SfMember) =
                m.FreeVars |> Set.forall (fun v -> match boundAt.TryGetValue v with | true, b -> b < p | _ -> true)
            let crossesWrite (m: SfMember) = seq { p .. m.Unit } |> Seq.exists (fun i -> unitWrites.[i])
            let ok (m: SfMember) =
                m.Extent = first.Extent && availableAt m && not (crossesWrite m)
            for m in remaining.Tail do
                if m.Extent = first.Extent && not (ok m) then
                    if not (availableAt m) then
                        rejected.Add $"{m.Desc}: an operand, seed or capture is bound after {first.Desc}, so the two cannot share one join point"
                    else
                        rejected.Add $"{m.Desc}: a statement between {first.Desc} and it may write (a call or an assignment), so it cannot move to one join point"
            let firstOk =
                first.FreeVars |> Set.forall (fun v -> match boundAt.TryGetValue v with | true, b -> b < p | _ -> true)
            let take, rest =
                if not firstOk then [], remaining.Tail
                else
                    let others = remaining.Tail |> List.filter ok
                    let taken = others |> List.map (fun m -> (m.Unit, m.Ordinal)) |> Set.ofList
                    let rest = remaining.Tail |> List.filter (fun m -> not (Set.contains (m.Unit, m.Ordinal) taken))
                    (first :: others), rest
            if take.Length >= 2 then
                // MIXED LICENCES in a reordering build (gap G107 (2)): one
                // traversal has one order, so a join of licensed and strict
                // legs would serialize the licensed ones. The default arm
                // SPLITS the group by licence -- the licensed legs share one
                // traversal (their lane / simd form), the strict legs another
                // (each its own ascending chain) -- so k statistics take two
                // passes, not k. A member that is itself mixed (an existing
                // join) keeps the whole-group decline. `strict` = the opt-in
                // arm joining every leg in ONE strict traversal.
                let legsOfTake = take |> List.collect (fun m -> m.Legs)
                let mixed =
                    reorderingBuild && (legsOfTake |> List.exists (fun l -> l.Licensed)) && (legsOfTake |> List.exists (fun l -> not l.Licensed))
                let uniform (m: SfMember) =
                    (m.Legs |> List.forall (fun l -> l.Licensed)) || (m.Legs |> List.forall (fun l -> not l.Licensed))
                if mixed && licenceArm = "split" && take |> List.forall uniform then
                    let (lic, strict) = take |> List.partition (fun m -> m.Legs.Head.Licensed)
                    let showAll (ms: SfMember list) = ms |> List.map (fun m -> m.Desc) |> String.concat ", "
                    for (part, other, label, otherLabel) in [ (lic, strict, "licensed", "strict"); (strict, lic, "strict", "licensed") ] do
                        // two MEMBERS at least: a lone member that is itself an existing
                        // join would be dissolved and re-minted identically (no sharing gained)
                        if part.Length >= 2 then
                            groups.Add part
                            splitNote.[part] <-
                                $"split by reorder licence (G107): the {label} legs share this traversal; the {otherLabel} legs ({showAll other}) are judged as their own group -- one traversal has one order, so a mixed join would serialize the licensed legs (BLADE_SIBLING_FOLDS=strict joins every leg in one strict traversal instead)"
                        else
                            for m in part do
                                rejected.Add $"{m.Desc}: the only {label} leg beside {otherLabel} siblings ({showAll other}); split by reorder licence (G107), it keeps its own pass"
                elif mixed && licenceArm = "strict" && take |> List.forall uniform then
                    groups.Add take
                    splitNote.[take] <- "BLADE_SIBLING_FOLDS=strict: every leg in ONE strict traversal (each its own ascending chain from its own seed; the licensed legs give up their lane / simd form for the shared pass)"
                else groups.Add take
            remaining <- rest

        // Decide each group.
        let decideGroup (g: SfMember list) : Result<unit, string> =
            let legs = g |> List.collect (fun m -> m.Legs)
            // THE THREAD GATE, relaxed to the threaded join's own policy (G7):
            // in a threading licensed build a requesting leg may join only when
            // EVERY leg carries its own licence and the threaded join is on --
            // then the synthesized join keeps each leg's own request (none is
            // manufactured) and threads under the joined Path B form.
            // Otherwise the requesting leg would lose its team: decline.
            // The synthesized traversal must pass the threaded join's ACTUAL
            // admission (the same knob, licence, operator and purity predicates
            // `joinedFoldThreadPlan` applies; the dense literal-extent shape is
            // this pass's own admission) BEFORE it replaces a requesting fold:
            // a join the emitter would then refuse would serialize a fold
            // that threads on its own today. On any failure the originals stay.
            let threadedJoinRefusal () : string option =
                let ops = legs |> List.map (fun l -> l.Op) |> List.distinct
                if not (Blade.Toolchain.joinThreadsEnabled ()) then
                    Some "a leg requests threads in this build and BLADE_JOIN_THREADS=0 keeps a reduction join serial; the leg keeps its own team"
                elif not (legs |> List.forall (fun l -> l.Licensed)) then
                    Some "a leg requests threads in this build but not every leg carries its own reorder licence; the threaded join (G7) needs every leg licensed, so the requesting leg keeps its own team"
                elif not (ops = [ Some IRAdd ] || ops = [ Some IRMul ]) then
                    Some "a leg requests threads in this build but the legs do not all fold with ONE builtin `+` or `*`; the threaded join (G7) has no form for that, so the requesting leg keeps its own team"
                else
                    match legs |> List.tryPick (fun l -> match l.Audit with Error why -> Some why | Ok () -> None) with
                    | Some why ->
                        Some $"a leg requests threads in this build but a leg's map kernel is outside the threaded join's purity/totality whitelist ({why}); the joined team would be refused, so the requesting leg keeps its own team"
                    | None -> None
            let threadRefusal =
                if threadsBuild && legs |> List.exists (fun l -> l.Threads) then threadedJoinRefusal () else None
            if threadRefusal.IsSome then
                Error threadRefusal.Value
            elif reorderingBuild && (legs |> List.exists (fun l -> l.Licensed)) && (legs |> List.exists (fun l -> not l.Licensed))
                 && not (splitNote.ContainsKey g && licenceArm = "strict") then
                let nl = legs |> List.filter (fun l -> l.Licensed) |> List.length
                Error $"legs disagree on the reorder licence in a reordering build ({nl} licensed, {legs.Length - nl} unlicensed); one traversal has one order, so the join would serialize the licensed legs"
            elif reorderingBuild && (legs |> List.forall (fun l -> l.Licensed))
                 && (legs |> List.map (fun l -> l.Op) |> List.distinct) |> (fun ops -> ops.Length <> 1 || ops.Head.IsNone) then
                Error "the licensed legs fold with different or non-builtin operators; the joined C++ form takes one builtin operator for every leg"
            else Ok ()

        // Distinct legs of a group (duplicates read one leg) and each member's
        // leg indexes.
        let layout (g: SfMember list) =
            let legs = ResizeArray<SfLeg>()
            let keys = ResizeArray<IRExpr option>()
            let idx =
                g |> List.map (fun m ->
                    match m.Key, m.Legs with
                    | Some key, [ leg ] ->
                        (match keys |> Seq.tryFindIndex (fun k -> k = Some key) with
                         | Some i -> [ i ]
                         | None ->
                            legs.Add leg
                            keys.Add(Some key)
                            [ legs.Count - 1 ])
                    | _, ls ->
                        ls |> List.map (fun l ->
                            legs.Add l
                            keys.Add None
                            legs.Count - 1))
            (List.ofSeq legs, idx)

        let inlineKeyOf (k: SfUnitKey) = match k with SfInl (i, c, _) -> Some (i, c) | SfOrig _ -> None
        let inlineDisp (key: int * int) = match inlineCache.TryGetValue key with | true, Ok (_, _, d) -> d | _ -> "?"
        let evidenceOf (now: bool) (g: SfMember list) (legs: SfLeg list) (dups: int) : string list =
            let opNames = g |> List.collect (fun m -> m.Operands) |> List.distinct |> List.map nameOf |> String.concat ", "
            let first = g.Head
            let inl =
                g |> List.choose (fun m -> inlineKeyOf m.UnitKey) |> List.distinct
                |> List.map (fun k ->
                    if now then $"{inlineDisp k} inlined (pure-total, scalar return)"
                    else $"{inlineDisp k} would be inlined (pure-total, scalar return); left as a call")
            let allNotes = legs |> List.mapi (fun j l -> (j, l.SeedNote)) |> List.choose (fun (j, n) -> n |> Option.map (fun x -> (j, x)))
            let isSelect (_, (o: string, _)) = o.EndsWith "-select"
            let notes = allNotes |> List.filter (isSelect >> not)
            let selectNotes = allNotes |> List.filter isSelect
            let selectLines =
                if selectNotes.IsEmpty then []
                else
                    [ "unseeded running max / min select legs (G107): the seed e (-inf for `if a > b then a else b`, +inf for `if a < b then a else b`, the integer MIN / MAX) is a LEFT IDENTITY bit for bit -- f(e, x) = x for every x, NaN and signed zeros included -- so each seeded chain is the unseeded first-cell chain exactly; the extent is a literal >= 1, so the empty-fold BL8003 cannot arise" ]
                    @ (selectNotes |> List.map (fun (j, (o, s)) -> $"leg {j}: unseeded {o} -> seed {s} (left identity; bit-exact)"))
            let seedLines =
                selectLines @
                if notes.IsEmpty then []
                else
                    let ops = notes |> List.map (fun (_, (o, _)) -> o) |> List.distinct |> String.concat " / "
                    let seeds = notes |> List.map (fun (_, (_, s)) -> s) |> List.distinct |> String.concat " / "
                    [ $"unseeded {ops} legs seeded {seeds}: bit-exact for every reachable value; signalling-NaN state is not preserved and is outside the arithmetic contract (formalism 2.4); the per-leg identity seed is exact, so the joined result equals the separate folds' under serial (repro) evaluation -- a licensed build may reassociate and lose the sign of an exact zero" ]
                    @ (notes |> List.map (fun (j, (o, s)) -> $"leg {j}: unseeded {o} -> seed {s} (identity; bit-exact)"))
            let lic = legs |> List.map (fun l -> if l.Licensed then "licensed" else "unlicensed") |> String.concat ", "
            [ $"{legs.Length} folds over {{{opNames}}} share one traversal ({first.Axis}: {first.Extent} cells)" ]
            @ (if dups > 0 then [ $"{dups} repeated fold(s) read one leg" ] else [])
            @ inl @ seedLines @ [ $"per-leg licences kept: {lic}" ]

        let resolveNames (s: string) =
            System.Text.RegularExpressions.Regex.Replace(s, "\u0002(-?[0-9]+)\u0003", fun m -> nameOf (int m.Groups.[1].Value))
        let record (outcome: Blade.Effects.DecisionOutcome) (ev: string list) =
            let outcome =
                match outcome with
                | Blade.Effects.Declined why -> Blade.Effects.Declined (resolveNames why)
                | o -> o
            Blade.Effects.Decisions.record
                { Blade.Effects.Rule = "sibling-folds"; Version = 1
                  Span = Blade.Ast.noSpan; Subject = subject
                  Outcome = outcome; Evidence = ev |> List.map resolveNames }

        let applied = ResizeArray<SfMember list * SfLeg list * int list list>()
        let mutable anyGroupRecord = false
        // Lifetime capability is a decision about one attempted inline, not
        // about the independent sibling groups that may still be joined.
        // judgeAt memoizes (statement, call ordinal), so enumerate that cache
        // once: discovery/reconstruction cannot duplicate the refusal, and
        // textually identical calls at different positions remain distinct.
        for KeyValue ((statement, ordinal), result) in inlineCache do
            match result with
            | Error why when why.Contains "lifetime event not honoured by"
                             && (match refusedCallFolds.TryGetValue((statement, ordinal)) with true, n -> n > 0 | _ -> false) ->
                anyGroupRecord <- true
                record (Blade.Effects.Declined why)
                    [ $"retained-inline candidate at region statement {statement + 1}, call {ordinal + 1}: the original call remains; independent sibling groups may still share a traversal" ]
            | _ -> ()
        for g in groups do
            let (legs, idx) = layout g
            let memberCount = g |> List.sumBy (fun m -> m.Legs.Length)
            let dups = memberCount - legs.Length
            if legs.Length < 2 then
                anyGroupRecord <- true
                record (Blade.Effects.Declined "the folds are one repeated value (let-level CSE's case, not a join)") []
            else
            let extra =
                (match splitNote.TryGetValue g with | true, n -> [ n ] | _ -> [])
                @ (rejected |> Seq.map (fun r -> $"not joined: {r}") |> List.ofSeq) @ refusals
            match decideGroup g with
            | Error why ->
                anyGroupRecord <- true
                record (Blade.Effects.Declined why) (evidenceOf false g legs dups @ extra)
            | Ok () when not enabled ->
                anyGroupRecord <- true
                record (Blade.Effects.Declined "disabled by BLADE_SIBLING_FOLDS") (evidenceOf false g legs dups @ extra)
            | Ok () ->
                anyGroupRecord <- true
                record Blade.Effects.Applied (evidenceOf true g legs dups @ extra)
                applied.Add((g, legs, idx))
        // the TOP-LEVEL region records its decline too once two folds were
        // candidates were FOUND -- admitted or rejected at admission (round 4
        // wave 9, lane A, A4, Codex 0d750baf member 4 and its diff review: a
        // write barrier, or a leg rejected as not total, left no record); a
        // module with fewer candidates stays silent, as before
        if not anyGroupRecord && (not topLevel.Value || Seq.length found >= 2) then
            let reasons = (List.ofSeq rejected) @ refusals
            match reasons with
            | [] ->
                let exts = admitted |> Seq.map (fun m -> string m.Extent) |> Seq.distinct |> String.concat ", "
                record (Blade.Effects.Declined $"no two folds share one dense axis and one join point (extents {exts})") []
            | r :: rest -> record (Blade.Effects.Declined r) rest

        if applied.Count = 0 then body else

        // ---- A7c: producer-input legs (rule `epilogue-legs` v1, gap G69) -----
        // An applied group whose every leg is `reduce(a, k[, s])` of a stored
        // array, after a rank-1 producer of this region whose cell epilogue
        // B1 will take (`epilogueReserved`: an original statement, or a let of
        // a call body the tentative spine inlined -- saxpy's `kernel(x, y)`)
        // and whose operands (or output) the legs read, is judged by
        // producerEpilogueLegAdmission. Admitted with the gate on, the call
        // holding the producer is inlined FOR the legs (the region then has
        // no call boundary between them), and the plan is recorded once the
        // join has its id (below). Nothing else changes: the join stays in
        // the IR, so the interpreter -- and any lane that does not carry the
        // legs -- runs it as written.
        let legPlans = System.Collections.Generic.Dictionary<int, IRId * ApplyInfo * EpilogueLegSpec list * string option>()
        // One entry per inlined CALL kept for legs (several plans may share it):
        // its pools, released once at the call's return boundary.
        let legInlines = System.Collections.Generic.Dictionary<int * int, (IRId * IRType) list * string>()
        if cellEpilogueOn () && not topLevel.Value then
            let legFacts : EpilogueLegFacts =
                let schedWrites = scheduleMayWriteIn programFuncs modul
                let schedWritesCb = scheduleMayWriteWithCallbacksIn programFuncs modul
                let assignsTo (id: IRId) (e: IRExpr) =
                    let mutable hit = false
                    iterIRExpr (fun n ->
                        match n with
                        | IRAssign (target, _) when Set.contains id (collectVarRefsIR target) -> hit <- true
                        | _ -> ()) e
                    hit
                { Resolve = (fun e -> resolveCallableIn funcs (fst (peelReynolds e)))
                  Writes = (fun e -> schedWrites e || mayWrite e)
                  CallbackWrites = schedWritesCb
                  Repeatable = (fun e -> (facts e).Repeatable)
                  SeedTotal = sfTotal Set.empty
                  PayloadOf = intervalPayloadOf modul
                  ProviderEffect = intervalHasProviderEffect modul
                  AssignedAnywhere = (fun id ->
                      (modul.Bindings |> List.exists (fun b -> bindingPayload modul b |> List.exists (assignsTo id)))
                      || (modul.Functions |> List.exists (fun f -> assignsTo id f.Body))
                      || (tentative |> Array.exists (fun u -> assignsTo id u.Value)))
                  ThreadsBuild = Blade.Toolchain.ompThreadEmissionEnabled ()
                  FreeVarsOf = freeVarsOf
                  OperandNotFresh = (fun id ->
                      let fresh (v: IRExpr) =
                          match v with
                          | IRCompute _ | IRArrayLit _ -> None
                          | CallOfNamed (f, _, _, _) when Map.containsKey f funcs -> None
                          | IRVar (y, _) -> Some $"an alias of `{nameOf y}`"
                          | CallOfNamed (y, _, _, _) -> Some $"a named view into `{nameOf y}`"
                          | _ -> Some "bound to a value that is not fresh storage of its own"
                      match boundAt.TryGetValue id with
                      | true, ui -> fresh tentative.[ui].Value
                      | _ ->
                          if regionParams.Contains id then None
                          else
                              match modul.Bindings |> List.tryFind (fun b -> b.Id = id) with
                              | Some b -> fresh b.Value
                              | None -> Some "bound outside this region and module")
                  // the region as the pass sees it (its inlined call bodies
                  // under fresh ids) joins the program's lets
                  IsDeferred =
                      programDeferredId modul (funcs |> Map.toSeq |> Seq.map snd)
                          (tentative |> Seq.map (fun u ->
                              match u.Binder with
                              | Some id -> IRLet (id, u.Value, IRLit IRLitUnit)
                              | None -> u.Value)) }
            let rec collapseC (e: IRExpr) = match e with IRCompute i -> collapseC i | e -> e
            let taken = System.Collections.Generic.HashSet<IRId>()
            let fmtSeed (s: IRExpr) =
                match s with
                | IRLit (IRLitFloat f) when System.Math.Abs f < 1e15 && f = System.Math.Floor f -> sprintf "%.1f" f
                | IRLit (IRLitFloat f) -> sprintf "%.17g" f
                | IRLit (IRLitInt n) -> string n
                | IRVar (id, _) -> nameOf id
                | _ -> "a total expression"
            applied |> Seq.iteri (fun gi (g, legs, _) ->
                if legs |> List.forall (fun l -> l.Source.IsSome) then
                    let joinAt = (g |> List.minBy (fun m -> (m.Unit, m.Ordinal))).Unit
                    let legOps = legs |> List.map (fun l -> let (a, _, _) = l.Source.Value in a)
                    let producers =
                        [ 0 .. tentative.Length - 1 ]
                        |> List.choose (fun ci ->
                            if not (epilogueReserved ci 0 tentative.[ci].Value) then None
                            else
                                match oneCellConsumerOf tentative.[ci].Value, tentative.[ci].Value with
                                | Some (pId, cinfo, ck), IRReduceCompute (_, ck2, cseed) ->
                                    (match boundAt.TryGetValue pId with
                                     | true, pu when pu < joinAt -> Some (pu, pId, (cinfo, ck, ck2, cseed))
                                     | _ -> None)
                                | _ -> None)
                        |> List.distinctBy (fun (pu, pId, _) -> (pu, pId))
                    let reads (pu: int, pId: IRId, b1) =
                        match collapseC tentative.[pu].Value with
                        | IRApplyCombinator pinfo ->
                            let ops = pinfo.Arrays |> List.choose (function IRVar (id, _) -> Some id | _ -> None)
                            if legOps |> List.exists (fun a -> a = pId || List.contains a ops) then Some (pu, pId, pinfo, b1) else None
                        | _ -> None
                    match producers |> List.choose reads |> List.sortBy (fun (pu, _, _, _) -> -pu) with
                    | [] -> ()
                    | (pu, pId, pinfo, (cinfo, ck, fkExpr, b1Seed)) :: _ ->
                    let pName = nameOf pId
                    let specs =
                        legs |> List.map (fun l ->
                            let (a, k, s) = l.Source.Value
                            let opName = nameOf a
                            let suffix = match l.Op with Some IRAdd -> "sum" | Some IRMul -> "prod" | _ -> "fold"
                            let seedText = match l.SeedNote with Some (_, txt) -> txt | None -> fmtSeed s
                            let spec : EpilogueLegSpec =
                                { Name = $"{opName}-{suffix}"; OperandId = a; OperandName = opName; Kernel = k; Seed = s
                                  SeedText = seedText; Licensed = reorderingBuild && l.Licensed }
                            spec)
                    let names = specs |> List.map (fun s -> s.Name) |> String.concat ", "
                    // Finding 5: a builtin `(+)` / `(*)` leg carries its OWN licence
                    // (formalism 2.4's recognised builtins), so it is strict only in
                    // the repro build; the record says which, per build.
                    let form =
                        if specs |> List.forall (fun s -> not s.Licensed) then "strict chains, ascending, own seeds"
                        elif specs |> List.forall (fun s -> s.Licensed) then
                            "builtin-licensed: each leg its own licence, its own K-lane state (C++) / fold flags (LLVM) and seed, never another leg's or the map's; strict under repro"
                        else "strict legs as ascending chains, licensed legs as their own K-lane states, each its own seed"
                    let headline = $"legs {names} ride {pName}'s producer pass ({form})"
                    let licenceNote =
                        if not reorderingBuild then "licences: the repro build keeps every leg strict (a builtin `(+)` / `(*)` leg is licensed only outside repro)"
                        else
                            specs |> List.map (fun s -> $"{s.Name}: " + (if s.Licensed then "licensed (its own: a builtin or commutative kernel)" else "strict (no licence of its own)")) |> String.concat ", " |> sprintf "licences: %s"
                    let region : EpilogueLegRegion =
                        { Subject = subject; ProducerName = pName; ProducerId = pId; ProducerInfo = pinfo
                          Stmts = tentative |> Array.map (fun u ->
                                    (u.Binder, (match u.Binder with Some id -> nameOf id | None -> "the region's result"), u.Value))
                          ProducerAt = pu; JoinAt = joinAt
                          MemberAts = g |> List.map (fun m -> m.Unit) |> List.distinct |> List.sort
                          Legs = specs }
                    let inlineKey = match tentative.[pu].Key with SfInl (i, c, _) -> Some (i, c) | SfOrig _ -> None
                    // The inlined callee's ARRAY lets (its stored pools), freed at the
                    // call's lifetime region (IRBeginPoolScope .. IRReleasePools), in both lanes.
                    let inlinedPools =
                        match inlineKey with
                        | Some key ->
                            tentative |> Array.toList |> List.choose (fun u ->
                                match u.Key, u.Binder, u.Value with
                                | SfInl (i, c, _), Some id, (IRCompute _ as v) when (i, c) = key -> Some (id, typeOf v)
                                | _ -> None)
                        | None -> []
                    let inlNote =
                        match inlineKey with
                        | Some key ->
                            let pools = inlinedPools |> List.map (fst >> nameOf) |> String.concat ", "
                            [ $"{inlineDisp key} inlined one level so the producer and the legs share one region (no call boundary between them) -- the OPT-IN arm, retained because BLADE_EPILOGUE_LEGS=1 is explicit (its lifetime audit is INCOMPLETE, G69: a fold kernel named by a non-owned unit is not audited; each lane that RETAINS it warns BL7005 on stderr, once, at the call; the planned closure is the scope model A, a later item); its pools ({pools}) are released ONCE by an IR-level lifetime region (IRBeginPoolScope before the inlined spine, IRReleasePools after the rebuilt spine and its evaluated scalar return, also behind a deleted final join) -- where the as-written call returned; CSE and scratch reuse exclude the event-bearing body, IRValidate checks the scope pairing, the exact pool ownership and every later read (directly, through storage / deferred aliases, captures and carried extents), and both statement-rendered lanes release there independently of join placement (C++: the tracked free of each named dense pool, or main's dense free; LLVM: a nested pool-tracking scope opened at the begin and popped at the release). Scope (fail closed): the callee was admitted because EVERY let of its inlined spine is a forced rank-1 dense map (an owned pool, listed) or an allocation-free value -- its type and the type of every let, branch, match, tuple, projection and compute its evaluation reaches is a numeric / Bool / complex scalar, a unit, an index label whose INNER type is one of those, or a tuple of them (no String, no bare Nat), it reads no String-typed value and evaluates no String literal, and it calls NO user function directly in any position (builtin operators -- arithmetic, libm, casts, extents, folds and maps -- are not calls; callee bodies, and the bodies of fold / map KERNELS it names, are NOT audited -- the known unaudited case) -- and it is the only inlinable call of its statement; any other callee, or a second call in the statement, is not retained for A7c (independent sibling-fold inlining is unchanged)" ]
                        | None -> []
                    // FINDING 4: the inline is kept only when BOTH lanes' fused forms
                    // can carry the legs in this build -- the facts each emitter's own
                    // gate reads (CodeGenCuda.buildFlatEpilogue / attachEpilogueLegs,
                    // EmitLlvm.tryBindFlatEpilogueProducer), stated here so a planned
                    // inline never outlives a route no lane emits.
                    let routeProblem () : string option =
                        let fk = kernelOf fkExpr
                        let elemOf (t: IRType) = match stripUnits t with IRTScalar et -> Some et | ArrayElem at -> (match stripUnits at.ElemType with IRTScalar et -> Some et | _ -> None) | _ -> None
                        let realFloat (et: ElemType option) = (et = Some ETFloat64 || et = Some ETFloat32)
                        let lanesOff () =
                            match System.Environment.GetEnvironmentVariable "BLADE_FOLD_LANES" with
                            | null -> false
                            | v -> List.contains (v.Trim().ToLowerInvariant()) [ "0"; "off"; "false"; "no" ]
                        match fk with
                        | None -> Some "the producer's epilogue fold kernel does not resolve"
                        | Some fk ->
                        let b1Op = builtinOp fk
                        let b1Licensed = reorderingBuild && licensed fk
                        let b1Elem = elemOf fk.RetType
                        let cellElem = elemOf pinfo.OutputType
                        let b1Request = fk.IsOmpParallel || ck.IsOmpParallel
                        match b1Seed with
                        | IRLit _ when b1Op = Some IRAdd || b1Op = Some IRMul ->
                            if cellElem <> b1Elem then Some "the producer's cell type differs from its epilogue fold's accumulator type (no lane fuses it)"
                            elif b1Licensed && b1Op <> Some IRAdd then Some "the epilogue fold is licensed but not a builtin `+` (the C++ lane-state form takes `+` only)"
                            elif b1Licensed && not (realFloat b1Elem) then Some "the epilogue fold is licensed over a non-floating element (no lane-state form)"
                            elif b1Licensed && lanesOff () then Some "the epilogue fold is licensed and BLADE_FOLD_LANES=off (the C++ lane keeps its two-pass fold)"
                            elif blasGateProbe () && not b1Request then Some "the BLAS gate is on and the epilogue fold carries no omp request (the C++ L1 dispatch may own it, in every build)"
                            else
                            specs |> List.tryPick (fun s ->
                                let lk = kernelOf s.Kernel
                                let lElem = lk |> Option.bind (fun c -> elemOf c.RetType)
                                let opElem =
                                    pinfo.Arrays |> List.tryFindIndex (function IRVar (id, _) -> id = s.OperandId | _ -> false)
                                    |> Option.bind (fun pos -> List.tryItem pos pinfo.ArrayTypes)
                                    |> Option.bind (fun at -> match stripUnits at.ElemType with IRTScalar et -> Some et | _ -> None)
                                match lk with
                                | None -> Some $"leg {s.Name}'s kernel does not resolve"
                                | Some lc ->
                                if s.Licensed && not b1Licensed then Some $"leg {s.Name} is licensed but the epilogue keeps its ordered chain in this build (v1 feeds a licensed leg only beside the lane-state form)"
                                elif s.Licensed && (builtinOp lc <> Some IRAdd || not (realFloat lElem) || lanesOff ()) then Some $"leg {s.Name} is licensed but takes no lane-state form (not a floating builtin `+`, or BLADE_FOLD_LANES=off)"
                                elif lElem.IsNone || lElem <> opElem then Some $"leg {s.Name}'s accumulator type is not its operand's element type"
                                else None)
                        | IRLit _ -> Some "the producer's epilogue fold kernel is not a builtin `+` / `*` (both lanes' fused loops evaluate it ahead of the intervening bindings)"
                        | _ -> Some "the producer's epilogue fold seed is not a literal (both lanes' fused loops evaluate it ahead of the producer)"
                    match producerEpilogueLegAdmission legFacts region with
                    | Error (why, rows) ->
                        recordEpilogueLegs subject (Blade.Effects.Declined (resolveNames why)) ((headline :: rows) |> List.map resolveNames)
                    | Ok rows when not (epilogueLegsEnabled ()) ->
                        recordEpilogueLegs subject (Blade.Effects.Declined "disabled by BLADE_EPILOGUE_LEGS") ((headline :: rows) |> List.map resolveNames)
                    // W2-4 round 8 (the seventh look, NEW 1 not fixed -- the user's decision):
                    // the kept one-level inline is OPT-IN. A plan that needs it declines under
                    // unset; a plan whose producer is already in the region is unaffected.
                    | Ok rows when inlineKey.IsSome && not (epilogueLegsInlineOptIn ()) ->
                        recordEpilogueLegs subject (Blade.Effects.Declined epilogueInlineOptInDecline) ((headline :: rows) |> List.map resolveNames)
                    | Ok rows when taken.Contains pId ->
                        recordEpilogueLegs subject (Blade.Effects.Declined $"another join's legs already ride `{pName}`'s producer pass") ((headline :: rows) |> List.map resolveNames)
                    | Ok rows ->
                    // ROUND 5, FAIL CLOSED (fourth review NEW 1, 3, 4): the kept inline's
                    // return-boundary event releases exactly the callee's OWNED
                    // allocations, so the callee must allocate nothing else: EVERY let of
                    // its inlined spine is either a forced rank-1 dense map (IRCompute of
                    // a map -- an owned pool, listed) or a value that holds no storage (a
                    // scalar or a tuple of scalars, e.g. a fold or a join's result) and is
                    // no deferred form. An array alias / rename (the LLVM lane copies it
                    // into an unlisted pool), a loop object, a deferred map, an array
                    // tuple or projection, or any other array-valued let declines. And
                    // one inlinable call per statement: several spines before one
                    // statement would share one delayed boundary.
                    let ownershipProblem () : string option =
                        match inlineKey with
                        | None -> None
                        | Some (i, c) ->
                            let call = inlineDisp (i, c)
                            if tentative |> Array.exists (fun u -> match u.Key with SfInl (i2, c2, _) -> i2 = i && c2 <> c | SfOrig _ -> false) then
                                Some $"the statement holding {call} holds another inlinable call (v1 places one return boundary per statement)"
                            else
                                tentative |> Array.tryPick (fun u ->
                                    match u.Key with
                                    | SfInl (i2, c2, _) when (i2, c2) = (i, c) ->
                                        let v = u.Value
                                        let owned =
                                            match v with
                                            | IRCompute inner ->
                                                (match collapseC inner with
                                                 | IRApplyCombinator _ ->
                                                    (match stripUnits (typeOf v) with
                                                     | ArrayElem at ->
                                                        not at.IsVirtual
                                                        && (match at.IndexTypes with
                                                            | [ ix ] -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1
                                                            | _ -> false)
                                                     | _ -> false)
                                                 | _ -> false)
                                            | _ -> false
                                        // Round 6 (fifth review NEW 1-2): ALLOCATION-FREE EVALUATION,
                                        // not a storage-free result type. A non-owned unit's type must
                                        // be an allocation-free VALUE type (numeric scalars, Bool,
                                        // complex, index values, unit, tuples of those -- NOT String,
                                        // whose C++ value owns heap storage), and NO node its
                                        // evaluation reaches may bind, branch to, build or call out a
                                        // value of any other type: a nested let (`if c then { let
                                        // spare = x; .. }`), an if / match arm, a tuple or projection,
                                        // a call result, a compute -- each is judged by its own type (and,
                                        // round 7, no user call is admitted at all; see below).
                                        // Reads of arrays inside folds and maps (their operands) are
                                        // leaves, not bindings.
                                        let nm = match u.Binder with Some id -> nameOf id | None -> "its return"
                                        let shapeProblem () =
                                            let mutable bad : string option = None
                                            iterIRExpr (fun n ->
                                                if bad.IsNone then
                                                    // Round 7 (sixth review NEW 1): EVERY call to a user
                                                    // callable declines -- whatever its result type, in any
                                                    // position (let RHS, tuple component, projection operand,
                                                    // branch arm, seed, arithmetic operand): a lane may expand
                                                    // its body in place (the LLVM lane's tuple-component path
                                                    // opens no pool scope), so a call's expansion may allocate
                                                    // outside the owned pools. Callee bodies are NOT audited.
                                                    // Builtins are IR operators (arithmetic, libm, casts,
                                                    // extents, folds / maps), not calls.
                                                    (match n with
                                                     | CallOfNamed (fid, _, _, _) when Map.containsKey fid funcs ->
                                                        let fnm = (Map.find fid funcs).SourceName
                                                        bad <- Some $"callee let `{nm}` calls {fnm}: a call's expansion may allocate outside the owned pools"
                                                     | IRApp _ ->
                                                        bad <- Some $"callee let `{nm}` applies a value that does not resolve to a known callable"
                                                     // (NEW 2) a String-typed LEAF (a literal, a variable) is an owning value
                                                     | IRLit (IRLitString _) ->
                                                        bad <- Some $"callee let `{nm}` evaluates a String literal (an owning value in the C++ lane)"
                                                     | IRVar (_, t) | IRParam (_, _, t) when (match stripUnits t with IRTScalar _ | IRTIdxTagged _ | IRTTuple _ -> not (allocationFreeValueType t) | _ -> false) ->
                                                        bad <- Some $"callee let `{nm}` reads a value of a type that is not an allocation-free value (a String or a String-valued index label)"
                                                     | _ -> ())
                                                    let judged =
                                                        if bad.IsSome then None
                                                        else
                                                        match n with
                                                        | IRLet (_, bv, _) -> Some (typeOf bv)
                                                        | IRIf _ | IRMatch _ | IRTuple _ | IRTupleProj _ | IRApp _ | IRCompute _ -> Some (typeOf n)
                                                        | _ -> None
                                                    match judged with
                                                    | Some t when not (allocationFreeValueType t) ->
                                                        let kind =
                                                            match n with
                                                            | IRLet _ -> "a nested let" | IRIf _ -> "an if" | IRMatch _ -> "a match"
                                                            | IRTuple _ -> "a tuple" | IRTupleProj _ -> "a projection" | IRApp _ -> "a call" | _ -> "a compute"
                                                        bad <- Some $"callee let `{nm}` evaluates {kind} of a type that is not an allocation-free value (an array, String, loop or function type)"
                                                    | _ -> ()) v
                                            bad
                                        if owned then None
                                        elif not (allocationFreeValueType (typeOf v)) || isDeferredForm v then
                                            Some $"callee let `{nm}` is not an owned allocation form (the inline's boundary releases only forced rank-1 dense maps; every other let must be an allocation-free value)"
                                        else
                                            match shapeProblem () with
                                            | Some why -> Some (why + " -- not an owned allocation form")
                                            | None -> None
                                    | _ -> None)
                    match ownershipProblem () with
                    | Some why ->
                        recordEpilogueLegs subject (Blade.Effects.Declined (resolveNames why + "; the call is not retained for A7c (independent sibling-fold inlining is unchanged)"))
                            ((headline :: rows) |> List.map resolveNames)
                    | None ->
                    match routeProblem () with
                    | Some why ->
                        recordEpilogueLegs subject (Blade.Effects.Declined ("no lane would carry the legs in this build: " + why + "; nothing is inlined for them"))
                            ((headline :: rows) |> List.map resolveNames)
                    | None ->
                        taken.Add pId |> ignore
                        (match inlineKey with
                         | Some key -> legInlines.[key] <- (inlinedPools, inlineDisp key)
                         | None -> ())
                        recordEpilogueLegs subject Blade.Effects.Applied
                            ((headline :: [ licenceNote; "planner admission (each lane's emitter records whether its fused form carried the legs)" ] @ inlNote @ rows) |> List.map resolveNames)
                        legPlans.[gi] <- (pId, pinfo, specs, (inlineKey |> Option.map inlineDisp)))

        // ---- rewrite on the spine with only the contributing inlines ---------
        let keptInlines =
            applied |> Seq.collect (fun (g, _, _) -> g |> List.choose (fun m -> inlineKeyOf m.UnitKey)) |> Set.ofSeq
            |> Set.union (Set.ofSeq legInlines.Keys)
        let finalUnits = buildUnits (fun k -> Set.contains k keptInlines)
        // A01: positions are used only while constructing the IR, never saved
        // as lifetime facts. Emit events AFTER reconstruction, including behind
        // a deleted final join. Every retained inline gets one region (also the
        // older join-driven inline), rather than one region per leg plan.
        // A region opened for a call inlined FOR LEGS (the opt-in A7c kept
        // inline, `legInlines`) is also marked by its first owned pool's binder
        // (never a join member, so the rebuild never drops it), with the call's
        // span: each lane that retains it warns BL7005 there (W2-4 round 9, the
        // user's decision). The older join-driven regions never warn.
        let starts = System.Collections.Generic.Dictionary<SfUnitKey, IRId * string>()
        let ends = System.Collections.Generic.Dictionary<SfUnitKey, IRId * IRExpr list * string>()
        for key in keptInlines do
            let us = finalUnits |> List.filter (fun u -> inlineKeyOf u.Key = Some key)
            let pools =
                us |> List.choose (fun u ->
                    match u.Binder, u.Value with
                    | Some id, IRCompute (IRApplyCombinator _ as v) ->
                        match stripUnits (typeOf v) with
                        | ArrayElem _ -> Some (IRVar (id, typeOf v))
                        | _ -> None
                    | _ -> None)
            if not pools.IsEmpty then
                let sid = builder.FreshId()
                let call = resolveNames (inlineDisp key)
                starts.[us.Head.Key] <- (sid, call)
                ends.[(List.last us).Key] <- (sid, pools, call)
                if legInlines.ContainsKey key then
                    match pools.Head with
                    | IRVar (start, _) ->
                        let span =
                            match callNodes.TryGetValue key with
                            | true, node -> CallSpanTable.tryFind node
                            | _ -> None
                        InlineEventSpans.record start span
                    | _ -> ()
        let replace = System.Collections.Generic.Dictionary<SfUnitKey * int, IRExpr>()
        let dropUnits = System.Collections.Generic.HashSet<SfUnitKey>()
        let projOf = System.Collections.Generic.Dictionary<IRId, IRId list * IRType list>()
        let inserts = System.Collections.Generic.Dictionary<SfUnitKey, (IRId * IRExpr) list>()
        let groupAt = ref -1
        for (g, legs, idx) in applied do
            groupAt.Value <- groupAt.Value + 1
            let built = legs |> List.map (fun l -> l.Build ())
            let comp = built |> List.map (fun (lf, _, _) -> lf) |> List.reduce (fun a b -> IRFusion (a, b))
            let rc = IRReduceCompute (comp, IRTuple (built |> List.map (fun (_, k, _) -> k)), IRTuple (built |> List.map (fun (_, _, s) -> s)))
            let jId = builder.FreshId()
            let jTy = typeOf rc
            let pIds = legs |> List.map (fun _ -> builder.FreshId())
            let pTys = legs |> List.map (fun l -> l.ResultType)
            // A7c: the admitted plan, keyed by the producer and by this join.
            (match legPlans.TryGetValue groupAt.Value with
             | true, (pId, pinfo, specs, inlinedCall) ->
                let opIds = pinfo.Arrays |> List.map (function IRVar (id, _) -> id | _ -> -1)
                let plan : EpilogueLegPlan =
                    { ProducerId = pId; JoinId = jId
                      Legs =
                        List.zip specs pTys |> List.mapi (fun slot (s, ty) ->
                            let pos = opIds |> List.findIndex (fun id -> id = s.OperandId)
                            { Slot = slot; OperandPos = pos
                              OperandId = s.OperandId; Kernel = s.Kernel; Seed = s.Seed; ElemType = ty
                              OperandElem = (match List.tryItem pos pinfo.ArrayTypes with Some at -> at.ElemType | None -> ty)
                              Licensed = s.Licensed; Name = s.Name; SlotId = builder.FreshId() })
                      ProducerName = nameOf pId; Subject = subject
                      InlinedCall = inlinedCall |> Option.map resolveNames
                      EmittedCpp = ref None; DecidedCpp = ref false; DecidedLlvm = ref false }
                EpilogueLegTable.record plan |> ignore
             | _ -> ())
            let lets =
                (jId, rc) :: (List.mapi (fun i pid -> (pid, IRTupleProj (IRVar (jId, jTy), i, false))) pIds)
            let earliest = (g |> List.minBy (fun m -> (m.Unit, m.Ordinal))).UnitKey
            inserts.[earliest] <- (match inserts.TryGetValue earliest with | true, l -> l | _ -> []) @ lets
            List.iter2 (fun (m: SfMember) (ix: int list) ->
                match m.Join with
                | Some jid ->
                    dropUnits.Add m.UnitKey |> ignore
                    projOf.[jid] <- (ix |> List.map (fun i -> pIds.[i]), ix |> List.map (fun i -> pTys.[i]))
                | None ->
                    let i = ix.Head
                    replace.[(m.UnitKey, m.Ordinal)] <- IRVar (pIds.[i], pTys.[i])) g idx
        let fixProj (e: IRExpr) =
            if projOf.Count = 0 then e
            else
                mapIRExpr (fun n ->
                    match n with
                    | ProjOfNamed (jid, _, k, _) when projOf.ContainsKey jid ->
                        let (ps, ts) = projOf.[jid]
                        if k < ps.Length then IRVar (ps.[k], ts.[k]) else n
                    | _ -> n) e
        let out = ResizeArray<IRId option * IRExpr>()
        for u in finalUnits do
            (match starts.TryGetValue u.Key with
             | true, (sid, call) -> out.Add((Some sid, IRBeginPoolScope call))
             | _ -> ())
            (match inserts.TryGetValue u.Key with
             | true, lets -> for (id, v) in lets do out.Add((Some id, v))
             | _ -> ())
            if not (dropUnits.Contains u.Key) then
                let v' =
                    sfWalk isOwnCall (fun ord n ->
                        match replace.TryGetValue((u.Key, ord)) with
                        | true, r -> r
                        | _ -> n) (fun _ c -> c) u.Value
                out.Add((u.Binder, fixProj v'))
            (match ends.TryGetValue u.Key with
             | true, (sid, pools, call) ->
                 out.Add((Some (builder.FreshId()), IRReleasePools (IRVar (sid, IRTUnit), pools, call)))
             | _ -> ())
        let items = List.ofSeq out
        let (_, tail) = List.last items
        List.foldBack (fun (b, v) acc ->
            match b with
            | Some id -> IRLet (id, v, acc)
            | None -> acc) (items |> List.take (items.Length - 1)) tail

    let quickHasFold (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x ->
            match x with
            | IRReduce _ | IRReduceCompute _ | IRProdSum _ -> n <- n + 1
            | IRApp _ when isOwnCall x -> n <- n + 1
            | _ -> ()) e
        n >= 2
    let functions' =
        modul.Functions |> List.map (fun f ->
            if f.Name.StartsWith "__lambda_" || f.IsStatic || f.IsArityPoly || f.IsCudaKernel || f.IsMpiParallel
               || hasTypeVarsInSignature f || not (quickHasFold f.Body) then f
            elif f.IsRepro then
                Blade.Effects.Decisions.record
                    { Blade.Effects.Rule = "sibling-folds"; Version = 1; Span = Blade.Ast.noSpan; Subject = f.SourceName
                      Outcome = Blade.Effects.Declined "the region is a `where repro` function (v1 leaves its folds as written)"
                      Evidence = [] }
                f
            else
                let ps = f.Params |> List.map (fun p -> p.VarId) |> Set.ofList
                { f with Body = processRegion f.SourceName (Some f.Id) ps f.Body })
    let bindings' =
        modul.Bindings |> List.map (fun b ->
            if quickHasFold b.Value then { b with Value = processRegion b.Name None Set.empty b.Value } else b)
    // ---- the TOP-LEVEL region (gap G107 (3), round 4 wave 4) ---------------
    // Separate top-level statements `let s = reduce(x, (+))` / `let ss =
    // reduce(x * x ..)` are siblings too: the module's binding sequence is
    // judged as ONE straight-line region (a synthetic let spine over the
    // bindings, each binding one statement), under the same admission and
    // the same write barrier -- every crossed binding judged by its whole
    // payload through the shared scheduling write analysis
    // (`scheduleMayWriteIn` over `intervalPayloadOf`), a provider write or
    // an assigning binding a barrier. A `let static` binding and a provider
    // write are opaque statements (never candidates). The rewrite inserts the
    // join and its projections as NEW internal bindings (`__sfj<id>`, never
    // printed) before the earliest member; each member binding keeps its id,
    // name and type and reads its projection, so it prints where it did. No
    // call is inlined and no existing join dissolved at top level.
    // The C++ emitter orders bindings by IRId (CodeGen.emissionOrderedItems),
    // so each new binding is keyed on the ORIGINAL binding it precedes
    // (`DerivedFuncOrigins`, the same emission-order correction a synthesized
    // function takes): it is emitted at exactly that program point. The
    // interpreter and the LLVM lane walk `Bindings` in list order.
    let topOrigins = System.Collections.Generic.Dictionary<IRId, IRId>()
    let bindings' =
        let opaque (b: IRBinding) = b.IsConst || modul.ProviderWrites.ContainsKey b.Id
        let foldCount =
            bindings' |> List.sumBy (fun b ->
                if opaque b then 0
                else
                    let mutable n = 0
                    iterIRExpr (fun x -> match x with IRReduce _ | IRReduceCompute _ | IRProdSum _ -> n <- n + 1 | _ -> ()) b.Value
                    n)
        if foldCount < 2 || not (siblingFoldsTopEnabled ()) then bindings'
        else
            let spine =
                List.foldBack (fun (b: IRBinding) acc ->
                    IRLet (b.Id, (if opaque b then IRLit IRLitUnit else b.Value), acc)) bindings' (IRLit IRLitUnit)
            topLevel.Value <- true
            topBarrier.Value <- bindings' |> List.filter opaque |> List.map (fun b -> b.Id) |> Set.ofList
            let out =
                try processRegion "top level" None Set.empty spine
                finally
                    topLevel.Value <- false
                    topBarrier.Value <- Set.empty
            if obj.ReferenceEquals(out, spine) then bindings'
            else
                let byId = bindings' |> List.map (fun b -> (b.Id, b)) |> Map.ofList
                let (lets, _) = unrollSpine out
                let pendingNew = ResizeArray<IRId>()
                // a carrier's name never equals a name the module already
                // binds (a user `let __sfj18 = 42.0` beside the carrier
                // `__sfj18` printed twice under `--print __sfj18` and emitted
                // two C++ declarations: round 4 wave 9, lane A, A4, Codex
                // 0d750baf member 3)
                let occupied =
                    System.Collections.Generic.HashSet<string>(
                        (bindings' |> List.map (fun b -> b.Name))
                        @ (modul.Functions |> List.collect (fun f -> [ f.Name; f.SourceName ])))
                // (nor one a binding EXTENDS with `_`: the emitter names the
                // join legs `<carrier>_0`, `<carrier>_1`, ...)
                let carrierName (id: IRId) =
                    let clashes (n: string) = occupied.Contains n || occupied |> Seq.exists (fun o -> not (isNull o) && o.StartsWith (n + "_"))
                    let rec pick (k: int) =
                        let n = if k = 0 then $"__sfj{id}" else $"__sfj{id}r{k}"
                        if clashes n then pick (k + 1) else n
                    let n = pick 0
                    occupied.Add n |> ignore
                    n
                let mapped =
                    lets |> List.map (fun (id, v) ->
                        match Map.tryFind id byId with
                        | Some b ->
                            for n in pendingNew do topOrigins.[n] <- b.Id
                            pendingNew.Clear()
                            if opaque b then b else { b with Value = v }
                        | None ->
                            pendingNew.Add id
                            { Id = id; Name = carrierName id; Type = typeOf v; Value = v; IsConst = false; IsMutable = false })
                mapped
    let modul =
        if topOrigins.Count = 0 then modul
        else { modul with DerivedFuncOrigins = topOrigins |> Seq.fold (fun acc (KeyValue (k, v)) -> Map.add k v acc) modul.DerivedFuncOrigins }
    if minted.Count = 0 then { modul with Functions = functions'; Bindings = bindings' }
    else
        let byOrigin = minted |> Seq.choose (fun (c, o) -> o |> Option.map (fun oid -> (oid, c))) |> Seq.groupBy fst |> Map.ofSeq
        let loose = minted |> Seq.filter (fun (_, o) -> o.IsNone) |> Seq.map fst |> List.ofSeq
        { modul with
            Functions =
                (functions' |> List.collect (fun f ->
                    match Map.tryFind f.Id byOrigin with
                    | Some cs -> f :: (cs |> Seq.map snd |> List.ofSeq)
                    | None -> [ f ])) @ loose
            Bindings = bindings'
            DerivedFuncOrigins =
                minted |> Seq.fold (fun acc (c, o) ->
                    match o with
                    | Some oid -> Map.add c.Id oid acc
                    | None -> acc) modul.DerivedFuncOrigins }
