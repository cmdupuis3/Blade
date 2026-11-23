// Shared analyses of the optimizer: type-carried extents, CSE facts, the write / retention analyses, payloads and schedules (split out of Optimize.fs, cleanup K19).
module Blade.OptimizeAnalysis

open Blade.Types

open Blade.Ast
open Blade.IR
open Blade.IRMono

/// The ids of the module's STREAMED provider reads (`s.vars.A |> z.stream`):
/// no array named after them ever exists, so a pass that moves a map over one
/// into a consumer (a fused fold, a spliced operand, a recomputation) hands the
/// emitters an operand they can only read through the segment route of a
/// STORED `|> compute` (G135 item 2 / lane G item G1: the fused fold rendered
/// `A.data` for an undeclared A, BL9002). The elision passes decline them.
let streamedProviderIds (modul: IRModule) : Set<IRId> =
    modul.ProviderReads |> Map.toSeq |> Seq.filter (fun (_, spec) -> spec.Streamed) |> Seq.map fst |> Set.ofSeq

let streamedOperandWhy = "an operand of the producer is a streamed provider read (no array exists: the stored `|> compute` is its segment-route materialization, which the consumer then reads)"

// (moved ahead of the scratch-reuse planner in round 4 wave 7, lane c, gap
// G220: its liveness and assignment scans read type-carried expressions too)
/// The extent expressions a TYPE carries (its index records' extents, a tagged
/// index's anonymous extent, nested types' extents): what the generic child
/// walk (`childrenOf`) never enters.
let rec irTypeExtentExprs (t: IRType) : IRExpr list =
    match t with
    | IRTScalar _ | IRTUnit | IRTNat _ | IRTNamed _ | IRTInfer _ -> []
    | IRTTuple ts -> List.collect irTypeExtentExprs ts
    | IRTLoop l -> List.collect irTypeExtentExprs l.ArrayTypes @ (l.KernelType |> Option.map irTypeExtentExprs |> Option.defaultValue [])
    | IRTComputation t | IRTPoly (t, _) | IRTUnitAnnotated (t, _) -> irTypeExtentExprs t
    | IRTIdxTagged (t, tag) -> irTypeExtentExprs t @ (match tag with IRefAnon (_, e) -> [ e ] | _ -> [])
    | IRTDist (_, t, axes) -> irTypeExtentExprs t @ (axes |> List.map (fun ix -> ix.Extent))
    | IRTGroupKeys (a, b, _) -> [ a.Extent; b.Extent ]
    | IRTArrow (slots, r, _) ->
        (slots |> List.collect (function SIdx ix | SIdxVirt ix -> [ ix.Extent ] | SVal st -> irTypeExtentExprs st)) @ irTypeExtentExprs r

let private arrayTypeExtentExprs (at: IRArrayType) : IRExpr list =
    (at.IndexTypes |> List.map (fun ix -> ix.Extent)) @ irTypeExtentExprs at.ElemType

/// Every type and index expression embedded in the nodes of `e` -- a
/// variable's or call's type, an array literal's, an apply's output, operand
/// and shared index types, a range's index records, an `ungroup`'s source
/// index records -- as extent expressions; and, recursively, those embedded
/// in the extracted extents themselves, to COMPLETION (types are finite
/// trees; each extent object is expanded once, by reference, which also keeps
/// it linear on a type shared by many nodes). Each extent object is returned
/// once.
let embeddedExtentExprs (e: IRExpr) : IRExpr list =
    let acc = ResizeArray<IRExpr>()
    let seen = System.Collections.Generic.HashSet<IRExpr>(HashIdentity.Reference)
    let rec visit (depth: int) (root: IRExpr) =
        let found = ResizeArray<IRExpr>()
        let addTy (t: IRType) = found.AddRange(irTypeExtentExprs t)
        let addIx (ixs: IRIndexType list) = for ix in ixs do found.Add ix.Extent
        iterIRExpr (fun n ->
            match n with
            | IRVar (_, ty) | IRParam (_, _, ty) | IRZero ty | IRUnseededSeed (_, ty, _) | IRUninit ty | IRAppAt (_, _, ty, _) -> addTy ty
            | IRArrayLit (_, at) -> found.AddRange(arrayTypeExtentExprs at)
            | IRApplyCombinator info ->
                addTy info.OutputType
                for at in info.ArrayTypes do found.AddRange(arrayTypeExtentExprs at)
                addIx info.SharedIndexTypes
                addIx info.KernelTDims
            | IRComposeApply info -> addTy info.OutputType
            | IRMethodFor mf ->
                for at in mf.ArrayTypes do found.AddRange(arrayTypeExtentExprs at)
                addIx mf.SharedIndexTypes
            | IRRange (ixs, _) -> addIx ixs
            | IRVirtualReverse ix -> addIx [ ix ]
            // the source index records `childrenOf` does not hand out
            | IRUngroup (_, src) -> addIx [ src ]
            | IRUngroupRows (_, _, src) -> addIx [ src ]
            | IRUngroupGrid (_, srcs, _) -> addIx srcs
            // EXHAUSTIVE (no wildcard; PM35's constructor table): every other
            // constructor carries no type and no index record -- its
            // expressions are children the walk above already visits. A new
            // IRExpr variant is classified here, not defaulted
            // (OptimizeTests `rec_fold_ring_constructor_table_walkers` pins
            // every type-carrying field against this function).
            | IRLit _ | IRBinOp _ | IRUnaryOp _ | IRIndex _ | IRSlice _ | IRCurry _ | IRTuple _ | IRComplex _
            | IRFma _ | IRRandAt _ | IRTupleProj _ | IRTupleCons _ | IRTupleDecons _ | IRFieldAccess _
            | IRStructLit _ | IRIf _ | IRMatch _ | IRLet _ | IRBeginPoolScope _ | IRReleasePools _
            | IRObjectFor _ | IRBind _ | IRParallel _ | IRFusion _ | IRChoice _ | IRFallback _
            | IRArrayProduct _ | IRComposeObj _ | IRComposeMeth _ | IRCompose _ | IRFunctorMap _ | IRPure _
            | IRCompute _ | IRReynolds _ | IRGuard _ | IRSequence _ | IRReplicate _ | IRMask _
            | IRIntersect _ | IRUnion _ | IRUnique _ | IRContains _ | IRDisplayEmit _ | IRDisplayJson _
            | IRDisplayNum _ | IRDisplayStr _ | IRGroupBy _ | IRGroupKeys _ | IRGroupBucket _ | IRSegments _
            | IRSegmentsGrid _ | IRGroupSizes _ | IRSort _ | IRReduce _ | IRReduceCompute _ | IRProdSum _
            | IRZip _ | IRAlign _ | IRStack _ | IRTranspose _ | IRDecompact _ | IRGram _ | IRGramApply _
            | IRMatmul _ | IREigh _ | IRSolve _ | IRLu _ | IRLuSolve _ | IRArrayNegate _ | IRArrayConjugate _
            | IRReverse _ | IRShift _ | IRDiag _ | IRJoin _ | IRSubset _ | IRHaloUnhash _ | IRArity _ | IRNth
            | IRRank _ | IRPolyIndex _ | IRPolyTail _ | IRExtent _ | IRRaggedLookup _ | IRCompoundMask _
            | IRCompoundProject _ | IRSparseKeys _ | IROrbitClass _ | IROpaqueExtent | IRAssign _ | IRForRange _
            | IRConstraintCheck _ | IRBreakIf _ | IRReplayRow _ -> ()) root
        for x in found do
            // COMPLETE (gap G89, lane-D review): every extent is expanded
            // once, by reference -- extents are subterms of finite type
            // trees, so this terminates without a depth bound (the old bound
            // of 8 let a write nested deeper slip past every client: the
            // never-written closure, the foreign-reference census).
            if seen.Add x then
                acc.Add x
                visit (depth + 1) x
    visit 0 e
    List.ofSeq acc

/// A kernel reference resolved through the module's own function table
/// (the AsyncLocal CallablesTable is not installed at this point in the
/// pipeline, as the fusion pass notes).
let internal resolveCallableIn (funcs: Map<IRId, IRCallable>) (k: IRExpr) : IRCallable option =
    match k with
    | IRVar (fid, _) -> Map.tryFind fid funcs
    | _ -> None

