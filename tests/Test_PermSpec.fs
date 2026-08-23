// Pins for the Sₙ permutation-module counting layer (MLPermSpec.fs) — stage
// 5a-i of the retired transforms-as-types plan §3.6 / §7 stage 5. Everything here is
// integer: there is no float, no tolerance and no rank decision anywhere in
// the Sₙ arc, which is what makes this block a stronger gate than the O(3)
// oracle blocks beside it.
//
// Four families of pin.
//
//  1. COUNTS. Bell(m) for m = 0..6 at N ≥ m (with the m = 0 convention pinned
//     explicitly — the empty partition, Bell(0) = 1, which is what makes
//     `perm_bias_dim(0, N) = 1` the invariant-readout case rather than a
//     degenerate zero), the TRUNCATED counts Σ_{j≤N} S(m, j) below it, and the
//     Stirling table itself against hand values.
//  2. THE ORDER CONVENTION 5a-ii will bake: RGS-lex ascending, all-one-block
//     first, all-singletons last, strictly ascending throughout, every string
//     a valid RGS.
//  3. THE INDEPENDENCE CERTIFICATE. MLPermSpec asserts unitriangularity by
//     construction on every call; this block ALSO tests the STRICT half
//     explicitly — for a > b, W[a][b] must be false. That is the numerical
//     shadow of the Coq keystone `rgs_lex_extends_refinement`: if it ever
//     fails, the emission order is not a linear extension of refinement and
//     the convention swaps to the block-count fallback. Failing LOUDLY here is
//     the entire purpose of the test. The witness-evaluation IDENTITY
//     (B_{γ'}(RGS γ) = 1 ⇔ γ coarser than γ') is re-derived independently
//     below by evaluating the indicator on the tuple, rather than trusting
//     `coarsens` to be both things at once.
//  4. A THIRD-ROUTE ENUMERATION. An independently coded recursive
//     block-insertion enumerator (partitions of [m] = for each partition of
//     [m−1], drop m into an existing block or open a new one) — a genuinely
//     different algorithm from the RGS odometer, canonicalized to RGS and
//     compared as SETS. Deliberately NOT ppl's setPartitions: the projects do
//     not reference each other, and an imported enumerator would not be an
//     independent route anyway.
module Blade.Tests.PermSpecReview

open Blade.Tests.TestHarness
open Blade.ML.PermSpec

// ---------------------------------------------------------------------------
// Route 3: recursive block-insertion, the standard textbook recursion.
// A partition is a LIST OF BLOCKS (each block a list of positions); nothing
// about RGS strings, growth restrictions or lex order appears until the
// canonicalization step below.
// ---------------------------------------------------------------------------
let rec private insertionPartitions (m: int) : int list list list =
    if m = 0 then [ [] ]
    else
        [ for p in insertionPartitions (m - 1) do
            // ... drop the new element into each existing block ...
            for bi in 0 .. p.Length - 1 do
                yield p |> List.mapi (fun i b -> if i = bi then b @ [ m - 1 ] else b)
            // ... or open a new block with it.
            yield p @ [ [ m - 1 ] ] ]

/// Canonicalize a block list to its RGS: label blocks by FIRST APPEARANCE.
let private toRgs (m: int) (p: int list list) : int[] =
    let raw = Array.create m -1
    p |> List.iteri (fun bi b -> b |> List.iter (fun pos -> raw.[pos] <- bi))
    let seen = System.Collections.Generic.Dictionary<int, int>()
    let out = Array.zeroCreate m
    for i in 0 .. m - 1 do
        if not (seen.ContainsKey raw.[i]) then seen.[raw.[i]] <- seen.Count
        out.[i] <- seen.[raw.[i]]
    out

/// The orbit indicator B_γ evaluated at a tuple, from its DEFINITION: 1 iff t
/// is constant on every block of γ. Independent of `coarsens` on purpose — the
/// witness identity is then a theorem being tested, not a definition being
/// restated.
let private bIndicator (g: int[]) (t: int[]) : bool =
    let m = g.Length
    let mutable ok = true
    for i in 0 .. m - 1 do
        for j in 0 .. m - 1 do
            if g.[i] = g.[j] && t.[i] <> t.[j] then ok <- false
    ok

/// Is this a legal restricted growth string?
let private isRgs (g: int[]) : bool =
    if g.Length = 0 then true
    elif g.[0] <> 0 then false
    else
        let mutable mx = 0
        let mutable ok = true
        for i in 1 .. g.Length - 1 do
            if g.[i] < 0 || g.[i] > mx + 1 then ok <- false
            mx <- max mx g.[i]
        ok

