namespace BladeML

/// Reverse-mode building blocks (hand-written adjoints) for the equivariant
/// ML ops — the executable semantics that the Blade compiler's `grad`
/// transform must reproduce, and the value oracle its generated C++ is
/// differentially tested against (same doctrine as the forward ops vs the
/// v7 oracle harness).
///
/// Conventions:
///   - vjpOp takes the op's inputs plus the output cotangent dOut and
///     returns input cotangents in argument order ("vector-Jacobian
///     product"). Layouts match the forward functions exactly.
///   - Adjoints ACCUMULATE with += into zero-initialized buffers, mirroring
///     the accumulation-loop structure the compiler transform emits.
///   - CG coefficients and spherical-harmonic values of NON-differentiated
///     inputs (edge vectors: positions are data, not parameters, in v1) are
///     constants with zero derivative (module doc §11).
///
/// Structural facts the tests pin (and the compiler transform relies on):
///   - gather and scatter_add are each other's adjoints;
///   - the tensor-product backward runs the SAME sparse CG iteration as the
///     forward, once per requested cotangent;
///   - the forward's `w <> 0.0` skip is an output-value optimization that
///     the dW adjoint must NOT inherit (zero weights have nonzero
///     gradients).
///
/// normAct is deliberately not covered yet: the e2e example uses `gated`.
module Autodiff =

    // ---- scalar nonlinearity derivatives ----

    let sigmoidGrad (x: float) : float =
        let s = Activations.sigmoid x
        s * (1.0 - s)

    /// d/dx silu(x) = sigmoid(x) + x * sigmoid'(x)
    let siluGrad (x: float) : float =
        Activations.sigmoid x + x * sigmoidGrad x

    // ---- linear ----

    /// VJP of Linear.linear: returns (dWeights, dX).
    /// out[dst+c] += w * x[src+c]  ⇒
    ///   dW[b, muO, muI] += Σ_c x[src+c] * dOut[dst+c]
    ///   dX[src+c]      += w * dOut[dst+c]
    /// Duplicate-irrep quirk (F3): multiple OUTPUT blocks may read the same
    /// (first-match) input block; dX accumulates across all of them.
    let vjpLinear (specIn: SpecEntry[]) (specOut: SpecEntry[])
                  (weights: float[]) (x: float[]) (dOut: float[])
                  : float[] * float[] =
        if dOut.Length <> Irreps.totalDim specOut then
            invalidArg "dOut" "cotangent length does not match specOut"
        let offs = Linear.weightOffsets specIn specOut
        let sIn = IrrepsIdx.blockStarts specIn
        let sOut = IrrepsIdx.blockStarts specOut
        let dW = Array.zeroCreate weights.Length
        let dX = Array.zeroCreate x.Length
        for b in 0 .. specOut.Length - 1 do
            let eo = specOut.[b]
            let bi = (Linear.findBlock specIn eo.Ir).Value
            let ei = specIn.[bi]
            let d = Irreps.dim eo.Ir
            for muO in 0 .. eo.Mult - 1 do
                for muI in 0 .. ei.Mult - 1 do
                    let wi = offs.[b] + muO * ei.Mult + muI
                    let w = weights.[wi]
                    let src = sIn.[bi] + muI * d
                    let dst = sOut.[b] + muO * d
                    for c in 0 .. d - 1 do
                        dW.[wi] <- dW.[wi] + x.[src + c] * dOut.[dst + c]
                        dX.[src + c] <- dX.[src + c] + w * dOut.[dst + c]
        dW, dX

    /// VJP of Linear.homLinear (the complete Schur basis — derive_linear's
    /// reference): the same pair-major traversal, both cotangents.
    let vjpHomLinear (specIn: SpecEntry[]) (specOut: SpecEntry[])
                     (weights: float[]) (x: float[]) (dOut: float[])
                     : float[] * float[] =
        if dOut.Length <> Irreps.totalDim specOut then
            invalidArg "dOut" "cotangent length does not match specOut"
        let sIn = IrrepsIdx.blockStarts specIn
        let sOut = IrrepsIdx.blockStarts specOut
        let dW = Array.zeroCreate weights.Length
        let dX = Array.zeroCreate x.Length
        let mutable wOff = 0
        for (bi, bo) in Linear.homPairs specIn specOut do
            let eo = specOut.[bo]
            let ei = specIn.[bi]
            let d = Irreps.dim eo.Ir
            for muO in 0 .. eo.Mult - 1 do
                for muI in 0 .. ei.Mult - 1 do
                    let wi = wOff + muO * ei.Mult + muI
                    let w = weights.[wi]
                    let src = sIn.[bi] + muI * d
                    let dst = sOut.[bo] + muO * d
                    for c in 0 .. d - 1 do
                        dW.[wi] <- dW.[wi] + x.[src + c] * dOut.[dst + c]
                        dX.[src + c] <- dX.[src + c] + w * dOut.[dst + c]
            wOff <- wOff + eo.Mult * ei.Mult
        dW, dX

    /// VJP of Activations.norms: out_k = ||x_slot||  ⇒  dX_slot = dOut_k · x_slot / ||x_slot||
    /// (zero-norm slots get zero cotangent — the subgradient convention).
    let vjpNorms (spec: SpecEntry[]) (feat: float[]) (dOut: float[]) : float[] =
        let starts = IrrepsIdx.blockStarts spec
        let dX = Array.zeroCreate feat.Length
        let mutable k = 0
        for b in 0 .. spec.Length - 1 do
            let e = spec.[b]
            let d = 2 * e.Ir.L + 1
            for mu in 0 .. e.Mult - 1 do
                let s = starts.[b] + mu * d
                let mutable acc = 0.0
                for c in 0 .. d - 1 do
                    acc <- acc + feat.[s + c] * feat.[s + c]
                let nrm = sqrt acc
                if nrm > 0.0 then
                    for c in 0 .. d - 1 do
                        dX.[s + c] <- dX.[s + c] + dOut.[k] * feat.[s + c] / nrm
                k <- k + 1
        dX

    // ---- tensor product ----

    /// VJP of TensorProduct.tensorProduct: returns (dWeights, dX, dY).
    /// out[o0+c3] += cg * w * x[x0+c1] * y[y0+c2]  ⇒
    ///   dW  += cg * x * y * dOut     (no w<>0 skip here!)
    ///   dX  += cg * w * y * dOut
    ///   dY  += cg * w * x * dOut
    /// Same path/mult/sparse-CG iteration as the forward — this is the
    /// "gradients ride the same sparse iteration" claim, executable.
    let vjpTensorProduct (cfg: TPConfig) (weights: float[]) (x: float[]) (y: float[])
                         (dOut: float[])
                         : float[] * float[] * float[] =
        if dOut.Length <> Irreps.totalDim cfg.SpecOut then
            invalidArg "dOut" "cotangent length does not match SpecOut"
        let s1 = IrrepsIdx.blockStarts cfg.Spec1
        let s2 = IrrepsIdx.blockStarts cfg.Spec2
        let so = IrrepsIdx.blockStarts cfg.SpecOut
        let ps = TensorProduct.paths cfg
        let offs = TensorProduct.weightOffsets cfg
        let dW = Array.zeroCreate weights.Length
        let dX = Array.zeroCreate x.Length
        let dY = Array.zeroCreate y.Length
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
                        let wi = wbase + (muO * e1.Mult + mu1) * e2.Mult + mu2
                        let w = weights.[wi]
                        let x0 = s1.[p.B1] + mu1 * d1
                        let y0 = s2.[p.B2] + mu2 * d2
                        let o0 = so.[p.BOut] + muO * dO
                        for e in cg do
                            let g = dOut.[o0 + e.C3]
                            dW.[wi] <- dW.[wi] + e.Coef * x.[x0 + e.C1] * y.[y0 + e.C2] * g
                            dX.[x0 + e.C1] <- dX.[x0 + e.C1] + e.Coef * w * y.[y0 + e.C2] * g
                            dY.[y0 + e.C2] <- dY.[y0 + e.C2] + e.Coef * w * x.[x0 + e.C1] * g
        dW, dX, dY

    // ---- gated activation ----

    /// VJP of Activations.gated: returns dFeat.
    /// The block-0 scalars do double duty (F2): each receives BOTH its own
    /// silu' term (as a feature) and the accumulated sigmoid' term from
    /// every higher-L multiplicity it gates.
    let vjpGated (spec: SpecEntry[]) (feat: float[]) (dOut: float[]) : float[] =
        if feat.Length <> Irreps.totalDim spec then
            invalidArg "feat" "feature vector length does not match spec"
        if dOut.Length <> feat.Length then
            invalidArg "dOut" "cotangent length does not match spec"
        if spec.Length = 0 then Array.empty
        else
        if spec.[0].Ir.L <> 0 then
            invalidArg "spec" "gated activation requires the first block to be scalars (L=0)"
        let starts = IrrepsIdx.blockStarts spec
        let numGates = spec.[0].Mult
        let dFeat = Array.zeroCreate feat.Length
        for b in 0 .. spec.Length - 1 do
            let e = spec.[b]
            let l = e.Ir.L
            let d = 2 * l + 1
            if l = 0 then
                for mu in 0 .. e.Mult - 1 do
                    let i = starts.[b] + mu
                    dFeat.[i] <- dFeat.[i] + siluGrad feat.[i] * dOut.[i]
            else
                for mu in 0 .. e.Mult - 1 do
                    let gi = starts.[0] + (mu % numGates)
                    let g = Activations.sigmoid feat.[gi]
                    let mutable inner = 0.0
                    for c in 0 .. d - 1 do
                        let i = starts.[b] + mu * d + c
                        dFeat.[i] <- dFeat.[i] + g * dOut.[i]
                        inner <- inner + feat.[i] * dOut.[i]
                    dFeat.[gi] <- dFeat.[gi] + sigmoidGrad feat.[gi] * inner
        dFeat

    // ---- message passing ----

    /// VJP of MessagePassing.gather: dFeat = scatter_add(dOut, sources).
    /// (Adjoint duality: gatherᵀ = scatter_add.)
    let vjpGather (dOut: float[]) (featDim: int) (nRows: int) (sources: int[]) : float[] =
        MessagePassing.scatterAdd dOut featDim sources nRows

    /// VJP of MessagePassing.scatterAdd: dValues = gather(dOut, targets).
    /// (Adjoint duality: scatter_addᵀ = gather.)
    let vjpScatterAdd (dOut: float[]) (featDim: int) (nTargets: int) (targets: int[]) : float[] =
        MessagePassing.gather dOut featDim nTargets targets

    // ---- equivariant convolution (the §12 composition) ----

    /// VJP of Conv.equivariantConv wrt (weights, nodeFeat); edge vectors are
    /// data (their sh expansion is a constant). Recompute-based, mirroring
    /// the per-edge forward structure:
    ///   forward: msg_e = TP(xf_e, sh_e; W); out = scatter_add(msg, tgt)
    ///   reverse: dMsg_e = dOut row tgt_e (gather);
    ///            (dW += , dXf_e) = TP-VJP; dNodeFeat = scatter_add(dXf, src)
    let vjpEquivariantConv
        (specIn: SpecEntry[]) (specOut: SpecEntry[]) (lmaxSh: int)
        (nodeFeat: float[]) (nNodes: int)
        (edgeSrc: int[]) (edgeTgt: int[]) (edgeVecs: float[])
        (weights: float[]) (dOut: float[])
        : float[] * float[] =
        let specSh = Irreps.shSpec lmaxSh
        let cfg = { Spec1 = specIn; Spec2 = specSh; SpecOut = specOut }
        let dIn = Irreps.totalDim specIn
        let dO = Irreps.totalDim specOut
        let nEdges = edgeSrc.Length
        if dOut.Length <> nNodes * dO then
            invalidArg "dOut" "cotangent length does not match nNodes * totalDim specOut"
        let dW = Array.zeroCreate weights.Length
        let dNodeFeat = Array.zeroCreate nodeFeat.Length
        for e in 0 .. nEdges - 1 do
            let src = edgeSrc.[e]
            let tgt = edgeTgt.[e]
            let sh = SphericalHarmonics.yTo lmaxSh edgeVecs.[3 * e] edgeVecs.[3 * e + 1] edgeVecs.[3 * e + 2]
            let xf = Array.sub nodeFeat (src * dIn) dIn
            let dMsg = Array.sub dOut (tgt * dO) dO
            let dWe, dXf, _dSh = vjpTensorProduct cfg weights xf sh dMsg
            for i in 0 .. dW.Length - 1 do
                dW.[i] <- dW.[i] + dWe.[i]
            for i in 0 .. dIn - 1 do
                dNodeFeat.[src * dIn + i] <- dNodeFeat.[src * dIn + i] + dXf.[i]
        dW, dNodeFeat

    // ---- loss ----

    /// Mean squared error over a batch: (1/n) Σ (pred - target)².
    let mse (pred: float[]) (target: float[]) : float =
        if pred.Length <> target.Length then invalidArg "target" "length mismatch"
        let n = float pred.Length
        let mutable acc = 0.0
        for i in 0 .. pred.Length - 1 do
            let d = pred.[i] - target.[i]
            acc <- acc + d * d
        acc / n

    /// VJP of mse wrt pred: dPred[i] = 2/n * (pred[i] - target[i]) * dLoss.
    let vjpMse (pred: float[]) (target: float[]) (dLoss: float) : float[] =
        let n = float pred.Length
        Array.init pred.Length (fun i -> 2.0 / n * (pred.[i] - target.[i]) * dLoss)
