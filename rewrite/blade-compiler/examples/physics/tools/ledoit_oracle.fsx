// ledoit_oracle.fsx — untrusted emitter + oracle for examples/physics/43.
// Spiked covariance: d = 40 chain registers, 4 sine normal modes
//   u_k(i) = sqrt(2/d) sin(pi k (i+0.5)/d), strengths l = (10, 5, 2.5, 1.3),
// white unit noise; observer sees N = 160 samples (gamma = 1/4).
// BBP threshold 1 + sqrt(gamma) = 1.5: modes 1-3 visible (biased+rotated),
// mode 4 swallowed by the bulk. Emits ±1 data tables (Z d x N, G 4 x N) and
// reference values for the blade file's pins.

open System
open System.Text

let d = 40
let N = 160
let gamma = float d / float N
let ls = [| 10.0; 5.0; 2.5; 1.3 |]
let K = 4

// minstd LCG -> ±1
let mutable state = 20260717UL
let nextPm () =
    state <- (state * 48271UL) % 2147483647UL
    if state % 2UL = 0UL then 1.0 else -1.0

let Z = Array2D.init d N (fun _ _ -> nextPm ())
let G = Array2D.init K N (fun _ _ -> nextPm ())

let u k i = sqrt (2.0 / float d) * sin (Math.PI * float k * (float i + 0.5) / float d)

// data X(i,t) = Z(i,t) + sum_k sqrt(l_k - 1) u_k(i) G(k,t)
let X = Array2D.init d N (fun i t ->
    let mutable s = Z.[i, t]
    for k in 0 .. K - 1 do s <- s + sqrt (ls.[k] - 1.0) * (u (k + 1) i) * G.[k, t]
    s)

// sample covariance (uncentered; population mean is 0)
let E = Array2D.init d d (fun i j ->
    let mutable s = 0.0
    for t in 0 .. N - 1 do s <- s + X.[i, t] * X.[j, t]
    s / float N)

// truth
let Sig = Array2D.init d d (fun i j ->
    let mutable s = if i = j then 1.0 else 0.0
    for k in 0 .. K - 1 do s <- s + (ls.[k] - 1.0) * (u (k + 1) i) * (u (k + 1) j)
    s)

// ---------- cyclic Jacobi eigh (descending) ----------
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
    while offNorm () > 1e-26 && sweep < 100 do
        sweep <- sweep + 1
        for p in 0 .. n - 2 do
            for q in p + 1 .. n - 1 do
                if abs A.[p, q] > 1e-20 then
                    let theta = (A.[q, q] - A.[p, p]) / (2.0 * A.[p, q])
                    let t = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (theta * theta + 1.0))
                    let c = 1.0 / sqrt (t * t + 1.0)
                    let s = t * c
                    for k in 0 .. n - 1 do
                        let akp = A.[k, p]
                        let akq = A.[k, q]
                        A.[k, p] <- c * akp - s * akq
                        A.[k, q] <- s * akp + c * akq
                    for k in 0 .. n - 1 do
                        let apk = A.[p, k]
                        let aqk = A.[q, k]
                        A.[p, k] <- c * apk - s * aqk
                        A.[q, k] <- s * apk + c * aqk
                    for k in 0 .. n - 1 do
                        let vkp = V.[k, p]
                        let vkq = V.[k, q]
                        V.[k, p] <- c * vkp - s * vkq
                        V.[k, q] <- s * vkp + c * vkq
    let idx = [| 0 .. n - 1 |] |> Array.sortByDescending (fun i -> A.[i, i])
    let lam = idx |> Array.map (fun i -> A.[i, i])
    let Q = Array2D.init n n (fun r c -> V.[r, idx.[c]])
    (lam, Q)

let (lam, Q) = eigh E

// ---------- BBP analytics vs measurement ----------
printfn "gamma = %.4f, threshold l* = %.4f, bulk edge = %.4f" gamma (1.0 + sqrt gamma) ((1.0 + sqrt gamma) ** 2.0)
printfn "%-4s %-10s %-14s %-14s | %-12s %-12s" "k" "l_true" "lam_BBP" "lam_measured" "ovl_BBP" "ovl_measured"
let ovl k col =
    let mutable s = 0.0
    for i in 0 .. d - 1 do s <- s + Q.[i, col] * u k i
    s * s
for k in 0 .. K - 1 do
    let l = ls.[k]
    let lamBBP = if l - 1.0 > sqrt gamma then l * (1.0 + gamma / (l - 1.0)) else (1.0 + sqrt gamma) ** 2.0
    let ovlBBP = if l - 1.0 > sqrt gamma then (1.0 - gamma / ((l - 1.0) * (l - 1.0))) / (1.0 + gamma / (l - 1.0)) else 0.0
    printfn "%-4d %-10.2f %-14.6f %-14.6f | %-12.6f %-12.6f" (k + 1) l lamBBP lam.[k] ovlBBP (ovl (k + 1) k)
// sub-threshold mode: max overlap with ANY sample eigenvector
let mutable omax4 = 0.0
let mutable oarg4 = -1
for c in 0 .. d - 1 do
    let o = ovl 4 c
    if o > omax4 then omax4 <- o; oarg4 <- c
