/// The static irreps-spec model shared by the ML sizing builtins
/// (ml/compiler/MLStatics.fs: total_dim, tp_weight_dim, linear_weight_dim,
/// sh_spec) and the ML-op elaborator (ml/compiler/MLElaborate.fs). Pure
/// functions of static data — the compile-time counterpart of ml/Irreps.fs
/// + ml/TensorProduct.paths.
///
/// A spec entry is (l, parity, mult) with parity 0 = even, 1 = odd; a spec
/// is an ordered list of entries; a TP config is (spec1, spec2, specOut).
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

/// sh_spec(lmax): [(0, even, 1); (1, odd, 1); ...] — parity (-1)^l.
let shSpec (lmax: int) : Spec =
    [ for l in 0 .. lmax -> { L = l; Parity = l % 2; Mult = 1 } ]

/// Valid TP paths (b1, b2, bOut) in lexicographic order — triangle
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

/// ml-spec §11.1 type check: every output block reachable from some pair.
let allValidOutputs (cfg: TPConfig) : bool =
    let reachable = tpPaths cfg |> List.map (fun (_, _, bo) -> bo) |> Set.ofList
    Set.count reachable = cfg.SpecOut.Length

/// Full Clebsch-Gordan decomposition of s1 ⊗ s2, merged-canonical: every
/// entry pair (e1, e2) contributes l ∈ [|l1-l2| .. l1+l2] with parity
/// p1*p2 and multiplicity m1*m2; contributions are aggregated by
/// (l, parity) and ordered ascending by (l, parity). Spec identity is
/// order-sensitive, so this canonicalization IS the definition of the
/// tp_spec surface builtin — stable to write in annotations.
/// Completeness: totalDim (tpSpec s1 s2) = totalDim s1 * totalDim s2.
let tpSpec (s1: Spec) (s2: Spec) : Spec =
    [ for e1 in s1 do
        for e2 in s2 do
          for l in abs (e1.L - e2.L) .. e1.L + e2.L ->
            { L = l; Parity = parityMul e1.Parity e2.Parity; Mult = e1.Mult * e2.Mult } ]
    |> List.groupBy (fun e -> (e.L, e.Parity))
    |> List.sortBy fst
    |> List.map (fun ((l, p), es) -> { L = l; Parity = p; Mult = es |> List.sumBy (fun e -> e.Mult) })

/// Total multiplicity per (l, parity). Duplicate spec entries AGGREGATE
/// here — Linear.findBlock's first-match rule makes later duplicates
/// unreachable for `linear` (finding F3), but hom-space dimension counting
/// must not inherit that quirk.
let aggregateByIrrep (s: Spec) : Map<int * int, int> =
    (Map.empty, s) ||> List.fold (fun m e ->
        m |> Map.change (e.L, e.Parity) (fun cur -> Some (defaultArg cur 0 + e.Mult)))

/// dim Hom_G(V_in, V_out) by Schur's lemma: an equivariant linear map can
/// only connect irreps of identical (l, parity), contributing one free
/// parameter per (input copy, output copy) pair — Σ_{(l,p)} multIn * multOut
/// over aggregated multiplicities. homDim = 0 ⇔ every equivariant map is 0.
let homDim (sIn: Spec) (sOut: Spec) : int =
    let aIn = aggregateByIrrep sIn
    aggregateByIrrep sOut
    |> Map.fold (fun acc k mOut ->
        match Map.tryFind k aIn with
        | Some mIn -> acc + mIn * mOut
        | None -> acc) 0

/// The complete Schur basis of Hom_G as block pairs: ALL (input, output)
/// block pairs of equal (l, parity) — the all-pairs generalization of
/// linearBlocks' first-match rule (duplicate input irreps become reachable,
/// finding F3). Output-major pair order; never fails — an output block with
/// no matching input is simply absent (derive_linear zero-fills it).
/// Σ multOut*multIn over these pairs = homDim (per-(l,p) product of sums).
let homBlocks (sIn: Spec) (sOut: Spec) : (int * int * SpecEntry * SpecEntry) list =
    [ for bo in 0 .. sOut.Length - 1 do
        for bi in 0 .. sIn.Length - 1 do
          if sIn.[bi].L = sOut.[bo].L && sIn.[bi].Parity = sOut.[bo].Parity then
            yield (bi, bo, sOut.[bo], sIn.[bi]) ]

