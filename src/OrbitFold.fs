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
      Seed: IRExpr
      /// The level's MAP kernel asked for threads (`where omp(...)`).
      MapOmp: bool }

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
            Some ({ VarId = p.VarId; ParamName = p.Name; Fold = fold; Seed = seed; MapOmp = cl.IsOmpParallel }, ix, cl.Body)
         | _ -> None)
    | _ -> None

/// The logical walk's SHAPE: everything the licensed plan (the orbit-class
/// fold) and the strict plan (the ordered orbit scatter) share -- the levels,
/// the ONE compact read at a permutation of the level coordinates, the group,
/// the cell function `f` (Body, the read substituted by `readVarId`), static
/// extent and accumulator type. Below `recogniseWalk`'s structural checks the
/// program IS a logical walk over a compact array.
type WalkShape =
    { SourceId: IRId
      SourceType: IRType
      SourceName: string
      Rank: int
      Extent: int64
      Strict: bool
      ElemTy: ElemType
      AccTy: ElemType
      Body: IRExpr
      /// Outermost first.
      Levels: OrbitLevel list
      LevelIds: Set<IRId>
      LevelNames: Set<string> }

type WalkRecognition =
    | WalkNotOrbit
    | WalkDeclined of reason: string
    | Walk of WalkShape

