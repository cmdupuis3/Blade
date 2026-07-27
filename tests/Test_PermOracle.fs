// The coarsening-indicator COMPLETENESS oracle — stage 5a-ii of
// plan-transforms-as-types §3.6, whose oracle paragraph reads:
//
//   "exact rational Reynolds — P_ref = (1/n!)Σ_σ σ^{⊗(k+l)} and B(BᵀB)⁻¹Bᵀ
//    over ℚ with the closed-form integer Gram ⟨B_γ, B_π⟩ = n^{b(γ∨π)};
//    STRONGER than the O(3) oracle — no tolerance anywhere."
//
// That "no tolerance anywhere" is load-bearing and is honoured literally:
// THERE IS NO `float` IN THIS FILE. Every matrix entry is a normalized
// BigInteger fraction (the local `Rat` below, the PolyOracle pattern), every
// comparison is structural equality of those fractions, and the one number
// that is not exact — the stopwatch — is an int64 of milliseconds printed in
// the footer. A pass here is an algebraic identity, not a numerical one.
//
// ---------------------------------------------------------------------------
// WHAT IS PROVED ELSEWHERE AND WHAT THIS FILE COVERS
// ---------------------------------------------------------------------------
// proofs/BladePartition.v proves the INDEPENDENCE half of §3.6's basis claim
// (the witness-evaluation matrix is unitriangular over the emission order,
// hence invertible over ℤ) and says, in as many words:
//
//   "NOT modelled, deliberately: that the coarsening indicators SPAN the
//    space of S_n-equivariant maps. ... completeness of the orbit basis is
//    the cited half of §6.1(a), exactly as Schur is cited for the O(3)
//    member."
//
// This block is the numerical discharge of exactly that CITED half. The
// invariant subspace of ℝ^{N^m} is defined here with no reference to
// partitions at all — it is the image of the group-average (Reynolds)
// projector P_ref = (1/N!)Σ_{σ∈S_N} M(σ)^{⊗m} — and the pin is that the
// span of the emitted basis IS that image, as an entrywise equality of two
// rational matrices. Independence comes along for free (a projector of trace
// M onto the span of M vectors forces those vectors independent), so the two
// halves meet here.
//
// The correspondence to `derive_perm_linear(K, L, N, ·)` is the §3.6 position
// convention: m = K + L, ℝ^{N^m} = Hom(ℝ^{N^K}, ℝ^{N^L}) with the input axes
// first, and an S_N-equivariant map is exactly an invariant vector of the
// m-fold tensor power. So "weight dim = #partitions of [m] with ≤ N blocks"
// and "basis = coarsening indicators" are the trace pin and the equality pin
// below, respectively.
//
// ---------------------------------------------------------------------------
// THE IDENTIFICATION (stated once, because the ORIENTATION is a real trap)
// ---------------------------------------------------------------------------
// A tuple t ∈ [N]^m has an EQUALITY PATTERN: the partition of the m positions
// by t_i = t_j. Labelled by first appearance that partition IS a restricted
// growth string — `patternRgs` below is literally MLPermSpec's canonical form
// applied to t's values — so tuples and partitions live in one vocabulary and
// no extra bridge is needed. Under it,
//
//     B_γ(t) = 1  ⇔  t is constant on every block of γ
//                 ⇔  every block of γ sits inside a block of pattern(t)
//                 ⇔  MLPermSpec.coarsens (patternRgs t) γ
//
// — pattern(t) is the COARSE argument, γ the FINE one. The prose direction
// ("γ coarsens the tuple's pattern") is the WRONG WAY ROUND and is vacuously
// true whenever the tuple has all-distinct entries; BladePartition.v flags
// the same hazard in its ORIENTATION paragraph and pins m = 2, 3 by
// computation for the same reason. Here the orientation is nailed twice: the
// DEFINITIONAL column ("t constant on γ's blocks", built by a direct loop
// that never calls into MLPermSpec) is pinned equal to the `coarsens` column,
// and getting it backwards fails the Gram closed form immediately.
//
// `coarsens` is API under test; the JOIN is not. γ ∨ π is built here by
// union-find over the m positions from the union of the two equality
// relations — the finest common coarsening, from the definition, with no
// MLPermSpec call — so the Gram closed form n^{b(γ∨π)} is an independent
// prediction and not a restatement.
//
// ---------------------------------------------------------------------------
// WHY THIS BLOCK IS NOT VACUOUSLY GREEN (negative controls, PolyOracle
// discipline: the perturbations were run against the LIVE pin, observed to
// fail loudly, reverted — and then kept as standing assertions so the
// discrimination itself is regression-tested)
// ---------------------------------------------------------------------------
// The equality pin has real content only if a WRONG basis fails it. Two
// perturbations, both retained below as checks that must PASS by failing:
//
//   (a) DROP one coarsening column (the last partition in emission order,
//       i.e. the finest one). The remaining columns still span an invariant
//       subspace, the Gram is still invertible, P_basis is still an honest
//       projector — but of trace exactly M − 1, and it differs from P_ref at
//       specific entries. This is the control that matters most, because it
//       is precisely the failure mode "the emitted basis is independent but
//       INCOMPLETE" that BladePartition.v cannot see.
//
//   (b) ADD a spurious 0/1 column that is not a coarsening indicator: the
//       indicator of the single all-zeros tuple. e_t is invariant only if the
//       S_N-orbit of t is a singleton, which never happens for N ≥ 2, so the
//       column is genuinely outside the invariant subspace: the Gram stays
//       invertible, P_basis gains a dimension (trace M + 1) and differs from
//       P_ref. The closed form breaks in the cheapest possible place — the
//       new diagonal entry is 1, while every legitimate diagonal entry is
//       N^{b(γ)} ≥ N ≥ 2. (The spurious column has no partition to take a
//       join with, so it is that diagonal, not the full N^{b(γ∨π)} sweep,
//       that detects it; the sweep covers the γ-indexed block, which stays
//       correct — another reason the EQUALITY pin, not the Gram pin, is the
//       load-bearing one.)
//
// Both were then wired into the LIVE anchor path in place of the honest
// column list, run, and reverted. Observed, verbatim:
//
//   (a) all seven anchors failed the equality pin. (3,3): "36 of 729 entries
//       differ, first at [5][5]: 0 vs 1/6 (tr 4 vs 5)"; (4,4): "576 of 65536
//       entries differ, first at [27][27]: 0 vs 1/24 (tr 14 vs 15)". Note
//       what did NOT fail: the Gram closed form, which the surviving columns
//       still satisfy exactly. Incompleteness is invisible to the Gram and
//       to BladePartition.v alike — only P_ref sees it.
//   (b) all seven anchors failed the equality pin. (3,3): "9 of 729 entries
//       differ, first at [0][0]: 1 vs 1/3 (tr 6 vs 5)"; (4,4): "16 of 65536
//       entries differ, first at [0][0]: 1 vs 1/4 (tr 16 vs 15)".
//
// Loud, specific, and off by whole units of trace rather than by an epsilon —
// which is the entire dividend of having no tolerance to hide in. The
// standing form of both controls is the last family of checks at each
// negative-control anchor.
//
// ---------------------------------------------------------------------------
// COST
// ---------------------------------------------------------------------------
// Anchors are (N, m) with N^m ≤ 256, so N ≤ 4 (≤ 24 permutations) and M ≤ 15.
// Nothing dense is ever multiplied: the Reynolds matrix is accumulated by
// permuting tuples (N!·N^m increments), its idempotence is checked through
// row-sparse structure (≤ N! nonzeros per row), and B is handled as column
// supports so B(BᵀB)⁻¹Bᵀ costs Σ_γ N^{b(γ)} column touches rather than a
// 256×256×15 rational product.
module Blade.Tests.PermOracleReview

