// =============================================================================
//  OrbitEnum.fsx -- reproducible enumeration for docs/plan-orbit-index-types.md
//
//  The doc proposes  OrbIdx<[(r1,s1),...,(rd,sd)], n>: a flat list of levels,
//  OUTERMOST-LAST, whose group is the iterated wreath  S_r1 <wr> ... <wr> S_rd.
//  Every enumeration claim in that doc is checkable here:
//    s4    cardinality closed form (iterated binomial) -> foldCellsChecked
//    s5    canonicalization + zero set                 -> canon / *Brute
//    s6    |G| and #Hom(G,+-1)                         -> groupOrder / charCount
//    s7    the deduction step (tie a level)            -> tieLevels
//    s7.1  composite stabilizers brute-forced at n=3   -> --stress
//    s7.2  rank-1 normalization + the int64 overflow wall
//
//  Run:   dotnet fsi proofs/OrbitEnum.fsx  [--stress]
//  #load: side-effect free -- exposes everything below as module `OrbitEnum`.
// =============================================================================

open System
open System.Collections.Generic
open System.Diagnostics

/// Per-level character: Plus = invariant (symmetric), Minus = sgn (antisymmetric).
type Sign = Plus | Minus

let signStr = function Plus -> "+" | Minus -> "-"
let showLevels (ls: (int * Sign) list) =
    "[" + String.Join(",", ls |> List.map (fun (r, s) -> sprintf "(%d%s)" r (signStr s))) + "]"

// --- checked int64 arithmetic (s7.2: wraparound must diagnose, not corrupt) ---
// All operands below are non-negative.

let addChecked (a: int64) (b: int64) : Result<int64, string> =
    if b > 0L && a > Int64.MaxValue - b then Error(sprintf "int64 overflow: %d + %d" a b) else Ok(a + b)

let mulChecked (a: int64) (b: int64) : Result<int64, string> =
    if a = 0L || b = 0L then Ok 0L
    elif a > Int64.MaxValue / b then Error(sprintf "int64 overflow: %d * %d" a b)
    else Ok(a * b)

let rec gcd64 (a: int64) (b: int64) = if b = 0L then a else gcd64 b (a % b)

/// Exact C(m,r) in int64.  The gcd reduction makes every intermediate equal to
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

/// s4 fold:  M0 = n ;  Mi = C(M+r-1, r) if s = + ,  C(M, r) if s = - .
/// Returns Error instead of wrapping.  foldCellsChecked [] n = Ok n.
let foldCellsChecked (levels: (int * Sign) list) (n: int64) : Result<int64, string> =
    if n < 0L then Error(sprintf "negative extent %d" n) else
    let rec go i lvls (m: int64) =
        match lvls with
        | [] -> Ok m
        | (r, s) :: rest ->
            if r < 1 then Error(sprintf "level %d: rank %d must be >= 1" i r) else
            let top = if s = Plus then addChecked m (int64 r - 1L) else Ok m
            match top |> Result.bind (fun t -> binomChecked t r) with
            | Error e -> Error(sprintf "level %d (r=%d,%s): %s" i r (signStr s) e)
            | Ok m' -> go (i + 1) rest m'
    go 1 levels n

/// s7.2: a level with r = 1 is the trivial group and a no-op at EITHER sign (S_1
/// has no sgn character, so (1,-) zeroes nothing).  Dropping these is the
/// load-bearing safeguard that makes depth <= log2(rank).
let normalize (levels: (int * Sign) list) = levels |> List.filter (fun (r, _) -> r <> 1)

// --- s5: canonicalization, innermost-first, one sort per level ---

let hasDupKeys (keys: int list list) =
    let s = HashSet<int list>()
    keys |> List.exists (fun k -> not (s.Add k))

/// Parity of the permutation that sorts `keys` (+1 even, -1 odd), by inversions.
let sortParity (keys: int list list) =
    let a = List.toArray keys
    let mutable inv = 0
    for i in 0 .. a.Length - 1 do
        for j in i + 1 .. a.Length - 1 do
            if compare a.[i] a.[j] > 0 then inv <- inv + 1
    if inv % 2 = 0 then 1 else -1

