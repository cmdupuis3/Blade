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

/// Packed layout + dense embedding of one S₂ component, computed together so
/// the count and the table can never drift.
///
/// PACKED LAYOUT (fixed surface contract — corpus pins hard-code it): kept
/// paths in `tpPaths` order; per kept path `mo` in 0..multOut−1; then a
/// mirror path contributes its full block (u1 outer, u2 inner, row-major) and
/// a diagonal path contributes u1 outer with u2 from u1 (τ = +1) or u1 + 1
/// (τ = −1).
///
/// EMBED TABLE: `(denseSlot, packedSlot, sign)` with denseSlot in tpDecl's
/// layout `pWOff(p) + (mo*m1 + u1)*m2 + u2`. The kept slot itself takes +1
/// and its partner slot τ; every dense slot not forced to zero by the
/// constraint appears EXACTLY once, so `wd(denseSlot) = sign * w(packedSlot)`
/// over a zeroed buffer reconstructs the whole dense tensor.
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
    for kp in symTpKeptPaths s do
        let p = kp.Idx
        let tau = transposeFactor comp kp.Sigma
        let tauF = float tau
        for mo in 0 .. multO.[p] - 1 do
            match kp.Mirror with
            | Some p' ->
                // Whole block free; the partner path's slot for (u2, u1) is
                // determined. m1s/m2s are SWAPPED at p', so denseAt p' mo u2
                // u1 = pWOff(p') + (mo*m2 + u2)*m1 + u1 as required.
                for u1 in 0 .. m1s.[p] - 1 do
                    for u2 in 0 .. m2s.[p] - 1 do
                        entries.Add (denseAt p mo u1 u2, packed, 1.0)
                        entries.Add (denseAt p' mo u2 u1, packed, tauF)
                        packed <- packed + 1
            | None ->
                let m = m1s.[p]
                for u1 in 0 .. m - 1 do
                    for u2 in (if tau = 1 then u1 else u1 + 1) .. m - 1 do
                        entries.Add (denseAt p mo u1 u2, packed, 1.0)
                        if u2 <> u1 then entries.Add (denseAt p mo u2 u1, packed, tauF)
                        packed <- packed + 1
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
/// s2TpCompaction.
let symTpEmbedTable (s: Spec) : (int * int * float) list = snd (s2TpCompaction S2Sym s)

/// Embedding of the packed antisymmetric buffer — same shape, partner sign
/// negated relative to symTpEmbedTable.
let altTpEmbedTable (s: Spec) : (int * int * float) list = snd (s2TpCompaction S2Alt s)

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