/// LET-LEVEL COMMON-SUBEXPRESSION ELIMINATION over REPEATABLE values -- the
/// P0 consumer plan-fortran-killer-2 section 3 listed ("reuse the facts for
/// ... later CSE") and did not build. Straight-line only: inside one
/// function body, a let whose value is structurally identical to an EARLIER
/// let's value is dropped and every later reference reads the earlier one.
/// Equality of values is equality of results only when
///
///   * the value is REPEATABLE: no assignment, no display, no call whose
///     callee is unknown or not repeatable. A callee is judged by its
///     `Blade.Effects` summary (exactly as the fusion pass reads it) and,
///     for a lifted lambda (which carries no summary -- `Unknown`), by its
///     own body under the same rules; a kernel REFERENCE (the callable an
///     apply or reduce runs per cell) is judged like a call, and a
///     function-typed name the module cannot resolve (a let alias of a
///     lambda, a function parameter, another module's function) is the
///     worst case. A read of a DEFERRED let inherits its producer's facts:
///     the producer runs at the read;
///   * NOTHING BETWEEN THE TWO EVALUATIONS CAN WRITE what the value reads.
///     A body containing an assignment or a loop declines wholesale (cheap
///     and sound). Every other write reaches a body through a CALL -- a
///     callee assigning through a `mut` parameter, or to a module-level
///     `let mut` it names -- so a let whose value may write (a callee whose
///     summary says Mutates, or whose effects are unknown) is a BARRIER:
///     nothing computed before it is reused after it;
///   * neither let is NAMED by a writing evaluation anywhere in the body
///     (directly, or through a callable or deferred let it names): that is
///     the storage a `mut` argument writes, and merging it with its twin
///     would let the write reach a name the program never passed. (Not
///     IRModule.MutableArrayLets: in a function body a plain array `let`
///     arrives there too -- it is reassignable in its own scope, so the
///     checker marks it mutable -- and the table cannot tell a `let mut`
///     apart (observed: both plain lets of a two-map body are listed). An
///     assignment in the body already declines the whole body.)
///
/// A MayFail value is fine: the first evaluation already ran, so the second
/// could not newly fail. Deferred lets (a bare apply left for a join) are
/// never touched -- their identity is the join's sharing declaration, and
/// dropping one would change what the join emits. Trivial values (a
/// literal, a variable) are not worth a record.
///
/// Identity is STRUCTURAL equality of the IR, never a rendering of it (a
/// `%A` key printed floats at ~10 significant digits and truncated deep
/// trees, so `b * 0.1` and `b * 0.10000000001` compared equal). Float
/// literals compare by their BIT PATTERN (so `0.0` and `-0.0` stay apart),
/// and two callables are the same value when their whole records agree once
/// their own ids, names and parameter ids are erased -- computed lazily, for
/// the callables a candidate value actually names.
///
/// Every body with a hit records `cse` (applied, the pairs); with BLADE_CSE
/// off the same body records `cse` declined, naming the pairs it would have
/// merged; nothing otherwise.
type internal CseFacts = {
    /// Evaluating it may change a value another evaluation reads.
    MayWrite: bool
    /// Evaluating it again, with the same inputs, gives the same value and
    /// no other observable effect.
    Repeatable: bool
}

/// The CSE judge over one module: facts of an expression, callees resolved
/// through the module's own function table and memoized per callable. A
/// callable met again while its own facts are being computed (recursion)
/// reads as the worst case -- pessimistic, so a cycle can only decline.
let internal isFunctionTyped (t: IRType) : bool =
    match stripUnits t with
    // a NULLARY function is `IRTArrow([], ret, None)` (Types / IR reserve the
    // empty-slot arrow for it): a function; an arrow whose slots are ALL index
    // slots is an array typed as its index function, not a callable value
    | IRTArrow (slots, _, _) -> slots.IsEmpty || slots |> List.exists (function SVal _ -> true | _ -> false)
    | _ -> false

/// Public twin of isFunctionTyped: a FUNCTION-valued type (an arrow with a
/// value slot, or the nullary empty-slot arrow), not an array typed as an
/// index arrow. The callback tables key only these parameters.
let isFunctionValuedType (t: IRType) : bool = isFunctionTyped t

let internal cseFactsOf (funcs: Map<IRId, IRCallable>) : IRExpr -> CseFacts =
    let worst = { MayWrite = true; Repeatable = false }
    let memo = System.Collections.Generic.Dictionary<IRId, CseFacts>()
    let rec ofCallable (c: IRCallable) : CseFacts =
        match memo.TryGetValue c.Id with
        | true, r -> r
        | _ ->
            memo.[c.Id] <- worst
            let r =
                if not c.Effects.Unknown then
                    { MayWrite = c.Effects.Mutates; Repeatable = Blade.Effects.isRepeatable c.Effects }
                else ofExpr c.Body
            // A static function's call is not a runtime value this pass
            // reasons about (the old rule, kept).
            let r = if c.IsStatic then { r with Repeatable = false } else r
            memo.[c.Id] <- r
            r
    and ofExpr (e: IRExpr) : CseFacts =
        let mutable mayWrite = false
        let mutable repeatable = true
        iterIRExpr (fun n ->
            match n with
            | IRAssign _ | IRForRange _ ->
                mayWrite <- true
                repeatable <- false
            | IRReplayRow _ ->
                repeatable <- false
            | IRBeginPoolScope _ | IRReleasePools _
            | IRDisplayEmit _ | IRDisplayJson _ | IRDisplayNum _ | IRDisplayStr _ ->
                repeatable <- false
            // An unfilled let-rec buffer is an IDENTITY (the storage the
            // recursion writes), never a value two lets could share.
            | IRUninit _ ->
                repeatable <- false
            // A callable named anywhere -- a call head, a kernel, a callable
            // passed as an argument -- may run: judge it. (The IRApp arm
            // below leaves a resolvable head to this visit.)
            | IRVar (fid, t) ->
                (match Map.tryFind fid funcs with
                 | Some c ->
                    let cf = ofCallable c
                    if cf.MayWrite then mayWrite <- true
                    if not cf.Repeatable then repeatable <- false
                 | None ->
                    // A FUNCTION-typed name this module cannot resolve -- a
                    // let alias of a lifted lambda (`let k = lambda ..`), a
                    // function-typed parameter, another module's function
                    // used as a kernel -- may run and may do anything.
                    if isFunctionTyped t then
                        mayWrite <- true
                        repeatable <- false)
            | IRParam (_, _, t) when isFunctionTyped t ->
                mayWrite <- true
                repeatable <- false
            | CallOfNamed (fid, _, _, _) when Map.containsKey fid funcs -> ()
            // A head this module cannot resolve -- a lambda-valued variable,
            // a higher-order parameter, another module's function -- may do
            // anything.
            | IRApp _ ->
                mayWrite <- true
                repeatable <- false
            | _ -> ()) e
        { MayWrite = mayWrite; Repeatable = repeatable }
    ofExpr

/// Where an array value's STORAGE comes from, as far as a write through it is
/// concerned: storage made inside the walked body (`Fresh` -- a store into it
/// is invisible outside), the storage of some of the walked callable's
/// parameters (`Params`, by index -- the CALLER decides what that is), or
/// anything else (`External`: a captured or module binding, a view of one, a
/// call's array result, a form not proved fresh).
type WriteProvenance = { External: bool; Params: Set<int> }

/// The facts the write analysis reads from its context (gaps G41 / G43).
type WriteFacts = {
    /// A callee or kernel reference -> its callable (aliases already chased).
    Resolve: IRExpr -> IRCallable option
    /// An OUTER let's initializer (one not bound inside the walked body): the
    /// analysis follows deferred computations through it -- a named unforced
    /// map runs where it is read -- and stops at a materialization boundary
    /// (`|> compute`, a stored value).
    OuterLet: IRId -> IRExpr option
    /// `let mut a = Z` over a dense array deep-copies Z (genVarAliasBinding /
    /// Interp.Core): such a let owns fresh storage.
    CopiedLet: IRId -> bool
    /// Does an UNRESOLVED call head or function-typed kernel name count as a
    /// write? true for every decline and for the join refusal (a reordered or
    /// joined evaluation must be PROVED write-free); false only for the
    /// parallel-body refusal, which refuses what it can SEE (the front end
    /// already judges a parallel body's calls).
    UnknownIsWrite: bool
    /// A CALLBACK -- a function-valued parameter, by id -- to the argument
    /// expressions every call site passes for it (None: not a known
    /// callback). The analysis JOINS every candidate's effects at each
    /// application (Codex finding 22: never one candidate chosen on a
    /// boolean); a candidate it cannot resolve contributes top under
    /// UnknownIsWrite.
    Callbacks: IRId -> IRExpr list option
}

/// The summary of one callable: does a call write storage the caller can
/// observe regardless of its arguments (`External`), and which of its
/// parameters' storage does it write (`Params`)?
let private writeNone : WriteProvenance = { External = false; Params = Set.empty }
let private writeJoin (a: WriteProvenance) (b: WriteProvenance) =
    { External = a.External || b.External; Params = Set.union a.Params b.Params }

