// =============================================================================
//  OrbRank.fs -- the pure-function layer of Phase 2 in
//  docs/plan-orbidx-bijections.md: OrbIdx canonicalization, the ascending-lex
//  traversal stream, and the arithmetic rank/unrank pair.
//
//  A class is a FLAT list of levels, OUTERMOST-LAST
//  (docs/plan-orbit-index-types.md §2):
//
//      OrbIdx<[(r1,s1), ..., (rd,sd)], n>,   G = S_r1 wr ... wr S_rd
//
//  and the empty list is the trivial class Idx<n>. Everything here is a
//  function of (levels, n) only -- no Blade types, no project dependencies, so
//  the file can be `#load`ed on its own by an external checker.
//
//  What is here, and where it comes from:
//
//    cellCountChecked  §4's fold  M0 = n ; M = C(M+r-1,r) at '+' , C(M,r) at '-'
//                      with exactly-overflow-checked arithmetic. The port of
//                      proofs/OrbitEnum.fsx `foldCellsChecked` / `binomChecked`.
//    canonOrb          §5's per-level sort fold, innermost first, with the
//                      character and the zero set. The port of OrbitEnum `canon`.
//    visitStream       the bijections plan §2 SEGMENT-PEELED traversal, general
//                      depth and both signs: per level, the successor of a
//                      sub-key is enumerated by EQUALITY-PREFIX peeling, which
//                      at depth 2 is exactly OrbitEnum `segmentedNestDepth2`
//                      (segments E, then B, then A -- emitted in stream
//                      position, so the union IS the ascending-lex stream).
//                      Nothing here enumerates-then-sorts.
//    orbRank/orbUnrank §3's random-access pair, computed ARITHMETICALLY: the
//                      sub-keys rank first (recursively), the level then ranks
//                      the resulting key sequence with the lex combinadic,
//                      '+' reduced to strict by s_j = k_j + (j-1). The stream
//                      is never walked.
//    orbSuccessor      the structural next-in-stream-order, O(rank).
//    validateLevels    the ONE structural gate (level ranks >= 1) every entry
//                      point above shares; visitStreamChecked is the
//                      Error-typed door to the stream for API consumers.
//    orbReadPlan       docs/plan-orbidx-decompaction.md §2's storage read,
//    orbRead           dense[t] = chi(t) * pool[rank(canon(t))] (0 on the zero
//    orbWriteCanonical set), and its canonical-only inverse. `orbReadPlan` is
//                      the cell-type-agnostic core (gate + canon + rank +
//                      character, no pool touched) that the interpreter's
//                      Value-typed reader shares with `orbRead`'s int64
//                      reference specialization. The reference semantics the
//                      interp/codegen decompaction paths of that plan's §4
//                      must agree with, cell for cell.
//
//  INVARIANT (§3, "the one hard constraint"): rank order = the §2 nest's visit
//  order = ascending lex. tests/Test_OrbRank.fs asserts it directly against a
//  brute-force canonicalization of every raw tuple.
//
//  ---------------------------------------------------------------------------
//  DEVIATION FROM THE REQUESTED SURFACE, stated once, loudly.
//
//  `orbRank` and `orbUnrank` were specified as
//
//      orbRank   : Level list -> int list -> Result<int64, string>
//      orbUnrank : Level list -> int64    -> Result<int list, string>
//
//  i.e. without the extent. That cannot be implemented correctly: the position
//  of a tuple in the ascending-lex stream depends on n. For [(2,+)] the tuple
//  (1,1) is stream position 4 at n = 4 and position 5 at n = 5, because the
//  level's combinadic ground set is M_{i-1}, which is a function of n. (An
//  n-free rank exists only for COLEX order, which §3 rules out: "rank order =
//  DFS order ... Order innovations (colex, Gray, blocked) are out of scope".)
//
//  So both take the extent as their SECOND curried argument, exactly like
//  `visitStream` and `orbSuccessor`:
//
//      orbRank   : Level list -> int -> int list -> Result<int64, string>
//      orbUnrank : Level list -> int -> int64    -> Result<int list, string>
//
//  A caller wired to the n-free spelling gets a compile error at the call site
//  (int list vs int), not a silently wrong number.
// =============================================================================

module Blade.OrbRank

open System

// -----------------------------------------------------------------------------
// The class
// -----------------------------------------------------------------------------

/// Per-level character: OPlus = invariant (symmetric), OMinus = sgn (antisymmetric).
type OrbSign =
    | OPlus
    | OMinus