open System.Numerics
open System.Collections.Generic
open Blade.Tests.TestHarness

module PS = Blade.ML.PermSpec

// ---------------------------------------------------------------------------
// Exact rationals — local, because this is an oracle: nothing it measures with
// may be code it is measuring. Always normalized (Den > 0, gcd = 1), so
// structural equality IS value equality and the entrywise pin needs no
// comparison function of its own.
// ---------------------------------------------------------------------------

type private Rat = { Num: bigint; Den: bigint }

[<RequireQualifiedAccess>]
module private Rat =
    let make (n: bigint) (d: bigint) : Rat =
        if d.IsZero then failwith "PermOracle: rational with zero denominator"
        let n, d = if d.Sign < 0 then -n, -d else n, d
        let g = BigInteger.GreatestCommonDivisor(n, d)
        if g.IsOne then { Num = n; Den = d } else { Num = n / g; Den = d / g }
    let zero : Rat = { Num = BigInteger.Zero; Den = BigInteger.One }
    let one : Rat = { Num = BigInteger.One; Den = BigInteger.One }
    let ofBigInt (n: bigint) : Rat = { Num = n; Den = BigInteger.One }
    let ofInt (n: int) : Rat = ofBigInt (bigint n)
    let isZero (a: Rat) = a.Num.IsZero
    let add (a: Rat) (b: Rat) = make (a.Num * b.Den + b.Num * a.Den) (a.Den * b.Den)
    let sub (a: Rat) (b: Rat) = make (a.Num * b.Den - b.Num * a.Den) (a.Den * b.Den)
    let mul (a: Rat) (b: Rat) = make (a.Num * b.Num) (a.Den * b.Den)
    let div (a: Rat) (b: Rat) =
        if b.Num.IsZero then failwith "PermOracle: rational division by zero"
        make (a.Num * b.Den) (a.Den * b.Num)
    let show (a: Rat) = if a.Den.IsOne then string a.Num else sprintf "%O/%O" a.Num a.Den

