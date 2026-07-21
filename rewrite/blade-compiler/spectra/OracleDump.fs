/// EXPECT-pin dumps for tests/corpus/spectra (run:
/// dotnet run --project spectra/BladeSpectra.fsproj). Values print G17 /
/// InvariantCulture so pins are exact and locale-independent; complex values
/// print as (re, im) pairs matching Blade's flat array output. Each block is
/// annotated with its corpus id.
module BladeSpectra.OracleDump

open System.Globalization
open BladeSpectra.Fft
open BladeSpectra.Polyspec

let private fmt (x: float) = x.ToString("G17", CultureInfo.InvariantCulture)
let private fmtArr (xs: float seq) =
    "[" + (xs |> Seq.map fmt |> String.concat ", ") + "]"
let private fmtC (z: Cplx) = sprintf "(%s, %s)" (fmt z.Re) (fmt z.Im)
let private fmtCArr (zs: Cplx seq) =
    "[" + (zs |> Seq.map fmtC |> String.concat ", ") + "]"

let dumpAll () =
    // Sanity: the Rand mirror must reproduce tests/corpus/rand/001's pins
    // (u = [0.739107444803618, 0.878809761522638, ...] for key 12345).
    printfn "// rand mirror cross-check (corpus rand/001, key 12345, n=4):"
    printfn "//   %s" (fmtArr (Rand.uniform 12345L 4))
    printfn ""

    // corpus spectra/012: fft of cos(2pi*2*i/8), n=8 (radix-2 path).
    let cosBin2 = [| for i in 0 .. 7 -> cos (2.0 * System.Math.PI * 2.0 * float i / 8.0) |]
    printfn "// corpus spectra/012 input (G17 literals):"
    printfn "//   x = %s" (fmtArr cosBin2)
    printfn "// EXPECT: X = %s" (fmtCArr (fft cosBin2))
    printfn ""

    // corpus spectra/013: fft at n=6 (naive table-DFT fallback path).
    let ramp6 = [| 0.0; 1.0; 2.0; 3.0; 4.0; 5.0 |]
    printfn "// corpus spectra/013 (n=6 fallback):"
    printfn "// EXPECT: X = %s" (fmtCArr (fft ramp6))
    printfn ""

    // corpus spectra/014: fft of r.uniform(12345, 8).
    let u8 = Rand.uniform 12345L 8
    printfn "// corpus spectra/014 (rand key 12345, n=8):"
    printfn "// EXPECT: X = %s" (fmtCArr (fft u8))
    printfn ""

    // corpus spectra/020: power of the bin-2 cosine, n=8.
    printfn "// corpus spectra/020:"
    printfn "// EXPECT: P = %s" (fmtArr (power cosBin2))
    printfn ""

    // corpus spectra/030: k=2 cross-spectrum of rand keys 1 and 2, n=8.
    let ua = Rand.uniform 1L 8
    let ub = Rand.uniform 2L 8
    printfn "// corpus spectra/030 (keys 1, 2; n=8):"
    printfn "// EXPECT: P = %s" (fmtCArr (polyspec [ua; ub]))
    printfn ""

    // corpus spectra/031: bispectrum (k=3) of a quadratically phase-coupled
    // signal at n=8: cos(w1 t) + cos(w2 t) + cos((w1+w2) t + 0.5).
    let coupled =
        [| for i in 0 .. 7 ->
             cos (2.0 * System.Math.PI * 1.0 * float i / 8.0)
             + cos (2.0 * System.Math.PI * 2.0 * float i / 8.0)
             + cos (2.0 * System.Math.PI * 3.0 * float i / 8.0 + 0.5) |]
    printfn "// corpus spectra/031 input (G17 literals):"
    printfn "//   x = %s" (fmtArr coupled)
    printfn "// EXPECT: B = %s" (fmtCArr (polyspec [coupled; coupled; coupled]))
    printfn ""

    // corpus spectra/032: dedup smoke at n=4 — fft + power + k=3 polyspec.
    let x4 = [| 1.0; 2.0; 3.0; 4.0 |]
    let y4 = [| 4.0; 3.0; 2.0; 1.0 |]
    let z4 = [| 1.0; 0.0; 2.0; 0.0 |]
    printfn "// corpus spectra/032 (n=4):"
    printfn "// EXPECT: X = %s" (fmtCArr (fft x4))
    printfn "// EXPECT: P = %s" (fmtArr (power x4))
    printfn "// EXPECT: B = %s" (fmtCArr (polyspec [x4; y4; z4]))
    printfn ""

    // corpus spectra/033: trispectrum (k=4) at n=4, 64 cells flat.
    printfn "// corpus spectra/033 (k=4, n=4):"
    printfn "// EXPECT: T = %s" (fmtCArr (polyspec [x4; x4; x4; x4]))
    printfn ""

    // ------------------------------------------------------------------
    // fft2 / ifft2 (corpus spectra/044+). fmt2D prints the nested rank-2
    // literal for pasting into the .blade input.
    // ------------------------------------------------------------------
    let fmt2D (r: int) (c: int) (xs: float[]) =
        "[" + (String.concat ", "
                 [ for i in 0 .. r - 1 ->
                     "[" + (String.concat ", " [ for j in 0 .. c - 1 -> fmt xs.[i * c + j] ]) + "]" ]) + "]"

    // corpus spectra/044: fft2 of the (0,0) impulse, 4x4 (radix-2 x radix-2).
    let imp44 = [| yield 1.0; for _ in 1 .. 15 -> 0.0 |]
    printfn "// corpus spectra/044 (fft2 impulse 4x4):"
    printfn "// EXPECT: X = %s" (fmtCArr (fft2 4 4 imp44))
    printfn ""

    // corpus spectra/045: fft2 of a plane wave cos(2pi(2i/8 + j/4)), 8x4 —
    // concentrates at bins (2,1) and (6,3) with weight rc/2 = 16.
    let pw84 =
        [| for i in 0 .. 7 do
             for j in 0 .. 3 ->
               cos (2.0 * System.Math.PI * (2.0 * float i / 8.0 + 1.0 * float j / 4.0)) |]
    printfn "// corpus spectra/045 input (G17 literals):"
    printfn "//   x = %s" (fmt2D 8 4 pw84)
    printfn "// EXPECT: X = %s" (fmtCArr (fft2 8 4 pw84))
    printfn ""

    // corpus spectra/046: ifft2(fft2(x)) round-trip at 8x8, x = 0..63.
    let x88 = [| for t in 0 .. 63 -> float t |]
    let y88 = ifft2 8 8 (fft2 8 8 x88)
    let mutable resid46 = 0.0
    for t in 0 .. 63 do
        let d = y88.[t] - x88.[t]
        resid46 <- resid46 + d * d
    printfn "// corpus spectra/046 (roundtrip 8x8): resid actual = %s" (fmt resid46)
    printfn ""

    // corpus spectra/047: Parseval at 6x4 (naive rows axis, radix-2 cols
    // axis): sum|X|^2 - rc*sum x^2, accumulated in row-major order.
    let x64 = [| for t in 0 .. 23 -> 0.5 * float t - 3.0 |]
    printfn "// corpus spectra/047 input:"
    printfn "//   x = %s" (fmt2D 6 4 x64)
    let sX47 = fft2 6 4 x64
    let mutable sx47 = 0.0
    for i in 0 .. 5 do
        for j in 0 .. 3 do
            sx47 <- sx47 + x64.[i * 4 + j] * x64.[i * 4 + j]
    let mutable sXX47 = 0.0
    for i in 0 .. 5 do
        for j in 0 .. 3 do
            let z = sX47.[i * 4 + j]
            sXX47 <- sXX47 + z.Re * z.Re + z.Im * z.Im
    printfn "// EXPECT: resid = %s" (fmt (sXX47 - 24.0 * sx47))
    printfn ""

    // corpus spectra/048: fft2 inside a for body on a module-level mut
    // field (the evolving-state seam): f(i,j) = (s+1)*(i*4+j), s = 0..2,
    // acc += Re X(0,0) + Im X(2,1) each pass.
    let f48 = Array.zeroCreate<float> 16
    let mutable acc48 = 0.0
    for s in 0 .. 2 do
        for i in 0 .. 3 do
            for j in 0 .. 3 do
                f48.[i * 4 + j] <- 1.0 * float (s + 1) * float (i * 4 + j)
        let sX = fft2 4 4 f48
        acc48 <- acc48 + sX.[0 * 4 + 0].Re + sX.[2 * 4 + 1].Im
    printfn "// corpus spectra/048 (fft2 in loop on mut field):"
    printfn "// EXPECT: acc = %s" (fmt acc48)
    printfn ""

    // corpus spectra/051: fft2 at 3x5 (naive table DFT on both axes).
    let x35 = [| for t in 0 .. 14 -> 0.25 * float t - 1.0 |]
    printfn "// corpus spectra/051 input:"
    printfn "//   x = %s" (fmt2D 3 5 x35)
    printfn "// EXPECT: X = %s" (fmtCArr (fft2 3 5 x35))
