/// The Sn permutation-module counting layer: orbit combinatorics only, no
/// emission and no certification lattice (those, and `ml.perm_equiv(N)`,
/// live elsewhere and count against this file's numbers).
/// WHY THERE IS NO CHARACTER TABLE HERE. For PERMUTATION modules the layer
/// algebra is orbit combinatorics, not representation theory:
///
///     dim Hom_{Sn}(R^{n^K}, R^{n^L}) = #orbits of Sn on [n]^{K+L}
///                                    = #partitions of [K+L] with <= n blocks
///
/// and the BASIS is the set of orbit (= coarsening) indicators, one per
/// partition, so the count is the theorem because the basis is emitted --
/// no Sn irrep, Kronecker coefficient, or Frobenius-Schur correction is
/// ever needed: Sn is an INDEX-ACTION member, point groups the block-spec one.
/// THE POSITION CONVENTION (fixed here; the loop-nest emitter bakes this
/// numbering in, so a change is a convention break, not a refactor). A
/// weight-basis element of `derive_perm_linear(K, L, N, x, w)` is a
/// partition of the m = K + L POSITIONS:
///
///     position 0 .. K-1        the INPUT axes  of x : Idx<N>^K, in order
///     position K .. K+L-1      the OUTPUT axes of the result : Idx<N>^L
///
/// inputs first, outputs after, both in axis order. A block touching only
/// input positions is a SUM, only output positions a BROADCAST, a mixed
/// block a GATHER. `derive_perm_bias(L, N, b)` is the same with K = 0, why
/// `permBiasDim` is `permWeightDim` at K = 0.
/// THE RGS MODEL. A partition of [0..m) is stored as its RESTRICTED GROWTH
/// STRING: `rgs.[i]` is the index of i's block, numbered by FIRST
/// APPEARANCE: `rgs.[0] = 0` and `rgs.[i] <= 1 + max (rgs.[0 .. i-1])`,
/// which makes the representation canonical (one string per partition) and
/// the block count `b(g) = 1 + max rgs` readable in O(m). The empty string
/// (m = 0) is the empty partition of the empty set, b = 0.
/// THE ORDER CONVENTION -- one function, trivially swappable. The canonical
/// weight order is the RGS ODOMETER in LEX ASCENDING order: 00...0 (the
/// all-one-block partition) first, 012...m-1 (all singletons) last.
/// `orderPartitions` is the ONLY place that decides this; a parallel Coq
/// thread is proving lex-ascending is a linear extension of
/// coarser-before-finer (`rgs_lex_extends_refinement`) -- if it instead
/// lands on block-count-then-RGS-lex, the swap noted there is the entire
/// diff; everything else here, and every count, is order-independent.
/// The lemma, since the code's triangularity assert is its numerical shadow:
/// if g' is strictly coarser than g, the first position where their RGS
/// strings differ must OPEN a new block in g, so g's value there is
/// strictly larger. Coarser => lex-smaller.
/// THE INDEPENDENCE CERTIFICATE -- integer, asserted on every counting call.
/// B_g is the orbit indicator, 1 iff t in [N]^m is CONSTANT on every block
/// of g; each partition's own RGS is a legal witness point, and
///
///     B_{g_b}(RGS g_a) = 1   <=>   g_a is coarser than g_b   <=>   coarsens g_a g_b
///
/// so the witness-evaluation matrix W.[a].[b] = coarsens g_a g_b is, by the
/// order lemma, UNITRIANGULAR, hence invertible over Z, hence {B_g} is
/// linearly independent -- no float, no tolerance, no rank decision. Every
/// call asserts (a violation is a compiler bug, not a user error): (1) the
/// odometer's count matches the Stirling-recurrence count, two independent
/// routes to one integer, and (2) W is unitriangular over the emitted
/// order. Cost O(M^2 * m), M <= 203 at the cap. The Gram matrix
/// <B_g, B_p> = n^{b(g v p)} that the Reynolds oracle needs is NOT here:
/// it is oracle machinery, not sizing.
/// CAPS AND THE STATIC-N RULE. K + L <= 6 (Bell(6) = 203 weights; the
/// emitted nest is b(g) deep, one weight-buffer slot per partition). The
/// surface also requires a static N >= K + L, so the basis is the FULL
/// partition lattice of [K+L]; below that it truncates to partitions with
/// <= N blocks -- a perfectly good but DIFFERENT basis (this file computes
/// it and the tests pin it), so the surface refuses rather than quietly
/// switching conventions.
module Blade.ML.PermSpec

