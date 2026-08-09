/// The static irreps-spec model shared by the ML sizing builtins
/// (MLStatics.fs) and the ML-op elaborator (MLElaborate.fs) -- compile-time
/// counterpart of ml/Irreps.fs + ml/TensorProduct.paths. Entry = (l, parity,
/// mult), parity 0=even/1=odd; TP config = (spec1, spec2, specOut).
module Blade.ML.Spec

type SpecEntry = { L: int; Parity: int; Mult: int }
type Spec = SpecEntry list
type TPConfig = { Spec1: Spec; Spec2: Spec; SpecOut: Spec }

let dim (e: SpecEntry) = 2 * e.L + 1
let blockDim (e: SpecEntry) = e.Mult * dim e
let totalDim (s: Spec) = s |> List.sumBy blockDim

/// Block start offsets; length = spec length + 1, last = totalDim.
let blockStarts (s: Spec) : int list =
    (0, s) ||> List.scan (fun acc e -> acc + blockDim e)

let parityMul (a: int) (b: int) = (a + b) % 2

/// sh_spec(lmax): [(0, even, 1); (1, odd, 1); ...] -- parity (-1)^l.
let shSpec (lmax: int) : Spec =
    [ for l in 0 .. lmax -> { L = l; Parity = l % 2; Mult = 1 } ]

/// Valid TP paths (b1, b2, bOut) in lexicographic order -- triangle
/// inequality + parity rule (ml/TensorProduct.paths).
let tpPaths (cfg: TPConfig) : (int * int * int) list =
    [ for b1 in 0 .. cfg.Spec1.Length - 1 do
        for b2 in 0 .. cfg.Spec2.Length - 1 do
          for bo in 0 .. cfg.SpecOut.Length - 1 do
            let e1 = cfg.Spec1.[b1]
            let e2 = cfg.Spec2.[b2]
            let eo = cfg.SpecOut.[bo]
            if eo.L >= abs (e1.L - e2.L) && eo.L <= e1.L + e2.L
               && eo.Parity = parityMul e1.Parity e2.Parity then
                yield (b1, b2, bo) ]

/// ml-spec section 11.1 type check: every output block reachable from some pair.
let allValidOutputs (cfg: TPConfig) : bool =
    let reachable = tpPaths cfg |> List.map (fun (_, _, bo) -> bo) |> Set.ofList
    Set.count reachable = cfg.SpecOut.Length

/// Full CG decomposition of s1 x s2, merged-canonical: each entry pair
/// (e1,e2) contributes l in [|l1-l2|..l1+l2], parity p1*p2, mult m1*m2,
/// aggregated/ordered by (l,parity) -- the tp_spec definition. totalDim (tpSpec s1 s2) = totalDim s1 * totalDim s2.
let tpSpec (s1: Spec) (s2: Spec) : Spec =
    [ for e1 in s1 do
        for e2 in s2 do
          for l in abs (e1.L - e2.L) .. e1.L + e2.L ->
            { L = l; Parity = parityMul e1.Parity e2.Parity; Mult = e1.Mult * e2.Mult } ]
    |> List.groupBy (fun e -> (e.L, e.Parity))
    |> List.sortBy fst
    |> List.map (fun ((l, p), es) -> { L = l; Parity = p; Mult = es |> List.sumBy (fun e -> e.Mult) })

/// Total multiplicity per (l, parity). Duplicate spec entries AGGREGATE
/// here -- Linear.findBlock's first-match rule makes later duplicates
/// unreachable for `linear` (finding F3); hom-space counting must not inherit that quirk.
let aggregateByIrrep (s: Spec) : Map<int * int, int> =
    (Map.empty, s) ||> List.fold (fun m e ->
        m |> Map.change (e.L, e.Parity) (fun cur -> Some (defaultArg cur 0 + e.Mult)))

/// dim Hom_G(V_in, V_out) by Schur's lemma: an equivariant linear map only
/// connects irreps of identical (l, parity), one free parameter per
/// (input copy, output copy) pair -- sum_{(l,p)} multIn*multOut. homDim = 0 iff every equivariant map is 0.
let homDim (sIn: Spec) (sOut: Spec) : int =
    let aIn = aggregateByIrrep sIn
    aggregateByIrrep sOut
    |> Map.fold (fun acc k mOut ->
        match Map.tryFind k aIn with
        | Some mIn -> acc + mIn * mOut
        | None -> acc) 0

/// The complete Schur basis of Hom_G as block pairs: ALL (input, output)
/// pairs of equal (l, parity) -- generalizes linearBlocks' first-match rule
/// (finding F3). Output-major; an unmatched output block is absent (derive_linear zero-fills it). sum multOut*multIn over pairs = homDim.
let homBlocks (sIn: Spec) (sOut: Spec) : (int * int * SpecEntry * SpecEntry) list =
    [ for bo in 0 .. sOut.Length - 1 do
        for bi in 0 .. sIn.Length - 1 do
          if sIn.[bi].L = sOut.[bo].L && sIn.[bi].Parity = sOut.[bo].Parity then
            yield (bi, bo, sOut.[bo], sIn.[bi]) ]

