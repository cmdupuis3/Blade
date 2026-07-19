// tica_build.fsx — oracle + generator for examples/physics/44 (TICA/VAC).
// 2D isotropic oscillator, 8 trajectories x 1500 Verlet steps (h = 0.05),
// dictionary = 4 linear + 10 quadratic monomials (14), lags 12/24 steps
// (tau = 0.6 / 1.2). Observation noise: uniform width sigma*sqrt(12),
// exact-double LCG (s*48271 mod 2147483647), applied to OBSERVED coords only.
// Clean VAC spectrum: {1 x4 (u(2) invariants), cos tau x4, cos 2tau x6}.
// Oracle mirrors the blade computation exactly (same loop order); it also
// runs whiten+eigh to print reference TICA values.

open System
open System.Text

let h = 0.05
let TSTEPS = 1500
let LAG1 = 12
let LAG2 = 24
let NTRAJ = 8
let NACC = float (NTRAJ * (TSTEPS - LAG2))     // accumulated samples
let ics = [| 2.0, 0.1, 0.0, 1.6; 1.9, 0.1, 0.9, 0.1; 1.6, -0.9, 0.8, 0.3; 0.4, 1.3, 0.9, -0.2
             1.0, 0.5, -1.2, 0.9; 0.3, -0.8, 1.5, 0.7; 1.8, 1.1, 0.2, -1.1; 0.1, 1.7, 0.1, -1.2 |]
let D = 14

// ---------- oracle simulation (mirrors the blade block exactly) ----------
let runStats (sigma: float) =
    let SM = Array.zeroCreate D
    let S20 = Array.zeroCreate (D * D)
    let S12 = Array.zeroCreate (D * D)
    let S24 = Array.zeroCreate (D * D)
    let SE = Array.zeroCreate NTRAJ
    let SE2 = Array.zeroCreate NTRAJ
    let mutable s = 20260717.0
    let draw () =
        s <- s * 48271.0 - floor (s * 48271.0 / 2147483647.0) * 2147483647.0
        (s / 2147483647.0 - 0.5) * 3.46410161513775 * sigma
    let dict (a: float) (b: float) (c: float) (d: float) =
        [| a; b; c; d; a*a; b*b; c*c; d*d; a*b; a*c; a*d; b*c; b*d; c*d |]
    for k in 0 .. NTRAJ - 1 do
        let (x0, y0, px0, py0) = ics.[k]
        let mutable x = x0
        let mutable y = y0
        let mutable px = px0
        let mutable py = py0
        let ring = Array.zeroCreate (4 * LAG2)
        for t in 0 .. TSTEPS - 1 do
            let pxh = px - 0.5 * h * x
            let pyh = py - 0.5 * h * y
            x <- x + h * pxh
            y <- y + h * pyh
            px <- pxh - 0.5 * h * x
            py <- pyh - 0.5 * h * y
            let xo = x + draw ()
            let yo = y + draw ()
            let pxo = px + draw ()
            let pyo = py + draw ()
            let slot = t - (t / LAG2) * LAG2
            let slotL1 = (t + LAG1) - ((t + LAG1) / LAG2) * LAG2
            if t >= LAG2 then
                let dv = dict xo yo pxo pyo
                let l24 = dict ring.[4*slot] ring.[4*slot+1] ring.[4*slot+2] ring.[4*slot+3]
                let l12 = dict ring.[4*slotL1] ring.[4*slotL1+1] ring.[4*slotL1+2] ring.[4*slotL1+3]
                for i in 0 .. D - 1 do
                    SM.[i] <- SM.[i] + dv.[i]
                    for j in 0 .. D - 1 do
                        S20.[i*D+j] <- S20.[i*D+j] + dv.[i] * dv.[j]
                        S12.[i*D+j] <- S12.[i*D+j] + l12.[i] * dv.[j]
                        S24.[i*D+j] <- S24.[i*D+j] + l24.[i] * dv.[j]
                let eo = 0.5 * (dv.[4] + dv.[5] + dv.[6] + dv.[7])
                SE.[k] <- SE.[k] + eo
                SE2.[k] <- SE2.[k] + eo * eo
            ring.[4*slot] <- xo
            ring.[4*slot+1] <- yo
            ring.[4*slot+2] <- pxo
            ring.[4*slot+3] <- pyo
    (SM, S20, S12, S24, SE, SE2)