// ---------------------------------------------------------------------------
// Tuples of [N]^m, coded little-index-major (position 0 most significant), and
// their equality patterns.
// ---------------------------------------------------------------------------

let private decodeTuple (n: int) (m: int) (code: int) : int[] =
    let t = Array.zeroCreate m
    let mutable c = code
    for i in m - 1 .. -1 .. 0 do
        t.[i] <- c % n
        c <- c / n
    t

let private encodeTuple (n: int) (t: int[]) : int =
    let mutable c = 0
    for i in 0 .. t.Length - 1 do c <- c * n + t.[i]
    c

/// The equality pattern of a tuple, labelled by FIRST APPEARANCE — i.e. the
/// canonical RGS of the partition of positions induced by t_i = t_j. This is
/// the identification stated in the header: `patternRgs (RGS γ) = γ`, pinned
/// below for every emitted γ.
let private patternRgs (t: int[]) : int[] =
    let seen = Dictionary<int, int>()
    t
    |> Array.map (fun v ->
        match seen.TryGetValue v with
        | true, k -> k
        | _ ->
            let k = seen.Count
            seen.[v] <- k
            k)

/// γ ∨ π, the finest common coarsening, straight from the definition: the
/// partition generated by the UNION of the two equality relations, computed by
/// union-find over the m positions. Deliberately independent of MLPermSpec —
/// this is what makes ⟨B_γ, B_π⟩ = N^{b(γ∨π)} a prediction.
let private joinRgs (a: int[]) (b: int[]) : int[] =
    let m = a.Length
    let parent = Array.init m id
    let rec find (x: int) : int =
        if parent.[x] = x then x
        else
            let r = find parent.[x]
            parent.[x] <- r
            r
    for i in 0 .. m - 1 do
        for j in i + 1 .. m - 1 do
            if a.[i] = a.[j] || b.[i] = b.[j] then
                let ri = find i
                let rj = find j
                if ri <> rj then parent.[ri] <- rj
    patternRgs (Array.init m find)

/// All N! permutations of [0..N−1] as arrays (N ≤ 4 here).
let rec private permsOf (xs: int list) : int list list =
    match xs with
    | [] -> [ [] ]
    | _ ->
        xs
        |> List.collect (fun x -> permsOf (List.filter (fun y -> y <> x) xs) |> List.map (fun r -> x :: r))

