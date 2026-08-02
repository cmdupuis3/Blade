/// The Sₙ permutation-module counting layer — the INTEGER half of stage 5a
/// (retired transforms-as-types plan §3.6, §7 stage 5, sub-stage 5a-i). No floats, no
/// emission, no certification lattice: 5a-ii adds the loop-nest emission and
/// the exact-rational Reynolds/Gram oracle, 5a-iii the `ml.perm_equiv(N)`
/// lattice. This file is the part both of those count against.
///
/// WHY THERE IS NO CHARACTER TABLE HERE. For PERMUTATION modules the layer
/// algebra is orbit combinatorics, not representation theory:
///
///     dim Hom_{Sₙ}(ℝ^{n^K}, ℝ^{n^L}) = #orbits of Sₙ on [n]^{K+L}
///                                    = #partitions of [K+L] with ≤ n blocks
///
/// and the BASIS is the set of orbit (= coarsening) indicators, one per
/// partition — so the count is the theorem *because* the basis is emitted
/// (§3.6's house rule). Nothing in this file, or in the ops it sizes, ever
/// needs an Sₙ irrep, a Kronecker coefficient, or a Frobenius–Schur
/// correction. That is the whole point of the 5a/5b split: Sₙ is an
/// INDEX-ACTION member, point groups are the block-spec member.
///
/// ---------------------------------------------------------------------------
/// THE POSITION CONVENTION (fixed HERE; 5a-ii bakes this exact numbering into
/// the emitted loop nests, so a change is a convention break, not a refactor).
/// ---------------------------------------------------------------------------
/// A weight-basis element of `derive_perm_linear(K, L, N, x, w)` is a
/// partition γ of the m = K + L POSITIONS
///
///     position 0 .. K−1        the INPUT axes  of x : Idx<N>^K, in order
///     position K .. K+L−1      the OUTPUT axes of the result : Idx<N>^L
///
/// — inputs first, outputs after, both in axis order. A block of γ that
/// touches only input positions is a SUM, a block touching only output
/// positions is a BROADCAST, a mixed block is a GATHER (§3.6; that reading is
/// 5a-ii's, this file only fixes which position is which).
/// `derive_perm_bias(L, N, b)` is the same construction with K = 0, i.e.
/// partitions of the L output positions alone — which is why `permBiasDim`
/// is `permWeightDim` at K = 0 and gets its own name only for the surface.
///
/// ---------------------------------------------------------------------------
/// THE RGS MODEL
/// ---------------------------------------------------------------------------
/// A partition of [0..m) is stored as its RESTRICTED GROWTH STRING: `rgs.[i]`
/// is the index of i's block, blocks numbered by FIRST APPEARANCE. Equivalently
///
///     rgs.[0] = 0        and        rgs.[i] ≤ 1 + max (rgs.[0 .. i−1])
///
/// which makes the representation canonical (one string per partition, no
/// quotient) and the block count `b(γ) = 1 + max rgs` readable in O(m). The
/// empty string (m = 0) is the empty partition of the empty set, b = 0.
///
/// ---------------------------------------------------------------------------
/// THE ORDER CONVENTION — one function, trivially swappable
/// ---------------------------------------------------------------------------
/// The canonical weight order is the RGS ODOMETER in LEX ASCENDING order:
/// 00…0 (the all-one-block partition) first, 012…m−1 (all singletons) last.
/// `orderPartitions` below is the ONLY place that decides this; a parallel Coq
/// thread is proving `rgs_lex_extends_refinement` (that lex-ascending is a
/// linear extension of coarser-before-finer), and if it lands on the FALLBACK
/// convention — block count ascending, then RGS-lex — the one-line body swap
/// noted at that function is the entire diff. Everything else here, and every
/// count, is order-independent.
///
/// The lemma itself, since the code's triangularity assert is its numerical
/// shadow: let γ' be strictly coarser than γ and let i be the first position
/// where the two strings differ. For j < i they agree. If i lies in the same
/// γ-block as some earlier j, then γ' merges that block too, so
/// rgs'.[i] = rgs'.[j] = rgs.[j] = rgs.[i] — not a difference. So i must OPEN
/// a new block in γ, i.e. rgs.[i] = 1 + max(rgs.[0..i−1]) = 1 + max(rgs'.[0..i−1]),
/// and the growth restriction gives rgs'.[i] ≤ that, hence rgs'.[i] < rgs.[i].
/// Coarser ⇒ lex-smaller. ∎
///
/// ---------------------------------------------------------------------------
/// THE INDEPENDENCE CERTIFICATE — integer, asserted on every counting call
/// ---------------------------------------------------------------------------
/// B_γ is the orbit indicator: B_γ(t) = 1 iff the tuple t ∈ [N]^m is CONSTANT
/// on every block of γ. Each partition's own RGS is a legal witness point
/// (it uses exactly b(γ) ≤ N distinct values), and evaluating one basis
/// function at another's witness is pure combinatorics:
///
///     B_{γ_b}(RGS γ_a) = 1   ⇔   γ_a is coarser than γ_b   ⇔   coarsens γ_a γ_b
///
/// (t = RGS(γ_a) is constant exactly on γ_a's blocks, so it is constant on
/// γ_b's blocks iff every γ_b block sits inside a γ_a block). So the
/// witness-evaluation matrix — ROW a = the witness point RGS(γ_a), COLUMN
/// b = the basis function B_{γ_b} — is
///
///     W.[a].[b] = coarsens γ_a γ_b
///
/// and the order lemma above makes it UNITRIANGULAR: W.[a].[a] is true
/// (reflexivity) and W.[a].[b] ⇒ a ≤ b. A unitriangular evaluation matrix is
/// invertible over ℤ, so {B_γ} is linearly independent — no float, no
/// tolerance, no rank decision anywhere. Two things are therefore asserted on
/// EVERY call that produces the list (house discipline, as MLSpec.polyLabels
/// does; a violation is a compiler bug, not a user error):
///
///   1. the odometer's own count equals the Stirling-recurrence count
///      Σ_{j=0..min(N,m)} S(m, j) — two independent routes to the same
///      integer, one enumerative and one recursive;
///   2. W is unitriangular over the emitted order.
///
/// Cost is O(M²·m) with M ≤ 203 at the cap — microseconds, paid gladly.
///
/// The Gram matrix ⟨B_γ, B_π⟩ = n^{b(γ ∨ π)} that 5a-ii's exact-rational
/// Reynolds oracle needs is NOT here: it is oracle machinery, not sizing.
///
/// ---------------------------------------------------------------------------
/// CAPS AND THE v1 STATIC-N RULE
/// ---------------------------------------------------------------------------
/// K + L ≤ 6 (Bell(6) = 203 weights; the emitted nest is b(γ) deep and the
/// weight buffer is one slot per partition). v1 additionally requires a static
/// N ≥ K + L, so the basis is the FULL partition lattice of [K+L] and the
/// weight count is Bell(K+L). Below that the lattice truncates to partitions
/// with ≤ N blocks — a perfectly good basis, and this file computes it (the
/// tests pin it), but a DIFFERENT one, and §3.6's no-silent-fork rule says the
/// surface must refuse rather than quietly switch conventions. So the sizing
/// builtins error below it with a diagnostic naming the truncated-basis
/// variant as a named deferral, exactly as `derive_perm_linear` will.
module Blade.ML.PermSpec