let tpWeightDim (cfg: TPConfig) : int =
    tpPaths cfg
    |> List.sumBy (fun (b1, b2, bo) ->
        cfg.SpecOut.[bo].Mult * cfg.Spec1.[b1].Mult * cfg.Spec2.[b2].Mult)

// The S2 compaction of the self-tensor-product.

/// The exchange sign of a CG path: sigma = (-1)^(l1+l2-lo), the sign
/// coefficients pick up when the two inputs swap (<l2 m2;l1 m1|lo mo> =
/// sigma <l1 m1;l2 m2|lo mo>, real basis included). Returns +1/-1;
/// l1+l2-lo >= 0 on every valid path, `abs` only guards misuse.
let exchangeSign (l1: int) (l2: int) (lo: int) : int =
    if abs (l1 + l2 - lo) % 2 = 0 then 1 else -1

/// Which S2-isotypic half of a self-tensor-product a compacted weight
/// buffer parameterizes: exchange-symmetric (Sym^2, `derive_sym_tp`) or
/// exchange-antisymmetric (Lambda^2, `derive_alt_tp`).
type S2Component =
    | S2Sym
    | S2Alt

/// The config `derive_tp(s, s, ...)` elaborates: both inputs `s`, output the
/// full CG decomposition. The S2 compaction is defined RELATIVE TO THIS
/// config -- kept-path indices index `tpPaths (selfTpConfig s)`, exactly the
/// path order (and dense weight layout) MLElaborate.tpDecl bakes.
let selfTpConfig (s: Spec) : TPConfig =
    { Spec1 = s; Spec2 = s; SpecOut = tpSpec s s }

/// One kept path of the S2 compaction. `Idx` indexes `tpPaths
/// (selfTpConfig s)`; `Mirror` is `Some j` (the DROPPED path b2,b1,bo) for
/// b1<b2, `None` on a diagonal b1=b2. `Sigma` is the exchange sign, shared by both mirror-pair members.
type KeptPath = {
    Idx: int
    B1: int
    B2: int
    BO: int
    Mirror: int option
    Sigma: int
}

/// The kept paths of the self-TP s x s: every path with b1 <= b2, in
/// `tpPaths` order. The mirror of a valid path is always valid (triangle
/// inequality and parity product are symmetric in the two inputs;
/// spec1=spec2), so every b1 < b2 path has a partner to drop.
let symTpKeptPaths (s: Spec) : KeptPath list =
    let cfg = selfTpConfig s
    let paths = tpPaths cfg
    let index = paths |> List.mapi (fun i p -> (p, i)) |> Map.ofList
    paths
    |> List.mapi (fun i p -> (i, p))
    |> List.filter (fun (_, (b1, b2, _)) -> b1 <= b2)
    |> List.map (fun (i, (b1, b2, bo)) ->
        { Idx = i
          B1 = b1
          B2 = b2
          BO = bo
          Mirror = (if b1 < b2 then Some (Map.find ((b2, b1, bo)) index) else None)
          Sigma = exchangeSign s.[b1].L s.[b2].L cfg.SpecOut.[bo].L })

/// The transpose factor: sign the PARTNER dense slot carries, tau = sigma
/// for Sym^2, -sigma for Lambda^2. Constraint: `c[(b2,b1,bo),mo,u2,u1] =
/// tau * c[(b1,b2,bo),mo,u1,u2]` -- a diagonal path's m x m block is
/// tau-symmetric (tau=+1: u1<=u2 free; tau=-1: u1<u2 free, u1=u2 forced to zero).
let private transposeFactor (comp: S2Component) (sigma: int) : int =
    match comp with
    | S2Sym -> sigma
    | S2Alt -> -sigma

/// The free-cell skeleton of one kept path: which multiplicity cells
/// (u1, u2) of the per-`mo` weight block are FREE, and the packed-slot
/// arithmetic indexing them. Both the dense embed table and the fused
/// cell table are built on this, so the two layouts cannot drift.
type private S2Skel = {
    Kept: KeptPath
    Tau: int
    /// packed slot of this path's (mo = 0, first cell)
    PackedBase: int
    /// packed slots per output multiplicity = Cells.Length
    CellsPerMo: int
    /// the canonical (u1, u2) cells, in packed order
    Cells: (int * int) list
}