printfn "mode4 max overlap over all eigenvectors: %.6f at col %d (1/d = %.4f)" omax4 oarg4 (1.0 / float d)
printfn "bulk top (lam_4): %.6f vs edge %.6f" lam.[3] ((1.0 + sqrt gamma) ** 2.0)
printfn "lam head: %s" (lam.[0 .. 7] |> Array.map (sprintf "%.4f") |> String.concat " ")

// ---------- the cleaning ladder ----------
let frobErr (M: float[,]) =
    let mutable s = 0.0
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do
            let dv = M.[i, j] - Sig.[i, j]
            s <- s + dv * dv
    s
let recon (dv: float[]) = Array2D.init d d (fun i j ->
    let mutable s = 0.0
    for k in 0 .. d - 1 do s <- s + Q.[i, k] * dv.[k] * Q.[j, k]
    s)

// spike inversion for out-of-bulk eigenvalues: l = ((x+1-g) + sqrt((x+1-g)^2 - 4x))/2
let invertBBP x =
    let b = x + 1.0 - gamma
    (b + sqrt (b * b - 4.0 * x)) / 2.0
let edge = (1.0 + sqrt gamma) ** 2.0

let errRaw = frobErr E
// naive: true l on the top 3, 1.0 on the rest (pays the rotation tax)
let dNaive = Array.init d (fun k -> if k < 3 then ls.[k] else 1.0)
let errNaive = frobErr (recon dNaive)
// RIE via inverted BBP + overlap law: d_k = 1 + (l^ - 1) * ovl^(l^)
let dRIE = Array.init d (fun k ->
    if lam.[k] > edge + 0.000001 then
        let lh = invertBBP lam.[k]
        let oh = (1.0 - gamma / ((lh - 1.0) * (lh - 1.0))) / (1.0 + gamma / (lh - 1.0))
        1.0 + (lh - 1.0) * oh
    else 1.0)
let errRIE = frobErr (recon dRIE)
// oracle bound: d_k = v_k^T Sig v_k (best possible in the sample basis)
let dOracle = Array.init d (fun k ->
    let mutable s = 0.0
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do s <- s + Q.[i, k] * Sig.[i, j] * Q.[j, k]
    s)
let errOracle = frobErr (recon dOracle)
printfn ""
printfn "spikes above edge: %d" (Array.length (lam |> Array.filter (fun x -> x > edge + 0.000001)))
printfn "l^ recovered: %s (true 10, 5, 2.5)" ([| 0; 1; 2 |] |> Array.map (fun k -> sprintf "%.4f" (invertBBP lam.[k])) |> String.concat " ")
printfn "RIE d: %s" (dRIE.[0 .. 3] |> Array.map (sprintf "%.4f") |> String.concat " ")
printfn "oracle d: %s" (dOracle.[0 .. 3] |> Array.map (sprintf "%.4f") |> String.concat " ")
printfn "err ladder: raw %.6f  naive(true l) %.6f  RIE %.6f  oracle %.6f" errRaw errNaive errRIE errOracle
printfn "improvement raw/RIE = %.2fx ; RIE/oracle = %.4f ; naive/RIE = %.3f" (errRaw / errRIE) (errRIE / errOracle) (errNaive / errRIE)
// unavoidable floor from the sub-threshold mode: (l4-1)^2 (its direction is lost)
printfn "sub-threshold floor (l4-1)^2 = %.4f vs oracle err %.6f" ((ls.[3] - 1.0) ** 2.0) errOracle

// ---------- ex 31 contrast: moments were never the obstacle ----------
let specMom p (v: float[]) = (v |> Array.sumBy (fun x -> x ** p)) / float d
let em1 = specMom 1.0 lam
let em2 = specMom 2.0 lam
let tm1 = (Array.init d (fun i -> Sig.[i, i]) |> Array.sum) / float d
let tm2 =
    let mutable s = 0.0
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do s <- s + Sig.[i, j] * Sig.[i, j]
    s / float d
let dm2 = em2 - gamma * em1 * em1          // ex 31's free deconvolution at order 2
printfn ""
printfn "m1: est %.6f truth %.6f | m2: raw %.6f deconv %.6f truth %.6f" em1 tm1 em2 dm2 tm2
printfn "m2 relerr: raw %.4f -> deconv %.4f (moments clean fine; matrix error above is untouched by them)" ((em2 - tm2) / tm2) ((dm2 - tm2) / tm2)

// ---------- emit blade literals ----------
let sb = StringBuilder()
let emitMat (name: string) (M: float[,]) (r: int) (c: int) =
    sb.AppendLine(sprintf "let %s: Array<Float64 like Idx<%d>, Idx<%d>> = [" name r c) |> ignore
    for i in 0 .. r - 1 do
        let row = [| for j in 0 .. c - 1 -> sprintf "%.1f" M.[i, j] |] |> String.concat ", "
        sb.AppendLine(sprintf "  [%s]%s" row (if i < r - 1 then "," else "")) |> ignore
    sb.AppendLine("]") |> ignore
emitMat "ZT" Z d N
emitMat "GT" G K N
IO.File.WriteAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "ledoit_literals.blade.txt"), sb.ToString())
printfn ""
printfn "literals written (Z %dx%d, G %dx%d, ±1 as x.0)" d N K N
