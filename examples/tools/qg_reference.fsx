// qg_reference.fsx — independent reference implementation for
// examples/09_qg_atmosphere.blade. Prints every EXPECT pin value.
//
// THE MIRROR CONTRACT (the sgs/spectra house rule): this file performs the
// SAME floating-point operations in the SAME order as the compiled example.
//   - Initial fields and the exponential filter are read from the COMMITTED
//     zarr chunk bytes (examples/data/qg_init_zarr) — byte-identical input.
//   - fft2/ifft2 come from spectra/Fft.fs — the same bit-exact mirror the
//     spectra corpus pins the generated transforms against.
//   - Every stage function below mirrors its namesake in the example
//     (invert/uhat/vhat/flux/tendency/ab3_fold/spec_sum/absmax),
//     expression for expression — including the sign-of-zero-relevant
//     forms: the barotropic inversion's `+ 0.0 * qh2` term, the flux's
//     `(u + ubg)`, the tendency's always-present qy/rek terms, the unary
//     minus (not `0.0 -`), and the rotating AB3 history buffers whose
//     stale cross-part contents are zero-multiplied on startup steps.
//     real*complex and complex+complex are componentwise on both sides
//     (std::complex matches bit-for-bit); no complex division, no
//     transcendentals anywhere in the solver.
//
// Run from anywhere:  dotnet fsi examples/tools/qg_reference.fsx

#load "../../src/spectra/Fft.fs"

open System
open System.Globalization
open BladeSpectra.Fft

let N = 64
let M = N * N

let fmt (x: float) = x.ToString("G17", CultureInfo.InvariantCulture)

// ---- committed store bytes (exactly what the compiled example reads) ------
let root = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let chunkPath name key = IO.Path.Combine(root, "examples", "data", "qg_init_zarr", name, "c", key)
let readChunk name key =
    let bs = IO.File.ReadAllBytes(chunkPath name key)
    [| for t in 0 .. bs.Length / 8 - 1 -> BitConverter.ToDouble(bs, t * 8) |]
let qRossby = readChunk "q_rossby" "0/0"
let qPred = readChunk "q_pred_rossby" "0"
let qMcw = readChunk "q_mcw" "0/0"
let q1Init = readChunk "q1_init" "0/0"
let filtw = readChunk "filtr" "0/0"

// ---- spectral tables (mirror the example's kernels) -----------------------
let kk = [| for m in 0 .. N - 1 -> if m < 32 then 1.0 * float m else 1.0 * float m - 64.0 |]
let k2i =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 -> kk.[j] * kk.[j] + kk.[i] * kk.[i] |]
let ainvA =
    [| for t in 0 .. M - 1 -> if k2i.[t] > 0.0 then -(1.0 / k2i.[t]) else 0.0 |]
let ZERO2 = Array.zeroCreate<float> M
let ONE2 = Array.create M 1.0

// ---- shared state (module-level muts in the example) ----------------------
let qh = Array.create M (cplx 0.0 0.0)
let qh2 = Array.create M (cplx 0.0 0.0)
let mutable phw = Array.create M (cplx 0.0 0.0)
let mutable ph2w = Array.create M (cplx 0.0 0.0)
let mutable hcur = Array.create M (cplx 0.0 0.0)
let mutable hp = Array.create M (cplx 0.0 0.0)
let mutable hpp = Array.create M (cplx 0.0 0.0)
let mutable hcur2 = Array.create M (cplx 0.0 0.0)
let mutable hp2 = Array.create M (cplx 0.0 0.0)
let mutable hpp2 = Array.create M (cplx 0.0 0.0)

// ---- stage functions (each mirrors the example's namesake) ----------------

/// psi_hat = aa*qa + ab*qb (componentwise; barotropic passes ab = zeros).
let invert (aa: float[]) (qa: Cplx[]) (ab: float[]) (qb: Cplx[]) : Cplx[] =
    [| for t in 0 .. M - 1 ->
         cplx (aa.[t] * qa.[t].Re + ab.[t] * qb.[t].Re)
              (aa.[t] * qa.[t].Im + ab.[t] * qb.[t].Im) |]