// ---------------------------------------------------------------------------
// SIDE 1 — the reference: the group-average (Reynolds) projector
//
//   P_ref = (1/N!) Σ_{σ ∈ S_N} M(σ)^{⊗m},     M(σ)[i][j] = [i = σ(j)]
//
// so M(σ)^{⊗m} sends the basis tuple S to σ∘S componentwise and
//
//   C[T][S] = #{σ : T = σ∘S},        P_ref = C / N!.
//
// C is accumulated by permuting tuples — no matrix is ever multiplied out —
// and every downstream claim about P_ref is a claim about the integer C:
//   symmetric      C[T][S] = C[S][T]   (σ ↦ σ⁻¹)
//   idempotent     C·C = N!·C          (P² = P scaled by (N!)²)
//   trace          Σ_T C[T][T] = Σ_σ (fix σ)^m = N!·#orbits   (Burnside)
// Checking idempotence in ℤ rather than in ℚ is the same statement with the
// common denominator cleared, and stays exact for the same reason.
// ---------------------------------------------------------------------------

type private RefCase = {
    /// C, the integer orbit-incidence matrix (P_ref · N!)
    C: int[][]
    /// N!
    Order: int
    P: Rat[][]
    Trace: Rat
    Symmetric: bool
    Idempotent: bool
    IdemDetail: string
}

let private buildRef (n: int) (m: int) : RefCase =
    let d = pown n m
    let tuples = Array.init d (decodeTuple n m)
    let perms = permsOf [ 0 .. n - 1 ] |> List.map List.toArray
    let order = List.length perms
    let c = Array.init d (fun _ -> Array.zeroCreate<int> d)
    for sg in perms do
        for s in 0 .. d - 1 do
            let img = tuples.[s] |> Array.map (fun v -> sg.[v])
            let ti = encodeTuple n img
            c.[ti].[s] <- c.[ti].[s] + 1

    // symmetry
    let mutable sym = true
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do
            if c.[i].[j] <> c.[j].[i] then sym <- false

    // idempotence, through the row supports (≤ N! nonzeros per row)
    let rowNz =
        Array.init d (fun i ->
            [| for j in 0 .. d - 1 do
                 if c.[i].[j] <> 0 then yield struct (j, int64 c.[i].[j]) |])
    let mutable idem = true
    let mutable idemDetail = ""
    let acc : int64[] = Array.zeroCreate d
    for i in 0 .. d - 1 do
        System.Array.Clear(acc, 0, d)
        for struct (k, v) in rowNz.[i] do
            for struct (j, w) in rowNz.[k] do
                acc.[j] <- acc.[j] + v * w
        for j in 0 .. d - 1 do
            if acc.[j] <> int64 order * int64 c.[i].[j] then
                if idem then
                    idemDetail <- sprintf "(C·C)[%d][%d] = %d but N!·C = %d" i j acc.[j] (int64 order * int64 c.[i].[j])
                idem <- false

    let p = Array.init d (fun i -> Array.init d (fun j -> Rat.make (bigint c.[i].[j]) (bigint order)))
    let mutable tr = Rat.zero
    for i in 0 .. d - 1 do tr <- Rat.add tr p.[i].[i]
    { C = c; Order = order; P = p; Trace = tr
      Symmetric = sym; Idempotent = idem; IdemDetail = idemDetail }

// ---------------------------------------------------------------------------
// SIDE 2 — the basis projector B(BᵀB)⁻¹Bᵀ over ℚ
//
// B is handed in as COLUMN SUPPORTS (per column, the tuple indices where it is
// 1) — every column here is 0/1, so that loses nothing and makes both the Gram
// and the projector cheap. The same routine serves the honest basis and both
// negative controls, which is the point: a control differs from the real thing
// only in the column list handed to it.
// ---------------------------------------------------------------------------

/// Gauss–Jordan inverse over ℚ. `None` iff singular (which never happens for a
/// Gram matrix of independent columns — a `None` IS a failure signal).
let private invertRat (g: bigint[][]) : Rat[][] option =
    let n = g.Length
    let a =
        Array.init n (fun i ->
            Array.init (2 * n) (fun j ->
                if j < n then Rat.ofBigInt g.[i].[j]
                elif j - n = i then Rat.one
                else Rat.zero))
    let mutable ok = true
    for col in 0 .. n - 1 do
        if ok then
            let mutable piv = -1
            for r in col .. n - 1 do
                if piv < 0 && not (Rat.isZero a.[r].[col]) then piv <- r
            if piv < 0 then ok <- false
            else
                let tmp = a.[col]
                a.[col] <- a.[piv]
                a.[piv] <- tmp
                let pv = a.[col].[col]
                for j in 0 .. 2 * n - 1 do a.[col].[j] <- Rat.div a.[col].[j] pv
                for r in 0 .. n - 1 do
                    if r <> col && not (Rat.isZero a.[r].[col]) then
                        let f = a.[r].[col]
                        for j in 0 .. 2 * n - 1 do
                            a.[r].[j] <- Rat.sub a.[r].[j] (Rat.mul f a.[col].[j])
    if ok then Some (Array.init n (fun i -> Array.sub a.[i] n n)) else None

