// Folds riding a windowed recurrence, rule rec-fold-ring, and the PM35 constructor table (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeRecFold

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono
open Blade.OptimizeGates
open Blade.OptimizeAnalysis
open Blade.OptimizeStorage
open Blade.OptimizeHotRow

// --- Folds riding a windowed recurrence (PM35, rule `rec-fold-ring` v1) -------
//
// A block- or function-body `let rec` whose LATER uses are folds of the
// trajectory is not windowed by the typechecker's ring (rec-window P1 demands
// fixed-row reads), so it allocates its whole N-row history even when every
// consumer is a fold -- bench recur's 301 x 20011 trajectory (48 MB) is folded
// INSIDE the sweep by row-fold fusion and never read again. This pass runs
// right after `fuseRowFoldsModule`, over the region
//
//   let T = (let BUF = uninit in <seed>; for n in lo..N { <step> }; BUF) in rest
//
// and, when every use of T in `rest` is a fold it can ride the sweep (or T
// has no use left), (1) moves each fold's state into the sweep -- the
// fold's per-row work right after row n is written (after the seed for row
// 0) -- and (2) stores BUF as a RING of R = K + 1 rows, K the step's maximum
// lag: every leading subscript i becomes `Int64(i) % R`.
//
// RING (storage only, every build): row r lives in slot r mod R, is written
// once (step r, or the seed), and is overwritten at step r + R; the audit
// below proves every row the program SELECTS is read at a step s with
// r <= s <= r + K (a lag c <= K, or the current row after its complete write),
// and a seed row only before the sweep -- so each read sees the value the
// full buffer held. A guarded zero-history read's clamp branch (index 0 when
// the guard fails) forms an in-range slot whose value the guard discards: the
// audit proves every use of such a read sits under its guard's conditions.
//
// CONSUMERS (each its own state; applied only together with the ring):
//   C1 `reduce(T, F[, s])`, T rank 1: acc <- F(acc, BUF[n]) after each row;
//      seeded from s (evaluated before the recursion), unseeded from row 0
//      (a seed arm is required: lo = 1);
//   C2 `reduce(method_for(T) <@> K, F, s)` over a plain map of T's rows /
//      cells: acc <- F(acc, K(BUF[n]));
//   C3 `method_for(T) <@> K |> compute`, scalar K (the partial fold
//      `reduce(traj, op)` lowers to it): out[n] <- K(BUF[n]), out stored;
//   C4 the rank-2 accumulator nest of `reduce(traj, op, axes = 2)`:
//      for j: acc <- op(acc, BUF[n][j]).
// FOLD ORDER: each chain applies F (op) to the same terms in the same
// ascending order from the same seed or first element -- exactly the
// strict-route association, so bitwise under BLADE_FP_REPRO=1
// BLADE_FP_CONTRACT=off; C4 is serial in every build (bitwise there too). In
// a licensed build C1/C2 run the serial chain instead of the lane tree, and a
// C3 row fold its own licensed per-row form instead of the bitwise row jam
// (licensed association changes, recorded).
// FAILURE ORDER: K, F and s are TOTAL (IRTotality, plus the row-fold /
// self-dot forms a row kernel takes) and write nothing, every statement from
// the recursion through the last consumer writes nothing (the shared write
// analysis), so evaluating the folds early is unobservable. The kernels are
// CALLED, never inlined (a `where repro` boundary stays).
// DECLINES (each recorded): T used otherwise (an escape, a fixed-row read --
// P1's snapshots ride the surface only --, a transpose ..), a consumer in a
// branch / loop body, a fold kernel with an omp request, a may-panic kernel or
// seed, a seed read after the recursion, an unseeded C1 with no seed arm,
// rank > 2, a `while` guard / freeze epilogue, a zero-filled buffer, a buffer
// mention the audit cannot classify, dense coverage, and a threaded step whose
// C++ form folds complete rows after the team (`recStepFoldForm`).

/// Does `vid` occur as an IRVar among the expression CHILDREN of `e` (the
/// ExprShape walk; types are `recFoldMentionsInTypes`)?
let recFoldMentions (vid: IRId) (e: IRExpr) =
    let mutable hit = false
    iterIRExpr (fun n -> match n with IRVar (id, _) when id = vid -> hit <- true | _ -> ()) e
    hit

/// EVERY expression a type carries in `e`, transitively and with no depth
/// bound: a worklist over the extents each extent's own nodes carry
/// (`embeddedExtentExprs`), deduplicated by reference, under a node budget.
/// None = the budget ran out (callers fail closed).
let recFoldTypeCarried (e: IRExpr) : IRExpr list option =
    let seen = System.Collections.Generic.HashSet<IRExpr>(HashIdentity.Reference)
    let out = ResizeArray<IRExpr>()
    let work = System.Collections.Generic.Queue<IRExpr>()
    for x in embeddedExtentExprs e do work.Enqueue x
    let mutable budget = 200000
    while work.Count > 0 && budget > 0 do
        let x = work.Dequeue ()
        if seen.Add x then
            budget <- budget - 1
            out.Add x
            for y in embeddedExtentExprs x do work.Enqueue y
    if work.Count > 0 then None else Some (List.ofSeq out)

/// Does `id` occur in an expression a TYPE carries in `e` (a range's extent,
/// a variable's index extents ..)? The child walk never enters those; an
/// exhausted budget answers yes (fail closed).
let recFoldMentionsInTypes (id: IRId) (e: IRExpr) =
    match recFoldTypeCarried e with
    | None -> true
    | Some xs -> xs |> List.exists (recFoldMentions id)

/// The rec-fold-ring census's OUTSIDE channels (the `b1-census-table` rows
/// marked ChCallable / ChModule): every place OTHER than the body being
/// rewritten that can name `id` (the trajectory T or its buffer), or None.
/// Built on THE module traversal (`compactionRoots`, shared with the
/// compaction census and `moduleReferencedIds`): every callable of the
/// program (body, captures by id, and the types of its parameters, return
/// and captures), every module binding (value and type), every side-table
/// operand executed at a binding's site, every declared type, every provider
/// write source -- plus the side tables' KEYS and operands keyed by an id no
/// binding carries, the provider specs' types, and the OTHER modules through
/// the foreign-reference census (a multi-module program without it fails
/// closed). The body (`currentFn`) or binding value (`currentBinding`) being
/// rewritten is classified by the census and the ring audit instead.
let recFoldForeignNaming (callables: IRCallable list) (modul: IRModule) (foreign: Set<IRId> option) (singleModule: bool)
                         (currentFn: IRId) (currentBinding: IRId) (id: IRId) : string option =
    let named (e: IRExpr) = recFoldMentions id e || recFoldMentionsInTypes id e
    let inType (t: IRType) = irTypeExtentExprs t |> List.exists named
    let inArrayType (at: IRArrayType) = inType (mkArrayLike at)
    let fromRoot (root: CompactionRoot) : string option =
        match root with
        | RootFunctionBody f when f.Id = currentFn -> None
        | RootFunctionBody f ->
            if f.Captures |> List.exists (fun c -> c.Id = id) || named f.Body then
                Some $"a callable (`{f.SourceName}`) names it (a capture, its body, or a type its body carries)"
            else None
        | RootBindingValue b when b.Id = currentBinding -> None
        | RootBindingValue b -> if named b.Value then Some $"the module binding `{b.Name}` names it" else None
        | RootSideOperand (b, what, e) -> if named e then Some $"{what} of `{b.Name}` names it" else None
        | RootType (what, t) -> if inType t then Some $"the type of {what} carries it in an extent" else None
        | RootSourceRef (what, sid) -> if sid = id then Some $"{what} reads it" else None
        // a provider read's mask names a MODULE binding by its surface name;
        // the trajectory and its buffer are block-local lets, never one
        | RootNameRef _ -> None
    match compactionRoots callables modul |> List.tryPick fromRoot with
    | Some why -> Some why
    | None ->
    let sideKeys =
        Seq.concat [ modul.CompoundInits |> Map.toSeq |> Seq.map fst; modul.SparseInits |> Map.toSeq |> Seq.map fst
                     modul.RandomInits |> Map.toSeq |> Seq.map fst; modul.ProviderWrites |> Map.toSeq |> Seq.map fst
                     modul.ProviderReads |> Map.toSeq |> Seq.map fst ]
        |> Seq.distinct |> List.ofSeq
    if sideKeys |> List.contains id then Some "a module side table (compound / sparse / random / provider) is keyed by it"
    elif sideKeys |> List.exists (fun k -> bindingSideOperands modul k |> List.exists named) then
        Some "a module side-table operand (compound / sparse / random) names it"
    elif modul.ProviderReads |> Map.exists (fun _ r -> inArrayType r.VarType || (match r.MaskType with Some mt -> inArrayType mt | None -> false)) then
        Some "a provider read's variable or mask type carries it in an extent"
    elif modul.ProviderWrites |> Map.exists (fun _ w -> inArrayType w.SourceType) then
        Some "a provider write's source type carries it in an extent"
    else
    match foreign with
    | Some refs when refs.Contains id -> Some "another module of the program names it (the foreign-reference census)"
    | Some _ -> None
    | None when singleModule -> None
    | None -> Some "a multi-module program reached the pass without its foreign-reference census (fail closed)"


