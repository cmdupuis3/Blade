namespace BladeML

open System

/// The end-to-end training oracle: a tiny E(3)-equivariant network trained by
/// full-batch gradient descent on a rotation-invariant regression task. This
/// module IS the specification of the Blade e2e example (docs
/// features/equivariant-nn.md §10 composition + AD): the .blade program
/// replicates this computation with the same data (baked as literals), the
/// same iteration order, and pins its loss trajectory / gradient snapshots
/// against these values.
///
/// Task: predict  y = Σ_{i<j} exp(-|r_i - r_j|²)  from a point cloud — a
/// smooth rotation/translation-invariant function of the geometry.
///
/// Architecture (specs kept minimal but exercising every op):
///   specIn  = [(0e,1)]                       node scalar
///   sh      = Y_to<2>  → [(0e,1),(1o,1),(2e,1)]
///   conv1   : specIn ⊗ sh → specH = [(0e,2),(1o,2),(2e,1)]   (w1, 5 params)
///   gate1   : gated activation on specH
///   lin     : specH → specH block-diagonal                    (w2, 9 params)
///   conv2   : specH ⊗ sh → specOut = [(0e,2)]                 (w3, 10 params)
///   gate2   : gated (all-scalar spec ⇒ silu)
///   readout : pred = Σ_nodes (wr · node) + br                 (wr, 2 + 1 params)
///   loss    : MSE over graphs
///
/// 27 parameters total. Graphs are complete digraphs on 5 nodes (20 edges),
/// edge vector = pos(src) - pos(tgt).
module TrainingOracle =

    let specIn = Irreps.mkSpec [ (0, Even, 1) ]
    let specH = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 2); (2, Even, 1) ]
    let specOut = Irreps.mkSpec [ (0, Even, 2) ]
    let lmaxSh = 2
    let specSh = Irreps.shSpec lmaxSh

    let cfg1 : TPConfig = { Spec1 = specIn; Spec2 = specSh; SpecOut = specH }
    let cfg2 : TPConfig = { Spec1 = specH; Spec2 = specSh; SpecOut = specOut }

    let dIn = Irreps.totalDim specIn      // 1
    let dH = Irreps.totalDim specH        // 13
    let dOut = Irreps.totalDim specOut    // 2

    let w1Dim = TensorProduct.weightDim cfg1                  // 5
    let w2Dim = Linear.weightDim specH specH                  // 9
    let w3Dim = TensorProduct.weightDim cfg2                  // 10
    let wrDim = dOut                                          // 2

    // ---- fixed dataset ----

    let nNodes = 5
    let nGraphs = 4

    /// Complete digraph on nNodes (i ≠ j), source-major order.
    let edgeSrc, edgeTgt =
        let s = ResizeArray<int>()
        let t = ResizeArray<int>()
        for i in 0 .. nNodes - 1 do
            for j in 0 .. nNodes - 1 do
                if i <> j then
                    s.Add i
                    t.Add j
        s.ToArray(), t.ToArray()

    let nEdges = edgeSrc.Length           // 20

    type Graph =
        { Pos: float[]                    // nNodes*3, row-major
          NodeFeat: float[]               // nNodes*dIn
          Target: float }

    /// y = Σ_{i<j} exp(-|r_i - r_j|²)
    let invariantTarget (pos: float[]) : float =
        let mutable acc = 0.0
        for i in 0 .. nNodes - 1 do
            for j in i + 1 .. nNodes - 1 do
                let dx = pos.[3 * i] - pos.[3 * j]
                let dy = pos.[3 * i + 1] - pos.[3 * j + 1]
                let dz = pos.[3 * i + 2] - pos.[3 * j + 2]
                acc <- acc + exp (-(dx * dx + dy * dy + dz * dz))
        acc

    /// Deterministic dataset + init weights (seed 20260711). The Blade
    /// example bakes these exact values as source literals via `dump`.
    /// Returns (graphs, w1, w2, w3, wr, br) — br is the readout bias,
    /// initialized to 0.
    let mkDataset () : Graph[] * float[] * float[] * float[] * float[] * float =
        let rng = Random(20260711)
        let graphs =
            Array.init nGraphs (fun _ ->
                let pos = Array.init (nNodes * 3) (fun _ -> 2.0 * rng.NextDouble() - 1.0)
                let feat = Array.init (nNodes * dIn) (fun _ -> 2.0 * rng.NextDouble() - 1.0)
                { Pos = pos; NodeFeat = feat; Target = invariantTarget pos })
        let initW n = Array.init n (fun _ -> rng.NextDouble() - 0.5)
        graphs, initW w1Dim, initW w2Dim, initW w3Dim, initW wrDim, 0.0

    /// Edge vectors for a graph: edge e points src → tgt as pos(src)-pos(tgt).
    let edgeVecs (pos: float[]) : float[] =
        let ev = Array.zeroCreate (nEdges * 3)
        for e in 0 .. nEdges - 1 do
            let s = edgeSrc.[e]
            let t = edgeTgt.[e]
            for c in 0 .. 2 do
                ev.[3 * e + c] <- pos.[3 * s + c] - pos.[3 * t + c]
        ev

    // ---- forward pass (intermediates kept for the reverse sweep) ----

    type ForwardTrace =
        { F1: float[]                     // conv1 out, nNodes*dH (pre-gate1)
          G1: float[]                     // gate1 out, nNodes*dH
          H: float[]                      // linear out, nNodes*dH (pre-conv2)
          F2: float[]                     // conv2 out, nNodes*dOut (pre-gate2)
          G2: float[]                     // gate2 out, nNodes*dOut
          Pred: float }

    let private mapRows (n: int) (d: int) (f: float[] -> float[]) (x: float[]) : float[] =
        let out = Array.zeroCreate (n * d)
        for i in 0 .. n - 1 do
            let row = f (Array.sub x (i * d) d)
            Array.blit row 0 out (i * d) d
        out

    let forwardGraph (g: Graph) (w1: float[]) (w2: float[]) (w3: float[]) (wr: float[]) (br: float) : ForwardTrace =
        let ev = edgeVecs g.Pos
        let f1 = Conv.equivariantConv specIn specH lmaxSh g.NodeFeat nNodes edgeSrc edgeTgt ev w1
        let g1 = mapRows nNodes dH (Activations.gated specH) f1
        let h = mapRows nNodes dH (Linear.linear specH specH w2) g1
        let f2 = Conv.equivariantConv specH specOut lmaxSh h nNodes edgeSrc edgeTgt ev w3
        let g2 = mapRows nNodes dOut (Activations.gated specOut) f2
        let mutable pred = br
        for i in 0 .. nNodes - 1 do
            for c in 0 .. dOut - 1 do
                pred <- pred + wr.[c] * g2.[i * dOut + c]
        { F1 = f1; G1 = g1; H = h; F2 = f2; G2 = g2; Pred = pred }

    // ---- loss + full-batch gradients (the reverse sweep) ----

    type Grads =
        { DW1: float[]; DW2: float[]; DW3: float[]; DWr: float[]; DBr: float }

    let lossAndGrads (graphs: Graph[]) (w1: float[]) (w2: float[]) (w3: float[]) (wr: float[]) (br: float)
        : float * Grads =
        let traces = graphs |> Array.map (fun g -> forwardGraph g w1 w2 w3 wr br)
        let preds = traces |> Array.map (fun t -> t.Pred)
        let targets = graphs |> Array.map (fun g -> g.Target)
        let loss = Autodiff.mse preds targets
        let dPred = Autodiff.vjpMse preds targets 1.0

        let dW1 = Array.zeroCreate w1.Length
        let dW2 = Array.zeroCreate w2.Length
        let dW3 = Array.zeroCreate w3.Length
        let dWr = Array.zeroCreate wr.Length
        let mutable dBr = 0.0

        // Cotangent naming: cX = cotangent of forward intermediate X.
        for s in 0 .. graphs.Length - 1 do
            let g = graphs.[s]
            let t = traces.[s]
            let ev = edgeVecs g.Pos
            // readoutᵀ: pred = Σ_i wr · G2_i + br
            dBr <- dBr + dPred.[s]
            let cG2 = Array.zeroCreate (nNodes * dOut)
            for i in 0 .. nNodes - 1 do
                for c in 0 .. dOut - 1 do
                    dWr.[c] <- dWr.[c] + t.G2.[i * dOut + c] * dPred.[s]
                    cG2.[i * dOut + c] <- wr.[c] * dPred.[s]
            // gate2ᵀ (per node, needs pre-activation F2)
            let cF2 = Array.zeroCreate (nNodes * dOut)
            for i in 0 .. nNodes - 1 do
                let d = Autodiff.vjpGated specOut (Array.sub t.F2 (i * dOut) dOut)
                                                  (Array.sub cG2 (i * dOut) dOut)
                Array.blit d 0 cF2 (i * dOut) dOut
            // conv2ᵀ → accumulates dW3, hands back the cotangent of H
            let dW3s, cH = Autodiff.vjpEquivariantConv specH specOut lmaxSh t.H nNodes edgeSrc edgeTgt ev w3 cF2
            for i in 0 .. dW3.Length - 1 do dW3.[i] <- dW3.[i] + dW3s.[i]
            // linᵀ (per node)
            let cG1 = Array.zeroCreate (nNodes * dH)
            for i in 0 .. nNodes - 1 do
                let dW2s, cRow = Autodiff.vjpLinear specH specH w2 (Array.sub t.G1 (i * dH) dH)
                                                                   (Array.sub cH (i * dH) dH)
                for k in 0 .. dW2.Length - 1 do dW2.[k] <- dW2.[k] + dW2s.[k]
                Array.blit cRow 0 cG1 (i * dH) dH
            // gate1ᵀ (per node, pre-activation F1)
            let cF1 = Array.zeroCreate (nNodes * dH)
            for i in 0 .. nNodes - 1 do
                let d = Autodiff.vjpGated specH (Array.sub t.F1 (i * dH) dH)
                                                (Array.sub cG1 (i * dH) dH)
                Array.blit d 0 cF1 (i * dH) dH
            // conv1ᵀ → dW1 (node features are data; their cotangent is dropped)
            let dW1s, _cFeat = Autodiff.vjpEquivariantConv specIn specH lmaxSh g.NodeFeat nNodes edgeSrc edgeTgt ev w1 cF1
            for i in 0 .. dW1.Length - 1 do dW1.[i] <- dW1.[i] + dW1s.[i]

        loss, { DW1 = dW1; DW2 = dW2; DW3 = dW3; DWr = dWr; DBr = dBr }

    // ---- training ----

    type TrainResult =
        { LossTrajectory: float[]         // loss BEFORE each step, then final: length steps+1
          Step0Grads: Grads               // gradients at the initial weights
          FinalW1: float[]; FinalW2: float[]; FinalW3: float[]; FinalWr: float[]
          FinalBr: float }

    let learningRate = 0.1
    let trainSteps = 30

    /// Full-batch gradient descent from the seeded init. Deterministic.
    let train () : TrainResult =
        let graphs, w1, w2, w3, wr, br0 = mkDataset ()
        let w1 = Array.copy w1
        let w2 = Array.copy w2
        let w3 = Array.copy w3
        let wr = Array.copy wr
        let mutable br = br0
        let traj = ResizeArray<float>()
        let mutable step0 : Grads option = None
        for _step in 1 .. trainSteps do
            let loss, grads = lossAndGrads graphs w1 w2 w3 wr br
            traj.Add loss
            if step0.IsNone then step0 <- Some grads
            let upd (w: float[]) (dw: float[]) =
                for i in 0 .. w.Length - 1 do
                    w.[i] <- w.[i] - learningRate * dw.[i]
            upd w1 grads.DW1
            upd w2 grads.DW2
            upd w3 grads.DW3
            upd wr grads.DWr
            br <- br - learningRate * grads.DBr
        let finalLoss, _ = lossAndGrads graphs w1 w2 w3 wr br
        traj.Add finalLoss
        { LossTrajectory = traj.ToArray()
          Step0Grads = step0.Value
          FinalW1 = w1; FinalW2 = w2; FinalW3 = w3; FinalWr = wr
          FinalBr = br }