let uhat (w: float[]) (ph: Cplx[]) : Cplx[] =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 ->
           let zp = ph.[i * N + j]
           cplx (w.[i] * zp.Im) (-(w.[i] * zp.Re)) |]

let vhat (w: float[]) (ph: Cplx[]) : Cplx[] =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 ->
           let zp = ph.[i * N + j]
           cplx (-(w.[j] * zp.Im)) (w.[j] * zp.Re) |]

let flux (u: float[]) (ubg: float) (q: float[]) : float[] =
    [| for t in 0 .. M - 1 -> (u.[t] + ubg) * q.[t] |]

let tendency (w: float[]) (k2: float[]) (qy: float) (rek: float) (uq: Cplx[]) (vq: Cplx[]) (ph: Cplx[]) : Cplx[] =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 ->
           let t = i * N + j
           let zu = uq.[t]
           let zv = vq.[t]
           let zp = ph.[t]
           cplx (w.[j] * zu.Im + w.[i] * zv.Im + qy * w.[j] * zp.Im + rek * k2.[t] * zp.Re)
                (-(w.[j] * zu.Re) - w.[i] * zv.Re - qy * w.[j] * zp.Re + rek * k2.[t] * zp.Im) |]

let ab3_fold (filt: float[]) (q: Cplx[]) (h0: Cplx[]) (h1: Cplx[]) (h2: Cplx[]) (d1: float) (d2: float) (d3: float) : Cplx[] =
    // filt * (q + d1*h0 + d2*h1 + d3*h2), componentwise in the same
    // association as std::complex evaluates the example's expression.
    [| for t in 0 .. M - 1 ->
         let re = ((q.[t].Re + d1 * h0.[t].Re) + d2 * h1.[t].Re) + d3 * h2.[t].Re
         let im = ((q.[t].Im + d1 * h0.[t].Im) + d2 * h1.[t].Im) + d3 * h2.[t].Im
         cplx (filt.[t] * re) (filt.[t] * im) |]

let ab3_d1 (s: int) (dt: float) = if s = 0 then dt else (if s = 1 then 1.5 * dt else (23.0 / 12.0) * dt)
let ab3_d2 (s: int) (dt: float) = if s = 0 then 0.0 else (if s = 1 then -0.5 * dt else (-16.0 / 12.0) * dt)
let ab3_d3 (s: int) (dt: float) = if s < 2 then 0.0 else (5.0 / 12.0) * dt

/// |z|^2 per cell — mirrors the example's `mag2` object_for kernel.
let mag2 (f: Cplx[]) : float[] =
    [| for t in 0 .. M - 1 -> f.[t].Re * f.[t].Re + f.[t].Im * f.[t].Im |]

/// Row tower: per-row prodsum(w_row, f2_row), then a fold across rows —
/// mirrors the example's spec_sum (row kernels over zip + reduce). The
/// row-grouped association differs from a flat sweep in the last ulps.
let spec_sum (w: float[]) (f2: float[]) : float =
    let rows =
        [| for i in 0 .. N - 1 ->
             let mutable acc = 0.0
             for j in 0 .. N - 1 do
                 acc <- acc + w.[i * N + j] * f2.[i * N + j]
             acc |]
    let mutable tot = 0.0
    for i in 0 .. N - 1 do tot <- tot + rows.[i]
    0.5 * tot / 16777216.0

/// Unweighted row tower — mirrors the example's half_mean2.
let half_mean2 (f2: float[]) : float =
    let rows =
        [| for i in 0 .. N - 1 ->
             let mutable acc = 0.0
             for j in 0 .. N - 1 do
                 acc <- acc + f2.[i * N + j]
             acc |]
    let mutable tot = 0.0
    for i in 0 .. N - 1 do tot <- tot + rows.[i]
    0.5 * tot / 16777216.0

