namespace BladeML

/// Message-passing primitives (ml-spec section 9) and the complete
/// equivariant convolution example (ml-spec section 12).
///
/// Feature matrices are flat row-major float[] with a stride: row i of an
/// (n x featDim) matrix lives at [i*featDim, (i+1)*featDim).
module MessagePassing =

    /// gather (one-to-one): out row e = features row sources[e].
    let gather (features: float[]) (featDim: int) (nRows: int) (sources: int[]) : float[] =
        if features.Length <> nRows * featDim then
            invalidArg "features" "features length does not match nRows * featDim"
        let out = Array.zeroCreate (sources.Length * featDim)
        for e in 0 .. sources.Length - 1 do
            let src = sources.[e]
            if src < 0 || src >= nRows then
                invalidArg "sources" (sprintf "source index %d out of range [0, %d)" src nRows)
            System.Array.Copy(features, src * featDim, out, e * featDim, featDim)
        out

    /// scatter_add (many-to-one): out row targets[e] += values row e.
    let scatterAdd (values: float[]) (featDim: int) (targets: int[]) (nTargets: int) : float[] =
        if values.Length <> targets.Length * featDim then
            invalidArg "values" "values length does not match targets.Length * featDim"
        let out = Array.zeroCreate (nTargets * featDim)
        for e in 0 .. targets.Length - 1 do
            let tgt = targets.[e]
            if tgt < 0 || tgt >= nTargets then
                invalidArg "targets" (sprintf "target index %d out of range [0, %d)" tgt nTargets)
            for c in 0 .. featDim - 1 do
                out.[tgt * featDim + c] <- out.[tgt * featDim + c] + values.[e * featDim + c]
        out

module Conv =

    /// Equivariant convolution (ml-spec section 12): per edge, gather source
    /// features, expand the edge vector in spherical harmonics up to lmaxSh,
    /// tensor-product them into a message, scatter-add onto targets.
    ///
    /// nodeFeat: (nNodes x totalDim specIn) row-major.
    /// edgeVecs: (E x 3) row-major.
    /// Returns (nNodes x totalDim specOut) row-major.
    let equivariantConv
        (specIn: SpecEntry[]) (specOut: SpecEntry[]) (lmaxSh: int)
        (nodeFeat: float[]) (nNodes: int)
        (edgeSrc: int[]) (edgeTgt: int[]) (edgeVecs: float[])
        (weights: float[]) : float[] =

        let specSh = Irreps.shSpec lmaxSh
        let cfg = { Spec1 = specIn; Spec2 = specSh; SpecOut = specOut }
        let dIn = Irreps.totalDim specIn
        let dOut = Irreps.totalDim specOut
        let nEdges = edgeSrc.Length
        if edgeTgt.Length <> nEdges || edgeVecs.Length <> nEdges * 3 then
            invalidArg "edgeTgt" "edge arrays must agree on edge count"
        if nodeFeat.Length <> nNodes * dIn then
            invalidArg "nodeFeat" "node feature length does not match nNodes * totalDim specIn"
        let out = Array.zeroCreate (nNodes * dOut)
        for e in 0 .. nEdges - 1 do
            let src = edgeSrc.[e]
            let tgt = edgeTgt.[e]
            let sh = SphericalHarmonics.yTo lmaxSh edgeVecs.[3 * e] edgeVecs.[3 * e + 1] edgeVecs.[3 * e + 2]
            let xf = Array.sub nodeFeat (src * dIn) dIn
            let msg = TensorProduct.tensorProduct cfg weights xf sh
            for i in 0 .. dOut - 1 do
                out.[tgt * dOut + i] <- out.[tgt * dOut + i] + msg.[i]
        out
