// koop_build.fsx — oracle + generator for examples/physics/45 (Koopman eigenvalues).
// Generator EDMD on closed-form trajectories with an EXACTLY-closed dictionary
// (monomials deg <= 3 of (x,p); linear dynamics => invariant subspace):
//   conservative SHO (3 amplitudes): spectrum = {±3i, ±2i, ±i(x2), 0}
//   damped SHO (gamma = 0.15):       spectrum = {a mu+ + b mu-}, mu± = -g ± i w',
//                                    invariant eigenvalue displaced to -2g exactly.
// L = A G^{-1} with G = sum d d^T, A = sum ddot d^T; ddot exact via chain rule.
// m.eig conventions: (RE, IM), descending modulus, +im first in conjugate pairs.

open System
open System.Text

let DD = 9
let dict (x: float) (p: float) =
    [| x; p; x*x; x*p; p*p; x*x*x; x*x*p; x*p*p; p*p*p |]
let dictDot (x: float) (p: float) (f: float) =
    // d/dt of each monomial with xdot = p, pdot = f
    [| p; f; 2.0*x*p; p*p + x*f; 2.0*p*f; 3.0*x*x*p; 2.0*x*p*p + x*x*f; p*p*p + 2.0*x*p*f; 3.0*p*p*f |]

let gamma = 0.15
let w1 = sqrt (1.0 - gamma * gamma)

let accumulate (samples: (float * float * float) seq) =
    let G = Array.zeroCreate (DD * DD)
    let A = Array.zeroCreate (DD * DD)
    for (x, p, f) in samples do
        let d = dict x p
        let dd = dictDot x p f
        for i in 0 .. DD - 1 do
            for j in 0 .. DD - 1 do
                G.[i*DD+j] <- G.[i*DD+j] + d.[i] * d.[j]
                A.[i*DD+j] <- A.[i*DD+j] + dd.[i] * d.[j]
    (G, A)

let consSamples =
    seq {
        for a in [ 0.8; 1.0; 1.2 ] do
            let mutable tf = 0.0
            for _ in 0 .. 499 do
                let x = a * cos tf
                let p = 0.0 - a * sin tf
                yield (x, p, -x)
                tf <- tf + 0.05
    }
let dampSamples =
    seq {
        let mutable tf = 0.0
        for _ in 0 .. 239 do
            let e = exp (-gamma * tf)
            let x = 1.2 * e * (cos (w1 * tf) + (gamma / w1) * sin (w1 * tf))
            let p = -1.2 * e * sin (w1 * tf) / w1
            yield (x, p, -x - 2.0 * gamma * p)
            tf <- tf + 0.05
    }

// ---------- eigh (for G^{-1}) ----------
let eigh (A0: float[,]) =
    let n = Array2D.length1 A0
    let A = Array2D.copy A0
    let V = Array2D.init n n (fun i j -> if i = j then 1.0 else 0.0)
    let offNorm () =
        let mutable s = 0.0
        for i in 0 .. n - 1 do
            for j in i + 1 .. n - 1 do s <- s + A.[i, j] * A.[i, j]
        s
    let mutable sweep = 0
    while offNorm () > 1e-30 && sweep < 200 do
        sweep <- sweep + 1
        for p in 0 .. n - 2 do
            for q in p + 1 .. n - 1 do
                if abs A.[p, q] > 1e-24 then
                    let theta = (A.[q, q] - A.[p, p]) / (2.0 * A.[p, q])
                    let t = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (theta * theta + 1.0))
                    let c = 1.0 / sqrt (t * t + 1.0)
                    let sn = t * c
                    for k in 0 .. n - 1 do
                        let akp = A.[k, p]
                        let akq = A.[k, q]
                        A.[k, p] <- c * akp - sn * akq
                        A.[k, q] <- sn * akp + c * akq
                    for k in 0 .. n - 1 do
                        let apk = A.[p, k]
                        let aqk = A.[q, k]
                        A.[p, k] <- c * apk - sn * aqk
                        A.[q, k] <- sn * apk + c * aqk
                    for k in 0 .. n - 1 do
                        let vkp = V.[k, p]
                        let vkq = V.[k, q]
                        V.[k, p] <- c * vkp - sn * vkq
                        V.[k, q] <- sn * vkp + c * vkq
    let idx = [| 0 .. n - 1 |] |> Array.sortByDescending (fun i -> A.[i, i])
    (idx |> Array.map (fun i -> A.[i, i]), Array2D.init n n (fun r c -> V.[r, idx.[c]]))

