/// The ORBIT-CLASS FOLD (bench/notes/microkernel-sketches-review.md M4; plan
/// docs/plans/plan-compact-sym-folds.md section 4.3-4.4): the logical sum over
/// the cells of a compact (symmetric / antisymmetric) array, regrouped by
/// orbit class and computed in ONE pass over the packed pool.
///
/// The shape it answers is the logical traversal Blade programs already
/// write -- `r` nested licensed builtin `(+)` folds over `range<I>`, the
/// innermost body a pure scalar function `f` of ONE read `S(v_1, .., v_r)` of
/// a compact array at a permutation of the level variables:
///
///     reduce(method_for(range<I>) <@> lambda(i) ->
///         reduce(method_for(range<I>) <@> lambda(j) -> f(S(i, j)), (+)), (+))
///
/// (and `reduce(S, (+), axes = r)`, which the checker desugars into exactly
/// that nest). Today that walk reads p^r logical cells through the
/// canonicalizing accessor; the pool holds C(p + r - 1, r) of them (strict:
/// C(p, r)).
///
/// WHY IT IS A REGROUPING, NOT A NEW ROUNDING. The logical sum is a fold of a
/// MULTISET of terms: one `f(S(v))` per logical cell `v`. Every logical cell
/// is an orbit member `tau(c)` of exactly one canonical cell `c`, and for a
/// symmetric array every member reads the same stored value, so the multiset
/// is `{ f(x_c) repeated w(c) times : c canonical }` with
/// `w(c) = r! / prod(m_k!)` (the orbit size; `m_k` the multiplicities of the
/// equal coordinates of `c`). The emitted fold:
///   * sums each weight class's canonical terms into its own accumulator
///     (`S_w`, one reduction subtree over that class's terms), then
///   * combines `S_w` repeated `w` times by an ADDITION-ONLY tree
///     (`rep(S, 3) = (S + S) + S`, `rep(S, 6) = rep(S, 3) + rep(S, 3)`).
/// Each node of that tree adds two subtrees whose leaves are disjoint
/// sub-multisets, so the whole is ONE binary tree over the logical multiset:
/// an association of the r licensed `+` folds (associativity + commutativity
/// of one `+` admit any tree over a multiset). Hence it is exactly the
/// logical sum, regrouped -- and it is emitted only where the fold licenses
/// that (every level's kernel a builtin `+` with `foldReorderLicensed`, and
/// not under BLADE_FP_REPRO=1).
///
/// WHY ADDITIONS, NOT `3.0 * S`. `round(3S) = round((S + S) + S)` (doubling
/// is exact), so without contraction the multiply form is the same number.
/// But the user default is `-ffp-contract=fast` (and LLVM `contract`): a
/// product the COMPILER introduced, `S1 + 3.0 * S3`, may be fused into
/// `fma(3.0, S3, S1)` -- one rounding of a value that is no association of
/// the multiset. An addition tree offers no product to contract: g++ may
/// rewrite `S + S` to `S * 2.0` (exact), and contract `(S * 2.0) + S` into
/// `fma(S, 2.0, S)` = round(3S), the same value; its result feeds an ADD, not
/// a multiply, so nothing further fuses. The combine is also emitted without
/// `reassoc` in the LLVM lane, so no rewrite to `S * 3.0` is licensed there.
///
/// ANTISYMMETRIC. A strict canonical cell `c` (all coordinates distinct) has
/// r! orbit members: r!/2 even permutations read `+x`, r!/2 odd ones read
/// `-x` (the accessor negates exactly), so the class contributes `f(x)` and
/// `f(-x)` r!/2 times each; every logical cell with a REPEATED coordinate is
/// not stored and reads `+0`, contributing `f(+0)` -- `Z = p^r - r! C(p, r)`
/// times. The general kernel is handled exactly that way: accumulate `f(x)`
/// and `f(-x)` separately (`S_+`, `S_-`), combine
/// `rep(f(0), Z) + (rep(S_+, r!/2) + rep(S_-, r!/2))`. A kernel PROVED even
/// (`(-x)(-x) = x x`, `abs`) reads `f(-x) = f(x)` bitwise, so `S_- = S_+` and
/// one accumulator with weight r! suffices (squares: weight 2 at rank 2); a
/// kernel proved odd has `S_- = -S_+` bitwise (negation commutes with
/// round-to-nearest), so the logical sum of an odd kernel comes out
/// `S_+ + (-S_+) = 0` -- exactly, and NaN/inf still propagate.
///
/// The interpreter twin keeps computing the logical sum directly (it is the
/// specification); the corpus pins the two agree at 1e-9, and the
/// byte-identity differentials pin BLADE_FP_REPRO=1, under which this form
/// is never taken.
module Blade.OrbitFold