/// A value of this type holds no array storage (scalars, units, index tags,
/// tuples of those). Public for the C++ kernel-body copy (gap G46).
/// A7c round 6: the value types an inlined callee's non-owned lets may have
/// -- values that allocate in NEITHER lane: numeric and Bool / complex
/// scalars, index-tagged values whose INNER type is one of those, unit, and
/// tuples of those (bare Nat excluded: the LLVM lane has no scalar arm for it). String is EXCLUDED (the
/// C++ lane's std::string owns heap storage that follows its scope), as is
/// every array, loop, computation and function type. Deliberately separate
/// from holdsNoStorage (an array-storage predicate other passes share).
let allocationFreeValueType (t: IRType) : bool =
    let rec go (t: IRType) =
        match stripUnits t with
        | IRTScalar (ETFloat64 | ETFloat32 | ETInt64 | ETInt32 | ETBool | ETComplex64 | ETComplex128) -> true
        // an index tag is judged by its INNER type (a string-valued EnumIdx label
        // is a std::string in C++); bare Nat is excluded (no LLVM scalar arm)
        | IRTIdxTagged (inner, _) -> go inner
        | IRTUnit -> true
        | IRTTuple ts -> ts |> List.forall go
        | _ -> false
    go t

let holdsNoStorage (t: IRType) : bool =
    let rec go (t: IRType) =
        match stripUnits t with
        | IRTScalar _ | IRTIdxTagged _ | IRTUnit | IRTNat _ -> true
        | IRTTuple ts -> ts |> List.forall go
        | _ -> false
    go t

let internal isDeferredForm (e: IRExpr) : bool =
    match e with
    | IRApplyCombinator _ | IRComposeApply _ | IRFusion _ | IRParallel _
    | IRComposeObj _ | IRComposeMeth _ | IRBind _ | IRFunctorMap _ | IRZip _
    | IRMethodFor _ | IRObjectFor _
    // wrappers codegen keeps deferred too (CodeGenBinding's deferred
    // registration; EmitLlvm's virtual choice / guard producers): their
    // operands run where the wrapper is read
    | IRChoice _ | IRFallback _ | IRGuard _ | IRSequence _ -> true
    // an object_for application is a VIRTUAL array in the LLVM lane
    // (objectForApply): a call-shaped head is not proof the value was stored
    | IRApp (IRObjectFor _, _, _) -> true
    | _ -> false

/// The deferred computation a read of the OUTER let `id` runs (none when the
/// let holds a stored value): followed through let aliases, tuple projections,
/// array views and every deferred wrapper until a materialization boundary
/// (`|> compute`, a stored value). Shared by the write analysis and the
/// retention analysis (gap G221).
let private outerDeferredValue (facts: WriteFacts) (id: IRId) : IRExpr option =
    let rec chase (seen: Set<IRId>) (x: IRExpr) =
        match x with
        | IRVar (j, _) when not (seen.Contains j) ->
            (match facts.OuterLet j with
             | Some v -> chase (seen.Add j) v
             | None -> None)
        // a projection of a deferred pack runs the pack
        | IRTupleProj (b, _, _) -> chase seen b
        // an array VIEW (partial index `m(i)`, slice,
        // shift, reverse, curry) of a deferred value is
        // virtual too: reading it runs the producer
        // (EmitLlvm rowView keeps AVirt / AVirtFlat).
        // An explicit `|> compute` stays the boundary.
        | IRIndex (b, _, _) | IRSlice (b, _, _, _) | IRShift (b, _, _, _)
        | IRReverse (b, _) | IRCurry (b, _, _) when not (try holdsNoStorage (typeOf x) with _ -> true) ->
            chase seen b
        | IRTuple es ->
            // every component is chased by the SAME
            // rules (names, projections, views,
            // deferred forms); a stored component
            // (evaluated at its let) drops out
            let live = es |> List.choose (chase seen)
            if live.IsEmpty then None else Some (IRTuple live)
        | v when isDeferredForm v -> Some v
        | _ -> None
    facts.OuterLet id |> Option.bind (chase (Set.singleton id))