let buildL (G: float[]) (A: float[]) =
    let Gm = Array2D.init DD DD (fun i j -> G.[i*DD+j])
    let (l0, q0) = eigh Gm
    printfn "  G eigen range %.3e .. %.3e (cond %.2e)" l0.[0] l0.[DD-1] (l0.[0] / l0.[DD-1])
    let GI = Array2D.init DD DD (fun i j ->
        let mutable s = 0.0
        for k in 0 .. DD - 1 do s <- s + q0.[i, k] * q0.[j, k] / l0.[k]
        s)
    Array2D.init DD DD (fun i j ->
        let mutable s = 0.0
        for k in 0 .. DD - 1 do s <- s + A.[i*DD+k] * GI.[k, j]
        s)

// complex eigenvalues via companion... use simple QR-free route: rely on the
// analytic truth + net's Francis in blade; here compute via characteristic
// roots using generalized power iteration is overkill — use MathNet-free
// approach: eigenvalues of small real matrix via unsymmetric QR (naive
// Hessenberg + shifted QR). For oracle purposes implement a basic complex
// solver via the eigenvalues of the 2Nx2N symmetric embedding is wrong for
// non-normal. Instead: validate blade output against ANALYTIC truth directly.
let (gC, aC) = accumulate consSamples
let (gD, aD) = accumulate dampSamples
printfn "CONSERVATIVE:"
let LC = buildL gC aC
printfn "DAMPED:"
let LD = buildL gD aD
// print L row-norm sanity + analytic expectations
printfn "analytic conservative spectrum: 0, ±i (x2), ±2i, ±3i"
printfn "analytic damped: mu± = %.6f ± %.6f i; lattice a*mu+ + b*mu-, a+b<=3" (-gamma) w1
printfn "  deg2 invariant analogue: mu+ + mu- = %.6f (real)" (-2.0 * gamma)
printfn "  3mu+ = (%.6f, %.6f) |.|=3; 2mu++mu- = (%.6f, %.6f) |.|=%.6f" (-3.0*gamma) (3.0*w1) (-3.0*gamma) w1 (sqrt (9.0*gamma*gamma + w1*w1))

// residual check: is L exact? verify L d = ddot on a fresh sample
let checkL (L: float[,]) (x: float) (p: float) (f: float) =
    let d = dict x p
    let dd = dictDot x p f
    let mutable worst = 0.0
    for i in 0 .. DD - 1 do
        let mutable s = 0.0
        for j in 0 .. DD - 1 do s <- s + L.[i, j] * d.[j]
        worst <- max worst (abs (s - dd.[i]))
    worst
printfn "L residual on fresh samples: cons %.3e  damp %.3e" (checkL LC 0.63 (-0.41) (-0.63)) (checkL LD 0.29 0.17 (-0.29 - 0.3*0.17))

// ---------- generate the blade file ----------
let sb = StringBuilder()
let w (s: string) = sb.AppendLine(s) |> ignore
let header = [
  "// TEST: Physics -- The Spectrum of the Law: Koopman Eigenvalues In-Language"
  "// ============================================================================"
  "// The arc's EDMD thread (ex 15-16, tools/koopman_edmd.ps1) used only the"
  "// KERNEL of the fitted generator -- invariants live at eigenvalue zero --"
  "// because no general eigensolver existed. math.eig (Francis QR, real +"
  "// imaginary parts, descending modulus) retires that limit: this file reads"
  "// the WHOLE spectrum of the law, in-language, and pins it against closed"
  "// forms. Design: generator EDMD, L = (sum ddot d^T)(sum d d^T)^{-1}, over"
  "// the 9 monomials of degree <= 3 in (x, p) -- a dictionary that is EXACTLY"
  "// invariant under linear flow, so the fit is exact to rounding and the"
  "// spectrum is analytic. Derivatives are exact (chain rule through the"
  "// equations of motion; no finite differences). Trajectories are closed"
  "// form, sampled in imperative blocks; G^{-1} via m.eigh whitening; the"
  "// eigenvalue read via m.eig."
  "// FINDINGS (pinned):"
  "//   * THE FREQUENCY ALGEBRA, MEASURED. Conservative oscillator (three"
  "//     amplitudes; isochronism keeps the ladder sharp): the fitted L's"
  "//     spectrum is EXACTLY {±3i, ±2i, ±i (twice), 0} -- the harmonic"
  "//     ladder of the flow, plus the invariant at zero. Every real part"
  "//     pins at ~1e-12: frequencies from data, no FFT, no phase unwrap."
  "//   * DISSIPATION SHIFTS THE LATTICE. Damped oscillator (gamma = 0.15,"
  "//     one spiral fills the plane): eigenvalues land on the exact lattice"
  "//     a mu+ + b mu- with mu± = -gamma ± i sqrt(1-gamma^2), a+b <= 3:"
  "//     pairs at -0.15 ± 0.9887i, -0.45 ± 2.9661i, -0.30 ± 1.9774i, ..."
  "//   * THE DETECTOR READS DAMPING AS DISPLACEMENT. The energy-like"
  "//     combination that sat at eigenvalue 0 in the conservative run"
  "//     appears at EXACTLY mu+ + mu- = -2 gamma = -0.3 + 0i (the smallest-"
  "//     modulus eigenvalue, purely real): the zero-variance/kernel verdict"
  "//     of ex 15-16 is the gamma -> 0 limit of an eigenvalue displacement"
  "//     the spectrum now measures directly."
  "// Two-route: the dictionary closure makes truth ANALYTIC (the lattice"
  "// above); an independent F# route confirmed the fitted L reproduces"
  "// d/dt on fresh samples to ~1e-12 before pinning."
  "// ============================================================================"
  "import math as m"
  "" ]
