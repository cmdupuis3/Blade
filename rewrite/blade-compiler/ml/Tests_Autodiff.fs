namespace BladeML

open System

/// Verification of the hand-written adjoints (Autodiff.fs) and the training
/// oracle (TrainingOracle.fs). The differential-oracle stance, applied to
/// gradients — independent routes must agree:
///
///   1. Finite differences: for random perturbation dx and cotangent u,
///      ⟨u, (f(x+εdx) - f(x-εdx)) / 2ε⟩  ≈  ⟨vjp_x(u), dx⟩.
///      Checked per op AND for the full 26-parameter model loss.
///   2. Adjoint duality: ⟨scatter_add(v), u⟩ = ⟨v, gather(u)⟩ exactly
///      (they are each other's transpose — the structural fact the compiler
///      transform will rely on).
///   3. The w=0 trap: the forward's `w <> 0.0` skip must not leak into dW
///      (zero weights have nonzero gradients).
///   4. Gradient equivariance: rotating a graph's positions leaves the loss
///      AND every weight gradient unchanged (invariant readout ⇒ invariant
///      loss surface ⇒ invariant gradients).
///   5. Training sanity: the fixed-seed run's loss decreases substantially.
module Tests_Autodiff =

    let private rng = Random(424242)

    let private randArray (n: int) : float[] =
        Array.init n (fun _ -> 2.0 * rng.NextDouble() - 1.0)

    let private dot (a: float[]) (b: float[]) : float =
        Array.fold2 (fun acc x y -> acc + x * y) 0.0 a b

    /// Central-difference directional derivative of f at x along dx,
    /// contracted with u: ⟨u, Df(x)[dx]⟩.
    let private fdDirectional (f: float[] -> float[]) (x: float[]) (dx: float[]) (u: float[]) : float =
        let eps = 1e-5
        let xp = Array.mapi (fun i v -> v + eps * dx.[i]) x
        let xm = Array.mapi (fun i v -> v - eps * dx.[i]) x
        let fp = f xp
        let fm = f xm
        let mutable acc = 0.0
        for i in 0 .. fp.Length - 1 do
            acc <- acc + u.[i] * (fp.[i] - fm.[i]) / (2.0 * eps)
        acc

    let private checkVjpAgainstFd (name: string) (f: float[] -> float[])
                                  (x: float[]) (vjpAtX: float[] -> float[]) =
        let u = randArray ((f x).Length)
        let dx = randArray x.Length
        let fd = fdDirectional f x dx u
        let an = dot (vjpAtX u) dx
        let scale = max (abs fd) (max (abs an) 1e-8)
        TestHarness.check (sprintf "%s (fd %.10g vs vjp %.10g)" name fd an)
                          (abs (fd - an) / scale <= 1e-6)

    let run () =
        TestHarness.section "autodiff: per-op VJPs vs finite differences"

        // ---- linear ----
        let specA = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 3); (2, Even, 1) ]
        let specB = Irreps.mkSpec [ (0, Even, 3); (1, Odd, 2); (2, Even, 2) ]
        let wLin = randArray (Linear.weightDim specA specB)
        let xLin = randArray (Irreps.totalDim specA)
        checkVjpAgainstFd "linear: dW"
            (fun w -> Linear.linear specA specB w xLin) wLin
            (fun u -> fst (Autodiff.vjpLinear specA specB wLin xLin u))
        checkVjpAgainstFd "linear: dX"
            (fun x -> Linear.linear specA specB wLin x) xLin
            (fun u -> snd (Autodiff.vjpLinear specA specB wLin xLin u))

        // ---- tensor product ----
        let cfg : TPConfig =
            { Spec1 = specA
              Spec2 = Irreps.shSpec 2
              SpecOut = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 2); (2, Even, 2) ] }
        let wTp = randArray (TensorProduct.weightDim cfg)
        let xTp = randArray (Irreps.totalDim cfg.Spec1)
        let yTp = randArray (Irreps.totalDim cfg.Spec2)
        let vjpTp (u: float[]) = Autodiff.vjpTensorProduct cfg wTp xTp yTp u
        checkVjpAgainstFd "tensor_product: dW"
            (fun w -> TensorProduct.tensorProduct cfg w xTp yTp) wTp
            (fun u -> let dw, _, _ = vjpTp u in dw)
        checkVjpAgainstFd "tensor_product: dX"
            (fun x -> TensorProduct.tensorProduct cfg wTp x yTp) xTp
            (fun u -> let _, dx, _ = vjpTp u in dx)
        checkVjpAgainstFd "tensor_product: dY"
            (fun y -> TensorProduct.tensorProduct cfg wTp xTp y) yTp
            (fun u -> let _, _, dy = vjpTp u in dy)

        // The w=0 trap: gradient wrt an all-zero weight vector is NOT zero.
        let dwAtZero, _, _ =
            Autodiff.vjpTensorProduct cfg (Array.zeroCreate wTp.Length) xTp yTp
                                      (Array.create (Irreps.totalDim cfg.SpecOut) 1.0)
        TestHarness.check "tensor_product: dW nonzero at w = 0 (forward skip must not leak)"
                          (dwAtZero |> Array.exists (fun v -> abs v > 1e-6))

        // ---- gated activation ----
        let specG = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 3); (2, Even, 1) ]
        let featG = randArray (Irreps.totalDim specG)
        checkVjpAgainstFd "gated: dFeat (incl. gate double-duty term)"
            (Activations.gated specG) featG
            (fun u -> Autodiff.vjpGated specG featG u)

        // ---- gather / scatter_add duality ----
        TestHarness.section "autodiff: gather/scatter adjoint duality"
        let nRows = 6
        let featDim = 4
        let srcIdx = [| 0; 2; 2; 5; 1; 4; 0 |]
        let feats = randArray (nRows * featDim)
        let vals = randArray (srcIdx.Length * featDim)
        // ⟨gather(feats), vals⟩ = ⟨feats, scatter_add(vals)⟩
        let lhs = dot (MessagePassing.gather feats featDim nRows srcIdx) vals
        let rhs = dot feats (MessagePassing.scatterAdd vals featDim srcIdx nRows)
        TestHarness.checkClose "⟨gather(f), v⟩ = ⟨f, scatter_add(v)⟩" 1e-12 lhs rhs
        // and the vjp wrappers are exactly those transposes
        TestHarness.checkArrayClose "vjpGather = scatter_add" 0.0
            (MessagePassing.scatterAdd vals featDim srcIdx nRows)
            (Autodiff.vjpGather vals featDim nRows srcIdx)
        TestHarness.checkArrayClose "vjpScatterAdd = gather" 0.0
            (MessagePassing.gather feats featDim nRows srcIdx)
            (Autodiff.vjpScatterAdd feats featDim nRows srcIdx)

        // ---- equivariant conv ----
        TestHarness.section "autodiff: equivariant conv VJP"
        let ci = TrainingOracle.specIn
        let ch = TrainingOracle.specH
        let nN = TrainingOracle.nNodes
        let eSrc = TrainingOracle.edgeSrc
        let eTgt = TrainingOracle.edgeTgt
        let posC = randArray (nN * 3)
        let evC = TrainingOracle.edgeVecs posC
        let nfC = randArray (nN * Irreps.totalDim ci)
        let wC = randArray (TensorProduct.weightDim TrainingOracle.cfg1)
        checkVjpAgainstFd "conv: dW"
            (fun w -> Conv.equivariantConv ci ch 2 nfC nN eSrc eTgt evC w) wC
            (fun u -> fst (Autodiff.vjpEquivariantConv ci ch 2 nfC nN eSrc eTgt evC wC u))
        checkVjpAgainstFd "conv: dNodeFeat"
            (fun nf -> Conv.equivariantConv ci ch 2 nf nN eSrc eTgt evC wC) nfC
            (fun u -> snd (Autodiff.vjpEquivariantConv ci ch 2 nfC nN eSrc eTgt evC wC u))

        // ---- full-model gradient vs finite differences ----
        TestHarness.section "autodiff: full model (all parameters) vs finite differences"
        let graphs, w1, w2, w3, wr, br = TrainingOracle.mkDataset ()
        let packed = Array.concat [ w1; w2; w3; wr; [| br |] ]
        let unpack (p: float[]) =
            let a = Array.sub p 0 w1.Length
            let b = Array.sub p w1.Length w2.Length
            let c = Array.sub p (w1.Length + w2.Length) w3.Length
            let d = Array.sub p (w1.Length + w2.Length + w3.Length) wr.Length
            let e = p.[w1.Length + w2.Length + w3.Length + wr.Length]
            a, b, c, d, e
        let lossAt (p: float[]) =
            let a, b, c, d, e = unpack p
            fst (TrainingOracle.lossAndGrads graphs a b c d e)
        let _, grads = TrainingOracle.lossAndGrads graphs w1 w2 w3 wr br
        let analytic = Array.concat [ grads.DW1; grads.DW2; grads.DW3; grads.DWr; [| grads.DBr |] ]
        let eps = 1e-5
        let mutable maxRel = 0.0
        for i in 0 .. packed.Length - 1 do
            let pp = Array.copy packed
            let pm = Array.copy packed
            pp.[i] <- pp.[i] + eps
            pm.[i] <- pm.[i] - eps
            let fd = (lossAt pp - lossAt pm) / (2.0 * eps)
            let scale = max (abs fd) (max (abs analytic.[i]) 1e-8)
            maxRel <- max maxRel (abs (fd - analytic.[i]) / scale)
        TestHarness.check (sprintf "all %d partials match central differences (max rel %.3g)"
                                   packed.Length maxRel)
                          (maxRel <= 1e-6)

        // ---- gradient equivariance under rotation ----
        TestHarness.section "autodiff: loss and gradients invariant under rotation"
        let rot = Rotations.randomRotation (Random(9001))
        let rotateGraph (g: TrainingOracle.Graph) : TrainingOracle.Graph =
            let pos' = Array.zeroCreate g.Pos.Length
            for i in 0 .. TrainingOracle.nNodes - 1 do
                for r in 0 .. 2 do
                    let mutable acc = 0.0
                    for c in 0 .. 2 do
                        acc <- acc + rot.[r].[c] * g.Pos.[3 * i + c]
                    pos'.[3 * i + r] <- acc
            // targets are recomputed from rotated positions; the target
            // function is invariant, so this is a consistency double-check
            { g with Pos = pos'; Target = TrainingOracle.invariantTarget pos' }
        let graphsR = graphs |> Array.map rotateGraph
        let lossO, gO = TrainingOracle.lossAndGrads graphs w1 w2 w3 wr br
        let lossR, gR = TrainingOracle.lossAndGrads graphsR w1 w2 w3 wr br
        TestHarness.checkClose "loss invariant under SO(3) on positions" 1e-9 lossO lossR
        TestHarness.checkArrayClose "dW1 invariant" 1e-9 gO.DW1 gR.DW1
        TestHarness.checkArrayClose "dW2 invariant" 1e-9 gO.DW2 gR.DW2
        TestHarness.checkArrayClose "dW3 invariant" 1e-9 gO.DW3 gR.DW3
        TestHarness.checkArrayClose "dWr invariant" 1e-9 gO.DWr gR.DWr
        TestHarness.checkClose "dBr invariant" 1e-9 gO.DBr gR.DBr

        // ---- training sanity ----
        TestHarness.section "autodiff: fixed-seed training run"
        let res = TrainingOracle.train ()
        let first = res.LossTrajectory.[0]
        let final = res.LossTrajectory.[res.LossTrajectory.Length - 1]
        TestHarness.check (sprintf "loss decreases (%.6g -> %.6g)" first final)
                          (final < 0.5 * first)
        TestHarness.check "trajectory is finite everywhere"
                          (res.LossTrajectory |> Array.forall Double.IsFinite)