/// THE WRITE ANALYSIS (gaps G41 / G43; exprMayWriteWith / callableMayWriteWith): does evaluating `root` write storage
/// that another evaluation can observe? Shared by codegen's reorder licence
/// (CodeGenExprSupport.callableMayWrite / exprMayWrite: a writing kernel or
/// map body is never reordered) and Lowering's join-leg refusal. One policy:
///   * STORAGE PROVENANCE, not initializer syntax: an assignment is local only
///     when its target's storage is proved fresh inside the walked body -- an
///     array literal, a materialized map, a let-rec buffer, a scalar, a
///     `let mut` deep copy, or an alias chain / view / field / tuple
///     projection of such a local. A view of a captured array (`let t = G(0)`),
///     a call's array result (`let t = get_G()`), anything not proved fresh is
///     external.
///   * ARGUMENT-AWARE calls: a callee's summary says which parameters it
///     writes; the write lands on the provenance of the caller's argument, so
///     a callee filling the caller's fresh scratch writes nothing observable.
///   * a callable named as a KERNEL (map, fold, inner reduce) writes
///     observably when it writes anything outside its own body, its
///     parameters included (a kernel's parameters are its operands' cells).
///   * FLOW-INSENSITIVE over rebinding: a local's (or parameter's) provenance
///     is the join of its initializer and EVERY value later assigned to it or
///     stored into one of its fields / elements (`t = G(0)` makes `t` a view
///     of G for every store through it), so a `let` initializer is never
///     taken as lifetime ownership.
///   * a named DEFERRED computation read here is evaluated here: followed
///     through `OuterLet`, let aliases, tuple projections and every deferred
///     wrapper (choice, fallback, guard, sequence included), until a
///     materialization boundary (`|> compute`, a stored value).
///   * an UNRESOLVED call head (a function-typed value, a curried result) or
///     an unresolved function-typed name in a KERNEL slot may do anything:
///     counted as a write (a decline declines, a refusal refuses an
///     unproved-pure evaluation).
/// Cycles: a callable met again while its own summary is computed answers
/// "writes every array parameter" (sound: its own direct writes are found on
/// the first visit); a deferred let is walked once.
let private writeAnalyzer (facts: WriteFacts) =
    let summaries = System.Collections.Generic.Dictionary<IRId, WriteProvenance>()
    let rec summaryOf (c: IRCallable) : WriteProvenance =
        match summaries.TryGetValue c.Id with
        | true, s -> s
        | _ ->
            // RECURSION: the least fixpoint, from "writes nothing" up (a may
            // analysis: a recursive call adds only what the body itself
            // does). The previous pessimistic seed -- "writes every array
            // parameter" -- polluted every self-recursive pure function that
            // forwards an array (examples/mandelbrot.bladenb's `esc_pert`),
            // and through argument-aware calls turned a captured operand into
            // a refused parallel write. Summaries computed DURING an iteration
            // may depend on this one's current value, so they are dropped and
            // recomputed on the next iteration; the last iteration's stay.
            let paramIdx =
                c.Params |> List.indexed |> List.collect (fun (i, p) -> [ (p.VarId, i) ]) |> Map.ofList
            let paramNames = c.Params |> List.indexed |> List.map (fun (i, p) -> (p.Name, i)) |> Map.ofList
            let allStorage =
                { External = false
                  Params = c.Params |> List.indexed |> List.filter (fun (_, p) -> not (holdsNoStorage p.Type)) |> List.map fst |> Set.ofList }
            // TOP: writes outside AND every storage parameter (Codex finding
            // 23: the old budget fallback kept External = false and never
            // re-ran the transfer, so a long parameter-rotating recursion
            // that reaches a capture on its last step was missed).
            let top = { allStorage with External = true }
            let rec iterate (current: WriteProvenance) (round: int) =
                summaries.[c.Id] <- current
                let before = System.Collections.Generic.HashSet<IRId>(summaries.Keys)
                let s = writeJoin current (walkBody paramIdx paramNames c.Body)
                // summaries first computed during this round read `current`;
                // drop them unless `current` is the final answer
                let dropNew () =
                    for k in List.ofSeq summaries.Keys do
                        if k <> c.Id && not (before.Contains k) then summaries.Remove k |> ignore
                if s = current then s
                elif round >= 64 then
                    // not converging within the budget: widen to TOP; the
                    // summaries built on the unfinished value are recomputed
                    dropNew ()
                    top
                else
                    dropNew ()
                    iterate s (round + 1)
            let s = iterate writeNone 0
            summaries.[c.Id] <- s
            s
    /// The writes of one body, as a provenance summary over its parameters.
    and walkBody (paramIdx: Map<IRId, int>) (paramNames: Map<string, int>) (body: IRExpr) : WriteProvenance =
        let locals = System.Collections.Generic.Dictionary<IRId, IRExpr>()
        let walkedLets = System.Collections.Generic.HashSet<IRId>()
        // THE ROOTS: the body AND the extents its types carry (a range bound
        // may hold a block with its own lets, rebindings and writes) -- every
        // scan below, the provenance collections included, covers all of them
        // (round 4 wave 6, the G186 diff reviews: a write, a rebinding `t = a`
        // and fresh scratch storage hidden in a bound)
        let roots = body :: embeddedExtentExprs body
        for r in roots do iterIRExpr (fun n -> match n with IRLet (id, v, _) -> locals.[id] <- v | _ -> ()) r
        // Every value a local or parameter can come to hold: its initializer
        // plus each later whole rebinding, and each storage-bearing value
        // stored into one of its fields / elements (a reference stored in an
        // aggregate is projected back out later).
        let rebinds = System.Collections.Generic.Dictionary<IRId, ResizeArray<IRExpr>>()
        let rec rootVar (t: IRExpr) =
            match t with
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> rootVar b
            | IRVar (id, _) -> Some id
            | _ -> None
        iterIRExpr (fun n ->
            match n with
            | IRAssign (target, v) ->
                (match rootVar target with
                 | Some id when locals.ContainsKey id || paramIdx.ContainsKey id ->
                     let whole = (match target with IRVar _ -> true | _ -> false)
                     let carries = whole || not (try holdsNoStorage (typeOf v) with _ -> false)
                     if carries then
                         (match rebinds.TryGetValue id with
                          | true, l -> l.Add v
                          | _ -> rebinds.[id] <- ResizeArray [ v ])
                 | _ -> ())
            | _ -> ()) |> fun scan -> for r in roots do scan r
        let rec prov (seen: Set<IRId>) (e: IRExpr) : WriteProvenance =
            let scalarTyped = try holdsNoStorage (typeOf e) with _ -> false
            let rebound (seen: Set<IRId>) (id: IRId) =
                match rebinds.TryGetValue id with
                | true, vs -> vs |> Seq.map (prov (seen.Add id)) |> Seq.fold writeJoin writeNone
                | _ -> writeNone
            match e with
            | IRVar (id, _) when paramIdx.ContainsKey id ->
                if seen.Contains id then writeNone
                else writeJoin { writeNone with Params = Set.singleton paramIdx.[id] } (rebound seen id)
            | IRParam (nm, _, _) when paramNames.ContainsKey nm -> { writeNone with Params = Set.singleton paramNames.[nm] }
            | IRVar (id, _) when locals.ContainsKey id ->
                if seen.Contains id then writeNone
                else
                    let init = locals.[id]
                    let fromInit =
                        match init with
                        | IRVar _ when facts.CopiedLet id -> writeNone
                        | _ -> prov (seen.Add id) init
                    writeJoin fromInit (rebound seen id)
            | _ when scalarTyped -> writeNone
            | IRVar _ | IRParam _ -> { writeNone with External = true }
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRSlice (b, _, _, _) | IRShift (b, _, _, _)
            | IRReverse (b, _) | IRCurry (b, _, _) -> prov seen b
            | IRTupleProj (IRTuple es, i, false) when i >= 0 && i < es.Length -> prov seen es.[i]
            | IRTupleProj (b, _, _) -> prov seen b
            | IRTuple es -> es |> List.map (prov seen) |> List.fold writeJoin writeNone
            | IRIf (_, a, b) -> writeJoin (prov seen a) (prov seen b)
            | IRMatch (_, cases) -> cases |> List.map (fun c -> prov seen c.Body) |> List.fold writeJoin writeNone
            | IRLet (_, _, b) -> prov seen b
            | _ when isFreshPoolFormWith (fun _ -> false) e -> writeNone
            | _ -> { writeNone with External = true }
        // The storage an assignment target writes: strip the element / field /
        // projection steps down to the base expression.
        let rec targetBase (t: IRExpr) =
            match t with
            | IRIndex (b, _, _) | IRFieldAccess (b, _) | IRTupleProj (b, _, _) -> targetBase b
            | other -> other
        let mutable acc = writeNone
        let add (p: WriteProvenance) = acc <- writeJoin acc p
        let rec visit (e: IRExpr) =
            if not acc.External then
                match e with
                // A print-replay row (rec-print-replay v1) writes STORAGE
                // nowhere: it prints only in the C++ lane's print-phase run,
                // after every binding, so it is no write to the bindings an
                // interval planner judges (the CSE facts keep it unmergeable).
                | IRReplayRow (_, row) -> visit row
                | IRAssign (target, value) ->
                    (match target with
                     // Rebinding a name: local when the name is the body's own
                     // (a let or a parameter), an observable write otherwise.
                     | IRVar (id, _) when locals.ContainsKey id || paramIdx.ContainsKey id -> ()
                     | IRVar _ | IRParam _ -> add { writeNone with External = true }
                     | _ -> add (prov Set.empty (targetBase target)))
                    visitChildren target
                    visit value
                | IRApp (f, args, _) ->
                    // The application of `h` to THIS call's `args`; a callback
                    // head applies EVERY candidate, effects joined.
                    let rec callAt (seenCb: Set<IRId>) (h: IRExpr) =
                        match facts.Resolve h with
                        | Some c ->
                            let s = summaryOf c
                            if s.External then add { writeNone with External = true }
                            for k in s.Params do
                                if k < args.Length then add (prov Set.empty args.[k])
                                else add { writeNone with External = true }
                        | None ->
                            match h with
                            | IRVar (cid, _) when (facts.Callbacks cid).IsSome ->
                                // a cycle among callbacks adds nothing new: the
                                // other candidates of the cycle are joined too
                                if not (seenCb.Contains cid) then
                                    for cand in (facts.Callbacks cid).Value do callAt (seenCb.Add cid) cand
                            // a combinator / loop-object head: its kernels are
                            // visited as references below
                            | IRObjectFor _ | IRMethodFor _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _ -> visit h
                            | _ -> if facts.UnknownIsWrite then add { writeNone with External = true }
                    callAt Set.empty f
                    args |> List.iter visit
                | IRVar (id, vty) ->
                    // A callable REFERENCE (a kernel slot, a callable passed
                    // on): its parameters are cells of its operands, so a
                    // parameter write is observable too. A callback joins all
                    // of its candidates. true = judged as a callable.
                    let rec kernelRef (seenCb: Set<IRId>) (h: IRExpr) : bool =
                        match facts.Resolve h with
                        | Some c ->
                            let s = summaryOf c
                            if s.External || not s.Params.IsEmpty then add { writeNone with External = true }
                            true
                        | None ->
                            match h with
                            | IRVar (cid, _) when (facts.Callbacks cid).IsSome ->
                                if not (seenCb.Contains cid) then
                                    for cand in (facts.Callbacks cid).Value do
                                        if not (kernelRef (seenCb.Add cid) cand) then
                                            if facts.UnknownIsWrite then add { writeNone with External = true }
                                true
                            | _ -> false
                    if kernelRef Set.empty e then ()
                    else
                         // an unresolved FUNCTION-typed name (a higher-order
                         // parameter or value in a kernel slot) may do anything
                         if isFunctionTyped vty then
                             if facts.UnknownIsWrite then add { writeNone with External = true }
                         elif not (locals.ContainsKey id) && not (paramIdx.ContainsKey id) && walkedLets.Add id then
                             // a named deferred computation runs where it is read
                             match outerDeferredValue facts id with
                             | Some deferred -> visit deferred
                             | None -> ()
                | IRParam (_, _, t) when isFunctionTyped t -> if facts.UnknownIsWrite then add { writeNone with External = true }
                | other -> visitChildren other
        and visitChildren (e: IRExpr) =
            let (ExprShape (children, _)) = e
            children |> List.iter visit
        // the extents the body's TYPES carry run too (`method_for(0..bump(a))`:
        // a mutating call in a range bound), and the child walk never enters
        // them -- so every summary, transitively through callees, counts their
        // writes (round 4 wave 6, the G186 diff review: a helper hiding such a
        // bound slipped past the join producer's interval audit)
        for r in roots do visit r
        acc
    (summaryOf, walkBody)

let exprMayWriteWith (facts: WriteFacts) (root: IRExpr) : bool =
    let (_, walkBody) = writeAnalyzer facts
    let s = walkBody Map.empty Map.empty root
    s.External || not s.Params.IsEmpty

/// The analysis over a CALLABLE's applications (a kernel's): a write to its
/// own parameters counts too (they are its operands' cells).
/// The summary itself: writes to storage outside the callable (`External`)
/// kept apart from writes to its own parameters (`Params`), for a judgment
/// that treats the two differently (Lowering's parallel-body refusal: a
/// `mut` parameter is the caller's to grant, a capture is a race).
let callableWriteSummaryWith (facts: WriteFacts) (c: IRCallable) : WriteProvenance =
    let (summaryOf, _) = writeAnalyzer facts
    summaryOf c