header |> List.iter w

let zeros n = List.replicate n "0.0" |> String.concat ", "

// accumulation block generator: closed-form samples
let accBlock (name: string) (body: string list) =
    w (sprintf "let %s = {" name)
    w (sprintf "    let mut st = [%s]" (zeros 162))
    w "    let mut d = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
    w "    let mut dd = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
    w "    let mut tf = 0.0"
    body |> List.iter w
    w "    st"
    w "}"
    w ""
let dictFill = [
    "            d(0) = x"
    "            d(1) = p"
    "            d(2) = x * x"
    "            d(3) = x * p"
    "            d(4) = p * p"
    "            d(5) = x * x * x"
    "            d(6) = x * x * p"
    "            d(7) = x * p * p"
    "            d(8) = p * p * p"
    "            dd(0) = p"
    "            dd(1) = f"
    "            dd(2) = 2.0 * x * p"
    "            dd(3) = p * p + x * f"
    "            dd(4) = 2.0 * p * f"
    "            dd(5) = 3.0 * x * x * p"
    "            dd(6) = 2.0 * x * p * p + x * x * f"
    "            dd(7) = p * p * p + 2.0 * x * p * f"
    "            dd(8) = 3.0 * p * p * f"
    "            for i in 0..9 {"
    "                for j in 0..9 {"
    "                    st(i * 9 + j) = st(i * 9 + j) + d(i) * d(j)"
    "                    st(81 + i * 9 + j) = st(81 + i * 9 + j) + dd(i) * d(j)"
    "                }"
    "            }" ]

w "let AMPI: Array<Float64 like Idx<3>> = [0.0, 1.0, 2.0]"
w ""
accBlock "SGC" ([
    "    for a in 0..3 {"
    "        let amp = 0.8 + 0.2 * AMPI(a)"
    "        tf = 0.0"
    "        for k in 0..500 {"
    "            let x = amp * cos(tf)"
    "            let p = 0.0 - amp * sin(tf)"
    "            let f = 0.0 - x" ] @ dictFill @ [
    "            tf = tf + 0.05"
    "        }"
    "    }" ])
accBlock "SGD" ([
    "    for k in 0..240 {"
    "        let e = exp(0.0 - 0.15 * tf)"
    sprintf "        let x = 1.2 * e * (cos(%.17g * tf) + %.17g * sin(%.17g * tf))" w1 (gamma / w1) w1
    sprintf "        let p = 0.0 - 1.2 * e * sin(%.17g * tf) / %.17g" w1 w1 ] @ [
    "        let f = 0.0 - x - 0.3 * p" ] @ (dictFill |> List.map (fun s -> s.Substring(4))) @ [
    "        tf = tf + 0.05"
    "    }" ])

// AMPI helper literal (amplitude index as float table: 0,1,2)
// post-processing per system: G inverse via eigh, L = A G^{-1}, m.eig
let post (sfx: string) (stn: string) =
    w (sprintf "// ---- fit L and read the spectrum, %s ----" (if sfx = "C" then "conservative" else "damped"))
    w (sprintf "let GM%s: Array<Float64 like Idx<9>, Idx<9>> = method_for(range<Idx<9>>) <*> method_for(range<Idx<9>>) <@> lambda(i: Int64, j: Int64) -> %s(i * 9 + j) |> compute" sfx stn)
    w (sprintf "let AM%s = method_for(range<Idx<9>>) <*> method_for(range<Idx<9>>) <@> lambda(i: Int64, j: Int64) -> %s(81 + i * 9 + j) |> compute" sfx stn)
    w (sprintf "let (QG%s, LG%s) = m.eigh(GM%s)" sfx sfx sfx)
    w (sprintf "let QD%s = method_for(range<Idx<9>>) <*> method_for(range<Idx<9>>) <@> lambda(i: Int64, k: Int64) -> QG%s(i, k) / LG%s(k) |> compute" sfx sfx sfx)
    w (sprintf "let GI%s = method_for(QD%s, QG%s) <@> lambda(u: Array<Float64 like Idx<9>>, v: Array<Float64 like Idx<9>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let LK%s: Array<Float64 like Idx<9>, Idx<9>> = method_for(AM%s, GI%s) <@> lambda(u: Array<Float64 like Idx<9>>, v: Array<Float64 like Idx<9>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let (RE%s, IM%s) = m.eig(LK%s)" sfx sfx sfx)
    w ""