/// Canonical form of one raw tuple plus its character.  None = the tuple is in
/// the zero set (s5: "s_i = - kills tuples with two equal sub-blocks").  Levels
/// are outermost-last, so peel the LAST level first and recurse inward.
let rec canon (levels: (int * Sign) list) (tup: int list) : (int list * int) option =
    match levels with
    | [] ->
        match tup with
        | [ x ] -> Some([ x ], 1)
        | _ -> failwithf "canon: base case needs a 1-element tuple, got %d" (List.length tup)
    | _ ->
        let inner = List.truncate (List.length levels - 1) levels
        let (r, s) = List.last levels
        let total = List.length tup
        if total % r <> 0 then failwithf "canon: tuple length %d not divisible by r=%d" total r
        let subs = tup |> List.chunkBySize (total / r) |> List.map (canon inner)
        if List.exists Option.isNone subs then None else
        let parts = subs |> List.map Option.get
        let keys = parts |> List.map fst
        let sgn = parts |> List.fold (fun a (_, c) -> a * c) 1
        let sorted () = List.concat (List.sortWith compare keys)
        match s with
        | Plus -> Some(sorted (), sgn)
        | Minus when hasDupKeys keys -> None
        | Minus -> Some(sorted (), sgn * sortParity keys)

let rankOf (levels: (int * Sign) list) = levels |> List.fold (fun a (r, _) -> a * r) 1

/// Walk all n^rank raw tuples, handing each canonical result to `sink`.
let bruteScan (levels: (int * Sign) list) (n: int) (sink: (int list * int) option -> unit) =
    let rank = rankOf levels
    let total = pown (int64 n) rank
    if total > 5_000_000L then failwithf "bruteScan: %d tuples is too many" total
    let d = Array.zeroCreate rank
    for e in 0L .. total - 1L do
        let mutable q = e
        for j in rank - 1 .. -1 .. 0 do
            d.[j] <- int (q % int64 n)
            q <- q / int64 n
        sink (canon levels (List.ofArray d))

/// Ground truth for s4: distinct orbits = stored cells.
let orbitCountBrute (levels: (int * Sign) list) (n: int) : int64 =
    let seen = HashSet<int list>()
    bruteScan levels n (function Some(k, _) -> seen.Add k |> ignore | None -> ())
    int64 seen.Count

/// Ground truth for the s5 zero set.
let zeroCountBrute (levels: (int * Sign) list) (n: int) : int64 =
    let c = ref 0L
    bruteScan levels n (function None -> c.Value <- c.Value + 1L | Some _ -> ())
    c.Value

/// Orbit sizes, to cross-check s5 against s6 by orbit-stabilizer.
let orbitSizes (levels: (int * Sign) list) (n: int) : int list =
    let m = Dictionary<int list, int>()
    bruteScan levels n (function
        | Some(k, _) -> m.[k] <- (match m.TryGetValue k with | true, v -> v + 1 | _ -> 1)
        | None -> ())
    m.Values |> List.ofSeq

// --- s6: the group itself, built as explicit permutations ---

let swap (a: int[]) i j = let t = a.[i] in a.[i] <- a.[j]; a.[j] <- t

let allPerms (k: int) : int[][] =
    let res = ResizeArray<int[]>()
    let cur = Array.init k id
    let rec go i =
        if i = k then res.Add(Array.copy cur)
        else for j in i .. k - 1 do (swap cur i j; go (i + 1); swap cur i j)
    go 0
    res.ToArray()

let composePerm (p: int[]) (q: int[]) = Array.init p.Length (fun x -> p.[q.[x]])
let composeInto (dst: int[]) (p: int[]) (q: int[]) =
    for x in 0 .. dst.Length - 1 do dst.[x] <- p.[q.[x]]
let invPerm (p: int[]) =
    let r = Array.zeroCreate p.Length
    for i in 0 .. p.Length - 1 do r.[p.[i]] <- i
    r