/// An ABSTRACT FUNCTION VALUE for the callback table: the callables an
/// expression may denote (`Fns`, by callable id), whether it may denote
/// ANYTHING (`Top`: an unknown function -- a value from an untracked name, a
/// function returned by a call, a parameter an unknown caller may fill), and,
/// for a tuple, the value of each component. Known targets are kept beside
/// `Top`: a call through `{launch} + Top` still feeds launch's parameters.
type private FnVal = { Fns: Set<IRId>; Top: bool; Comps: Map<int, FnVal> }
let private fvBot : FnVal = { Fns = Set.empty; Top = false; Comps = Map.empty }
let private fvTop : FnVal = { fvBot with Top = true }
let rec private fvJoin (a: FnVal) (b: FnVal) : FnVal =
    { Fns = Set.union a.Fns b.Fns
      Top = a.Top || b.Top
      Comps =
        b.Comps |> Map.fold (fun acc k v ->
            Map.add k (match Map.tryFind k acc with Some w -> fvJoin w v | None -> v) acc) a.Comps }
/// A type whose values may carry a function: a function type, or a tuple
/// with such a component.
let rec private mayHoldFunction (t: IRType) : bool =
    match stripUnits t with
    | IRTTuple ts -> ts |> List.exists mayHoldFunction
    | t -> isFunctionTyped t
/// Every leaf of a (nested) tuple value joined: a FLAT projection may read any.
let rec private fvLeaves (v: FnVal) : FnVal =
    v.Comps |> Map.fold (fun acc _ c -> fvJoin acc (fvLeaves c)) { v with Comps = Map.empty }

/// THE CALLBACK-ARGUMENT TABLE (gap G41; Codex findings 22 / 25 / 26-28),
/// shared by Lowering's parallel-body refusal and both emitters' write facts:
/// for each function-valued parameter -- and each let / module-binding alias
/// of a function value -- the CLOSED set of callables it may hold, or no entry
/// when it may hold an unknown one (the walker then counts its applications
/// as unknown, i.e. a write).
///
/// A monotone abstract interpretation solved to its LEAST FIXPOINT, so the
/// answer does not depend on declaration or discovery order (finding 28):
///   * every tracked name -- a callback parameter, a `let` (local or MODULE
///     level: binding identity is kept, finding 26) -- starts at BOTTOM
///     (undiscovered), never at unknown, and grows by joins: a `let`
///     takes its initializer's value; a parameter the value of every argument
///     any call passes it, through EVERY target the call's head may denote --
///     an indirect `h(f)` (h itself a callback, or an alias of one: aliases
///     are evaluated, not matched by name, finding 27) feeds each candidate's
///     slots, known targets kept even beside an unknown one;
///   * RETURN FLOW (gap G47): every analyzed callable has a RETURN value, the
///     value of its body, and a call evaluates to the join of the return
///     values of every target its head may denote (a parameter's value is
///     the join over all call sites, so the substitution is context-
///     insensitive and sound). A function's result therefore flows to its
///     KNOWN callers instead of escaping: `passthrough(launch)(ident)` feeds
///     ident into launch's `f`. A partial application, a call through an
///     unknown head, and a callable the table did not analyze still return
///     TOP;
///   * PARAMETERS that may hold a function -- a function type, or a TUPLE with
///     a function component (G47) -- are tracked, the tuple ones component-
///     wise, so passing a tuple holding a callable to a function that reads
///     only its scalar component is not an escape;
///   * TOP arises only from a genuine unknown: an untracked name (a binder the
///     table does not model, a builtin head), the function an unknown or
///     partial call returns, a tuple read through an untracked tuple;
///   * a function value that ESCAPES -- reaches a position the table does not
///     follow: stored in a struct, used as a kernel, handed to an unknown call
///     or to an untracked parameter -- makes every tracked parameter of every
///     callable it may denote TOP (an unknown caller may then pass it
///     anything), and escapes that callable's RETURN value too (the unknown
///     caller may call it and keep what it returns). Tuples are followed
///     component-wise (a structural projection reads its component; a flat one
///     any leaf), so a callable stored in a tuple escapes only when the tuple
///     does. An alias INITIALIZER is not an escape; the alias's USES are
///     classified.
/// The lattice is finite, so the iteration converges; should it ever exceed
/// its budget it widens to TOP -- the EMPTY table, every callback unknown
/// (the rule the write summaries follow). `resolve` is the caller's callable
/// resolver (let aliases of named callables included where it chases them).
let callbackTable (resolve: IRExpr -> IRCallable option) (funcs: IRCallable list) (bindings: (IRId * IRExpr) list) : Map<IRId, IRExpr list> =
    let callables = System.Collections.Generic.Dictionary<IRId, IRCallable>()
    for f in funcs do callables.[f.Id] <- f
    // TRACKED parameters: a function type, or a tuple with a function leaf
    // (G47: tracked component-wise; only the function-typed ones are exported)
    let isCbParam = System.Collections.Generic.HashSet<IRId>()
    for f in funcs do
        for p in f.Params do
            if (try mayHoldFunction p.Type with _ -> false) then isCbParam.Add p.VarId |> ignore
    let analyzed = System.Collections.Generic.HashSet<IRId>(funcs |> Seq.map (fun f -> f.Id))
    // ---- one scan: call sites, alias definitions, escaping expressions ----
    let calls = ResizeArray<IRExpr * IRExpr list>()
    let defs = ResizeArray<IRId * IRExpr>()
    let defined = System.Collections.Generic.HashSet<IRId>()
    let escapes = ResizeArray<IRExpr>()
    // `escape`: the value of `e` flows somewhere the table does not follow
    let rec scan (escape: bool) (e: IRExpr) =
        match e with
        | IRApp (h, args, _) ->
            calls.Add (h, args)
            if escape then escapes.Add e
            scan false h
            args |> List.iter (scan false)
        | IRLet (x, v, b) ->
            defs.Add (x, v)
            defined.Add x |> ignore
            scan false v
            scan escape b
        | IRTuple es -> es |> List.iter (scan escape)
        | IRTupleProj (t, _, _) ->
            if escape then escapes.Add e
            scan false t
        | IRIf (c, a, b) ->
            scan false c
            scan escape a
            scan escape b
        | IRSequence es ->
            // a discarded statement's value goes nowhere
            es |> List.iteri (fun i x -> scan (escape && i = es.Length - 1) x)
        | IRVar _ -> if escape then escapes.Add e
        // a REASSIGNED tracked name joins the new value (an untracked target
        // stays unknown: its value escapes through the generic arm)
        | IRAssign (IRVar (x, _), v) when defined.Contains x || isCbParam.Contains x ->
            defs.Add (x, v)
            scan false v
        | ExprShape (children, _) -> children |> List.iter (scan true)
    for (id, v) in bindings do
        defs.Add (id, v)
        defined.Add id |> ignore
        scan false v
        // the extents its types carry are code too (round 4 wave 6: call
        // sites and aliases inside a range bound feed the table, as the
        // write analysis's roots do)
        for x in embeddedExtentExprs v do scan false x
    // a function's result flows to its callers (RETURN FLOW, G47): the body's
    // value is the callable's return value, which each call evaluates to and
    // which escapes with the callable itself -- the tail is not an escape
    for f in funcs do
        scan false f.Body
        for x in embeddedExtentExprs f.Body do scan false x
    // ---- the abstract values ----
    let env = System.Collections.Generic.Dictionary<IRId, FnVal>()
    let ret = System.Collections.Generic.Dictionary<IRId, FnVal>()
    let repr = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    let single (e: IRExpr) (c: IRCallable) =
        if not (callables.ContainsKey c.Id) then callables.[c.Id] <- c
        if not (repr.ContainsKey c.Id) then repr.[c.Id] <- e
        { fvBot with Fns = Set.singleton c.Id }
    let rec eval (e: IRExpr) : FnVal =
        match e with
        | IRVar (id, _) ->
            match resolve e with
            | Some c -> single e c
            | None ->
                match env.TryGetValue id with
                | true, v -> v
                | _ ->
                    // tracked but undiscovered: bottom; untracked: unknown
                    if defined.Contains id || isCbParam.Contains id then fvBot else fvTop
        | IRLet (_, _, b) -> eval b
        | IRIf (_, a, b) -> fvJoin (eval a) (eval b)
        | IRSequence es -> (match List.tryLast es with Some l -> eval l | None -> fvBot)
        | IRTuple es -> { fvBot with Comps = es |> List.mapi (fun i x -> (i, eval x)) |> Map.ofList }
        | IRTupleProj (t, i, flat) ->
            let tv = eval t
            let sub =
                if flat then fvLeaves tv
                else match Map.tryFind i tv.Comps with Some c -> c | None -> fvBot
            if tv.Top then fvJoin sub fvTop else sub
        | IRApp (h, args, _) when (match resolve e with Some _ -> false | None -> true) ->
            // RETURN FLOW (G47): the join of every target's return value; a
            // result that cannot hold a function carries nothing
            if not (try mayHoldFunction (typeOf e) with _ -> true) then fvBot
            else
                let hv = eval h
                let fromTargets =
                    hv.Fns |> Seq.fold (fun acc cid ->
                        match callables.TryGetValue cid with
                        | true, c when analyzed.Contains cid && args.Length = c.Params.Length ->
                            fvJoin acc (match ret.TryGetValue cid with | true, r -> r | _ -> fvBot)
                        // a partial (or over-) application, or a callable whose
                        // body the table did not analyze: an unknown function
                        | _ -> fvJoin acc fvTop) fvBot
                if hv.Top then fvJoin fromTargets fvTop else fromTargets
        | _ ->
            match resolve e with
            | Some c -> single e c
            // an untracked value that may HOLD a function (a function, or a
            // tuple with a function leaf -- a call's result, a field read)
            | None -> if (try mayHoldFunction (typeOf e) with _ -> true) then fvTop else fvBot
    let mutable changed = false
    let setVar (id: IRId) (v: FnVal) =
        let old = match env.TryGetValue id with | true, o -> o | _ -> fvBot
        let nv = fvJoin old v
        if nv <> old then
            env.[id] <- nv
            changed <- true
    let setRet (id: IRId) (v: FnVal) =
        let old = match ret.TryGetValue id with | true, o -> o | _ -> fvBot
        let nv = fvJoin old v
        if nv <> old then
            ret.[id] <- nv
            changed <- true
    // An escaping callable: an unknown caller may fill its tracked parameters
    // with anything AND keep whatever it returns (G47), so its return value
    // escapes too (each callable visited once per escape: a callable may
    // return itself).
    let escapeVal (v: FnVal) =
        let seen = System.Collections.Generic.HashSet<IRId>()
        let rec go (v: FnVal) =
            for cid in v.Fns do
                if seen.Add cid then
                    match callables.TryGetValue cid with
                    | true, c ->
                        for p in c.Params do
                            if isCbParam.Contains p.VarId then setVar p.VarId fvTop
                    | _ -> ()
                    match ret.TryGetValue cid with
                    | true, r -> go r
                    | _ -> ()
            for KeyValue (_, cv) in v.Comps do go cv
        go v
    let budget = 10000
    let mutable rounds = 0
    changed <- true
    while changed && rounds < budget do
        changed <- false
        rounds <- rounds + 1
        for (x, v) in defs do setVar x (eval v)
        for f in funcs do setRet f.Id (eval f.Body)
        for (h, args) in calls do
            let hv = eval h
            for cid in hv.Fns do
                match callables.TryGetValue cid with
                | true, c ->
                    args |> List.iteri (fun i a ->
                        if i < c.Params.Length && isCbParam.Contains c.Params.[i].VarId then setVar c.Params.[i].VarId (eval a)
                        else escapeVal (eval a))
                    // a PARTIAL application: an unknown caller supplies the rest
                    for p in List.skip (min args.Length c.Params.Length) c.Params do
                        if isCbParam.Contains p.VarId then setVar p.VarId fvTop
                | _ -> ()
            // an unknown target: every function-valued argument escapes
            if hv.Top then
                for a in args do escapeVal (eval a)
        for e in escapes do escapeVal (eval e)
    if changed then
        // over budget: widen to TOP -- every callback unknown
        Map.empty
    else
        let reprOf (cid: IRId) =
            match repr.TryGetValue cid with
            | true, e -> e
            | _ -> IRVar (cid, IRTUnit)
        env
        |> Seq.choose (fun kv ->
            let v = kv.Value
            if v.Top || v.Fns.IsEmpty || not v.Comps.IsEmpty then None
            else Some (kv.Key, v.Fns |> Set.toList |> List.map reprOf))
        |> Map.ofSeq

