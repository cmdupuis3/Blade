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