// ---------- eigh (descending, columns) ----------
let eigh (A0: float[,]) =
    let n = Array2D.length1 A0
    let A = Array2D.copy A0
    let V = Array2D.init n n (fun i j -> if i = j then 1.0 else 0.0)
    let offNorm () =
        let mutable sq = 0.0
        for i in 0 .. n - 1 do
            for j in i + 1 .. n - 1 do sq <- sq + A.[i, j] * A.[i, j]
        sq
    let mutable sweep = 0
    while offNorm () > 1e-26 && sweep < 100 do
        sweep <- sweep + 1
        for p in 0 .. n - 2 do
            for q in p + 1 .. n - 1 do
                if abs A.[p, q] > 1e-20 then
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

let mm (A: float[,]) (B: float[,]) =
    let n = Array2D.length1 A
    Array2D.init n n (fun i j ->
        let mutable sq = 0.0
        for k in 0 .. n - 1 do sq <- sq + A.[i, k] * B.[k, j]
        sq)

let postprocess tag (SM: float[], S20: float[], S12: float[], S24: float[], SE: float[], SE2: float[]) =
    let mu = SM |> Array.map (fun v -> v / NACC)
    let C0 = Array2D.init D D (fun i j -> S20.[i*D+j] / NACC - mu.[i] * mu.[j])
    let sym (S: float[]) = Array2D.init D D (fun i j -> (S.[i*D+j] + S.[j*D+i]) / 2.0 / NACC - mu.[i] * mu.[j])
    let CT12 = sym S12
    let CT24 = sym S24
    let (L0, Q0) = eigh C0
    let QS = Array2D.init D D (fun i k -> Q0.[i, k] / sqrt L0.[k])
    let QS2 = Array2D.init D D (fun i k -> Q0.[i, k] * sqrt L0.[k])
    let Q0T = Array2D.init D D (fun i j -> Q0.[j, i])
    let W = mm QS Q0T
    let W1 = mm QS2 Q0T
    let M12 = mm (mm W CT12) W
    let M24 = mm (mm W CT24) W
    let (LT12, U12) = eigh M12
    let (LT24, U24) = eigh M24
    // projections of E and the oscillating probe onto the top-4 whitened span
    let vE = Array.init D (fun i -> if i >= 4 && i <= 7 then 0.5 else 0.0)
    let vO = Array.init D (fun i -> if i = 4 then 1.0 else (if i = 6 then -1.0 else 0.0))
    let tilde (v: float[]) = Array.init D (fun i -> Array.init D (fun j -> W1.[i, j] * v.[j]) |> Array.sum)
    let rayleigh (v: float[]) (M: float[,]) =
        let tv = tilde v
        let den = tv |> Array.sumBy (fun x -> x * x)
        let mutable num = 0.0
        for i in 0 .. D - 1 do
            for j in 0 .. D - 1 do num <- num + tv.[i] * M.[i, j] * tv.[j]
        num / den
    let proj (v: float[]) (U: float[,]) =
        let tv = tilde v
        let den = tv |> Array.sumBy (fun x -> x * x)
        let mutable num = 0.0
        for k in 0 .. 3 do
            let mutable dp = 0.0
            for i in 0 .. D - 1 do dp <- dp + tv.[i] * U.[i, k]
            num <- num + dp * dp
        num / den
    let plateau = [ 0 .. 3 ] |> List.sumBy (fun k -> abs (LT12.[k] - LT24.[k]))
    let oscMove = abs (LT12.[4] - LT24.[4])
    let dmax =
        [ 0 .. NTRAJ - 1 ] |> List.map (fun k ->
            let n = float (TSTEPS - LAG2)
            let m = SE.[k] / n
            let v = SE2.[k] / n - m * m
            sqrt (abs v) / m) |> List.max
    printfn "=== %s ===" tag
    printfn "L0 head %s tail %s" (L0.[0..2] |> Array.map (sprintf "%.4f") |> String.concat " ") (L0.[11..13] |> Array.map (sprintf "%.4g") |> String.concat " ")
    printfn "LT12: %s" (LT12 |> Array.map (sprintf "%.6f") |> String.concat " ")
    printfn "LT24: %s" (LT24 |> Array.map (sprintf "%.6f") |> String.concat " ")
    printfn "plateau(top4) %.6f  oscMove(5th) %.6f" plateau oscMove
    printfn "projE12 %.6f  projOsc12 %.6f  dmaxE %.6f" (proj vE U12) (proj vO U12) dmax
    printfn "rayE12 %.12f rayE24 %.12f rayO12 %.12f rayO24 %.12f" (rayleigh vE M12) (rayleigh vE M24) (rayleigh vO M12) (rayleigh vO M24)
    printfn "(cos tau = %.6f, cos 2tau = %.6f)" (cos 0.6) (cos 1.2)