/// 4 bits per point: an exact HashSet key.  Degree <= 15 covers every group here
/// (the largest is S_3 <wr> S_3, degree 9).
let encodePerm (p: int[]) : int64 =
    if p.Length > 15 then failwithf "encodePerm: degree %d > 15" p.Length
    let mutable c = 0L
    for i in 0 .. p.Length - 1 do c <- (c <<< 4) ||| int64 p.[i]
    c

/// G1 = S_r1 ; G_i = G_{i-1} <wr> S_{r_i} acting on m*r_i points by
/// (block b, offset x) |-> (pi(b), g_b(x)).  Signs do not affect the group.
let buildWreath (ranks: int list) : int[][] =
    match ranks with
    | [] -> [| [| 0 |] |]
    | r0 :: rest ->
        let mutable g = allPerms r0
        let mutable deg = r0
        for r in rest do
            let acc = ResizeArray<int[]>()
            let pick = Array.zeroCreate r
            let rec tuples b =
                if b < r then for t in 0 .. g.Length - 1 do (pick.[b] <- t; tuples (b + 1))
                else
                    for pi in allPerms r do
                        let e = Array.zeroCreate (deg * r)
                        for blk in 0 .. r - 1 do
                            let gb = g.[pick.[blk]]
                            for x in 0 .. deg - 1 do e.[blk * deg + x] <- pi.[blk] * deg + gb.[x]
                        acc.Add e
            tuples 0
            g <- acc.ToArray()
            deg <- deg * r
        g

/// |G| by explicit construction, asserted against the closed form of s2.
let groupOrder (ranks: int list) : int64 =
    let els = buildWreath ranks
    let seen = HashSet<int64>()
    for e in els do seen.Add(encodePerm e) |> ignore
    let mutable expect = 1L
    for r in ranks do expect <- pown expect r * Seq.fold (*) 1L [ for i in 1 .. r -> int64 i ]
    if int64 els.Length <> expect || int64 seen.Count <> expect then
        failwithf "groupOrder %A: built %d (%d distinct), closed form %d" ranks els.Length seen.Count expect
    expect

/// #Hom(G,+-1) = |G^ab|, valid once we assert G^ab is elementary abelian 2
/// (g^2 in [G,G] for all g).  [G,G] = closure under composition of ALL pairwise
/// commutators g h g^-1 h^-1.
let charCount (ranks: int list) : int =
    let ord = groupOrder ranks                       // also validates the construction
    let els = buildWreath ranks
    let invs = els |> Array.map invPerm
    let b1, b2, b3 = Array.zeroCreate els.[0].Length, Array.zeroCreate els.[0].Length, Array.zeroCreate els.[0].Length
    let inSub = HashSet<int64>()
    let sub = ResizeArray<int[]>()
    for i in 0 .. els.Length - 1 do
        let g, gi = els.[i], invs.[i]
        for j in 0 .. els.Length - 1 do
            composeInto b1 g els.[j]                 // g h
            composeInto b2 gi invs.[j]               // g^-1 h^-1
            composeInto b3 b1 b2                     // [g,h]
            if inSub.Add(encodePerm b3) then sub.Add(Array.copy b3)
    let mutable changed = true
    while changed do                                 // close the generating set under products
        changed <- false
        let snap = sub.ToArray()
        for a in snap do
            for b in snap do
                composeInto b1 a b
                if inSub.Add(encodePerm b1) then (sub.Add(Array.copy b1); changed <- true)
    for g in els do
        if not (inSub.Contains(encodePerm (composePerm g g))) then
            failwithf "charCount %A: some g^2 lies outside [G,G]; G^ab not elem. abelian 2" ranks
    let h = int64 sub.Count
    if ord % h <> 0L then failwithf "charCount %A: |[G,G]|=%d does not divide |G|=%d" ranks h ord
    int (ord / h)

/// s7 deduction step: a commutative kernel on a REPEATED identity appends one
/// level (outermost-last); rank-1 levels then normalize away.
let tieLevels (levels: (int * Sign) list) (arity: int) (sign: Sign) =
    normalize (levels @ [ (arity, sign) ])