// ---- THE PM35 CONSTRUCTOR TABLE (`b1-census-table`, round 4 wave 3 B1) ----
//
// Every member of every IR data type reachable from IRProgram through which
// a value could name the trajectory T or its buffer BUF -- every union-case
// field and record field whose type can carry an IRExpr or an int (IRId is an
// int alias, so every int field is listed) -- with the channel that decides
// it. The list is NOT a list of found holes: OptimizeTests
// `rec_fold_ring_constructor_table_closed` enumerates the members BY
// REFLECTION and fails on a member with no row (or a row naming no member),
// so a new constructor or field cannot reach the pass unclassified; and
// `rec_fold_ring_constructor_table_walkers` / `_foreign` build a value with a
// sentinel id at each row's member and check that the walker the row names
// reaches it (ChChild / ChRef: the ExprShape walk; ChType:
// embeddedExtentExprs / irTypeExtentExprs; ChCallable / ChModule:
// recFoldForeignNaming) and that ChBinder / ChParam / ChInert / ChAdmitted
// rows are reached by none.
//
// Channels outside the IR data (no row; each decided here):
//   * optimizer side tables filled BEFORE the pass -- RecStepTable
//     (fuseSliceWrites) and CellFoldTable (fuseRowFolds) -- name the buffer by
//     design; the threaded step re-verifies through `recStepFoldForm` and
//     indexes `n % R` (corpus recursive-arrays/051, OptimizeTests
//     rec_fold_ring_threaded_team_indexes_the_ring); every other table (pool
//     reuse, CSE facts, hot-row / member / compaction / join / orbit / group /
//     alias plans, sibling folds and maps, epilogue legs, pool-scope events)
//     and every emitter cell is computed AFTER, over the rewritten tree, where
//     the ring is an ordinary local buffer; the interpreter runs that tree;
//   * other modules: the foreign-reference census (recFoldForeignNaming);
//   * the surface: the pass runs on the lowered IR only, so every surface
//     spelling reaches it as rows of this table; rec-window's surface ring
//     (P1) rewrites fixed-row reads before lowering and leaves snapshot reads,
//     which this census declines as fixed-row reads (052);
//   * names: a provider read's mask names a MODULE binding by its surface
//     name (RootNameRef); T and BUF are block-local lets;
//   * EVALUATIONS a moved party triggers (B1 review rounds 1 and 2): a fold /
//     row / combine kernel that reads a DEFERRED value -- a let deferred by
//     the program's classifier (IRLift.computeDeferredIds for module
//     bindings, and its rules for every let: a deferred node, an alias, a
//     wholly-deferred tuple, a projection of one, a let chain by its final
//     value), a streamed provider read, or a binding of
//     ANOTHER module (its value is not visible here) -- would force it inside
//     the sweep instead of after the recursion: IRTotality refuses the read
//     (`recFoldDeferredId`), so the consumer declines. Seeds are closed
//     scalar expressions (seedOk), captures are empty, and the step itself
//     does not move.
// Values DERIVED from the buffer (a row view, a closure over one) are not
// names: a view formed in the step lives in the step's scope and outlives its
// slot only through a write (the producer-write audit over the chain without
// the buffer's binder: 060, 063), a closure capturing it likewise (only a
// scalar assignment outside the region is exempt, and a closure is no
// scalar); a value read from the seed rows before the sweep may not be used
// inside it (preViews); a view in a type extent is a ChType mention (062).
type RecFoldChannel =
    /// IRVar's id: the reference itself. In the region a classified lag /
    /// current-row / seed-row read or the row writer, else declined
    /// (recursive-arrays/059-061, 063); after it a C1-C4 consumer operand
    /// (046-051, 056), else declined (052 fixed row, 053 escape, 055 branch).
    | ChRef
    /// An expression child ExprShape hands out: recFoldMentions, the census
    /// `find`, the ring audit `walk` and the shared write analysis all see
    /// it (its IRVar is then the ChRef row).
    | ChChild
    /// An expression a type carries: embeddedExtentExprs / irTypeExtentExprs
    /// (exhaustive) reach it; recFoldMentionsInTypes declines a mention (062,
    /// the deep / effect decision pins) and typesQuiet a computing extent.
    | ChType
    /// A callable's capture, body or signature type: recFoldForeignNaming
    /// declines (057).
    | ChCallable
    /// A module binding, side table, provider spec or declared type:
    /// recFoldForeignNaming declines (no source spelling reaches it -- a
    /// block-local let is out of scope at module level -- so fail closed).
    | ChModule
    /// Holds members of another listed owner type (each has its own rows).
    | ChContainer
    /// Introduces a fresh id (a let, a loop or pattern variable, a parameter's
    /// body id, a callable's or binding's own id): names no existing value.
    | ChBinder
    /// IRParam's position: a reference to the ENCLOSING callable's parameter.
    /// The trajectory is a let, never a parameter of the callable holding it;
    /// another callable receives it only from a call whose argument names it
    /// (a ChRef whole use, declined: 053).
    | ChParam
    /// A number, identity or span with no storage meaning (ranks, dims,
    /// offsets, index-type and nominal ids, loop-index dependencies, array
    /// identities compared by equality, unit exponents): never resolved to a
    /// value.
    | ChInert
    /// MutableArrayLets: the desugar records the let rec binding and its
    /// buffer; the deep copy at the binding site copies the ring, and the
    /// trajectory has no use left after the rewrite -- admitted.
    /// LetMutBinders (D9): the rec desugar's buffer is a surface `let mut`
    /// (TypeCheckInfer's bufLet), so its id is there; the ring keeps that id
    /// (the rewritten `IRLet (bufId, IRUninit ringT, ..)`), and every reader of
    /// the set runs BEFORE the optimizer (IRMono.monomorphizeModule carries it
    /// onto pack-unroll copies, Lowering.refuseDeferredWritesAndUnorderedIfs
    /// judges captured plain lets); after it the set is only unioned into
    /// merged module records (CodeGen / EmitLlvm), never looked up -- admitted.
    | ChAdmitted

