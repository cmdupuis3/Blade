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