let absmax (u: float[]) (sh: float) : float =
    let mutable m = 0.0
    for i in 0 .. N - 1 do
        for j in 0 .. N - 1 do
            let a = abs (u.[i * N + j] + sh)
            m <- if a > m then a else m
    m

let seed (q0: float[]) (dst: Cplx[]) =
    let s = fft2 N N q0
    for t in 0 .. M - 1 do dst.[t] <- s.[t]

/// One barotropic run over steps sLo..sHi-1 (the example's run block, stage
/// for stage). Returns max(|u|,|v|) over the whole run.
let runBt (aa: float[]) (qy: float) (rek: float) (dt: float) (sLo: int) (sHi: int) : float =
    let mutable cflmax = 0.0
    for s in sLo .. sHi - 1 do
        phw <- invert aa qh ZERO2 qh2
        let uhw = uhat kk phw
        let vhw = vhat kk phw
        let u = ifft2 N N uhw
        let v = ifft2 N N vhw
        let qr = ifft2 N N qh
        let uqw = flux u 0.0 qr
        let vqw = flux v 0.0 qr
        let mu = absmax u 0.0
        let mv = absmax v 0.0
        cflmax <- if mu > cflmax then mu else cflmax
        cflmax <- if mv > cflmax then mv else cflmax
        let sUQ = fft2 N N uqw
        let sVQ = fft2 N N vqw
        hpp <- hp
        hp <- hcur
        hcur <- tendency kk k2i qy rek sUQ sVQ phw
        let qn = ab3_fold filtw qh hcur hp hpp (ab3_d1 s dt) (ab3_d2 s dt) (ab3_d3 s dt)
        for t in 0 .. M - 1 do qh.[t] <- qn.[t]
    cflmax

// ===========================================================================
// Part A1 — Rossby wave: L = 2pi, beta = 20, rek = 0, dt = 0.001, 1000 steps.
// ===========================================================================
seed qRossby qh
phw <- invert ainvA qh ZERO2 qh2
let ea1_0 = spec_sum k2i (mag2 phw)
let za1_0 = half_mean2 (mag2 qh)
let cflraw_a1 = runBt ainvA 20.0 0.0 0.001 0 1000
phw <- invert ainvA qh ZERO2 qh2
let ea1_T = spec_sum k2i (mag2 phw)
let za1_T = half_mean2 (mag2 qh)
let qf1 = ifft2 N N qh
// rank-1 residual chain: DERR kernel, then reduced products (flat order).
let derr = [| for j in 0 .. N - 1 -> qf1.[0 * N + j] - qPred.[j] |]
let mutable num_r = 0.0
let mutable den_r = 0.0
for j in 0 .. N - 1 do num_r <- num_r + derr.[j] * derr.[j]
for j in 0 .. N - 1 do den_r <- den_r + qPred.[j] * qPred.[j]
let rossby_err = num_r / den_r
let cfl_a1 = cflraw_a1 * 0.001 / (6.283185307179586 / 64.0)

printfn "// ---- Part A1 pins ----"
printfn "// EXPECT: ea1_0 = %s" (fmt ea1_0)
printfn "// EXPECT: za1_0 = %s" (fmt za1_0)
printfn "// EXPECT: ea1_T = %s" (fmt ea1_T)
printfn "// EXPECT: za1_T = %s" (fmt za1_T)
printfn "// EXPECT: cfl_a1 = %s" (fmt cfl_a1)
printfn "// EXPECT: qa1_p0 = %s" (fmt qf1.[0 * N + 0])
printfn "// EXPECT: qa1_p1 = %s" (fmt qf1.[0 * N + 16])
printfn "// EXPECT: qa1_p2 = %s" (fmt qf1.[32 * N + 40])
printfn "// EXPECT: rossby_err = %s" (fmt rossby_err)
printfn "// (energy drift |ea1_T/ea1_0 - 1| = %s)" (fmt (abs (ea1_T / ea1_0 - 1.0)))
printfn ""