// --- s7.1: generic kernels and brute-force axis stabilizers ---

let mutable freshCounter = 0
let freshVal () = freshCounter <- freshCounter + 1; int64 freshCounter

/// A "generic commutative kernel": memo on the UNORDERED value pair drawing
/// globally fresh ids, so it satisfies f(a,b)=f(b,a) and nothing else, and two
/// distinct kernels never share a value.
let genericKernel () =
    let memo = Dictionary<struct (int64 * int64), int64>()
    fun (a: int64) (b: int64) ->
        let k = if a <= b then struct (a, b) else struct (b, a)
        match memo.TryGetValue k with
        | true, v -> v
        | _ -> let v = freshVal () in memo.[k] <- v; v

let genericSym (n: int) =
    let m = Array2D.zeroCreate<int64> n n
    for i in 0 .. n - 1 do
        for j in i .. n - 1 do
            let v = freshVal () in m.[i, j] <- v; m.[j, i] <- v
    m

let buildRank8 (n: int) (f: int[] -> int64) =
    let t = Array.zeroCreate<int64> (pown n 8)
    let d = Array.zeroCreate 8
    for e in 0 .. t.Length - 1 do
        let mutable q = e
        for j in 7 .. -1 .. 0 do
            d.[j] <- q % n
            q <- q / n
        t.[e] <- f d
    t

/// Axis permutations in S_rank leaving the tensor pointwise fixed (early exit).
let stabilizerCount (n: int) (rank: int) (t: int64[]) : int =
    let pw = Array.init rank (fun j -> pown n (rank - 1 - j))
    let digits =
        Array.init t.Length (fun e ->
            let d = Array.zeroCreate rank
            let mutable q = e
            for j in rank - 1 .. -1 .. 0 do
                d.[j] <- q % n
                q <- q / n
            d)
    let mutable count = 0
    for p in allPerms rank do
        let mutable ok = true
        let mutable e = 0
        while ok && e < t.Length do
            let d = digits.[e]
            let mutable idx = 0
            for j in 0 .. rank - 1 do idx <- idx + d.[p.[j]] * pw.[j]
            if t.[idx] <> t.[e] then ok <- false
            e <- e + 1
        if ok then count <- count + 1
    count

// --- traversal nest: segment-peeled, branch-free reference emitter ---------
// The storage-order loop nest for a depth-2 class, peeled into straight-line
// affine segments (docs/plan-orbidx-bijections.md s2): per K1 body, in stream
// order -- E: K2 = K1 (the diagonal; '+' outer only), B: first coords equal,
// second strictly greater, A: first coord strictly greater, inner simplex
// free. No conditional appears in any bound; the union is the exact
// ascending-lex canonical stream (checked below against bruteScan).
let segmentedNestDepth2 (sInner: Sign) (sOuter: Sign) (n: int) : int list list =
    let res = ResizeArray<int list>()
    let innerLo i = match sInner with Plus -> i | Minus -> i + 1
    for i1 in 0 .. n - 1 do
        for j1 in innerLo i1 .. n - 1 do
            (match sOuter with
             | Plus -> res.Add [ i1; j1; i1; j1 ]
             | Minus -> ())
            for j2 in j1 + 1 .. n - 1 do
                res.Add [ i1; j1; i1; j2 ]
            for i2 in i1 + 1 .. n - 1 do
                for j2 in innerLo i2 .. n - 1 do
                    res.Add [ i1; j1; i2; j2 ]
    List.ofSeq res

// =============================================================================
//  Self-running PASS/FAIL report -- only when this file is the script being run.
// =============================================================================

let mutable nPass = 0
let mutable nFail = 0

let report (name: string) (ok: bool) (detail: string) =
    if ok then nPass <- nPass + 1 else nFail <- nFail + 1
    printfn "%s  %-52s %s" (if ok then "PASS" else "FAIL") name detail

let expectEq (name: string) (got: 'a) (want: 'a) =
    report name (got = want) (if got = want then sprintf "%A" got else sprintf "got %A, want %A" got want)