/// One level of an OrbIdx class: (rank, sign). Levels are OUTERMOST-LAST, so
/// `List.last levels` is the outermost level and `[]` is the trivial class.
type Level = int * OrbSign

let signStr (s: OrbSign) = match s with OPlus -> "+" | OMinus -> "-"

let showLevels (ls: Level list) =
    "[" + (ls |> List.map (fun (r, s) -> sprintf "(%d%s)" r (signStr s)) |> String.concat ",") + "]"

/// Number of raw axes the class acts on: the product of the level ranks
/// (1 for the empty class, whose tuples are single coordinates).
let axisRank (levels: Level list) = levels |> List.fold (fun a (r, _) -> a * r) 1

/// §7.2's load-bearing normalization: a level with r = 1 is the trivial group
/// and a no-op at EITHER sign, so it is dropped. Without it an AST could append
/// trivial levels forever.
let normalizeLevels (levels: Level list) = levels |> List.filter (fun (r, _) -> r <> 1)

/// The inner (all but outermost) class and the outermost level. Only called on
/// a non-empty list.
let private peelOuter (levels: Level list) =
    List.truncate (List.length levels - 1) levels, List.last levels

/// Structural validation of a class, shared by EVERY entry point below: each
/// level's rank must be >= 1. One producer, one message shape --
/// `cellCountChecked`, `orbRank`, `orbUnrank` and `visitStreamChecked` all
/// Error through this, and `orbSuccessor`'s None gate consumes it too, so a
/// malformed class cannot draw different verdicts from different doors.
/// (Adversarial-review hardening, 2026-08-01: previously visitStream raised,
/// the Result trio each carried its own inline check, and orbSuccessor had a
/// fourth private spelling.)
let validateLevels (levels: Level list) : Result<unit, string> =
    let rec go i lvls =
        match lvls with
        | [] -> Ok()
        | (r, s) :: rest ->
            if r < 1 then Error(sprintf "level %d (r=%d,%s): rank must be >= 1" i r (signStr s))
            else go (i + 1) rest
    go 1 levels

// -----------------------------------------------------------------------------
// Checked int64 arithmetic (§7.2: wraparound must diagnose, not corrupt).
// All operands are non-negative. Ported from proofs/OrbitEnum.fsx.
// -----------------------------------------------------------------------------

let addChecked (a: int64) (b: int64) : Result<int64, string> =
    if b > 0L && a > Int64.MaxValue - b then Error(sprintf "int64 overflow: %d + %d" a b) else Ok(a + b)

let subChecked (a: int64) (b: int64) : Result<int64, string> =
    if b > a then Error(sprintf "int64 underflow: %d - %d" a b) else Ok(a - b)

let mulChecked (a: int64) (b: int64) : Result<int64, string> =
    if a = 0L || b = 0L then Ok 0L
    elif a > Int64.MaxValue / b then Error(sprintf "int64 overflow: %d * %d" a b)
    else Ok(a * b)

let rec gcd64 (a: int64) (b: int64) = if b = 0L then a else gcd64 b (a % b)

/// Exact C(m,r) in int64. The gcd reduction makes every intermediate equal to
/// C(m-r+i, i) <= C(m,r), so the multiply-then-divide loop cannot wrap *even
/// transiently*: an overflow here means the true binomial exceeds int64.
let binomChecked (m: int64) (r: int) : Result<int64, string> =
    if r < 0 then Error(sprintf "C(%d,%d): negative rank" m r)
    elif m < 0L then Error(sprintf "C(%d,%d): negative extent" m r)
    elif int64 r > m then Ok 0L
    else
        let mutable acc = Ok 1L
        for i in 1 .. r do
            match acc with
            | Error _ -> ()
            | Ok a ->
                let f = m - int64 r + int64 i     // acc*f/i is exact; and since
                let g = gcd64 f (int64 i)         // gcd(i/g,f/g)=1, (i/g) divides acc
                acc <- mulChecked (a / (int64 i / g)) (f / g)
        acc