// ===========================================================================
// Part A2 — McWilliams decaying turbulence: beta = 0, rek = 0, dt = 0.005,
// 1500 steps (t = 7.5); the filter is the only dissipation.
// ===========================================================================
seed qMcw qh
phw <- invert ainvA qh ZERO2 qh2
let ea2_0 = spec_sum k2i (mag2 phw)
let za2_0 = half_mean2 (mag2 qh)
let cflraw_a2 = runBt ainvA 0.0 0.0 0.005 0 1500
phw <- invert ainvA qh ZERO2 qh2
let ea2_T = spec_sum k2i (mag2 phw)
let za2_T = half_mean2 (mag2 qh)
let qf2 = ifft2 N N qh
let cfl_a2 = cflraw_a2 * 0.005 / (6.283185307179586 / 64.0)

printfn "// ---- Part A2 pins ----"
printfn "// EXPECT: ea2_0 = %s" (fmt ea2_0)
printfn "// EXPECT: za2_0 = %s" (fmt za2_0)
printfn "// EXPECT: ea2_T = %s" (fmt ea2_T)
printfn "// EXPECT: za2_T = %s" (fmt za2_T)
printfn "// EXPECT: cfl_a2 = %s" (fmt cfl_a2)
printfn "// EXPECT: qa2_p0 = %s" (fmt qf2.[5 * N + 12])
printfn "// EXPECT: qa2_p1 = %s" (fmt qf2.[33 * N + 47])
printfn "// (E ratio = %s, Z ratio = %s)" (fmt (ea2_T / ea2_0)) (fmt (za2_T / za2_0))
printfn ""

// ===========================================================================
// Part B — two-layer QG, pyqg QGModel defaults except U1 = 0.05 (2x the
// pyqg default — the shear is boosted so the baroclinic instability's
// exponential growth is visible within a 1600-step demo run): L = 1e6 m,
// rd = 15 km, delta = 0.25, U2 = 0, beta = 1.5e-11, rek = 5.787e-7,
// dt = 7200 s, 1600 steps (KE1 sampled at step 800).
// ===========================================================================
let dkB = 6.283185307179586 / 1000000.0
let kkB = [| for m in 0 .. N - 1 -> kk.[m] * dkB |]
let k2B =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 -> kkB.[j] * kkB.[j] + kkB.[i] * kkB.[i] |]
let rdi2 = 1.0 / (15000.0 * 15000.0)
let f1 = rdi2 / (1.0 + 0.25)
let f2 = 0.25 * f1
let qy1 = 1.5e-11 + f1 * 0.05
let qy2 = 1.5e-11 - f2 * 0.05
let a11B = [| for t in 0 .. M - 1 -> if k2B.[t] > 0.0 then -(k2B.[t] + f2) / (k2B.[t] * (k2B.[t] + f1 + f2)) else 0.0 |]
let a12B = [| for t in 0 .. M - 1 -> if k2B.[t] > 0.0 then -(f1) / (k2B.[t] * (k2B.[t] + f1 + f2)) else 0.0 |]
let a21B = [| for t in 0 .. M - 1 -> if k2B.[t] > 0.0 then -(f2) / (k2B.[t] * (k2B.[t] + f1 + f2)) else 0.0 |]
let a22B = [| for t in 0 .. M - 1 -> if k2B.[t] > 0.0 then -(k2B.[t] + f1) / (k2B.[t] * (k2B.[t] + f1 + f2)) else 0.0 |]