/// PACKED LAYOUT (fixed contract -- corpus pins hard-code it): kept paths
/// in `tpPaths` order, per path `mo` in 0..multOut-1; a mirror path's full
/// block (u1 outer, u2 inner, row-major), a diagonal path's u1 outer with
/// u2 from u1 (tau=+1) or u1+1 (tau=-1). Slot = `PackedBase + mo *
/// CellsPerMo + cellIdx`; a path with no free cell is dropped entirely.
let private s2TpSkeleton (comp: S2Component) (s: Spec) : S2Skel list =
    let sA = List.toArray s
    let oA = (selfTpConfig s).SpecOut |> List.toArray
    let out = ResizeArray<S2Skel> ()
    let mutable packed = 0
    for kp in symTpKeptPaths s do
        let tau = transposeFactor comp kp.Sigma
        let m1 = sA.[kp.B1].Mult
        let m2 = sA.[kp.B2].Mult
        let cells =
            match kp.Mirror with
            // Mirror pair: whole m1 x m2 block free (dropped partner's (u2,u1) slot determines it).
            | Some _ -> [ for u1 in 0 .. m1 - 1 do for u2 in 0 .. m2 - 1 -> (u1, u2) ]
            // Diagonal: triangle of the tau-symmetric block (inclusive at tau=+1, strict/zeroed at tau=-1).
            | None -> [ for u1 in 0 .. m1 - 1 do for u2 in (if tau = 1 then u1 else u1 + 1) .. m1 - 1 -> (u1, u2) ]
        if not cells.IsEmpty then
            out.Add { Kept = kp; Tau = tau; PackedBase = packed; CellsPerMo = cells.Length; Cells = cells }
        packed <- packed + cells.Length * oA.[kp.BO].Mult
    List.ofSeq out

/// Packed slot count + dense embedding of one S2 component (computed
/// together so they can't drift). EMBED TABLE: `(denseSlot, packedSlot,
/// sign)`, denseSlot in tpDecl's layout `pWOff(p)+(mo*m1+u1)*m2+u2`. Kept
/// slot takes +1, partner slot tau; every dense slot not forced to zero
/// appears EXACTLY once, so `wd(denseSlot) = sign * w(packedSlot)` over a
/// zeroed buffer reconstructs the dense tensor -- the oracle
/// `derive_tp(S,S,x,y,embed(w))` the compacted kernels are value-pinned against.
let private s2TpCompaction (comp: S2Component) (s: Spec) : int * (int * int * float) list =
    let cfg = selfTpConfig s
    let paths = tpPaths cfg |> List.toArray
    let sA = List.toArray s
    let oA = List.toArray cfg.SpecOut
    let multO = paths |> Array.map (fun (_, _, bo) -> oA.[bo].Mult)
    let m1s = paths |> Array.map (fun (b1, _, _) -> sA.[b1].Mult)
    let m2s = paths |> Array.map (fun (_, b2, _) -> sA.[b2].Mult)
    // tpDecl's pWOff, recomputed here (not shared) since this must match the contract exactly.
    let wOff = Array.zeroCreate (paths.Length + 1)
    for p in 0 .. paths.Length - 1 do
        wOff.[p + 1] <- wOff.[p] + multO.[p] * m1s.[p] * m2s.[p]
    let denseAt p mo u1 u2 = wOff.[p] + (mo * m1s.[p] + u1) * m2s.[p] + u2
    let entries = ResizeArray<int * int * float> ()
    let mutable packed = 0
    for sk in s2TpSkeleton comp s do
        let p = sk.Kept.Idx
        let tauF = float sk.Tau
        for mo in 0 .. multO.[p] - 1 do
            sk.Cells |> List.iteri (fun ci (u1, u2) ->
                let slot = sk.PackedBase + mo * sk.CellsPerMo + ci
                entries.Add (denseAt p mo u1 u2, slot, 1.0)
                match sk.Kept.Mirror with
                // m1s/m2s are SWAPPED at partner path p', so denseAt p' mo u2 u1 = pWOff(p')+(mo*m2+u2)*m1+u1 as required.
                | Some p' -> entries.Add (denseAt p' mo u2 u1, slot, tauF)
                | None -> if u2 <> u1 then entries.Add (denseAt p mo u2 u1, slot, tauF))
        packed <- packed + sk.CellsPerMo * multO.[p]
    (packed, List.ofSeq entries)

/// Closed-form dimension of an S2 component: a mirror pair's
/// 2*multOut*m1*m2 dense parameters split evenly; a diagonal path's per-mo
/// m x m block splits as m(m+tau)/2 + m(m-tau)/2 (cross-checked against s2TpWeightDim).
let private s2TpWeightDimClosed (comp: S2Component) (s: Spec) : int =
    let sOut = tpSpec s s |> List.toArray
    let sA = List.toArray s
    symTpKeptPaths s
    |> List.sumBy (fun kp ->
        let multO = sOut.[kp.BO].Mult
        match kp.Mirror with
        | Some _ -> multO * sA.[kp.B1].Mult * sA.[kp.B2].Mult
        | None ->
            let m = sA.[kp.B1].Mult
            multO * (m * (m + transposeFactor comp kp.Sigma) / 2))