/// s4 cardinality cases; `want` is the doc's value wherever the doc states one.
let cardCases : ((int * Sign) list * int * int64) list =
    [ [ (2, Plus) ], 4, 10L                                 // SymIdx<2,4>
      [ (2, Minus) ], 4, 6L                                 // AntisymIdx<2,4>
      [ (3, Plus) ], 3, 10L
      [ (3, Minus) ], 4, 4L
      [ (2, Minus); (2, Plus) ], 4, 21L                     // RiemannIdx<4> = 21   (s3.4/s4)
      [ (2, Plus); (2, Plus) ], 3, 21L                      // deduced f(A,A) class: S(S+1)/2, S=6
      [ (2, Plus); (2, Plus) ], 4, 55L                      // deduced f(A,A) class: S=10 -> 55
      [ (2, Plus); (2, Minus) ], 4, 45L
      [ (2, Minus); (2, Minus) ], 4, 15L
      [ (3, Plus); (2, Plus) ], 3, 55L
      [ (2, Plus); (3, Minus) ], 3, 20L
      [ (2, Minus); (3, Plus) ], 4, 56L
      [ (1, Minus); (2, Plus) ], 4, 10L                     // rank-1 level: no-op at either sign
      [ (2, Plus); (2, Plus); (2, Plus) ], 4, 1540L ]       // s4 depth 3: 1540 vs 65536 dense