runStats 0.0 |> postprocess "CLEAN sigma=0"
runStats 0.1 |> postprocess "NOISY sigma=0.1"

// ---------- generate the blade file ----------
let sb = StringBuilder()
let w (s: string) = sb.AppendLine(s) |> ignore
let header = [
  "// TEST: Physics -- The Detector That Survives Noise: TICA/VAC In-Language"
  "// ============================================================================"
  "// Review-gap 3, closed. The arc's invariant detector (ex 09-16) reads"
  "// conserved quantities as ZERO-VARIANCE combinations -- a criterion that"
  "// dies at any measurement-noise floor. The noise-robust reformulation is"
  "// the generalized eigenproblem C(tau) v = lambda C(0) v (TICA/VAC):"
  "// white observation noise is lag-uncorrelated, so it inflates C(0) ONLY;"
  "// invariants keep a tau-INDEPENDENT eigenvalue (a plateau) while every"
  "// oscillating direction's eigenvalue moves with tau. It needed an"
  "// eigensolver; math.eigh is here, and so -- via the new imperative"
  "// blocks -- is the arc's first fully IN-LANGUAGE dynamics: 8 velocity-"
  "// Verlet trajectories of the 2D isotropic oscillator (12,000 symplectic"
  "// steps, h = 0.05) with an exact-double LCG observation channel, no"
  "// external data at all -- not even emitted tables."
  "// Dictionary: all 14 linear+quadratic monomials of (x, y, px, py)."
  "// The clean VAC spectrum of this system is EXACTLY {1 x4, cos(tau) x4,"
  "// cos(2 tau) x6}: the eigenvalue-1 subspace is the u(2) invariant algebra"
  "// {Ex+Ey, Ex-Ey, Lz, xy+px py} (superintegrability made spectral), the"
  "// linear monomials rotate at the orbital frequency, the remaining six"
  "// quadratics at twice it. Lags: 12 and 24 steps (tau = 0.6, 1.2)."
  "// FINDINGS (pinned):"
  "//   * CLEAN: top four eigenvalues sit at 1 to ~1e-3 (Verlet floor); the"
  "//     next cluster reads cos(0.6) = 0.8253 at lag 12 and cos(1.2) ="
  "//     0.3624 at lag 24, the last six read cos(1.2) / cos(2.4) -- the"
  "//     predicted trigonometric spectrum, measured."
  "//   * NOISE KILLS THE OLD DETECTOR: sigma = 0.1 observation noise lifts"
  "//     the per-trajectory relative dispersion of E from the 1e-4 Verlet"
  "//     floor to ~1e-1 -- three orders; every zero-variance verdict flips"
  "//     VARIANT. The detector of ex 09-16 is structurally blind here."
  "//   * TICA SURVIVES: under the same noise the invariant quartet's"
  "//     eigenvalues drop (each by its own noise load) but stay TOP and"
  "//     PLATEAU: sum of top-4 |lambda(tau1) - lambda(tau2)| stays ~1e-2"
  "//     while the 5th eigenvalue moves ~0.4 between the same two lags."
  "//     tau-independence, not magnitude, is the invariant's signature."
  "//   * THE DETECTOR VERDICT, REBORN PER QUANTITY: the Rayleigh quotient"
  "//     lambda_v(tau) = v~' M(tau) v~ / v~'v~ is the noise-robust replacement"
  "//     for the old dispersion test. Under noise the energy direction reads"
  "//     ~0.88 at BOTH lags (plateau, |diff| = 0.003) while the oscillating"
  "//     probe (x^2 - px^2) swings from +0.36 to -0.74 between the same two"
  "//     lags. tau-independence per quantity -- immune even to the eigenvalue"
  "//     near-degeneracies that mix the sorted eigenvectors."
  "// Two-route discipline: an independent F# route (same integrator/LCG,"
  "// own Jacobi) reproduces every pin; the clean spectrum doubles as an"
  "// analytic oracle (cos clusters known in closed form)."
  "// ============================================================================"
  "import math as m"
  "" ]
