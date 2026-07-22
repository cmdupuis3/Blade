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