/// Free-parameter count of one S2 component, cross-checked: packed slot
/// count MUST equal the closed-form dimension -- a mismatch is a compiler
/// bug (enumeration vs counting disagree), failing loudly rather than mis-sizing a buffer.
let private s2TpWeightDim (comp: S2Component) (s: Spec) : int =
    let packed, _ = s2TpCompaction comp s
    let closed = s2TpWeightDimClosed comp s
    if packed <> closed then
        failwithf "internal: S2 %s compaction of the self-TP enumerated %d packed slots but the closed form says %d (spec %A)"
            (match comp with S2Sym -> "symmetric" | S2Alt -> "antisymmetric") packed closed s
    packed

/// dim of the exchange-SYMMETRIC equivariant bilinear maps s x s -> tp_spec
/// s s = dim Hom_G(Sym^2 V, tp_spec) -- the `derive_sym_tp` weight buffer.
let symTpWeightDim (s: Spec) : int = s2TpWeightDim S2Sym s

/// dim of the exchange-ANTISYMMETRIC maps = dim Hom_G(Lambda^2 V, tp_spec)
/// -- the `derive_alt_tp` weight buffer, zero when the hom-space is wholly
/// symmetric; at mult > 1 the Lambda^2 A tensor Lambda^2 U Cauchy term keeps it nonzero.
let altTpWeightDim (s: Spec) : int = s2TpWeightDim S2Alt s

/// symTpWeightDim s + altTpWeightDim s = tpWeightDim (selfTpConfig s): the
/// two components partition the dense parameter space. Cheap; asserted
/// where the compacted kernels are synthesized.
let s2TpSplitIsPartition (s: Spec) : bool =
    symTpWeightDim s + altTpWeightDim s = tpWeightDim (selfTpConfig s)

/// Embedding of the packed symmetric buffer into the dense `derive_tp`
/// layout: `(denseSlot, packedSlot, sign)` entries, see s2TpCompaction.
/// NOT a codegen input -- the emitted kernel reads `s2TpCells` directly;
/// this is the injection embed: W_sym -> W_dense the corpus value-pins by hand.
let symTpEmbedTable (s: Spec) : (int * int * float) list = snd (s2TpCompaction S2Sym s)

/// Embedding of the packed antisymmetric buffer -- same shape, partner sign
/// negated relative to symTpEmbedTable.
let altTpEmbedTable (s: Spec) : (int * int * float) list = snd (s2TpCompaction S2Alt s)

/// One FUSED term pair of the compacted self-TP kernel: one multiplicity
/// cell (u1, u2) of one kept path, with the dropped dense contribution
/// folded in as a second product.
///
/// Emitted arithmetic per cell, per `mo` in 0..MultO-1, per CG entry
/// (c1, c2, c3, coef) of the path's OWN table:
///
///   out[OutOff + mo*OutDim + c3] +=
///       (coef * w[WBase + mo*WStride])
///     * ( x[OffA + c1] * y[OffB + c2]  +  PairSign * (y[OffA + e2] * x[OffB + e1]) )
///
/// (e1,e2) = (c2,c1) on a MIRROR cell, (c1,c2) on a DIAGONAL one: MIRROR
/// (b1<b2) collapses the dropped path's CG table (transposed, times sigma)
/// onto the kept entries with sign sigma*tau (+1 Sym^2, -1 Lambda^2);
/// DIAGONAL (b1=b2) swaps the MULTIPLICITY indices instead -- (u2,u1) is
/// tau times (u1,u2), PairSign=tau, 0.0 on u1=u2 (a single term).
type S2TpCell = {
    /// index into `tpPaths (selfTpConfig s)` -- names the CG table to bake
    Path: int
    /// mirror-pair cell (a dropped partner path folds in) vs diagonal cell
    IsMirror: bool
    /// offset into x/y of the (b1,u1) / (b2,u2) multiplicity slots
    OffA: int
    OffB: int
    /// output block start / block dim / output multiplicity (the `mo` bound)
    OutOff: int
    OutDim: int
    MultO: int
    /// packed weight slot at mo = 0, and the stride per mo
    WBase: int
    WStride: int
    /// coefficient of the folded-in partner term; 0.0 = single-term cell
    PairSign: float
}