/// Sequence a list of Results, keeping the first error.
let private resultAll (xs: Result<'a, string> list) : Result<'a list, string> =
    let rec go acc rest =
        match rest with
        | [] -> Ok(List.rev acc)
        | Ok v :: tl -> go (v :: acc) tl
        | Error e :: _ -> Error e
    go [] xs

// -----------------------------------------------------------------------------
// §4: cardinality
// -----------------------------------------------------------------------------

/// M0 = n ; Mi = C(M + r - 1, r) if s = '+' , C(M, r) if s = '-'.
/// Every step is exactly overflow-checked; `cellCountChecked [] n = Ok n`.
let cellCountChecked (levels: Level list) (n: int64) : Result<int64, string> =
    match validateLevels levels with
    | Error e -> Error e
    | Ok() ->
    if n < 0L then Error(sprintf "negative extent %d" n) else
    let rec go i lvls (m: int64) =
        match lvls with
        | [] -> Ok m
        | (r, s) :: rest ->
            let top = if s = OPlus then addChecked m (int64 r - 1L) else Ok m
            match top |> Result.bind (fun t -> binomChecked t r) with
            | Error e -> Error(sprintf "level %d (r=%d,%s): %s" i r (signStr s) e)
            | Ok m' -> go (i + 1) rest m'
    go 1 levels n

// -----------------------------------------------------------------------------
// §5: canonicalization
// -----------------------------------------------------------------------------

let private hasDupKeys (keys: int list list) =
    let s = System.Collections.Generic.HashSet<int list>()
    keys |> List.exists (fun k -> not (s.Add k))

/// Parity of the permutation that sorts `keys` (+1 even, -1 odd), by inversions.
let sortParity (keys: int list list) =
    let a = List.toArray keys
    let mutable inv = 0
    for i in 0 .. a.Length - 1 do
        for j in i + 1 .. a.Length - 1 do
            if compare a.[i] a.[j] > 0 then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

/// Canonical form of one raw tuple plus its character. `None` = the tuple is in
/// the zero set (§5: an s = '-' level kills tuples with two equal sub-blocks).
/// Levels are outermost-last, so the LAST level is peeled first and the sorts
/// happen innermost-first. `canonOrb [] [x] = Some([x], 1)`.
let rec canonOrb (levels: Level list) (tup: int list) : (int list * int) option =
    match levels with
    | [] ->
        match tup with
        | [ x ] -> Some([ x ], 1)
        | _ -> failwithf "canonOrb: base case needs a 1-element tuple, got %d" (List.length tup)
    | _ ->
        let inner, (r, s) = peelOuter levels
        if r < 1 then failwithf "canonOrb: level rank %d must be >= 1" r
        let total = List.length tup
        if total % r <> 0 then failwithf "canonOrb: tuple length %d not divisible by r=%d" total r
        let subs = tup |> List.chunkBySize (total / r) |> List.map (canonOrb inner)
        if List.exists Option.isNone subs then None else
        let parts = subs |> List.map Option.get
        let keys = parts |> List.map fst
        let sgn = parts |> List.fold (fun a (_, c) -> a * c) 1
        let sorted () = List.concat (List.sortWith compare keys)
        match s with
        | OPlus -> Some(sorted (), sgn)
        | OMinus when hasDupKeys keys -> None
        | OMinus -> Some(sorted (), sgn * sortParity keys)

// -----------------------------------------------------------------------------
// §2: the segment-peeled traversal stream
// -----------------------------------------------------------------------------
//
// A canonical tuple of class `inner @ [(r,s)]` is the concatenation of r
// canonical sub-keys of class `inner`, weakly ('+') or strictly ('-')
// increasing in lex order. Sub-keys all have the same length, so ascending lex
// on the FLAT tuple is exactly ascending lex on the sub-key sequence -- that is
// what makes the decomposition compositional.
//
// The successor set of a sub-key is enumerated by EQUALITY-PREFIX peeling: a
// key K > L differs from L first at some sub-key position p, where it is
// strictly greater, and is free (subject to canonicality) afterwards. That
// splits the region into disjoint straight-line segments, and emitting the
// equality segment first and then p = r-1 down to 0 puts them in exact stream
// order, so nothing is sorted and nothing is revisited.
//
// At depth 2 this unrolls to OrbitEnum's `segmentedNestDepth2`:
//   E  K2 = K1              (emitted only when the outer sign is '+')
//   B  i2 = i1, j2 > j1     (equality prefix of length 1)
//   A  i2 > i1, j2 free     (equality prefix of length 0)

/// Canonical keys of `levels` over extent `n`, in ascending lex order,
/// restricted to those > `lo` (strict) or >= `lo` (not strict); `lo = None`
/// means unrestricted. Lazy at every level.
let rec keysFrom (levels: Level list) (n: int) (lo: int list option) (strict: bool) : seq<int list> =
    match levels with
    | [] ->
        let start =
            match lo with
            | None -> 0
            | Some [ p ] -> if strict then p + 1 else p
            | Some other ->
                failwithf "OrbRank.keysFrom: base class needs a 1-element bound, got %d" (List.length other)
        seq { for x in max 0 start .. n - 1 -> [ x ] }
    | _ ->
        let inner, (r, s) = peelOuter levels
        if r < 1 then failwithf "OrbRank.keysFrom: level rank %d must be >= 1" r
        let strictLevel = (s = OMinus)
        // The free tail: `m` further sub-keys, each above the previous one per
        // the level sign, in ascending lex order.
        let rec freeTail (m: int) (prev: int list option) : seq<int list> =
            if m <= 0 then Seq.singleton []
            else
                seq {
                    for k in keysFrom inner n prev strictLevel do
                        for rest in freeTail (m - 1) (Some k) do
                            yield k @ rest
                }
        match lo with
        | None -> freeTail r None
        | Some flat ->
            let total = List.length flat
            if total % r <> 0 then
                failwithf "OrbRank.keysFrom: bound length %d not divisible by r=%d" total r
            let ls = flat |> List.chunkBySize (total / r) |> List.toArray
            seq {
                // Segment E: the equality region, at its stream position.
                if not strict then yield flat
                // Then one segment per equality-prefix length, longest first --
                // which is ascending, since a longer agreement with `lo` means
                // the first difference (upward) happens later.
                for p in r - 1 .. -1 .. 0 do
                    let prefix = Array.sub ls 0 p |> Array.toList |> List.concat
                    for k in keysFrom inner n (Some ls.[p]) true do
                        for rest in freeTail (r - 1 - p) (Some k) do
                            yield prefix @ k @ rest
            }

/// The ascending-lex canonical stream of the class: every stored cell exactly
/// once, in storage order. Lazy; general depth; both signs at every level.
///
/// RAW FORM: a malformed class (a level with r < 1) RAISES from inside the
/// enumeration, and a negative extent silently yields the empty stream --
/// kept for oracle/`#load` use where an exception is the right loudness.
/// API consumers should call `visitStreamChecked`, which fails the same way
/// the other entry points do.
let visitStream (levels: Level list) (n: int) : seq<int list> = keysFrom levels n None false

/// The Error-typed door to the traversal: validates the class and the extent
/// UP FRONT (through the shared `validateLevels`), and only then exposes the
/// lazy stream -- whose enumeration can then no longer raise, because the
/// only reachable `failwithf`s in `keysFrom` are the level-rank guard (just
/// validated) and internal bound-shape invariants that `lo = None` entry
/// preserves. This is the visitStream failure-mode unification the
/// 2026-08-01 adversarial review asked for: five entry points, one verdict
/// for a malformed class.
let visitStreamChecked (levels: Level list) (n: int) : Result<seq<int list>, string> =
    match validateLevels levels with
    | Error e -> Error e
    | Ok() ->
        if n < 0 then Error(sprintf "negative extent %d" n)
        else Ok(keysFrom levels n None false)

// -----------------------------------------------------------------------------
// §3: the arithmetic rank/unrank pair
// -----------------------------------------------------------------------------

/// Lex rank of a strictly increasing sequence drawn from [0, ground).
/// Position j skips every value v with c_{j-1} < v < c_j, each contributing
/// C(ground-1-v, r-j) completions; the inner sum telescopes by hockey-stick to
///     C(ground - c_{j-1} - 1, r-j+1) - C(ground - c_j, r-j+1).
/// Every term is <= C(ground, r) = this level's cell count, so an overflow here
/// means the class itself does not fit in int64.
let lexRankStrict (ground: int64) (cs: int64 list) : Result<int64, string> =
    let r = List.length cs
    let mutable acc = Ok 0L
    let mutable prev = -1L
    let mutable j = 1
    for c in cs do
        (match acc with
         | Error _ -> ()
         | Ok a ->
            if c <= prev then acc <- Error(sprintf "lexRankStrict: %d does not exceed %d" c prev)
            elif c >= ground then acc <- Error(sprintf "lexRankStrict: %d outside [0,%d)" c ground)
            else
                acc <-
                    binomChecked (ground - prev - 1L) (r - j + 1)
                    |> Result.bind (fun hi ->
                        binomChecked (ground - c) (r - j + 1)
                        |> Result.bind (fun lo -> subChecked hi lo)
                        |> Result.bind (addChecked a)))
        prev <- c
        j <- j + 1
    acc

/// Greedy inverse of `lexRankStrict`. The per-position search is a BINARY
/// search on the monotone hockey-stick partial sum, mirroring the C++
/// `rnk::unrank` (orbit_wreath_utilities.hpp): with the prefix fixed,
///     pre(v) = C(ground-prev-1, r-j+1) - C(ground-v, r-j+1)
/// counts the completions skipped by choosing position j >= v; it is 0 at
/// v = prev+1 and nondecreasing, so position j is the LARGEST v whose pre(v)
/// still fits under the remainder. O(r log ground) binomials instead of the
/// old linear scan's O(ground) -- which mattered the moment the §7.2 wall
/// became a test: at depth-3 n=360 the outer ground set is ~2.1e9, and a
/// linear scan cannot cross it. Every binomial here is <= C(ground, r) =
/// this level's cell count, so the arithmetic overflows only when the class
/// itself leaves int64.
let lexUnrankStrict (ground: int64) (r: int) (rank: int64) : Result<int64 list, string> =
    if r < 0 then Error(sprintf "lexUnrankStrict: negative rank width %d" r) else
    match binomChecked ground r with
    | Error e -> Error e
    | Ok total ->
        if rank < 0L || rank >= total then
            Error(sprintf "rank %d outside [0,%d)" rank total)
        else
            let res = ResizeArray<int64>()
            let mutable rem = rank
            let mutable prev = -1L
            let mutable err : string option = None
            for j in 1 .. r do
                if err.IsNone then
                    match binomChecked (ground - prev - 1L) (r - j + 1) with
                    | Error e -> err <- Some e
                    | Ok base_ ->
                        if prev + 1L > ground - 1L then
                            err <- Some "lexUnrankStrict: ran off the ground set"
                        else
                            let mutable lo = prev + 1L
                            let mutable hi = ground - 1L
                            while err.IsNone && lo < hi do
                                let mid = lo + (hi - lo + 1L) / 2L
                                match binomChecked (ground - mid) (r - j + 1) with
                                | Error e -> err <- Some e
                                | Ok c -> if base_ - c <= rem then lo <- mid else hi <- mid - 1L
                            if err.IsNone then
                                match binomChecked (ground - lo) (r - j + 1) with
                                | Error e -> err <- Some e
                                | Ok c ->
                                    rem <- rem - (base_ - c)
                                    res.Add lo
                                    prev <- lo
            match err with
            | Some e -> Error e
            | None -> Ok(List.ofSeq res)

/// '+' reduces to strict by s_j = k_j + (j-1) -- a fixed per-position shift.
/// This realizes the same strict<->weak correspondence canonLeftJustify uses,
/// in a DIFFERENT encoding (that helper stores successive differences); same
/// bijection, not the same map.
let private strictify (s: OrbSign) (a: int64 list) : Result<int64 list, string> =
    match s with
    | OMinus -> Ok a
    | OPlus -> a |> List.mapi (fun i v -> addChecked v (int64 i)) |> resultAll

let private groundOf (s: OrbSign) (m: int64) (r: int) : Result<int64, string> =
    match s with
    | OMinus -> Ok m
    | OPlus -> addChecked m (int64 r - 1L)

/// Recursive worker for `orbRank`; the class is already validated.
let rec private orbRankGo (levels: Level list) (n: int) (t: int list) : Result<int64, string> =
    match levels with
    | [] ->
        match t with
        | [ x ] when x >= 0 && x < n -> Ok(int64 x)
        | [ x ] -> Error(sprintf "orbRank: coordinate %d outside [0,%d)" x n)
        | _ -> Error(sprintf "orbRank: base class needs a 1-element tuple, got %d" (List.length t))
    | _ ->
        let inner, (r, s) = peelOuter levels
        let total = List.length t
        if total = 0 || total % r <> 0 then
            Error(sprintf "orbRank: tuple length %d not divisible by r=%d" total r)
        else
            // The sub-keys rank first; the level then ranks the key sequence.
            t
            |> List.chunkBySize (total / r)
            |> List.map (orbRankGo inner n)
            |> resultAll
            |> Result.bind (fun a ->
                let ordered =
                    a |> List.pairwise
                      |> List.forall (fun (x, y) -> if s = OMinus then x < y else x <= y)
                if not ordered then
                    Error(sprintf "orbRank: tuple is not canonical at the outer level (%d%s): sub-key ranks %s"
                                  r (signStr s) (a |> List.map string |> String.concat ","))
                else
                    cellCountChecked inner (int64 n)
                    |> Result.bind (fun m ->
                        groundOf s m r
                        |> Result.bind (fun ground ->
                            strictify s a |> Result.bind (lexRankStrict ground))))

/// Position of a canonical tuple in `visitStream levels n`, computed
/// arithmetically. Errors on a malformed class (via `validateLevels`), a
/// non-canonical tuple, an out-of-range coordinate, a malformed shape, or
/// int64 overflow.
///
/// NOTE the extent argument -- see the DEVIATION block at the top of this file.
let orbRank (levels: Level list) (n: int) (t: int list) : Result<int64, string> =
    match validateLevels levels with
    | Error e -> Error e
    | Ok() -> orbRankGo levels n t

/// Recursive worker for `orbUnrank`; the class is already validated.
let rec private orbUnrankGo (levels: Level list) (n: int) (rank: int64) : Result<int list, string> =
    match levels with
    | [] ->
        if rank < 0L || rank >= int64 n then Error(sprintf "orbUnrank: rank %d outside [0,%d)" rank n)
        else Ok [ int rank ]
    | _ ->
        let inner, (r, s) = peelOuter levels
        cellCountChecked inner (int64 n)
        |> Result.bind (fun m ->
            groundOf s m r
            |> Result.bind (fun ground ->
                lexUnrankStrict ground r rank
                |> Result.bind (fun cs ->
                    // undo s_j = k_j + (j-1)
                    cs
                    |> List.mapi (fun i c -> if s = OPlus then subChecked c (int64 i) else Ok c)
                    |> resultAll
                    |> Result.bind (fun a ->
                        a
                        |> List.map (fun v -> orbUnrankGo inner n v)
                        |> resultAll
                        |> Result.map List.concat))))

/// Inverse of `orbRank`: the tuple at position `rank` of `visitStream levels n`.
/// Errors on a malformed class (via `validateLevels`), an out-of-range rank,
/// or int64 overflow.
///
/// NOTE the extent argument -- see the DEVIATION block at the top of this file.
let orbUnrank (levels: Level list) (n: int) (rank: int64) : Result<int list, string> =
    match validateLevels levels with
    | Error e -> Error e
    | Ok() -> orbUnrankGo levels n rank

/// The next canonical tuple in stream order, or None at the last one.
/// Structural, O(rank) -- it never walks the stream: at the outermost level the
/// rightmost sub-key that can be advanced is advanced to its own successor, and
/// everything to its right is refilled minimally (identical copies at a '+'
/// level, successive successors at a '-' one).
let rec private orbSuccessorRec (levels: Level list) (n: int) (t: int list) : int list option =
    match levels with
    | [] ->
        match t with
        | [ x ] -> if x + 1 < n then Some [ x + 1 ] else None
        | _ -> None
    | _ ->
        let inner, (r, s) = peelOuter levels
        let total = List.length t
        if r < 1 || total = 0 || total % r <> 0 then None
        else
            let ls = t |> List.chunkBySize (total / r) |> List.toArray
            // The minimal completion of `m` sub-keys strictly after `k`.
            let rec fillTail (cur: int list) (m: int) (acc: int list list) : int list list option =
                if m <= 0 then Some(List.rev acc)
                else
                    match s with
                    | OPlus -> fillTail cur (m - 1) (cur :: acc)
                    | OMinus ->
                        match orbSuccessorRec inner n cur with
                        | None -> None
                        | Some nxt -> fillTail nxt (m - 1) (nxt :: acc)
            let mutable res : int list option = None
            let mutable i = r - 1
            while res.IsNone && i >= 0 do
                (match orbSuccessorRec inner n ls.[i] with
                 | Some k ->
                     match fillTail k (r - 1 - i) [] with
                     | Some tail ->
                         let prefix = Array.sub ls 0 i |> Array.toList
                         res <- Some(List.concat (prefix @ [ k ] @ tail))
                     | None -> ()
                 | None -> ())
                i <- i - 1
            res

/// Validating wrapper. §3 names successor as the resumable/streamed cold-path
/// mechanism, and a silent monotonicity break on malformed input is exactly
/// what a range read cannot detect — so the input must be a genuine canonical
/// tuple of this class before the structural advance runs: right length,
/// digits in [0, n), canonical fixed point (which also rejects zero-set
/// tuples). None still means "last cell" for valid input; malformed input is
/// also None, deterministically, never a plausible-but-wrong neighbor.
/// (Adversarial-review finding, 2026-08-01.)
let orbSuccessor (levels: Level list) (n: int) (t: int list) : int list option =
    let ranksOk = match validateLevels levels with Ok() -> true | Error _ -> false
    if not ranksOk || n < 0 || List.length t <> axisRank levels then None
    elif t |> List.exists (fun d -> d < 0 || d >= n) then None
    else
        match canonOrb levels t with
        | Some (c, _) when c = t -> orbSuccessorRec levels n t
        | _ -> None

// -----------------------------------------------------------------------------
// The storage read/write path (docs/plan-orbidx-decompaction.md §2)
// -----------------------------------------------------------------------------
//
//     dense[t] = 0                                   if canon(t) is zero-set
//              = chi(t) * pool[orbRank(canon(t))]    otherwise
//
// This is the REFERENCE SEMANTICS: the thing the interp `decompactOrb` and the
// C++ streaming scatter of that plan's §4 must agree with cell for cell, and
// the thing a held-out table can pin. So the cells are int64 and the
// arithmetic is exact -- no float, no rounding, no "close enough". A generic
// `orbRead<'T>` over an arbitrary numeric cell type is FUTURE WORK: it needs a
// negation that is total for the cell type (see the Int64.MinValue case
// below), which is a per-type decision, not an inlined `~-`. Complex cells
// additionally need the conjugation character the plan §2 rules out of the
// +-1 system entirely.
//
// Note what is NOT here: nothing walks `visitStream`. Both entry points go
// canonOrb -> orbRank, so the read cost is the arithmetic rank's, and a stream
// / rank disagreement would be a bug in the pair these two inherit (pinned
// against brute force in tests/Test_OrbRank.fs), not a second order convention
// invented here.

/// A raw tuple, for message text: `(0,1,2,3)`.
let private showTuple (t: int list) =
    "(" + (t |> List.map string |> String.concat ",") + ")"

/// The shared structural gate of the storage path: the class and the extent
/// (through `cellCountChecked`, hence through `validateLevels` -- still the
/// ONE structural door, per the 2026-08-01 unification), then the pool size,
/// the tuple's axis rank, and its digit range. Returns the class's cell count.
///
/// Every one of these is a REFUSAL, never a repair: an out-of-range digit or a
/// short tuple canonicalizes perfectly happily into some other class's cell,
/// so letting either through would produce a plausible number read from the
/// wrong offset -- the silent aliased read this gate exists to make
/// impossible. `who` prefixes the messages this function raises itself; the
/// malformed-class verdict is passed through UNPREFIXED so that a bad class
/// still draws the identical string from every door in the file.
let private storageGate (who: string) (levels: Level list) (n: int) (poolLen: int) (t: int list)
                        : Result<int64, string> =
    match cellCountChecked levels (int64 n) with
    | Error e -> Error e
    | Ok m ->
        if int64 poolLen <> m then
            Error(sprintf "%s: pool has %d cells, %s at n=%d needs %d"
                          who poolLen (showLevels levels) n m)
        else
            let axes = axisRank levels
            let len = List.length t
            if len <> axes then
                Error(sprintf "%s: tuple %s has length %d, %s acts on %d axes"
                              who (showTuple t) len (showLevels levels) axes)
            else
                match t |> List.tryFind (fun d -> d < 0 || d >= n) with
                | Some d -> Error(sprintf "%s: coordinate %d outside [0,%d)" who d n)
                | None -> Ok m

/// Where a §2 read lands, WITHOUT touching a pool: everything about
/// `dense[t]` that is a function of `(levels, n, t)` alone.
///
///   OrbZeroCell        canon(t) is in the zero set. The value is 0 and NO cell
///                      is stored -- the in-domain zero the header's DOMAIN
///                      CONTRACT insists is a value, not an error.
///   OrbPoolCell (i,chi) the value is `chi * pool[i]`, chi in {-1,+1}.
///
/// This is the shared core of every §2 reader, and it exists because the cell
/// type is not universal: the reference `orbRead` below is int64 (exact, so a
/// held-out table can pin it), while the interpreter's pool holds `Value` and
/// the compiled path holds `double`. Negation is the only per-type decision in
/// the read (see the Int64.MinValue case in `orbRead`), so it -- and ONLY it --
/// is what the callers specialize. Nothing else about the read is re-derived
/// anywhere: the gate, the canonicalization, the rank and the zero-set/
/// out-of-domain split all live here, once.
type OrbReadPlan =
    | OrbZeroCell
    | OrbPoolCell of index: int * chi: int

/// The (levels, n, t)-only half of §2's read. `who` prefixes the refusal text,
/// so each caller's messages name the door the user actually went through;
/// `poolLen` is the cell count the caller has, checked against the class's fold
/// exactly as `orbRead`'s own gate does. Only MALFORMED input is refused (bad
/// class, negative extent, wrong pool size, wrong axis rank, digit outside
/// [0,n)) -- a MIRRORED tuple is a legal read that returns chi = -1.
let orbReadPlan (who: string) (levels: Level list) (n: int) (poolLen: int) (t: int list)
                : Result<OrbReadPlan, string> =
    match storageGate who levels n poolLen t with
    | Error e -> Error e
    | Ok _ ->
        // The gate has established length = axisRank and every level rank >= 1,
        // so canonOrb's three `failwithf` guards are all unreachable here.
        match canonOrb levels t with
        | None -> Ok OrbZeroCell
        | Some (c, chi) ->
            match orbRank levels n c with
            | Error e -> Error e
            | Ok r ->
                if r < 0L || r >= int64 poolLen then
                    // Unreachable while rank < cellCount = poolLen; kept so a
                    // future rank bug is a diagnosis, not an IndexOutOfRange.
                    Error(sprintf "%s: rank %d outside the pool [0,%d)" who r poolLen)
                else Ok(OrbPoolCell(int r, chi))

/// §2's read: the value of the dense tensor at ANY raw tuple `t`, served out
/// of the canonical pool. Zero on the zero set, `chi(t) * pool[rank]`
/// otherwise -- so a mirrored tuple is a legal read that returns the signed
/// cell, and only MALFORMED input is refused (bad class, negative extent,
/// wrong pool size, wrong axis rank, digit outside [0,n)).
let orbRead (levels: Level list) (n: int) (pool: int64[]) (t: int list) : Result<int64, string> =
    match orbReadPlan "orbRead" levels n (Array.length pool) t with
    | Error e -> Error e
    | Ok OrbZeroCell -> Ok 0L
    | Ok (OrbPoolCell (i, chi)) ->
        let v = pool.[i]
        if chi >= 0 then Ok v
        elif v = Int64.MinValue then
            // The one value whose negation leaves int64. §7.2's rule applies to
            // the read too: wraparound must diagnose.
            Error(sprintf "int64 overflow: -(%d)" v)
        else Ok(-v)

/// §2's read inverted, and ONLY at a canonical cell: `t` must be a canonOrb
/// fixed point (which forces character +1, since a fixed point sorts with no
/// inversions at every level), in range, with a correctly sized pool. A
/// mirrored, zero-set, out-of-range, wrong-length or wrong-pool argument is
/// refused with its own message shape, and the pool is left untouched.
///
/// Mirrored write-through (solving `chi * pool[rank] = v` by dividing out the
/// character) is DELIBERATELY not provided: it would make one pool cell
/// writable under all |orbit| spellings of its tuple, turning an ordinary
/// scatter into silent last-writer-wins aliasing that no after-the-fact
/// well-definedness check can reconstruct -- and on the zero set the equation
/// has no solution at all for v <> 0, so the "obvious" generalization is
/// partial as well as unsafe. Callers that mean to fill a pool should
/// canonicalize first (canonOrb) and write the canonical cell once.
let orbWriteCanonical (levels: Level list) (n: int) (pool: int64[]) (t: int list) (v: int64)
                      : Result<unit, string> =
    match storageGate "orbWriteCanonical" levels n (Array.length pool) t with
    | Error e -> Error e
    | Ok _ ->
        match canonOrb levels t with
        | None ->
            Error(sprintf "orbWriteCanonical: tuple %s is in the zero set of %s -- it has no pool cell"
                          (showTuple t) (showLevels levels))
        | Some (c, chi) when c <> t ->
            Error(sprintf "orbWriteCanonical: tuple %s is not canonical for %s (canonical form %s, character %+d)"
                          (showTuple t) (showLevels levels) (showTuple c) chi)
        | Some (_, chi) when chi <> 1 ->
            // Unreachable: a canonOrb fixed point sorts with zero inversions at
            // every level, so its character is +1. Kept as a loud invariant.
            Error(sprintf "orbWriteCanonical: canonical tuple %s of %s carries character %+d, expected +1"
                          (showTuple t) (showLevels levels) chi)
        | Some _ ->
            match orbRank levels n t with
            | Error e -> Error e
            | Ok r ->
                if r < 0L || r >= int64 pool.Length then
                    Error(sprintf "orbWriteCanonical: rank %d outside the pool [0,%d)" r pool.Length)
                else
                    pool.[int r] <- v
                    Ok()