open Blade.IR
open Blade.IRPrint
open Blade.Types
open Blade.CodeGenState
open Blade.CodeGenExprSupport

/// The sentinel variable id the plan's kernel body reads the cell value
/// through. Negative (ids minted by an IRBuilder are >= 0, the synthetic slot
/// ids are -1..-3) and private to one emitted fold: each lane binds it around
/// its own rendering of the body and unbinds it after.
let readVarId : IRId = -770001

/// BLADE_ORBIT_FOLD=0|off keeps the logical walk (the A/B escape hatch; read
/// per call, default on). A walk it turns away records the decline in
/// `blade plan`.
let orbitFoldEnabled () : bool = Blade.Toolchain.envFlag "BLADE_ORBIT_FOLD" true

/// Parity of the kernel body in the read value, PROVED syntactically (bitwise
/// under round-to-nearest, with or without contraction: every rule below is
/// a sign symmetry of the IEEE operation itself).
type Parity =
    | PEven
    | POdd
    | PUnknown

type OrbitLevel =
    { VarId: IRId
      ParamName: string
      Fold: IRCallable
      Seed: IRExpr }

/// How the class accumulators combine into the logical total.
type Combine =
    /// accumulator k
    | CAcc of int
    /// `f(+0)`, the value a repeated-coordinate cell of a strict group adds
    | CZero
    | CNeg of Combine
    /// `rep(e, w)`: e added to itself w times, by an addition-only tree
    | CRep of Combine * int64
    | CAdd of Combine * Combine

type OrbitPlan =
    { /// The compact array's binding id (the reads' `IRVar`).
      SourceId: IRId
      SourceExpr: IRExpr
      SourceName: string
      Rank: int
      Extent: int64
      Strict: bool
      ElemTy: ElemType
      AccTy: ElemType
      /// The kernel body with every read of the source replaced by
      /// `IRVar (readVarId, IRTScalar ElemTy)`.
      Body: IRExpr
      Levels: OrbitLevel list
      /// The OUTERMOST level's seed (`reduce(.., (+), s)`): the fold's answer is
      /// `s + T`, T the regrouped multiset. Inner seeds are +0 (checked), and
      /// all copies of +0 collapse into the accumulators' own +0 start.
      OuterSeed: IRExpr
      Parity: Parity
      /// Canonical cells stored: C(p + r - 1, r), strict C(p, r).
      PoolCells: int64
      /// Strict groups: logical cells with a repeated coordinate (read +0).
      ZeroCells: int64
      /// Symmetric groups: the distinct orbit weights, ascending; accumulator
      /// k is the class of weight `Weights.[k]`. Strict groups: [].
      Weights: int64 list
      /// Strict groups: 1 accumulator (S_+) for a proved-even or proved-odd
      /// kernel, 2 (S_+, S_-) otherwise.
      AccCount: int
      Total: Combine
      /// Classes for `blade plan`: weight classes (symmetric), or the strict
      /// orbit class plus the repeated-coordinate zero class (strict).
      ClassCount: int }

type Recognition =
    /// Not a logical walk over a compact array: say nothing.
    | NotOrbit
    /// A logical walk over a compact array the orbit fold declines, and why
    /// (the `blade plan` census line).
    | Declined of reason: string
    | Orbit of OrbitPlan

