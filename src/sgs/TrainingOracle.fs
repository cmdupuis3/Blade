namespace BladeSgs

open BladeML

/// The a-priori closure-training oracle (corpus sgs/006-007): filtered
/// velocity gradients -> bridged irreps inputs, exact SGS stresses ->
/// bridged targets, and the certified model
///   p = derive_linear(HSPEC, TAUSPEC, w2, gated(HSPEC, derive_tp(GSPEC, GSPEC, g, g, w1)))
/// trained by full-batch SGD on loss = sum_s |p_s - t_s|^2 (via ml.norms of
/// the same-spec difference — the certified invariant exit). The Blade twin
/// trains the SAME model through ad.grad over the compiler-synthesized
/// layers; this replica pins the trajectory via the BladeML VJPs.
///
/// Data: the 8 W=2 tiles of the N=4 divergence-free field (Oracle.fs
/// config). Input g_s = bridge9(mean of central-difference gradients over
/// the tile) — the box filter and the FD gradient commute on a periodic
/// uniform grid, so the tile mean IS the filtered gradient. Target
/// t_s = sym_to_irr(packSym(tau_s)).
module TrainingOracle =

    let private f2s (v: float) : string =
        let s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
        if s.Contains "." || s.Contains "e" || s.Contains "E" then s else s + ".0"

    let private arr (name: string) (xs: float[]) =
        printfn "%s = [%s]" name (xs |> Array.map f2s |> String.concat ", ")

    // ---- the frozen bridge closed forms (source of truth: ml/ dump-cartesian,
    // where they are certified against the SphericalHarmonics fit) ----
    let private sqrt2 = sqrt 2.0
    let private sqrt3 = sqrt 3.0
    let private sqrt6 = sqrt 6.0
    let private mkRow (entries: (int * int * float) list) : float[] =
        let r = Array.zeroCreate 9
        for (i, j, c) in entries do r.[3 * i + j] <- c
        r
    let private bridge9 : float[][] =
        [| mkRow [ (0, 0, 1.0 / sqrt3); (1, 1, 1.0 / sqrt3); (2, 2, 1.0 / sqrt3) ]
           mkRow [ (0, 2, 1.0 / sqrt2); (2, 0, -1.0 / sqrt2) ]
           mkRow [ (1, 0, 1.0 / sqrt2); (0, 1, -1.0 / sqrt2) ]
           mkRow [ (2, 1, 1.0 / sqrt2); (1, 2, -1.0 / sqrt2) ]
           mkRow [ (0, 1, 1.0 / sqrt2); (1, 0, 1.0 / sqrt2) ]
           mkRow [ (1, 2, 1.0 / sqrt2); (2, 1, 1.0 / sqrt2) ]
           mkRow [ (0, 0, -1.0 / sqrt6); (1, 1, -1.0 / sqrt6); (2, 2, 2.0 / sqrt6) ]
           mkRow [ (0, 2, 1.0 / sqrt2); (2, 0, 1.0 / sqrt2) ]
           mkRow [ (0, 0, 1.0 / sqrt2); (1, 1, -1.0 / sqrt2) ] |]
    let private symToIrr : float[][] =
        [| [| 1.0 / sqrt3; 0.0; 0.0; 1.0 / sqrt3; 0.0; 1.0 / sqrt3 |]
           [| 0.0; sqrt2; 0.0; 0.0; 0.0; 0.0 |]
           [| 0.0; 0.0; 0.0; 0.0; sqrt2; 0.0 |]
           [| -1.0 / sqrt6; 0.0; 0.0; -1.0 / sqrt6; 0.0; 2.0 / sqrt6 |]
           [| 0.0; 0.0; sqrt2; 0.0; 0.0; 0.0 |]
           [| 1.0 / sqrt2; 0.0; 0.0; -1.0 / sqrt2; 0.0; 0.0 |] |]

    let private matvecFlat (n: int) (m: float[]) (x: float[]) : float[] =
        [| for i in 0 .. n - 1 ->
             let mutable acc = 0.0
             for j in 0 .. n - 1 do
                 acc <- acc + m.[i * n + j] * x.[j]
             acc |]

    // ---- tile data from the Oracle.fs field ----
    let private nGrid = 4
    let private h = System.Math.PI / 2.0
    let private fieldAt (c: int) (i: int) (j: int) (k: int) : float =
        let x = float i * h
        let y = float j * h
        let z = float k * h
        match c with
        | 0 -> Oracle.u0 x y z
        | 1 -> Oracle.u1 x y z
        | _ -> Oracle.u2 x y z

    /// Central-difference gradient G[c][d] = d_d u_c at (i,j,k), periodic.
    let private fdGrad (i: int) (j: int) (k: int) : float[][] =
        let w a = ((a % nGrid) + nGrid) % nGrid
        Array.init 3 (fun c ->
            [| (fieldAt c (w (i + 1)) j k - fieldAt c (w (i - 1)) j k) / (2.0 * h)
               (fieldAt c i (w (j + 1)) k - fieldAt c i (w (j - 1)) k) / (2.0 * h)
               (fieldAt c i j (w (k + 1)) - fieldAt c i j (w (k - 1))) / (2.0 * h) |])

    /// (g_s, t_s) for the 8 tiles, tile order (ti, tj, tk) ascending lex,
    /// in-tile sample order (di, dj, dk) ascending lex.
    let private samples () : (float[] * float[])[] =
        [| for ti in 0 .. 1 do
             for tj in 0 .. 1 do
               for tk in 0 .. 1 do
                 let pts =
                     [| for di in 0 .. 1 do
                          for dj in 0 .. 1 do
                            for dk in 0 .. 1 do
                              yield (2 * ti + di, 2 * tj + dj, 2 * tk + dk) |]
                 // filtered gradient = tile mean of point FD gradients
                 let gbar = Array.init 3 (fun _ -> Array.zeroCreate 3)
                 for (i, j, k) in pts do
                     let g = fdGrad i j k
                     for c in 0 .. 2 do
                         for d in 0 .. 2 do
                             gbar.[c].[d] <- gbar.[c].[d] + g.[c].[d]
                 for c in 0 .. 2 do
                     for d in 0 .. 2 do
                         gbar.[c].[d] <- gbar.[c].[d] / 8.0
                 let gFlat = [| for c in 0 .. 2 do for d in 0 .. 2 do yield gbar.[c].[d] |]
                 let gIn = MathUtils.matVec bridge9 gFlat
                 // exact stress: central second comoment over the tile
                 let rows =
                     Array.init 3 (fun c -> pts |> Array.map (fun (i, j, k) -> fieldAt c i j k))
                 let t = 8.0
                 let mu = rows |> Array.map (fun r -> Array.sum r / t)
                 let prodsum (a: float[]) (b: float[]) =
                     let mutable acc = 0.0
                     for q in 0 .. a.Length - 1 do
                         acc <- acc + a.[q] * b.[q]
                     acc
                 let pairs = [| (0, 0); (0, 1); (0, 2); (1, 1); (1, 2); (2, 2) |]
                 let cen = pairs |> Array.map (fun (a, b) -> prodsum rows.[a] rows.[b] / t - mu.[a] * mu.[b])
                 let tOut = MathUtils.matVec symToIrr cen
                 yield (gIn, tOut) |]

    // ---- the model and its VJP chain ----
    let private gspec = Irreps.mkSpec [ (0, Even, 1); (1, Even, 1); (2, Even, 1) ]
    let private tspec = Irreps.mkSpec [ (0, Even, 1); (2, Even, 1) ]
    let private hspec = Irreps.tpSpec gspec gspec
    let private cfg : TPConfig = { Spec1 = gspec; Spec2 = gspec; SpecOut = hspec }

    let private fwd (w1: float[]) (w2: float[]) (g: float[]) : float[] =
        let hRaw = TensorProduct.tensorProduct cfg w1 g g
        let a = Activations.gated hspec hRaw
        Linear.homLinear hspec tspec w2 a

    /// Per-sample loss EXACTLY as the Blade twin computes it (sq6 helper):
    /// acc over k ascending of (p_k - t_k)^2.
    let private sampleLoss (w1: float[]) (w2: float[]) (g: float[]) (t: float[]) : float =
        let p = fwd w1 w2 g
        let mutable acc = 0.0
        for k in 0 .. 5 do
            let d = p.[k] - t.[k]
            acc <- acc + d * d
        acc

    let dump () =
        // Every OTHER tile: the 8 tiles of this field come in near-duplicate
        // pairs (t0~t1, t2~t3, ...), and the halved training set keeps the
        // grad-expanded corpus twin under the harness's 60s compile budget
        // (the full 8-sample unroll compiled in ~23s standalone and timed
        // out under full-suite parallel g++ load).
        let data = samples () |> Array.indexed |> Array.choose (fun (i, s) -> if i % 2 = 0 then Some s else None)
        printfn "// ===== sgs closure training oracle (sgs/006-007) ====="
        let specStr (s: SpecEntry[]) =
            s
            |> Array.map (fun e ->
                sprintf "(%d, %d, %d)" e.Ir.L (if e.Ir.P = Even then 0 else 1) e.Mult)
            |> String.concat ", "
        printfn "// GSPEC = [%s]  TAUSPEC = [%s]" (specStr gspec) (specStr tspec)
        printfn "// HSPEC = tp_spec(GSPEC, GSPEC) = [%s]  total_dim = %d" (specStr hspec) (Irreps.totalDim hspec)
        let w1dim = TensorProduct.weightDim cfg
        let w2dim = Irreps.homDim hspec tspec
        printfn "// tp_full_weight_dim = %d  hom_dim = %d" w1dim w2dim
        for s in 0 .. data.Length - 1 do
            arr (sprintf "train_g%d" s) (fst data.[s])
            arr (sprintf "train_t%d" s) (snd data.[s])
        let w10 = Array.init w1dim (fun i -> 0.03 * float (i + 1) * (if i % 2 = 0 then 1.0 else -1.0))
        let w20 = Array.init w2dim (fun i -> 0.1 * float (i + 1) * (if i % 2 = 0 then 1.0 else -1.0))
        arr "w1_init" w10
        arr "w2_init" w20

        let lr = 0.05
        let mutable w1 = Array.copy w10
        let mutable w2 = Array.copy w20
        let losses =
            [| for _step in 1 .. 6 ->
                 let dW1 = Array.zeroCreate w1dim
                 let dW2 = Array.zeroCreate w2dim
                 let mutable lossAcc = 0.0
                 for s in 0 .. data.Length - 1 do
                     let g, t = data.[s]
                     let hRaw = TensorProduct.tensorProduct cfg w1 g g
                     let a = Activations.gated hspec hRaw
                     let p = Linear.homLinear hspec tspec w2 a
                     let mutable sAcc = 0.0
                     for k in 0 .. 5 do
                         let d = p.[k] - t.[k]
                         sAcc <- sAcc + d * d
                     lossAcc <- lossAcc + sAcc
                     let dD = Array.init 6 (fun k -> 2.0 * (p.[k] - t.[k]))
                     let dw2, dA = Autodiff.vjpHomLinear hspec tspec w2 a dD
                     let dH = Autodiff.vjpGated hspec hRaw dA
                     let dw1, _, _ = Autodiff.vjpTensorProduct cfg w1 g g dH
                     for i in 0 .. w1dim - 1 do
                         dW1.[i] <- dW1.[i] + dw1.[i]
                     for i in 0 .. w2dim - 1 do
                         dW2.[i] <- dW2.[i] + dw2.[i]
                 w1 <- Array.map2 (fun w gr -> w - lr * gr) w1 dW1
                 w2 <- Array.map2 (fun w gr -> w - lr * gr) w2 dW2
                 lossAcc |]
        arr "train_losses" losses
        arr "w1_final" w1
        arr "w2_final" w2

        // ---- 007: equivariance certificate of the TRAINED closure ----
        // Both routes at the house rotation R = Rz(0.7)·Ry(1.1), final
        // weights: model(D_g·g0) vs D_tau·model(g0); plus loss invariance
        // (rotated sample AND rotated target -> identical loss). The
        // rotated inputs are printed for baking; the Blade twin reuses the
        // d_gspec/d_tauspec matrices from dump-cartesian.
        let r = MathUtils.matMul (Rotations.rotZ 0.7) (Rotations.rotY 1.1)
        let dg = Rotations.repMatrix gspec r
        let dt = Rotations.repMatrix tspec r
        let g0, t0 = data.[0]
        let g0rot = matvecFlat 9 dg g0
        let t0rot = matvecFlat 6 dt t0
        arr "cert7_g0_rot" g0rot
        arr "cert7_t0_rot" t0rot
        let pPlain = fwd w1 w2 g0
        let pRotRoute = fwd w1 w2 g0rot
        let pDtauRoute = matvecFlat 6 dt pPlain
        arr "cert7_p" pPlain
        arr "cert7_p_rot" pRotRoute
        arr "cert7_p_dtau" pDtauRoute
        printfn "// 007 certificate: |model(Dg) - D model(g)|_max = %g"
            (MathUtils.maxAbsDiff pRotRoute pDtauRoute)
        let l0 = sampleLoss w1 w2 g0 t0
        let lRot = sampleLoss w1 w2 g0rot t0rot
        printfn "cert7_loss = %s" (f2s l0)
        printfn "cert7_loss_rot = %s" (f2s lRot)
        printfn "// 007 loss invariance: |diff| = %g" (abs (l0 - lRot))