/// The static cap on m = K + L. Bell(6) = 203 partitions; the certificate is
/// O(203^2 * 6) and the emitted weight buffer 203 slots.
[<Literal>]
let maxPositions = 6

/// b(g) -- the block count of an RGS: 1 + max, and 0 for the empty string
/// (m = 0 is the empty partition of the empty set).
let blockCount (rgs: int[]) : int =
    if rgs.Length = 0 then 0 else 1 + Array.max rgs

/// S(m, j) -- Stirling numbers of the second kind by the standard recurrence
/// S(a, b) = b*S(a-1, b) + S(a-1, b-1), S(0, 0) = 1, S(a, 0) = 0 for a > 0,
/// and S(a, b) = 0 for b > a. int64 is overkill at m <= 6, just overflow safety.
let stirling2 (m: int) (j: int) : int64 =
    if m < 0 || j < 0 then
        failwith $"internal: stirling2 with a negative argument (m = {m}, j = {j})"
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
/// sum_{j=0..min(n,m)} S(m, j); at n >= m this is the Bell number B(m). The
/// j = 0 term is the EMPTY partition, contributing exactly at m = 0 (B(0) =
/// 1) -- the convention behind `permBiasDim(0, N) = 1`: an L = 0 output is
/// the invariant readout, with exactly one Sn-linear form, not zero.
let partitionCount (m: int) (n: int) : int64 =
    if m < 0 then failwith $"internal: partitionCount with negative m ({m})"
    if n < 0 then failwith $"internal: partitionCount with negative block bound ({n})"
    let mutable acc = 0L
    for j in 0 .. min n m do
        acc <- acc + stirling2 m j
    acc

/// `coarsens g' g` -- g' is coarser than or equal to g: every block of g
/// sits inside a block of g', i.e. `for all i j: g.[i] = g.[j] => g'.[i] =
/// g'.[j]`. Reflexive, transitive, antisymmetric on RGS strings: the
/// refinement partial order, and also the witness-evaluation identity from
/// the header (`coarsens g_a g_b` = B_{g_b} evaluated at witness RGS(g_a)).
let coarsens (gCoarse: int[]) (gFine: int[]) : bool =
    if gCoarse.Length <> gFine.Length then
        failwith $"internal: coarsens on partitions of different sizes ({gCoarse.Length} vs {gFine.Length})"
    let m = gFine.Length
    let mutable ok = true
    for i in 0 .. m - 1 do
        for j in i + 1 .. m - 1 do
            if gFine.[i] = gFine.[j] && gCoarse.[i] <> gCoarse.[j] then ok <- false
    ok

/// Every RGS of length m, LEX ASCENDING: the odometer, generated directly
/// in canonical form (position i ranges over 0 .. 1 + max of the prefix, so
/// no filtering and no duplicate ever occurs). m = 0 yields the empty string.
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

/// THE CANONICAL WEIGHT ORDER -- the single swappable point of this file.
/// Currently the IDENTITY on the odometer's output (RGS-lex ascending),
/// a linear extension of coarsest-first per the header's order lemma. If
/// the Coq thread lands on the fallback convention instead (block count
/// ascending, then RGS-lex), the entire change is:
///
///     parts |> List.sortBy blockCount   // stable, so RGS-lex survives
///                                       // within a block-count class
///
/// and the unitriangularity assert below re-certifies it for free.
let private orderPartitions (parts: int[] list) : int[] list = parts

