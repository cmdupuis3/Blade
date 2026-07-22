namespace BladeML

/// Equivariant activation functions (ml-spec section 8).
///
/// Nonlinearities apply directly only to scalars (L=0). Higher-L blocks are
/// either gated by a sigmoid of a scalar feature (gated) or scaled by a
/// function of their norm (normAct). Both are exactly SO(3)-equivariant:
/// the gate/scale factors depend only on invariants.
///
/// O(3) caveat (documented in ml/README.md): under improper elements, gating
/// by sigmoid of an ODD scalar (L0o) breaks equivariance since sigmoid is
/// not odd. The spec implicitly assumes even scalars in block 0.
module Activations =

    let sigmoid (x: float) : float = 1.0 / (1.0 + exp (-x))
    let silu (x: float) : float = x * sigmoid x
    let relu (x: float) : float = max x 0.0

    /// Gated activation (ml-spec section 8.1). Block 0 must be scalars; its
    /// features double as the gates for higher-L blocks (gate for
    /// multiplicity mu is sigmoid(features(0, mu % num_gates, 0)) — the
    /// spec's exact rule, including the scalar double-duty).
    let gated (spec: SpecEntry[]) (feat: float[]) : float[] =
        if feat.Length <> Irreps.totalDim spec then
            invalidArg "feat" "feature vector length does not match spec"
        if spec.Length = 0 then Array.empty
        else
        if spec.[0].Ir.L <> 0 then
            invalidArg "spec" "gated activation requires the first block to be scalars (L=0)"
        let starts = IrrepsIdx.blockStarts spec
        let numGates = spec.[0].Mult
        let out = Array.zeroCreate feat.Length
        for b in 0 .. spec.Length - 1 do
            let e = spec.[b]
            let l = e.Ir.L
            let d = 2 * l + 1
            if l = 0 then
                for mu in 0 .. e.Mult - 1 do
                    out.[starts.[b] + mu] <- silu feat.[starts.[b] + mu]
            else
                for mu in 0 .. e.Mult - 1 do
                    let g = sigmoid feat.[starts.[0] + (mu % numGates)]
                    for c in 0 .. d - 1 do
                        let i = starts.[b] + mu * d + c
                        out.[i] <- g * feat.[i]
        out

    // ------------------------------------------------------------------
    // Invariant-exit ops (equiv-discipline surface, 2026-07-18): the two
    // sanctioned ways OUT of representation land inside a certified body.
    // The compiler elaborates ml.scalars / ml.norms with EXACTLY these loop
    // orders (ulp contract).
    // ------------------------------------------------------------------

    /// scalars(spec, x): copies the l=0 blocks' entries (all parities) into
    /// a plain array, block order then multiplicity order. Under equiv(O3)
    /// the judgment only admits calls on specs with no (l=0, odd) blocks —
    /// pseudoscalars are SO(3)- but not O(3)-invariant.
    let scalars (spec: SpecEntry[]) (feat: float[]) : float[] =
        if feat.Length <> Irreps.totalDim spec then
            invalidArg "feat" "feature vector length does not match spec"
        let starts = IrrepsIdx.blockStarts spec
        [| for b in 0 .. spec.Length - 1 do
             if spec.[b].Ir.L = 0 then
                 for mu in 0 .. spec.[b].Mult - 1 do
                     yield feat.[starts.[b] + mu] |]

    /// norms(spec, x): per-(block, multiplicity) 2-norms — sqrt of the sum
    /// of squares over the 2l+1 components, ascending component order, in
    /// (block, mu) order. O(3)-invariant for every parity (squares kill the
    /// parity sign).
    let norms (spec: SpecEntry[]) (feat: float[]) : float[] =
        if feat.Length <> Irreps.totalDim spec then
            invalidArg "feat" "feature vector length does not match spec"
        let starts = IrrepsIdx.blockStarts spec
        [| for b in 0 .. spec.Length - 1 do
             let e = spec.[b]
             let d = 2 * e.Ir.L + 1
             for mu in 0 .. e.Mult - 1 do
                 let s = starts.[b] + mu * d
                 let mutable acc = 0.0
                 for c in 0 .. d - 1 do
                     acc <- acc + feat.[s + c] * feat.[s + c]
                 yield sqrt acc |]

    /// Norm activation (ml-spec section 8.2): higher-L copies are scaled by
    /// silu(||v||) / (||v|| + 1e-8); scalars get silu directly.
    let normAct (spec: SpecEntry[]) (feat: float[]) : float[] =
        if feat.Length <> Irreps.totalDim spec then
            invalidArg "feat" "feature vector length does not match spec"
        let starts = IrrepsIdx.blockStarts spec
        let out = Array.zeroCreate feat.Length
        for b in 0 .. spec.Length - 1 do
            let e = spec.[b]
            let l = e.Ir.L
            let d = 2 * l + 1
            if l = 0 then
                for mu in 0 .. e.Mult - 1 do
                    out.[starts.[b] + mu] <- silu feat.[starts.[b] + mu]
            else
                for mu in 0 .. e.Mult - 1 do
                    let s = starts.[b] + mu * d
                    let mutable normSq = 0.0
                    for c in 0 .. d - 1 do
                        normSq <- normSq + feat.[s + c] * feat.[s + c]
                    let nrm = sqrt normSq
                    let scale = silu nrm / (nrm + 1e-8)
                    for c in 0 .. d - 1 do
                        out.[s + c] <- feat.[s + c] * scale
        out