post "C" "SGC"
post "D" "SGD"

// scalar reads + verdicts (slot order confirmed by first run)
w "// ---- the ladder and the lattice, read off ----"
w "let imc0 = IMC(0)"
w "let imc2 = IMC(2)"
w "let imc4 = IMC(4)"
w "let imc6 = IMC(6)"
w "let rec8 = REC(8)"
w "let imc8 = IMC(8)"
w "let maxreC = {"
w "    let mut worst = 0.0"
w "    for k in 0..9 {"
w "        let a = sqrt(REC(k) * REC(k))"
w "        worst = if a > worst then a else worst"
w "    }"
w "    worst"
w "}"
w "let devL = sqrt((IMC(0) - 3.0) * (IMC(0) - 3.0)) + sqrt((IMC(2) - 2.0) * (IMC(2) - 2.0)) + sqrt((IMC(4) - 1.0) * (IMC(4) - 1.0)) + sqrt((IMC(6) - 1.0) * (IMC(6) - 1.0))"
w "let red0 = RED(0)"
w "let imd0 = IMD(0)"
w "let red2 = RED(2)"
w "let imd2 = IMD(2)"
w "let red6 = RED(6)"
w "let imd6 = IMD(6)"
w "let red8 = RED(8)"
w "let imd8 = IMD(8)"
w (sprintf "let devD = sqrt((RED(0) + 0.45) * (RED(0) + 0.45)) + sqrt((IMD(0) - %.17g) * (IMD(0) - %.17g)) + sqrt((RED(2) + 0.3) * (RED(2) + 0.3)) + sqrt((IMD(2) - %.17g) * (IMD(2) - %.17g)) + sqrt((RED(6) + 0.15) * (RED(6) + 0.15)) + sqrt((IMD(6) - %.17g) * (IMD(6) - %.17g))" (3.0*w1) (3.0*w1) (2.0*w1) (2.0*w1) w1 w1)
w ""
w "// ---- verdicts ----"
w "let ladderV = if maxreC < 0.000000000001 then (if devL < 0.000000000001 then \"IMAGINARY_LADDER_MEASURED\" else \"NO\") else \"NO\""
w "let invV = if sqrt(rec8 * rec8) < 0.000000000001 then (if sqrt(imc8 * imc8) < 0.000000000001 then \"INVARIANT_AT_ZERO\" else \"NO\") else \"NO\""
w "let lattV = if devD < 0.0000000001 then \"DAMPED_LATTICE_EXACT\" else \"NO\""
w "let dispV = if sqrt((red8 + 0.3) * (red8 + 0.3)) < 0.0000000001 then (if sqrt(imd8 * imd8) < 0.000000000001 then \"INVARIANT_DISPLACED_TO_MINUS_2GAMMA\" else \"NO\") else \"NO\""
w ""
w "// the ladder, the kernel, the lattice, the displacement:"
let pins = [
  "imc0 = 3"
  "imc2 = 2"
  "imc4 = 0.999999999999999"
  "imc6 = 0.999999999999998"
  "rec8 = -1.54427505080323e-16"
  "imc8 = 0"
  "maxreC = 9.436895676534e-16"
  "devL = 9.65894031423886e-15"
  "ladderV = \"IMAGINARY_LADDER_MEASURED\""
  "invV = \"INVARIANT_AT_ZERO\""
  "red0 = -0.449999999999983"
  "imd0 = 2.96605798999278"
  "red2 = -0.300000000000001"
  "imd2 = 1.97737199332852"
  "red6 = -0.149999999999999"
  "imd6 = 0.988685996664264"
  "red8 = -0.300000000000004"
  "imd8 = 0"
  "devD = 2.93376434257198e-14"
  "lattV = \"DAMPED_LATTICE_EXACT\""
  "dispV = \"INVARIANT_DISPLACED_TO_MINUS_2GAMMA\"" ]
pins |> List.iter (fun p -> w ("// EXPECT: " + p))

IO.File.WriteAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "45_spectrum_of_the_law.blade"), sb.ToString())
printfn "blade written"
