// make_qg_zarr.fsx — deterministic fixture generator for the QG atmosphere
// example (examples/07_qg_atmosphere.blade).
//
// Bakes the initial conditions and the Arbic–Flierl exponential filter into
// a committed zarr v3 store at examples/data/qg_init_zarr, reusing the
// repo's own machinery end to end:
//   - ZarrWrite.writeStoreV3 (providers/ZarrProvider.fs) writes the store,
//   - BladeSpectra.Fft.fft2/ifft2 (spectra/Fft.fs) — the SAME bit-exact
//     oracle mirror the spectra corpus pins against — builds q = ∇²ψ
//     spectrally, so the baked q fields are exactly consistent with the
//     example's own spectral operators,
//   - BladeSpectra.Rand (spectra/Rand.fs) — the mt19937_64 mirror of
//     Blade's rand module — supplies the deterministic noise.
//
// The filter is baked (rather than computed in-language) because it needs
// exp(), and the C++ runtime's libm is not bit-identical to .NET's
// (tests/corpus/sgs/004 header): baking keeps the compiled example
// transcendental-free, so its EXPECT pins mirror the F# oracle exactly.
//
// Run from anywhere:  dotnet fsi examples/tools/make_qg_zarr.fsx
// (Regenerates the committed store in place; idempotent.)

#r "System.Security.Cryptography"
#load "../../Ast.fs"
#load "../../Types.fs"
#load "../../Ir.fs"
#load "../../providers/ProviderRegistry.fs"
#load "../../providers/ZarrProvider.fs"
#load "../../spectra/Rand.fs"
#load "../../spectra/Fft.fs"

open System
open Blade.ZarrProvider
open BladeSpectra
open BladeSpectra.Fft

let N = 64
let M = N * N
let PI = Math.PI

// Signed wavenumber INDEX per axis, full-spectrum FFT ordering
// [0..31, -32..-1] — mirrors kk in the example (dk = 1 index units).
let kkIdx = [| for m in 0 .. N - 1 -> if m < N / 2 then float m else float m - float N |]

// kappa^2 in index units at flat cell (i*N + j): kk(j)^2 + kk(i)^2.
let k2Idx =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 ->
           kkIdx.[j] * kkIdx.[j] + kkIdx.[i] * kkIdx.[i] |]

/// q = "∇²ψ" through the example's own spectral pipeline: fft2 -> multiply
/// by -kappa^2 (index units; physical dk^2 scaling belongs to the caller) ->
/// ifft2. Baking THIS (rather than an analytic q) makes the stored field
/// exactly the discrete Laplacian the example inverts.
let qFromPsiIdx (psi: float[]) : float[] =
    let ph = fft2 N N psi
    let qh = [| for t in 0 .. M - 1 -> { Re = -k2Idx.[t] * ph.[t].Re; Im = -k2Idx.[t] * ph.[t].Im } |]
    ifft2 N N qh

/// Spectral KE of a streamfunction spectrum (unnormalized transform):
/// 0.5 * sum kappa^2 |ph|^2 / M^2, kappa in index units.
let specKE (ph: Cplx[]) : float =
    let mutable s = 0.0
    for t in 0 .. M - 1 do
        s <- s + k2Idx.[t] * (ph.[t].Re * ph.[t].Re + ph.[t].Im * ph.[t].Im)
    0.5 * s / (float M * float M)

// ---------------------------------------------------------------------------
// Part A1 — single westward Rossby mode: psi = cos(2x) on L = 2pi (KE = 1).
// x is the cell-centered pyqg grid x_j = (j + 0.5) * L / N.
// ---------------------------------------------------------------------------
let L_A = 2.0 * PI
let xA = [| for j in 0 .. N - 1 -> (float j + 0.5) * L_A / float N |]
let psiRossby =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 -> cos (2.0 * xA.[j]) |]
let qRossby = qFromPsiIdx psiRossby
printfn "q_rossby: KE of psi = %.17g (target 1.0)" (specKE (fft2 N N psiRossby))

// Predicted q(0, :) row after T = 1 at beta = 20: the mode-2 Rossby wave
// translates westward at c = -beta/k^2 = -5, so q(x, T) = q0(x - c*T)
// = -4 cos(2 (x + 5)). Used only in a tolerance VERDICT (the AB3 time
// error is ~1e-6), so generator-side trig is fine here.
let qPredRossby = [| for j in 0 .. N - 1 -> -4.0 * cos (2.0 * (xA.[j] + 5.0)) |]