header |> List.iter w

// ICs literal
w "// ---- initial conditions: spread in all four invariants ----"
let icRows = ics |> Array.map (fun (a, b, c, d) -> sprintf "  [%.1f, %.1f, %.1f, %.1f]" a b c d) |> String.concat ",\n"
w (sprintf "let ICS: Array<Float64 like Idx<8>, Idx<4>> = [\n%s\n]" icRows)
w ""

// the integration block, parameterized by sigma; returns the 618-slot stats array
let zeros n = List.replicate n "0.0" |> String.concat ", "
let statBlock (name: string) (sigma: string) =
    w (sprintf "// ---- integrate + accumulate, sigma = %s (stats: mean 0..13, C0-raw 14..209," sigma)
    w "//      lag12-raw 210..405, lag24-raw 406..601, E-sums 602..609, E-sq 610..617) ----"
    w (sprintf "let %s = {" name)
    w (sprintf "    let mut st = [%s]" (zeros 618))
    w (sprintf "    let mut ring = [%s]" (zeros 96))
    w "    let mut dv = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
    w "    let mut dl = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
    w "    let mut dm = [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]"
    w "    let mut s = 20260717.0"
    w "    let mut x = 0.0"
    w "    let mut y = 0.0"
    w "    let mut px = 0.0"
    w "    let mut py = 0.0"
    w "    for k in 0..8 {"
    w "        x = ICS(k, 0)"
    w "        y = ICS(k, 1)"
    w "        px = ICS(k, 2)"
    w "        py = ICS(k, 3)"
    w "        for t in 0..1500 {"
    w "            let pxh = px - 0.025 * x"
    w "            let pyh = py - 0.025 * y"
    w "            x = x + 0.05 * pxh"
    w "            y = y + 0.05 * pyh"
    w "            px = pxh - 0.025 * x"
    w "            py = pyh - 0.025 * y"
    w "            s = s * 48271.0 - floor(s * 48271.0 / 2147483647.0) * 2147483647.0"
    w (sprintf "            let xo = x + (s / 2147483647.0 - 0.5) * 3.46410161513775 * %s" sigma)
    w "            s = s * 48271.0 - floor(s * 48271.0 / 2147483647.0) * 2147483647.0"
    w (sprintf "            let yo = y + (s / 2147483647.0 - 0.5) * 3.46410161513775 * %s" sigma)
    w "            s = s * 48271.0 - floor(s * 48271.0 / 2147483647.0) * 2147483647.0"
    w (sprintf "            let pxo = px + (s / 2147483647.0 - 0.5) * 3.46410161513775 * %s" sigma)
    w "            s = s * 48271.0 - floor(s * 48271.0 / 2147483647.0) * 2147483647.0"
    w (sprintf "            let pyo = py + (s / 2147483647.0 - 0.5) * 3.46410161513775 * %s" sigma)
    w "            let slot = t - (t / 24) * 24"
    w "            let slotL = (t + 12) - ((t + 12) / 24) * 24"
    w "            let g = if t > 23 then 1.0 else 0.0"
    w "                dv(0) = xo"
    w "                dv(1) = yo"
    w "                dv(2) = pxo"
    w "                dv(3) = pyo"
    w "                dv(4) = xo * xo"
    w "                dv(5) = yo * yo"
    w "                dv(6) = pxo * pxo"
    w "                dv(7) = pyo * pyo"
    w "                dv(8) = xo * yo"
    w "                dv(9) = xo * pxo"
    w "                dv(10) = xo * pyo"
    w "                dv(11) = yo * pxo"
    w "                dv(12) = yo * pyo"
    w "                dv(13) = pxo * pyo"
    w "                dm(0) = ring(4 * slot)"
    w "                dm(1) = ring(4 * slot + 1)"
    w "                dm(2) = ring(4 * slot + 2)"
    w "                dm(3) = ring(4 * slot + 3)"
    w "                dm(4) = dm(0) * dm(0)"
    w "                dm(5) = dm(1) * dm(1)"
    w "                dm(6) = dm(2) * dm(2)"
    w "                dm(7) = dm(3) * dm(3)"
    w "                dm(8) = dm(0) * dm(1)"
    w "                dm(9) = dm(0) * dm(2)"
    w "                dm(10) = dm(0) * dm(3)"
    w "                dm(11) = dm(1) * dm(2)"
    w "                dm(12) = dm(1) * dm(3)"
    w "                dm(13) = dm(2) * dm(3)"
    w "                dl(0) = ring(4 * slotL)"
    w "                dl(1) = ring(4 * slotL + 1)"
    w "                dl(2) = ring(4 * slotL + 2)"
    w "                dl(3) = ring(4 * slotL + 3)"
    w "                dl(4) = dl(0) * dl(0)"
    w "                dl(5) = dl(1) * dl(1)"
    w "                dl(6) = dl(2) * dl(2)"
    w "                dl(7) = dl(3) * dl(3)"
    w "                dl(8) = dl(0) * dl(1)"
    w "                dl(9) = dl(0) * dl(2)"
    w "                dl(10) = dl(0) * dl(3)"
    w "                dl(11) = dl(1) * dl(2)"
    w "                dl(12) = dl(1) * dl(3)"
    w "                dl(13) = dl(2) * dl(3)"
    w "                for i in 0..14 {"
    w "                    st(i) = st(i) + g * dv(i)"
    w "                    for j in 0..14 {"
    w "                        st(14 + i * 14 + j) = st(14 + i * 14 + j) + g * dv(i) * dv(j)"
    w "                        st(210 + i * 14 + j) = st(210 + i * 14 + j) + g * dl(i) * dv(j)"
    w "                        st(406 + i * 14 + j) = st(406 + i * 14 + j) + g * dm(i) * dv(j)"
    w "                    }"
    w "                }"
    w "                let eo = 0.5 * (dv(4) + dv(5) + dv(6) + dv(7))"
    w "                st(602 + k) = st(602 + k) + g * eo"
    w "                st(610 + k) = st(610 + k) + g * eo * eo"
    w "            ring(4 * slot) = xo"
    w "            ring(4 * slot + 1) = yo"
    w "            ring(4 * slot + 2) = pxo"
    w "            ring(4 * slot + 3) = pyo"
    w "        }"
    w "    }"
    w "    st"
    w "}"
    w ""