let callableMayWriteWith (facts: WriteFacts) (c: IRCallable) : bool =
    let (summaryOf, _) = writeAnalyzer facts
    let s = summaryOf c
    s.External || not s.Params.IsEmpty

/// THE RETENTION ANALYSIS (gap G221): may evaluating a callable (or a
/// statement) leave a REFERENCE to storage it was handed -- an argument, an
/// operand cell, a capture -- in storage that outlives the evaluation? A
/// boolean per callable (any retention anywhere = it may retain every
/// argument), over the same WriteFacts as the write analysis (callees,
/// callbacks, outer deferred lets) and over the extents the body's types
/// carry. Fail closed. True when the walk meets:
///   * a WHOLE assignment `t = v` of a storage-bearing value whose target is
///     not a let of the walked body nor one of its parameters (a module mut,
///     an enclosing `let mut` a nested function captured; m1 m2 m6);
///   * ANY element / field / projection store of a storage-bearing value --
///     no destination is proved private (Codex design review d71e628b: a fresh
///     container's field or element may itself point into caller storage, and
///     the generic store emitter assigns the wrapper);
///   * a call whose head resolves to a callable that may retain, a callback
///     whose candidates may, or ANY unresolved head given a storage-bearing
///     argument (`handlers(0)(y)`);
///   * a callable REFERENCE (a kernel slot, a callable passed on) that may
///     retain, an unresolved function-typed name;
///   * a named outer deferred computation read here whose kernels may retain.
/// Recursion: the least fixpoint from "retains nothing" (summaries computed
/// during an unfinished iteration are dropped, as in the write analysis).
let private retentionAnalyzer (facts: WriteFacts) =
    let summaries = System.Collections.Generic.Dictionary<IRId, bool>()
    let carries (e: IRExpr) = not (try holdsNoStorage (typeOf e) with _ -> false)
    let rec retainsOf (c: IRCallable) : bool =
        match summaries.TryGetValue c.Id with
        | true, s -> s
        | _ ->
            let locals0 =
                (c.Params |> List.map (fun p -> p.VarId)) |> Set.ofList
            let names0 = c.Params |> List.map (fun p -> p.Name) |> Set.ofList
            let rec iterate (current: bool) (round: int) =
                summaries.[c.Id] <- current
                let before = System.Collections.Generic.HashSet<IRId>(summaries.Keys)
                let s = current || walk locals0 names0 Set.empty c.Body
                let dropNew () =
                    for k in List.ofSeq summaries.Keys do
                        if k <> c.Id && not (before.Contains k) then summaries.Remove k |> ignore
                if s = current then s
                elif round >= 64 then
                    dropNew ()
                    true
                else
                    dropNew ()
                    iterate s (round + 1)
            let s = iterate false 0
            summaries.[c.Id] <- s
            s
    /// `outer`: ids that count as LOCAL to the walk besides the lets it binds
    /// (a callable's parameters; a scope's own lets when a statement is judged).
    and walk (outer: Set<IRId>) (outerNames: Set<string>) (outerPrivate: Set<IRId>) (body: IRExpr) : bool =
        let locals = System.Collections.Generic.HashSet<IRId>(outer)
        let walkedLets = System.Collections.Generic.HashSet<IRId>()
        let roots = body :: embeddedExtentExprs body
        // PRIVATE roots: a let whose initializer is an EAGER fresh pool (a
        // materialized map, an array literal) and that is never whole-rebound
        // -- a store DIRECTLY into one of its cells (`out(i) = v`, one subscript
        // step on the name itself) lands in storage the walk owns; whether that
        // storage then escapes is the escape analysis's question, not a
        // retention of v. Any deeper path (`cells(0).slots(0) = v`) or a view
        // (`let slot = out(0)`) is not private.
        let fresh = System.Collections.Generic.HashSet<IRId>(outerPrivate)
        let rebound = System.Collections.Generic.HashSet<IRId>()
        for r in roots do
            iterIRExpr (fun n ->
                match n with
                | IRLet (id, v, _) ->
                    locals.Add id |> ignore
                    if isFreshPoolFormWith (fun _ -> false) v then fresh.Add id |> ignore
                | IRAssign (IRVar (id, _), _) -> rebound.Add id |> ignore
                | _ -> ()) r
        let privateRoot (id: IRId) = fresh.Contains id && not (rebound.Contains id)
        let mutable found = false
        let rec visit (e: IRExpr) =
            if not found then
                match e with
                | IRReplayRow (_, row) -> visit row
                | IRAssign (target, value) ->
                    (match target with
                     | IRVar (id, _) when locals.Contains id -> ()
                     | IRParam (nm, _, _) when outerNames.Contains nm -> ()
                     | IndexOfNamed (id, _, _, _) when privateRoot id -> ()
                     | _ -> if carries value then found <- true)
                    visitChildren target
                    visit value
                | IRApp (f, args, _) ->
                    let storageArg = args |> List.exists carries
                    let rec callAt (seenCb: Set<IRId>) (h: IRExpr) =
                        match facts.Resolve h with
                        | Some c -> if retainsOf c then found <- true
                        | None ->
                            match h with
                            | IRVar (cid, _) when (facts.Callbacks cid).IsSome ->
                                if not (seenCb.Contains cid) then
                                    for cand in (facts.Callbacks cid).Value do callAt (seenCb.Add cid) cand
                            | IRObjectFor _ | IRMethodFor _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _ -> visit h
                            | _ -> if storageArg then found <- true
                    callAt Set.empty f
                    // the HEAD expression is evaluated too (`handlers(keep(y))
                    // (1.0)`: a retaining call inside an indexed head; Codex
                    // diff review d71e628b round 2) -- a bare name or a
                    // combinator head was judged by callAt above
                    (match f with
                     | IRVar _ | IRParam _ | IRObjectFor _ | IRMethodFor _ | IRCompose _ | IRComposeObj _ | IRComposeMeth _ -> ()
                     | _ -> visit f)
                    args |> List.iter visit
                | IRVar (id, vty) ->
                    let rec kernelRef (seenCb: Set<IRId>) (h: IRExpr) : bool =
                        match facts.Resolve h with
                        | Some c ->
                            if retainsOf c then found <- true
                            true
                        | None ->
                            match h with
                            | IRVar (cid, _) when (facts.Callbacks cid).IsSome ->
                                if not (seenCb.Contains cid) then
                                    for cand in (facts.Callbacks cid).Value do
                                        if not (kernelRef (seenCb.Add cid) cand) then found <- true
                                true
                            | _ -> false
                    if kernelRef Set.empty e then ()
                    elif isFunctionTyped vty then found <- true
                    elif not (locals.Contains id) && walkedLets.Add id then
                        match outerDeferredValue facts id with
                        | Some deferred -> visit deferred
                        | None -> ()
                | IRParam (_, _, t) when isFunctionTyped t -> found <- true
                | other -> visitChildren other
        and visitChildren (e: IRExpr) =
            let (ExprShape (children, _)) = e
            children |> List.iter visit
        for r in roots do visit r
        found
    (retainsOf, walk)

