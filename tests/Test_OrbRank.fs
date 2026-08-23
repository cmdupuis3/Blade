// Pins for the OrbIdx bijection layer (src/OrbRank.fs) — Phase 2's pure
// functions from docs/plan-orbidx-bijections.md: the §4 cardinality fold, the
// §5 canonicalizer, the §2 segment-peeled traversal stream, and the §3
// arithmetic rank/unrank pair.
//
// The ground truth is BRUTE FORCE, not a second closed form: every one of the
// n^rank raw tuples is canonicalized with `canonOrb`, the distinct results are
// sorted, and that list — content AND order — is what `visitStream` must
// reproduce cell for cell. Everything else is pinned against that list:
// `cellCountChecked` is its length, `orbRank` is the index of each element,
// `orbUnrank` is the inverse, and the `orbSuccessor` chain from the first
// element is the whole list.
//
// This is §3's "one hard constraint" made assertable: rank order = the §2
// nest's visit order = ascending lex. A read→write roundtrip cannot catch an
// order mismatch (both sides shift together — the antisym storage post-mortem),
// so the stream is compared against an independent enumeration, and the two
// depth-1 classes are additionally cross-checked against the hand-written
// triangular offset formulas the existing SymIdx/AntisymIdx storage uses.
//
// The depth-2 traversal carries a second, independent emitter: a local
// transcription of `segmentedNestDepth2` from proofs/OrbitEnum.fsx (the E/B/A
// peeling written as literal loop nests), so the general recursion in
// OrbRank.keysFrom is checked against the hand-unrolled shape the plan §2
// specifies, not only against the brute-force set.
//
// The last section pins the STORAGE path on top of all of that
// (docs/plan-orbidx-decompaction.md §2, `orbRead`/`orbWriteCanonical`):
// dense[t] = chi(t)*pool[rank(canon(t))], zero on the zero set. Its oracle is
// again independent — canonOrb for the character, the visitStream position for
// the offset — because a read->write roundtrip cancels a pool shift exactly.
module Blade.Tests.OrbRankReview

open System.Collections.Generic
open Blade.Tests.TestHarness
open Blade.OrbRank

/// Ground truth: canonicalize every raw tuple, keep the distinct canonical
/// forms, sort them. `List.sort` on equal-length int lists IS ascending lex.
let private bruteCanonical (levels: Level list) (n: int) : int list list =
    let rank = axisRank levels
    let total = pown (int64 n) rank
    if total > 5_000_000L then failwith $"bruteCanonical: {total} tuples is too many"
    let seen = HashSet<int list>()
    let d = Array.zeroCreate rank
    for e in 0L .. total - 1L do
        let mutable q = e
        for j in rank - 1 .. -1 .. 0 do
            d.[j] <- int (q % int64 n)
            q <- q / int64 n
        match canonOrb levels (List.ofArray d) with
        | Some (k, _) -> seen.Add k |> ignore
        | None -> ()
    seen |> List.ofSeq |> List.sort

/// The hand-unrolled depth-2 nest of plan §2, transcribed from
/// `segmentedNestDepth2` in proofs/OrbitEnum.fsx: per K1 body, in stream order,
/// E (K2 = K1, '+' outer only), B (first coords equal, second strictly
/// greater), A (first coord strictly greater, inner simplex free). No
/// conditional appears in any bound.
let private segmentedNestDepth2 (sInner: OrbSign) (sOuter: OrbSign) (n: int) : int list list =
    let res = ResizeArray<int list>()
    let innerLo i = match sInner with OPlus -> i | OMinus -> i + 1
    for i1 in 0 .. n - 1 do
        for j1 in innerLo i1 .. n - 1 do
            (match sOuter with
             | OPlus -> res.Add [ i1; j1; i1; j1 ]
             | OMinus -> ())
            for j2 in j1 + 1 .. n - 1 do
                res.Add [ i1; j1; i1; j2 ]
            for i2 in i1 + 1 .. n - 1 do
                for j2 in innerLo i2 .. n - 1 do
                    res.Add [ i1; j1; i2; j2 ]
    List.ofSeq res

/// The sweep menu is a stated CLOSURE, not a curated list (adversarial-review
/// follow-up, 2026-08-01: the degenerate corners -- the empty class, rank-1
/// levels -- were exactly the rows a curated list forgot, and the C++
/// orb_rank bounds bug survived 149 green checks in their absence). Every
/// class over the full level alphabet (r <= 3, both signs) up to depth 2,
/// plus every sign pattern at depth 3 over rank 2:
///
///     1 empty + 6 depth-1 + 36 depth-2 + 8 exact-depth-3 = 51 classes,
///
/// the SAME closure the C++ menu generates (orb_wreath_tests.cpp `closure` /
/// `exact`). A class family not swept is now a statement about the bound
/// (r <= 3 at d <= 2; r = 2 at d = 3), never an oversight inside it. The old
/// curated rows -- Idx, SymIdx, AntisymIdx, RiemannIdx, func(A,A), the mixed
/// depth-3 -- are all elements of this closure.
let private levelAlphabet : Level list =
    [ for r in 1 .. 3 do
        for s in [ OPlus; OMinus ] do
            yield (r, s) ]