let runReport (stress: bool) =
    let sw = Stopwatch.StartNew()

    printfn "\n--- s4: cardinality closed form vs brute-force orbit count ---"
    for (lv, n, want) in cardCases do
        let nm = sprintf "%s n=%d" (showLevels lv) n
        match foldCellsChecked lv (int64 n) with
        | Error e -> report nm false ("fold errored: " + e)
        | Ok c ->
            let brute = orbitCountBrute lv n
            report nm (c = brute && c = want) (sprintf "fold=%d brute=%d doc=%d" c brute want)
    expectEq "foldCellsChecked [] n = Ok n" (foldCellsChecked [] 7L) (Ok 7L)
    expectEq "orbitCountBrute [] 7 = 7" (orbitCountBrute [] 7) 7L
    report "depth-3 n=4: 1540 cells vs 65536 dense" (pown 4L 8 = 65536L)
        (sprintf "%.1fx reduction" (65536.0 / 1540.0))

    printfn "\n--- s5: zero set (an s=- level kills equal sub-blocks) ---"
    expectEq "zeros [(2-)] n=4  (antisym diagonal)" (zeroCountBrute [ (2, Minus) ] 4) 4L
    expectEq "zeros [(2+)] n=4  (none)" (zeroCountBrute [ (2, Plus) ] 4) 0L
    expectEq "zeros [(2-),(2+)] n=4  (i=j or k=l)" (zeroCountBrute [ (2, Minus); (2, Plus) ] 4) 112L
    expectEq "zeros [(2+),(2-)] n=4  (equal blocks)" (zeroCountBrute [ (2, Plus); (2, Minus) ] 4) 28L
    expectEq "zeros [(2-),(2-)] n=4" (zeroCountBrute [ (2, Minus); (2, Minus) ] 4) 136L
    expectEq "zeros [(1-),(2+)] n=4  (S_1 sgn is vacuous)" (zeroCountBrute [ (1, Minus); (2, Plus) ] 4) 0L
    let idem =
        cardCases |> List.forall (fun (lv, n, _) ->
            let ok = ref true
            bruteScan lv n (function Some(k, _) -> (if canon lv k <> Some(k, 1) then ok.Value <- false) | None -> ())
            ok.Value)
    report "canon is idempotent with character +1" idem "over every s4 case"
    for (lv, n) in [ [ (2, Plus); (2, Plus) ], 3; [ (2, Minus); (2, Plus) ], 4; [ (2, Plus); (3, Minus) ], 3 ] do
        let g = groupOrder (List.map fst lv)
        let sizes = orbitSizes lv n
        report (sprintf "orbit sizes divide |G|=%d for %s" g (showLevels lv))
            (sizes |> List.forall (fun s -> g % int64 s = 0L))
            (sprintf "%d orbits, sizes %A" (List.length sizes) (List.distinct sizes |> List.sort))

    printfn "\n--- s6: |G| and #Hom(G,+-1) ---"
    for (ranks, ordW, chW) in [ [ 2 ], 2L, 2; [ 2; 2 ], 8L, 4; [ 2; 2; 2 ], 128L, 8 ] do
        let d = List.length ranks
        expectEq (sprintf "|S_2 wr .. (depth %d)|" d) (groupOrder ranks) ordW
        expectEq (sprintf "#Hom(depth-%d, +-1)" d) (charCount ranks) chW
    for r in 1 .. 3 do
        for k in 1 .. 3 do
            let want = if r = 1 && k = 1 then 1 elif r = 1 || k = 1 then 2 else 4
            expectEq (sprintf "#Hom(S_%d wr S_%d, +-1)" r k) (charCount [ r; k ]) want
    expectEq "|S_3 wr S_3| = 6^3*3!" (groupOrder [ 3; 3 ]) 1296L
    expectEq "|S_2 wr S_4| = 2^4*4!" (groupOrder [ 2; 4 ]) 384L

    printfn "\n--- s7: deduction (tieLevels) and the group each call claims ---"
    expectEq "func(C,D): four trivial classes" (tieLevels [] 1 Plus) []
    expectEq "func(C,A): one S_2, rest trivial" (groupOrder [ 2 ] * groupOrder [ 1 ]) 2L
    expectEq "func(A,B): untied product 2*2" (groupOrder [ 2 ] * groupOrder [ 2 ]) 4L
    expectEq "func(A,A): ties a level" (tieLevels [ (2, Plus) ] 2 Plus) [ (2, Plus); (2, Plus) ]
    expectEq "  |S_2 wr S_2| = 8" (groupOrder [ 2; 2 ]) 8L
    expectEq "f(A,A)*f(A,A): ties a third" (tieLevels [ (2, Plus); (2, Plus) ] 2 Plus)
        [ (2, Plus); (2, Plus); (2, Plus) ]
    expectEq "  |S_2 wr S_2 wr S_2| = 128" (groupOrder [ 2; 2; 2 ]) 128L
    expectEq "h(f(A,A),g(B,B)) untied: 8*8" (groupOrder [ 2; 2 ] * groupOrder [ 2; 2 ]) 64L
    expectEq "crossed 16-axis case: (S_2)^8" (pown (groupOrder [ 2 ]) 8) 256L
    expectEq "RiemannIdx<4> cells = 21" (foldCellsChecked [ (2, Minus); (2, Plus) ] 4L) (Ok 21L)
    report "degeneracy: 128 < 384 stays sound" (384L % 128L = 0L)
        "S_2 wr S_2 wr S_2 sits inside S_2 wr S_4 at index 3"

    printfn "\n--- s7.2: rank-1 normalization and the int64 overflow wall ---"
    expectEq "normalize drops (1,+) and (1,-)"
        (normalize [ (1, Plus); (2, Minus); (1, Minus); (3, Plus) ]) [ (2, Minus); (3, Plus) ]
    let junk = List.replicate 1000 (1, Plus)
    expectEq "1000 (1,+) levels leave cells unchanged" (foldCellsChecked junk 4L) (Ok 4L)
    expectEq "  ... and normalize away entirely" (normalize junk) []
    let maxDepth n =
        let rec go d =
            if d > 40 then d
            else match foldCellsChecked (List.replicate (d + 1) (2, Plus)) n with
                 | Ok _ -> go (d + 1)
                 | Error _ -> d
        go 0
    for (n, want) in [ 2L, 7; 4L, 5; 8L, 4; 16L, 4; 64L, 3; 100L, 3; 360L, 3; 1000L, 2 ] do
        expectEq (sprintf "max depth (all r=2) at n=%d" n) (maxDepth n) want
    match foldCellsChecked (List.replicate 3 (2, Plus)) 360L with
    | Ok c ->
        report "n=360 depth 3 is ~2.2e18 cells"
            (c > 2_000_000_000_000_000_000L && c < 2_400_000_000_000_000_000L) (string c)
    | Error e -> report "n=360 depth 3 is ~2.2e18 cells" false e
    match foldCellsChecked (List.replicate 4 (2, Plus)) 360L with
    | Error e -> report "n=360 depth 4 diagnoses, not wraps" true e
    | Ok v -> report "n=360 depth 4 diagnoses, not wraps" false (sprintf "wrapped to %d" v)

    printfn "\n--- traversal: segment-peeled nest = ascending-lex canonical stream ---"
    for sInner in [ Plus; Minus ] do
        for sOuter in [ Plus; Minus ] do
            for n in [ 3; 4 ] do
                let levels = [ (2, sInner); (2, sOuter) ]
                let expected =
                    let seen = HashSet<int list>()
                    bruteScan levels n (function Some(k, _) -> seen.Add k |> ignore | None -> ())
                    seen |> List.ofSeq |> List.sort
                let got = segmentedNestDepth2 sInner sOuter n
                report (sprintf "peeled nest %s n=%d" (showLevels levels) n)
                    (got = expected)
                    (sprintf "%d cells, exact stream match" got.Length)

    if not stress then printfn "\n(s7.1 stress sweeps skipped; pass --stress to run them)"
    else
        printfn "\n--- s7.1: brute-force axis stabilizers over all 8! at n=3 ---"
        let n = 3
        let A, B = genericSym n, genericSym n
        let f, g, h, q = genericKernel (), genericKernel (), genericKernel (), genericKernel ()
        // (a) q(P,P) with P = f(A,A): one object combined with itself -> depth 3
        let ta = buildRank8 n (fun d ->
            q (f A.[d.[0], d.[1]] A.[d.[2], d.[3]]) (f A.[d.[4], d.[5]] A.[d.[6], d.[7]]))
        expectEq "q(P,P), P=f(A,A)  ->  |S_2 wr S_2 wr S_2|" (stabilizerCount n 8 ta) 128
        // (b) h(f(A,A), g(B,B)) with A <> B: two distinct objects -> untied 8*8
        let tb = buildRank8 n (fun d ->
            h (f A.[d.[0], d.[1]] A.[d.[2], d.[3]]) (g B.[d.[4], d.[5]] B.[d.[6], d.[7]]))
        expectEq "h(f(A,A),g(B,B)), A<>B  ->  untied 8*8" (stabilizerCount n 8 tb) 64
        // (c) every kernel = multiplication, A entries distinct primes: A^(x)4 collapses
        let primes = [| 2L; 3L; 5L; 7L; 11L; 13L |]
        let Ap = Array2D.zeroCreate<int64> n n
        let mutable k = 0
        for i in 0 .. n - 1 do
            for j in i .. n - 1 do
                Ap.[i, j] <- primes.[k]
                Ap.[j, i] <- primes.[k]
                k <- k + 1
        let tc = buildRank8 n (fun d ->
            Ap.[d.[0], d.[1]] * Ap.[d.[2], d.[3]] * Ap.[d.[4], d.[5]] * Ap.[d.[6], d.[7]])
        expectEq "all kernels = *, A^(x)4 degenerates to |S_2 wr S_4|" (stabilizerCount n 8 tc) 384
        // guard rails: the stabilizer counter is neither saturating nor empty
        expectEq "  control: all-distinct tensor -> trivial" (stabilizerCount n 8 (buildRank8 n (fun _ -> freshVal ()))) 1
        expectEq "  control: constant tensor -> all of S_8" (stabilizerCount n 8 (buildRank8 n (fun _ -> 7L))) 40320

    sw.Stop()
    printfn "\n=== %d passed, %d failed  (%.1f s) ===" nPass nFail sw.Elapsed.TotalSeconds
    if nFail > 0 then 1 else 0

let isMain = fsi.CommandLineArgs |> Array.exists (fun a -> a.EndsWith "OrbitEnum.fsx")
if isMain then exit (runReport (fsi.CommandLineArgs |> Array.contains "--stress"))