/// Recognise the logical walk's shape. `resolve` sees through a lane's
/// deferred bindings; `resolveCl` resolves a callable reference. Every
/// refusal here is shared by both plans (and keeps today's order and text).
let recogniseWalk (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
                  (sourceName: IRId -> string) (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) : WalkRecognition =
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
    | None -> WalkNotOrbit
    | Some (levels, body) when levels.Length < 2 -> WalkNotOrbit
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
    if reads.Count = 0 then WalkNotOrbit else
    let (gid, gty, idxs0) = reads.[0]
    let r = levels.Length
    let (gix, gelemTy) = (compactGroupOf gty).Value
    // Structural: a read of a compact group whose every coordinate is a level
    // variable. Below this line the program IS a logical walk over a compact
    // array, so every refusal is a census line.
    if gix.Rank <> r || idxs0.Length <> r then WalkNotOrbit else
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
    let decline why = WalkDeclined why
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
    Walk
        { SourceId = gid
          SourceType = gty
          SourceName = sourceName gid
          Rank = r
          Extent = n
          Strict = strict
          ElemTy = et
          AccTy = acc
          Body = body'
          Levels = levels
          LevelIds = levelIds
          LevelNames = Set.ofList (levels |> List.map (fun l -> l.ParamName)) }

/// THE LICENSED PLAN (the orbit-class fold): today's vetoes, in today's order
/// and words -- a builtin `+` at every level with its reorder licence, +0
/// inner seeds, the domain countable, the knob, and not the repro build.
let planLicensed (shape: WalkShape) : Recognition =
    let decline why = Declined why
    let levels = shape.Levels
    let acc = shape.AccTy
    let strict = shape.Strict
    let n = shape.Extent
    let r = shape.Rank
    let body' = shape.Body
    let et = shape.ElemTy
    let gid = shape.SourceId
    let gty = shape.SourceType
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
          SourceName = shape.SourceName
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

/// Recognise the logical walk and plan the orbit-class fold (the licensed
/// plan): the orbit callers' entry, result unchanged by the split.
let recognise (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
              (sourceName: IRId -> string) (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) : Recognition =
    match recogniseWalk resolve resolveCl sourceName comp kernel seed with
    | WalkNotOrbit -> NotOrbit
    | WalkDeclined why -> Declined why
    | Walk shape -> planLicensed shape

// ===========================================================================
// THE ORDERED ORBIT SCATTER (round-2 item A2; strict, no licence consumed)
// ===========================================================================
//
// The same logical walk, in a build where every level's fold is STRICT (the
// repro build, or no level carries a reorder licence): the source is
//
//     fold_out(s_o, [ fold_in(s_i, [ f(S(i, j)) for j ]) for i ])      (rank 2)
//
// and the emitted form keeps EVERY chain the source has, each fed exactly its
// source operand sequence in its source order from its own seed, with the
// kernels applied accumulator-first as the walk applies them -- it only
// changes WHERE each term comes from. A symmetric read has f(S(i, j)) =
// f(S(j, i)) bitwise (same stored cell, f pure), so canonical cell (i <= j)
// is read ONCE and its term appended to row chain R[i] and, when i <> j, to
// R[j]. Visiting canonical rows in ascending order, row chain R[j] receives
// the terms of column j from rows 0..j-1 (ascending) before its own row's
// terms (ascending): precisely logical row j in column order. Rows complete
// in ascending order, so the outer chain runs unchanged: out = k_o(out, R[i]).
//
// THE G-ROW-GROUP SCHEDULE (what makes it vectorize without touching any
// chain): rows i0..i0+G-1 together -- their G x G diagonal block in
// block-lex order, then for each column j >= i0 + G ascending: the G row
// chains each take their own z(i0+m, j), and column chain R[j] takes
// z(i0, j) .. z(i0+G-1, j) in row order. R[i0+m] then has rows < i0 (earlier
// groups), rows i0..i0+m-1 (the block), then its own row: its source
// sequence, for ANY group sizes (a tail group of one row is the G = 1 form).
// Lanes are DISTINCT chains (row chains across m, column chains across j),
// never one chain split, so no reorder licence is consumed and no
// `omp simd` / reduction pragma may sit on a chain-carrying loop.
//
// RANK 3 (slices + retained H): the leaf chain H[a, b] = fold_leaf(s_l,
// [f(S(a, b, c)) for c]) depends only on the unordered pair (symmetric read,
// coordinate-free f, invariant seed), so ONE state per pair (packed SymIdx<2,
// n>) is CSE, not regrouping. Per first coordinate i, the slice {(j, k) : i <=
// j <= k} of the pool is a packed triangle: the rank-2 group scatter over it
// with chains R[r] = H[i, i + r] serves the events H[i, j] <- k and H[i, k]
// <- j, and the THIRD event H[j, k] <- i (j > i) is served from the SAME term
// (no second pool pass). Slices run in ascending i, so each H[j, k] receives
// c < j from earlier slices' third events (ascending), then c >= j from slice
// j's own chains: its source sequence. Then the retained H is a packed
// SymIdx<2, n> pool and the middle / outer folds are the rank-2 scatter over it
// with f = identity.
//
// ADMISSION (planStrict; every refusal names the first failed condition):
// symmetric real pools (AntisymIdx's mirror -x cells and +0 repeated cells
// are v2), rank 2 or 3, every level strict in THIS build (a mixed nest keeps
// the walk in v1: making its licensed level strict would change the default
// build's bits), f and every level's fold kernel and seed TOTAL and pure under
// the closed whitelist (Blade.IRTotality, shared with G35 / G7): the scatter
// interleaves kernel applications across chains and evaluates f once per
// canonical cell instead of once per logical cell, so an aborting or effectful
// operation could otherwise change what is observed (with a total f the walk's
// first-failure cell question does not arise; at G = 1 it would be the same
// cell anyway: a sorted tuple is its lex-least permutation). Seeds are
// coordinate-free (hoisted once); state <= 2^20 accumulators.

/// BLADE_ORBIT_SCATTER=0|off keeps the logical walk (A/B escape hatch; read
/// per call, default on). Emission-only: both lanes, no optimizer pass.
let orbitScatterEnabled () : bool = Blade.Toolchain.envFlag "BLADE_ORBIT_SCATTER" true

/// The scatter's state cap (accumulators): rank 2 holds n row chains, rank 3
/// C(n + 1, 2) pair chains.
let scatterStateMax = 1L <<< 20

type StrictPlan =
    { Shape: WalkShape
      /// Per level, outermost first: is the fold kernel the builtin `+` with
      /// no `where repro` demand (rendered inline, and the row chains' vector
      /// add)? A repro-vetoed kernel is always called through its callable.
      LevelAdd: bool list
      /// Canonical cells of the pool: C(n + r - 1, r).
      PoolCells: int64
      /// Chains held: n (rank 2) or C(n + 1, 2) (rank 3).
      StateCells: int64
      RowGroup: int
      ColBlock: int
      /// Free scalar variables read by f, a fold kernel (or a callee) or a
      /// seed: each lane checks they are bound at its own emission site.
      FreeIds: Set<IRId> }

type StrictRecognition =
    | StrictNotWalk
    | StrictDeclined of reason: string
    | StrictScatter of StrictPlan

/// The STRUCTURAL scan over f, a kernel body or a seed: None = only scalar
/// reads, operators, `if` / `let` / fma and resolved non-recursive calls, no
/// level coordinate (free scalar reads collected into `free`); Some why =
/// refused. TOTALITY (which operators may abort) is judged separately, by the
/// shared Blade.IRTotality whitelist (`planStrict`).
/// `levels` / `levelNames` are the walk's coordinates (a mention is refused
/// with `coordWhy`); `bound` / `boundNames` are let-bound and parameter
/// scalars; `visiting` guards recursion through calls.
let rec private strictWalk (resolveCl: IRExpr -> IRCallable option) (levels: Set<IRId>) (levelNames: Set<string>) (coordWhy: string)
                           (bound: Set<IRId>) (boundNames: Set<string>) (visiting: Set<IRId>)
                           (free: System.Collections.Generic.HashSet<IRId>) (e: IRExpr) : string option =
    let go = strictWalk resolveCl levels levelNames coordWhy bound boundNames visiting free
    let first (xs: IRExpr list) = xs |> List.tryPick go
    match e with
    | IRLit _ -> None
    | IRVar (id, _) when id = readVarId -> None
    | IRVar (id, _) when levels.Contains id -> Some coordWhy
    | IRVar (id, _) when bound.Contains id -> None
    | IRVar (id, ty) ->
        (match Blade.IRTotality.scalarElem ty with
         | Some _ -> free.Add id |> ignore; None
         | None -> Some "reads a value that is not a scalar")
    | IRParam (name, _, _) when levelNames.Contains name -> Some coordWhy
    | IRParam (name, _, _) when boundNames.Contains name -> None
    | IRParam _ -> Some "reads a parameter the scatter cannot place"
    | IRBinOp (_, _, a, b, _) -> first [ a; b ]
    | IRUnaryOp (_, a) -> go a
    | IRIf (c, a, b) -> first [ c; a; b ]
    | IRFma (a, b, c) -> first [ a; b; c ]
    | IRLet (id, v, body) ->
        (match go v with
         | Some why -> Some why
         | None -> strictWalk resolveCl levels levelNames coordWhy (bound.Add id) boundNames visiting free body)
    | IRApp (f, args, _) ->
        (match resolveCl f with
         | Some cl when visiting.Contains cl.Id -> Some "the body recurses (a recursive callee is outside the scatter whitelist)"
         | Some cl when cl.Params.Length = args.Length ->
             (match first args with
              | Some why -> Some why
              | None -> strictCallable resolveCl levels levelNames coordWhy visiting free cl)
         | _ -> Some "the body calls a function that does not resolve to a callable")
    | other -> Some $"the body contains a form outside the scatter whitelist ({other.GetType().Name})"

/// A callable's body under the whitelist, its parameters bound (by id and by
/// name) and its captures counted as free scalar reads.
and private strictCallable (resolveCl: IRExpr -> IRCallable option) (levels: Set<IRId>) (levelNames: Set<string>) (coordWhy: string)
                           (visiting: Set<IRId>) (free: System.Collections.Generic.HashSet<IRId>) (cl: IRCallable) : string option =
    if cl.IsCudaKernel || cl.IsMpiParallel || cl.IsArityPoly then Some "the kernel carries a cuda / mpi / arity strategy"
    else
    let bound = cl.Params |> List.map (fun p -> p.VarId) |> Set.ofList
    let names = cl.Params |> List.map (fun p -> p.Name) |> Set.ofList
    let notScalarParam = cl.Params |> List.exists (fun p -> (Blade.IRTotality.scalarElem p.Type).IsNone)
    if notScalarParam then Some "a kernel parameter is not a scalar"
    elif cl.Captures |> List.exists (fun c -> levels.Contains c.Id) then Some coordWhy
    elif cl.Captures |> List.exists (fun c -> (Blade.IRTotality.scalarElem c.Type).IsNone) then Some "the kernel captures a non-scalar value"
    else
    for c in cl.Captures do free.Add c.Id |> ignore
    strictWalk resolveCl levels levelNames coordWhy bound names (visiting.Add cl.Id) free cl.Body

/// Is the fold kernel exactly `p0 + p1` (accumulator first) -- the builtin
/// `+` the emitters may render inline (`acc + z`, the same IEEE add with the
/// same operand order, so even a NaN payload is the walk's)?
let isPlainAdd (cl: IRCallable) : bool =
    match cl.Params, cl.Body with
    | [ p0; p1 ], IRBinOp (_, IRAdd, l, r, _) ->
        let isP (q: IRParam) (e: IRExpr) =
            match e with
            | IRVar (id, _) -> id = q.VarId
            | IRParam (_, idx, _) -> idx = q.Index
            | _ -> false
        isP p0 l && isP p1 r && foldKernelBuiltinOp cl = Some IRAdd
    | _ -> false

/// THE STRICT PLAN (the ordered orbit scatter) for a recognised walk.
/// `deferredOf` sees through the lane's deferred computations (the totality
/// walk refuses a read of one).
let planStrict (resolveCl: IRExpr -> IRCallable option) (deferredOf: IRId -> IRExpr option) (shape: WalkShape) : StrictRecognition =
    let isDeferred (id: IRId) = (deferredOf id).IsSome
    // TOTALITY: the ONE shared whitelist (Blade.IRTotality, also the G35 row
    // slots' and the G7 joins'), no level coordinates bound (the structural
    // scan has already refused any mention of one).
    let totalExpr (e: IRExpr) =
        match Blade.IRTotality.walk resolveCl deferredOf isDeferred Map.empty Map.empty Set.empty Set.empty Set.empty e with
        | Ok _ -> None
        | Error why -> Some why
    let totalCallable (cl: IRCallable) =
        match Blade.IRTotality.auditCallable resolveCl deferredOf isDeferred cl [] with
        | Ok () -> None
        | Error why -> Some why
    let decline why = StrictDeclined why
    let r = shape.Rank
    let n = shape.Extent
    let levels = shape.Levels
    if shape.Strict then
        decline "antisymmetric storage: the ordered scatter covers SymIdx (a strict pool's mirror cells read -x and its repeated-coordinate cells read +0; v1 places no such events)"
    elif r >= 4 then
        decline $"rank {r}: v1 scatters ranks 2 and 3 (rank >= 4 needs recursive slices)"
    else
    let licensed = levels |> List.map (fun l -> foldReorderLicensed l.Fold)
    let reproBuild = fpReproEnabled ()
    let mixed =
        if reproBuild || not (List.exists id licensed) then None
        elif List.forall id licensed then
            Some "every level's fold carries a reorder licence in a non-repro build (the licensed forms own that walk)"
        else
            let li = licensed |> List.findIndex id
            let ui = licensed |> List.findIndex not
            Some $"a mixed nest: level {li + 1}'s fold carries a reorder licence and level {ui + 1}'s does not (v1 keeps the walk; making the licensed level strict would change the default build's bits)"
    match mixed with
    | Some why -> decline why
    | None ->
    let fTy = try Blade.IRTotality.scalarElem (typeOf shape.Body) with _ -> None
    if fTy <> Some shape.AccTy then
        decline "the cell function's value type is not the folds' accumulator type"
    else
    let free = System.Collections.Generic.HashSet<IRId>()
    let coord = "it uses a level coordinate"
    let fWhy =
        match strictWalk resolveCl shape.LevelIds shape.LevelNames coord Set.empty Set.empty Set.empty free shape.Body with
        | Some why -> Some why
        | None -> totalExpr shape.Body
    match fWhy with
    | Some why -> decline $"the cell function is outside the scatter's total whitelist ({why})"
    | None ->
    let kernelWhy =
        levels |> List.indexed |> List.tryPick (fun (i, l) ->
            if l.Fold.Params.Length <> 2 then Some $"level {i + 1}'s fold kernel does not take two parameters"
            else
            match (match strictCallable resolveCl shape.LevelIds shape.LevelNames coord Set.empty free l.Fold with
                   | Some why -> Some why
                   | None -> totalCallable l.Fold) with
            | Some why -> Some $"level {i + 1}'s fold kernel is outside the scatter's total whitelist ({why})"
            | None ->
                match Blade.IRTotality.scalarElem l.Fold.RetType with
                | Some t when t = shape.AccTy -> None
                | _ -> Some $"level {i + 1}'s fold kernel does not return the accumulator type")
    match kernelWhy with
    | Some why -> decline why
    | None ->
    let seedWhy =
        levels |> List.indexed |> List.tryPick (fun (i, l) ->
            let seedCoord = "it mentions a level coordinate (per-row seeds are v2)"
            match (match strictWalk resolveCl shape.LevelIds shape.LevelNames seedCoord Set.empty Set.empty Set.empty free l.Seed with
                   | Some why -> Some why
                   | None -> totalExpr l.Seed) with
            | Some why -> Some $"level {i + 1}'s seed is outside the scatter's admission ({why})"
            | None -> None)
    match seedWhy with
    | Some why -> decline why
    | None ->
    let poolCells = binom (n + int64 r - 1L) r
    let stateCells = if r = 2 then bigint n else binom (n + 1L) 2
    if poolCells > bigint (System.Int64.MaxValue / 4L) || stateCells > bigint scatterStateMax then
        decline $"the scatter's state ({stateCells} chains) exceeds its cap of {scatterStateMax} accumulators"
    elif not (orbitScatterEnabled ()) then
        decline "disabled by BLADE_ORBIT_SCATTER=0"
    else
    StrictScatter
        { Shape = shape
          // A `where repro` kernel keeps its CALLABLE BOUNDARY (BLADE_REPRO_FN:
          // contraction off, never inlined into a contracting caller), even
          // when its body is a plain `+`: inlined, `seed + x * x` could fuse.
          LevelAdd = levels |> List.map (fun l -> isPlainAdd l.Fold && not (foldKernelReproVetoed l.Fold))
          PoolCells = int64 poolCells
          StateCells = int64 stateCells
          RowGroup = scatterRowGroup ()
          ColBlock = scatterColBlock ()
          FreeIds = Set.ofSeq free }

/// Recognise the walk and plan the scatter.
let recogniseStrict (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
                    (sourceName: IRId -> string) (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) : StrictRecognition =
    match recogniseWalk resolve resolveCl sourceName comp kernel seed with
    | WalkNotOrbit -> StrictNotWalk
    | WalkDeclined why -> StrictDeclined why
    | Walk shape ->
        let deferredOf (id: IRId) =
            match resolve (IRVar (id, IRTUnit)) with
            | IRVar (id2, _) when id2 = id -> None
            | e -> Some e
        planStrict resolveCl deferredOf shape

/// Every thread request the walk's levels carry (each level's fold kernel and
/// map kernel, outermost first): the scatter spends none of them (its chains
/// thread only as a wavefront), so each must reach the census. No licence
/// moves between levels.
let scatterThreadRequests (p: StrictPlan) : string list =
    p.Shape.Levels |> List.indexed |> List.collect (fun (i, l) ->
        [ if l.MapOmp then yield $"level {i + 1}'s map"
          if l.Fold.IsOmpParallel then yield $"level {i + 1}'s fold" ])

/// `blade plan`'s form line for a taken scatter (both lanes' form).
let scatterForm (p: StrictPlan) : string =
    let s = p.Shape
    if s.Rank = 2 then
        $"ordered orbit scatter (rank 2, extent {s.Extent}, row groups of {p.RowGroup}): one pass over the packed pool, every row chain fed in its source order, the outer chain serial from its seed"
    else
        $"ordered orbit scatter (rank 3, extent {s.Extent}, row groups of {p.RowGroup}, shared pair chains {p.StateCells} of {s.Extent * s.Extent}): one pass over the packed pool, every pair chain fed in its source order, then the middle and outer chains serial from their seeds"

let scatterEvidence (p: StrictPlan) : string =
    let s = p.Shape
    let logical = pown (bigint s.Extent) s.Rank
    let why =
        if fpReproEnabled () then "every fold strict in this build (BLADE_FP_REPRO=1)"
        else "no level's fold carries a reorder licence"
    $"one pass over the {p.PoolCells}-cell SymIdx<{s.Rank}, {s.Extent}> pool of `{s.SourceName}` instead of {logical} logical reads; {why}: no licence consumed -- each chain keeps its seed, its operand order and its kernel; column block {p.ColBlock}; bitwise the walk under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off"

/// The evidence tag of a scatter decision the LLVM EMITTER recorded itself
/// (EmitLlvm.tryOrbitScatter): `blade plan` under BLADE_LLVM=1 puts those in
/// place of the C++ lane's scatter records (CliCommands.mergeLlvmScatterRecords).
let llvmScatterRecordTag = "(recorded by the LLVM lane's emitter)"

/// Is this a fold-form record ABOUT the ordered scatter (taken, yielded to
/// G35, or declined) -- the records whose LLVM-lane version is the emitter's own?
let isScatterRecord (d: Blade.Effects.Decision) : bool =
    d.Rule = "fold-form"
    && (match d.Outcome with
        | Blade.Effects.Declined why -> why.StartsWith "ordered orbit scatter not taken"
        | Blade.Effects.Applied -> d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: ordered orbit scatter (rank"))

/// The marker a G35 (threaded complete rows) fold-form record carries when the
/// scatter was admissible for the same walk: the serial LLVM lane takes it.
let scatterServesSerialMarker = "; a serial lane takes the ordered orbit scatter"

/// Record a taken scatter. `orbitWhy` is the orbit-class fold's decline for
/// the same walk: BLADE_ORBIT_FOLD=0 governs that LICENSED regrouping only,
/// so the strict scatter still applies (its own knob is BLADE_ORBIT_SCATTER),
/// and the record says so.
let recordScatterTaken (subject: string) (p: StrictPlan) (orbitWhy: string option) : unit =
    let knobNote =
        match orbitWhy with
        | Some why when why.Contains "BLADE_ORBIT_FOLD" ->
            "; BLADE_ORBIT_FOLD=0 disables the licensed orbit-class fold only (the scatter's knob is BLADE_ORBIT_SCATTER)"
        | _ -> ""
    recordFoldForm subject (scatterForm p) (scatterEvidence p + knobNote)

let recordScatterDeclined (subject: string) (why: string) : unit =
    recordCodegenDecision "fold-form" subject (Blade.Effects.Declined $"ordered orbit scatter not taken: {why}")
        [ "fold form: ordered orbit scatter not taken -- the logical walk is kept"; why ]

/// The prefix of the decline a G35-precedence record carries; the LLVM lane's
/// plan translation (CliCommands.llvmLaneDecision) turns it back into the
/// scatter it takes there.
let scatterG35Precedence = "the threaded complete-row form (G35) is taken under the omp request"

/// THE PRECEDENCE (round 3, W2-0a): when the G35 row-slot form AND the
/// ordered scatter both admit a walk (repro, threads on, an omp request on
/// the outer level, a stored SymIdx<2/3> pool, every level strict, the
/// closed total whitelist), the SCATTER is taken -- measured faster under
/// mt8 repro in the wave-1 harness A/B (bench/notes/round3/w1-ab.md, 21
/// interleaved rounds, blade-cpp: pairkernel 9.23 -> 7.76 ms, 0.841
/// [0.811, 0.872]; comoment3 4.43 -> 3.74 ms, 0.843 [0.817, 0.885]). Both
/// strict forms are bitwise the serial walk under REPRO + CONTRACT=off --
/// PINNED as raw uint64 bits (cancellation, -0.0, distinct NaN payloads) by
/// `blade test omp-reduce` scatter_precedence_raw_bits_rank{2,3}, not inferred
/// from timings. Default = the scatter where both admit;
/// `BLADE_FOLD_ROW_SLOTS=prefer` = the row slots (the round-2 order;
/// `recordScatterYieldedToRows`); `0` = row slots off. The row slots stay the
/// form of every walk the scatter does not admit.
let rowSlotsYieldedToScatter =
    "admitted, but the ordered orbit scatter admits the same walk and takes precedence (measured faster under mt8 repro, round 3: pairkernel 0.841, comoment3 0.843 of the row slots' time; BLADE_FOLD_ROW_SLOTS=prefer gives the row slots)"

/// The form line of that yielded row-slot record (`blade plan`).
let rowSlotsYieldedForm =
    "fold form: ordered orbit scatter (repro; the threaded complete-row form admitted but yielded: measured slower, round 3)"

/// The census clause of the scatter's omp note when the row slots yielded.
let rowSlotsYieldedCensus =
    "the threaded complete-row form admitted but yielded to the scatter: measured slower under mt8 repro, round 3 (BLADE_FOLD_ROW_SLOTS=prefer spends the request on the row-slot team)"

/// The prefix every row-slot record whose walk the C++ plan scatters carries
/// (declined, or admitted and yielded): the LLVM lane restates both the same way.
let rowSlotsScatterFormPrefix = "fold form: ordered orbit scatter (repro; the threaded complete-row form "

/// The C++ lane took G35 for a walk the scatter admits (only under
/// BLADE_FOLD_ROW_SLOTS=prefer since round 3): record why the scatter was not
/// taken there, with its form for the serial lanes.
let recordScatterYieldedToRows (subject: string) (p: StrictPlan) : unit =
    recordCodegenDecision "fold-form" subject
        (Blade.Effects.Declined $"ordered orbit scatter not taken: {scatterG35Precedence} (BLADE_FOLD_ROW_SLOTS=prefer; unset, 0 or BLADE_OMP_THREADS=1 gives the scatter)")
        [ "fold form: " + scatterForm p; scatterEvidence p ]

// ===========================================================================
// THE ROW-OWNED PULL SCATTER (round 3, D-P3; C++ lane, rank 3, a thread team)
// ===========================================================================
//
// The rank-3 scatter's LEAF chains partitioned by H row over the team the
// walk's OUTER request opens (blade_orbit_scatter.hpp leaf3_pull carries the
// ownership proof): the owner of row a seeds every H[a, b] once, pulls
// T(c, a, b) for c < a in ascending c, then runs slice a's group scatter with
// no third event -- each pair chain's source sequence, on one thread. A
// mandatory team-completion barrier precedes the middle / outer tail, which is
// scatter3's own (the rank-2 scatter over the retained H, serial from its
// seeds). Same chains, same operands, same seeds, same kernels: bitwise the
// serial scatter (and the walk) under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off,
// at every team size.
//
// WHAT THE REQUEST BUYS. The outer fold's (or the outer map level's) `omp`
// request is spent as CONCURRENT EVALUATION of pure, total sub-computations
// of its operands -- the leaf chains -- exactly the half of the licence the
// threaded complete-row form (G35) spends on whole rows; its reorder half
// stays unspent: no leaf, middle or outer chain is split, merged or
// reassociated, and no licence moves between levels (an inner level's own
// request still reaches the census).
//
// WHAT IS NOT KEPT: the serial form's same-term third-event reuse. f is
// evaluated once per READ -- C(p + 2, 3) slice cells + C(p + 1, 3) pulls --
// not once per pool cell (pure, deterministic, coordinate-free f: every
// evaluation is the same value). The gain over G35 is fewer and contiguous
// reads, not fewer f evaluations.
//
// ADMISSION (CodeGenBinding.orbitPullAdmission): planStrict admitted the walk
// (rank 3, symmetric, the closed total whitelist for f, every kernel and
// seed); the build emits threads; the request sits at EXACTLY the G35 request
// / depth gate (the outer fold kernel's `where omp`, or a map `omp` covering
// the outermost level, a licensed outer fold, a rectangular outer level) in a
// strict (repro) build; the five-party write audit
// (Optimize.planOrbitPull, the shared transitive analysis over the module,
// plus the callback-aware codegen analysis of the team's bodies); the work
// gate `BLADE_TILE_TUNE pull_min`; `BLADE_ORBIT_PULL`. The LLVM lane has no
// thread runtime and keeps its serial scatter.

/// BLADE_ORBIT_PULL=0|off keeps the other strict forms (the threaded
/// complete-row form or the serial scatter, by their own precedence); unset /
/// `1` / `on` = on. Emission-only (C++ lane; `suiteClearedKnobs`, not an
/// optimizer gate); read per call.
let orbitPullEnabled () : bool = Blade.Toolchain.envFlag "BLADE_ORBIT_PULL" true

/// The pull form's STATIC WORK GATE (BLADE_TILE_TUNE pull_min): pool reads
/// (slice cells + pulls) below which the team is not opened. Default 2^18 =
/// 262144 (p >= 92) is PROVISIONAL, on operation counts and the box's ~40 us
/// team entry (RESULTS 16.1), like `rowslots_min`. Speed-only.
let pullMin () : int64 = max 0L (Blade.Toolchain.tileTune "pull_min" 262144L)

/// (slice cells C(p + 2, 3), pulls C(p + 1, 3), leaf applications
/// C(p + 1, 2) * p) of a rank-3 plan.
let pullCounts (p: StrictPlan) : int64 * int64 * int64 =
    let n = p.Shape.Extent
    let c3 (m: int64) = (m * (m - 1L) * (m - 2L)) / 6L
    (c3 (n + 2L), c3 (n + 1L), (n * (n + 1L) / 2L) * n)

/// `blade plan`'s form line for a taken pull (the C++ lane's form; it starts
/// with the scatter's own phrase, so `isScatterRecord` holds for it).
let pullForm (p: StrictPlan) : string =
    let s = p.Shape
    $"ordered orbit scatter (rank 3, extent {s.Extent}, row groups of {p.RowGroup}, shared pair chains {p.StateCells} of {s.Extent * s.Extent}), row-owned pull on a thread team: each H row's pair chains on ONE thread -- seeded once, T(c, a, b) pulled for c < a ascending, then slice a's group scatter with no third event; a team-completion barrier; then the middle and outer chains serial from their seeds"

let pullEvidence (p: StrictPlan) : string =
    let s = p.Shape
    let (slice, pulls, leaf) = pullCounts p
    let logical = pown (bigint s.Extent) 3
    let why =
        if fpReproEnabled () then "every fold strict in this build (BLADE_FP_REPRO=1)"
        else "every fold strict in this build"
    $"{slice} slice cells + {pulls} pulls = {slice + pulls} contiguous reads of the {p.PoolCells}-cell SymIdx<3, {s.Extent}> pool of `{s.SourceName}` instead of {logical} logical reads; f evaluated {slice + pulls} times (the serial scatter: {slice} -- its same-term third-event reuse across owners is NOT kept: the gain is fewer, contiguous reads, not fewer f evaluations); {leaf} leaf applications, {s.Extent * s.Extent} middle and {s.Extent} outer (the serial scatter's counts); {why}: no licence consumed -- the outer request buys concurrent evaluation of the leaf chains (pure, total sub-computations of its operands), no chain split, merged or reassociated; column block {p.ColBlock}; bitwise the serial scatter and the walk under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off at every team size"

/// The ownership plan line of the record.
let pullOwnership (p: StrictPlan) : string =
    let n = p.Shape.Extent
    let rowWork (a: int64) = a * (n - a) + (n - a) * (n - a + 1L) / 2L
    let mid = n / 2L
    $"ownership plan: {n} H rows claimed one at a time in ascending order by the team (row a = a(p - a) pulls + (p - a)(p - a + 1)/2 slice cells, descending in a: row 0 = {rowWork 0L} reads, row {mid} = {rowWork mid}, row {n - 1L} = {rowWork (n - 1L)}; ~{n} / T rows per thread, e.g. {float n / 8.0:F1} at T = 8); each owner seeds, pulls and scatters its row in a private buffer and stores it back once: writes disjoint by row, no synchronization inside the team; the region's end is the team-completion barrier before the serial middle / outer tail over the retained H (scatter2); the EXECUTED form is chosen at run time on the team that does the work: a requested team of one opens no team and an ACTUAL team of one (OMP_THREAD_LIMIT=1, an inactive nested region) is detected inside the region before any pull work, and both run the serial slice form's leaf stage (`serial-leaf3`: the same bits, without the pull's extra f evaluations), an actual team of two or more the pull (`row-pull`); the form and the requested / actual team are recorded per site in the run record's `forms` census (BLADE_RUN_RECORD) -- these counts are the planner's static facts, not an observation; PRECEDENCE: the pull comes first, the walk's other strict forms (the serial scatter, the threaded complete-row form G35) by their own order only when it declines (combined with W2-0's default: pull > scatter > row slots; `BLADE_FOLD_ROW_SLOTS=prefer` does not preempt the pull -- `BLADE_ORBIT_PULL=0` does)"

/// Record a taken pull form: the fold-form line (a scatter record, so the
/// LLVM lane's plan puts its emitter's own serial-scatter record in its
/// place), the five-party matrix and the ownership plan.
let recordPullTaken (subject: string) (p: StrictPlan) (orbitWhy: string option) (matrix: string list) : unit =
    let knobNote =
        match orbitWhy with
        | Some why when why.Contains "BLADE_ORBIT_FOLD" ->
            "; BLADE_ORBIT_FOLD=0 disables the licensed orbit-class fold only (the scatter's knob is BLADE_ORBIT_SCATTER, the pull's BLADE_ORBIT_PULL)"
        | _ -> ""
    recordCodegenDecision "fold-form" subject Blade.Effects.Applied
        ([ "fold form: " + pullForm p; pullEvidence p + knobNote; pullOwnership p ] @ matrix)

/// The prefix of a pull decline's form line (CliCommands.llvmLaneDecision
/// re-states it for the LLVM lane, which has no thread runtime).
let pullDeclinedForm = "fold form: row-owned pull scatter not taken"

let recordPullDeclined (subject: string) (why: string) : unit =
    recordCodegenDecision "fold-form" subject (Blade.Effects.Declined $"row-owned pull scatter not taken: {why}")
        [ pullDeclinedForm + " -- the walk's other strict forms decide (the threaded complete-row form, the serial scatter)"; why ]

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

// ===========================================================================
// THE ROW ORBIT ACTION (round 3, D-P2; gap G73): rank 2, ONE channel, a
// STORED SymIdx<2, n> pool
// ===========================================================================
//
// THE SHAPE (v1). A row action is a map binding
//
//     u = method_for(range<I>) <@> lambda(i) ->
//             reduce(method_for(range<I>) <@> lambda(j) -> g(S(i, j), V1(i), V1(j), ..), k, s) |> compute
//
// -- an IRApplyCombinator over `range<I>` whose kernel body is ONE
// IRReduceCompute level over `range<I>` (S's own tag) -- with g a pure, total
// scalar function of x = S(i, j) (or S(j, i): the same stored cell), of
// rank-1 dense arrays read at the chain's OWN coordinate i or at the PARTNER
// j, and of invariant scalars; no other use of i, j. Row chain i of the walk
// is fold_k(s, [ g(S(i, j), V(i), V(j)) for j = 0 .. n-1 ]), stored as u(i).
// (The ordered scatter's scalar walk is the special case "outer level a fold,
// g coordinate-free".)
//
// THE ROLE-TERM RULE (Codex review D, finding 5). One pass over the canonical
// cells: cell (a, b), a < b, is read ONCE and gives row chain a the term
// g(x, own = V(a), partner = V(b)) and row chain b the term g(x, own = V(b),
// partner = V(a)) -- TWO evaluations with the own / partner weights swapped,
// never one weighted term at both endpoints (S01 = 2, V = [3, 5]:
// S(i, j) * V(i) is 6 at (0, 1) and 10 at (1, 0); the packed READ is
// invariant, the cell TERM is not); a = b gives one term g(x, V(a), V(a)).
// Each term is bitwise the source's term at that position: symmetric storage
// answers (a, b) and (b, a) from one cell, the weights are read from the same
// stored cells, g is pure and its expression tree is the source's. The ORDER
// is the rank-2 scatter's (blade_orbit_scatter.hpp tri, unchanged proof):
// row chain r receives rows 0 .. r-1's column-r terms ascending, then its own
// row's terms ascending -- j = 0 .. n-1, the walk's order -- from its own
// seed, through its own kernel applied accumulator-first. Hence the walk's
// value: tested as raw bits for Float64 under BLADE_FP_REPRO=1
// BLADE_FP_CONTRACT=off in the C++ and the LLVM lane (`blade test
// orbit-scatter`'s action leg). The chains start from the fold's SEED (an
// explicit -0.0 seed is preserved); a seedless deferred `(+)` carries the
// checker's +0.0 seed in every lane, the walk and the interpreter alike (the
// pre-existing G74 family; this item neither changes nor closes it). Under `-ffp-contract=fast` a term
// stored to a Z block may contract differently from the walk's own context
// (the scatter's status; contraction invariance is not claimed).
//
// LICENSED BUILDS: v1 takes the strict form for row actions too -- the
// serial chain is an admissible association of a licensed fold, and its only
// licensed competitor is the per-row K-lane walk over canonicalizing reads;
// the licence is not consumed and the record says so. `BLADE_ORBIT_ACTIONS=
// strict` restricts the form to strict builds (BLADE_FP_REPRO=1, or a fold
// without a reorder licence) for the A/B. A walk that FOLDS the weighted rows
// (an outer fold over the action) is recognised and declined in v1 -- a mixed
// licence nest first, by planStrict's own rule (D/5's [1e16, -1e16, 1] nest:
// the ascending chain gives 1, a regrouped one 0).
//
// REFUSALS (fail closed; each names the first failed condition): two
// contracted axes in one stage; AntisymIdx (signed events, v2); Hermitian;
// wreath; a compact array that is not rank 2; the pool read at both
// coordinate orders, or a second compact array; a read of any other array, or
// of a weight at a subscript other than i / j; a weight that is not a rank-1
// dense array over S's index; a per-row seed; f, the kernel or the seed
// outside the closed total whitelist (IRTotality: no panic can arrive in a
// different order); state > 2^20 chains; a DEFERRED pool or weight (P1-style
// fusion is v2); a block-local or function-body action (v1's storage proof is
// storedArrayProof over top-level bindings); in the C++ lane only, a thread
// request on the map, the inner map or the fold in a threaded build (the old
// emitter keeps the row walk; the LLVM lane has no thread runtime and takes
// the serial action when its own admission succeeds). Types: the C++ lane
// admits Float64 / Float32 / Int64 / Int32, the LLVM lane Float64 / Int64.
// Multi-channel is NOT RECOGNIZED: a tuple kernel or seed returns
// ActionNotAction (recogniseAction), with no refusal record (v1.1). A finite
// set of tested refusals, not an exhaustive list. ADMISSION: `orbitActionAdmissible` (the planner's
// five-party audit, Optimize.planOrbitActions, plus the callback-aware codegen
// write analysis of the term, kernel and seed). The interpreter is unchanged:
// it is the oracle, the walk.

/// BLADE_ORBIT_ACTIONS: unset / `1` / `on` = the row action wherever it is
/// admitted (licensed builds too, recorded); `strict` = strict builds only
/// (BLADE_FP_REPRO=1, or a fold with no reorder licence); `0` / `off` = the
/// row walk. An optimizer gate (`Optimize.optimizerGates`: `blade test
/// opt-diff` compares off vs on, under REPRO, where the form fires). Read per
/// call; any other value reads as on.
type ActionsMode =
    | ActionsOn
    | ActionsStrict
    | ActionsOff

let orbitActionsMode () : ActionsMode =
    match Blade.Toolchain.envSwitch "BLADE_ORBIT_ACTIONS" with
    | Blade.Toolchain.SwitchOff -> ActionsOff
    | Blade.Toolchain.SwitchOther v when v.ToLowerInvariant() = "strict" -> ActionsStrict
    | _ -> ActionsOn

/// The sentinel variables the plan's body reads weight k through: its value
/// at the chain's OWN coordinate and at the PARTNER. Negative and private to
/// one emitted action (each lane binds them around its rendering of the
/// body), disjoint from `readVarId`.
let actionOwnId (k: int) : IRId = -770100 - 2 * k
let actionPartnerId (k: int) : IRId = -770101 - 2 * k

type ActionWeight =
    { ArrayId: IRId
      ArrayName: string
      ElemTy: ElemType
      /// The read's own IR type (the sentinel's).
      ReadTy: IRType
      /// Read at the own coordinate / at the partner somewhere in g.
      Own: bool
      Partner: bool
      /// Sentinels actionOwnId Index / actionPartnerId Index.
      Index: int }

type ActionShape =
    { SourceId: IRId
      SourceType: IRType
      SourceName: string
      Extent: int64
      ElemTy: ElemType
      AccTy: ElemType
      /// g with the pool read replaced by `readVarId` and each weight read by
      /// its own / partner sentinel: a scalar function of the cell value and
      /// the weights' values only.
      Body: IRExpr
      /// The row coordinate (the map's parameter) and the partner (the inner
      /// level's).
      OwnVarId: IRId
      OwnName: string
      Level: OrbitLevel
      /// The map kernel's own thread request (`where omp(...)`).
      MapOmp: bool
      Weights: ActionWeight list }

type ActionPlan =
    { Shape: ActionShape
      /// The fold kernel is exactly `p0 + p1` with no repro demand (inline,
      /// and the row chains' vector add).
      LevelAdd: bool
      PoolCells: int64
      StateCells: int64
      RowGroup: int
      ColBlock: int
      /// Free scalars of g, the kernel (or a callee) and the seed.
      FreeIds: Set<IRId>
      /// The fold carries a reorder licence in this build (the strict form
      /// is then an admissible association, recorded as such).
      Licensed: bool }

type ActionRecognition =
    | ActionNotAction
    | ActionDeclined of reason: string
    | ActionTaken of ActionPlan

/// The shared analysis of one row's inner level: `(iId, iName)` the row
/// coordinate, `ixO` its range's index type, `(c2, k2, s2)` the inner fold.
/// Ok shape / Error (decline reason) / None (not an action over a compact
/// pool: say nothing).
let private analyseAction (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
                          (sourceName: IRId -> string) (iId: IRId) (iName: string) (mapOmp: bool) (ixO: IRIndexType)
                          (c2: IRExpr) (k2: IRExpr) (s2: IRExpr) : Result<ActionShape, string> option =
    match levelOf resolveCl (resolve c2) k2 s2 with
    | None -> None
    | Some (lvl, ixJ, gBody) ->
    let jId = lvl.VarId
    let jName = lvl.ParamName
    let coordKey (e: IRExpr) =
        match e with
        | IRVar (id, _) when id = iId -> Some "i"
        | IRVar (id, _) when id = jId -> Some "j"
        | IRParam (name, _, _) when name = jName -> Some "j"
        | _ -> None
    let isCoord (e: IRExpr) = (coordKey e).IsSome
    // TWO CONTRACTED AXES: the term is itself a fold over a third level
    // (`T(i, j, k) * v(j) * w(k)`): ordered events need per-destination
    // queues (v2). Recognised only when a compact array is read there.
    let nested =
        match gBody with
        | IRReduceCompute (c3, k3, s3) ->
            (match levelOf resolveCl (resolve c3) k3 s3 with
             | Some (l3, _, b3) ->
                 let isCoord3 (e: IRExpr) =
                     isCoord e
                     || (match e with
                         | IRVar (id, _) -> id = l3.VarId
                         | IRParam (name, _, _) -> name = l3.ParamName
                         | _ -> false)
                 let rs = ResizeArray()
                 collectReads isCoord3 b3 rs
                 Some (rs.Count > 0)
             | None -> None)
        | _ -> None
    match nested with
    | Some true ->
        Some (Error "two contracted axes in one stage (the term folds a third level): ordered events need per-destination queues (v2)")
    | _ ->
    let reads = ResizeArray()
    collectReads isCoord gBody reads
    if reads.Count = 0 then None else
    let (gid, gty, idxs0) = reads.[0]
    let (gix, gelemTy) = (compactGroupOf gty).Value
    let decline (why: string) = Some (Error why)
    let keys (xs: IRExpr list) = xs |> List.map coordKey
    let ks = keys idxs0
    if gix.Rank <> 2 || idxs0.Length <> 2 then
        decline $"the compact array `{sourceName gid}` is rank {gix.Rank}: v1's row action reads a rank-2 pool (the rank-3 one-axis stage is a separate item)"
    elif ks <> [ Some "i"; Some "j" ] && ks <> [ Some "j"; Some "i" ] then
        decline "the pool read does not use the row coordinate and the partner coordinate exactly once each"
    elif reads |> Seq.exists (fun (g, _, xs) -> g <> gid || keys xs <> ks) then
        decline "the term reads the compact array at more than one coordinate order, or reads a second compact array"
    else
    match gix.Symmetry with
    | SymHermitian -> decline "Hermitian storage: a mirror cell reads the conjugate (no real orbit action)"
    | SymAntisymmetric ->
        decline "antisymmetric storage: a mirror cell reads -x and the diagonal +0 (signed events, v2) -- the packed read is not the mirror's value"
    | SymWreath -> decline "wreath (OrbIdx) storage: v1's row action covers SymIdx<2, n>"
    | SymNone -> None
    | SymSymmetric ->
    match scalarElem gelemTy |> Option.filter realNumeric with
    | None -> decline "the compact array's elements are not real numbers"
    | Some et ->
    let nOpt = tryEvalIntIR gix.Extent
    match nOpt with
    | None -> decline "the compact group's extent is not static"
    | Some n ->
    let levelOk (ix: IRIndexType) =
        ix.Rank = 1 && ix.Symmetry = SymNone && ix.IxKind = IxKPlain
        && tryEvalIntIR ix.Extent = Some n
        && (match ix.Tag, gix.Tag with
            | Some a, Some b -> a = b
            | _ -> true)
    if not (levelOk ixO && levelOk ixJ) then
        decline $"the row and partner ranges do not each cover the pool's {n} positions"
    elif n < 1L then decline "an empty pool"
    else
    let readTy = IRTScalar et
    let body1 = substReads gid (fun xs -> keys xs = ks && xs |> List.forall isCoord) readTy gBody
    // THE WEIGHTS: every remaining read `W(c)`, c the row or the partner
    // coordinate, of a rank-1 dense array over S's index becomes a sentinel.
    let weights = System.Collections.Generic.List<ActionWeight>()
    let mutable weightWhy : string option = None
    let weightOk (wty: IRType) =
        match wty with
        | ArrayElem at when not at.IsVirtual ->
            (match at.IndexTypes, scalarElem at.ElemType |> Option.filter realNumeric with
             | [ ix ], Some wet when levelOk ix -> Some wet
             | _ -> None)
        | _ -> None
    let rec subst (e: IRExpr) : IRExpr =
        match e with
        | IRIndex (IRVar (wid, wty), [ c ], _) when isCoord c && wid <> gid ->
            (match weightOk (IR.stripUnits wty) with
             | None ->
                 if weightWhy.IsNone then
                     weightWhy <- Some $"the weight `{sourceName wid}` is not a rank-1 dense array of real numbers over the pool's index ({n} positions)"
                 e
             | Some wet ->
                 let own = coordKey c = Some "i"
                 let k =
                     match weights |> Seq.tryFindIndex (fun w -> w.ArrayId = wid) with
                     | Some k ->
                         let w = weights.[k]
                         weights.[k] <- { w with Own = w.Own || own; Partner = w.Partner || not own }
                         k
                     | None ->
                         weights.Add { ArrayId = wid; ArrayName = sourceName wid; ElemTy = wet; ReadTy = IRTScalar wet
                                       Own = own; Partner = not own; Index = weights.Count }
                         weights.Count - 1
                 IRVar ((if own then actionOwnId k else actionPartnerId k), IRTScalar wet))
        | _ ->
            let kids, rebuild = irChildren e
            if kids.IsEmpty then e else rebuild (kids |> List.map subst)
    let body2 = subst body1
    let rec hasIndex (e: IRExpr) =
        match e with
        | IRIndex _ -> true
        | _ -> (fst (irChildren e)) |> List.exists hasIndex
    match weightWhy with
    | Some why -> decline why
    | None ->
    if hasIndex body2 then
        decline "the term reads another array, or a weight at a subscript other than the row / partner coordinate (a third coordinate, an index expression, a second compact array)"
    else
    let accOpt =
        match scalarElem lvl.Fold.RetType with
        | Some a when realNumeric a -> Some a
        | _ -> None
    match accOpt with
    | None -> decline "the fold's accumulator is not a real numeric type"
    | Some acc ->
    Some (Ok
        { SourceId = gid
          SourceType = gty
          SourceName = sourceName gid
          Extent = n
          ElemTy = et
          AccTy = acc
          Body = body2
          OwnVarId = iId
          OwnName = iName
          Level = lvl
          MapOmp = mapOmp
          Weights = List.ofSeq weights })

/// THE ACTION PLAN for a recognised shape: the knob, the licence policy, the
/// closed total whitelist over g, the kernel and the seed (planStrict's own
/// predicates), coordinate-free seeds, the state cap.
let planAction (resolveCl: IRExpr -> IRCallable option) (deferredOf: IRId -> IRExpr option) (shape: ActionShape) : ActionRecognition =
    let isDeferred (id: IRId) = (deferredOf id).IsSome
    let totalExpr (e: IRExpr) =
        match Blade.IRTotality.walk resolveCl deferredOf isDeferred Map.empty Map.empty Set.empty Set.empty Set.empty e with
        | Ok _ -> None
        | Error why -> Some why
    let decline why = ActionDeclined why
    let fold = shape.Level.Fold
    let licensed = foldReorderLicensed fold && not (fpReproEnabled ())
    match orbitActionsMode () with
    | ActionsOff -> decline "disabled by BLADE_ORBIT_ACTIONS=0"
    | ActionsStrict when licensed ->
        decline "BLADE_ORBIT_ACTIONS=strict: the fold carries a reorder licence in this non-repro build (the strict form is restricted to strict builds; the row walk keeps its licensed form)"
    | _ ->
    let levels = Set.ofList [ shape.OwnVarId; shape.Level.VarId ]
    let names = Set.ofList [ shape.OwnName; shape.Level.ParamName ]
    let fTy = try Blade.IRTotality.scalarElem (typeOf shape.Body) with _ -> None
    if fTy <> Some shape.AccTy then decline "the cell term's value type is not the fold's accumulator type"
    else
    let free = System.Collections.Generic.HashSet<IRId>()
    let coord = "it uses the row or partner coordinate beyond the pool and weight reads (v1: no other use of i, j)"
    // the weight sentinels are the plan's own bound scalars, not free reads
    let sentinels =
        shape.Weights |> List.collect (fun w -> [ actionOwnId w.Index; actionPartnerId w.Index ]) |> Set.ofList
    let fWhy =
        match strictWalk resolveCl levels names coord sentinels Set.empty Set.empty free shape.Body with
        | Some why -> Some why
        | None -> totalExpr shape.Body
    match fWhy with
    | Some why -> decline $"the cell term is outside the closed total whitelist ({why})"
    | None ->
    let kernelWhy =
        if fold.Params.Length <> 2 then Some "the fold kernel does not take two parameters"
        else
        match (match strictCallable resolveCl levels names coord Set.empty free fold with
               | Some why -> Some why
               | None ->
                   match Blade.IRTotality.auditCallable resolveCl deferredOf isDeferred fold [] with
                   | Ok () -> None
                   | Error why -> Some why) with
        | Some why -> Some $"the fold kernel is outside the closed total whitelist ({why})"
        | None ->
            match Blade.IRTotality.scalarElem fold.RetType with
            | Some t when t = shape.AccTy -> None
            | _ -> Some "the fold kernel does not return the accumulator type"
    match kernelWhy with
    | Some why -> decline why
    | None ->
    let seed = shape.Level.Seed
    let seedMentionsRow =
        (collectVarRefsIR seed).Contains shape.OwnVarId
        || (let rec m (e: IRExpr) =
                match e with
                | IRParam (name, _, _) -> name = shape.OwnName
                | _ -> (fst (irChildren e)) |> List.exists m
            m seed)
    if seedMentionsRow then decline "the seed mentions the row coordinate (per-row seeds are v2)"
    else
    let seedWhy =
        match strictWalk resolveCl levels names "it mentions a coordinate (per-row seeds are v2)" Set.empty Set.empty Set.empty free seed with
        | Some why -> Some why
        | None -> totalExpr seed
    match seedWhy with
    | Some why -> decline $"the seed is outside the action's admission ({why})"
    | None ->
    let n = shape.Extent
    if n > scatterStateMax then
        decline $"the action's state ({n} row chains) exceeds its cap of {scatterStateMax} accumulators"
    else
    ActionTaken
        { Shape = shape
          LevelAdd = isPlainAdd fold && not (foldKernelReproVetoed fold)
          PoolCells = n * (n + 1L) / 2L
          StateCells = n
          RowGroup = scatterRowGroup ()
          ColBlock = scatterColBlock ()
          FreeIds = Set.ofSeq free
          Licensed = licensed }

let private deferredOfResolve (resolve: IRExpr -> IRExpr) (id: IRId) : IRExpr option =
    match resolve (IRVar (id, IRTUnit)) with
    | IRVar (id2, _) when id2 = id -> None
    | e -> Some e

/// Recognise a ROW ACTION (the map form, v1) and plan it.
let recogniseAction (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
                    (sourceName: IRId -> string) (info: ApplyInfo) : ActionRecognition =
    match info.Arrays, resolveCl info.Kernel with
    | [ IRRange ([ ixO ], None) ], Some cl
            when info.KernelOutputRank = 0 && not info.IsCoIteration && not info.HasReynolds && cl.Params.Length = 1 ->
        (match cl.Body with
         | IRReduceCompute (c2, k2, s2) ->
             (match k2, s2 with
              | IRTuple _, _ | _, IRTuple _ -> ActionNotAction
              | _ ->
              let p = cl.Params.Head
              // a cuda / mpi / arity strategy on the map kernel, or on the inner
              // map's, is outside the serial action (fail closed)
              let strategyOf (k: IRCallable option) =
                  match k with
                  | Some c -> c.IsCudaKernel || c.IsMpiParallel || c.IsArityPoly
                  | None -> false
              let innerStrategy =
                  match resolve c2 with
                  | IRApplyCombinator i2 -> strategyOf (resolveCl i2.Kernel)
                  | _ -> false
              match analyseAction resolve resolveCl sourceName p.VarId p.Name cl.IsOmpParallel ixO c2 k2 s2 with
              | None -> ActionNotAction
              | Some (Error why) -> ActionDeclined why
              | Some (Ok _) when strategyOf (Some cl) || innerStrategy ->
                  ActionDeclined "the map kernel (or the inner map's) carries a cuda / mpi / arity strategy"
              | Some (Ok shape) -> planAction resolveCl (deferredOfResolve resolve) shape)
         | _ -> ActionNotAction)
    | _ -> ActionNotAction

/// A WALK that folds weighted rows -- `reduce(method_for(range<I>) <@>
/// lambda(i) -> reduce(.. g(S(i, j), V(i), V(j)) .., k1, s1), k0, s0)` with
/// at least one weight (a coordinate-free g is the ordered scatter's) -- is
/// recognised and DECLINED in v1: a mixed licence nest first (planStrict's
/// rule and words: making the licensed level strict would change the default
/// build's bits; D/5's [1e16, -1e16, 1] nest), then a fully licensed nest in
/// a non-repro build (the licensed walk owns it), then the scalar form
/// itself (v1 builds the stored vector output only). Some why = a census line;
/// None = not such a walk.
let recogniseActionWalk (resolve: IRExpr -> IRExpr) (resolveCl: IRExpr -> IRCallable option)
                        (sourceName: IRId -> string) (comp: IRExpr) (kernel: IRExpr) (seed: IRExpr) : string option =
    match levelOf resolveCl (resolve comp) kernel seed with
    | Some (outer, ixO, IRReduceCompute (c2, k2, s2)) ->
        (match k2, s2 with
         | IRTuple _, _ | _, IRTuple _ -> None
         | _ ->
         match analyseAction resolve resolveCl sourceName outer.VarId outer.ParamName outer.MapOmp ixO c2 k2 s2 with
         | Some (Ok shape) when not shape.Weights.IsEmpty ->
             let lic = [ foldReorderLicensed outer.Fold; foldReorderLicensed shape.Level.Fold ]
             let repro = fpReproEnabled ()
             if not repro && List.exists id lic && not (List.forall id lic) then
                 let li = lic |> List.findIndex id
                 let ui = lic |> List.findIndex not
                 Some $"a mixed nest: level {li + 1}'s fold carries a reorder licence and level {ui + 1}'s does not (v1 keeps the walk; making the licensed level strict would change the default build's bits)"
             elif not repro && List.forall id lic then
                 Some "every level's fold carries a reorder licence in a non-repro build (the licensed row walk owns it)"
             else
                 Some "an outer fold over the weighted rows: v1 builds the row action's stored vector output only (the scalar form is v1.1) -- the walk is kept"
         | _ -> None)
    | _ -> None

/// `blade plan`'s form line of a taken action (both lanes' form).
let actionForm (p: ActionPlan) : string =
    $"orbit action (rank 2, extent {p.Shape.Extent}, 1 channel, row groups of {p.RowGroup}): own / partner role terms; every row chain fed its source order from its seed"

let actionEvidence (p: ActionPlan) : string =
    let s = p.Shape
    let ws =
        if s.Weights.IsEmpty then "no weights"
        else
            s.Weights
            |> List.map (fun w ->
                let roles = [ (if w.Own then "own" else ""); (if w.Partner then "partner" else "") ] |> List.filter ((<>) "") |> String.concat " + "
                $"`{w.ArrayName}` ({roles})")
            |> String.concat ", "
    let lic =
        if p.Licensed then
            "the fold carries a reorder licence in this build: the strict form is taken for the row action too (the serial chain is an admissible association; the licence is not consumed -- BLADE_ORBIT_ACTIONS=strict restricts the form to strict builds)"
        elif fpReproEnabled () then "every fold strict in this build (BLADE_FP_REPRO=1)"
        else "the fold carries no reorder licence"
    $"one pass over the {p.PoolCells}-cell SymIdx<2, {s.Extent}> pool of `{s.SourceName}` instead of {s.Extent * s.Extent} logical reads; each off-diagonal canonical cell (a, b) read ONCE and its term evaluated TWICE -- row chain a with own = a, partner = b, row chain b with own = b, partner = a (never one weighted term at both endpoints) -- the diagonal once; weights {ws}; {lic}; each chain keeps its seed, its operand order and its kernel (accumulator first); column block {p.ColBlock}; bitwise the walk under BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off"

/// The evidence tag the LLVM emitter's own action records carry (the same tag
/// as its scatter records, so CliCommands' merge treats them alike).
let isActionRecord (d: Blade.Effects.Decision) : bool =
    d.Rule = "fold-form"
    && (match d.Outcome with
        | Blade.Effects.Declined why -> why.StartsWith "orbit action not taken"
        | Blade.Effects.Applied -> d.Evidence |> List.exists (fun e -> e.StartsWith "fold form: orbit action (rank"))

let actionDeclinedForm = "fold form: orbit action not taken -- the row walk is kept"

let actionDeclineOutcome (why: string) = Blade.Effects.Declined $"orbit action not taken: {why}"

let recordActionTaken (subject: string) (p: ActionPlan) (matrix: string list) (ompNote: string option) : unit =
    recordCodegenDecision "fold-form" subject Blade.Effects.Applied
        ([ "fold form: " + actionForm p; actionEvidence p ] @ matrix @ (match ompNote with Some n -> [ n ] | None -> []))

let recordActionDeclined (subject: string) (why: string) : unit =
    recordCodegenDecision "fold-form" subject (actionDeclineOutcome why) [ actionDeclinedForm; why ]

/// The thread requests the action's levels carry (the map kernel's and the
/// fold kernel's). v1's action is serial. C++ lane: where threads are emitted
/// the old emitter's row walk keeps the request (declined); where they are
/// not, the action is taken and the dropped request reaches the census. LLVM
/// lane: no thread runtime -- the serial action is taken whenever its own
/// admission succeeds, the request recorded as unserved there.
let actionThreadRequests (p: ActionPlan) : string list =
    [ if p.Shape.MapOmp then yield "the map"
      if p.Shape.Level.MapOmp then yield "the inner map"
      if p.Shape.Level.Fold.IsOmpParallel then yield "the fold" ]

/// THE ADMISSION (both lanes): the planner's five-party audit
/// (Optimize.planOrbitActions over the top-level bindings: producers of the
/// pool, every weight and every free scalar by payload and storage proof;
/// the consumer, the fold kernel and the seed; every intervening binding --
/// the shared transitive write analysis, unknown = refuse) plus the
/// callback-aware codegen write analysis of the term, the kernel and the
/// seed (`deferred`: the lane's deferred computations). Ok the matrix lines,
/// or Error the first failed condition.
let orbitActionAdmissible (deferred: Map<IRId, IRExpr>) (actionId: IRId) (p: ActionPlan) : Result<string list, string> =
    let s = p.Shape
    match Blade.Optimize.OrbitActionTable.tryAudit actionId with
    | None -> Error "no five-party audit recorded for this action (v1 admits a TOP-LEVEL action binding over top-level pools and weights: the storage proof is storedArrayProof): unknown declines"
    | Some audit ->
    match audit.Consumer with
    | Some why -> Error why
    | None ->
    let parties =
        [ yield ("the pool", s.SourceId)
          for w in s.Weights do yield ($"the weight `{w.ArrayName}`", w.ArrayId)
          for f in p.FreeIds do yield ("a free scalar", f) ]
    let readWhy =
        parties |> List.tryPick (fun (what, id) ->
            match Map.tryFind id audit.Reads with
            | None -> Some $"{what} is not a top-level binding ahead of the action (v1: unknown declines)"
            | Some r ->
                match r.Producer, r.Between with
                | Error why, _ -> Some why
                | _, Error why -> Some why
                | Ok _, Ok _ -> None)
    match readWhy with
    | Some why -> Error why
    | None ->
    if exprMayWriteIn deferred s.Body then Error "the cell term may write (callback-aware codegen write analysis)"
    elif callableMayWrite s.Level.Fold then Error "the fold kernel may write (callback-aware codegen write analysis)"
    elif exprMayWriteIn deferred s.Level.Seed then Error "the seed may write (callback-aware codegen write analysis)"
    else
    let line (id: IRId) =
        let r = audit.Reads.[id]
        let between = match r.Between with Ok k -> k | Error _ -> 0
        let how = match r.Producer with Ok h -> h | Error _ -> ""
        $"`{r.Name}` {how}, {between} binding(s) between it and the action -- no write"
    let weightsLine =
        if s.Weights.IsEmpty then "none"
        else s.Weights |> List.map (fun w -> line w.ArrayId) |> String.concat "; "
    let freeLine =
        if p.FreeIds.IsEmpty then "none"
        else p.FreeIds |> Seq.map line |> String.concat "; "
    Ok [ "five-party audit (planner: Optimize.scheduleMayWriteIn over the module + cseFactsOf, payloads by intervalPayloadOf, storage by storedArrayProof; emitter: the callback-aware codegen write analysis): "
         + $"producer = the pool {line s.SourceId}; "
         + $"weights = {weightsLine}; free scalars = {freeLine}; "
         + "consumer = the action's map kernel and its cell term -- no write; "
         + "fold kernel -- no write; seed -- no write, coordinate-free; "
         + "intervening = every top-level binding between each read binding and the action (counted above) -- no write, no provider effect (none is moved: the action runs at its own site and only interleaves its own pure, total row chains); "
         + "parallel bodies = none (v1's action is serial: no team, so the callback table is not consulted); "
         + "totality: the closed whitelist (Blade.IRTotality) over the term, the kernel and the seed -- no panic, so no failure can arrive in a different order; the output stays stored under its own name" ]