type private BasisCase = {
    Gram: bigint[][]
    P: Rat[][]
    Trace: Rat
}

let private basisProjector (d: int) (colSupports: int[][]) : BasisCase option =
    let nc = colSupports.Length
    let colsOf = Array.init d (fun _ -> ResizeArray<int>())
    colSupports |> Array.iteri (fun ci sup -> for t in sup do colsOf.[t].Add ci)
    // (BᵀB)[a][b] = #{t : both columns are 1 at t}
    let g = Array.init nc (fun _ -> Array.create nc BigInteger.Zero)
    for t in 0 .. d - 1 do
        let l = colsOf.[t]
        for a in 0 .. l.Count - 1 do
            for b in 0 .. l.Count - 1 do
                g.[l.[a]].[l.[b]] <- g.[l.[a]].[l.[b]] + BigInteger.One
    match invertRat g with
    | None -> None
    | Some gi ->
        // A = B·(BᵀB)⁻¹  then  P = A·Bᵀ, both through the supports.
        let a = Array.init d (fun _ -> Array.create nc Rat.zero)
        for t in 0 .. d - 1 do
            for gm in colsOf.[t] do
                let row = gi.[gm]
                for pi in 0 .. nc - 1 do
                    if not (Rat.isZero row.[pi]) then a.[t].[pi] <- Rat.add a.[t].[pi] row.[pi]
        let p = Array.init d (fun _ -> Array.create d Rat.zero)
        for s in 0 .. d - 1 do
            let arow = a.[s]
            let prow = p.[s]
            for t in 0 .. d - 1 do
                let mutable v = Rat.zero
                for pi in colsOf.[t] do
                    if not (Rat.isZero arow.[pi]) then v <- Rat.add v arow.[pi]
                prow.[t] <- v
        let mutable tr = Rat.zero
        for i in 0 .. d - 1 do tr <- Rat.add tr p.[i].[i]
        Some { Gram = g; P = p; Trace = tr }

/// Entrywise comparison over ℚ — zero tolerance, structural equality of
/// normalized fractions. Returns (#differing entries, a located description of
/// the first difference in row-major order).
let private compareExact (a: Rat[][]) (b: Rat[][]) : int * string =
    let d = a.Length
    let mutable diffs = 0
    let mutable first = ""
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do
            if a.[i].[j] <> b.[i].[j] then
                if diffs = 0 then
                    first <- sprintf "first at [%d][%d]: %s vs %s" i j (Rat.show a.[i].[j]) (Rat.show b.[i].[j])
                diffs <- diffs + 1
    (diffs, first)

// ---------------------------------------------------------------------------