/// May a call of `c` retain a reference to storage it was handed (G221)?
let callableMayRetainWith (facts: WriteFacts) (c: IRCallable) : bool =
    let (retainsOf, _) = retentionAnalyzer facts
    retainsOf c

/// A checker over statements, sharing one summary table: may evaluating
/// `root` leave a reference to storage it reads in storage that outlives it?
/// `locals` are the ids whose whole rebinding is local (the scope's own lets).
/// `privates` are the scope lets whose cells a direct store may fill without
/// retaining the stored value (eager fresh pools never whole-rebound).
let retentionCheckerWith (facts: WriteFacts) : Set<IRId> -> Set<IRId> -> IRExpr -> bool =
    let (_, walk) = retentionAnalyzer facts
    fun (locals: Set<IRId>) (privates: Set<IRId>) (root: IRExpr) -> walk locals Set.empty privates root

/// The operands a module SIDE TABLE executes at binding `id`'s own site: a
/// `compound(dense, mask)`'s two operands, a `sparse(values, keys)`'s values,
/// a random fill's key / parameters / weights / address (or `fill_random`'s
/// modulus). The binding's own `Value` is then a placeholder (IRUnit), so an
/// analysis that reads only `Value` audits nothing: every admission and every
/// demand census reads `bindingPayload` instead (W1-4 review findings 1-2).
let bindingSideOperands (modul: IRModule) (id: IRId) : IRExpr list =
    (match Map.tryFind id modul.CompoundInits with Some (d, m) -> [ d; m ] | None -> [])
    @ (match Map.tryFind id modul.SparseInits with Some v -> [ v ] | None -> [])
    @ (match Map.tryFind id modul.RandomInits with
       | Some (FillModulus e) -> [ e ]
       | Some (RandGen (_, key, pars, weights, address)) ->
           key :: pars
           @ (weights |> Option.map fst |> Option.toList)
           @ (address |> Option.map (fun (s, o) -> [ s; o ]) |> Option.defaultValue [])
       | None -> [])

/// A module binding's ACTUAL execution payload: its value plus the side-table
/// operands executed at its site.
let bindingPayload (modul: IRModule) (b: IRBinding) : IRExpr list =
    b.Value :: bindingSideOperands modul b.Id

/// The payload as ONE expression (the value when there is no side operand).
let bindingPayloadExpr (modul: IRModule) (b: IRBinding) : IRExpr =
    match bindingSideOperands modul b.Id with
    | [] -> b.Value
    | ops -> IRSequence (ops @ [ b.Value ])

/// THE FAIL-CLOSED DEFERRED-VALUE PREDICATE an admission that MOVES a kernel
/// passes to IRTotality (gap G98; PM35's B1 rule, now shared): the program's
/// classifier (`IRLift.deferredValuePredicate` over the module bindings, the
/// lets of every binding payload, of every callable body and of `extraRoots`
/// -- an inlined region the pass is judging), plus every STREAMED provider
/// read (fetched lazily), plus every id whose binder none of those shows: a
/// value bound nowhere here is ANOTHER module's binding (an imported callee's
/// kernel may read one), whose value cannot be inspected -- it answers
/// deferred. A read of a deferred value runs its producer (and its panic)
/// where the reading kernel runs, so a moved kernel must not read one.
let programDeferredId (modul: IRModule) (callables: IRCallable seq) (extraRoots: IRExpr seq) : IRId -> bool =
    let callables = List.ofSeq callables
    let extraRoots = List.ofSeq extraRoots
    let payloads = modul.Bindings |> List.collect (bindingPayload modul)
    let knownBinders = System.Collections.Generic.HashSet<IRId>()
    for b in modul.Bindings do knownBinders.Add b.Id |> ignore
    let noteBinders (e: IRExpr) =
        iterIRExpr (fun n ->
            match n with
            | IRLet (id, _, _) -> knownBinders.Add id |> ignore
            | IRForRange (v, _, _, _) -> knownBinders.Add v |> ignore
            | IRMatch (_, cases) -> for c in cases do for v in patternBoundIds c.Pattern do knownBinders.Add v |> ignore
            | _ -> ()) e
    for p in payloads do noteBinders p
    for r in extraRoots do noteBinders r
    for f in callables do
        knownBinders.Add f.Id |> ignore
        for p in f.Params do knownBinders.Add p.VarId |> ignore
        // NOT the captures: a capture names its binder (an outer let, a
        // parameter, a module binding -- all noted above when they are this
        // module's), and an imported binding captured unqualified must stay
        // opaque (Codex review round 2: `from Helpers import d`, then a
        // kernel reading `d(i)`).
        noteBinders f.Body
    let opaque (id: IRId) =
        not (knownBinders.Contains id)
        || (match Map.tryFind id modul.ProviderReads with Some r -> r.Streamed | None -> false)
    IRLift.deferredValuePredicate modul.Bindings
        (List.concat [ payloads; callables |> List.map (fun f -> f.Body); extraRoots ]) opaque

/// A pass's OWN deferred census widened by the program's classifier (gap G98
/// class audit): the node test most passes use (`let d = <deferred form>`)
/// misses an alias of a deferred binder, a wholly-deferred tuple, a
/// projection of one and a let chain ending in a deferred form -- deferred in
/// every lane (`IRLift.computeDeferredIds`). OR-ed, so a pass only declines
/// MORE. FAIL CLOSED like `programDeferredId`: an id bound nowhere this module
/// or its callables show -- ANOTHER module's binding, which a moved kernel may
/// read (`Helpers.d(i)`, a deferred map there) -- answers deferred (fable
/// review of G98: the open variant admitted an imported deferred map).
let widenDeferred (modul: IRModule) (callables: IRCallable seq) (own: IRId -> bool) : IRId -> bool =
    let classify = programDeferredId modul callables Seq.empty
    // a NEGATIVE id is an audit placeholder a pass minted for a certified
    // read (planSiblingMaps' halo window reads, -1000000 - k), never a
    // binding: the pass's own answer (Codex review of G98, round 1)
    fun (id: IRId) -> own id || (id >= 0 && classify id)

/// `widenDeferred` whose census also covers `extraRoots` (a region a pass
/// judges, e.g. its inlined call bodies under fresh ids: gap G219) -- an id
/// those roots bind is classified by its value; an id bound nowhere stays
/// deferred (fail closed).
let widenDeferredWith (modul: IRModule) (callables: IRCallable seq) (extraRoots: IRExpr seq) (own: IRId -> bool) : IRId -> bool =
    let classify = programDeferredId modul callables extraRoots
    fun (id: IRId) -> own id || (id >= 0 && classify id)