let private sweepClasses : Level list list =
    [ yield []
      for l1 in levelAlphabet do
          yield [ l1 ]
          for l2 in levelAlphabet do
              yield [ l1; l2 ]
      for s1 in [ OPlus; OMinus ] do
          for s2 in [ OPlus; OMinus ] do
              for s3 in [ OPlus; OMinus ] do
                  yield [ (2, s1); (2, s2); (2, s3) ] ]

// -----------------------------------------------------------------------------
// The group-character oracle (adversarial-review hardening, 2026-08-01).
//
// The class's wreath group, built as explicit permutations exactly the way
// proofs/OrbitEnum.fsx `buildWreath` builds it -- G_0 trivial on one point,
// G_i = G_{i-1} wr S_{r_i} on deg*r_i points by (block b, offset x) ->
// (pi(b), g_b(x)) -- with the CHARACTER carried along: chi(e) multiplies the
// sub-elements' characters and, at a '-' level, sgn(pi). The oracle asserts
//
//     canonOrb (g . t) = chi(g) * canonOrb t
//
// (same canonical key; sign scaled by chi; zero set closed under the action)
// over the whole group. This is the ONE check that pins the canonicalizer's
// CHARACTER without a second canonicalizer: the hand spot checks sample a few
// points, and every stream/rank/count pin is sign-blind off the canonical
// set, so a global sign-convention drift would sail through all of them.
// The C++ harness runs the same oracle over its whole menu
// (src/cpp/orb_wreath_tests.cpp, section (g)).
// -----------------------------------------------------------------------------

let private allPermsArr (k: int) : int[][] =
    let res = ResizeArray<int[]>()
    let cur = Array.init k id
    let swap i j = let t = cur.[i] in cur.[i] <- cur.[j]; cur.[j] <- t
    let rec go i =
        if i = k then res.Add(Array.copy cur)
        else for j in i .. k - 1 do (swap i j; go (i + 1); swap i j)
    go 0
    res.ToArray()

/// +1 / -1 parity of a permutation, by inversion count.
let private permSign (p: int[]) : int =
    let mutable inv = 0
    for i in 0 .. p.Length - 1 do
        for j in i + 1 .. p.Length - 1 do
            if p.[i] > p.[j] then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

/// The full signed wreath group of `levels` as (permutation, character) pairs.
/// Levels are outermost-last, so folding head-to-tail wreathes innermost-first
/// -- the same order buildWreath takes its ranks.
let private buildSignedWreath (levels: Level list) : (int[] * int)[] =
    let mutable g = [| ([| 0 |], 1) |]
    let mutable deg = 1
    for (r, s) in levels do
        let pis = allPermsArr r
        let acc = ResizeArray<int[] * int>()
        let pick = Array.zeroCreate r
        let rec tuples b =
            if b < r then
                for t in 0 .. g.Length - 1 do
                    pick.[b] <- t
                    tuples (b + 1)
            else
                for pi in pis do
                    let e = Array.zeroCreate (deg * r)
                    let mutable chi = if s = OMinus then permSign pi else 1
                    for blk in 0 .. r - 1 do
                        let (gp, gc) = g.[pick.[blk]]
                        chi <- chi * gc
                        for x in 0 .. deg - 1 do
                            e.[blk * deg + x] <- pi.[blk] * deg + gp.[x]
                    acc.Add(e, chi)
        tuples 0
        g <- acc.ToArray()
        deg <- deg * r
    g

/// Every raw tuple of the class's axis rank over [0, n), in base-n order.
let private allRawTuples (levels: Level list) (n: int) : seq<int list> =
    let rank = axisRank levels
    let total = pown (int64 n) rank
    seq {
        let d = Array.zeroCreate rank
        for e in 0L .. total - 1L do
            let mutable q = e
            for j in rank - 1 .. -1 .. 0 do
                d.[j] <- int (q % int64 n)
                q <- q / int64 n
            yield List.ofArray d
    }