/// The certificate of the header, run on every list this file produces.
let private certify (m: int) (maxBlocks: int) (parts: int[] list) : unit =
    // (1) enumeration vs recurrence -- two independent routes to one integer.
    let got = int64 (List.length parts)
    let want = partitionCount m maxBlocks
    if got <> want then
        failwith $"internal: the RGS odometer enumerated {got} partitions of [{m}] with <= {maxBlocks} blocks, but the Stirling recurrence says {want}"
    // (2) the witness-evaluation matrix W.[a].[b] = coarsens g_a g_b is
    //     unitriangular over the emitted order: unit diagonal, and nothing
    //     below it (a basis function is 1 at a LATER witness only).
    let arr = List.toArray parts
    for a in 0 .. arr.Length - 1 do
        if not (coarsens arr.[a] arr.[a]) then
            failwithf "internal: the coarsening order is not reflexive at partition %d (%A) of [%d]"
                a arr.[a] m
        for b in 0 .. a - 1 do
            if coarsens arr.[a] arr.[b] then
                failwithf "internal: the witness-evaluation matrix of [%d] is NOT unitriangular -- partition %d (%A) is coarser than the EARLIER partition %d (%A), so the emission order is not a linear extension of refinement (the rgs_lex_extends_refinement keystone; see the order-convention block in MLPermSpec.fs)"
                    m a arr.[a] b arr.[b]

/// The weight basis of the Sn index-action surface: every partition of the
/// m positions into at most `maxBlocks` blocks, in canonical emission
/// order, each as its RGS. Length = `partitionCount m maxBlocks` (asserted).
/// `maxBlocks` is the axis extent N: a partition with more blocks than N
/// has no witness tuple in [N]^m, so it is not a basis element; the
/// truncated set is still downward-closed under coarsening (certificate
/// holds verbatim there too), but the surface refuses it anyway, see
/// `checkPermSizing`.
let permPartitions (m: int) (maxBlocks: int) : int[] list =
    if m < 0 then failwith $"internal: permPartitions with negative position count ({m})"
    if m > maxPositions then
        failwith $"internal: permPartitions with {m} positions -- the S_n surface is capped at K + L <= {maxPositions} (retired transforms-as-types plan section 3.6)"
    if maxBlocks < 0 then
        failwith $"internal: permPartitions with negative block bound ({maxBlocks})"
    let parts =
        rgsOdometer m
        |> List.filter (fun rgs -> blockCount rgs <= maxBlocks)
        |> orderPartitions
    certify m maxBlocks parts
    parts

/// dim Hom_{Sn}(R^{N^K}, R^{N^L}) -- the free-weight count of
/// `ml.derive_perm_linear(K, L, N, x, w)`. Bell(K+L) at N >= K+L.
let permWeightDim (k: int) (l: int) (n: int) : int =
    List.length (permPartitions (k + l) n)

/// dim (R^{N^L})^{Sn} -- the free-weight count of the rep-introduction form
/// `ml.derive_perm_bias(L, N, b)`, i.e. `permWeightDim 0 l n`. Bell(L) at
/// N >= L; 1 at L = 0 (the invariant readout, see `partitionCount`).
let permBiasDim (l: int) (n: int) : int =
    List.length (permPartitions l n)

// The surface preconditions: shared so the sizing builtins and the emitted
// ops speak with ONE voice (no-silent-fork rule).

/// The static preconditions of every Sn surface, in one place: the K+L cap,
/// a positive extent, and N >= m. `what` is the surface name for the
/// message; `mLabel` spells m the way the caller's arguments do.
let checkPermSizing (what: string) (mLabel: string) (m: int) (n: int) : Result<unit, string> =
    if m < 0 then
        Error $"{what}: {mLabel} must be >= 0 (got {m})"
    elif m > maxPositions then
        Error (sprintf "%s: %s = %d exceeds the cap of %d -- the S_n weight basis is one partition of the %s positions per weight (Bell(%d) = %d at the cap), and the emitted kernel is one loop nest per partition (retired transforms-as-types plan section 3.6)"
                   what mLabel m maxPositions mLabel maxPositions (partitionCount maxPositions maxPositions))
    elif n < 1 then
        Error $"{what}: N must be a static int >= 1 (got {n}) -- it is the node-axis extent"
    elif n < m then
        Error (sprintf "%s: N = %d is smaller than %s = %d. The S_n surface requires a static N >= %s so the weight basis is the FULL partition lattice of the %d positions (Bell(%d) = %d weights); at N = %d the lattice truncates to the partitions with at most N blocks (%d weights) -- a different basis, so the count, the weight layout and the emitted kernel all change. That TRUNCATED-BASIS variant is unsupported rather than silently substituted: raise N, or lower %s"
                   what n mLabel m mLabel m m (partitionCount m m) n (partitionCount m n) mLabel)
    else Ok ()