statBlock "STC" "0.0"
statBlock "STN" "0.1"

// post-processing per suffix
let post (sfx: string) (stn: string) =
    w (sprintf "// ---- TICA pipeline, %s ----" (if sfx = "C" then "clean" else "noisy"))
    w (sprintf "let MU%s = method_for(range<Idx<14>>) <@> lambda(i: Int64) -> %s(i) / 11808.0 |> compute" sfx stn)
    w (sprintf "let C0%s: Array<Float64 like Idx<14>, Idx<14>> = method_for(range<Idx<14>>) <*> method_for(range<Idx<14>>) <@> lambda(i: Int64, j: Int64) -> %s(14 + i * 14 + j) / 11808.0 - MU%s(i) * MU%s(j) |> compute" sfx stn sfx sfx)
    w (sprintf "let CT12%s = method_for(range<Idx<14>>) <*> method_for(range<Idx<14>>) <@> lambda(i: Int64, j: Int64) -> (%s(210 + i * 14 + j) + %s(210 + j * 14 + i)) / 2.0 / 11808.0 - MU%s(i) * MU%s(j) |> compute" sfx stn stn sfx sfx)
    w (sprintf "let CT24%s = method_for(range<Idx<14>>) <*> method_for(range<Idx<14>>) <@> lambda(i: Int64, j: Int64) -> (%s(406 + i * 14 + j) + %s(406 + j * 14 + i)) / 2.0 / 11808.0 - MU%s(i) * MU%s(j) |> compute" sfx stn stn sfx sfx)
    w (sprintf "let (Q0%s, L0%s) = m.eigh(C0%s)" sfx sfx sfx)
    w (sprintf "let QS%s = method_for(range<Idx<14>>) <*> method_for(range<Idx<14>>) <@> lambda(i: Int64, k: Int64) -> Q0%s(i, k) / sqrt(L0%s(k)) |> compute" sfx sfx sfx)
    w (sprintf "let WH%s = method_for(QS%s, Q0%s) <@> lambda(u: Array<Float64 like Idx<14>>, v: Array<Float64 like Idx<14>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let T12%s = method_for(WH%s, CT12%s) <@> lambda(u: Array<Float64 like Idx<14>>, v: Array<Float64 like Idx<14>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let M12%s: Array<Float64 like Idx<14>, Idx<14>> = method_for(T12%s, WH%s) <@> lambda(u: Array<Float64 like Idx<14>>, v: Array<Float64 like Idx<14>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let T24%s = method_for(WH%s, CT24%s) <@> lambda(u: Array<Float64 like Idx<14>>, v: Array<Float64 like Idx<14>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let M24%s: Array<Float64 like Idx<14>, Idx<14>> = method_for(T24%s, WH%s) <@> lambda(u: Array<Float64 like Idx<14>>, v: Array<Float64 like Idx<14>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let (U12%s, LT12%s) = m.eigh(M12%s)" sfx sfx sfx)
    w (sprintf "let (U24%s, LT24%s) = m.eigh(M24%s)" sfx sfx sfx)
    w (sprintf "let pl%s = sqrt((LT12%s(0) - LT24%s(0)) * (LT12%s(0) - LT24%s(0))) + sqrt((LT12%s(1) - LT24%s(1)) * (LT12%s(1) - LT24%s(1))) + sqrt((LT12%s(2) - LT24%s(2)) * (LT12%s(2) - LT24%s(2))) + sqrt((LT12%s(3) - LT24%s(3)) * (LT12%s(3) - LT24%s(3)))" sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx sfx)
    w (sprintf "let om%s = sqrt((LT12%s(4) - LT24%s(4)) * (LT12%s(4) - LT24%s(4)))" sfx sfx sfx sfx sfx)
    // back-transform basis: W1 = Q0 D^{+1/2} Q0^T ; vE~ = W1 vE ; vOsc~ = W1 vOsc
    w (sprintf "let QT%s = method_for(range<Idx<14>>) <*> method_for(range<Idx<14>>) <@> lambda(i: Int64, k: Int64) -> Q0%s(i, k) * sqrt(L0%s(k)) |> compute" sfx sfx sfx)
    w (sprintf "let W1%s = method_for(QT%s, Q0%s) <@> lambda(u: Array<Float64 like Idx<14>>, v: Array<Float64 like Idx<14>>) -> prodsum(u, v) |> compute" sfx sfx sfx)
    w (sprintf "let VE%s = method_for(range<Idx<14>>) <@> lambda(i: Int64) -> 0.5 * (W1%s(i, 4) + W1%s(i, 5) + W1%s(i, 6) + W1%s(i, 7)) |> compute" sfx sfx sfx sfx sfx)
    w (sprintf "let VO%s = method_for(range<Idx<14>>) <@> lambda(i: Int64) -> W1%s(i, 4) - W1%s(i, 6) |> compute" sfx sfx sfx)
    // per-quantity Rayleigh quotients in the whitened metric: the detector verdict,
    // reborn -- lambda_v(tau) is tau-independent iff v is invariant
    let ray (name: string) (vec: string) (mat: string) =
        w (sprintf "let %s = {" name)
        w "    let mut num = 0.0"
        w "    let mut den = 0.0"
        w (sprintf "    for i in 0..14 { den = den + %s(i) * %s(i) }" vec vec)
        w "    for i in 0..14 {"
        w (sprintf "        for j in 0..14 { num = num + %s(i) * %s(i, j) * %s(j) }" vec mat vec)
        w "    }"
        w "    num / den"
        w "}"
    ray (sprintf "rE12%s" sfx) (sprintf "VE%s" sfx) (sprintf "M12%s" sfx)
    ray (sprintf "rE24%s" sfx) (sprintf "VE%s" sfx) (sprintf "M24%s" sfx)
    ray (sprintf "rO12%s" sfx) (sprintf "VO%s" sfx) (sprintf "M12%s" sfx)
    ray (sprintf "rO24%s" sfx) (sprintf "VO%s" sfx) (sprintf "M24%s" sfx)
    // per-trajectory zero-variance detector: max relative dispersion of E
    w (sprintf "let dmax%s = {" sfx)
    w "    let mut best = 0.0"
    w "    for k in 0..8 {"
    w (sprintf "        let em = %s(602 + k) / 1476.0" stn)
    w (sprintf "        let ev = %s(610 + k) / 1476.0 - em * em" stn)
    w "        let rel = sqrt(sqrt(ev * ev)) / em"
    w "        best = if rel > best then rel else best"
    w "    }"
    w "    best"
    w "}"
    w (sprintf "let lt12a%s = LT12%s(0)" sfx sfx)
    w (sprintf "let lt12d%s = LT12%s(3)" sfx sfx)
    w (sprintf "let lt12e%s = LT12%s(4)" sfx sfx)
    w (sprintf "let lt12i%s = LT12%s(8)" sfx sfx)
    w (sprintf "let lt24e%s = LT24%s(4)" sfx sfx)
    w ""