let tpWeightDim (cfg: TPConfig) : int =
    tpPaths cfg
    |> List.sumBy (fun (b1, b2, bo) ->
        cfg.SpecOut.[bo].Mult * cfg.Spec1.[b1].Mult * cfg.Spec2.[b2].Mult)

// ============================================================================
// The S₂ compaction of the self-tensor-product (plan-transforms-as-types §3.2)
// ============================================================================

/// The exchange sign of a CG path: σ = (−1)^(l1 + l2 − lo), the factor the
/// coefficients pick up when the two inputs are swapped
/// (⟨l2 m2; l1 m1 | lo mo⟩ = σ ⟨l1 m1; l2 m2 | lo mo⟩ — the real basis
/// inherits it because the real→complex change of basis is per-l and acts on
/// each input independently, so it commutes with the swap). +1/−1 as an int;
/// l1 + l2 − lo ≥ 0 on every valid path, `abs` only guards misuse.
let exchangeSign (l1: int) (l2: int) (lo: int) : int =
    if abs (l1 + l2 - lo) % 2 = 0 then 1 else -1

/// Which S₂-isotypic half of a self-tensor-product a compacted weight buffer
/// parameterizes: the exchange-symmetric maps (Sym², `derive_sym_tp`) or the
/// exchange-antisymmetric ones (Λ², `derive_alt_tp`).
type S2Component =
    | S2Sym
    | S2Alt

/// The config `derive_tp(s, s, ...)` elaborates: both inputs `s`, output the
/// full CG decomposition. The S₂ compaction is defined RELATIVE TO THIS
/// config — kept-path indices index `tpPaths (selfTpConfig s)`, which is
/// exactly the path order (and hence the dense weight layout) that
/// MLElaborate.tpDecl bakes.
let selfTpConfig (s: Spec) : TPConfig =
    { Spec1 = s; Spec2 = s; SpecOut = tpSpec s s }

/// One kept path of the S₂ compaction. `Idx` indexes `tpPaths (selfTpConfig
/// s)`; `Mirror` is `Some j` — the index of the swapped path (b2, b1, bo),
/// which the compaction DROPS — for an off-diagonal path b1 < b2, and `None`
/// on a diagonal path b1 = b2 (whose per-mo m×m weight block carries the S₂
/// action internally). `Sigma` is the path's exchange sign; both members of a
/// mirror pair share it.
type KeptPath = {
    Idx: int
    B1: int
    B2: int
    BO: int
    Mirror: int option
    Sigma: int
}

/// The kept paths of the self-TP s ⊗ s: every path with b1 ≤ b2, in `tpPaths`
/// order. The mirror path of a valid path is always valid (the triangle
/// inequality and the parity product are symmetric in the two inputs, and
/// spec1 = spec2 here), so every b1 < b2 path has a partner to drop.
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

/// The transpose factor of a component on a given path: the sign the PARTNER
/// dense slot carries, τ = σ for Sym² and τ = −σ for Λ². The defining
/// constraint on the dense coefficient tensor is
/// `c[(b2,b1,bo), mo, u2, u1] = τ · c[(b1,b2,bo), mo, u1, u2]`, so on a
/// diagonal path the per-mo m×m block is τ-symmetric: τ = +1 keeps u1 ≤ u2
/// free, τ = −1 keeps u1 < u2 free and FORCES the u1 = u2 cells to zero.
let private transposeFactor (comp: S2Component) (sigma: int) : int =
    match comp with
    | S2Sym -> sigma
    | S2Alt -> -sigma

/// The free-cell skeleton of one kept path: which multiplicity cells
/// (u1, u2) of the path's per-`mo` weight block are FREE parameters, and the
/// packed-slot arithmetic that indexes them. Both consumers of the packed
/// layout — the dense embed table (stage 1a's oracle) and the fused cell
/// table the kernel is generated from (stage 1b) — are built on this, so the
/// layout cannot drift between them.
type private S2Skel = {
    Kept: KeptPath
    /// transposeFactor comp Sigma
    Tau: int
    /// packed slot of this path's (mo = 0, first cell)
    PackedBase: int
    /// packed slots per output multiplicity = Cells.Length
    CellsPerMo: int
    /// the canonical (u1, u2) cells, in packed order
    Cells: (int * int) list
}