/// The OPEN variant (an unseen id answers false): for a pass whose kernels
/// may name ids minted after its census (the sibling-fold pass judges an
/// inlined region whose binders are fresh) and whose own whitelist refuses
/// every array read of a foreign binding first.
let widenDeferredOpen (modul: IRModule) (callables: IRCallable seq) (own: IRId -> bool) : IRId -> bool =
    let roots =
        List.concat [ modul.Bindings |> List.collect (bindingPayload modul)
                      callables |> Seq.map (fun f -> f.Body) |> List.ofSeq ]
    let classify = IRLift.deferredValuePredicate modul.Bindings roots (fun _ -> false)
    fun (id: IRId) -> own id || classify id

/// THE INTERVENING-STATEMENT PAYLOAD of a scheduling interval (hot-row, the
/// cell epilogue, compact row classes -- W1-4 review finding 7): the planners
/// walk let CHAINS of (id, name, value); a MODULE binding in such a chain is
/// resolved by id to its complete payload (its value plus its side-table
/// operands, plus the source a provider write reads), so the read,
/// repeatability and write checks see what its site actually runs. A nested
/// ordinary let (not a module binding) keeps its own value. Producer and
/// consumer RECOGNITION keeps the original values.
let intervalPayloadOf (modul: IRModule) : IRId -> IRExpr -> IRExpr =
    let byId = modul.Bindings |> List.map (fun b -> (b.Id, b)) |> Map.ofList
    fun (id: IRId) (v: IRExpr) ->
        match Map.tryFind id byId with
        | Some b ->
            let provider =
                match Map.tryFind b.Id modul.ProviderWrites with
                | Some w -> [ IRVar (w.SourceId, IRTUnit) ]
                | None -> []
            match bindingSideOperands modul b.Id @ provider with
            | [] -> v
            | ops -> IRSequence (ops @ [ v ])
        | None -> v

/// A provider effect at an intervening module binding (a store write): an
/// external effect no schedule moves work across.
let intervalHasProviderEffect (modul: IRModule) (id: IRId) : bool =
    modul.ProviderWrites.ContainsKey id
/// THE SCHEDULING-ADMISSION WRITE PREDICATE (gap G41; Codex reviews 14-19):
/// one instance of the write analysis above over a whole module -- callees
/// resolved through the module's functions AND let aliases of them, named
/// deferred values followed through every let in the module (module bindings
/// and function-body lets), `let mut` deep copies of dense arrays recognized.
/// Every pass that MOVES an evaluation relative to another (hot-row and cell
/// epilogue schedules, compact row classes, compaction fusion, row-fold
/// fusion) asks it about all five parties: the producer kernel, the consumer
/// (row / cell / walk) kernel, the OUTER fold kernel, the seed, and every
/// binding in between. A conservative `cseFactsOf` check may stand beside it
/// (it also catches display output), never instead of it: `cseFactsOf` does
/// not chase a named deferred array initializer.
let rec scheduleMayWriteIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    scheduleMayWriteWith false programFuncs modul

/// `scheduleMayWriteIn` with every CALLBACK (a function-valued parameter)
/// joined over the arguments its call sites pass (`callbackTable`, the
/// collector Lowering's parallel-body refusal uses): the predicate an
/// admission applies to a PARALLEL body (A7c's producer under an omp
/// request). An unknown callback still counts as a write.
and scheduleMayWriteWithCallbacksIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    scheduleMayWriteWith true programFuncs modul

and scheduleMayWriteWith (withCallbacks: bool) (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    let facts = scheduleFactsWith withCallbacks programFuncs modul
    fun (e: IRExpr) -> exprMayWriteWith facts e

/// The write facts of `scheduleMayWriteWith` (one module instance), shared
/// with the retention predicate (gap G221).
and scheduleFactsWith (withCallbacks: bool) (programFuncs: IRCallable list) (modul: IRModule) : WriteFacts =
    let funcs =
        (programFuncs @ modul.Functions) |> List.map (fun f -> (f.Id, f)) |> Map.ofList
    let lets = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    // the lets of carried extents too (a range bound may bind its own
    // scratch: round 4 wave 6, the write analysis's roots)
    let note (e: IRExpr) =
        for r in e :: embeddedExtentExprs e do
            iterIRExpr (fun n -> match n with IRLet (id, v, _) -> lets.[id] <- v | _ -> ()) r
    // a side-table binding's initializer is its PAYLOAD (its value is a
    // placeholder): a reference to it is charged with what its site runs
    for b in modul.Bindings do
        let payload = bindingPayloadExpr modul b
        lets.[b.Id] <- payload
        note payload
    for f in modul.Functions do note f.Body
    let rec chaseAlias (seen: Set<IRId>) (e: IRExpr) =
        match e with
        | IRVar (id, _) when not (funcs.ContainsKey id) && not (seen.Contains id) ->
            (match lets.TryGetValue id with
             | true, (IRVar _ as v) -> chaseAlias (seen.Add id) v
             | _ -> e)
        | _ -> e
    let resolve (e: IRExpr) =
        match chaseAlias Set.empty e with
        | IRVar (id, _) -> Map.tryFind id funcs
        | _ -> None
    let copied (id: IRId) =
        modul.MutableArrayLets.Contains id
        && (match lets.TryGetValue id with
            | true, IRVar (_, ty) ->
                (match stripUnits ty with
                 | ArrayElem at -> at.IndexTypes |> List.forall (fun ix -> ix.IxKind = IxKPlain && ix.Symmetry = SymNone)
                 | _ -> false)
            | _ -> false)
    let facts : WriteFacts =
        { Resolve = resolve
          OuterLet = (fun id -> match lets.TryGetValue id with | true, v -> Some v | _ -> None)
          CopiedLet = copied
          UnknownIsWrite = true
          Callbacks = (fun _ -> None) }
    if not withCallbacks then facts
    else
        let table =
            callbackTable resolve (programFuncs @ modul.Functions) (modul.Bindings |> List.map (fun b -> (b.Id, b.Value)))
        { facts with
            Callbacks = (fun id ->
                match Map.tryFind id table with
                | Some args when not args.IsEmpty -> Some args
                | _ -> None) }

let scheduleMayWrite (modul: IRModule) : IRExpr -> bool = scheduleMayWriteIn [] modul

/// THE RETENTION PREDICATE over a module (gap G221; retentionAnalyzer with
/// the scheduling write facts and the callback table): may evaluating `e`
/// leave a reference to storage it reads in storage that outlives it? Every
/// consumer of FRESHNESS that hands a pool to someone else (scratch reuse's
/// donor, ...) asks it: `locals` = the scope lets whose whole rebinding is
/// local, `privates` = those whose cells a direct store fills privately (see
/// retentionCheckerWith).
let scheduleMayRetainIn (programFuncs: IRCallable list) (modul: IRModule) : Set<IRId> -> Set<IRId> -> IRExpr -> bool =
    retentionCheckerWith (scheduleFactsWith true programFuncs modul)

/// A resource boundary reached directly or through a callable is a motion
/// barrier without being a source write. Every downstream schedule planner
/// audits this independently of scheduleMayWriteIn.
let scheduleHasLifetimeBarrierIn (programFuncs: IRCallable list) (modul: IRModule) : IRExpr -> bool =
    if not (((programFuncs @ modul.Functions) |> List.exists (fun f -> containsPoolLifetimeEvent f.Body))
            || (modul.Bindings |> List.exists (fun b -> containsPoolLifetimeEvent (bindingPayloadExpr modul b)))) then
        fun _ -> false
    else
    let funcs = (programFuncs @ modul.Functions) |> List.map (fun f -> f.Id, f) |> Map.ofList
    let values = System.Collections.Generic.Dictionary<IRId, IRExpr>()
    for b in modul.Bindings do values.[b.Id] <- bindingPayloadExpr modul b
    for e in (modul.Bindings |> List.map _.Value) @ ((programFuncs @ modul.Functions) |> List.map _.Body) do
        iterIRExpr (function IRLet (id, v, _) -> values.[id] <- v | _ -> ()) e
    let rec go seen e =
        match e with
        | IRBeginPoolScope _ | IRReleasePools _ -> true
        | IRVar (id, _) when not (Set.contains id seen) ->
            match Map.tryFind id funcs with
            | Some f -> go (Set.add id seen) f.Body
            | None ->
                match values.TryGetValue id with
                | true, v when isDeferredForm v || isFunctionTyped (typeOf v) -> go (Set.add id seen) v
                | _ -> false
        | ExprShape (children, _) -> children |> List.exists (go seen)
    go Set.empty