/// Keys: `Owner.Field` (records) / `Owner.Case.Field` (unions), Owner the
/// type's name without its generic arity; the F# field names as declared
/// (`Item`, `Item1` .. for unnamed fields).
let recFoldConstructorTable : (string * RecFoldChannel) list =
    [
      // IRProgram
      "IRProgram.Modules", ChContainer; "IRProgram.DeclSpans", ChInert;
      // IRModule
      "IRModule.Types", ChContainer; "IRModule.Functions", ChContainer; "IRModule.Bindings", ChContainer;
      "IRModule.ProviderReads", ChModule; "IRModule.ProviderWrites", ChModule; "IRModule.RandomInits", ChModule;
      "IRModule.CompoundInits", ChModule; "IRModule.SparseInits", ChModule; "IRModule.MutableArrayLets", ChAdmitted; "IRModule.LetMutBinders", ChAdmitted;
      "IRModule.DerivedFuncOrigins", ChInert;
      // IRTypeDef
      "IRTypeDef.IRTDAlias.ty", ChModule; "IRTypeDef.IRTDStruct.fields", ChModule;
      "IRTypeDef.IRTDVariant.variants", ChModule; "IRTypeDef.IRTDIndexType.idx", ChModule;
      "IRTypeDef.IRTDEnumIdx.idx", ChModule;
      // IRTypeG
      "IRTypeG.IRTTuple.Item", ChContainer; "IRTypeG.IRTLoop.Item", ChContainer;
      "IRTypeG.IRTComputation.Item", ChContainer; "IRTypeG.IRTPoly.baseType", ChContainer;
      "IRTypeG.IRTNat.Item", ChInert; "IRTypeG.IRTIdxTagged.inner", ChContainer;
      "IRTypeG.IRTIdxTagged.tag", ChContainer; "IRTypeG.IRTDist.order", ChInert; "IRTypeG.IRTDist.elem", ChContainer;
      "IRTypeG.IRTDist.axes", ChContainer; "IRTypeG.IRTInfer.Item", ChInert;
      "IRTypeG.IRTUnitAnnotated.Item1", ChContainer; "IRTypeG.IRTUnitAnnotated.Item2", ChInert;
      "IRTypeG.IRTGroupKeys.outerIdx", ChContainer; "IRTypeG.IRTGroupKeys.sourceIdx", ChContainer;
      "IRTypeG.IRTArrow.slots", ChContainer; "IRTypeG.IRTArrow.result", ChContainer;
      "IRTypeG.IRTArrow.identity", ChInert;
      // LoopTypeG
      "LoopTypeG.Arity", ChInert; "LoopTypeG.ArrayTypes", ChContainer; "LoopTypeG.KernelType", ChContainer;
      // IdxRefG
      "IdxRefG.IRefAnon.nominalId", ChInert; "IdxRefG.IRefAnon.extent", ChType;
      // IRExpr
      "IRExpr.IRVar.id", ChRef; "IRExpr.IRVar.ty", ChType; "IRExpr.IRParam.idx", ChParam; "IRExpr.IRParam.ty", ChType;
      "IRExpr.IRBinOp.Item3", ChChild; "IRExpr.IRBinOp.Item4", ChChild; "IRExpr.IRUnaryOp.Item2", ChChild;
      "IRExpr.IRArrayLit.Item1", ChChild; "IRExpr.IRArrayLit.Item2", ChType; "IRExpr.IRIndex.array", ChChild;
      "IRExpr.IRIndex.index", ChChild; "IRExpr.IRIndex.identity", ChInert; "IRExpr.IRSlice.array", ChChild;
      "IRExpr.IRSlice.dim", ChInert; "IRExpr.IRSlice.start", ChChild; "IRExpr.IRSlice.stop", ChChild;
      "IRExpr.IRCurry.array", ChChild; "IRExpr.IRCurry.index", ChChild; "IRExpr.IRCurry.resultRank", ChInert;
      "IRExpr.IRAppAt.func", ChChild; "IRExpr.IRAppAt.args", ChChild; "IRExpr.IRAppAt.retType", ChType;
      "IRExpr.IRTuple.Item", ChChild; "IRExpr.IRComplex.re", ChChild; "IRExpr.IRComplex.im", ChChild;
      "IRExpr.IRFma.a", ChChild; "IRExpr.IRFma.b", ChChild; "IRExpr.IRFma.c", ChChild;
      "IRExpr.IRRandAt.args", ChChild; "IRExpr.IRRandAt.weights", ChChild; "IRExpr.IRTupleProj.Item1", ChChild;
      "IRExpr.IRTupleProj.Item2", ChInert; "IRExpr.IRTupleCons.head", ChChild; "IRExpr.IRTupleCons.tail", ChChild;
      "IRExpr.IRTupleDecons.tuple", ChChild; "IRExpr.IRFieldAccess.obj", ChChild;
      "IRExpr.IRStructLit.fields", ChChild; "IRExpr.IRIf.cond", ChChild; "IRExpr.IRIf.thenBr", ChChild;
      "IRExpr.IRIf.elseBr", ChChild; "IRExpr.IRMatch.scrutinee", ChChild; "IRExpr.IRMatch.cases", ChContainer;
      "IRExpr.IRLet.Item1", ChBinder; "IRExpr.IRLet.value", ChChild; "IRExpr.IRLet.body", ChChild;
      "IRExpr.IRReleasePools.scope", ChChild; "IRExpr.IRReleasePools.pools", ChChild;
      "IRExpr.IRMethodFor.Item", ChContainer; "IRExpr.IRObjectFor.Item", ChContainer;
      "IRExpr.IRApplyCombinator.Item", ChContainer; "IRExpr.IRComposeApply.Item", ChContainer;
      "IRExpr.IRBind.comp", ChChild; "IRExpr.IRBind.cont", ChChild; "IRExpr.IRParallel.Item1", ChChild;
      "IRExpr.IRParallel.Item2", ChChild; "IRExpr.IRParallel.fusionDepth", ChInert; "IRExpr.IRFusion.Item1", ChChild;
      "IRExpr.IRFusion.Item2", ChChild; "IRExpr.IRChoice.Item1", ChChild; "IRExpr.IRChoice.Item2", ChChild;
      "IRExpr.IRFallback.Item1", ChChild; "IRExpr.IRFallback.Item2", ChChild; "IRExpr.IRArrayProduct.Item1", ChChild;
      "IRExpr.IRArrayProduct.Item2", ChChild; "IRExpr.IRComposeObj.Item1", ChChild;
      "IRExpr.IRComposeObj.Item2", ChChild; "IRExpr.IRComposeMeth.Item1", ChChild;
      "IRExpr.IRComposeMeth.Item2", ChChild; "IRExpr.IRCompose.Item1", ChChild; "IRExpr.IRCompose.Item2", ChChild;
      "IRExpr.IRFunctorMap.func", ChChild; "IRExpr.IRFunctorMap.comp", ChChild; "IRExpr.IRPure.Item", ChChild;
      "IRExpr.IRCompute.Item", ChChild; "IRExpr.IRReynolds.kernel", ChChild; "IRExpr.IRGuard.cond", ChChild;
      "IRExpr.IRGuard.body", ChChild; "IRExpr.IRSequence.Item", ChChild; "IRExpr.IRReplicate.count", ChChild;
      "IRExpr.IRReplicate.body", ChChild; "IRExpr.IRMask.array", ChChild; "IRExpr.IRMask.pred", ChChild;
      "IRExpr.IRIntersect.Item1", ChChild; "IRExpr.IRIntersect.Item2", ChChild; "IRExpr.IRUnion.Item1", ChChild;
      "IRExpr.IRUnion.Item2", ChChild; "IRExpr.IRUnique.array", ChChild; "IRExpr.IRContains.array", ChChild;
      "IRExpr.IRContains.value", ChChild; "IRExpr.IRDisplayEmit.data", ChChild; "IRExpr.IRDisplayEmit.id", ChChild;
      "IRExpr.IRDisplayJson.rank", ChInert; "IRExpr.IRDisplayJson.data", ChChild; "IRExpr.IRDisplayNum.data", ChChild;
      "IRExpr.IRDisplayStr.data", ChChild; "IRExpr.IRGroupBy.values", ChChild; "IRExpr.IRGroupBy.grouping", ChChild;
      "IRExpr.IRGroupKeys.keys", ChChild; "IRExpr.IRGroupBucket.grouping", ChChild;
      "IRExpr.IRUngroupGrid.grouped", ChChild; "IRExpr.IRUngroupGrid.sources", ChType;
      "IRExpr.IRUngroupRows.rows", ChChild; "IRExpr.IRUngroupRows.source", ChType;
      "IRExpr.IRUngroup.grouped", ChChild; "IRExpr.IRUngroup.source", ChType; "IRExpr.IRGroupSizes.grouping", ChChild;
      "IRExpr.IRSort.array", ChChild; "IRExpr.IRSort.key", ChChild; "IRExpr.IRReduce.array", ChChild;
      "IRExpr.IRReduce.kernel", ChChild; "IRExpr.IRReduce.init", ChChild;
      "IRExpr.IRReduceCompute.computation", ChChild; "IRExpr.IRReduceCompute.kernel", ChChild;
      "IRExpr.IRReduceCompute.init", ChChild; "IRExpr.IRProdSum.args", ChChild; "IRExpr.IRZip.Item", ChChild;
      "IRExpr.IRAlign.arrays", ChChild; "IRExpr.IRAlign.spec", ChContainer; "IRExpr.IRStack.Item", ChChild;
      "IRExpr.IRTranspose.array", ChChild; "IRExpr.IRTranspose.dim1", ChInert; "IRExpr.IRTranspose.dim2", ChInert;
      "IRExpr.IRDecompact.array", ChChild; "IRExpr.IRDecompact.dim", ChInert; "IRExpr.IRGram.left", ChChild;
      "IRExpr.IRGram.right", ChChild; "IRExpr.IRGramApply.left", ChChild; "IRExpr.IRGramApply.right", ChChild;
      "IRExpr.IRGramApply.vec", ChChild; "IRExpr.IRMatmul.left", ChChild; "IRExpr.IRMatmul.right", ChChild;
      "IRExpr.IREigh.operand", ChChild; "IRExpr.IRSolve.matrix", ChChild; "IRExpr.IRSolve.rhs", ChChild;
      "IRExpr.IRLu.matrix", ChChild; "IRExpr.IRLuSolve.lu", ChChild; "IRExpr.IRLuSolve.piv", ChChild;
      "IRExpr.IRLuSolve.rhs", ChChild; "IRExpr.IRArrayNegate.array", ChChild;
      "IRExpr.IRArrayConjugate.array", ChChild; "IRExpr.IRReverse.array", ChChild; "IRExpr.IRReverse.dim", ChInert;
      "IRExpr.IRShift.array", ChChild; "IRExpr.IRShift.dim", ChInert; "IRExpr.IRShift.offset", ChChild;
      "IRExpr.IRShift.boundary", ChContainer; "IRExpr.IRDiag.array", ChChild; "IRExpr.IRJoin.arrays", ChChild;
      "IRExpr.IRJoin.dim", ChInert; "IRExpr.IRSubset.array", ChChild; "IRExpr.IRSubset.dim", ChInert;
      "IRExpr.IRSubset.start", ChChild; "IRExpr.IRSubset.length", ChChild; "IRExpr.IRRange.Item1", ChType;
      "IRExpr.IRRange.offset", ChChild; "IRExpr.IRVirtualReverse.Item", ChType; "IRExpr.IRHaloUnhash.window", ChChild;
      "IRExpr.IRArity.resolved", ChInert; "IRExpr.IRZero.ty", ChType; "IRExpr.IRUnseededSeed.baseSeed", ChChild;
      "IRExpr.IRUnseededSeed.ty", ChType; "IRExpr.IRUninit.ty", ChType; "IRExpr.IRRank.array", ChChild;
      "IRExpr.IRPolyIndex.pack", ChChild; "IRExpr.IRPolyIndex.index", ChChild; "IRExpr.IRPolyTail.pack", ChChild;
      "IRExpr.IRPolyTail.drop", ChInert; "IRExpr.IRExtent.array", ChChild; "IRExpr.IRExtent.dim", ChInert;
      "IRExpr.IRRaggedLookup.lengths", ChChild; "IRExpr.IRCompoundMask.mask", ChChild;
      "IRExpr.IRCompoundProject.parent", ChChild; "IRExpr.IRCompoundProject.prefixLen", ChInert;
      "IRExpr.IRSparseKeys.source", ChContainer; "IRExpr.IROrbitClass.levels", ChInert;
      "IRExpr.IROrbitClass.extent", ChChild; "IRExpr.IRAssign.target", ChChild; "IRExpr.IRAssign.value", ChChild;
      "IRExpr.IRForRange.varId", ChBinder; "IRExpr.IRForRange.lo", ChChild; "IRExpr.IRForRange.hi", ChChild;
      "IRExpr.IRForRange.body", ChChild; "IRExpr.IRConstraintCheck.cond", ChChild;
      "IRExpr.IRConstraintCheck.span", ChInert; "IRExpr.IRBreakIf.cond", ChChild;
      "IRExpr.IRReplayRow.target", ChInert; "IRExpr.IRReplayRow.row", ChChild;
      // IRArrayTypeG
      "IRArrayTypeG.ElemType", ChContainer; "IRArrayTypeG.IndexTypes", ChContainer; "IRArrayTypeG.Identity", ChInert;
      // IRIndexTypeG
      "IRIndexTypeG.Id", ChInert; "IRIndexTypeG.Rank", ChInert; "IRIndexTypeG.Extent", ChType;
      "IRIndexTypeG.Dependencies", ChInert;
      // ArrayIdentity
      "ArrayIdentity.AIDLiteral.Item", ChInert; "ArrayIdentity.AIDParameter.idx", ChInert;
      "ArrayIdentity.AIDAlias.root", ChInert;
      // IRMatchCase
      "IRMatchCase.Pattern", ChContainer; "IRMatchCase.Guard", ChChild; "IRMatchCase.Body", ChChild;
      // IRPattern
      "IRPattern.IRPatVar.Item", ChBinder; "IRPattern.IRPatTuple.Item", ChContainer;
      "IRPattern.IRPatCons.Item1", ChContainer; "IRPattern.IRPatCons.Item2", ChContainer;
      "IRPattern.IRPatVariant.tag", ChInert; "IRPattern.IRPatVariant.Item3", ChContainer;
      "IRPattern.IRPatStruct.fields", ChContainer;
      // MethodForInfo
      "MethodForInfo.Arrays", ChChild; "MethodForInfo.Identities", ChInert; "MethodForInfo.ArrayTypes", ChType;
      "MethodForInfo.SDimsPerArray", ChInert; "MethodForInfo.TotalSDims", ChInert;
      "MethodForInfo.SharedIndexTypes", ChType;
      // ObjectForInfo
      "ObjectForInfo.Kernel", ChChild; "ObjectForInfo.CommGroups", ChInert; "ObjectForInfo.InputRanks", ChInert;
      "ObjectForInfo.OutputRank", ChInert;
      // ApplyInfo
      "ApplyInfo.Loop", ChChild; "ApplyInfo.Kernel", ChChild; "ApplyInfo.Arrays", ChChild;
      "ApplyInfo.Identities", ChInert; "ApplyInfo.ArrayTypes", ChType; "ApplyInfo.SharedIndexTypes", ChType;
      "ApplyInfo.SDimsPerArray", ChInert; "ApplyInfo.KernelInputRanks", ChInert;
      "ApplyInfo.KernelOutputRank", ChInert; "ApplyInfo.KernelTDims", ChType; "ApplyInfo.OutputType", ChType;
      // ComposeApplyInfo
      "ComposeApplyInfo.Composition", ChChild; "ComposeApplyInfo.InputArrays", ChChild;
      "ComposeApplyInfo.OutputType", ChType;
      // AlignSpec
      "AlignSpec.Offsets", ChInert; "AlignSpec.Boundary", ChContainer;
      // BoundaryMode
      "BoundaryMode.BndPad.Item", ChChild;
      // SparseKeysSource
      "SparseKeysSource.SkRuntime.keys", ChChild; "SparseKeysSource.SkDomain.plan", ChInert;
      // DomainPlan
      "DomainPlan.Levels", ChInert;
      // DomainLevel
      "DomainLevel.Lo", ChInert; "DomainLevel.Hi", ChInert;
      // DomainAffine
      "DomainAffine.Coefs", ChInert;
      // Span
      "Span.StartLine", ChInert; "Span.StartCol", ChInert; "Span.EndLine", ChInert; "Span.EndCol", ChInert;
      // UnitSig
      "UnitSig.Dims", ChInert; "UnitSig.Scale", ChInert;
      // UnitScale
      "UnitScale.Consts", ChInert;
      // IRArrowSlotG
      "IRArrowSlotG.SIdx.Item", ChContainer; "IRArrowSlotG.SIdxVirt.Item", ChContainer;
      "IRArrowSlotG.SVal.Item", ChContainer;
      // IRCallable
      "IRCallable.Id", ChBinder; "IRCallable.Params", ChContainer; "IRCallable.RetType", ChCallable;
      "IRCallable.Body", ChCallable; "IRCallable.CommGroups", ChInert; "IRCallable.AntisymGroups", ChInert;
      "IRCallable.Parallelism", ChInert; "IRCallable.CudaBlockSize", ChInert; "IRCallable.Captures", ChContainer;
      // IRParam
      "IRParam.Type", ChCallable; "IRParam.Index", ChInert; "IRParam.VarId", ChBinder;
      // CaptureInfo
      "CaptureInfo.Id", ChCallable; "CaptureInfo.Type", ChCallable;
      // IRBinding
      "IRBinding.Id", ChBinder; "IRBinding.Type", ChModule; "IRBinding.Value", ChModule;
      // ProviderReadSpec
      "ProviderReadSpec.VarType", ChModule; "ProviderReadSpec.MaskType", ChModule;
      // ProviderWriteSpec
      "ProviderWriteSpec.SourceId", ChModule; "ProviderWriteSpec.SourceType", ChModule;
      // RandomFillSpec
      "RandomFillSpec.FillModulus.Item", ChModule; "RandomFillSpec.RandGen.key", ChModule;
      "RandomFillSpec.RandGen.pars", ChModule; "RandomFillSpec.RandGen.weights", ChModule;
      "RandomFillSpec.RandGen.address", ChModule;
    ]