/// The fused cell table of one S2 component: kept paths' free cells in
/// packed order (`WBase` runs over the packed buffer). Dropped: every
/// b1 > b2 mirror path, every tau=-1 diagonal path at mult 1 (neither
/// contributes arithmetic). Sum MultO over cells = packed dim (checked).
let s2TpCells (comp: S2Component) (s: Spec) : S2TpCell list =
    let cfg = selfTpConfig s
    let sA = List.toArray s
    let oA = cfg.SpecOut |> List.toArray
    let sIn = blockStarts s
    let sOut = blockStarts cfg.SpecOut
    [ for sk in s2TpSkeleton comp s do
        let kp = sk.Kept
        let isMirror = kp.Mirror.IsSome
        let d1 = dim sA.[kp.B1]
        let d2 = dim sA.[kp.B2]
        // sigma*tau on a mirror cell (dropped path's CG sign * its weight sign), tau on a diagonal one.
        let pair = if isMirror then kp.Sigma * sk.Tau else sk.Tau
        for ci in 0 .. sk.CellsPerMo - 1 do
            let (u1, u2) = sk.Cells.[ci]
            yield { Path = kp.Idx
                    IsMirror = isMirror
                    OffA = sIn.[kp.B1] + u1 * d1
                    OffB = sIn.[kp.B2] + u2 * d2
                    OutOff = sOut.[kp.BO]
                    OutDim = dim oA.[kp.BO]
                    MultO = oA.[kp.BO].Mult
                    WBase = sk.PackedBase + ci
                    WStride = sk.CellsPerMo
                    PairSign = (if not isMirror && u1 = u2 then 0.0 else float pair) } ]

// Symmetric and exterior powers of a spec.

/// Exact binomial coefficient C(n, k) as int64, multiplicative formula
/// (each partial product is the integer C(n-k+i, i), exact division).
/// Local rather than ppl/Combinatorics: MLSpec stays dependency-free (shared with the standalone BladeML project).
let binomial (n: int) (k: int) : int64 =
    if k < 0 || n < 0 || k > n then 0L
    else
        let k = min k (n - k)
        let mutable acc = 1L
        for i in 1 .. k do
            acc <- acc * int64 (n - k + i) / int64 i
        acc

/// Which power of a representation the weight-peel decomposes: symmetric
/// Sym^k(V) (MULTISETS, item reusable) or exterior Lambda^k(V) (SUBSETS, item used at most once).
type PowerKind =
    | PowSym
    | PowAlt

let private powerName (kind: PowerKind) =
    match kind with PowSym -> "sym_spec" | PowAlt -> "alt_spec"

/// The Z2-graded weight histogram of a character: `h.[p].[w+off]` = number
/// of basis vectors of weight w in parity sector p. O(3) = SO(3) x {+/-I},
/// so an irrep is (l,parity) and parity is an INDEPENDENT Z2 grading: a
/// product of vectors adds weights and adds parities mod 2. Integer
/// arithmetic below -- no Wigner tables, no floats.
let private histWidth (off: int) = 2 * off + 1

/// Graded knapsack over the basis "items" of V (each spec entry (l,p,m)
/// contributes m copies of each weight -l..l). `f.[j]` is the graded
/// histogram of degree j. The ONLY difference between the two powers is
/// the inner j loop direction: Sym^k ascends (item may repeat: multisets,
/// h_k); Lambda^k descends (item enters at most once: subsets, e_k).
let private gradedPowerHist (kind: PowerKind) (s: Spec) (k: int) (off: int) : int[][] =
    let width = histWidth off
    let f = Array.init (k + 1) (fun _ -> Array.init 2 (fun _ -> Array.zeroCreate<int> width))
    f.[0].[0].[off] <- 1
    let js = match kind with PowSym -> [ 1 .. k ] | PowAlt -> [ k .. -1 .. 1 ]
    for e in s do
        for _ in 1 .. e.Mult do
            for w in -e.L .. e.L do
                for j in js do
                    let src = f.[j - 1]
                    let dst = f.[j]
                    for q in 0 .. 1 do
                        let sq = src.[q]
                        let dq = dst.[(q + e.Parity) % 2]
                        for i in 0 .. width - 1 do
                            if sq.[i] <> 0 then dq.[i + w] <- dq.[i + w] + sq.[i]
    f.[k]

/// Weight-peel of one parity sector: take the highest weight L with
/// nonzero multiplicity c, emit c copies of irrep (L,p), subtract c copies
/// of its weight multiset {-L..L}. Asserts are compiler-bug guards: a
/// genuine representation's character stays nonnegative and w <-> -w
/// symmetric at every step, peeling to exactly zero.
let private peelSector (what: string) (p: int) (off: int) (hist: int[]) : SpecEntry list =
    let h = Array.copy hist
    let check (stage: string) =
        for i in 0 .. histWidth off - 1 do
            if h.[i] < 0 then
                failwithf "internal: %s weight histogram went negative (parity %d, weight %d, %s)"
                    what p (i - off) stage
            if h.[i] <> h.[histWidth off - 1 - i] then
                failwithf "internal: %s weight histogram is not w <-> -w symmetric (parity %d, weight %d, %s)"
                    what p (i - off) stage
    check "before the peel"
    let out = ResizeArray<SpecEntry> ()
    for l in off .. -1 .. 0 do
        let c = h.[off + l]
        if c > 0 then
            out.Add { L = l; Parity = p; Mult = c }
            for w in -l .. l do
                h.[off + w] <- h.[off + w] - c
            check (sprintf "after peeling l=%d" l)
    if h |> Array.exists (fun x -> x <> 0) then
        failwithf "internal: %s weight histogram did not peel to zero (parity %d, residue %A)" what p h
    List.ofSeq out