let runPermOracleTests () : BlockResult =
    printHeader "Perm Oracle (coarsening indicators vs the exact Reynolds projector)"
    let sw = System.Diagnostics.Stopwatch.StartNew()
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

    // (N, m) anchors. N^m ≤ 256 throughout; (2,3) and (3,4) are the TRUNCATION
    // anchors, where the ≤ N-block filter actually bites and the basis is a
    // strict subset of the Bell(m) partitions.
    let anchors = [ (2, 2); (3, 2); (2, 3); (3, 3); (4, 3); (3, 4); (4, 4) ]
    // Where the negative controls run (the two truncation anchors included, so
    // the discrimination is tested on the truncated basis too).
    let negAnchors = set [ (2, 2); (2, 3); (3, 3); (3, 4) ]

    for (n, m) in anchors do
        let tag = sprintf "N=%d m=%d" n m
        let d = pown n m
        let parts = PS.permPartitions m n |> List.toArray
        let nPart = parts.Length
        let bell = PS.partitionCount m m
        let want = PS.partitionCount m n

        // ====================================================================
        // 1. THE REFERENCE PROJECTOR — exact, and about tuples only
        // ====================================================================
        let rf = buildRef n m
        check (sprintf "%s: tr(P_ref) = #partitions of [%d] with <= %d blocks (exact)" tag m n)
              (rf.Trace = Rat.ofBigInt (bigint want) && int64 nPart = want)
              (sprintf "tr = %s, partitionCount = %d, permPartitions emitted %d, |S_%d| = %d, dim = %d"
                   (Rat.show rf.Trace) want nPart n rf.Order d)
        check (sprintf "%s: P_ref idempotent and symmetric over Q (exact)" tag)
              (rf.Idempotent && rf.Symmetric)
              (if not rf.Idempotent then rf.IdemDetail
               elif not rf.Symmetric then "C is not symmetric"
               else sprintf "P^2 = P via C*C = %d*C, and C = C^T, over %d^2 entries" rf.Order d)

        // ====================================================================
        // 2. THE BASIS — two independent constructions of the same 0/1 columns
        // ====================================================================
        let patterns = Array.init d (fun code -> patternRgs (decodeTuple n m code))
        // (a) via the API under test: B_γ(t) = coarsens (pattern t) γ.
        let colVia =
            parts |> Array.map (fun g ->
                [| for t in 0 .. d - 1 do if PS.coarsens patterns.[t] g then yield t |])
        // (b) definitionally: t constant on every block of γ. No MLPermSpec.
        let colDef =
            parts |> Array.map (fun g ->
                [| for code in 0 .. d - 1 do
                     let t = decodeTuple n m code
                     let mutable ok = true
                     for i in 0 .. m - 1 do
                         for j in i + 1 .. m - 1 do
                             if g.[i] = g.[j] && t.[i] <> t.[j] then ok <- false
                     if ok then yield code |])
        let idOk = parts |> Array.forall (fun g -> patternRgs g = g)
        let colsAgree = Array.forall2 (fun (a: int[]) (b: int[]) -> a = b) colVia colDef
        let sizesOk =
            Array.forall2 (fun (g: int[]) (sup: int[]) ->
                bigint sup.Length = BigInteger.Pow(bigint n, PS.blockCount g)) parts colVia
        check (sprintf "%s: equality pattern IS an RGS; coarsens-built B = definitional B (%d columns)" tag nPart)
              (idOk && colsAgree && sizesOk)
              (if not idOk then "patternRgs(RGS gamma) <> gamma for some emitted partition"
               elif not colsAgree then "the coarsens orientation disagrees with the definition"
               elif not sizesOk then "|supp B_gamma| <> N^b(gamma)"
               else sprintf "|supp B_gamma| = N^b(gamma) for all %d, total support %d"
                        nPart (colVia |> Array.sumBy Array.length))

        // ====================================================================
        // 3. THE GRAM CLOSED FORM — against an INDEPENDENT union-find join
        // ====================================================================
        let bc = basisProjector d colVia
        match bc with
        | None ->
            check (sprintf "%s: Gram (B^T B) is invertible over Q" tag) false
                  "Gaussian elimination found a zero pivot - the emitted basis is dependent"
        | Some bs ->
            let mutable gramOk = true
            let mutable gramBad = ""
            for a in 0 .. nPart - 1 do
                for b in 0 .. nPart - 1 do
                    let j = joinRgs parts.[a] parts.[b]
                    let predicted = BigInteger.Pow(bigint n, PS.blockCount j)
                    if bs.Gram.[a].[b] <> predicted then
                        if gramOk then
                            gramBad <- sprintf "[%d][%d] = %O but N^b(join) = %O (join %A)" a b bs.Gram.[a].[b] predicted j
                        gramOk <- false
            check (sprintf "%s: (B^T B)[g,p] = N^b(g v p) exactly, %d^2 entries" tag nPart)
                  gramOk
                  (if gramOk then
                     sprintf "spot: [0][0] = %O (all-one-block), [%d][%d] = %O (finest), [0][%d] = %O"
                         bs.Gram.[0].[0] (nPart - 1) (nPart - 1) bs.Gram.[nPart - 1].[nPart - 1]
                         (nPart - 1) bs.Gram.[0].[nPart - 1]
                   else gramBad)

            // ================================================================
            // 4. THE PIN: P_basis = P_ref ENTRYWISE OVER Q
            // ================================================================
            let (diffs, firstDiff) = compareExact bs.P rf.P
            check (sprintf "%s: B(B^T B)^-1 B^T = P_ref ENTRYWISE over Q, %d^2 entries, zero tolerance" tag d)
                  (diffs = 0 && bs.Trace = rf.Trace)
                  (if diffs = 0 then sprintf "identical; tr = %s on both sides" (Rat.show bs.Trace)
                   else sprintf "%d of %d entries differ, %s (tr %s vs %s)"
                            diffs (d * d) firstDiff (Rat.show bs.Trace) (Rat.show rf.Trace))

            // ================================================================
            // 5. THE TRUNCATION ANCHORS
            // ================================================================
            if want < bell then
                check (sprintf "%s: TRUNCATED basis certified (%d < Bell(%d) = %d)" tag nPart m bell)
                      (diffs = 0 && int64 nPart = want && int64 nPart < bell)
                      (sprintf "the <= %d-block filter drops %d of Bell(%d) = %d partitions, and the remaining %d still span the whole invariant space exactly"
                           n (bell - int64 nPart) m bell nPart)

            // ================================================================
            // 6. NEGATIVE CONTROLS — the block's own discrimination
            // ================================================================
            if negAnchors.Contains((n, m)) then
                // (a) drop the last (finest) coarsening column
                let dropped = Array.sub colVia 0 (nPart - 1)
                match basisProjector d dropped with
                | None ->
                    check (sprintf "%s: NC(a) dropped column -> trace deficit 1 and P_basis <> P_ref" tag)
                          false "the truncated Gram was singular - control inconclusive"
                | Some nb ->
                    let (nd, nfirst) = compareExact nb.P rf.P
                    let trOk = nb.Trace = Rat.ofInt (nPart - 1)
                    check (sprintf "%s: NC(a) dropping partition %A -> tr falls to %d and P_basis <> P_ref" tag parts.[nPart - 1] (nPart - 1))
                          (trOk && nd > 0)
                          (sprintf "tr = %s (want %d), %d of %d entries differ, %s"
                               (Rat.show nb.Trace) (nPart - 1) nd (d * d) nfirst)
                // (b) add a spurious NON-coarsening column: the indicator of the
                //     all-zeros tuple. Its Gram diagonal is 1, and no legitimate
                //     column has diagonal 1 (those are N^b with 1 <= b, N >= 2).
                let spurious = Array.append colVia [| [| 0 |] |]
                match basisProjector d spurious with
                | None ->
                    check (sprintf "%s: NC(b) spurious column -> Gram loses the closed form and P_basis <> P_ref" tag)
                          false "the extended Gram was singular - control inconclusive"
                | Some nb ->
                    let (nd, nfirst) = compareExact nb.P rf.P
                    let diag = nb.Gram.[nPart].[nPart]
                    let legit =
                        [ 1 .. min n m ] |> List.map (fun b -> BigInteger.Pow(bigint n, b))
                    let closedFormBroken = diag = BigInteger.One && not (List.contains diag legit)
                    let trOk = nb.Trace = Rat.ofInt (nPart + 1)
                    check (sprintf "%s: NC(b) spurious e_(0..0) column -> Gram diagonal 1 is no N^b, tr rises to %d, P_basis <> P_ref" tag (nPart + 1))
                          (closedFormBroken && trOk && nd > 0)
                          (sprintf "Gram[%d][%d] = %O, legitimate diagonals %s; tr = %s (want %d); %d of %d entries differ, %s"
                               nPart nPart diag
                               (legit |> List.map string |> String.concat "/")
                               (Rat.show nb.Trace) (nPart + 1) nd (d * d) nfirst)

    sw.Stop()
    printFooter "Perm Oracle"
        [ sprintf "%d passed" passed; sprintf "%d failed" failed; sprintf "%d ms" sw.ElapsedMilliseconds ]
    { Block = "Perm Oracle"; Passed = passed; Failed = failed; Skipped = 0; FailedNames = failedNames }