// ---------------------------------------------------------------------------
// Caps
// ---------------------------------------------------------------------------

/// The static cap on m = K + L. Bell(6) = 203 partitions; the certificate is
/// O(203²·6) and the emitted weight buffer 203 slots.
[<Literal>]
let maxPositions = 6

// ---------------------------------------------------------------------------
// The integer layer
// ---------------------------------------------------------------------------

/// b(γ) — the block count of an RGS: 1 + max, and 0 for the empty string
/// (m = 0 is the empty partition of the empty set).
let blockCount (rgs: int[]) : int =
    if rgs.Length = 0 then 0 else 1 + Array.max rgs

/// S(m, j) — Stirling numbers of the second kind by the standard recurrence
/// S(a, b) = b·S(a−1, b) + S(a−1, b−1), with S(0, 0) = 1, S(a, 0) = 0 for
/// a > 0, and S(a, b) = 0 for b > a. int64 is enormous overkill at m ≤ 6
/// (S(6, 3) = 90) and is what keeps the reference route free of any overflow
/// argument.
let stirling2 (m: int) (j: int) : int64 =
    if m < 0 || j < 0 then
        failwithf "internal: stirling2 with a negative argument (m = %d, j = %d)" m j
    elif j > m then 0L
    else
        let mutable prev : int64[] = Array.zeroCreate (j + 1)
        prev.[0] <- 1L // S(0, 0) = 1
        for a in 1 .. m do
            let cur : int64[] = Array.zeroCreate (j + 1)
            for b in 1 .. min a j do
                cur.[b] <- int64 b * prev.[b] + prev.[b - 1]
            prev <- cur
        prev.[j]