/// Sym^k(V) / Lambda^k(V) as a spec, merged-canonical ascending by
/// (l, parity), stable for IrrepsIdx<> annotations. Cross-checked against
/// Coq-proved cardinalities every call: total_dim(Sym^k)=C(n+k-1,k),
/// total_dim(Lambda^k)=C(n,k), n=total_dim s.
let powerSpec (kind: PowerKind) (s: Spec) (k: int) : Spec =
    if k < 0 then failwithf "internal: %s with negative k (%d)" (powerName kind) k
    let n = totalDim s
    let lMax = s |> List.fold (fun a e -> max a e.L) 0
    let off = lMax * k
    let hist = gradedPowerHist kind s k off
    let res =
        [ for p in 0 .. 1 do yield! peelSector (powerName kind) p off hist.[p] ]
        |> List.groupBy (fun e -> (e.L, e.Parity))
        |> List.sortBy fst
        |> List.map (fun ((l, p), es) -> { L = l; Parity = p; Mult = es |> List.sumBy (fun e -> e.Mult) })
    let expected = match kind with PowSym -> binomial (n + k - 1) k | PowAlt -> binomial n k
    if int64 (totalDim res) <> expected then
        failwithf "internal: %s(spec, %d) decomposed to total_dim %d but the basis cardinality is %d (spec %A)"
            (powerName kind) k (totalDim res) expected s
    res

/// Sym^k(V): the degree-k monomial space, whose basis is the canonical
/// multisets of V's basis -- literally SymIdx<k, IrrepsIdx<spec>>.
/// `derive_poly<k>`'s input spec.
let symPowerSpec (s: Spec) (k: int) : Spec = powerSpec PowSym s k

/// Lambda^k(V): the exterior power. Empty (the zero space) exactly when k > dim V.
let altPowerSpec (s: Spec) (k: int) : Spec = powerSpec PowAlt s k

/// The degree-k parameter-count theorem: free weights of a degree-k
/// homogeneous equivariant polynomial map V -> W = dim Hom_G(Sym^k V, W)
/// (polarization, counted by Schur). k=1 is homDim; k=2 with
/// sOut=tp_spec(s,s) reproduces symTpWeightDim exactly.
let polyWeightDim (s: Spec) (k: int) (sOut: Spec) : int = homDim (symPowerSpec s k) sOut

/// The canonical multisets i1<=...<=ik over 0..n-1 in LEX order -- the
/// SymIdx<k, Idx<n>> cell order the compiler's unrank agrees with, and
/// ppl/SymTensor.enumerate produces (never rankOf, which is colex).
/// Length = C(n+k-1, k).
let symMultisets (n: int) (k: int) : int list list =
    let rec go (start: int) (rem: int) : int list list =
        if rem = 0 then [ [] ]
        else [ for i in start .. n - 1 do
                 for rest in go i (rem - 1) -> i :: rest ]
    go 0 k

// The Sym^k label basis: the integer label layer.
//
// LABEL CONVENTION (fixed here; a change is a convention break, not a
// refactor -- the emitted kernel bakes this exact enumeration). A label
// names one basis vector of Sym^k(V), built by copy-splitting (reduces the
// plethysm to single-row Sym^j(V_l) factors) then pairwise multiplicity-
// free CG coupling. Every label emits exactly one vector, so the
// per-(L,P) label count MUST equal `powerSpec PowSym`'s multiplicity
// (Coq-checked at the sector-summation level, proofs/BladeSymPower.v
// `sym_copy_splitting`; asserted on every `polyLabels` call):
//
//  1. COPIES: (l,p,m) -> m copies of (l,p,1), indexed globally, block-major, mult inner (`polyCopies`).
//  2. SECTORS: a nondecreasing size-k multiset of copy indices, lex-ascending `symMultisets` order
//     (at k=2, this is the self-TP kept-cell enumeration, `s2TpCells`).
//  3. DEGREES: copy c's degree j_c = its sector multiplicity; only used (j_c>0) copies carry label data.
//  4. OCCURRENCES: a used copy selects one occurrence of V_{L_c} inside Sym^{j_c}(V_{l_c}) (`symOccurrences`).
//  5. CHAIN: the L_c couple LEFT-COMB in copy order, each step adding one intermediate L in
//     |L_acc-L_c|..L_acc+L_c ASCENDING; the final L is the label's irrep.
//  6. PARITY: P = sum_c j_c*p_c mod 2 (Sym^j(V_{l,p}) picks up scalar (-1)^{j*p}, multiplicative along the chain).
//
// ENUMERATION ORDER (`PolyLabel.Index`): sectors in `symMultisets` lex order (slowest); within a sector,
// occurrences then chain steps, both left to right, RIGHTMOST varying fastest (nested, not a product,
// since chain ranges depend on the occurrence choices).
//
// The copy-split basis is NOT GL(m)-channel-covariant: copy indices name multiplicity SLOTS, never channel structure.

