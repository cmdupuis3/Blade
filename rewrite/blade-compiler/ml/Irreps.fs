namespace BladeML

/// Parity under spatial inversion (ml-spec section 2.1).
type Parity =
    | Even
    | Odd

/// An irrep of O(3), parameterized by angular momentum L and parity p.
/// Dimension is 2L+1. In Blade this is `Irrep<L, p>` with named aliases
/// L0e, L1o, ... (ml-spec section 2.1).
type Irrep = { L: int; P: Parity }

/// One entry of an irreps specification: an irrep with a multiplicity.
/// A spec is an ordered array of entries, e.g. [(L0e,16); (L1o,8); (L2e,4)]
/// meaning 16 scalars, 8 vectors, 4 rank-2 tensors (ml-spec section 2.2).
type SpecEntry = { Ir: Irrep; Mult: int }

module Irreps =

    /// parity_mul (ml-spec section 2.3): Even is the identity of Z_2.
    let parityMul (p1: Parity) (p2: Parity) : Parity =
        if p1 = p2 then Even else Odd

    let paritySign (p: Parity) : int =
        match p with
        | Even -> 1
        | Odd -> -1

    /// Parity of the degree-l spherical harmonic: (-1)^l.
    let shParity (l: int) : Parity =
        if l % 2 = 0 then Even else Odd

    let irrep (l: int) (p: Parity) : Irrep =
        if l < 0 then invalidArg "l" "angular momentum must be >= 0"
        { L = l; P = p }

    // Named irreps (ml-spec section 2.1).
    let L0e = irrep 0 Even
    let L0o = irrep 0 Odd
    let L1e = irrep 1 Even
    let L1o = irrep 1 Odd
    let L2e = irrep 2 Even
    let L2o = irrep 2 Odd
    let L3e = irrep 3 Even
    let L3o = irrep 3 Odd

    /// dim(Irrep<L, p>) = 2L + 1.
    let dim (ir: Irrep) : int = 2 * ir.L + 1

    /// Build a spec from (l, parity, multiplicity) triples, validating.
    let mkSpec (entries: (int * Parity * int) list) : SpecEntry[] =
        entries
        |> List.map (fun (l, p, m) ->
            if m < 1 then invalidArg "entries" "multiplicity must be >= 1"
            { Ir = irrep l p; Mult = m })
        |> List.toArray

    /// block_dim(entry) = mult * dim(irrep).
    let blockDim (e: SpecEntry) : int = e.Mult * dim e.Ir

    /// total_dim(spec) = sum of block dims.
    let totalDim (spec: SpecEntry[]) : int =
        spec |> Array.sumBy blockDim

    /// sh_spec<L_max>: one copy of each degree 0..L_max with the natural
    /// spherical-harmonic parity (-1)^l: [(L0e,1); (L1o,1); (L2e,1); ...].
    let shSpec (lmax: int) : SpecEntry[] =
        if lmax < 0 then invalidArg "lmax" "lmax must be >= 0"
        Array.init (lmax + 1) (fun l -> { Ir = irrep l (shParity l); Mult = 1 })

    /// Full Clebsch-Gordan decomposition of s1 ⊗ s2, merged-canonical:
    /// every entry pair contributes l ∈ [|l1-l2| .. l1+l2] with parity
    /// p1·p2 and multiplicity m1·m2; contributions aggregate by (l, parity)
    /// and sort ascending by (l, parity) (Even before Odd at equal l).
    /// Independent twin of the compiler's Blade.ML.Spec.tpSpec — the two
    /// are pinned against the same hand-computed truth, never each other's
    /// output. Completeness: totalDim (tpSpec a b) = totalDim a * totalDim b.
    let tpSpec (s1: SpecEntry[]) (s2: SpecEntry[]) : SpecEntry[] =
        [| for e1 in s1 do
             for e2 in s2 do
               for l in abs (e1.Ir.L - e2.Ir.L) .. e1.Ir.L + e2.Ir.L ->
                 (l, parityMul e1.Ir.P e2.Ir.P), e1.Mult * e2.Mult |]
        |> Array.groupBy fst
        |> Array.map (fun ((l, p), cs) -> { Ir = irrep l p; Mult = cs |> Array.sumBy snd })
        |> Array.sortBy (fun e -> (e.Ir.L, e.Ir.P))

    /// dim Hom_G(V_in, V_out) by Schur's lemma: Σ_{(l,p)} multIn·multOut
    /// over aggregated multiplicities (duplicate spec entries of the same
    /// irrep pool together — unlike Linear.findBlock's first-match rule).
    /// homDim = 0 ⇔ the only equivariant linear map is zero.
    let homDim (sIn: SpecEntry[]) (sOut: SpecEntry[]) : int =
        let agg (s: SpecEntry[]) =
            s |> Array.fold (fun m e ->
                let k = (e.Ir.L, e.Ir.P)
                Map.add k (e.Mult + (Map.tryFind k m |> Option.defaultValue 0)) m) Map.empty
        let aIn = agg sIn
        agg sOut |> Map.fold (fun acc k mOut ->
            acc + mOut * (Map.tryFind k aIn |> Option.defaultValue 0)) 0