/// One two-layer step range; mirrors the example's runB blocks stage for
/// stage. Returns max(|u + Ubg|, |v|) over both layers for the range.
let run2L (sLo: int) (sHi: int) : float =
    let mutable cflmax = 0.0
    for s in sLo .. sHi - 1 do
        phw <- invert a11B qh a12B qh2
        ph2w <- invert a21B qh a22B qh2
        // ---- layer 1 ----
        let uhw1 = uhat kkB phw
        let vhw1 = vhat kkB phw
        let u1 = ifft2 N N uhw1
        let v1 = ifft2 N N vhw1
        let q1r = ifft2 N N qh
        let uqw1 = flux u1 0.05 q1r
        let vqw1 = flux v1 0.0 q1r
        let mu1 = absmax u1 0.05
        let mv1 = absmax v1 0.0
        cflmax <- if mu1 > cflmax then mu1 else cflmax
        cflmax <- if mv1 > cflmax then mv1 else cflmax
        let sUQ1 = fft2 N N uqw1
        let sVQ1 = fft2 N N vqw1
        hpp <- hp
        hp <- hcur
        hcur <- tendency kkB k2B qy1 0.0 sUQ1 sVQ1 phw
        // ---- layer 2 ----
        let uhw2 = uhat kkB ph2w
        let vhw2 = vhat kkB ph2w
        let u2 = ifft2 N N uhw2
        let v2 = ifft2 N N vhw2
        let q2r = ifft2 N N qh2
        let uqw2 = flux u2 0.0 q2r
        let vqw2 = flux v2 0.0 q2r
        let mu2 = absmax u2 0.0
        let mv2 = absmax v2 0.0
        cflmax <- if mu2 > cflmax then mu2 else cflmax
        cflmax <- if mv2 > cflmax then mv2 else cflmax
        let sUQ2 = fft2 N N uqw2
        let sVQ2 = fft2 N N vqw2
        hpp2 <- hp2
        hp2 <- hcur2
        hcur2 <- tendency kkB k2B qy2 5.787e-7 sUQ2 sVQ2 ph2w
        // ---- AB3 + filter, both layers ----
        let qn = ab3_fold filtw qh hcur hp hpp (ab3_d1 s 7200.0) (ab3_d2 s 7200.0) (ab3_d3 s 7200.0)
        let qn2 = ab3_fold filtw qh2 hcur2 hp2 hpp2 (ab3_d1 s 7200.0) (ab3_d2 s 7200.0) (ab3_d3 s 7200.0)
        for t in 0 .. M - 1 do qh.[t] <- qn.[t]
        for t in 0 .. M - 1 do qh2.[t] <- qn2.[t]
    cflmax

seed q1Init qh
for t in 0 .. M - 1 do qh2.[t] <- cplx 0.0 0.0
phw <- invert a11B qh a12B qh2
let ke1_0 = spec_sum k2B (mag2 phw)
let cflraw_b1 = run2L 0 800
phw <- invert a11B qh a12B qh2
let ke1_mid = spec_sum k2B (mag2 phw)
let cflraw_b2 = run2L 800 1600
phw <- invert a11B qh a12B qh2
let ke1_end = spec_sum k2B (mag2 phw)
ph2w <- invert a21B qh a22B qh2
let ke2_end = spec_sum k2B (mag2 ph2w)
let qf3 = ifft2 N N qh
let cflraw_b = if cflraw_b1 > cflraw_b2 then cflraw_b1 else cflraw_b2
let cfl_b = cflraw_b * 7200.0 / (1000000.0 / 64.0)

printfn "// ---- Part B pins ----"
printfn "// EXPECT: ke1_mid = %s" (fmt ke1_mid)
printfn "// EXPECT: ke1_end = %s" (fmt ke1_end)
printfn "// EXPECT: ke2_end = %s" (fmt ke2_end)
printfn "// EXPECT: cfl_b = %s" (fmt cfl_b)
printfn "// EXPECT: qb_p0 = %s" (fmt qf3.[7 * N + 9])
printfn "// EXPECT: qb_p1 = %s" (fmt qf3.[50 * N + 3])
printfn "// (ke1_0 = %s, growth mid->end = %s)" (fmt ke1_0) (fmt (ke1_end / ke1_mid))
