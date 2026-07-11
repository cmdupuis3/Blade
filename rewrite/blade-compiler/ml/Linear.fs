namespace BladeML

/// Equivariant linear layer (ml-spec section 7): mixes multiplicities within
/// each irrep block, shared across m-components. Cross-irrep mixing would
/// break equivariance and is unrepresentable here by construction — the
/// weight layout (LinearWeightIdx) only has within-block matrices.
module Linear =

    /// First block of `spec` carrying exactly this irrep, if any.
    /// (Spec quirk, documented in ml/README.md: with duplicate irrep entries
    /// in spec_in, only the first is ever read — find_block_idx is
    /// first-match by definition.)
    let findBlock (spec: SpecEntry[]) (ir: Irrep) : int option =
        spec |> Array.tryFindIndex (fun e -> e.Ir = ir)

    /// Type-check from ml-spec section 11.2: every output irrep must exist
    /// in the input spec.
    let allIrrepsPresent (specIn: SpecEntry[]) (specOut: SpecEntry[]) : bool =
        specOut |> Array.forall (fun e -> (findBlock specIn e.Ir).IsSome)

    /// Weight offsets per output block; length = specOut.Length + 1.
    /// Block b holds a (multOut x multIn) matrix, row-major.
    let weightOffsets (specIn: SpecEntry[]) (specOut: SpecEntry[]) : int[] =
        let offs = Array.zeroCreate (specOut.Length + 1)
        for b in 0 .. specOut.Length - 1 do
            let e = specOut.[b]
            match findBlock specIn e.Ir with
            | None ->
                invalidArg "specOut" (sprintf "output irrep L=%d %A not present in input spec" e.Ir.L e.Ir.P)
            | Some bi ->
                offs.[b + 1] <- offs.[b] + e.Mult * specIn.[bi].Mult
        offs

    let weightDim (specIn: SpecEntry[]) (specOut: SpecEntry[]) : int =
        (weightOffsets specIn specOut).[specOut.Length]

    let linear (specIn: SpecEntry[]) (specOut: SpecEntry[]) (weights: float[]) (x: float[]) : float[] =
        if x.Length <> Irreps.totalDim specIn then
            invalidArg "x" "input length does not match specIn"
        let offs = weightOffsets specIn specOut
        if weights.Length <> offs.[specOut.Length] then
            invalidArg "weights" (sprintf "weight length %d, expected %d" weights.Length offs.[specOut.Length])
        let sIn = IrrepsIdx.blockStarts specIn
        let sOut = IrrepsIdx.blockStarts specOut
        let out = Array.zeroCreate (Irreps.totalDim specOut)
        for b in 0 .. specOut.Length - 1 do
            let eo = specOut.[b]
            let bi = (findBlock specIn eo.Ir).Value
            let ei = specIn.[bi]
            let d = Irreps.dim eo.Ir
            for muO in 0 .. eo.Mult - 1 do
                for muI in 0 .. ei.Mult - 1 do
                    let w = weights.[offs.[b] + muO * ei.Mult + muI]
                    if w <> 0.0 then
                        let src = sIn.[bi] + muI * d
                        let dst = sOut.[b] + muO * d
                        for c in 0 .. d - 1 do
                            out.[dst + c] <- out.[dst + c] + w * x.[src + c]
        out