// ---------------------------------------------------------------------------
// Part A2 — McWilliams (1984) decaying-turbulence IC, pyqg recipe adapted
// to the full spectrum: Gaussian white noise (Box–Muller over the
// mt19937_64 mirror, keys 42/43) shaped by the band-limited envelope
// ckappa = 1/sqrt(kappa^2 (1 + (kappa^2/36)^2)) (peak near kappa = 6),
// DC removed (ckappa(0) = 0), normalized to KE = 1; q = "∇²ψ".
// Building the spectrum as fft2(real noise) * real envelope keeps it
// exactly Hermitian, so q is real by construction.
// ---------------------------------------------------------------------------
let u1 = Rand.uniform 42L M
let u2 = Rand.uniform 43L M
let noise =
    [| for t in 0 .. M - 1 ->
         sqrt (-2.0 * log u1.[t]) * cos (2.0 * PI * u2.[t]) |]
let noiseHat = fft2 N N noise
let envelope =
    [| for t in 0 .. M - 1 ->
         let kap2 = k2Idx.[t]
         if kap2 > 0.0 then 1.0 / sqrt (kap2 * (1.0 + (kap2 / 36.0) * (kap2 / 36.0))) else 0.0 |]
let psiHatRaw = [| for t in 0 .. M - 1 -> { Re = noiseHat.[t].Re * envelope.[t]; Im = noiseHat.[t].Im * envelope.[t] } |]
let keRaw = specKE psiHatRaw
let scaleMcw = 1.0 / sqrt keRaw
let psiHatMcw = [| for t in 0 .. M - 1 -> { Re = psiHatRaw.[t].Re * scaleMcw; Im = psiHatRaw.[t].Im * scaleMcw } |]
let qMcw =
    ifft2 N N [| for t in 0 .. M - 1 -> { Re = -k2Idx.[t] * psiHatMcw.[t].Re; Im = -k2Idx.[t] * psiHatMcw.[t].Im } |]
printfn "q_mcw:    KE(normalized) = %.17g (target 1.0)" (specKE psiHatMcw)

// ---------------------------------------------------------------------------
// Part B — two-layer QG upper-layer noise IC, pyqg default shape:
// q1 = 1e-7 * uniform noise (mt19937_64 key 7, mapped to [-1, 1)); q2 = 0
// (computed in-language). Units: s^-1 on the physical L = 1e6 m grid.
// ---------------------------------------------------------------------------
let uB = Rand.uniform 7L M
let q1Init = [| for t in 0 .. M - 1 -> 1.0e-7 * (2.0 * uB.[t] - 1.0) |]

// ---------------------------------------------------------------------------
// The Arbic–Flierl exponential filter (pyqg _initialize_filter): in
// nondimensional wavenumber wvx = sqrt((k dx)^2 + (l dy)^2) = 2pi|m|/N per
// axis — grid-shape-only, so ONE array serves all three runs.
//   filtr = exp(-23.6 (wvx - 0.65pi)^4)  for wvx > 0.65pi, else 1.
// ---------------------------------------------------------------------------
let cphi = 0.65 * PI
let filtr =
    [| for i in 0 .. N - 1 do
         for j in 0 .. N - 1 ->
           let kx = kkIdx.[j] * 2.0 * PI / float N
           let ky = kkIdx.[i] * 2.0 * PI / float N
           let wvx = sqrt (kx * kx + ky * ky)
           if wvx > cphi then exp (-23.6 * (wvx - cphi) ** 4.0) else 1.0 |]

// ---------------------------------------------------------------------------
// Write the store (idempotent: replaced wholesale).
// ---------------------------------------------------------------------------
let root = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let store = IO.Path.Combine(root, "examples", "data", "qg_init_zarr")
if IO.Directory.Exists store then IO.Directory.Delete(store, true)

let grid2 name (data: float[]) : ZarrWrite.WriteVar =
    { Name = name
      DimNames = Some [ "y"; "x" ]
      Shape = [ int64 N; int64 N ]
      Chunks = [ int64 N; int64 N ]
      FillValue = FillFloat 0.0
      Data = ZarrWrite.WF64 data
      OmitChunks = []
      Blade = None }

ZarrWrite.writeStoreV3 store
    [ grid2 "q_rossby" qRossby
      { Name = "q_pred_rossby"
        DimNames = Some [ "x" ]
        Shape = [ int64 N ]
        Chunks = [ int64 N ]
        FillValue = FillFloat 0.0
        Data = ZarrWrite.WF64 qPredRossby
        OmitChunks = []
        Blade = None }
      grid2 "q_mcw" qMcw
      grid2 "q1_init" q1Init
      grid2 "filtr" filtr ]

printfn "wrote %s" store
printfn "  q_rossby[0,0]=%.17g  q_mcw[0,0]=%.17g  q1_init[0,0]=%.17g  filtr[0,0]=%.17g"
    qRossby.[0] qMcw.[0] q1Init.[0] filtr.[0]