/// The REFERENCE count of partitions of [m] into at most n blocks:
/// Σ_{j=0..min(n,m)} S(m, j). At n ≥ m this is the Bell number B(m).
///
/// The j = 0 term is the EMPTY partition — S(0, 0) = 1 and S(a, 0) = 0 for
/// a > 0 — so it contributes exactly at m = 0 and nowhere else. That is how
/// B(0) = 1 enters, and it is the convention behind `permBiasDim(0, N) = 1`:
/// an L = 0 output is the invariant readout, which has exactly one Sₙ-linear
/// form (the constant), not zero.
let partitionCount (m: int) (n: int) : int64 =
    if m < 0 then failwithf "internal: partitionCount with negative m (%d)" m
    if n < 0 then failwithf "internal: partitionCount with negative block bound (%d)" n
    let mutable acc = 0L
    for j in 0 .. min n m do
        acc <- acc + stirling2 m j
    acc

/// `coarsens γ' γ` — γ' is coarser than or equal to γ: every block of γ sits
/// inside a block of γ'.
///
///     coarsens γ' γ  ⇔  ∀ i j.  γ.[i] = γ.[j] ⇒ γ'.[i] = γ'.[j]
///
/// Reflexive, transitive, antisymmetric on RGS strings (canonical form ⇒ the
/// two-way implication forces equality), i.e. the refinement partial order.
/// It is also the witness-evaluation identity, read at the top of this file:
/// `coarsens γ_a γ_b` = the value of the orbit indicator B_{γ_b} at the
/// witness point RGS(γ_a).
let coarsens (gCoarse: int[]) (gFine: int[]) : bool =
    if gCoarse.Length <> gFine.Length then
        failwithf "internal: coarsens on partitions of different sizes (%d vs %d)"
            gCoarse.Length gFine.Length
    let m = gFine.Length
    let mutable ok = true
    for i in 0 .. m - 1 do
        for j in i + 1 .. m - 1 do
            if gFine.[i] = gFine.[j] && gCoarse.[i] <> gCoarse.[j] then ok <- false
    ok

/// Every RGS of length m, LEX ASCENDING — the odometer, generated directly in
/// canonical form (position i ranges over 0 .. 1 + max of the prefix, so no
/// filtering and no duplicate ever occurs). m = 0 yields exactly one string,
/// the empty one.
let private rgsOdometer (m: int) : int[] list =
    let buf : int[] = Array.zeroCreate m
    let acc = System.Collections.Generic.List<int[]>()
    let rec go (i: int) (mx: int) =
        if i = m then acc.Add(Array.copy buf)
        else
            for v in 0 .. mx + 1 do
                buf.[i] <- v
                go (i + 1) (max mx v)
    go 0 -1
    List.ofSeq acc

/// THE CANONICAL WEIGHT ORDER — the single swappable point of this file.
///
/// Currently the IDENTITY on the odometer's output, i.e. RGS-lex ascending,
/// which the order lemma in the header makes a linear extension of
/// coarsest-first. If the Coq thread lands on the fallback convention instead
/// (block count ascending, then RGS-lex), the entire change is this body:
///
///     parts |> List.sortBy blockCount        // F# List.sortBy is STABLE, so
///                                            // RGS-lex survives within a
///                                            // block-count class
///
/// — and the unitriangularity assert below re-certifies it for free, since
/// coarser ⇒ strictly fewer blocks makes that order a linear extension too.
let private orderPartitions (parts: int[] list) : int[] list = parts

/// The certificate of the header, run on every list this file produces.
let private certify (m: int) (maxBlocks: int) (parts: int[] list) : unit =
    // (1) enumeration vs recurrence — two independent routes to one integer.
    let got = int64 (List.length parts)
    let want = partitionCount m maxBlocks
    if got <> want then
        failwithf "internal: the RGS odometer enumerated %d partitions of [%d] with <= %d blocks, but the Stirling recurrence says %d"
            got m maxBlocks want
    // (2) the witness-evaluation matrix W.[a].[b] = coarsens γ_a γ_b is
    //     unitriangular over the emitted order: unit diagonal, and nothing
    //     below it (a basis function is 1 at a LATER witness only).
    let arr = List.toArray parts
    for a in 0 .. arr.Length - 1 do
        if not (coarsens arr.[a] arr.[a]) then
            failwithf "internal: the coarsening order is not reflexive at partition %d (%A) of [%d]"
                a arr.[a] m
        for b in 0 .. a - 1 do
            if coarsens arr.[a] arr.[b] then
                failwithf "internal: the witness-evaluation matrix of [%d] is NOT unitriangular — partition %d (%A) is coarser than the EARLIER partition %d (%A), so the emission order is not a linear extension of refinement (the rgs_lex_extends_refinement keystone; see the order-convention block in MLPermSpec.fs)"
                    m a arr.[a] b arr.[b]