let private factorial (k: int) : bigint = List.fold (fun (a: bigint) (b: int) -> a * bigint b) 1I [ 1 .. k ]

let private binom (n: int64) (k: int) : bigint =
    if k < 0 || int64 k > n then 0I
    else List.fold (fun (a: bigint) (i: int) -> a * bigint (n - int64 i) / bigint (i + 1)) 1I [ 0 .. k - 1 ]

/// The orbit size of a canonical tuple whose equal-coordinate runs have
/// these multiplicities: r! / prod(m!).
let orbitWeight (mults: int list) : int64 =
    let r = List.sum mults
    int64 (factorial r / (mults |> List.fold (fun a m -> a * factorial m) 1I))

/// Every run-length composition of r (the equality pattern of a canonical
/// tuple, read left to right) with at most n runs: the leaves of the emitted
/// nest.
let rec private compositions (r: int) (maxRuns: int) : int list list =
    if r = 0 then [ [] ]
    elif maxRuns = 0 then []
    else
        [ for first in 1 .. r do
            for rest in compositions (r - first) (maxRuns - 1) do
                yield first :: rest ]

/// The addition-only tree for `w` copies of a value, as the steps an emitter
/// runs on an accumulator that starts AT the value: `true` = double
/// (`acc + acc`), `false` = add the base (`acc + base`). Most significant bit
/// first: w = 3 -> [double; add] = (S + S) + S; w = 6 -> [double; add;
/// double] = ((S + S) + S) + ((S + S) + S).
let repSteps (w: int64) : bool list =
    if w < 1L then invalidArg "w" "rep weight must be >= 1"
    let bits = [ for b in 62 .. -1 .. 0 -> (w >>> b) &&& 1L = 1L ] |> List.skipWhile not |> List.tail
    bits |> List.collect (fun bit -> if bit then [ true; false ] else [ true ])

let private irChildren (e: IRExpr) : IRExpr list * (IRExpr list -> IRExpr) =
    match e with
    | ExprShape (kids, rebuild) -> (kids, rebuild)

let private isZeroSeed (acc: ElemType) (e: IRExpr) : bool =
    match e with
    | IRLit (IRLitFloat v) -> (acc = ETFloat64 || acc = ETFloat32) && v = 0.0 && not (System.Double.IsNegative v)
    | IRLit (IRLitInt 0L) -> acc = ETInt64 || acc = ETInt32
    | _ -> false

let rec private scalarElem (t: IRType) : ElemType option =
    match t with
    | IRTScalar et -> Some et
    | IRTIdxTagged (inner, _) -> scalarElem inner
    | _ -> None

let private realNumeric (et: ElemType) =
    match et with
    | ETFloat64 | ETFloat32 | ETInt64 | ETInt32 -> true
    | _ -> false

/// The compact group of a source array's type: (rank, extent, strict, elem,
/// tag, symmetry).
let private compactGroupOf (ty: IRType) =
    match ty with
    | ArrayElem at when not at.IsVirtual ->
        (match at.IndexTypes with
         | [ ix ] when ix.Rank >= 2 && ix.Symmetry <> SymNone -> Some (ix, at.ElemType)
         | _ -> None)
    | _ -> None

/// All reads of compact arrays in `e` whose index list consists only of
/// level variables (by VarId or by the current level's parameter name).
let rec private collectReads (isLevel: IRExpr -> bool) (e: IRExpr) (acc: ResizeArray<IRId * IRType * IRExpr list>) : unit =
    match e with
    | IRIndex (IRVar (gid, gty), idxs, _) when (compactGroupOf gty).IsSome && not idxs.IsEmpty && idxs |> List.forall isLevel ->
        acc.Add((gid, gty, idxs))
    | _ ->
        let kids, _ = irChildren e
        for k in kids do collectReads isLevel k acc