post "C" "STC"
post "N" "STN"

w "// ---- verdicts ----"
w "let cleanV = if lt12dC > 0.99 then (if lt12eC < 0.9 then \"FOUR_INVARIANTS_AT_ONE\" else \"NO\") else \"NO\""
w "let cosV = if sqrt((lt12eC - 0.825336) * (lt12eC - 0.825336)) < 0.05 then (if sqrt((lt12iC - 0.362358) * (lt12iC - 0.362358)) < 0.08 then \"COS_CLUSTERS_AS_PREDICTED\" else \"NO\") else \"NO\""
w "let killV = if dmaxC < 0.001 then (if dmaxN > 0.05 then \"ZERO_VARIANCE_DETECTOR_KILLED\" else \"NO\") else \"NO\""
w "let plateauV = if plN < 0.05 then (if omN > 0.1 then \"INVARIANTS_PLATEAU_UNDER_NOISE\" else \"NO\") else \"NO\""
w "let rayV = if sqrt((rE12N - rE24N) * (rE12N - rE24N)) < 0.02 then (if rE12N > 0.8 then (if sqrt((rO12N - rO24N) * (rO12N - rO24N)) > 0.5 then \"ENERGY_PLATEAUS_OSCILLATOR_MOVES\" else \"NO\") else \"NO\") else \"NO\""
w ""
w "// the spectrum, the kill, the plateau, the recovery:"
let pins = [
  "lt12aC = 1.00636317473505"
  "lt12dC = 1.00002561504601"
  "lt12eC = 0.829776702463998"
  "lt12iC = 0.362270476478817"
  "lt24eC = 0.370016822317159"
  "cleanV = \"FOUR_INVARIANTS_AT_ONE\""
  "cosV = \"COS_CLUSTERS_AS_PREDICTED\""
  "plC = 0.000113032431726223"
  "omC = 0.459759880146839"
  "rE12C = 0.999991281219178"
  "rE24C = 1.00000642780998"
  "rO12C = 0.362274679654077"
  "rO24C = -0.737624341874798"
  "dmaxC = 0.000220805419249497"
  "dmaxN = 0.11991037124116"
  "killV = \"ZERO_VARIANCE_DETECTOR_KILLED\""
  "lt12aN = 0.986011029932157"
  "lt12dN = 0.845388597692952"
  "lt12eN = 0.820554575143416"
  "lt12iN = 0.355131300761363"
  "lt24eN = 0.364657568355723"
  "plN = 0.0140412641665086"
  "omN = 0.455897006787693"
  "plateauV = \"INVARIANTS_PLATEAU_UNDER_NOISE\""
  "rE12N = 0.881436112112715"
  "rE24N = 0.884377231310071"
  "rO12N = 0.353414767157682"
  "rO24N = -0.720952145609765"
  "rayV = \"ENERGY_PLATEAUS_OSCILLATOR_MOVES\"" ]
pins |> List.iter (fun p -> w ("// EXPECT: " + p))

IO.File.WriteAllText(@"C:\Users\cdupu\Documents\_blade-compiler\examples\physics\44_detector_survives_noise.blade", sb.ToString())
printfn "blade written"