/// One multiplicity-1 copy of the copy-split spec: entry (l, p, m) becomes
/// m copies of (l, p, 1).
type PolyCopy = {
    /// global copy index = position in `polyCopies s`
    Copy: int
    /// the spec block this copy came from, and its multiplicity index in it
    Block: int
    MultIdx: int
    L: int
    Parity: int
}

/// The copy splitting of a spec, in copy order (block-major, multiplicity
/// index inner). Length = sum mult.
let polyCopies (s: Spec) : PolyCopy list =
    let mutable i = -1
    [ for b in 0 .. s.Length - 1 do
        let e = s.[b]
        for u in 0 .. e.Mult - 1 do
          i <- i + 1
          yield { Copy = i; Block = b; MultIdx = u; L = e.L; Parity = e.Parity } ]

/// The occurrences of irreps V_L inside Sym^j(V_(l,p)), as
/// (L, copy-index-within-the-L-multiplicity-space) pairs, CONVENTION ORDER
/// L DESCENDING then RREF ascending -- the order `SymPowerTables.symPowerTable
/// j l` emits `Occurrences` in (pinned against it in tests, since MLSpec
/// stays dependency-free). COUNTS come from `powerSpec PowSym [(l,p,1)] j`.
let symOccurrences (l: int) (p: int) (j: int) : (int * int) list =
    if j < 1 then failwithf "internal: symOccurrences with degree %d (must be >= 1)" j
    let entries = powerSpec PowSym [ { L = l; Parity = p; Mult = 1 } ] j
    let want = (j * p) % 2
    for e in entries do
        if e.Parity <> want then
            failwithf "internal: Sym^%d(V_%d parity %d) produced an occurrence of parity %d (the copy-power parity rule says %d)"
                j l p e.Parity want
    entries
    |> List.sortByDescending (fun e -> e.L)
    |> List.collect (fun e -> [ for c in 0 .. e.Mult - 1 -> (e.L, c) ])

/// One used copy of a sector: which copy, at what degree, and which occurrence
/// of V_(OccL) inside Sym^Degree(V_copy) this label selects.
type PolyCopyUse = {
    Copy: int
    CopyL: int
    CopyParity: int
    /// j_c -- the copy's multiplicity in the sector multiset
    Degree: int
    /// flat index into `symOccurrences CopyL CopyParity Degree`
    Occ: int
    /// that occurrence's irrep, and its RREF copy index within the L space
    OccL: int
    OccCopy: int
}

/// One label of the Sym^k basis -- see the convention block above.
type PolyLabel = {
    /// canonical flat index = position in `polyLabels s k`
    Index: int
    /// the sector: a nondecreasing size-k multiset of copy indices
    Sector: int list
    /// the used copies, ascending by copy index (empty only at k = 0)
    Uses: PolyCopyUse list
    /// the left-comb couplings: `Chain.[i]` is the running L after
    /// `Uses.[i+1]` couples on (length = Uses.Length - 1; last entry, if any, is `L`).
    Chain: int list
    /// the label's irrep and parity
    L: int
    Parity: int
    /// k!/prod_c j_c!, the sector-constant identity sqrt(k!/prod k_c!) --
    /// a property of the sector, recorded here regardless of where codegen bakes it.
    Multinomial: int64
}

