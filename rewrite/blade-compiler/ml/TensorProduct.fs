namespace BladeML

/// The equivariant tensor product (ml-spec section 5).
///
/// Paths: all (block1, block2, blockOut) triples satisfying the CG selection
/// rules — |l1-l2| <= l_out <= l1+l2 and p_out = p1*p2. This is the
/// TensorPaths<cfg> SparseIdx of the spec, enumerated in lexicographic
/// (b1, b2, bOut) order.
///
/// Weights: DepIdx over paths, inner shape (mult_out, mult1, mult2) per the
/// spec's WeightIdx<cfg> ("uvw" fully-connected mode in e3nn terms), stored
/// flattened path-major, mult_out-major within a path.
///
/// No normalization is applied (the spec does not define one; e3nn applies
/// path normalization — noted in ml/README.md).
type TPConfig =
    { Spec1: SpecEntry[]
      Spec2: SpecEntry[]
      SpecOut: SpecEntry[] }

type TPPath = { B1: int; B2: int; BOut: int }

module TensorProduct =

    /// Single-source CG selection rules: the enumerator lives in
    /// Blade.ML.Spec.tpPaths (the compiler's static model — the same source
    /// file is compiled into this project); the reference impl delegates and
    /// maps the triples back to TPPath. Parity maps Even -> 0 / Odd -> 1;
    /// both sides are lexicographic in (B1, B2, BOut).
    let private toSpecEntry (e: SpecEntry) : Blade.ML.Spec.SpecEntry =
        { L = e.Ir.L
          Parity = (match e.Ir.P with Even -> 0 | Odd -> 1)
          Mult = e.Mult }

    let private toSpecCfg (cfg: TPConfig) : Blade.ML.Spec.TPConfig =
        { Spec1 = cfg.Spec1 |> Array.toList |> List.map toSpecEntry
          Spec2 = cfg.Spec2 |> Array.toList |> List.map toSpecEntry
          SpecOut = cfg.SpecOut |> Array.toList |> List.map toSpecEntry }

    /// All valid paths for a config, lexicographic in (B1, B2, BOut).
    let paths (cfg: TPConfig) : TPPath[] =
        Blade.ML.Spec.tpPaths (toSpecCfg cfg)
        |> List.map (fun (b1, b2, bo) -> { B1 = b1; B2 = b2; BOut = bo })
        |> Array.ofList

    /// Type-check from ml-spec section 11.1: every output block must be
    /// reachable from some input block pair.
    let allValidOutputs (cfg: TPConfig) : bool =
        Blade.ML.Spec.allValidOutputs (toSpecCfg cfg)

    let pathWeightCount (cfg: TPConfig) (p: TPPath) : int =
        cfg.SpecOut.[p.BOut].Mult * cfg.Spec1.[p.B1].Mult * cfg.Spec2.[p.B2].Mult

    /// Weight offsets per path; length = paths+1, last entry = weightDim.
    let weightOffsets (cfg: TPConfig) : int[] =
        let ps = paths cfg
        let offs = Array.zeroCreate (ps.Length + 1)
        for i in 0 .. ps.Length - 1 do
            offs.[i + 1] <- offs.[i] + pathWeightCount cfg ps.[i]
        offs

    let weightDim (cfg: TPConfig) : int =
        (weightOffsets cfg).[(paths cfg).Length]

    let private validate (cfg: TPConfig) (weights: float[]) (x: float[]) (y: float[]) =
        if not (allValidOutputs cfg) then
            invalidArg "cfg" "tensor_product: some output irrep is unreachable (all_valid_outputs fails)"
        if weights.Length <> weightDim cfg then
            invalidArg "weights" (sprintf "weight length %d, expected %d" weights.Length (weightDim cfg))
        if x.Length <> Irreps.totalDim cfg.Spec1 then
            invalidArg "x" "input 1 length does not match spec1"
        if y.Length <> Irreps.totalDim cfg.Spec2 then
            invalidArg "y" "input 2 length does not match spec2"

    /// Sparse implementation: per path, iterate multiplicities and only the
    /// nonzero support of the real CG tensor (the CGIndex iteration).
    let tensorProduct (cfg: TPConfig) (weights: float[]) (x: float[]) (y: float[]) : float[] =
        validate cfg weights x y
        let s1 = IrrepsIdx.blockStarts cfg.Spec1
        let s2 = IrrepsIdx.blockStarts cfg.Spec2
        let so = IrrepsIdx.blockStarts cfg.SpecOut
        let ps = paths cfg
        let offs = weightOffsets cfg
        let out = Array.zeroCreate (Irreps.totalDim cfg.SpecOut)
        for pi in 0 .. ps.Length - 1 do
            let p = ps.[pi]
            let e1 = cfg.Spec1.[p.B1]
            let e2 = cfg.Spec2.[p.B2]
            let eo = cfg.SpecOut.[p.BOut]
            let d1 = Irreps.dim e1.Ir
            let d2 = Irreps.dim e2.Ir
            let dO = Irreps.dim eo.Ir
            let cg = Wigner.realCGSparse e1.Ir.L e2.Ir.L eo.Ir.L
            let wbase = offs.[pi]
            for muO in 0 .. eo.Mult - 1 do
                for mu1 in 0 .. e1.Mult - 1 do
                    for mu2 in 0 .. e2.Mult - 1 do
                        let w = weights.[wbase + (muO * e1.Mult + mu1) * e2.Mult + mu2]
                        if w <> 0.0 then
                            let x0 = s1.[p.B1] + mu1 * d1
                            let y0 = s2.[p.B2] + mu2 * d2
                            let o0 = so.[p.BOut] + muO * dO
                            for e in cg do
                                out.[o0 + e.C3] <- out.[o0 + e.C3]
                                                   + e.Coef * w * x.[x0 + e.C1] * y.[y0 + e.C2]
        out

    /// Dense reference implementation: identical semantics, but iterates the
    /// full (2l1+1)(2l2+1)(2l3+1) box with the dense coupling tensor.
    /// Exists purely as a differential oracle for the sparse version.
    let tensorProductDense (cfg: TPConfig) (weights: float[]) (x: float[]) (y: float[]) : float[] =
        validate cfg weights x y
        let s1 = IrrepsIdx.blockStarts cfg.Spec1
        let s2 = IrrepsIdx.blockStarts cfg.Spec2
        let so = IrrepsIdx.blockStarts cfg.SpecOut
        let ps = paths cfg
        let offs = weightOffsets cfg
        let out = Array.zeroCreate (Irreps.totalDim cfg.SpecOut)
        for pi in 0 .. ps.Length - 1 do
            let p = ps.[pi]
            let e1 = cfg.Spec1.[p.B1]
            let e2 = cfg.Spec2.[p.B2]
            let eo = cfg.SpecOut.[p.BOut]
            let d1 = Irreps.dim e1.Ir
            let d2 = Irreps.dim e2.Ir
            let dO = Irreps.dim eo.Ir
            let cg = Wigner.realCGDense e1.Ir.L e2.Ir.L eo.Ir.L
            let wbase = offs.[pi]
            for muO in 0 .. eo.Mult - 1 do
                for mu1 in 0 .. e1.Mult - 1 do
                    for mu2 in 0 .. e2.Mult - 1 do
                        let w = weights.[wbase + (muO * e1.Mult + mu1) * e2.Mult + mu2]
                        for c1 in 0 .. d1 - 1 do
                            for c2 in 0 .. d2 - 1 do
                                for c3 in 0 .. dO - 1 do
                                    out.[so.[p.BOut] + muO * dO + c3] <-
                                        out.[so.[p.BOut] + muO * dO + c3]
                                        + cg.[c1].[c2].[c3] * w
                                          * x.[s1.[p.B1] + mu1 * d1 + c1]
                                          * y.[s2.[p.B2] + mu2 * d2 + c2]
        out