let windowRecFoldsModule (modul: IRModule) (builder: IRBuilder) (programFuncs: IRCallable list) (foreign: Set<IRId> option) (singleModule: bool) : IRModule =
    let writes = scheduleMayWriteIn programFuncs modul
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in programFuncs do callables.[f.Id] <- f
    for f in modul.Functions do callables.[f.Id] <- f
    let resolveE (e: IRExpr) = match e with IRVar (kid, _) -> (match callables.TryGetValue kid with | true, c -> Some c | _ -> None) | _ -> None
    let mentions = recFoldMentions
    let count (vid: IRId) (e: IRExpr) =
        let mutable n = 0
        iterIRExpr (fun x -> match x with IRVar (id, _) when id = vid -> n <- n + 1 | _ -> ()) e
        n
    let rec chainOf (e: IRExpr) : (IRId * IRExpr) list * IRExpr =
        match e with
        | IRLet (id, v, b) -> let s, fin = chainOf b in (id, v) :: s, fin
        | _ -> [], e
    let rebuild (stmts: (IRId * IRExpr) list) (fin: IRExpr) =
        List.foldBack (fun (id, v) acc -> IRLet (id, v, acc)) stmts fin
    // Type-carried expressions (a range's extent, a variable's index extents
    // ..): the generic child walk never enters those (recFoldTypeCarried).
    let typeCarried = recFoldTypeCarried
    let mentionsInTypes = recFoldMentionsInTypes
    // A type-carried expression that COMPUTES (anything but literals, names
    // and scalar arithmetic) runs at its type's materialization, outside what
    // the shared write analysis walks: its effects are unaudited.
    let typeCarriedSimple (e: IRExpr) =
        let rec simple (x: IRExpr) =
            match x with
            | IRLit _ | IRVar _ | IRParam _ -> true
            | IRBinOp (_, _, a, b, _) -> simple a && simple b
            | IRUnaryOp (_, a) -> simple a
            | _ -> false
        match typeCarried e with
        | None -> false
        | Some xs -> xs |> List.forall simple
    // What an expression can EXECUTE beyond its own tree, transitively: the
    // callables it names (their bodies, and the expressions their types
    // carry) and the MODULE BINDINGS it names -- a deferred binding runs its
    // initializer where it is forced, and a binding's side-table operands
    // (compound / sparse / random) run at its site -- with the payloads of
    // those bindings (Codex review round 5: a deferred map whose producer
    // called a writer in a range bound was reached by the write analysis but
    // not by the type-effect audit). Returns the callables and the payloads.
    let moduleBindingById = modul.Bindings |> List.map (fun b -> b.Id, b) |> Map.ofList
    let reachableFrom (roots: IRExpr list) : IRCallable list * IRExpr list =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let out = ResizeArray<IRCallable>()
        let payloads = ResizeArray<IRExpr>()
        let rec visit (e: IRExpr) =
            let extents = match typeCarried e with Some xs -> xs | None -> []
            for x in e :: extents do
                iterIRExpr (fun n ->
                    match n with
                    | IRVar (id, _) when seen.Add id ->
                        (match callables.TryGetValue id with
                         | true, c -> out.Add c; visit c.Body
                         | _ ->
                            match Map.tryFind id moduleBindingById with
                            | Some b ->
                                let p = bindingPayloadExpr modul b
                                payloads.Add p
                                visit p
                            | _ -> ())
                    | _ -> ()) x
        roots |> List.iter visit
        List.ofSeq out, List.ofSeq payloads
    let reachableCallables (roots: IRExpr list) : IRCallable list = fst (reachableFrom roots)
    // A DEFERRED value a moved kernel could force (B1 review, fable round 1:
    // a fold kernel reading `d(i)` of a deferred module map ran d's kernel --
    // and its panic -- inside the sweep): every let value of the program
    // (module bindings, the lets inside their payloads, every callable's
    // lets) whose value is a deferred form, reached through any chain of
    // `let b = a` aliases, and every STREAMED provider read (fetched lazily).
    // IRTotality refuses a read of one (its evaluation is not audited).
    // AT LEAST as wide as the program's own classifier (B1 review round 2: a
    // projection of a wholly-deferred tuple, the tuple itself, and a let
    // chain ending in a deferred map are deferred in every lane --
    // IRLift.computeDeferredIds -- and were missed by the node test alone);
    // an id bound nowhere in this module (another module's binding) answers
    // deferred (fail closed). The shared predicate (gap G98).
    let recFoldDeferredId = programDeferredId modul callables.Values Seq.empty
    let boundInBlock (blk: IRExpr) (id: IRId) =
        let mutable hit = false
        iterIRExpr (fun x -> match x with IRLet (b, _, _) when b = id -> hit <- true | _ -> ()) blk
        hit
    let staticExt (ix: IRIndexType) = match ix.Extent with IRLit (IRLitInt v) -> Some v | _ -> None
    let plainIx (ix: IRIndexType) = ix.IxKind = IxKPlain && ix.Symmetry = SymNone && ix.Rank = 1
    let scalarTy (t: IRType) = match t with IRTScalar _ -> true | _ -> false
    let totalCallable (c: IRCallable) =
        match IRTotality.auditCallable resolveE (fun _ -> None) recFoldDeferredId c [] with
        | Ok () -> true
        | Error _ -> false
    let totalExpr (e: IRExpr) =
        match IRTotality.walk resolveE (fun _ -> None) recFoldDeferredId Map.empty Map.empty Set.empty Set.empty Set.empty e with
        | Ok _ -> true
        | Error _ -> false
    let plainScalarCallable (arity: int) (c: IRCallable) =
        c.Params.Length = arity && c.Captures.IsEmpty && not c.IsStatic && not c.IsOmpParallel
        && not c.IsCudaKernel && not c.IsMpiParallel && not c.IsArityPoly && scalarTy c.RetType
    // A row kernel K of a rank-2 trajectory: its one parameter is the row.
    // Total when IRTotality admits its body with the row bound as a plain
    // value, or the body is one of the two row forms that whitelist does not
    // list: a fold over the row (non-empty static extent, or seeded) whose
    // kernel and seed are total, and the self-dot / dot of the row.
    let rowKernelTotal (rowCells: int64) (k: IRCallable) =
        match k.Params with
        | [ p ] ->
            // Each row form is judged on its own; the rest of the body, those
            // forms replaced by a scalar literal of the row's element type,
            // must pass the whitelist with the row bound as a plain value.
            let mutable ok = true
            let rowElem = match p.Type with ArrayElem at -> Some at.ElemType | _ -> None
            let floatRow = match rowElem with Some (IRTScalar (ETFloat64 | ETFloat32)) -> true | _ -> false
            let lit =
                match rowElem with
                | Some (IRTScalar ETFloat64) -> Some (IRLit (IRLitFloat 0.0))
                | Some (IRTScalar ETFloat32) -> Some (IRLit (IRLitFloat32 0.0f))
                | Some (IRTScalar ETInt64) -> Some (IRLit (IRLitInt 0L))
                | _ -> None
            let isRow (e: IRExpr) = match e with IRVar (r, _) -> r = p.VarId | _ -> false
            match lit with
            | None -> false
            | Some lit ->
            let rest =
                mapIRExpr (fun x ->
                    match x with
                    | IRReduce (r, f2, s2) when isRow r ->
                        let fine =
                            (s2.IsSome || rowCells >= 1L)
                            && (match resolveE f2 with Some fc -> plainScalarCallable 2 fc && totalCallable fc | None -> false)
                            && (match s2 with Some s -> totalExpr s | None -> true)
                        if not fine then ok <- false
                        lit
                    | IRProdSum args when not args.IsEmpty && args |> List.forall isRow ->
                        if not floatRow then ok <- false
                        lit
                    | _ -> x) k.Body
            ok
            && (match IRTotality.walk resolveE (fun _ -> None) recFoldDeferredId Map.empty Map.empty
                                      (Set.singleton p.VarId) (Set.singleton p.Name) (Set.singleton k.Id) rest with
                | Ok _ -> true
                | Error _ -> false)
        | _ -> false
    let record (subject: string) outcome evidence =
        Blade.Effects.Decisions.record
            { Blade.Effects.Rule = "rec-fold-ring"; Version = 1
              Span = Blade.Ast.noSpan; Subject = subject
              Outcome = outcome; Evidence = evidence }
    let mutable subject = ""
    let mutable enclosingRepro = false
    // The callable whose body is being rewritten (its own body holds the
    // region; every OTHER callable naming T or BUF reads it from outside).
    let mutable currentFn : IRId = -1
    // The module binding whose value is being rewritten (-1: a function).
    let mutable currentBinding : IRId = -1
    // EVERY channel other than the body being rewritten that can name `id`
    // (the trajectory T or its buffer): the first one found, or None. The
    // census and the buffer audit classify the body's own mentions; any
    // OTHER naming is unclassified and declines. Channels (exhaustive over
    // what can carry an IRId or an expression):
    //   - callables (every module's, incl. lifted lambdas and named
    //     functions) other than the one being rewritten: captures, body,
    //     expressions embedded in the body's types, parameter and return
    //     types' extents;
    //   - module bindings other than the one being rewritten: value, the
    //     extents of its type;
    //   - the module's side tables: CompoundInits / SparseInits / RandomInits
    //     operands and keys, ProviderWrites' source and keys, ProviderReads'
    //     keys (its spec carries no expression). MutableArrayLets is ADMITTED: the
    //     desugar records both its buffer and the let rec binding there, and an
    //     assignment to either is a tree node the census / audit classifies.
    // Side tables the optimizer's LATER passes fill (pool reuse, CSE facts,
    // hot-row / member / compaction / join / orbit / group / alias plans,
    // sibling folds, epilogue legs, pool-scope events) are computed over the
    // REWRITTEN tree, where the ring is an ordinary local buffer; the two
    // filled BEFORE this pass (RecStepTable, CellFoldTable) name the buffer
    // by design and their emitters re-verify the store shape (the threaded
    // step through `recStepFoldForm`).
    // (The complete channel list is THE CONSTRUCTOR TABLE,
    // `recFoldConstructorTable`, closed by reflection in OptimizeTests.)
    let allCallables = List.ofSeq callables.Values
    let foreignNaming (id: IRId) (_isT: bool) : string option =
        recFoldForeignNaming allCallables modul foreign singleModule currentFn currentBinding id
    // ---- the ring audit -------------------------------------------------
    // The leading subscript of one buffer READ: the lag it may select (Some
    // c, c >= 1), a current-row read (Some 0), a seed row (literal), or why
    // the ring cannot hold it. Guarded forms return the guard conditions their
    // clamp needs to hold for the read to be selected.
    let rec clampOf (n: IRId) (hi: int64) (e: IRExpr) : Result<int64 option * IRExpr list, string> =
        let leafOf (x: IRExpr) =
            match x with
            | IRUnaryOp (IRCast (ETInt64, _), leaf) | leaf -> leaf
        let leafLag (leaf: IRExpr) =
            match leaf with
            | IRBinOp (_, IRSub, IRVar (v, _), IRLit (IRLitInt c), _) when v = n && c >= 1L -> Some (Some c)   // n - c: selected only under `>= 0`
            | IRBinOp (_, IRAdd, IRVar (v, _), IRLit (IRLitInt c), _) when v = n && c >= 0L && c <= hi && hi > 0L && c <= System.Int64.MaxValue - (hi - 1L) -> Some None   // n + c: never selected under `< n` (c <= the extent: no Int64 wrap for n < extent)
            | IRVar (v, _) when v = n -> Some None                                                                 // n: never selected under `< n`
            | _ -> None
        let condOk (leaf: IRExpr) (c: IRExpr) =
            match c with
            | IRBinOp (_, IRGe, l, IRLit (IRLitInt 0L), _) -> l = leaf
            | IRBinOp (_, IRLt, l, IRVar (v, _), _) -> l = leaf && v = n
            | _ -> false
        match e with
        | IRIf (_, IRIf _, _) -> Error "a doubly guarded trajectory read (v1 audits the single zero-history clamp)"
        | IRIf (c, inner, IRLit (IRLitInt 0L)) ->
            let leaf = leafOf inner
            (match leafLag leaf with
             | Some lag ->
                // the clamp's guard must be the bound its leaf needs: a lag
                // `n - c` selected only when `n - c >= 0`; a current / future
                // row `n + c` only when `n + c < n` (never)
                let need = match lag with Some _ -> IRGe | None -> IRLt
                (match c with
                 | IRBinOp (_, op, _, _, _) when op = need && condOk leaf c -> Ok (lag, [ c ])
                 | _ -> Error "a guarded trajectory read whose guard is not the zero-history bound of its own index")
             | None -> Error "a guarded trajectory read at a leading index that is not `n - c`, `n + c` or `n`")
        | _ -> Error "a trajectory read whose leading index is a select other than the zero-history clamp"
    // The audit proper. Ok (K, ringable reason text) or the first failure.
    let auditRegion (bufId: IRId) (bufT: IRType) (pre: (IRId * IRExpr) list) (n: IRId) (lo: int64) (hi: int64)
                    (step: (IRId * IRExpr) list) : Result<int64, string> =
        let mutable bad : string option = None
        let fail (why: string) = if bad.IsNone then bad <- Some why
        let mutable maxLag = 0L
        // A guarded lag at or past the extent is never selected, but it would
        // size the ring (and K + 1 could overflow): v1 declines it.
        let noteLag (c: int64) =
            if c >= hi then fail "a guarded lag at or beyond the recursion's extent (never selected; v1 declines it rather than size a ring by it)"
            elif c > maxLag then maxLag <- c
        let trailing = match bufT with ArrayElem at -> at.IndexTypes.Tail | _ -> []
        let cells = match trailing with [ ix ] -> staticExt ix | [] -> Some 1L | _ -> None
        // Classify every BUF mention of `e`. `ctx` = the conditions of the
        // enclosing then-branches; `rawConds` = let-bound guarded views and
        // the conditions their uses need; `phase`: 0 = before the row's
        // writer (lags only), 1 = after it (lags and the current row), 2 =
        // a seed statement after the seed writer (row reads < lo).
        let rec walk (phase: int) (ctx: IRExpr list) (raws: Map<IRId, IRExpr list>) (e: IRExpr) : unit =
            if bad.IsSome then () else
            match e with
            | IRAssign (IndexOfNamed (b, _, _, _), _) when b = bufId -> fail "a second store into the trajectory"
            | IRAssign (IRVar (b, _), _) when b = bufId -> fail "a whole-array assignment of the trajectory"
            | IndexOfNamed (b, _, i0 :: rest, _) when b = bufId ->
                rest |> List.iter (walk phase ctx raws)
                (match i0 with
                 | IRLit (IRLitInt r) when phase = 2 && r >= 0L && r < lo -> ()
                 | IRLit _ -> fail "a read of a FIXED row inside the sweep (its slot would have to outlive the sweep)"
                 | IRVar (v, _) when v = n -> if phase <> 1 then fail "a read of the current row before its complete write"
                 | IRBinOp (_, IRSub, IRVar (v, _), IRLit (IRLitInt c), _) when v = n && c >= 1L && c <= lo ->
                    if c > maxLag then maxLag <- c
                 | IRIf _ ->
                    (match clampOf n hi i0 with
                     | Ok (lag, conds) ->
                        (match lag with Some c -> noteLag c | None -> ())
                        if not (conds |> List.forall (fun c -> List.contains c ctx)) then
                            fail "a guarded trajectory read used outside its guard (the clamp's slot could hold another row)"
                     | Error why -> fail why)
                 | _ -> fail "a trajectory read at a leading index the ring audit cannot bound (not `n - c`, a guarded lag, the current row or a seed row)")
            | IRVar (b, _) when b = bufId -> fail "the trajectory is used whole inside the recursion (an alias, an argument or a view of the whole buffer)"
            | IRVar (r, _) when raws.ContainsKey r ->
                if not (raws.[r] |> List.forall (fun c -> List.contains c ctx)) then
                    fail "a guarded row view used outside its guard (the clamp's slot could hold another row)"
            | IRLet (id, (IndexOfNamed (b, _, (IRIf _ as i0) :: rest, _) as v), body) when b = bufId ->
                // A hoisted guarded row view: forming it is safe (an in-range
                // slot); every USE must sit under the guard.
                rest |> List.iter (walk phase ctx raws)
                (match clampOf n hi i0 with
                 | Ok (lag, conds) ->
                    (match lag with Some c -> noteLag c | None -> ())
                    walk phase ctx (Map.add id conds raws) body
                 | Error why -> fail why)
            | IRIf (c, a, b) ->
                walk phase ctx raws c
                walk phase (c :: ctx) raws a
                walk phase ctx raws b
            | ExprShape (children, _) -> children |> List.iter (walk phase ctx raws)
        // Callables never see the buffer (a capture would read it whole).
        (match foreignNaming bufId false with
         | Some why -> fail ("the trajectory's buffer is named outside the recursion: " + why)
         | None -> ())
        // a buffer read hidden in a type (a range's extent ..) is outside
        // every classified site
        if (pre |> List.exists (fun (_, v) -> mentionsInTypes bufId v)) || (step |> List.exists (fun (_, v) -> mentionsInTypes bufId v)) then
            fail "the trajectory's buffer is read inside a type's extent (a range bound or an index extent: not a classified read)"
        // The seed: lo = 1 needs exactly one writer of row 0, complete.
        let isRowWriter (rowIdx: IRExpr -> bool) (v: IRExpr) =
            match v with
            | IRAssign (IndexOfNamed (b, _, [ i0 ], _), _) when b = bufId && trailing.IsEmpty -> rowIdx i0
            | IRForRange (k, IRLit (IRLitInt 0L), IRLit (IRLitInt m), cellBody) when Some m = cells && trailing.Length = 1 ->
                let cs, cfin = chainOf cellBody
                (match List.tryLast cs, cfin with
                 | Some (_, IRAssign (IndexOfNamed (b, _, [ i0; IRVar (k2, _) ], _), _)), IRLit IRLitUnit ->
                    b = bufId && k2 = k && rowIdx i0
                 | _ -> false)
            | _ -> false
        let writerValue (v: IRExpr) : IRExpr =
            // the writer's own reads (its value / cell chain minus the store)
            match v with
            | IRAssign (IRIndex (_, _, _), rhs) -> rhs
            | IRForRange (_, _, _, cellBody) ->
                let cs, _ = chainOf cellBody
                let lets = cs |> List.take (cs.Length - 1)
                let rhs = match List.last cs with (_, IRAssign (_, r)) -> r | _ -> IRLit IRLitUnit
                rebuild lets rhs
            | _ -> v
        let preWriters = pre |> List.filter (fun (_, v) -> mentions bufId v && isRowWriter (fun i -> i = IRLit (IRLitInt 0L)) v)
        if lo = 1L && preWriters.Length <> 1 then fail "the seed does not write row 0 exactly once and completely"
        elif lo = 0L && not preWriters.IsEmpty then fail "a statement before a seedless recursion writes the trajectory"
        elif lo <> 0L && lo <> 1L then fail "the recursion loop starts past row 1"
        let mutable seenSeed = false
        for (_, v) in pre do
            if bad.IsNone && mentions bufId v then
                if isRowWriter (fun i -> i = IRLit (IRLitInt 0L)) v && not seenSeed then
                    seenSeed <- true
                    if mentions bufId (writerValue v) then fail "the seed row reads the trajectory"
                elif seenSeed then walk 2 [] Map.empty v
                else fail "a statement before the seed reads the trajectory"
        // A value bound before the sweep from the seed rows (a row view of
        // row 0) must not be used inside it: row 0's slot is overwritten R
        // steps later. Closed transitively over the pre-sweep lets.
        let preViews =
            let mutable s = pre |> List.filter (fun (_, v) -> mentions bufId v) |> List.map fst |> Set.ofList
            let mutable grow = true
            while grow do
                grow <- false
                for (id, v) in pre do
                    if not (s.Contains id) && (s |> Set.exists (fun x -> mentions x v)) then
                        s <- s.Add id
                        grow <- true
            s
        if step |> List.exists (fun (_, v) -> preViews |> Set.exists (fun x -> mentions x v || mentionsInTypes x v)) then
            fail "a value read from the seed rows before the sweep is used inside it (row 0's slot is overwritten R steps later)"
        // The step: exactly one complete writer of row n; before it lags only.
        let isN (i: IRExpr) = match i with IRVar (v, _) -> v = n | _ -> false
        let writers = step |> List.filter (fun (_, v) -> isRowWriter isN v)
        if writers.Length <> 1 then fail "the step does not write row n exactly once and completely (a conditional, partial or second writer)"
        let mutable phase = 0
        // Raw guarded views are let-bound in the STEP's own spine (the
        // desugar hoists them before the writer); thread them through.
        let mutable raws : Map<IRId, IRExpr list> = Map.empty
        for (id, v) in step do
            if bad.IsNone then
                if phase = 0 && isRowWriter isN v then
                    walk 0 [] raws (writerValue v)
                    phase <- 1
                else
                    match v with
                    | IndexOfNamed (b, _, (IRIf _ as i0) :: rest, _) when b = bufId ->
                        rest |> List.iter (walk phase [] raws)
                        (match clampOf n hi i0 with
                         | Ok (lag, conds) ->
                            (match lag with Some c -> noteLag c | None -> ())
                            raws <- Map.add id conds raws
                         | Error why -> fail why)
                    | _ -> walk phase [] raws v
        match bad with
        | Some why -> Error why
        | None -> Ok maxLag
    // ---- consumers ------------------------------------------------------
    // (kind, node) per use of T; the rewrite replaces each node.
    let tryRegion (tId: IRId) (block: IRExpr) (body: IRExpr) : (Result<IRExpr, string> * string list) option =
        match block with
        | IRLet (bufId, IRUninit bufT, chain) ->
            let stmts, fin = chainOf chain
            match fin, List.tryLast stmts, bufT with
            | IRVar (b, _), Some (loopId, IRForRange (nId, IRLit (IRLitInt lo), IRLit (IRLitInt hi), stepBody)), ArrayElem at
                    when b = bufId && not at.IsVirtual ->
                let pre = stmts |> List.take (stmts.Length - 1)
                let rank = at.IndexTypes.Length
                let lead = at.IndexTypes.Head
                let trailingCells =
                    match at.IndexTypes.Tail with
                    | [] -> Some 1L
                    | [ ix ] when plainIx ix -> staticExt ix
                    | _ -> None
                let elem = at.ElemType
                let evidenceBase = [ $"the {hi}-row trajectory ({hi} x {defaultArg trailingCells 0L} cells)" ]
                let decline (why: string) = Some (Error why, evidenceBase)
                if staticExt lead <> Some hi || not (plainIx lead) then decline "the recursion loop does not run over the buffer's whole leading axis"
                elif rank > 2 || trailingCells.IsNone then decline "v1 rings rank-1 and rank-2 trajectories with a static plain trailing axis"
                elif not (scalarTy elem) then decline "the trajectory's cells are not scalars"
                else
                let stepStmts, stepFin = chainOf stepBody
                if stepFin <> IRLit IRLitUnit then decline "the step does not end in unit (an early exit or a value)" else
                let mutable breaks = false
                iterIRExpr (fun x -> match x with IRBreakIf _ | IRReplayRow _ -> breaks <- true | _ -> ()) chain
                if breaks then decline "a `while` guard or the freeze idiom (its epilogue repeats the last written row)" else
                // The type-carried walk is unbounded in depth but budgeted:
                // an exhausted budget is its own recorded decline (every check
                // built on the walk also fails closed on it).
                if (typeCarried chain).IsNone || (typeCarried body).IsNone then
                    decline "the expressions the region's types carry exceed the type-carried walk's budget (200000 nodes): fail closed" else
                // ---- the consumers in `body`
                let consumers = System.Collections.Generic.List<string * IRExpr>()
                let mutable cbad : string option = None
                let cfail (why: string) = if cbad.IsNone then cbad <- Some why
                let isT (e: IRExpr) = match e with IRVar (t, _) -> t = tId | _ -> false
                // A callable (a lambda) reading the trajectory reads it whole,
                // from a body this scan does not walk: decline.
                (match foreignNaming tId true with
                 | Some why -> cfail ("the trajectory is named outside its block: " + why + " -- a read this census cannot classify")
                 | None -> ())
                let rec gridNest (e: IRExpr) =
                    match e with
                    | IRLet (acc, seed, IRLet (_, IRForRange (a, IRLit (IRLitInt 0L), IRLit (IRLitInt ha), IRLet (_, IRForRange (bv, IRLit (IRLitInt 0L), IRLit (IRLitInt hb), IRLet (_, IRAssign (IRVar (acc2, _), comb), IRLit IRLitUnit)), IRLit IRLitUnit)), cont))
                        when acc = acc2 && rank = 2 && ha = hi && Some hb = trailingCells
                             // EXACTLY the desugar's combine: `acc OP T(a, b)`
                             // or `k(acc, T(a, b))` -- nothing else reads a
                             // coordinate, so the moved combine is closed
                             && (let isRead (r: IRExpr) =
                                     match r with
                                     | IndexOfNamed (t, _, [ IRVar (ia, _); IRVar (ib, _) ], _) -> t = tId && ia = a && ib = bv
                                     | _ -> false
                                 match comb with
                                 | IRBinOp (_, _, IRVar (a2, _), rd, _) -> a2 = acc && isRead rd
                                 | IRApp (IRVar _, [ IRVar (a2, _); rd ], _) -> a2 = acc && isRead rd
                                 | _ -> false) ->
                        Some (acc, seed, a, bv, comb, cont)
                    | _ -> None
                let rec find (cond: bool) (e: IRExpr) : unit =
                    if cbad.IsSome || not (mentions tId e) then () else
                    match e with
                    | IRReduce (t, _, _) when isT t ->
                        if cond then cfail "a fold of the trajectory sits in a branch or loop body (v1 rides unconditional consumers)"
                        else consumers.Add ("C1", e)
                    | IRReduceCompute (IRApplyCombinator info, _, _) when (match info.Arrays with [ t ] -> isT t | _ -> false) ->
                        if cond then cfail "a fold of the trajectory sits in a branch or loop body (v1 rides unconditional consumers)"
                        else consumers.Add ("C2", e)
                    | IRCompute (IRApplyCombinator info) when (match info.Arrays with [ t ] -> isT t | _ -> false) ->
                        if cond then cfail "a row map of the trajectory sits in a branch or loop body (v1 rides unconditional consumers)"
                        else consumers.Add ("C3", e)
                    | _ when (gridNest e).IsSome ->
                        let (_, seed, _, _, comb, cont) = (gridNest e).Value
                        if cond then cfail "a fold of the trajectory sits in a branch or loop body (v1 rides unconditional consumers)"
                        else consumers.Add ("C4", e)
                        find cond seed
                        if mentions tId comb && count tId comb <> 1 then cfail "the accumulator nest reads the trajectory twice"
                        find cond cont
                    | IRIndex (t, _, _) when isT t -> cfail "a fixed-row read of the trajectory after the recursion (rec-window's snapshots ride the surface only; a ring beside snapshots is v1.1)"
                    | IRVar (t, _) when t = tId -> cfail "the trajectory is used whole after the recursion (an escape: a value, an argument, an alias, a transpose or another operand)"
                    | IRIf (c, a, b) -> find cond c; find true a; find true b
                    | IRMatch _ -> (match e with ExprShape (ch, _) -> ch |> List.iter (find true))
                    | IRForRange (_, l, h, b) -> find cond l; find cond h; find true b
                    | IRBinOp (_, (IRAnd | IROr), a, b, _) -> find cond a; find true b
                    | ExprShape (children, _) -> children |> List.iter (find cond)
                find false body
                if mentionsInTypes tId body then
                    cfail "the trajectory is read inside a type's extent after the recursion (a range bound or an index extent: not a classified consumer)"
                match cbad with
                | Some why -> decline why
                | None ->
                // (`find` classifies every subtree that mentions T: a mention
                // outside a consumer node reaches its IRVar arm and declines;
                // the rewrite re-checks that no mention survives.)
                // ---- ring audit
                match auditRegion bufId bufT pre nId lo hi stepStmts with
                | Error why -> decline why
                | Ok k ->
                let r = k + 1L
                if not (r < hi && r <= max 8L (hi / 4L)) then
                    decline $"dense coverage: a {r}-row ring (max lag K = {k}) against an extent of {hi}"
                else
                // ---- the threaded step (C++ persistent team)
                let stepEntry = RecStepTable.tryStep nId |> Option.filter (fun en -> en.BufferId = bufId)
                let rowVec = match elem with IRTScalar (ETFloat64 | ETFloat32) -> true | _ -> false
                let strict = Blade.Toolchain.fpReproEnabled () || enclosingRepro
                let teamForm =
                    match stepEntry with
                    | Some en -> Some (recStepFoldForm strict (foldLanesSwitchedOff ()) rowVec en.Fold.Value)
                    | None -> None
                if Blade.Toolchain.ompThreadEmissionEnabled () && teamForm = Some RecStepStrictPostPass then
                    decline "the threaded step's strict post-pass folds COMPLETE rows after the team (BLADE_FP_REPRO=1, `where repro`, a row kernel other than the self-dot, or lanes off): the trajectory must stay whole"
                else
                // ---- per-consumer admission
                // Replace each consumer node (by reference) through `by`.
                let replaceNodes (table: (IRExpr * IRExpr) list) (e: IRExpr) =
                    let rec go (x: IRExpr) =
                        match table |> List.tryFind (fun (n0, _) -> obj.ReferenceEquals (n0, x)) with
                        | Some (_, by) -> go by
                        | None ->
                            match x with
                            | ExprShape ([], _) -> x
                            | ExprShape (children, rb) -> rb (children |> List.map go)
                    go e
                let interval =
                    // every statement of `rest` up to (and including) the one
                    // holding the last consumer, each consumer node masked by
                    // a sentinel (a C4 nest keeps its continuation): the
                    // consumers' own state writes are not the interval's
                    // (the sentinel is T's own id -- no use of T survives the
                    // masking, and minting ids here would shift every id a
                    // declined region's program emits)
                    let sentinel = tId
                    let sV = IRVar (sentinel, IRTUnit)
                    let masked =
                        replaceNodes
                            (consumers |> List.ofSeq |> List.map (fun (kind, node) ->
                                if kind = "C4" then
                                    let (_, _, _, _, _, cont) = (gridNest node).Value
                                    node, IRLet (sentinel, sV, cont)
                                else node, sV)) body
                    let ss, fin = chainOf masked
                    let rec upTo (acc: IRExpr list) (xs: (IRId * IRExpr) list) =
                        match xs with
                        | [] -> List.rev (fin :: acc)
                        | (_, v) :: t ->
                            let acc' = v :: acc
                            if mentions sentinel (rebuild t fin) then upTo acc' t else List.rev acc'
                    upTo [] ss
                let boundInBody =
                    let s = System.Collections.Generic.HashSet<IRId>()
                    iterIRExpr (fun x -> match x with IRLet (id, _, _) -> s.Add id |> ignore | IRForRange (id, _, _, _) -> s.Add id |> ignore | _ -> ()) body
                    iterIRExpr (fun x -> match x with IRLet (id, _, _) -> s.Add id |> ignore | _ -> ()) block
                    s
                let assigned =
                    let s = System.Collections.Generic.HashSet<IRId>()
                    iterIRExpr (fun x -> match x with IRAssign (IRVar (id, _), _) -> s.Add id |> ignore | _ -> ()) body
                    iterIRExpr (fun x -> match x with IRAssign (IRVar (id, _), _) -> s.Add id |> ignore | _ -> ()) block
                    s
                // A seed moves ahead of the recursion: it must be CLOSED over
                // values that dominate it -- literals, scalar arithmetic,
                // casts, selects and reads of scalars bound before the
                // recursion and never assigned. No call, no callable, no
                // array form (a lambda's body could read a later binding).
                let seedOk (s: IRExpr) =
                    let rec closed (x: IRExpr) =
                        match x with
                        | IRLit _ -> true
                        | IRVar (id, ty) ->
                            not (callables.ContainsKey id) && not (boundInBody.Contains id) && not (assigned.Contains id)
                            && (match ty with IRTScalar _ -> true | _ -> false)
                        | IRBinOp (_, _, a, b, _) -> closed a && closed b
                        | IRUnaryOp (_, a) -> closed a
                        | IRIf (c, a, b) -> closed c && closed a && closed b
                        | _ -> false
                    closed s && totalExpr s && not (writes s)
                let foldKernelOk (fk: IRExpr) (f: IRCallable) = plainScalarCallable 2 f && totalCallable f && not (writes fk)
                let rowCells = defaultArg trailingCells 1L
                let mapKernelOk (info: ApplyInfo) =
                    not info.HasReynolds && not info.IsCoIteration && info.KernelOutputRank = 0
                    && (match resolveE info.Kernel with
                        | Some kc ->
                            kc.Params.Length = 1 && kc.Captures.IsEmpty && not kc.IsStatic && not kc.IsOmpParallel
                            && not kc.IsCudaKernel && not kc.IsMpiParallel && not kc.IsArityPoly && scalarTy kc.RetType
                            && (if rank = 2 then rowKernelTotal rowCells kc else totalCallable kc)
                            && not (writes info.Kernel)
                        | None -> false)
                let admit (kind: string, node: IRExpr) : Result<string, string> =
                    match kind, node with
                    | "C1", IRReduce (_, fk, init) ->
                        if rank <> 1 then Error "a whole-array fold of a rank-2 trajectory that is not the default partial fold"
                        else
                        (match resolveE fk with
                         | Some f when f.IsOmpParallel -> Error "the fold kernel requests omp (a fused serial chain would drop the request)"
                         | Some f when foldKernelOk fk f ->
                            (match init with
                             | Some s when not (seedOk s) -> Error "the fold's seed may panic, write, or read a value bound or assigned after the recursion"
                             | None when not (match elem with IRTScalar (ETFloat64 | ETFloat32 | ETInt64) -> true | _ -> false) -> Error "an unseeded fold of a cell type the placeholder state cannot hold (v1: Float64, Float32, Int64 cells)"
                             | None when lo = 0L -> Ok "a full fold of the rank-1 trajectory with no seed arm: acc <- (n == 0 ? x(0) : F(acc, x(n))) after each row, ascending from row 0"
                             | _ -> Ok "a full fold of the rank-1 trajectory: acc <- F(acc, x(n)) after each row, ascending")
                         | Some _ -> Error "the fold kernel is not a pure total capture-free scalar kernel"
                         | None -> Error "the fold kernel does not resolve to a callable")
                    | "C2", IRReduceCompute (IRApplyCombinator info, fk, init) ->
                        (match resolveE fk with
                         | Some f when f.IsOmpParallel -> Error "the fold kernel requests omp (a fused serial chain would drop the request)"
                         | Some f when foldKernelOk fk f && mapKernelOk info ->
                            if not (seedOk init) then Error "the fold's seed may panic, write, or read a value bound or assigned after the recursion"
                            else Ok "a fold over a map of the trajectory: acc <- F(acc, K(x(n))) after each row, ascending from the seed"
                         | Some _ -> Error "the map or fold kernel is not a pure total capture-free scalar kernel (or the map is not a plain row map)"
                         | None -> Error "the fold kernel does not resolve to a callable")
                    | "C3", IRCompute (IRApplyCombinator info) ->
                        if not (mapKernelOk info) then Error "the row map's kernel is not a pure total capture-free scalar kernel of one row (or the map is not plain)"
                        else
                        (match info.OutputType with
                         | ArrayElem ot when ot.IndexTypes.Length = 1 && staticExt ot.IndexTypes.Head = Some hi && not ot.IsVirtual ->
                            Ok "a row map of the trajectory: out(n) <- K(x(n)) right after each row (out is stored)"
                         | _ -> Error "the row map's output is not a dense vector over the recursion axis")
                    | "C4", _ ->
                        let (_, seed, _, _, comb, _) = (gridNest node).Value
                        let combOk =
                            match comb with
                            | IRBinOp (_, op, IRVar _, _, _) ->
                                (match op, elem with
                                 | (IRAdd | IRSub | IRMul), _ -> true
                                 | IRDiv, IRTScalar (ETFloat64 | ETFloat32) -> true
                                 | _ -> false)
                            | IRApp (kf, [ IRVar _; _ ], _) ->
                                (match resolveE kf with Some f -> not f.IsOmpParallel && foldKernelOk kf f | None -> false)
                            | _ -> false
                        if not combOk then Error "the accumulator nest's combine may panic or is not a pure total kernel"
                        elif not (seedOk seed) then Error "the fold's seed may panic, write, or read a value bound or assigned after the recursion"
                        else Ok "the rank-2 full fold: for j, acc <- op(acc, x(n, j)) right after each row (the nest's own row-major chain)"
                    | _ -> Error "internal: unknown consumer"
                let verdicts = consumers |> Seq.map (fun c -> c, admit c) |> List.ofSeq
                match verdicts |> List.tryPick (fun (_, v) -> match v with Error why -> Some why | Ok _ -> None) with
                | Some why -> decline why
                | None ->
                // The producer writes nothing outside its buffer (ring or
                // not, consumers or not): a write is how a row VIEW could
                // escape the step it was formed in (a callee storing its
                // argument in a module array outlives the row's slot). The one
                // exemption is a SCALAR accumulator bound outside the region
                // -- row-fold fusion's own in-sweep fold state -- which cannot
                // hold a view; its value is still audited.
                // The CHAIN without the buffer's binder (so the buffer counts as
                // outside storage), the classified row stores replaced by their
                // values (the audit proved they are the only syntactic stores),
                // and assignments to SCALAR lets bound outside the region
                // masked: what remains must write NOTHING -- no write into an
                // earlier row through a view, an alias or a `mut` parameter
                // (the moved folds read rows once, right after their write),
                // no storage a row view could escape into.
                let chainMasked =
                    let outsideScalar (id: IRId) (t: IRType) =
                        (match t with IRTScalar _ -> true | _ -> false) && not (boundInBlock block id)
                    mapIRExpr (fun x ->
                        match x with
                        | IRAssign (IndexOfNamed (b, _, _, _), v) when b = bufId -> v
                        | IRAssign (IRVar (id, t), v) when outsideScalar id t -> v
                        | _ -> x) chain
                // Effects hidden in type-carried expressions (a range bound
                // that calls) are outside the write analysis: every expression
                // the region and the callables it reaches carry in types must
                // be literals, names and scalar arithmetic.
                let typesQuiet (roots: IRExpr list) =
                    let cs, payloads = reachableFrom roots
                    roots |> List.forall typeCarriedSimple
                    && payloads |> List.forall typeCarriedSimple
                    && cs |> List.forall (fun c ->
                        typeCarriedSimple c.Body
                        && (c.RetType :: (c.Params |> List.map (fun p -> p.Type)))
                           |> List.forall (fun t -> irTypeExtentExprs t |> List.forall typeCarriedSimple
                                                     && irTypeExtentExprs t |> List.forall (fun e -> match e with IRLit _ | IRVar _ | IRParam _ | IRBinOp _ | IRUnaryOp _ -> true | _ -> false)))
                if writes chainMasked then
                    decline "the recursion may write storage other than its own row stores (an earlier row through a view, an alias or a `mut` parameter, or storage a row view could escape into)"
                elif not (typesQuiet [ chain ]) then
                    decline "an expression a type carries in the recursion (or in a function it calls) computes (a call or an array form): its effects are not audited"
                elif consumers.Count > 0 && not (typesQuiet (interval @ (consumers |> Seq.map snd |> List.ofSeq))) then
                    decline "an expression a type carries between the recursion and its last fold (or in a kernel it calls) computes: its effects are not audited"
                elif consumers.Count > 0 && writes block then
                    decline "the recursion (the producer) may write storage outside its own buffer (the folds would move across it)"
                elif consumers.Count > 0 && (interval |> List.exists writes) then
                    decline "a statement between the recursion and its last fold may write (the folds would move across it)"
                elif not (recFoldRingEnabled ()) then
                    Some (Error recFoldOptInDecline, evidenceBase @ [ "admitted: by default the trajectory would ring and its folds ride the sweep" ])
                else
                // ---- the rewrite
                let ringIx = { lead with Extent = IRLit (IRLitInt r); Tag = None; Id = builder.FreshId () }
                let ringT = mkArrayLike { at with IndexTypes = ringIx :: at.IndexTypes.Tail }
                let nVar =
                    let mutable found = None
                    iterIRExpr (fun x -> match x with IRVar (v, t) when v = nId && found.IsNone -> found <- Some (IRVar (v, t)) | _ -> ()) stepBody
                    defaultArg found (IRVar (nId, IRTScalar ETInt64))
                let bufV = IRVar (bufId, bufT)
                let rowRead (row: IRExpr) = IRIndex (bufV, [ row ], None)
                let elemZero =
                    match elem with
                    | IRTScalar ETFloat64 -> Some (IRLit (IRLitFloat 0.0))
                    | IRTScalar ETFloat32 -> Some (IRLit (IRLitFloat32 0.0f))
                    | IRTScalar ETInt64 -> Some (IRLit (IRLitInt 0L))
                    | _ -> None
                let unitLet (v: IRExpr) (tail: IRExpr) = IRLet (builder.FreshId (), v, tail)
                // K applied to row `row`. A rank-2 row kernel is INLINED (its
                // own lets freshened, its parameter bound to a row-view let),
                // as row-fold fusion splices its K: the LLVM lane has no
                // array-parameter calls. A `where repro` kernel, or one whose
                // body names a capturing callable (an inner lambda capturing
                // the row by id), stays a call (the C++ lane; the LLVM lane
                // then falls back, its refusal recorded).
                let applyRowKernel (kExpr: IRExpr) (kc: IRCallable) (row: IRExpr) (k: IRExpr -> IRExpr) : IRExpr =
                    let pId = builder.FreshId ()
                    let pV = IRVar (pId, kc.RetType)
                    let capturing =
                        let mutable hit = false
                        iterIRExpr (fun x -> match x with IRVar (id, _) -> (match callables.TryGetValue id with | true, c when not c.Captures.IsEmpty -> hit <- true | _ -> ()) | _ -> ()) kc.Body
                        hit
                    if rank = 1 || kc.IsRepro || capturing then IRLet (pId, IRApp (kExpr, [ rowRead row ], kc.RetType), k pV)
                    else
                    let binders = System.Collections.Generic.List<IRId>()
                    iterIRExpr (fun x -> match x with IRLet (id, _, _) | IRForRange (id, _, _, _) -> binders.Add id | _ -> ()) kc.Body
                    let mapped = binders |> Seq.distinct |> Seq.map (fun id -> id, builder.FreshId ()) |> Map.ofSeq
                    let rowId = builder.FreshId ()
                    let p = kc.Params.Head
                    let body =
                        mapIRExpr (fun x ->
                            match x with
                            | IRVar (id, t) when id = p.VarId -> IRVar (rowId, t)
                            | IRVar (id, t) -> (match Map.tryFind id mapped with Some m -> IRVar (m, t) | None -> x)
                            | IRLet (id, v, b) -> (match Map.tryFind id mapped with Some m -> IRLet (m, v, b) | None -> x)
                            | IRForRange (id, l, h, b) -> (match Map.tryFind id mapped with Some m -> IRForRange (m, l, h, b) | None -> x)
                            | _ -> x) kc.Body
                    IRLet (rowId, rowRead row, IRLet (pId, body, k pV))
                // per consumer: (state lets before the region, update(row) -> IR, replacement)
                let plans =
                    consumers |> List.ofSeq |> List.map (fun (kind, node) ->
                        match kind, node with
                        | "C1", IRReduce (_, fk, init) ->
                            let f = (resolveE fk).Value
                            let accId = builder.FreshId ()
                            let accT = f.RetType
                            let accV = IRVar (accId, accT)
                            let initV = match init with Some s -> s | None -> (defaultArg elemZero (IRLit (IRLitInt 0L)))
                            let upd (row: IRExpr) (isSeed: bool) =
                                if init.IsNone && isSeed then IRAssign (accV, rowRead row)
                                elif init.IsNone && lo = 0L then
                                    // no seed arm: row 0 is written by the sweep's
                                    // first step; the chain starts there (a VALUE
                                    // select, the placeholder state never folded)
                                    let first = IRBinOp (IRElementwise, IREq, IRUnaryOp (IRCast (ETInt64, SrcLoc.Nowhere), row), IRLit (IRLitInt 0L), SrcLoc.Nowhere)
                                    IRAssign (accV, IRIf (first, rowRead row, IRApp (fk, [ accV; rowRead row ], accT)))
                                else IRAssign (accV, IRApp (fk, [ accV; rowRead row ], accT))
                            ([ accId, initV ], upd, (node, accV))
                        | "C2", IRReduceCompute (IRApplyCombinator info, fk, init) ->
                            let f = (resolveE fk).Value
                            let accId = builder.FreshId ()
                            let accT = f.RetType
                            let accV = IRVar (accId, accT)
                            let kc = (resolveE info.Kernel).Value
                            let upd (row: IRExpr) (_: bool) =
                                applyRowKernel info.Kernel kc row (fun pV -> IRAssign (accV, IRApp (fk, [ accV; pV ], accT)))
                            ([ accId, init ], upd, (node, accV))
                        | "C3", IRCompute (IRApplyCombinator info) ->
                            let kc = (resolveE info.Kernel).Value
                            let outId = builder.FreshId ()
                            let outT = info.OutputType
                            let outV = IRVar (outId, outT)
                            let upd (row: IRExpr) (_: bool) =
                                applyRowKernel info.Kernel kc row (fun pV -> IRAssign (IRIndex (outV, [ row ], None), pV))
                            ([ outId, IRUninit outT ], upd, (node, outV))
                        | _ ->
                            let (accId, seed, _, bv, comb, cont) = (gridNest node).Value
                            let accT = (match comb with IRBinOp _ | IRApp _ -> (try typeOf comb with _ -> elem) | _ -> elem)
                            let upd (row: IRExpr) (_: bool) =
                                let j = builder.FreshId ()
                                let jV = IRVar (j, IRTScalar ETInt64)
                                let comb' =
                                    mapIRExpr (fun x ->
                                        match x with
                                        | IndexOfNamed (t, _, [ _; IRVar (ib, _) ], _) when t = tId && ib = bv -> IRIndex (bufV, [ row; jV ], None)
                                        | _ -> x) comb
                                IRForRange (j, IRLit (IRLitInt 0L), IRLit (IRLitInt rowCells),
                                            unitLet (IRAssign (IRVar (accId, accT), comb')) (IRLit IRLitUnit))
                            ([ accId, seed ], upd, (node, cont)))
                // Spliced into the spine (a let chain, never a block value).
                let updatesAt (row: IRExpr) (isSeed: bool) (tail: IRExpr) =
                    List.foldBack (fun (_, upd, _) acc ->
                        let lets, final = chainOf (upd row isSeed)
                        rebuild lets (unitLet final acc)) plans tail
                // the step: its statements, then every consumer's update of row n
                let step' = rebuild stepStmts (updatesAt nVar false (IRLit IRLitUnit))
                let pre' =
                    if lo = 1L then pre @ fst (chainOf (updatesAt (IRLit (IRLitInt 0L)) true (IRLit IRLitUnit)))
                    else pre
                let chain' = rebuild (pre' @ [ loopId, IRForRange (nId, IRLit (IRLitInt lo), IRLit (IRLitInt hi), step') ]) (IRVar (bufId, ringT))
                // the ring: every leading subscript of BUF, every BUF type
                let ringed =
                    mapIRExpr (fun x ->
                        match x with
                        | IndexOfNamed (b, _, i0 :: rest, ident) when b = bufId -> IRIndex (IRVar (bufId, ringT), recRingIndex r i0 :: rest, ident)
                        | IRVar (b, _) when b = bufId -> IRVar (bufId, ringT)
                        | _ -> x) chain'
                let block' = IRLet (bufId, IRUninit ringT, ringed)
                let body' = replaceNodes (plans |> List.map (fun (_, _, rp) -> rp)) body
                if mentions tId body' || mentionsInTypes tId body' then decline "internal: a use of the trajectory survived the consumer rewrite" else
                let states = plans |> List.collect (fun (s, _, _) -> s)
                let result = rebuild states (IRLet (tId, block', body'))
                let consumerTxt = verdicts |> List.map (fun (_, v) -> match v with Ok t -> t | Error w -> w)
                let teamTxt =
                    match teamForm with
                    | Some RecStepInTeamPartials -> [ "the threaded step folds each worker's chunk inside the team (licensed self-dot partials); after the team only the partials are read" ]
                    | Some RecStepNoFold -> [ "the threaded step keeps its persistent team; nothing reads the rows after it" ]
                    | Some RecStepStrictPostPass -> [ "the time loop is emitted without a team in this build (threads suppressed); the C++ emitter re-checks recStepFoldForm and runs the plain serial loop over the ring if it would fold complete rows" ]
                    | None -> []
                let licTxt =
                    if consumers.Count = 0 then [ "no fold moves: the trajectory's consumer already rides the sweep (row-fold fusion) or nothing reads it; the ring changes no value in any build" ]
                    else [ "fold order: every chain applies its kernel to the same terms in the same ascending order from the same seed / first element (bitwise under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off); in a LICENSED build a full fold runs its serial chain instead of the lane tree and a row fold its own licensed form instead of the bitwise row jam (licensed association changes)"
                           "failure order: the moved kernels and seeds are total and write nothing, and nothing from the recursion through the last fold writes (the shared write analysis), so evaluating them inside the sweep is unobservable; the fold kernels are called (a rank-2 row kernel is inlined on a row view, a `where repro` one stays a call)" ]
                let ev =
                    [ $"ring of {r} rows (max lag K = {k}) in place of the {hi}-row trajectory, {rowCells} cells a row; resident rows {hi} -> {r} ({hi * rowCells} -> {r * rowCells} cells)" ]
                    @ (if consumerTxt.IsEmpty then [] else [ "consumers: " + String.concat "; " consumerTxt ])
                    @ teamTxt @ licTxt
                    @ [ "the ring: row i in slot i mod R, written once and overwritten R steps later; every selected read is a lag c <= K, the current row after its complete write, or a seed row before the sweep (each guarded zero-history read used only under its guard)" ]
                Some (Ok result, ev)
            | _ -> None
        | _ -> None
    let rec rw (e: IRExpr) : IRExpr =
        match e with
        | IRLet (tId, block, body) ->
            (match tryRegion tId block body with
             | Some (Ok e', ev) ->
                record subject Blade.Effects.Applied ev
                // the rewritten region's tail may hold further regions
                let rec descend (x: IRExpr) =
                    match x with
                    | IRLet (t, b, rest) when t = tId -> IRLet (t, b, rw rest)
                    | IRLet (id, v, rest) -> IRLet (id, v, descend rest)
                    | _ -> x
                descend e'
             | Some (Error why, ev) ->
                record subject (Blade.Effects.Declined why) (ev @ [ "the trajectory is stored whole; its folds (if any) stay where they are" ])
                IRLet (tId, rwInner block, rw body)
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
            enclosingRepro <- f.IsRepro
            currentFn <- f.Id
            currentBinding <- -1
            { f with Body = rw f.Body })
    let newBindings =
        modul.Bindings |> List.map (fun b ->
            subject <- b.Name
            enclosingRepro <- false
            currentFn <- -1
            currentBinding <- b.Id
            { b with Value = rw b.Value })
    { modul with Functions = newFunctions; Bindings = newBindings }
