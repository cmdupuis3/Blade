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