/// The Sym^k label basis of a spec, in canonical enumeration order. Two
/// counting theorems asserted on EVERY call (cheap; a violation is a
/// compiler bug): (1) per (L,P) the label count equals `powerSpec PowSym s
/// k`'s multiplicity; (2) sum_(L,P) count*(2L+1) = C(total_dim s+k-1,k) --
/// redundant given (1), but catches grouping bugs for free.
let polyLabels (s: Spec) (k: int) : PolyLabel list =
    if k < 0 then failwithf "internal: polyLabels with negative k (%d)" k
    let copies = polyCopies s |> List.toArray
    let n = copies.Length
    // occ.[c].[j] -- the occurrence list of copy c at degree j (index 0 unused).
    let occ =
        Array.init n (fun c ->
            Array.init (k + 1) (fun j ->
                if j < 1 then [] else symOccurrences copies.[c].L copies.[c].Parity j))
    let factorial (m: int) = Seq.fold (fun acc i -> acc * int64 i) 1L (seq { 2 .. m })
    // Occurrence choices: used copies left to right, copy 0 SLOWEST.
    let rec occChoices (rest: (int * int) list) : PolyCopyUse list list =
        match rest with
        | [] -> [ [] ]
        | (c, j) :: tl ->
            [ for (oi, (oL, oc)) in List.indexed occ.[c].[j] do
                for restUses in occChoices tl ->
                  { Copy = c; CopyL = copies.[c].L; CopyParity = copies.[c].Parity
                    Degree = j; Occ = oi; OccL = oL; OccCopy = oc } :: restUses ]
    // Left-comb chain: each step ascending, the LAST step varies fastest.
    let rec chains (acc: int) (rest: int list) : int list list =
        match rest with
        | [] -> [ [] ]
        | lNext :: tl ->
            [ for lMid in abs (acc - lNext) .. acc + lNext do
                for tail in chains lMid tl -> lMid :: tail ]
    let labels =
        [ for sector in symMultisets n k do
            // `countBy` on the sorted sector gives (copy, degree) in ascending
            // copy order -- the used copies, left to right.
            let uses = sector |> List.countBy id
            let parity = (uses |> List.sumBy (fun (c, j) -> j * copies.[c].Parity)) % 2
            let multinomial =
                uses |> List.fold (fun acc (_, j) -> acc / factorial j) (factorial k)
            for us in occChoices uses do
                match us with
                // k = 0: the empty sector is the trivial label (Sym^0 = R).
                | [] -> yield (sector, us, [], 0, 0, multinomial)
                | head :: tl ->
                    for ch in chains head.OccL (tl |> List.map (fun u -> u.OccL)) do
                        let finalL = match List.tryLast ch with Some x -> x | None -> head.OccL
                        yield (sector, us, ch, finalL, parity, multinomial) ]
        |> List.mapi (fun i (sector, us, ch, l, p, mn) ->
            { Index = i; Sector = sector; Uses = us; Chain = ch; L = l; Parity = p; Multinomial = mn })
    // (1) the constructive-direct-sum theorem, at the level of counts
    let got = labels |> List.countBy (fun lb -> (lb.L, lb.Parity)) |> Map.ofList
    let want =
        powerSpec PowSym s k
        |> List.map (fun e -> ((e.L, e.Parity), e.Mult))
        |> Map.ofList
    if got <> want then
        failwithf "internal: the Sym^%d label basis of %A counts %A per (L, parity) but sym_spec says %A"
            k s (Map.toList got) (Map.toList want)
    // (2) dimension closure against the Coq-proved basis cardinality
    let dimSum = got |> Map.fold (fun acc (l, _) c -> acc + int64 c * int64 (2 * l + 1)) 0L
    let expected = binomial (totalDim s + k - 1) k
    if dimSum <> expected then
        failwithf "internal: the Sym^%d label basis of %A spans %d dimensions but the basis cardinality is %d"
            k s dimSum expected
    labels

/// Label multiplicity per (L, parity) -- equal to `symPowerSpec s k` block
/// by block (asserted inside `polyLabels`).
let polyLabelCounts (s: Spec) (k: int) : Map<int * int, int> =
    polyLabels s k |> List.countBy (fun lb -> (lb.L, lb.Parity)) |> Map.ofList

/// `polyWeightDim` recomputed label by label (Schur: each label contributes
/// one free weight per matching W copy) -- pinned against it in tests.
let polyWeightDimViaLabels (s: Spec) (k: int) (sOut: Spec) : int =
    let agg = aggregateByIrrep sOut
    polyLabels s k
    |> List.sumBy (fun lb -> defaultArg (Map.tryFind (lb.L, lb.Parity) agg) 0)

/// Per OUTPUT block of `linear`: (input block index, out entry, in entry),
/// input resolved FIRST-MATCH by irrep (ml/Linear.findBlock semantics --
/// duplicate input irreps beyond the first are unreachable, finding F3).
let linearBlocks (specIn: Spec) (specOut: Spec) : Result<(int * SpecEntry * SpecEntry) list, string> =
    specOut
    |> List.fold (fun acc eo ->
        acc |> Result.bind (fun rows ->
            match specIn |> List.tryFindIndex (fun ei -> ei.L = eo.L && ei.Parity = eo.Parity) with
            | Some bi -> Ok (rows @ [ (bi, eo, specIn.[bi]) ])
            | None ->
                Error (sprintf "linear: output irrep (l=%d, parity=%d) not present in the input spec (all_irreps_present fails)"
                           eo.L eo.Parity)))
        (Ok [])

let linearWeightDim (specIn: Spec) (specOut: Spec) : Result<int, string> =
    linearBlocks specIn specOut
    |> Result.map (List.sumBy (fun (_, eo, ei) -> eo.Mult * ei.Mult))