let runOrbRankTests () : BlockResult =
    printHeader "OrbIdx bijections (OrbRank.fs)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [ name ]
            resultLine Fail name detail

    // ---- §4 depth-1 anchors: the two closed forms, independently written ----
    // C(n+r-1,r) at '+' and C(n,r) at '-', computed here by a plain factorial
    // ratio in bigint so no binomChecked code is shared with the thing tested.
    let binomBig (m: int) (r: int) : bigint =
        if r < 0 || m < r then bigint 0
        else
            let mutable acc = bigint 1
            for i in 1 .. r do acc <- acc * bigint (m - r + i) / bigint i
            acc
    let mutable anchorOk = true
    let mutable anchorBad = ""
    for n in 0 .. 12 do
        for r in 1 .. 4 do
            let wantPlus = binomBig (n + r - 1) r
            let wantMinus = binomBig n r
            let gotPlus = cellCountChecked [ (r, OPlus) ] (int64 n)
            let gotMinus = cellCountChecked [ (r, OMinus) ] (int64 n)
            if gotPlus <> Ok(int64 wantPlus) || gotMinus <> Ok(int64 wantMinus) then
                anchorOk <- false
                anchorBad <- sprintf "n=%d r=%d: %A/%A vs %A/%A" n r gotPlus gotMinus wantPlus wantMinus
    check "depth-1 anchors: cellCount [(r,+)] = C(n+r-1,r), [(r,-)] = C(n,r), n<=12, r<=4"
          anchorOk anchorBad
    check "C(n+1,2) spot: [(2,+)] at n = 4 is 10, at n = 100 is 5050"
          (cellCountChecked [ (2, OPlus) ] 4L = Ok 10L
           && cellCountChecked [ (2, OPlus) ] 100L = Ok 5050L) ""
    check "cellCountChecked [] n = Ok n (the trivial class)"
          (cellCountChecked [] 7L = Ok 7L && cellCountChecked [] 0L = Ok 0L) ""
    check "canonOrb [] [x] = Some([x], 1)"
          (canonOrb [] [ 5 ] = Some([ 5 ], 1) && canonOrb [] [ 0 ] = Some([ 0 ], 1)) ""

    // ---- the doc's cited cardinalities --------------------------------------
    check "RiemannIdx<4> = OrbIdx<[(2,-),(2,+)],4> folds 4 -> 6 -> 21"
          (cellCountChecked [ (2, OMinus); (2, OPlus) ] 4L = Ok 21L) "21"
    check "depth 3 at n = 4: 1540 cells (vs 65536 dense, 42.6x)"
          (cellCountChecked [ (2, OPlus); (2, OPlus); (2, OPlus) ] 4L = Ok 1540L) "1540"
    check "rank-1 level is a no-op at either sign: [(1,-),(2,+)] at n=4 = 10"
          (cellCountChecked [ (1, OMinus); (2, OPlus) ] 4L = Ok 10L
           && cellCountChecked [ (1, OPlus); (2, OPlus) ] 4L = Ok 10L
           && normalizeLevels [ (1, OPlus); (2, OMinus); (1, OMinus) ] = [ (2, OMinus) ]) ""

    // ---- §7.2: the int64 wall diagnoses, it does not wrap -------------------
    match cellCountChecked [ (2, OPlus); (2, OPlus); (2, OPlus) ] 1000L with
    | Error e -> check "overflow: depth-3 all-'+' at n = 1000 diagnoses" true e
    | Ok v -> check "overflow: depth-3 all-'+' at n = 1000 diagnoses" false ($"wrapped to {v}")
    check "overflow: depth 2 at n = 1000 still fits (125250375250 cells)"
          (cellCountChecked [ (2, OPlus); (2, OPlus) ] 1000L = Ok 125250375250L) ""
    check "malformed class: a level with r < 1 is an Error, not a wrong count"
          (match cellCountChecked [ (0, OPlus) ] 4L with Error _ -> true | Ok _ -> false) ""
    check "negative extent is an Error"
          (match cellCountChecked [ (2, OPlus) ] -1L with Error _ -> true | Ok _ -> false) ""

    // ---- §5: canonicalization, character, zero set --------------------------
    check "canonOrb [(2,-)]: sorts with the sign, kills the diagonal"
          (canonOrb [ (2, OMinus) ] [ 1; 0 ] = Some([ 0; 1 ], -1)
           && canonOrb [ (2, OMinus) ] [ 0; 1 ] = Some([ 0; 1 ], 1)
           && canonOrb [ (2, OMinus) ] [ 2; 2 ] = None) ""
    check "canonOrb [(2,+)]: sorts, character stays +1, no zero set"
          (canonOrb [ (2, OPlus) ] [ 1; 0 ] = Some([ 0; 1 ], 1)
           && canonOrb [ (2, OPlus) ] [ 2; 2 ] = Some([ 2; 2 ], 1)) ""
    check "canonOrb Riemann shape: inner '-' sign survives the outer '+' sort"
          (canonOrb [ (2, OMinus); (2, OPlus) ] [ 1; 0; 2; 3 ] = Some([ 0; 1; 2; 3 ], -1)
           && canonOrb [ (2, OMinus); (2, OPlus) ] [ 2; 3; 1; 0 ] = Some([ 0; 1; 2; 3 ], -1)
           && canonOrb [ (2, OMinus); (2, OPlus) ] [ 1; 0; 3; 2 ] = Some([ 0; 1; 2; 3 ], 1)
           && canonOrb [ (2, OMinus); (2, OPlus) ] [ 0; 0; 1; 2 ] = None) ""
    check "canonOrb outer '-': equal sub-blocks are the zero set"
          (canonOrb [ (2, OPlus); (2, OMinus) ] [ 0; 1; 1; 0 ] = None
           && canonOrb [ (2, OPlus); (2, OMinus) ] [ 0; 1; 0; 2 ] = Some([ 0; 1; 0; 2 ], 1)
           && canonOrb [ (2, OPlus); (2, OMinus) ] [ 0; 2; 0; 1 ] = Some([ 0; 1; 0; 2 ], -1)) ""

    // ---- §2 depth-2: the general recursion vs the hand-unrolled E/B/A nest --
    let mutable nestOk = true
    let mutable nestBad = ""
    let mutable nestCells = 0
    for sInner in [ OPlus; OMinus ] do
        for sOuter in [ OPlus; OMinus ] do
            for n in [ 3; 4; 5 ] do
                let got = visitStream [ (2, sInner); (2, sOuter) ] n |> List.ofSeq
                let want = segmentedNestDepth2 sInner sOuter n
                nestCells <- nestCells + got.Length
                if got <> want then
                    nestOk <- false
                    nestBad <- $"{(showLevels [ (2, sInner); (2, sOuter) ])} n={n}: {got.Length} vs {want.Length} cells"
    check "traversal: keysFrom = the hand-unrolled segmentedNestDepth2 (4 sign combos, n = 3,4,5)"
          nestOk (if nestOk then $"{nestCells} cells, exact stream match" else nestBad)

    // ---- the sweep: stream / count / rank / unrank / successor vs brute -----
    check "sweep menu = closure(d<=2, r<=3) + exact(d=3, r=2)"
          (List.length sweepClasses = 51) ($"{(List.length sweepClasses)} classes")
    let mutable sweptCells = 0
    for lv in sweepClasses do
        for n in [ 3; 4 ] do
            let nm = $"{(showLevels lv)} n={n}"
            let stream = visitStream lv n |> List.ofSeq
            let truth = bruteCanonical lv n
            sweptCells <- sweptCells + stream.Length
            check ($"stream {nm} = brute canonical set, in order")
                  (stream = truth)
                  ($"{stream.Length} cells")
            check ($"cellCount {nm} = stream length")
                  (cellCountChecked lv (int64 n) = Ok(int64 stream.Length))
                  (string stream.Length)
            // rank(stream[i]) = i and unrank(i) = stream[i], for every cell.
            let arr = List.toArray stream
            let mutable rankBad = ""
            let mutable rankOk = true
            for i in 0 .. arr.Length - 1 do
                if rankOk then
                    match orbRank lv n arr.[i] with
                    | Ok r when r = int64 i -> ()
                    | other -> rankOk <- false; rankBad <- sprintf "rank %A = %A, want %d" arr.[i] other i
                if rankOk then
                    match orbUnrank lv n (int64 i) with
                    | Ok t when t = arr.[i] -> ()
                    | other -> rankOk <- false; rankBad <- sprintf "unrank %d = %A, want %A" i other arr.[i]
            check ($"rank/unrank {nm}: rank(stream[i]) = i and unrank inverts it")
                  rankOk (if rankOk then $"{arr.Length} cells" else rankBad)
            // the successor chain from the first cell IS the stream.
            let chain =
                if arr.Length = 0 then []
                else
                    let acc = ResizeArray<int list>()
                    let mutable cur = Some arr.[0]
                    let mutable guard = 0
                    while cur.IsSome && guard <= arr.Length do
                        acc.Add cur.Value
                        cur <- orbSuccessor lv n cur.Value
                        guard <- guard + 1
                    List.ofSeq acc
            check ($"successor chain {nm} = the stream, None at the last cell")
                  (chain = stream) ($"{(max 0 (chain.Length - 1))} steps")

    // ---- larger extents: stream vs arithmetic, no brute force ---------------
    // Brute force is n^rank; these classes are past its reach, so the pin is
    // the internal one — the stream's length is the §4 count and every position
    // round-trips through the arithmetic pair.
    let mutable bigOk = true
    let mutable bigBad = ""
    let mutable bigCells = 0
    for (lv, n) in [ [ (2, OPlus) ], 40
                     [ (3, OMinus) ], 12
                     [ (2, OPlus); (2, OPlus) ], 7
                     [ (2, OMinus); (2, OPlus) ], 9
                     [ (2, OPlus); (2, OMinus) ], 6
                     [ (2, OPlus); (2, OPlus); (2, OPlus) ], 5 ] do
        let stream = visitStream lv n |> List.ofSeq
        bigCells <- bigCells + stream.Length
        if cellCountChecked lv (int64 n) <> Ok(int64 stream.Length) then
            bigOk <- false
            bigBad <- sprintf "%s n=%d: count %A vs %d cells" (showLevels lv) n
                              (cellCountChecked lv (int64 n)) stream.Length
        stream |> List.iteri (fun i t ->
            if bigOk then
                if orbRank lv n t <> Ok(int64 i) then
                    bigOk <- false
                    bigBad <- sprintf "%s n=%d: rank %A = %A, want %d" (showLevels lv) n t (orbRank lv n t) i
                elif orbUnrank lv n (int64 i) <> Ok t then
                    bigOk <- false
                    bigBad <- sprintf "%s n=%d: unrank %d = %A, want %A" (showLevels lv) n i
                                      (orbUnrank lv n (int64 i)) t)
    check "larger extents: count = stream length and rank/unrank invert, 6 classes"
          bigOk (if bigOk then $"{bigCells} cells" else bigBad)

    // ---- Phase 0 anchor: the depth-1 ranks ARE the triangular offsets -------
    // Written out longhand, the way the existing SymIdx/AntisymIdx storage
    // computes them — an independent formula, not a rearrangement of the
    // combinadic under test.
    let mutable triOk = true
    let mutable triBad = ""
    for n in [ 5; 7; 12 ] do
        for t in visitStream [ (2, OPlus) ] n do
            match t with
            | [ i; j ] ->
                let want = int64 (i * n - i * (i - 1) / 2 + (j - i))
                if orbRank [ (2, OPlus) ] n t <> Ok want then
                    triOk <- false
                    triBad <- sprintf "sym n=%d %A: %A vs %d" n t (orbRank [ (2, OPlus) ] n t) want
            | _ -> triOk <- false
        for t in visitStream [ (2, OMinus) ] n do
            match t with
            | [ i; j ] ->
                let want = int64 (i * n - i * (i + 1) / 2 + (j - i - 1))
                if orbRank [ (2, OMinus) ] n t <> Ok want then
                    triOk <- false
                    triBad <- sprintf "antisym n=%d %A: %A vs %d" n t (orbRank [ (2, OMinus) ] n t) want
            | _ -> triOk <- false
    check "depth-1 anchor: rank [(2,+)]/[(2,-)] = the packed triangular offsets (n = 5,7,12)"
          triOk triBad

    // ---- negative controls ---------------------------------------------------
    let isError r = match r with Error _ -> true | Ok _ -> false
    check "orbRank refuses a non-canonical tuple"
          (isError (orbRank [ (2, OPlus) ] 4 [ 1; 0 ])
           && isError (orbRank [ (2, OMinus) ] 4 [ 2; 2 ])
           && isError (orbRank [ (2, OMinus); (2, OPlus) ] 4 [ 2; 3; 0; 1 ])) ""
    check "orbRank refuses an out-of-range coordinate and a malformed shape"
          (isError (orbRank [ (2, OPlus) ] 4 [ 0; 4 ])
           && isError (orbRank [ (2, OPlus) ] 4 [ 0; 1; 2 ])
           && isError (orbRank [] 4 [ 0; 1 ])) ""
    // ---- the rank DOMAIN, exhaustively: the box sweep -----------------------
    // Every tuple in {-1..n}^axes either is a stream cell (and ranks to its
    // index) or is refused. No hand-picked probes: the box contains every
    // negative, every == n off-by-one, every non-canonical ordering -- in
    // particular every ORDERED perturbation where only the bounds check
    // stands between the tuple and a plausible neighbouring offset, the gap
    // the C++ orb_rank bug hid in. Budgeted at 100k probes per class, which
    // at n = 3 covers every closure class up to axes = 6; the C++ harness
    // runs the same sweep over its whole menu to axes = 9
    // (orb_wreath_tests.cpp, "rank domain").
    let mutable boxOk = true
    let mutable boxBad = ""
    let mutable boxProbes = 0L
    let mutable boxClasses = 0
    for lv in sweepClasses do
        let axes = axisRank lv
        let n = 3
        let box = pown (int64 (n + 2)) axes
        if box <= 100_000L then
            boxClasses <- boxClasses + 1
            let stream = visitStream lv n |> List.ofSeq
            let index = System.Collections.Generic.Dictionary<int list, int64>()
            stream |> List.iteri (fun i t -> index.[t] <- int64 i)
            let d = Array.zeroCreate axes
            for e in 0L .. box - 1L do
                if boxOk then
                    let mutable q = e
                    for j in axes - 1 .. -1 .. 0 do
                        d.[j] <- int (q % int64 (n + 2)) - 1
                        q <- q / int64 (n + 2)
                    let t = List.ofArray d
                    boxProbes <- boxProbes + 1L
                    let got = orbRank lv n t
                    let ok =
                        match index.TryGetValue t with
                        | true, i -> got = Ok i
                        | false, _ -> (match got with Error _ -> true | Ok _ -> false)
                    if not ok then
                        boxOk <- false
                        boxBad <- sprintf "%s n=%d: rank %A = %A" (showLevels lv) n t got
    check "rank domain box {-1..n}^axes: every tuple is a stream cell (rank = index) or refused"
          boxOk
          (if boxOk then $"{boxProbes} probes over {boxClasses} classes (box <= 100k)" else boxBad)
    check "orbUnrank refuses a rank outside [0, M)"
          (isError (orbUnrank [ (2, OPlus) ] 4 10L)
           && isError (orbUnrank [ (2, OPlus) ] 4 -1L)
           && isError (orbUnrank [ (2, OMinus); (2, OPlus) ] 4 21L)
           && orbUnrank [ (2, OMinus); (2, OPlus) ] 4 20L = Ok [ 2; 3; 2; 3 ]) ""
    check "orbSuccessor is None exactly at the last cell"
          (orbSuccessor [ (2, OPlus) ] 4 [ 3; 3 ] = None
           && orbSuccessor [ (2, OPlus) ] 4 [ 2; 3 ] = Some [ 3; 3 ]
           && orbSuccessor [ (3, OMinus) ] 4 [ 1; 2; 3 ] = None
           && orbSuccessor [ (2, OMinus); (2, OPlus) ] 4 [ 2; 3; 2; 3 ] = None) ""
    check "empty class: stream is 0..n-1, rank is the identity"
          (visitStream [] 5 |> List.ofSeq = [ [ 0 ]; [ 1 ]; [ 2 ]; [ 3 ]; [ 4 ] ]
           && orbRank [] 5 [ 3 ] = Ok 3L
           && orbUnrank [] 5 3L = Ok [ 3 ]
           && orbSuccessor [] 5 [ 4 ] = None) ""
    check "empty extent: no cells anywhere"
          (visitStream [ (2, OPlus) ] 0 |> Seq.isEmpty
           && visitStream [ (2, OMinus) ] 1 |> Seq.isEmpty
           && cellCountChecked [ (2, OMinus) ] 1L = Ok 0L) ""

    // ---- one verdict for a malformed class at every door --------------------
    check "validateLevels: all five entry points consume the same gate"
          ((match validateLevels [ (0, OPlus) ] with Error _ -> true | Ok _ -> false)
           && validateLevels [] = Ok()
           && validateLevels [ (2, OMinus); (2, OPlus) ] = Ok()
           && isError (cellCountChecked [ (0, OPlus) ] 4L)
           && isError (orbRank [ (0, OPlus) ] 4 [ 0 ])
           && isError (orbUnrank [ (0, OPlus) ] 4 0L)
           && orbSuccessor [ (0, OPlus) ] 4 [ 0 ] = None
           && (match visitStreamChecked [ (0, OPlus) ] 4 with Error _ -> true | Ok _ -> false)) ""
    check "visitStreamChecked: Ok stream = visitStream; negative extent is an Error"
          ((match visitStreamChecked [ (2, OMinus); (2, OPlus) ] 4 with
            | Ok s -> List.ofSeq s = (visitStream [ (2, OMinus); (2, OPlus) ] 4 |> List.ofSeq)
            | Error _ -> false)
           && (match visitStreamChecked [ (2, OPlus) ] -1 with Error _ -> true | Ok _ -> false)) ""

    // ---- overflow pins on the rank arithmetic itself ------------------------
    // The §7.2 wall was previously exercised only through cellCountChecked;
    // these pin binomChecked at the exact int64 edge and drive rank/unrank
    // THROUGH the near-wall arithmetic (adversarial-review hardening,
    // 2026-08-01). C(66,33) is the largest central binomial under int64.
    check "binomChecked at the int64 edge: C(66,33) exact, C(67,33) diagnoses"
          (binomChecked 66L 33 = Ok 7219428434016265740L
           && isError (binomChecked 67L 33)) ""
    let d3 = [ (2, OPlus); (2, OPlus); (2, OPlus) ]
    let m360 = 2228651736717934395L
    check "depth-3 n=360 fits exactly under the wall: 2228651736717934395 cells"
          (cellCountChecked d3 360L = Ok m360) ""
    check "depth-3 n=360: unrank(M-1) is the maximal tuple and round-trips through rank"
          (orbUnrank d3 360 (m360 - 1L) = Ok(List.replicate 8 359)
           && orbRank d3 360 (List.replicate 8 359) = Ok(m360 - 1L)) ""
    check "depth-3 n=1000: rank and unrank diagnose the wall too, not only cellCount"
          (isError (orbRank d3 1000 (List.replicate 8 0))
           && isError (orbUnrank d3 1000 0L)) ""

    // ---- the group-character oracle: canon(g·t) = chi(g)·canon(t) -----------
    let mutable chiOk = true
    let mutable chiBad = ""
    let mutable chiPairs = 0
    let runChi (lv: Level list) (n: int) (tuples: int list seq) =
        let g = buildSignedWreath lv
        // The construction validates itself first: |G| must be the closed
        // form prod_i |G_{i-1}|^{r_i} * r_i! before anything is swept.
        let expect = lv |> List.fold (fun acc (r, _) -> pown acc r * List.fold (*) 1 [ 1 .. r ]) 1
        if g.Length <> expect then
            chiOk <- false
            chiBad <- sprintf "%s: built |G| = %d, closed form %d" (showLevels lv) g.Length expect
        for t in tuples do
            if chiOk then
                let ta = List.toArray t
                let c0 = canonOrb lv t
                for (perm, chi) in g do
                    if chiOk then
                        let u = List.init ta.Length (fun i -> ta.[perm.[i]])
                        let c1 = canonOrb lv u
                        chiPairs <- chiPairs + 1
                        let good =
                            match c0, c1 with
                            | None, None -> true
                            | Some(k0, s0), Some(k1, s1) -> k1 = k0 && s1 = chi * s0
                            | _ -> false
                        if not good then
                            chiOk <- false
                            chiBad <- sprintf "%s n=%d: t=%A g·t=%A chi=%d: canon(t)=%A, canon(g·t)=%A"
                                              (showLevels lv) n t u chi c0 c1
    for (lv, n) in [ [ (2, OPlus) ], 4
                     [ (2, OMinus) ], 4
                     [ (3, OPlus) ], 3
                     [ (3, OMinus) ], 3
                     [ (2, OMinus); (2, OPlus) ], 4          // Riemann shape
                     [ (2, OPlus); (2, OMinus) ], 3
                     [ (2, OPlus); (2, OPlus); (2, OPlus) ], 2
                     [ (2, OMinus); (2, OPlus); (2, OMinus) ], 2 ] do
        runChi lv n (allRawTuples lv n)
    // Depth-3 mixed signs at a real extent: the raw cube (3^8 tuples x |G| =
    // 128) is out of a unit run's budget in F#, so the action is checked on
    // the full canonical stream instead. The zero set at depth 3 is covered
    // by the raw n=2 sweeps above and by the C++ harness's full-menu raw
    // sweep at n=3 (orb_wreath_tests.cpp section (g)).
    runChi [ (2, OMinus); (2, OPlus); (2, OMinus) ] 3
           (visitStream [ (2, OMinus); (2, OPlus); (2, OMinus) ] 3)
    check "chi-oracle: canon(g.t) = chi(g)*canon(t), full wreath group, 9 sweeps (incl. depth-3 mixed signs)"
          chiOk (if chiOk then sprintf "%d (g,t) pairs, 0 violations" chiPairs else chiBad)

    // -------------------------------------------------------------------------
    // The storage read/write path (docs/plan-orbidx-decompaction.md §2):
    //
    //     dense[t] = chi(t) * pool[orbRank(canon(t))],  0 on the zero set.
    //
    // The pool is filled pool[i] = i + 1 IN STREAM ORDER, so every cell's value
    // names its own offset: a read served from a neighbouring cell shows up as
    // a wrong NUMBER, not as a plausible one. And the expected value is
    // recomputed here from `canonOrb` plus the cell's position in `visitStream`
    // — never from orbRead's own canon/rank calls — because a read->write
    // roundtrip cancels exactly the pool-shift bug this is looking for (the
    // antisym storage post-mortem). The write side is pinned the same way: fill
    // a zeroed pool one canonical cell at a time and require the result to be
    // the stream-order fill, cell for cell.
    // -------------------------------------------------------------------------
    let mutable rwOk = true
    let mutable rwBad = ""
    let mutable rwCells = 0
    let mutable rwProbes = 0L
    let mutable rwZero = 0
    let mutable rwNeg = 0
    for (lv, n) in [ [], 5                                       // the trivial class
                     [ (2, OPlus) ], 4
                     [ (2, OMinus) ], 5
                     [ (3, OMinus) ], 5
                     [ (3, OPlus) ], 4
                     [ (2, OMinus); (2, OPlus) ], 4              // the Riemann shape
                     [ (2, OPlus); (2, OMinus) ], 4
                     [ (2, OMinus); (2, OMinus); (2, OPlus) ], 3 // depth 3, mixed signs
                     [ (2, OPlus); (2, OPlus); (2, OPlus) ], 3 ] do
        let stream = visitStream lv n |> List.ofSeq
        let pos = Dictionary<int list, int>()
        stream |> List.iteri (fun i t -> pos.[t] <- i)
        let pool = Array.init stream.Length (fun i -> int64 i + 1L)
        rwCells <- rwCells + stream.Length
        // (a) the dense sweep: EVERY raw tuple of [0,n)^axes, not only the
        // canonical ones — the mirrors and the zero set are the whole point.
        let mutable readOk = true
        let mutable readBad = ""
        for t in allRawTuples lv n do
            if readOk then
                rwProbes <- rwProbes + 1L
                let want =
                    match canonOrb lv t with
                    | None ->
                        rwZero <- rwZero + 1
                        Ok 0L
                    | Some (c, chi) ->
                        if chi < 0 then rwNeg <- rwNeg + 1
                        Ok(int64 chi * (int64 pos.[c] + 1L))
                let got = orbRead lv n pool t
                if got <> want then
                    readOk <- false
                    readBad <- sprintf "read %A = %A, want %A" t got want
        // (b) the write round trip, on every canonical cell.
        let wpool = Array.zeroCreate<int64> stream.Length
        let mutable writeOk = true
        let mutable writeBad = ""
        for i in 0 .. stream.Length - 1 do
            if writeOk then
                match orbWriteCanonical lv n wpool stream.[i] (int64 i + 1L) with
                | Ok() -> ()
                | Error e -> writeOk <- false; writeBad <- sprintf "write %A: %s" stream.[i] e
        if writeOk && wpool <> pool then
            writeOk <- false
            writeBad <- "the written pool differs from the stream-order fill"
        if rwOk && not (readOk && writeOk) then
            rwOk <- false
            rwBad <- sprintf "%s n=%d: %s%s" (showLevels lv) n readBad writeBad
    check "storage: orbRead = chi*pool[rank] over every raw tuple, orbWriteCanonical round-trips (9 classes)"
          rwOk
          (if rwOk then
               sprintf "%d raw probes, %d cells, %d zero-set, %d mirrored-negative"
                       rwProbes rwCells rwZero rwNeg
           else rwBad)

    // Hand pins, so the sweep's oracle is not the only witness. [(2,-),(2,+)]
    // at n = 4, pool[i] = i+1: the inner '-' keys are [0;1]->0 ... [2;3]->5, so
    // [0;1;2;3] is the outer weak pair (0,5) at rank 0*6 + 5 = 5 and its cell
    // holds 6. Each single mirror negates it; the double mirror restores it;
    // the block swap is in the '+' level and changes nothing.
    let poolR21 = Array.init 21 (fun i -> int64 i + 1L)
    let lvR = [ (2, OMinus); (2, OPlus) ]
    check "storage: the signed reads of one Riemann orbit, by hand"
          (orbRead lvR 4 poolR21 [ 0; 1; 2; 3 ] = Ok 6L
           && orbRead lvR 4 poolR21 [ 1; 0; 2; 3 ] = Ok(-6L)
           && orbRead lvR 4 poolR21 [ 0; 1; 3; 2 ] = Ok(-6L)
           && orbRead lvR 4 poolR21 [ 1; 0; 3; 2 ] = Ok 6L
           && orbRead lvR 4 poolR21 [ 2; 3; 0; 1 ] = Ok 6L
           && orbRead lvR 4 poolR21 [ 0; 1; 0; 1 ] = Ok 1L      // first cell
           && orbRead lvR 4 poolR21 [ 0; 0; 1; 2 ] = Ok 0L      // zero set
           && orbRead lvR 4 poolR21 [ 3; 3; 0; 1 ] = Ok 0L) ""
    let poolA10 = Array.init 10 (fun i -> int64 i + 1L)
    check "storage: [(3,-)] at n=5 reads the S_3 orbit with the alternating character"
          (orbRead [ (3, OMinus) ] 5 poolA10 [ 0; 1; 2 ] = Ok 1L
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 1; 2; 0 ] = Ok 1L        // 3-cycle: even
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 2; 0; 1 ] = Ok 1L
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 0; 2; 1 ] = Ok(-1L)      // transposition
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 1; 0; 2 ] = Ok(-1L)
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 2; 1; 0 ] = Ok(-1L)
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 1; 1; 2 ] = Ok 0L        // repeat: zero set
           && orbRead [ (3, OMinus) ] 5 poolA10 [ 4; 3; 2 ] = Ok(-10L)) "" // last cell, mirrored

    // ---- storage refusals: malformed input is never a silent aliased read ---
    // Each of these canonicalizes perfectly happily into SOME cell — a short
    // tuple into a smaller class's, an out-of-range digit into whatever the
    // combinadic makes of it — which is exactly why the refusal has to be a
    // gate and not a downstream accident.
    check "orbRead refuses digit=n, negative digit, wrong length, wrong pool size, bad class, bad extent"
          (isError (orbRead lvR 4 poolR21 [ 0; 1; 2; 4 ])
           && isError (orbRead lvR 4 poolR21 [ 0; 1; 2; -1 ])
           && isError (orbRead lvR 4 poolR21 [ 0; 1; 2 ])
           && isError (orbRead lvR 4 poolR21 [ 0; 1; 2; 3; 0 ])
           && isError (orbRead lvR 4 (Array.zeroCreate 20) [ 0; 1; 2; 3 ])
           && isError (orbRead lvR 4 (Array.zeroCreate 22) [ 0; 1; 2; 3 ])
           && isError (orbRead [ (0, OPlus) ] 4 poolR21 [ 0 ])
           && isError (orbRead lvR -1 [||] [ 0; 1; 2; 3 ])) ""
    // §7.2's wall applies to the read too: the one int64 whose negation leaves
    // int64 diagnoses on a mirrored read instead of wrapping back to itself.
    let poolMin = Array.create 6 System.Int64.MinValue
    check "orbRead: a mirrored read of Int64.MinValue diagnoses, it does not wrap"
          (orbRead [ (2, OMinus) ] 4 poolMin [ 0; 1 ] = Ok System.Int64.MinValue
           && isError (orbRead [ (2, OMinus) ] 4 poolMin [ 1; 0 ])) ""

    let wPool = Array.init 21 (fun i -> int64 i + 1L)
    let wBefore = Array.copy wPool
    let wRefusals =
        [ orbWriteCanonical lvR 4 wPool [ 1; 0; 2; 3 ] 99L                    // mirrored, chi = -1
          orbWriteCanonical lvR 4 wPool [ 0; 0; 1; 2 ] 99L                    // zero set
          orbWriteCanonical lvR 4 wPool [ 0; 1; 2; 4 ] 99L                    // digit = n
          orbWriteCanonical lvR 4 wPool [ 0; 1; 2; -1 ] 99L                   // negative digit
          orbWriteCanonical lvR 4 wPool [ 0; 1; 2 ] 99L                       // wrong length
          orbWriteCanonical lvR 4 (Array.zeroCreate 20) [ 0; 1; 2; 3 ] 99L    // wrong pool size
          orbWriteCanonical [ (0, OPlus) ] 4 wPool [ 0 ] 99L                  // malformed class
          // Non-canonical with character +1 (two mirrors cancel): the gate is a
          // canonical FIXED POINT test, not a sign test, and this row is the
          // one that tells the two apart.
          orbWriteCanonical [ (2, OMinus); (2, OMinus); (2, OPlus) ] 3 (Array.zeroCreate 6)
                            [ 1; 0; 0; 2; 1; 2; 0; 1 ] 99L ]
    check "orbWriteCanonical refuses mirrored / zero-set / out-of-range / malformed writes"
          (wRefusals |> List.forall isError) (sprintf "%d refusals" (List.length wRefusals))
    check "storage: a refused write leaves the pool untouched, and each refusal has its own message"
          (wPool = wBefore
           && (wRefusals
               |> List.map (fun r -> match r with Error e -> e | Ok() -> "!accepted")
               |> List.distinct
               |> List.length) = List.length wRefusals) ""
    // validateLevels is still the ONE structural gate: the two storage doors do
    // not merely also-fail on a malformed class, they fail with the IDENTICAL
    // string the other doors produce (they reach it through cellCountChecked,
    // and pass it through unprefixed for exactly this reason).
    let badClass = [ (0, OPlus) ]
    let gateMsg = match validateLevels badClass with Error e -> e | Ok() -> "!accepted"
    check "storage: the malformed-class verdict is the same string at the storage doors as everywhere else"
          (orbRead badClass 4 [||] [ 0 ] = Error gateMsg
           && orbWriteCanonical badClass 4 [||] [ 0 ] 0L = Error gateMsg
           && cellCountChecked badClass 4L = Error gateMsg
           && orbRank badClass 4 [ 0 ] = Error gateMsg) gateMsg

    printFooter "OrbIdx bijections" [ sprintf "%d passed" passed; sprintf "%d failed" failed
                                      sprintf "%d cells swept" (sweptCells + bigCells + nestCells)
                                      sprintf "%d storage reads" rwProbes ]
    { Block = "OrbIdx Bijections"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