/// PACKED LAYOUT (fixed surface contract — corpus pins hard-code it): kept
/// paths in `tpPaths` order; per kept path `mo` in 0..multOut−1; then a
/// mirror path contributes its full block (u1 outer, u2 inner, row-major) and
/// a diagonal path contributes u1 outer with u2 from u1 (τ = +1) or u1 + 1
/// (τ = −1). In skeleton terms: the packed slot of (path, mo, cellIdx) is
/// `PackedBase + mo * CellsPerMo + cellIdx`.
///
/// Paths with NO free cell (a τ = −1 diagonal path at multiplicity 1) are
/// dropped from the skeleton entirely — they contribute no parameter and, in
/// the fused kernel, no arithmetic.
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
            // Mirror pair: the whole m1 x m2 block is free (the dropped
            // partner path's (u2, u1) slot is what the constraint determines).
            | Some _ -> [ for u1 in 0 .. m1 - 1 do for u2 in 0 .. m2 - 1 -> (u1, u2) ]
            // Diagonal: the per-mo m x m block is τ-symmetric, so the free
            // cells are the triangle — inclusive of the diagonal at τ = +1,
            // strict at τ = −1 (which FORCES u1 = u2 to zero).
            | None -> [ for u1 in 0 .. m1 - 1 do for u2 in (if tau = 1 then u1 else u1 + 1) .. m1 - 1 -> (u1, u2) ]
        if not cells.IsEmpty then
            out.Add { Kept = kp; Tau = tau; PackedBase = packed; CellsPerMo = cells.Length; Cells = cells }
        packed <- packed + cells.Length * oA.[kp.BO].Mult
    List.ofSeq out

/// Packed slot count + dense embedding of one S₂ component, computed together
/// so the count and the table can never drift.
///
/// EMBED TABLE: `(denseSlot, packedSlot, sign)` with denseSlot in tpDecl's
/// layout `pWOff(p) + (mo*m1 + u1)*m2 + u2`. The kept slot itself takes +1
/// and its partner slot τ; every dense slot not forced to zero by the
/// constraint appears EXACTLY once, so `wd(denseSlot) = sign * w(packedSlot)`
/// over a zeroed buffer reconstructs the whole dense tensor. This is the
/// stage-1a oracle: `derive_tp(S, S, x, y, embed(w))` is what the compacted
/// kernels are value-pinned against, and it is the layout the corpus pins
/// hand-encode.
let private s2TpCompaction (comp: S2Component) (s: Spec) : int * (int * int * float) list =
    let cfg = selfTpConfig s
    let paths = tpPaths cfg |> List.toArray
    let sA = List.toArray s
    let oA = List.toArray cfg.SpecOut
    let multO = paths |> Array.map (fun (_, _, bo) -> oA.[bo].Mult)
    let m1s = paths |> Array.map (fun (b1, _, _) -> sA.[b1].Mult)
    let m2s = paths |> Array.map (fun (_, b2, _) -> sA.[b2].Mult)
    // tpDecl's pWOff, recomputed here rather than shared: the layout is the
    // contract, and this is the one place that must match it.
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
                // m1s/m2s are SWAPPED at the partner path p', so denseAt p'
                // mo u2 u1 = pWOff(p') + (mo*m2 + u2)*m1 + u1 as required.
                | Some p' -> entries.Add (denseAt p' mo u2 u1, slot, tauF)
                | None -> if u2 <> u1 then entries.Add (denseAt p mo u2 u1, slot, tauF))
        packed <- packed + sk.CellsPerMo * multO.[p]
    (packed, List.ofSeq entries)

/// Closed-form dimension of an S₂ component (§3.2's compaction rule): a
/// mirror pair's 2·multOut·m1·m2 dense parameters split evenly between the
/// two components, and a diagonal path's per-mo m×m block splits as
/// m(m+τ)/2 ⊕ m(m−τ)/2. Derived independently of the packed enumeration so
/// the two can be cross-checked (s2TpWeightDim).
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

/// Free-parameter count of one S₂ component, cross-checked: the packed
/// layout's slot count MUST equal the closed-form dimension. A mismatch is a
/// compiler bug (the enumeration and the counting rule disagree), not a user
/// error, so it fails loudly here rather than silently mis-sizing a buffer.
let private s2TpWeightDim (comp: S2Component) (s: Spec) : int =
    let packed, _ = s2TpCompaction comp s
    let closed = s2TpWeightDimClosed comp s
    if packed <> closed then
        failwithf "internal: S2 %s compaction of the self-TP enumerated %d packed slots but the closed form says %d (spec %A)"
            (match comp with S2Sym -> "symmetric" | S2Alt -> "antisymmetric") packed closed s
    packed

/// dim of the exchange-SYMMETRIC equivariant bilinear maps s × s -> tp_spec
/// s s = dim Hom_G(Sym²V, tp_spec) — the `derive_sym_tp` weight buffer.
let symTpWeightDim (s: Spec) : int = s2TpWeightDim S2Sym s