/// Replace every read of `gid` AT the recognised coordinate tuple (`sameTuple`)
/// by the read variable. A read of the same array anywhere else (`g(i, 0)`)
/// stays an IRIndex, which `pureScalarBody` then refuses.
let rec private substReads (gid: IRId) (sameTuple: IRExpr list -> bool) (readTy: IRType) (e: IRExpr) : IRExpr =
    match e with
    | IRIndex (IRVar (g, _), idxs, _) when g = gid && sameTuple idxs -> IRVar (readVarId, readTy)
    | _ ->
        let kids, rebuild = irChildren e
        if kids.IsEmpty then e else rebuild (kids |> List.map (substReads gid sameTuple readTy))

/// Is the substituted body a pure scalar function of the read (and of
/// loop-invariant scalars)? Returns the reason it is not.
let rec private pureScalarBody (levelIds: Set<IRId>) (levelNames: Set<string>) (bound: Set<IRId>) (e: IRExpr) : string option =
    let go = pureScalarBody levelIds levelNames bound
    let first (xs: IRExpr list) = xs |> List.tryPick go
    match e with
    | IRLit _ -> None
    | IRVar (id, _) when id = readVarId -> None
    | IRVar (id, _) when levelIds.Contains id -> Some "the kernel uses a level coordinate beyond the read"
    | IRVar (id, _) when bound.Contains id -> None
    | IRVar (_, ty) ->
        (match scalarElem ty with
         | Some _ -> None
         | None -> Some "the kernel reads a non-scalar value besides the cell")
    | IRParam (name, _, _) when levelNames.Contains name -> Some "the kernel uses a level coordinate beyond the read"
    | IRParam _ -> Some "the kernel reads a parameter the fold cannot place"
    | IRBinOp (_, _, a, b, _) -> first [ a; b ]
    | IRUnaryOp (_, a) -> go a
    | IRIf (c, a, b) -> first [ c; a; b ]
    | IRFma (a, b, c) -> first [ a; b; c ]
    | IRLet (id, v, body) ->
        (match go v with
         | Some why -> Some why
         | None -> pureScalarBody levelIds levelNames (bound.Add id) body)
    | IRIndex _ -> Some "the kernel reads another array (or the same array at another coordinate order)"
    | _ -> Some "the kernel body is not a pure scalar function of the read"

/// Parity of `e` in the read variable (see `Parity`).
let rec private parityOf (e: IRExpr) : Parity =
    let mentions (x: IRExpr) =
        let rec m (x: IRExpr) =
            match x with
            | IRVar (id, _) when id = readVarId -> true
            | _ -> (fst (irChildren x)) |> List.exists m
        m x
    if not (mentions e) then PEven
    else
        match e with
        | IRVar (id, _) when id = readVarId -> POdd
        | IRUnaryOp (IRNeg, a) -> parityOf a
        | IRUnaryOp (IRMath "abs", a) ->
            (match parityOf a with
             | PEven | POdd -> PEven
             | PUnknown -> PUnknown)
        | IRBinOp (_, (IRMul | IRDiv), a, b, _) ->
            (match parityOf a, parityOf b with
             | PEven, PEven | POdd, POdd -> PEven
             | PEven, POdd | POdd, PEven -> POdd
             | _ -> PUnknown)
        | IRBinOp (_, (IRAdd | IRSub), a, b, _) ->
            (match parityOf a, parityOf b with
             | PEven, PEven -> PEven
             | POdd, POdd -> POdd
             | _ -> PUnknown)
        | _ ->
            // Any other operation of EVEN operands is even (same inputs, same
            // deterministic result); anything else is not proved.
            if (fst (irChildren e)) |> List.forall (fun k -> parityOf k = PEven) then PEven
            else PUnknown

/// One level of the nest: `reduce(method_for(range<I>) <@> lambda(v) -> body,
/// fold, seed)`.
let private levelOf (resolveCl: IRExpr -> IRCallable option) (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) =
    match comp, kernel, seed with
    | _, IRTuple _, _ | _, _, IRTuple _ -> None
    | IRApplyCombinator info, _, _ ->
        (match info.Arrays, resolveCl info.Kernel, resolveCl kernel with
         | [ IRRange ([ ix ], None) ], Some cl, Some fold
                when info.KernelOutputRank = 0 && not info.IsCoIteration && not info.HasReynolds
                     && cl.Params.Length = 1 && fold.Params.Length = 2 ->
            let p = cl.Params.Head
            Some ({ VarId = p.VarId; ParamName = p.Name; Fold = fold; Seed = seed }, ix, cl.Body)
         | _ -> None)
    | _ -> None