/// The weight basis of the Sₙ index-action surface: every partition of the
/// m positions into at most `maxBlocks` blocks, in the canonical emission
/// order, each as its RGS. Length = `partitionCount m maxBlocks` (asserted),
/// = Bell(m) whenever maxBlocks ≥ m.
///
/// `maxBlocks` is the axis extent N: a partition with more blocks than N has
/// no witness tuple in [N]^m at all — its indicator is identically zero and it
/// is not a basis element. The truncated set is still downward-closed under
/// coarsening (coarser means FEWER blocks), which is why the unitriangularity
/// certificate holds verbatim in the truncated case; v1's surface refuses it
/// anyway, see `checkPermSizing`.
let permPartitions (m: int) (maxBlocks: int) : int[] list =
    if m < 0 then failwithf "internal: permPartitions with negative position count (%d)" m
    if m > maxPositions then
        failwithf "internal: permPartitions with %d positions — the S_n surface is capped at K + L <= %d (retired transforms-as-types plan §3.6)"
            m maxPositions
    if maxBlocks < 0 then
        failwithf "internal: permPartitions with negative block bound (%d)" maxBlocks
    let parts =
        rgsOdometer m
        |> List.filter (fun rgs -> blockCount rgs <= maxBlocks)
        |> orderPartitions
    certify m maxBlocks parts
    parts

/// dim Hom_{Sₙ}(ℝ^{N^K}, ℝ^{N^L}) — the free-weight count of
/// `ml.derive_perm_linear(K, L, N, x, w)`. Bell(K+L) at N ≥ K+L.
let permWeightDim (k: int) (l: int) (n: int) : int =
    List.length (permPartitions (k + l) n)

/// dim (ℝ^{N^L})^{Sₙ} — the free-weight count of the rep-introduction form
/// `ml.derive_perm_bias(L, N, b)`, i.e. `permWeightDim 0 l n`. Bell(L) at
/// N ≥ L; 1 at L = 0 (the invariant readout, see `partitionCount`).
let permBiasDim (l: int) (n: int) : int =
    List.length (permPartitions l n)

// ---------------------------------------------------------------------------
// The surface preconditions — shared so the sizing builtins and the (5a-ii)
// ops speak with ONE voice, per §3.6's no-silent-fork rule.
// ---------------------------------------------------------------------------

/// The v1 static preconditions of every Sₙ surface, in one place: the K+L cap,
/// a positive extent, and the full-basis requirement N ≥ m. `what` is the
/// surface name for the message; `mLabel` spells m the way the caller's
/// arguments do (e.g. "K + L" vs "L").
let checkPermSizing (what: string) (mLabel: string) (m: int) (n: int) : Result<unit, string> =
    if m < 0 then
        Error (sprintf "%s: %s must be >= 0 (got %d)" what mLabel m)
    elif m > maxPositions then
        Error (sprintf "%s: %s = %d exceeds the cap of %d — the S_n weight basis is one partition of the %s positions per weight (Bell(%d) = %d at the cap), and the emitted kernel is one loop nest per partition (retired transforms-as-types plan §3.6)"
                   what mLabel m maxPositions mLabel maxPositions (partitionCount maxPositions maxPositions))
    elif n < 1 then
        Error (sprintf "%s: N must be a static int >= 1 (got %d) — it is the node-axis extent" what n)
    elif n < m then
        Error (sprintf "%s: N = %d is smaller than %s = %d. The v1 S_n surface requires a static N >= %s so the weight basis is the FULL partition lattice of the %d positions (Bell(%d) = %d weights); at N = %d the lattice truncates to the partitions with at most N blocks (%d weights) — a different basis, so the count, the weight layout and the emitted kernel all change. That TRUNCATED-BASIS variant is a named deferral (retired transforms-as-types plan §3.6, stage 5a), not a silent fallback: raise N, or lower %s"
                   what n mLabel m mLabel m m (partitionCount m m) n (partitionCount m n) mLabel)
    else Ok ()