let private lexLess (a: int[]) (b: int[]) = compare (List.ofArray a) (List.ofArray b) < 0

let private show (g: int[]) = if g.Length = 0 then "<empty>" else g |> Array.map string |> String.concat ""

let runPermSpecTests () : BlockResult =
    printHeader "Perm Spec (Sn orbit basis)"
    let mutable passed = 0
    let mutable failed = 0
    let mutable failedNames : string list = []
    let check name ok detail =
        if ok then
            passed <- passed + 1
            resultLine Pass name detail
        else
            failed <- failed + 1
            failedNames <- failedNames @ [name]
            resultLine Fail name detail

    // ---- 1a. the m = 0 convention, pinned before anything depends on it ----
    // The odometer produces exactly ONE string of length 0 (the empty one),
    // whose block count is 0 — the empty partition of the empty set. The
    // Stirling route agrees only because the reference sum starts at j = 0 and
    // S(0,0) = 1; that single term is the whole convention.
    let p0 = permPartitions 0 6
    check "m=0: one partition (the empty RGS), b = 0 — Bell(0) = 1"
          (p0.Length = 1 && p0.Head.Length = 0 && blockCount p0.Head = 0)
          ($"{p0.Length} partition(s)")
    check "m=0: Stirling route agrees (S(0,0) = 1 is the j = 0 term)"
          (partitionCount 0 6 = 1L && partitionCount 0 0 = 1L && stirling2 0 0 = 1L)
          ($"partitionCount 0 6 = {(partitionCount 0 6)}")
    check "m=0: perm_bias_dim(0, N) = 1 — the L = 0 invariant readout"
          (permBiasDim 0 1 = 1 && permBiasDim 0 5 = 1 && permWeightDim 0 0 3 = 1) ""

    // ---- 1b. Bell pins at N >= m ------------------------------------------
    let bell = [ 1; 1; 2; 5; 15; 52; 203 ]
    let bellGot = [ for m in 0 .. maxPositions -> (permPartitions m maxPositions).Length ]
    check "Bell(m) for m = 0..6 at N >= m: 1 1 2 5 15 52 203"
          (bellGot = bell)
          (bellGot |> List.map string |> String.concat " ")
    // N strictly greater than m must not change anything (no truncation).
    let bellWide = [ for m in 0 .. maxPositions -> (permPartitions m 9).Length ]
    check "N > m adds nothing: the same 7 Bell numbers at N = 9"
          (bellWide = bell) ""

    // ---- 1c. TRUNCATED counts (N < m) — the deferred variant's arithmetic --
    // These are exactly the counts the v1 surface REFUSES to compute silently;
    // the counting layer still has to get them right, because 5a's deferral is
    // "not this convention yet", not "cannot".
    check "truncated m=4, N=2: S(4,1) + S(4,2) = 1 + 7 = 8"
          ((permPartitions 4 2).Length = 8 && stirling2 4 1 = 1L && stirling2 4 2 = 7L)
          (string (permPartitions 4 2).Length)
    check "truncated m=4, N=3: + S(4,3) = 6 -> 14"
          ((permPartitions 4 3).Length = 14 && stirling2 4 3 = 6L)
          (string (permPartitions 4 3).Length)
    check "truncated m=5, N=2: S(5,1) + S(5,2) = 1 + 15 = 16"
          ((permPartitions 5 2).Length = 16 && stirling2 5 2 = 15L)
          (string (permPartitions 5 2).Length)
    check "truncated m=6, N=1: the all-one-block partition alone"
          ((permPartitions 6 1).Length = 1 && (permPartitions 6 1).Head = Array.zeroCreate 6) ""
    // Every truncation is a PREFIX-closed sub-family by block count, and the
    // per-block-count histogram is the Stirling row.
    let histOk =
        [ 0 .. maxPositions ] |> List.forall (fun m ->
            let byB = permPartitions m m |> List.countBy blockCount |> Map.ofList
            [ 1 .. m ] |> List.forall (fun j ->
                int64 (defaultArg (Map.tryFind j byB) 0) = stirling2 m j))
    check "per-block-count histogram = the Stirling row S(m, .), m = 0..6" histOk ""

    // ---- 1d. the Stirling table itself, hand values ------------------------
    check "S(m, j) hand pins: S(4,2)=7 S(5,2)=15 S(5,3)=25 S(6,3)=90 S(6,2)=31"
          (stirling2 4 2 = 7L && stirling2 5 2 = 15L && stirling2 5 3 = 25L
           && stirling2 6 3 = 90L && stirling2 6 2 = 31L) ""
    check "S(m, 1) = S(m, m) = 1 and S(m, j) = 0 for j > m, m <= 6"
          ([ 1 .. 6 ] |> List.forall (fun m ->
               stirling2 m 1 = 1L && stirling2 m m = 1L && stirling2 m (m + 1) = 0L)) ""

    // ---- 2. the order convention 5a-ii bakes ------------------------------
    let orderOk =
        [ 1 .. maxPositions ] |> List.forall (fun m ->
            let ps = permPartitions m m |> List.toArray
            ps.[0] = Array.zeroCreate m
            && ps.[ps.Length - 1] = Array.init m id
            && ps |> Array.forall isRgs
            && Seq.forall (fun i -> lexLess ps.[i] ps.[i + 1]) (seq { 0 .. ps.Length - 2 }))
    check "order: strict RGS-lex ascending, 00..0 first, 012..m-1 last, m = 1..6"
          orderOk ""
    // The exact list at m = 3, spelled out — the smallest case where the
    // convention is visible at a glance and the one a reader will check.
    let p3 = permPartitions 3 3 |> List.map show
    check "m=3 order, spelled out: 000 001 010 011 012"
          (p3 = [ "000"; "001"; "010"; "011"; "012" ])
          (String.concat " " p3)

    // ---- 3a. the STRICT half of unitriangularity (the Coq keystone shadow) -
    // permPartitions already asserts this on every call; re-testing it here as
    // an explicit, named, counting check is deliberate. A violation means
    // RGS-lex is NOT a linear extension of refinement and the whole emission
    // order swaps to the block-count fallback.
    let mutable strictViolations : (int * int * int) list = []
    let mutable diagOk = true
    for m in 0 .. maxPositions do
        let ps = permPartitions m m |> List.toArray
        for a in 0 .. ps.Length - 1 do
            if not (coarsens ps.[a] ps.[a]) then diagOk <- false
            for b in 0 .. a - 1 do
                if coarsens ps.[a] ps.[b] then
                    strictViolations <- (m, a, b) :: strictViolations
    check "unitriangular, STRICT half: W[a][b] = false for every a > b, m = 0..6"
          (List.isEmpty strictViolations)
          (if List.isEmpty strictViolations then "0 violations over 0..6"
           else sprintf "%d VIOLATIONS, e.g. %A" strictViolations.Length (List.head strictViolations))
    check "unitriangular, unit diagonal: coarsens g g for every g, m = 0..6" diagOk ""
    // Antisymmetry: `coarsens` really is a partial order on canonical strings,
    // so the triangularity above cannot be an artifact of a degenerate relation.
    let antisymOk =
        [ 0 .. maxPositions ] |> List.forall (fun m ->
            let ps = permPartitions m m |> List.toArray
            seq { for a in 0 .. ps.Length - 1 do for b in 0 .. ps.Length - 1 -> (a, b) }
            |> Seq.forall (fun (a, b) ->
                not (coarsens ps.[a] ps.[b] && coarsens ps.[b] ps.[a]) || a = b))
    check "coarsens is antisymmetric on RGS strings (a real partial order)" antisymOk ""

    // ---- 3b. the witness-evaluation IDENTITY, re-derived independently -----
    // B_{γ_b}(RGS γ_a) computed from the indicator's definition must equal
    // coarsens γ_a γ_b — the identity the certificate rests on. Also: every
    // witness point is legal, using exactly b(γ) <= N distinct values.
    let identityOk =
        [ 0 .. maxPositions ] |> List.forall (fun m ->
            let ps = permPartitions m m |> List.toArray
            seq { for a in 0 .. ps.Length - 1 do for b in 0 .. ps.Length - 1 -> (a, b) }
            |> Seq.forall (fun (a, b) -> bIndicator ps.[b] ps.[a] = coarsens ps.[a] ps.[b]))
    check "witness identity: B_{g_b}(RGS g_a) = 1 <=> g_a coarser than g_b, m = 0..6"
          identityOk ""
    let witnessLegal =
        [ 0 .. maxPositions ] |> List.forall (fun m ->
            permPartitions m m |> List.forall (fun g ->
                Set.ofArray g = Set.ofList [ 0 .. blockCount g - 1 ] && blockCount g <= m))
    check "witness points are legal: RGS g uses exactly the b(g) <= N values 0..b-1" witnessLegal ""

    // ---- 4. the third-route cross-pin -------------------------------------
    let mutable routeDetail = ""
    let routesAgree =
        [ 0 .. maxPositions ] |> List.forall (fun m ->
            let odo = permPartitions m m |> List.map List.ofArray
            let ins = insertionPartitions m |> List.map (toRgs m >> List.ofArray)
            let odoSet = Set.ofList odo
            let insSet = Set.ofList ins
            // duplicate-free on BOTH routes, and the same set
            let ok = odoSet.Count = odo.Length && insSet.Count = ins.Length && odoSet = insSet
            if m = maxPositions then
                routeDetail <- $"m=6: odometer {odo.Length}, insertion {ins.Length}, set {insSet.Count}"
            ok)
    check "third route (recursive block-insertion) = the RGS odometer as SETS, m = 0..6"
          routesAgree routeDetail
    // The insertion route also re-derives Bell independently of both the
    // odometer and the Stirling recurrence.
    let insCounts = [ for m in 0 .. maxPositions -> (insertionPartitions m).Length ]
    check "third route reproduces Bell(0..6) on its own: 1 1 2 5 15 52 203"
          (insCounts = bell) (insCounts |> List.map string |> String.concat " ")

    // ---- 5. the sizing entry points, with the §3.6 anchors -----------------
    check "perm_weight_dim(1, 1, N) = Bell(2) = 2 — DeepSets (a*x + b*sum(x))"
          (permWeightDim 1 1 2 = 2 && permWeightDim 1 1 8 = 2) ""
    check "perm_weight_dim(2, 2, 5) = Bell(4) = 15 — the Maron k=l=2 count"
          (permWeightDim 2 2 5 = 15) (string (permWeightDim 2 2 5))
    check "perm_bias_dim(2, 5) = Bell(2) = 2 — the Maron k=2 bias count"
          (permBiasDim 2 5 = 2) ""
    check "perm_weight_dim(1, 2, 3) = Bell(3) = 5, perm_weight_dim(3, 3, 6) = Bell(6) = 203"
          (permWeightDim 1 2 3 = 5 && permWeightDim 3 3 6 = 203) ""
    check "perm_weight_dim is symmetric in K and L (m = K + L is all it sees)"
          ([ for k in 0 .. 4 do for l in 0 .. 4 - k -> (k, l) ]
           |> List.forall (fun (k, l) -> permWeightDim k l 6 = permWeightDim l k 6)) ""
    check "perm_bias_dim(L, N) = perm_weight_dim(0, L, N) for L = 0..6"
          ([ 0 .. 6 ] |> List.forall (fun l -> permBiasDim l 6 = permWeightDim 0 l 6)) ""

    // ---- 6. the v1 surface preconditions (no silent fork) ------------------
    let isErr r = match r with Error _ -> true | Ok _ -> false
    let msgOf r = match r with Error (m: string) -> m | Ok _ -> ""
    let rejN = checkPermSizing "perm_weight_dim" "K + L" 4 3
    check "N < K + L is REFUSED (v1 full-basis rule), not silently truncated"
          (isErr rejN) ""
    // The message was reworded in the c85e48c comment cleanup: the retired-plan
    // "§3.6 named deferral" phrasing became "unsupported rather than silently
    // substituted". The load-bearing content is unchanged: the TRUNCATED-BASIS
    // variant is named, and both counts (full 15 vs truncated 14) appear.
    check "the N < K + L diagnostic names the TRUNCATED-BASIS variant and both counts"
          (let m = msgOf rejN
           m.Contains "TRUNCATED-BASIS" && m.Contains "15" && m.Contains "14"
           && m.Contains "unsupported")
          (msgOf rejN)
    check "N = K + L exactly is accepted; N >= 1 is required"
          (checkPermSizing "perm_weight_dim" "K + L" 4 4 = Ok ()
           && isErr (checkPermSizing "perm_weight_dim" "K + L" 0 0)) ""
    let rejCap = checkPermSizing "perm_weight_dim" "K + L" 7 9
    check "K + L > 6 is REFUSED with the cap named"
          (isErr rejCap && (msgOf rejCap).Contains "203") (msgOf rejCap)
    // The internal layer is harder still: above the cap it throws rather than
    // returning a Result, because only a compiler bug can get there.
    let capThrows =
        try permPartitions 7 7 |> ignore; false
        with _ -> true
    check "permPartitions above the cap throws (internal, unreachable from source)" capThrows ""
    check "permWeightDim/permBiasDim themselves compute the truncated basis (the surface, not the layer, is what refuses)"
          (permWeightDim 2 2 2 = 8 && permBiasDim 5 2 = 16) ""

    printFooter "Perm Spec" [ $"{passed} passed"; $"{failed} failed" ]
    { Block = "Perm Spec"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