/// Recognise the logical walk. `resolve` sees through a lane's deferred
/// bindings; `resolveCl` resolves a callable reference.
let recognise (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
              (sourceName: IRId -> string) (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) : Recognition =
    let rec collect (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) acc =
        match levelOf resolveCl (resolve comp) kernel seed with
        | None -> None
        | Some (lvl, ix, body) ->
            let acc = (lvl, ix) :: acc
            match body with
            | IRReduceCompute (c2, k2, s2) ->
                (match collect c2 k2 s2 acc with
                 | Some r -> Some r
                 // the inner reduce is not a level: it is part of the body
                 | None -> Some (List.rev acc, body))
            | _ -> Some (List.rev acc, body)
    match collect comp kernel seed [] with
    | None -> NotOrbit
    | Some (levels, body) when levels.Length < 2 -> NotOrbit
    | Some (levelsIx, body) ->
    let levels = levelsIx |> List.map fst
    let levelIds = levels |> List.map (fun l -> l.VarId) |> Set.ofList
    let innerName = (List.last levels).ParamName
    let isLevel (e: IRExpr) =
        match e with
        | IRVar (id, _) -> levelIds.Contains id
        | IRParam (name, _, _) -> name = innerName
        | _ -> false
    let reads = ResizeArray()
    collectReads isLevel body reads
    if reads.Count = 0 then NotOrbit else
    let (gid, gty, idxs0) = reads.[0]
    let r = levels.Length
    let (gix, gelemTy) = (compactGroupOf gty).Value
    // Structural: a read of a compact group whose every coordinate is a level
    // variable. Below this line the program IS a logical walk over a compact
    // array, so every refusal is a census line.
    if gix.Rank <> r || idxs0.Length <> r then NotOrbit else
    let keyOf (e: IRExpr) =
        match e with
        | IRVar (id, _) -> Some (string id)
        | IRParam (name, _, _) -> Some ("p:" + name)
        | _ -> None
    let tupleKey (xs: IRExpr list) = xs |> List.map keyOf
    let levelKeys =
        levels |> List.mapi (fun i l -> if i = r - 1 then [ Some (string l.VarId); Some ("p:" + l.ParamName) ] else [ Some (string l.VarId) ])
    let coversEveryLevel =
        let ks = tupleKey idxs0
        levelKeys |> List.forall (fun alts -> ks |> List.exists (fun k -> alts |> List.contains k))
        && (ks |> List.distinct |> List.length) = r
    let decline why = Declined why
    if not coversEveryLevel then decline "the read does not use every level coordinate exactly once"
    elif reads |> Seq.exists (fun (g, _, xs) -> g <> gid || tupleKey xs <> tupleKey idxs0) then
        decline "the kernel reads the compact array at more than one coordinate order, or reads a second compact array"
    else
    match gix.Symmetry with
    | SymHermitian -> decline "Hermitian storage: a conjugate mirror is not a real orbit (no whitelisted kernel)"
    | SymWreath -> decline "wreath (OrbIdx) storage: the orbit-class fold covers SymIdx / AntisymIdx groups"
    | _ ->
    let strict = (gix.Symmetry = SymAntisymmetric)
    match scalarElem gelemTy |> Option.filter realNumeric with
    | None -> decline "the compact array's elements are not real numbers (complex / non-numeric storage has no real orbit)"
    | Some et ->
    let readTy = IRTScalar et
    let body' = substReads gid (fun xs -> tupleKey xs = tupleKey idxs0 && xs |> List.forall isLevel) readTy body
    match pureScalarBody levelIds (Set.ofList (levels |> List.map (fun l -> l.ParamName))) Set.empty body' with
    | Some why -> decline why
    | None ->
    let nOpt = tryEvalIntIR gix.Extent
    let levelExtentsOk =
        levelsIx |> List.forall (fun (_, ix) ->
            ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
            && tryEvalIntIR ix.Extent = nOpt
            && (match ix.Tag, gix.Tag with
                | Some a, Some b -> a = b
                | _ -> true))
    match nOpt with
    | None -> decline "the compact group's extent is not static"
    | Some n when not levelExtentsOk -> decline $"the level ranges do not each cover the group's {n} logical positions"
    | Some n when n < 1L -> decline "an empty group (the logical fold's emptiness rules stay with the walk)"
    | Some n when r > 6 -> decline $"rank {r} > 6 (the class nest has 2^(r-1) leaves)"
    | Some n ->
    let accTys = levels |> List.map (fun l -> scalarElem l.Fold.RetType)
    let accOpt =
        match accTys |> List.distinct with
        | [ Some a ] when realNumeric a -> Some a
        | _ -> None
    match accOpt with
    | None -> decline "the folds' accumulator is not one real numeric type"
    | Some acc ->
    let badFold =
        levels |> List.indexed |> List.tryPick (fun (i, l) ->
            if foldKernelBuiltinOp l.Fold <> Some IRAdd then
                Some $"level {i + 1} folds with a kernel that is not the builtin `+` (only a `+` fold's terms regroup)"
            elif not (foldReorderLicensed l.Fold) then
                Some $"level {i + 1}'s fold carries no reorder licence (`where repro`)"
            else None)
    match badFold with
    | Some why -> decline why
    | None ->
    let badSeed =
        levels |> List.indexed |> List.skip 1 |> List.tryPick (fun (i, l) ->
            if isZeroSeed acc l.Seed then None
            else Some $"level {i + 1}'s seed is not +0 (an inner seed repeats once per row of the logical walk)")
    match badSeed with
    | Some why -> decline why
    | None ->
    let poolCells = if strict then binom n r else binom (n + int64 r - 1L) r
    let zeroCells = if strict then (bigint n) ** r - factorial r * binom n r else 0I
    if poolCells > bigint (System.Int64.MaxValue / 4L) || zeroCells > bigint (System.Int64.MaxValue / 4L) then
        decline "the logical domain is too large to count in Int64"
    elif not (orbitFoldEnabled ()) then
        decline "disabled by BLADE_ORBIT_FOLD=0"
    elif fpReproEnabled () then
        decline "BLADE_FP_REPRO=1 keeps the logical walk's serial association"
    else
    let parity = if strict then parityOf body' else PEven
    let weights =
        if strict then []
        else compositions r r |> List.map orbitWeight |> List.distinct |> List.sort
    let half = int64 (factorial r / 2I)
    let accCount, strictPart =
        if not strict then (weights.Length, None)
        else
            match parity with
            | PEven -> (1, Some (CRep (CAcc 0, 2L * half)))
            | POdd -> (1, Some (CAdd (CRep (CAcc 0, half), CRep (CNeg (CAcc 0), half))))
            | PUnknown -> (2, Some (CAdd (CRep (CAcc 0, half), CRep (CAcc 1, half))))
    let total =
        match strictPart with
        | None ->
            weights |> List.mapi (fun k w -> if w = 1L then CAcc k else CRep (CAcc k, w))
            |> List.reduce (fun a b -> CAdd (a, b))
        | Some sp ->
            let z = int64 zeroCells
            if z = 0L then sp
            elif int64 poolCells = 0L then CRep (CZero, z)
            else CAdd (CRep (CZero, z), sp)
    Orbit
        { SourceId = gid
          SourceExpr = IRVar (gid, gty)
          SourceName = sourceName gid
          Rank = r
          Extent = n
          Strict = strict
          ElemTy = et
          AccTy = acc
          Body = body'
          Levels = levels
          OuterSeed = levels.Head.Seed
          Parity = parity
          PoolCells = int64 poolCells
          ZeroCells = int64 zeroCells
          Weights = weights
          AccCount = accCount
          Total = total
          ClassCount = if strict then (if zeroCells > 0I then 2 else 1) else weights.Length }

/// The body read at the MIRROR value: `f(-x)` (the strict group's odd orbit
/// members), as an expression over the same read variable.
let negatedBody (p: OrbitPlan) : IRExpr =
    let readTy = IRTScalar p.ElemTy
    let rec go (e: IRExpr) =
        match e with
        | IRVar (id, _) when id = readVarId -> IRUnaryOp (IRNeg, IRVar (readVarId, readTy))
        | _ ->
            let kids, rebuild = irChildren e
            if kids.IsEmpty then e else rebuild (kids |> List.map go)
    go p.Body

/// Did any level's fold ask for `omp`?
let ompRequested (p: OrbitPlan) : bool = p.Levels |> List.exists (fun l -> l.Fold.IsOmpParallel)

/// THE THREADED FORM (bench G32), the C++ lane's: an `omp` request on any
/// level's fold is spent as a thread team over the OUTERMOST class level --
/// the rows of the first coordinate, each an independent run of the pool
/// with its own equality patterns, summed into per-thread class
/// accumulators that join the class totals in thread order. Still
/// additions only and still one association of the licensed `+` folds
/// (every class total is a sum of disjoint sub-multisets); deterministic for
/// a fixed team size. Not under BLADE_OMP_THREADS (no thread construct is
/// emitted), nor for a pool with fewer than two rows. The LLVM lane has no
/// thread runtime and keeps the serial pass.
let threadedForm (p: OrbitPlan) : bool =
    ompRequested p && ompThreadEmissionEnabled () && p.PoolCells > 0L && p.Extent >= 2L

/// `blade plan`'s form and evidence for a taken orbit fold.
let planForm (p: OrbitPlan) : string = $"orbit-class fold (rank {p.Rank}, {p.ClassCount} classes)"

/// The suffix the C++ lane's form carries when it threads (`threadedForm`);
/// CliCommands.llvmLaneDecision strips it for the serial LLVM lane.
let threadedSuffix = "; thread team over the first-coordinate rows, per-thread class sums combined in thread order"

/// The C++ lane's form line: `planForm`, plus `threadedSuffix` when threaded.
let planFormCpp (p: OrbitPlan) : string =
    if threadedForm p then planForm p + threadedSuffix else planForm p

let planEvidence (p: OrbitPlan) : string =
    let group = if p.Strict then $"AntisymIdx<{p.Rank}, {p.Extent}>" else $"SymIdx<{p.Rank}, {p.Extent}>"
    let classes =
        if p.Strict then
            let k =
                match p.Parity with
                | PEven -> "even kernel: f(-x) = f(x), one accumulator"
                | POdd -> "odd kernel: f(-x) = -f(x), the mirrors cancel"
                | PUnknown -> "general kernel: f(x) and f(-x) accumulated apart"
            $"strict orbits weight {orbitWeight (List.replicate p.Rank 1)} ({k}); {p.ZeroCells} repeated-coordinate cell(s) add f(+0)"
        else
            let ws = p.Weights |> List.map string |> String.concat "/"
            $"weights {ws}"
    $"one pass over the {p.PoolCells}-cell {group} pool of `{p.SourceName}` instead of {p.Extent}^{p.Rank} logical reads; {classes}; addition-only combine (licensed regrouping of the logical sum)"

/// Record the decision for `blade plan` (rule `fold-form`; the C++ lane's
/// form, threaded or not -- and, when threaded, the `omp` construct).
let recordTaken (subject: string) (p: OrbitPlan) : unit =
    recordFoldForm subject (planFormCpp p) (planEvidence p)
    if threadedForm p then
        recordOmpConstruct subject "omp parallel num_threads(T) + fixed-order combine"
            "orbit-class fold: first-coordinate rows dealt boustrophedon to the team, per-thread class sums combined in thread order"

let recordDeclined (subject: string) (why: string) : unit =
    recordCodegenDecision "fold-form" subject (Blade.Effects.Declined $"orbit-class fold not taken: {why}")
        [ "fold form: orbit-class fold not taken -- the logical walk is kept"; why ]