/// dim of the exchange-ANTISYMMETRIC maps = dim Hom_G(Λ²V, tp_spec) — the
/// `derive_alt_tp` weight buffer. Zero exactly when the whole hom-space is
/// symmetric (e.g. a single multiplicity-1 scalar block); §3.2's correction:
/// at multiplicity > 1 the Λ²A⊗Λ²U Cauchy term keeps it nonzero.
let altTpWeightDim (s: Spec) : int = s2TpWeightDim S2Alt s

/// The two components partition the dense parameter space:
/// symTpWeightDim s + altTpWeightDim s = tpWeightDim (selfTpConfig s).
/// Cheap; asserted where the compacted kernels are synthesized.
let s2TpSplitIsPartition (s: Spec) : bool =
    symTpWeightDim s + altTpWeightDim s = tpWeightDim (selfTpConfig s)

/// Embedding of the packed symmetric buffer into the dense `derive_tp`
/// coefficient layout: `(denseSlot, packedSlot, sign)` entries, see
/// s2TpCompaction. NOT a codegen input since stage 1b — the emitted kernel
/// reads `s2TpCells` and never materializes the dense buffer. This is the
/// injection `embed : W_sym ↪ W_dense` of §4(a): the machine-readable
/// statement of the storage-correctness claim the corpus value-pins by hand.
let symTpEmbedTable (s: Spec) : (int * int * float) list = snd (s2TpCompaction S2Sym s)

/// Embedding of the packed antisymmetric buffer — same shape, partner sign
/// negated relative to symTpEmbedTable.
let altTpEmbedTable (s: Spec) : (int * int * float) list = snd (s2TpCompaction S2Alt s)

/// One FUSED term pair of the arithmetically compacted self-TP kernel
/// (plan-transforms-as-types §7 stage 1b): one canonical multiplicity cell
/// (u1, u2) of one kept path, with the dropped dense contribution it absorbs
/// folded in as a second product.
///
/// The emitted arithmetic per cell, per `mo` in 0..MultO−1, per entry `t` of
/// the path's OWN CG table (c1, c2, c3, coef):
///
///   out[OutOff + mo*OutDim + c3] +=
///       (coef * w[WBase + mo*WStride])
///     * ( x[OffA + c1] * y[OffB + c2]  +  PairSign * (y[OffA + e2] * x[OffB + e1]) )
///
/// with (e1, e2) = (c2, c1) on a MIRROR cell and (c1, c2) on a DIAGONAL one —
/// the two ways the same pair swap shows up:
///
/// - MIRROR (b1 < b2): the dropped (b2, b1, bo) path's CG table is the kept
///   one transposed times σ (Wigner cross-block exchange identity, pinned in
///   ml/Tests_Wigner), and its determined weight slot carries τ, so the whole
///   dropped path collapses onto the kept path's entries with the single sign
///   στ — +1 for Sym², −1 for Λ², independent of the path. The CG components
///   swap between the two products; the multiplicity slots do not.
/// - DIAGONAL (b1 = b2): the CG table is shared (no transpose), and it is the
///   MULTIPLICITY indices that swap: the (u2, u1) dense cell is τ times the
///   (u1, u2) one. PairSign = τ, and 0.0 on the u1 = u2 cell (only reachable
///   at τ = +1) which is its own partner and so contributes a single term.
type S2TpCell = {
    /// index into `tpPaths (selfTpConfig s)` — names the CG table to bake
    Path: int
    /// mirror-pair cell (a dropped partner path folds in) vs diagonal cell
    IsMirror: bool
    /// offset into x/y of the (b1, u1) multiplicity slot
    OffA: int
    /// offset into x/y of the (b2, u2) multiplicity slot
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

/// The fused cell table of one S₂ component: the kept paths' free cells in
/// packed order (so `WBase` runs over exactly the packed buffer). Dropped
/// relative to the dense path list: every b1 > b2 mirror path, and every
/// τ = −1 diagonal path at multiplicity 1 — neither contributes arithmetic.
/// Σ MultO over the cells = the packed dimension (checked at synthesis).
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
        // στ on a mirror cell (the dropped path's CG sign times its weight
        // slot's sign), τ on a diagonal one.
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

/// Per OUTPUT block of `linear`: (input block index, out entry, in entry),
/// input resolved FIRST-MATCH by irrep (ml/Linear.findBlock semantics —
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
