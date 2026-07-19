namespace BladeML

/// Prints the training oracle's fixed inputs and pinned outputs in a
/// copy-pasteable form for authoring (and re-pinning) the Blade e2e example:
/// dataset literals, initial weights, CG tables for the two TP configs,
/// step-0 gradients, and the loss trajectory. Run:
///   dotnet run --project ml -- dump-oracle
module OracleDump =

    /// Full round-trip precision, invariant culture — the same spelling
    /// discipline the compiler's floatToCppLiteral uses.
    let private f2s (v: float) : string =
        let s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

    let private arr (name: string) (xs: float[]) =
        printfn "%s = [%s]" name (xs |> Array.map f2s |> String.concat ", ")

    let private arrInt (name: string) (xs: int[]) =
        printfn "%s = [%s]" name (xs |> Array.map string |> String.concat ", ")

    /// The sparse real-CG entries of every path of a TP config, flattened in
    /// path order — exactly the constant tables the Blade example needs.
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
            let c1 = cg |> Array.map (fun e -> e.C1)
            let c2 = cg |> Array.map (fun e -> e.C2)
            let c3 = cg |> Array.map (fun e -> e.C3)
            let co = cg |> Array.map (fun e -> e.Coef)
            arrInt (sprintf "%s_p%d_c1" label pi) c1
            arrInt (sprintf "%s_p%d_c2" label pi) c2
            arrInt (sprintf "%s_p%d_c3" label pi) c3
            arr (sprintf "%s_p%d_coef" label pi) co

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
            arr (sprintf "pos%d" s) g.Pos
            arr (sprintf "feat%d" s) g.NodeFeat
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
        // The FULL CG decomposition of 1o ⊗ 1o via Irreps.tpSpec, pushed
        // through the reference tensor product on fixed inputs — the value
        // pins for the compiler's `ml.tp_spec` round-trip test.
        printfn ""
        printfn "// ===== tp_spec pins (ml-ops/009): full 1o (x) 1o ====="
        let s1o = Irreps.mkSpec [ (1, Odd, 1) ]
        let sFull = Irreps.tpSpec s1o s1o
        let specStr (s: SpecEntry[]) =
            s
            |> Array.map (fun e ->
                sprintf "(%d, %d, %d)" e.Ir.L (if e.Ir.P = Even then 0 else 1) e.Mult)
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
        // fixed weights — the value pins for the certified-pipeline test.
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
        // Certified model derive_linear -> norms; loss = Σ (norms − tgt)²;
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
    /// Fixed proper rotation R = Rz(0.7)·Ry(1.1) (the ml-e2e convention);
    /// per test both routes are printed — f(D_in·x) and D_out·f(x) — as
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
        printfn "// ===== equiv certificates (fixed R = Rz(0.7)·Ry(1.1)) ====="
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
