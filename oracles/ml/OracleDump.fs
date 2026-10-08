namespace BladeML

/// Prints the training oracle's fixed inputs and pinned outputs in a
/// copy-pasteable form for authoring (and re-pinning) the Blade e2e example:
/// dataset literals, initial weights, CG tables for the two TP configs,
/// step-0 gradients, and the loss trajectory. Run:
///   dotnet run --project oracles/ml -- dump-oracle
module OracleDump =

    /// Full round-trip precision, invariant culture -- the same spelling
    /// discipline the compiler's floatToCppLiteral uses.
    let private f2s (v: float) : string =
        let s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

    let private arr (name: string) (xs: float[]) =
        printfn "%s = [%s]" name (xs |> Array.map f2s |> String.concat ", ")

    let private arrInt (name: string) (xs: int[]) =
        printfn "%s = [%s]" name (xs |> Array.map string |> String.concat ", ")

    /// The sparse real-CG entries of every path of a TP config, flattened in
    /// path order -- exactly the constant tables the Blade example needs.
    let private dumpCgTables (label: string) (cfg: TPConfig) =
        let ps = TensorProduct.paths cfg
        printfn "// %s: %d paths" label ps.Length
        for pi in 0 .. ps.Length - 1 do
            let p = ps.[pi]
            let l1 = cfg.Spec1.[p.B1].Ir.L
            let l2 = cfg.Spec2.[p.B2].Ir.L
            let lo = cfg.SpecOut.[p.BOut].Ir.L
            let cg = Wigner.realCGSparse l1 l2 lo
            printfn "//   path %d: (b1=%d,l1=%d) x (b2=%d,l2=%d) -> (bo=%d,lo=%d), %d entries"
                pi p.B1 l1 p.B2 l2 p.BOut lo cg.Length
            let c1 = cg |> Array.map _.C1
            let c2 = cg |> Array.map _.C2
            let c3 = cg |> Array.map _.C3
            let co = cg |> Array.map _.Coef
            arrInt $"{label}_p{pi}_c1" c1
            arrInt $"{label}_p{pi}_c2" c2
            arrInt $"{label}_p{pi}_c3" c3
            arr $"{label}_p{pi}_coef" co

    let dump () =
        let graphs, w1, w2, w3, wr, br = TrainingOracle.mkDataset ()

        printfn "// ===== TrainingOracle dump (seed 20260711) ====="
        printfn "// specs: in=[(0e,1)] H=[(0e,2),(1o,2),(2e,1)] out=[(0e,2)]; lmaxSh=2"
        printfn "// dims: dIn=%d dH=%d dOut=%d; weights: w1=%d w2=%d w3=%d wr=%d"
            TrainingOracle.dIn TrainingOracle.dH TrainingOracle.dOut
            TrainingOracle.w1Dim TrainingOracle.w2Dim TrainingOracle.w3Dim TrainingOracle.wrDim
        printfn "// nodes=%d edges=%d graphs=%d; lr=%s steps=%d"
            TrainingOracle.nNodes TrainingOracle.nEdges TrainingOracle.nGraphs
            (f2s TrainingOracle.learningRate) TrainingOracle.trainSteps
        printfn ""
        arrInt "edge_src" TrainingOracle.edgeSrc
        arrInt "edge_tgt" TrainingOracle.edgeTgt
        printfn ""
        for s in 0 .. graphs.Length - 1 do
            let g = graphs.[s]
            arr $"pos{s}" g.Pos
            arr $"feat{s}" g.NodeFeat
            printfn "target%d = %s" s (f2s g.Target)
        printfn ""
        arr "w1_init" w1
        arr "w2_init" w2
        arr "w3_init" w3
        arr "wr_init" wr
        printfn "br_init = %s" (f2s br)
        printfn ""
        dumpCgTables "cg1" TrainingOracle.cfg1
        dumpCgTables "cg2" TrainingOracle.cfg2
        printfn ""
        let res = TrainingOracle.train ()
        arr "loss_trajectory" res.LossTrajectory
        arr "dw1_step0" res.Step0Grads.DW1
        arr "dw2_step0" res.Step0Grads.DW2
        arr "dw3_step0" res.Step0Grads.DW3
        arr "dwr_step0" res.Step0Grads.DWr
        printfn "dbr_step0 = %s" (f2s res.Step0Grads.DBr)
        arr "w1_final" res.FinalW1
        arr "w2_final" res.FinalW2
        arr "w3_final" res.FinalW3
        arr "wr_final" res.FinalWr
        printfn "br_final = %s" (f2s res.FinalBr)

        // ===== tp_spec full-decomposition pins (corpus ml-ops/009) =====
        // The FULL CG decomposition of 1o (x) 1o via Irreps.tpSpec, pushed
        // through the reference tensor product on fixed inputs -- the value
        // pins for the compiler's `ml.tp_spec` round-trip test.
        printfn ""
        printfn "// ===== tp_spec pins (ml-ops/009): full 1o (x) 1o ====="
        let s1o = Irreps.mkSpec [ (1, Odd, 1) ]
        let sFull = Irreps.tpSpec s1o s1o
        let specStr (s: SpecEntry[]) =
            s
            |> Array.map (fun e ->
                $"({e.Ir.L}, {if e.Ir.P = Even then 0 else 1}, {e.Mult})")
            |> String.concat ", "
        printfn "// tp_spec = [%s]; total_dim = %d" (specStr sFull) (Irreps.totalDim sFull)
        let cfgFull : TPConfig = { Spec1 = s1o; Spec2 = s1o; SpecOut = sFull }
        printfn "// tp_weight_dim = %d; hom_dim(sh1 -> full) = %d"
            (TensorProduct.weightDim cfgFull)
            (Irreps.homDim (Irreps.shSpec 1) sFull)
        let xF = [| 1.0; 2.0; 3.0 |]
        let yF = [| 0.5; -1.0; 2.0 |]
        let wF = [| 1.0; 1.0; 1.0 |]
        arr "tp_full_out" (TensorProduct.tensorProduct cfgFull wF xF yF)

        // ===== equiv-certified pipeline pins (corpus ml-equiv/002) =====
        // y_to(2, 1,2,3) -> TP(sh2 (x) sh2 -> sh2) -> gated -> linear -> norms,
        // fixed weights -- the value pins for the certified-pipeline test.
        printfn ""
        printfn "// ===== equiv pipeline pins (ml-equiv/002) ====="
        let sh2spec = Irreps.shSpec 2
        let cfgP : TPConfig = { Spec1 = sh2spec; Spec2 = sh2spec; SpecOut = sh2spec }
        let wtp = Array.init (TensorProduct.weightDim cfgP) (fun i -> 0.1 * float (i + 1))
        let wl = [| 0.5; -0.7; 0.25 |]
        printfn "// tp_weight_dim = %d; linear_weight_dim = %d"
            (TensorProduct.weightDim cfgP) (Linear.weightDim sh2spec sh2spec)
        arr "pipe_wtp" wtp
        arr "pipe_wl" wl
        let shv = SphericalHarmonics.evalUpTo 2 1.0 2.0 3.0 |> Array.concat
        let tv = TensorProduct.tensorProduct cfgP wtp shv shv
        let gv = Activations.gated sh2spec tv
        let lv = Linear.linear sh2spec sh2spec wl gv
        arr "pipe_out" (Activations.norms sh2spec lv)

        // ===== derive-layer training pins (corpus ml-equiv/017) =====
        // Certified model derive_linear -> norms; loss = Sigma (norms - tgt)^2;
        // 6 recorded losses (pre-update) over 5+1 SGD steps at lr 0.1 via
        // vjpNorms/vjpHomLinear. The Blade twin trains the SAME model
        // through grad() over the compiler-synthesized layers.
        printfn ""
        printfn "// ===== derive training pins (ml-equiv/017) ====="
        let dInS = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 1) ]
        let dOutS = Irreps.mkSpec [ (0, Even, 1); (1, Odd, 2) ]
        let x017 = [| 0.5; -1.2; 0.8; 0.3; -0.6 |]
        let tgt017 = [| 1.0; 0.5; 0.25 |]
        let mutable w017 = [| 0.4; -0.3; 0.2; 0.1 |]
        let lr017 = 0.1
        let losses017 =
            [| for _ in 1 .. 6 ->
                 let h = Linear.homLinear dInS dOutS w017 x017
                 let n = Activations.norms dOutS h
                 let loss = Array.map2 (fun a b -> (a - b) * (a - b)) n tgt017 |> Array.sum
                 let dN = Array.map2 (fun a b -> 2.0 * (a - b)) n tgt017
                 let dH = Autodiff.vjpNorms dOutS h dN
                 let dW, _ = Autodiff.vjpHomLinear dInS dOutS w017 x017 dH
                 w017 <- Array.map2 (fun w g -> w - lr017 * g) w017 dW
                 loss |]
        arr "derive_train_losses" losses017
        arr "derive_train_w_final" w017

    /// dump-equiv: the rotation-CERTIFICATE fixtures (corpus ml-equiv/018+).
    /// Fixed proper rotation R = Rz(0.7)*Ry(1.1) (the ml-e2e convention);
    /// per test both routes are printed -- f(D_in*x) and D_out*f(x) -- as
    /// pasteable literals, plus the dense block-diagonal D matrices
    /// (Rotations.repMatrix; a full matvec over them reproduces applyRep to
    /// the ulp). These validate the compiler's equiv discipline ONCE; user
    /// networks' certificate is the type check.
    let dumpEquiv () =
        let r = MathUtils.matMul (Rotations.rotZ 0.7) (Rotations.rotY 1.1)
        let matvec (n: int) (m: float[]) (x: float[]) : float[] =
            // EXACTLY the loop the Blade tests bake: i ascending, j ascending.
            [| for i in 0 .. n - 1 ->
                 let mutable acc = 0.0
                 for j in 0 .. n - 1 do
                     acc <- acc + m.[i * n + j] * x.[j]
                 acc |]
        printfn "// ===== equiv certificates (fixed R = Rz(0.7)*Ry(1.1)) ====="
        arr "rot" (r |> Array.concat)

        // ---- 018: derive_linear both ways (011's fixtures) ----
        let sIn = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 1) ]
        let sOut = Irreps.mkSpec [ (0, Even, 1); (1, Odd, 2) ]
        let w = [| 0.5; -1.0; 2.0; 0.25 |]
        let x = [| 1.0; 2.0; 3.0; -1.0; 0.5 |]
        let dinX = Rotations.applyRep sIn r x
        let lhs = Linear.homLinear sIn sOut w dinX
        let dOutM = Rotations.repMatrix sOut r
        let rhs = matvec (Irreps.totalDim sOut) dOutM (Linear.homLinear sIn sOut w x)
        printfn "// 018 derive_linear: |f(Dx) - D f(x)|_max = %g" (MathUtils.maxAbsDiff lhs rhs)
        arr "cert_lin_din_x" dinX
        arr "cert_lin_dout" dOutM
        arr "cert_lin_lhs" lhs
        arr "cert_lin_rhs" rhs

        // ---- 019: derive_tp both ways (013's fixtures) ----
        let s1o = Irreps.mkSpec [ (1, Odd, 1) ]
        let sFull = Irreps.tpSpec s1o s1o
        let cfgF : TPConfig = { Spec1 = s1o; Spec2 = s1o; SpecOut = sFull }
        let xT = [| 1.0; 2.0; 3.0 |]
        let yT = [| 0.5; -1.0; 2.0 |]
        let wT = [| 1.0; 1.0; 1.0 |]
        let dxT = Rotations.applyRep s1o r xT
        let dyT = Rotations.applyRep s1o r yT
        let lhsT = TensorProduct.tensorProduct cfgF wT dxT dyT
        let dOutT = Rotations.repMatrix sFull r
        let rhsT = matvec (Irreps.totalDim sFull) dOutT (TensorProduct.tensorProduct cfgF wT xT yT)
        printfn "// 019 derive_tp: |f(Dx,Dy) - D f(x,y)|_max = %g" (MathUtils.maxAbsDiff lhsT rhsT)
        arr "cert_tp_dx" dxT
        arr "cert_tp_dy" dyT
        arr "cert_tp_dout" dOutT
        arr "cert_tp_lhs" lhsT
        arr "cert_tp_rhs" rhsT

        // ---- 020: certified-pipeline INVARIANCE (002's fixtures) ----
        let sh2 = Irreps.shSpec 2
        let cfgP : TPConfig = { Spec1 = sh2; Spec2 = sh2; SpecOut = sh2 }
        let wtp = Array.init (TensorProduct.weightDim cfgP) (fun i -> 0.1 * float (i + 1))
        let wl = [| 0.5; -0.7; 0.25 |]
        let pipe (px: float) (py: float) (pz: float) =
            let shv = SphericalHarmonics.evalUpTo 2 px py pz |> Array.concat
            let tv = TensorProduct.tensorProduct cfgP wtp shv shv
            let gv = Activations.gated sh2 tv
            let lv = Linear.linear sh2 sh2 wl gv
            Activations.norms sh2 lv
        let p = [| 1.0; 2.0; 3.0 |]
        let rp = MathUtils.matVec r p
        printfn "// 020 pipeline invariance: |pipe(Rp) - pipe(p)|_max = %g"
            (MathUtils.maxAbsDiff (pipe rp.[0] rp.[1] rp.[2]) (pipe p.[0] p.[1] p.[2]))
        arr "cert_pipe_rp" rp
        arr "cert_pipe_out" (pipe p.[0] p.[1] p.[2])
        arr "cert_pipe_out_rot" (pipe rp.[0] rp.[1] rp.[2])

        // ---- 021: gradient invariance (017's model) ----
        let dIn21 = Irreps.mkSpec [ (0, Even, 2); (1, Odd, 1) ]
        let dOut21 = Irreps.mkSpec [ (0, Even, 1); (1, Odd, 2) ]
        let tgt21 = [| 1.0; 0.5; 0.25 |]
        let w21 = [| 0.4; -0.3; 0.2; 0.1 |]
        let x21 = [| 0.5; -1.2; 0.8; 0.3; -0.6 |]
        let gradAt (xv: float[]) =
            let h = Linear.homLinear dIn21 dOut21 w21 xv
            let n = Activations.norms dOut21 h
            let loss = Array.map2 (fun a b -> (a - b) * (a - b)) n tgt21 |> Array.sum
            let dN = Array.map2 (fun a b -> 2.0 * (a - b)) n tgt21
            let dH = Autodiff.vjpNorms dOut21 h dN
            let dW, _ = Autodiff.vjpHomLinear dIn21 dOut21 w21 xv dH
            loss, dW
        let l0, g0 = gradAt x21
        let xRot = Rotations.applyRep dIn21 r x21
        let l1, g1 = gradAt xRot
        printfn "// 021 grad invariance: |loss diff| = %g, |dw diff|_max = %g"
            (abs (l0 - l1)) (MathUtils.maxAbsDiff g0 g1)
        arr "cert_grad_x_rot" xRot
        printfn "cert_grad_loss = %s" (f2s l0)
        printfn "cert_grad_loss_rot = %s" (f2s l1)
        arr "cert_grad_dw" g0
        arr "cert_grad_dw_rot" g1

    /// dump-cartesian: the Cartesian<->irreps bridge -- constants,
    /// orthogonality, and rotation/parity certificates (corpus sgs/001-003;
    /// later the pins for the compiler's CartesianBridge.fs).
    ///
    /// FROZEN CONVENTIONS (single source of truth for the whole sgs arc):
    ///  - Symmetric pack order: [s00, s01, s02, s11, s12, s22] (upper
    ///    triangle, row-major).
    ///  - 3x3 Cartesian tensors flatten row-major: g[3i+j] = G_ij. For a
    ///    velocity-gradient field, G_ij = d_j u_i (row = component,
    ///    col = derivative axis).
    ///  - GSPEC = [(0,e,1); (1,e,1); (2,e,1)] (dim 9). The gradient of a true
    ///    vector is odd (x) odd = parity-EVEN throughout; the l=1 block is
    ///    the axial pseudovector (vorticity) in Y1 component order (y, z, x),
    ///    with a_x = g21 - g12 (the curl convention for G_ij = d_j u_i).
    ///  - TAUSPEC = [(0,e,1); (2,e,1)] (dim 6).
    ///  - Bridge rows are ORTHONORMAL over R^9 with the Frobenius inner
    ///    product: |G|_F = |bridge9 G|, |S|_F = |sym_to_irr packSym(S)| (the
    ///    packed-6 form weights off-diagonals by sqrt(2)).
    ///  - The l=2 rows are DERIVED BY FIT against SphericalHarmonics.eval
    ///    (never hand-transcribed): the fitted-and-normalized rows must equal
    ///    the closed forms with one Schur ratio sqrt(15/8pi) across all five.
    let dumpCartesian () =
        let sqrt2 = sqrt 2.0
        let sqrt3 = sqrt 3.0
        let sqrt6 = sqrt 6.0
        let mkRow (entries: (int * int * float) list) : float[] =
            let r = Array.zeroCreate 9
            for (i, j, c) in entries do r.[3 * i + j] <- c
            r
        // Rows in irreps order: l=0 | l=1 (Y1 order y,z,x of the axial) | l=2
        // (Y2 order xy, yz, 3z^2-r^2, xz, x^2-y^2).
        let bridge9 : float[][] =
            [| mkRow [ (0, 0, 1.0 / sqrt3); (1, 1, 1.0 / sqrt3); (2, 2, 1.0 / sqrt3) ]
               mkRow [ (0, 2, 1.0 / sqrt2); (2, 0, -1.0 / sqrt2) ]                       // a_y = (g02-g20)/sqrt2
               mkRow [ (1, 0, 1.0 / sqrt2); (0, 1, -1.0 / sqrt2) ]                       // a_z = (g10-g01)/sqrt2
               mkRow [ (2, 1, 1.0 / sqrt2); (1, 2, -1.0 / sqrt2) ]                       // a_x = (g21-g12)/sqrt2
               mkRow [ (0, 1, 1.0 / sqrt2); (1, 0, 1.0 / sqrt2) ]                        // xy
               mkRow [ (1, 2, 1.0 / sqrt2); (2, 1, 1.0 / sqrt2) ]                        // yz
               mkRow [ (0, 0, -1.0 / sqrt6); (1, 1, -1.0 / sqrt6); (2, 2, 2.0 / sqrt6) ] // 3z^2-r^2
               mkRow [ (0, 2, 1.0 / sqrt2); (2, 0, 1.0 / sqrt2) ]                        // xz
               mkRow [ (0, 0, 1.0 / sqrt2); (1, 1, -1.0 / sqrt2) ] |]                    // x^2-y^2
        // Packed-6 forms for symmetric tensors (order [t0, xy, yz, z2, xz, x2y2]).
        let symToIrr : float[][] =
            [| [| 1.0 / sqrt3; 0.0; 0.0; 1.0 / sqrt3; 0.0; 1.0 / sqrt3 |]
               [| 0.0; sqrt2; 0.0; 0.0; 0.0; 0.0 |]
               [| 0.0; 0.0; 0.0; 0.0; sqrt2; 0.0 |]
               [| -1.0 / sqrt6; 0.0; 0.0; -1.0 / sqrt6; 0.0; 2.0 / sqrt6 |]
               [| 0.0; 0.0; sqrt2; 0.0; 0.0; 0.0 |]
               [| 1.0 / sqrt2; 0.0; 0.0; -1.0 / sqrt2; 0.0; 0.0 |] |]
        let irrToSym : float[][] =
            [| [| 1.0 / sqrt3; 0.0; 0.0; -1.0 / sqrt6; 0.0; 1.0 / sqrt2 |]   // s00
               [| 0.0; 1.0 / sqrt2; 0.0; 0.0; 0.0; 0.0 |]                     // s01
               [| 0.0; 0.0; 0.0; 0.0; 1.0 / sqrt2; 0.0 |]                     // s02
               [| 1.0 / sqrt3; 0.0; 0.0; -1.0 / sqrt6; 0.0; -1.0 / sqrt2 |]   // s11
               [| 0.0; 0.0; 1.0 / sqrt2; 0.0; 0.0; 0.0 |]                     // s12
               [| 1.0 / sqrt3; 0.0; 0.0; 2.0 / sqrt6; 0.0; 0.0 |] |]          // s22
        let flatten9 (g: float[][]) : float[] =
            [| for i in 0 .. 2 do for j in 0 .. 2 do yield g.[i].[j] |]
        let packSymM (s: float[][]) : float[] =
            [| s.[0].[0]; s.[0].[1]; s.[0].[2]; s.[1].[1]; s.[1].[2]; s.[2].[2] |]
        let conj (r: float[][]) (g: float[][]) : float[][] =
            MathUtils.matMul r (MathUtils.matMul g (MathUtils.transpose r))
        let vnorm (v: float[]) : float = sqrt (Array.sumBy (fun x -> x * x) v)
        let vvT (v: float[]) : float[][] =
            Array.init 3 (fun i -> Array.init 3 (fun j -> v.[i] * v.[j]))
        let matvecFlat (n: int) (m: float[]) (x: float[]) : float[] =
            // EXACTLY the loop the Blade tests bake: i ascending, j ascending.
            [| for i in 0 .. n - 1 ->
                 let mutable acc = 0.0
                 for j in 0 .. n - 1 do
                     acc <- acc + m.[i * n + j] * x.[j]
                 acc |]

        printfn "// ===== cartesian bridge (sgs/001-003; CartesianBridge.fs pins) ====="

        // ---- derive l=2 by fit against the harmonics (the anti-transcription
        // discipline), then check the closed forms against the fit ----
        let rng = System.Random(20260719)
        let mutable b2 = Unchecked.defaultof<float[][]>
        let mutable fitted = false
        let mutable attempt = 0
        while not fitted && attempt < 10 do
            try
                let pts = Array.init 6 (fun _ -> Rotations.randomUnitVector rng)
                let pMat = pts |> Array.map (fun v -> packSymM (vvT v))
                let yMat = pts |> Array.map (fun v -> SphericalHarmonics.eval 2 v.[0] v.[1] v.[2])
                let cand = MathUtils.transpose (MathUtils.solve pMat yMat)   // 5x6
                let mutable resid = 0.0
                for _ in 1 .. 4 do
                    let v = Rotations.randomUnitVector rng
                    resid <- max resid (MathUtils.maxAbsDiff
                                            (MathUtils.matVec cand (packSymM (vvT v)))
                                            (SphericalHarmonics.eval 2 v.[0] v.[1] v.[2]))
                if resid < 1e-9 then
                    b2 <- cand
                    fitted <- true
            with _ -> ()
            attempt <- attempt + 1
        if not fitted then failwith "dump-cartesian: could not fit B2"
        let packIdx = [| (0, 0); (0, 1); (0, 2); (1, 1); (1, 2); (2, 2) |]
        let embedRow (row: float[]) : float[] =
            let r = Array.zeroCreate 9
            for k in 0 .. 5 do
                let (i, j) = packIdx.[k]
                if i = j then r.[3 * i + j] <- row.[k]
                else
                    r.[3 * i + j] <- row.[k] / 2.0
                    r.[3 * j + i] <- row.[k] / 2.0
            r
        let mutable ratioMin = infinity
        let mutable ratioMax = 0.0
        let mutable rowDev = 0.0
        for k in 0 .. 4 do
            let e = embedRow b2.[k]
            let n = vnorm e
            ratioMin <- min ratioMin n
            ratioMax <- max ratioMax n
            rowDev <- max rowDev (MathUtils.maxAbsDiff (Array.map (fun x -> x / n) e) bridge9.[4 + k])
        printfn "// fit check: |normalized fitted l2 row - closed row|_max = %g" rowDev
        printfn "// fit check: Schur ratio in [%.15g, %.15g]; sqrt(15/8pi) = %.15g"
            ratioMin ratioMax (sqrt (15.0 / (8.0 * System.Math.PI)))

        // ---- specs, house rotation, fixed fixtures ----
        let gspec = Irreps.mkSpec [ (0, Even, 1); (1, Even, 1); (2, Even, 1) ]
        let tspec = Irreps.mkSpec [ (0, Even, 1); (2, Even, 1) ]
        let gspecWrong = Irreps.mkSpec [ (0, Even, 1); (1, Odd, 1); (2, Even, 1) ]
        let r = MathUtils.matMul (Rotations.rotZ 0.7) (Rotations.rotY 1.1)
        let gFix = [| [| 0.6; -1.1; 0.4 |]; [| 0.9; 0.3; -0.7 |]; [| -0.2; 0.8; 1.2 |] |]
        let sFix = [| [| 1.0; 0.5; -0.3 |]; [| 0.5; 2.0; 0.7 |]; [| -0.3; 0.7; -1.0 |] |]
        arr "rot" (r |> Array.concat)
        arr "bridge9" (bridge9 |> Array.concat)
        arr "sym_to_irr" (symToIrr |> Array.concat)
        arr "irr_to_sym" (irrToSym |> Array.concat)
        arr "g_fix" (flatten9 gFix)
        arr "s_fix_packed" (packSymM sFix)
        arr "d_gspec" (Rotations.repMatrix gspec r)
        arr "d_tauspec" (Rotations.repMatrix tspec r)

        // ---- 001: symmetric bridge -- round-trip + rotation certificate ----
        let bS = MathUtils.matVec symToIrr (packSymM sFix)
        let lhsS = MathUtils.matVec symToIrr (packSymM (conj r sFix))
        let rhsS = matvecFlat 6 (Rotations.repMatrix tspec r) bS
        let backS = MathUtils.matVec irrToSym bS
        printfn "// 001 sym bridge: |cart route - irreps route|_max = %g" (MathUtils.maxAbsDiff lhsS rhsS)
        printfn "// 001 round-trip: |irr_to_sym(sym_to_irr s) - s|_max = %g" (MathUtils.maxAbsDiff backS (packSymM sFix))
        let frobS = sqrt (packSymM sFix |> Array.mapi (fun k x -> if fst packIdx.[k] = snd packIdx.[k] then x * x else 2.0 * x * x) |> Array.sum)
        printfn "// 001 orthogonality: |S|_F = %s, |sym_to_irr(S)| = %s" (f2s frobS) (f2s (vnorm bS))
        arr "cert_s" bS
        arr "cert_s_lhs" lhsS
        arr "cert_s_rhs" rhsS
        arr "cert_s_back" backS
        printfn "cert_s_frob = %s" (f2s frobS)
        printfn "cert_s_norm = %s" (f2s (vnorm bS))

        // ---- 002/003: full-gradient bridge -- certificate + improper contrast ----
        let bG = MathUtils.matVec bridge9 (flatten9 gFix)
        let lhsG = MathUtils.matVec bridge9 (flatten9 (conj r gFix))
        let rhsG = matvecFlat 9 (Rotations.repMatrix gspec r) bG
        printfn "// 003 grad bridge: |cart route - irreps route|_max = %g" (MathUtils.maxAbsDiff lhsG rhsG)
        printfn "// 003 orthogonality: |G|_F = %s, |bridge9(G)| = %s"
            (f2s (vnorm (flatten9 gFix))) (f2s (vnorm bG))
        arr "cert_g" bG
        arr "cert_g_lhs" lhsG
        arr "cert_g_rhs" rhsG
        arr "cert_g_back" (MathUtils.matVec (MathUtils.transpose bridge9) bG)
        printfn "cert_g_frob = %s" (f2s (vnorm (flatten9 gFix)))
        printfn "cert_g_norm = %s" (f2s (vnorm bG))
        // Improper element inversion.R: Cartesian route is UNCHANGED
        // ((-R) G (-R)^T = R G R^T -- gradients are parity-even), so the
        // right-parity irreps route must equal the proper one, while the
        // WRONG assignment (vorticity as (1, odd)) flips the l=1 block.
        let impRight = Rotations.applyRepImproper gspec r bG
        let impWrong = Rotations.applyRepImproper gspecWrong r bG
        printfn "// 002 improper: right-parity resid = %g; wrong-parity resid = %g (must be LARGE)"
            (MathUtils.maxAbsDiff lhsG impRight) (MathUtils.maxAbsDiff lhsG impWrong)
        arr "cert_g_improper_wrong" impWrong

    // ===================================================================
    // dump-lie: the Lie-algebra pins for tests/corpus/lie/ (Lie.fs)
    // ===================================================================

    /// f2s with -0.0 folded to +0.0: a pasted literal should not carry a
    /// sign the algebra does not.
    let private fz (v: float) : string = f2s (if v = 0.0 then 0.0 else v)

    let private rowLit (xs: float[]) : string =
        "[" + (xs |> Array.map fz |> String.concat ", ") + "]"

    /// Residual spelling for // comments.
    let private sci (v: float) : string =
        v.ToString("0.00e+00", System.Globalization.CultureInfo.InvariantCulture)

    let private vecL (name: string) (xs: float[]) = printfn "%s = %s" name (rowLit xs)

    /// Rank-2 literal, nested rows: one line up to 9 cells, else one row per
    /// line inside the open bracket (examples/physics/41 spells its 16x16
    /// literal the same way).
    let private matL (name: string) (m: float[][]) =
        if m.Length * m.[0].Length <= 9 then
            printfn "%s = [%s]" name (m |> Array.map rowLit |> String.concat ", ")
        else
            printfn "%s = [" name
            m |> Array.iteri (fun i r -> printfn "  %s%s" (rowLit r) (if i < m.Length - 1 then "," else ""))
            printfn "]"

    let private matIntL (name: string) (m: int[][]) =
        let rowI (xs: int[]) = "[" + (xs |> Array.map string |> String.concat ", ") + "]"
        printfn "%s = [%s]" name (m |> Array.map rowI |> String.concat ", ")

    /// Rank-3 literal, one leading slice per line.
    let private ten3L (name: string) (t: float[][][]) =
        printfn "%s = [" name
        t |> Array.iteri (fun i m ->
            printfn "  [%s]%s" (m |> Array.map rowLit |> String.concat ", ") (if i < t.Length - 1 then "," else ""))
        printfn "]"

    let private require (ok: bool) (what: string) =
        if not ok then failwith $"dump-lie: cross-check failed: {what}"

    let private invTuple (rep: Lie.AlgebraReport) : int[] =
        let np, nm, n0 = rep.Signature
        [| rep.Dim; rep.Derived; rep.Center; rep.Radical; np; nm; n0; rep.Rank |]

    let private algebraBlock (tag: string) (title: string) (gens: float[][][]) (rep: Lie.AlgebraReport) =
        let np, nm, n0 = rep.Signature
        printfn "// %s: dim %d, [g,g] %d, center %d, radical %d, kappa (+,-,0) = (%d,%d,%d), rank %d; closure %s"
            title rep.Dim rep.Derived rep.Center rep.Radical np nm n0 rep.Rank (sci rep.Closure)
        ten3L $"{tag}_basis" gens
        arrInt $"{tag}_inv" (invTuple rep)
        vecL $"{tag}_killing_eig" rep.KappaEig

    /// dump-lie: P0 of docs/plans/plan-latent-symmetry-learning.md -- the
    /// so(3) tables (certified against Rotations.wignerD), structure
    /// constants / Killing / Casimir, small-algebra invariants, moment
    /// annihilation, the d = 12 canonicalization lighthouse, and the
    /// functional lane. Every cross-check is ASSERTED; a failure aborts the
    /// dump rather than printing a wrong pin.
    let dumpLie () =
        Lie.sweepsHighWater <- 0
        Lie.cappedCalls <- 0
        Lie.capLog.Clear()
        let axes = [| 0; 1; 2 |]
        let ax (a: int) = Lie.axisName a
        let tables (l: int) = Array.init 3 (fun a -> Lie.table a l)
        let zeroCount (s: float[]) =
            let smax = if s.Length = 0 then 0.0 else Array.max s
            s |> Array.filter (fun x -> x <= 1e-9 * max 1.0 smax) |> Array.length
        printfn "// ===== dump-lie: Lie-algebra pins for tests/corpus/lie/ (plan-latent-symmetry-learning.md P0) ====="
        printfn "// Floats round-trip (f2s; -0.0 prints 0.0). Rank-2 literals are nested rows, rank-3 nested slices."
        printfn "// Axis order everywhere: 0 = x, 1 = y, 2 = z. Every svd / eigh below is the m.svd / m.eigh Jacobi"
        printfn "// (descending; sign fix: the first max-|entry| row of each column positive)."

        // ---------------- 1. tables ----------------
        printfn ""
        printfn "// ===== 1. so(3) generator tables in the certifier's real basis (MLLieDischarge.blockGenerator) ====="
        printfn "// index c = m + l: R_m at c = l + m, I_m at c = l - m; L_z on (R_m, I_m) = [[0, -m], [m, 0]];"
        printfn "// [J_x, J_y] = J_z (cyclic); Sum_a J_a^2 = -l(l+1) I; the l = 1 basis order is (y, z, x)."
        let mutable fdDev = 0.0
        for l in 0 .. 3 do
            for a in axes do
                fdDev <- max fdDev (Lie.maxDiffM (Lie.table a l) (Lie.tableFromWigner a l))
        printfn "// CERTIFICATION: closed forms vs d/dtheta Rotations.wignerD l (R_a(theta)) at theta = 0 (the oracle's"
        printfn "// harmonics-FIT Wigner D, differentiated exactly as a degree-l trig polynomial from 2l+3 samples):"
        printfn "//   max |dev| over l = 0..3, a = x, y, z = %s" (sci fdDev)
        require (fdDev <= 1e-12) "closed-form tables vs the wignerD derivative"
        let mutable tabAlg = 0.0
        for l in 0 .. 3 do
            let j = tables l
            let n = 2 * l + 1
            tabAlg <- max tabAlg (Lie.bracketResidual j)
            tabAlg <- max tabAlg (Lie.maxDiffM (Lie.casimirSum j) (Lie.mscale (float (-l * (l + 1))) (Lie.eye n)))
            for x in j do tabAlg <- max tabAlg (Lie.maxAbsM (Lie.madd x (MathUtils.transpose x)))
        printfn "//   bracket, Casimir + l(l+1) I and antisymmetry residuals, l = 0..3: max = %s" (sci tabAlg)
        require (tabAlg <= 1e-13) "table brackets / Casimir / antisymmetry"
        for l in 0 .. 3 do
            for a in axes do
                matL $"lie_j{ax a}_l{l}" (Lie.table a l)
        printfn ""
        printfn "// l = 1 vs the standard rotation generators (L_a)_jk = -eps_ajk on (x, y, z) (= d/dtheta R_a(theta) at 0):"
        printfn "// J_a^(1) = P L_a P^T, P the (y, z, x) permutation ((y, z, x) = P (x, y, z))"
        for a in axes do
            matL $"so3_lstd_{ax a}" (Lie.lStd a)
        matL "so3_perm_yzx" Lie.permYZX
        let pDev =
            axes
            |> Array.map (fun a ->
                Lie.maxDiffM (Lie.table a 1)
                    (MathUtils.matMul Lie.permYZX (MathUtils.matMul (Lie.lStd a) (MathUtils.transpose Lie.permYZX))))
            |> Array.max
        printfn "// max |J_a^(1) - P L_a P^T| = %s (exact)" (sci pDev)
        require (pDev = 0.0) "l = 1 table vs P L P^T"

        // ---------------- 2. structure constants / Killing / Casimir ----------------
        printfn ""
        printfn "// ===== 2. structure constants, Killing form, Casimir ====="
        printfn "// c(i, j, k) = <E_k, [E_i, E_j]> / <E_k, E_k> (= eps_ijk); kappa(i, j) = Sum_kl c(i,k,l) c(j,l,k) (= -2 I);"
        printfn "// Casimir Sum_a E_a^2 (= -l(l+1) I). so3_* on the standard 3x3 L_a; so3_*_l<l> on the lie_j*_l<l> tables."
        let stdC = Lie.structureConstants Lie.so3Std
        ten3L "so3_c" stdC
        matL "so3_kappa" (Lie.killing stdC)
        matL "so3_casimir" (Lie.casimirSum Lie.so3Std)
        let mutable scDev = 0.0
        for l in 1 .. 3 do
            let j = tables l
            let c = Lie.structureConstants j
            let kap = Lie.killing c
            for i in 0 .. 2 do
                for jj in 0 .. 2 do
                    for k in 0 .. 2 do
                        scDev <- max scDev (abs (c.[i].[jj].[k] - Lie.leviCivita i jj k))
                    scDev <- max scDev (abs (kap.[i].[jj] - (if i = jj then -2.0 else 0.0)))
            ten3L $"so3_c_l{l}" c
            matL $"so3_kappa_l{l}" kap
            matL $"so3_casimir_l{l}" (Lie.casimirSum j)
        printfn "// max |c - eps| and |kappa + 2 I| over the l = 1..3 tables = %s" (sci scDev)
        require (scDev <= 1e-13) "table structure constants / Killing"
        printfn "// kappa in a Frobenius-ORTHONORMAL basis E_a = L_a / |L_a|_F, eigenvalues descending: basis-free within"
        printfn "// the representation, = -2 / |L_a|_F^2 = -6 / (l(l+1)(2l+1)) (-1 on the 3x3 L_a; -0.2 at l = 2; -1/14 at l = 3)"
        let frobEig (gens: float[][][]) = (Lie.analyze 1 gens).KappaEig
        vecL "so3_kappa_frob_eig" (frobEig Lie.so3Std)
        for l in 1 .. 3 do
            vecL $"so3_kappa_frob_eig_l{l}" (frobEig (tables l))

        // ---------------- 3. small algebras ----------------
        printfn ""
        printfn "// ===== 3. small-algebra invariants (check_b4b), every rank decision at 1e-9 ====="
        printfn "// *_inv = (dim, dim [g,g], dim center, dim radical, n+, n-, n0 of kappa, rank); radical = [g,g]^perp-kappa"
        printfn "// (Cartan); rank = dim ker ad_X^dim for a seeded X (System.Random(2))."
        printfn "// *_killing_eig = kappa's eigenvalues (descending) in a Frobenius-orthonormal basis of the span:"
        printfn "// basis-free for the given matrices (an orthogonal change of basis is a congruence); the SIGNATURE"
        printfn "// is basis-free outright, the values scale with the representation's trace form (cf. sl2 vs so21)."
        let algebras =
            [ ("alg_so3", "so(3), standard 3x3 L_x, L_y, L_z", Lie.so3Std, [| 3; 3; 0; 0; 0; 3; 0; 1 |])
              ("alg_so21", "so(2,1): A^T eta + eta A = 0, eta = diag(1, -1, -1); E01+E10, E02+E20, E21-E12",
               Lie.so21, [| 3; 3; 0; 0; 2; 1; 0; 1 |])
              ("alg_sl2", "sl(2,R) 2x2: H = E00-E11, E01, E10", Lie.sl2, [| 3; 3; 0; 0; 2; 1; 0; 1 |])
              ("alg_se2", "se(2) homogeneous 3x3: E10-E01, E02, E12", Lie.se2, [| 3; 2; 0; 3; 0; 1; 2; 1 |])
              ("alg_heis3", "heis3 upper triangular: E01, E12, E02", Lie.heis3, [| 3; 1; 1; 3; 0; 0; 3; 3 |])
              ("alg_u2", "u(2) realified 4x4 ([[a, -b], [b, a]] for a + ib): iI, i sigma_z, i sigma_x, i sigma_y",
               Lie.u2, [| 4; 3; 1; 1; 0; 3; 1; 2 |])
              ("alg_gl2", "gl(2): E00, E01, E10, E11", Lie.gl2, [| 4; 3; 1; 1; 2; 1; 1; 2 |])
              ("alg_se11", "se(1,1) homogeneous 3x3: E10+E01, E02, E12", Lie.se11, [| 3; 2; 0; 3; 1; 0; 2; 1 |])
              ("alg_so4", "so(4) = so(3) + so(3): E_ij - E_ji, i < j", Lie.so4, [| 6; 6; 0; 0; 0; 6; 0; 2 |])
              ("alg_so31", "so(3,1) Lorentz: rotations E_ij - E_ji (1 <= i < j), boosts E0j + Ej0", Lie.so31,
               [| 6; 6; 0; 0; 3; 3; 0; 2 |])
              ("alg_se3", "se(3) homogeneous 4x4: rotations E21-E12, E02-E20, E10-E01, translations E_i3", Lie.se3,
               [| 6; 6; 0; 3; 0; 3; 3; 2 |])
              ("alg_sl2r2", "sl(2) |x R^2 homogeneous 3x3: H, E01, E10 (padded), E02, E12", Lie.sl2R2,
               [| 5; 5; 0; 2; 2; 1; 2; 1 |]) ]
        for (tag, title, gens, expect) in algebras do
            let rep = Lie.analyze 2 gens
            algebraBlock tag title gens rep
            require (invTuple rep = expect) $"{tag} invariants {invTuple rep} vs RIGOR.md B4 (vii) {expect}"
        let j2 = [| [| 0.0; -1.0 |]; [| 1.0; 0.0 |] |]
        let su2H = [| Lie.qmat 0.0 1.0 0.0 0.0; Lie.qmat 0.0 0.0 1.0 0.0; Lie.qmat 0.0 0.0 0.0 1.0 |]
        let comm =
            [| Lie.commutantDim [| j2 |]
               Lie.commutantDim [| Lie.kron (Lie.eye 2) j2 |]
               Lie.commutantDim [| Lie.blockDiag [ j2; Lie.mscale 2.0 j2 ] |]
               Lie.commutantDim su2H
               Lie.commutantDim (tables 1)
               Lie.commutantDim (tables 2) |]
        printfn "// commutant dims dim End_g(V) = dim ker of the stacked (I (x) G^T - G (x) I):"
        printfn "//   (so(2) on R^2, u(1) charge 1 on R^4 = 2 copies, u(1) charges (1, 2) on R^4, su(2) on H by left"
        printfn "//    multiplication, so(3) on D_1, so(3) on D_2) -- R/C/H types: 2 = C, 8 = M_2(C), 4 = C + C, 4 = H, 1 = R"
        arrInt "commutant_dims" comm
        require (comm = [| 2; 8; 4; 4; 1; 1 |]) "commutant dims"
        let su2Cas = Lie.casimirSum (su2H |> Array.map (Lie.mscale 0.5))
        let _, su2Eig = Lie.eighJ (Lie.symmetrize su2Cas)
        printfn "// su(2) on H, generators q/2 ([L_x, L_y] = L_z normalization): Casimir eigenvalues (half-integer j = 1/2:"
        printfn "// -3/4; the real-harmonic tables do NOT apply -- the canonicalizer's flag-and-stop case)"
        vecL "su2_h_casimir_eig" su2Eig

        // ---------------- 4. moment annihilation ----------------
        printfn ""
        printfn "// ===== 4. moment annihilation A |-> rho_k(A) M_k (RIGOR.md B1.5) ====="
        let tuples4 = Lie.canonTuples 4 3
        printfn "// SymIdx<4, 3> canonical cells: i <= j <= k <= l, lexicographic (15); scale = sqrt(4! / prod n_i!)"
        matIntL "sym4_tuples" tuples4
        vecL "sym4_scale" (tuples4 |> Array.map Lie.tupleScale)
        printfn "// isotropic: E[z^(x)4], z uniform on S^2 = (d_ij d_kl + d_ik d_jl + d_il d_jk) / 15"
        vecL "iso4_cells" (tuples4 |> Array.map (fun t -> Lie.iso4.[Lie.flatIndex 3 t]))
        printfn "// octahedral: E[z^(x)4], z uniform on {+-e_a} = (1/3) Sum_a e_a^(x)4 (cubic anisotropy, finite stabilizer)"
        vecL "oct4_cells" (tuples4 |> Array.map (fun t -> Lie.oct4.[Lie.flatIndex 3 t]))
        printfn "// unknowns: gl3 = E_ij, (i, j) row-major (9); so3 = E_il - E_li, (i, l) = (0,1), (0,2), (1,2) (3)"
        printfn "//   [note: E01 - E10 = -L_z, E02 - E20 = +L_y, E12 - E21 = -L_x]"
        printfn "// *_op_* = the 15 x p constraint matrix, column j = rho_4(B_j) M4 on the canonical cells, row x scale;"
        printfn "// *_sv_*_scaled its singular values (== the dense 81-row operator's: row norms are Frobenius norms);"
        printfn "// *_sv_*_unscaled the same without the scale (wrong geometry, same nullspace)."
        let mom4 =
            [ ("iso4", Lie.iso4, [ ("gl3", Lie.gl3Basis, 3); ("so3", Lie.so3Basis, 3) ])
              ("oct4", Lie.oct4, [ ("gl3", Lie.gl3Basis, 0); ("so3", Lie.so3Basis, 0) ]) ]
        for (tag, mom, bases) in mom4 do
            for (btag, basis, expectZeros) in bases do
                let cS = Lie.momentOperator 4 3 mom basis Lie.Scaled
                let cU = Lie.momentOperator 4 3 mom basis Lie.Unscaled
                let cD = Lie.momentOperator 4 3 mom basis Lie.Dense
                let sS = fst (Lie.svdPad cS)
                let sU = fst (Lie.svdPad cU)
                let sD = fst (Lie.svdPad cD)
                let dev = MathUtils.maxAbsDiff sS sD
                printfn "// %s on %s: %d singular values <= 1e-9 (expect %d); |sv_scaled - sv_dense81|_max = %s"
                    tag btag (zeroCount sS) expectZeros (sci dev)
                require (zeroCount sS = expectZeros && zeroCount sU = expectZeros) $"{tag} on {btag}: zero count"
                require (dev <= 1e-13) $"{tag} on {btag}: scaled vs dense singular values"
                matL $"{tag}_op_{btag}" cS
                vecL $"{tag}_sv_{btag}_scaled" sS
                vecL $"{tag}_sv_{btag}_unscaled" sU
        let tuples2 = Lie.canonTuples 2 3
        let m2 = Lie.vecRM (Lie.eye 3)
        let k2D = Lie.momentOperator 2 3 m2 Lie.gl3Basis Lie.Dense
        let k2S = Lie.momentOperator 2 3 m2 Lie.gl3Basis Lie.Scaled
        let k2sD = fst (Lie.svdPad k2D)
        let k2sS = fst (Lie.svdPad k2S)
        printfn "// k = 2, M2 = I: A |-> A M2 + M2 A^T = A + A^T on gl3. Dense = 9 rows (i, j) row-major; scaled ="
        printfn "// the 6 SymIdx<2, 3> cells x sqrt(2! / prod n_i!), zero-row padded to 9 x 9 (m.svd needs m >= n)."
        printfn "// zeros: dense %d, scaled %d (expect 3 = so(3))" (zeroCount k2sD) (zeroCount k2sS)
        require (zeroCount k2sD = 3 && zeroCount k2sS = 3) "k = 2 zero count"
        matIntL "sym2_tuples" tuples2
        vecL "sym2_scale" (tuples2 |> Array.map Lie.tupleScale)
        matL "k2_op_gl3_dense" k2D
        vecL "k2_sv_gl3_dense" k2sD
        vecL "k2_sv_gl3_scaled" k2sS

        // ---------------- 5. canonicalization lighthouse ----------------
        printfn ""
        printfn "// ===== 5. canonicalization lighthouse (check_b4; RIGOR.md B4 (viii)) ====="
        let cr = Lie.canonicalize ()
        printfn "// rep 0 + 1 + 1 + 2 (d = %d): canon_target_a = blockdiag(lie_ja_l0, lie_ja_l1, lie_ja_l1, lie_ja_l2)" cr.D
        printfn "// disguise S = I + %s (2u - 1), u ~ System.Random(%d).NextDouble() row-major, each entry"
            (f2s Lie.disguiseAmp) Lie.disguiseSeed
        printfn "// Math.Round(_, 2) (exact to type); S^-1 by Gauss-Jordan. sigma(S) in [%s, %s], cond(S) = %s"
            (f2s (Array.min cr.SSv)) (f2s (Array.max cr.SSv)) (f2s (Array.max cr.SSv / Array.min cr.SSv))
        matL "canon_s" cr.S
        matL "canon_s_inv" cr.SInv
        for a in axes do
            matL $"canon_target_{ax a}" cr.Target.[a]
        printfn "// J~_a = (S . target_a) . S^-1 (that association, m.matmul's i, j, t-ascending loop)"
        for a in axes do
            matL $"canon_jt_{ax a}" cr.Jt.[a]
        printfn "// (0) Frobenius-orthonormal basis E_i of span{J~_a}: the left singular vectors of the 144 x 3"
        printfn "//     [vec J~_x | vec J~_y | vec J~_z] (row-major vec), reshaped 12 x 12; structure constants"
        printfn "//     c(i, j, k) = <E_k, [E_i, E_j]>; closure residual = %s" (sci cr.Closure)
        vecL "canon_e_sv" cr.ESv
        for i in 0 .. 2 do
            matL $"canon_e{i}" cr.E.[i]
        ten3L "canon_e_c" cr.EC
        matL "canon_e_kappa" cr.KappaE
        vecL "canon_e_kappa_eig" cr.KappaEEig
        let osv = cr.OmegaSv
        let nOm = osv.Length
        printfn "// (i) invariant metric: Omega = -2 Sum_ij kinv_ij rhoW(E_i) rhoW(E_j) (144 x 144, kinv = canon_e_kappa^-1),"
        printfn "//     rhoW(E) vec(S) = -vec(E^T S + S E) = -(kron(E^T, I) + kron(I, E^T)) vec(S); N_r, N_l = the right"
        printfn "//     null bases of Omega and Omega^T (svd, rcond 1e-9); M = sym(N_r (N_l^T N_r)^-1 N_l^T vec(I)) -- the"
        printfn "//     spectral projector of vec(I) = the Haar average of g^T g: basis-free, so canon_m pins entry by entry."
        printfn "//     dim ker Omega = %d right, %d left (expect 6 = 1 + 4 + 1); gap: sigma[%d] = %s vs sigma[%d] = %s, sigma_max = %s"
            cr.OmegaNullR cr.OmegaNullL (nOm - 7) (sci osv.[nOm - 7]) (nOm - 6) (sci osv.[nOm - 6]) (sci osv.[0])
        require (cr.OmegaNullR = 6 && cr.OmegaNullL = 6) "dim ker Omega"
        matL "canon_m" cr.M
        vecL "canon_m_eig" cr.MEig
        printfn "//     M > 0: min eigenvalue %s; invariance max_a |J~_a^T M + M J~_a| = %s" (f2s (Array.min cr.MEig)) (sci cr.MInvResid)
        require (Array.min cr.MEig > 0.0 && cr.MInvResid <= 1e-10) "invariant metric"
        printfn "// (ii) R = M^(1/2) = Q diag(sqrt lam) Q^T (eigh), R^-1 = Q diag(1/sqrt lam) Q^T; L_a = (R . J~_a) . R^-1"
        matL "canon_r" cr.R
        matL "canon_r_inv" cr.RInv
        for a in axes do
            matL $"canon_l_{ax a}" cr.LGen.[a]
        printfn "//     max |L_a + L_a^T| = %s; bracket residual of L = %s" (sci cr.LAntisym) (sci cr.LBracket)
        require (cr.LAntisym <= 1e-10) "antisymmetry after R"
        printfn "// (iii) (KQ, KLAM) = eigh(-canon_e_kappa / 2); J_i = Sum_j KQ(j, i) / sqrt(KLAM(i)) (R E_j R^-1);"
        printfn "//     orientation c_12^3 = <J_2, [J_0, J_1]> / <J_2, J_2> = %s -> %s"
            (f2s cr.C123) (if cr.C123 < 0.0 then "negative, all three NEGATED" else "positive, kept")
        matL "canon_kappa_q" cr.KQ
        vecL "canon_kappa_lam" cr.KLam
        for a in axes do
            matL $"canon_j_{ax a}" cr.J.[a]
        matL "canon_j_kappa" cr.JKappa
        printfn "//     max |J_a + J_a^T| = %s; [J_x,J_y]-J_z, [J_y,J_z]-J_x, [J_z,J_x]-J_y max = %s" (sci cr.JAntisym) (sci cr.JBracket)
        require (cr.JBracket <= 1e-10) "frame brackets"
        printfn "// (iv) Casimir C = Sum_a J_a^2 (frame-free: = R (Casimir of J~) R^-1, so pinnable entry by entry)"
        matL "canon_casimir" cr.Casimir
        vecL "canon_casimir_eig" cr.CasimirEig
        let casExpect = Array.append [| 0.0 |] (Array.append (Array.create 6 -2.0) (Array.create 5 -6.0))
        let casDev = MathUtils.maxAbsDiff cr.CasimirEig casExpect
        printfn "//     |eig - (0, -2 x6, -6 x5)|_max = %s" (sci casDev)
        require (casDev <= 1e-10) "Casimir spectrum"
        printfn "// (v) per l: T with T J_a = D_a^(l) T, the stacked (I_n (x) J_a^T - D_a (x) I_12) nullspace (rcond 1e-8),"
        printfn "//     Loewdin-orthonormalized (G^-1/2 by eigh) and scaled by sqrt(2l+1); U = the T stacked, l ascending."
        for b in cr.Blocks do
            printfn "//     l = %d: Hom dim %d, max |T T^T - I| = %s" b.Deg b.Mult (sci b.TTResid)
        arrInt "canon_hom_dims" (cr.Blocks |> List.map (fun b -> b.Mult) |> Array.ofList)
        require ((cr.Blocks |> List.map (fun b -> b.Mult)) = [ 1; 2; 1 ]) "Hom dims"
        printfn "//     U is fixed only up to the residual gauge: a sign per n_l = 1 block and O(2) on the l = 1 pair --"
        printfn "//     pin U J_a U^T and the residuals, not U's entries."
        matL "canon_u" cr.U
        printfn "//     max |U U^T - I| = %s" (sci cr.UOrth)
        require (cr.UOrth <= 1e-10) "U orthogonal"
        let comm12 = Lie.commutantDim cr.J
        printfn "//     commutant of the canonical frame: dim End_g(V) = %d (1 + 4 + 1)" comm12
        require (comm12 = 6) "lighthouse commutant"
        printfn "// FINAL: max_a |U J_a U^T - canon_target_a| = %s" (sci cr.Resid)
        require (cr.Resid <= 1e-10) "canonicalization residual"

        // ---------------- 6. functional lane ----------------
        printfn ""
        printfn "// ===== 6. functional lane (RIGOR.md B3.2, invariance B = 0) ====="
        printfn "// rows vec(grad f(x_n) x_n^T), entry (i, j) row-major = d_i f(x_n) * x_n[j]: (grad f . A x_n) = row . vec(A)."
        printfn "// *_sv = the 9 singular values over gl(3) (8 rows zero-padded to 9 x 9); *_null = V's null columns as"
        printfn "// 3 x 3 generators (a basis of a DEGENERATE singular subspace: pin *_proj, the basis-free projector);"
        printfn "// *_inv / *_killing_eig as in section 3."
        matL "fn_points" Lie.functionalPoints
        let lane (tag: string) (title: string) (grad: float[] -> float[]) (expectNull: int) (expectInv: int[]) =
            let fr = Lie.functionalLane 3 grad Lie.functionalPoints
            printfn "// %s: %d zero singular values (expect %d)" title fr.Null.Length expectNull
            require (fr.Null.Length = expectNull) $"{tag} nullity"
            matL $"{tag}_rows" fr.Rows
            vecL $"{tag}_sv" fr.Sv
            ten3L $"{tag}_null" fr.Null
            matL $"{tag}_proj" fr.Proj
            match fr.Report with
            | Some rep ->
                let np, nm, n0 = rep.Signature
                printfn "// %s nullspace algebra: dim %d, [g,g] %d, center %d, radical %d, kappa (+,-,0) = (%d,%d,%d), rank %d; closure %s"
                    tag rep.Dim rep.Derived rep.Center rep.Radical np nm n0 rep.Rank (sci rep.Closure)
                arrInt $"{tag}_inv" (invTuple rep)
                vecL $"{tag}_killing_eig" rep.KappaEig
                require (invTuple rep = expectInv) $"{tag} invariants {invTuple rep} vs {expectInv}"
            | None -> ()
            fr
        let hr =
            lane "fn_h" "h(x) = x0^2 - x1^2 - x2^2 (Lorentz-invariant; expect so(2,1))"
                (fun x -> [| 2.0 * x.[0]; -2.0 * x.[1]; -2.0 * x.[2] |]) 3 [| 3; 3; 0; 0; 2; 1; 0; 1 |]
        let hProjDev = Lie.maxDiffM hr.Proj Lie.so21Projector
        printfn "// fn_h_proj vs the exact so(2,1) projector (1/2) Sum vec(b) vec(b)^T over alg_so21_basis: max dev = %s" (sci hProjDev)
        require (hProjDev <= 1e-12) "fn_h projector vs exact so(2,1)"
        let _ =
            lane "fn_f" "f(x) = x0^2 + x1^2 ON R^3 (x2 FREE: so(2) PLUS the three E_2j that only move x2)"
                (fun x -> [| 2.0 * x.[0]; 2.0 * x.[1]; 0.0 |]) 4 [| 4; 2; 0; 4; 1; 1; 2; 2 |]
        let _ =
            lane "fn_g" "g(x) = x0^2 + x1^2 + x2 (the linear term pins E_2j = 0: so(2) alone, abelian, kappa = 0)"
                (fun x -> [| 2.0 * x.[0]; 2.0 * x.[1]; 1.0 |]) 1 [| 1; 0; 1; 1; 0; 0; 1; 1 |]

        // ---------------- summary ----------------
        printfn ""
        printfn "// ===== summary ====="
        printfn "// tables vs wignerD derivative %s; table algebra %s; l = 1 vs P L P^T %s; structure constants %s"
            (sci fdDev) (sci tabAlg) (sci pDev) (sci scDev)
        printfn "// canonicalization: M invariance %s, antisymmetry %s, frame brackets %s, Casimir %s, U U^T %s, FINAL %s"
            (sci cr.MInvResid) (sci cr.LAntisym) (sci cr.JBracket) (sci casDev) (sci cr.UOrth) (sci cr.Resid)
        printfn "// functional: fn_h projector vs exact so(2,1) %s" (sci hProjDev)
        printfn "// Jacobi: converged calls needed <= %d rotating sweeps (m.svd / m.eigh default budget: 10); %d call(s) hit"
            Lie.sweepsHighWater Lie.cappedCalls
        printfn "// the %d-sweep cap on a numerically-zero column gone subnormal (the 1e-15 guard cannot pass; its sigma"
            Lie.maxSweeps
        printfn "// is ~1e-158 either way, every nonzero sigma / vector converged): %s"
            (if Lie.cappedCalls = 0 then "none" else String.concat ", " Lie.capLog)
