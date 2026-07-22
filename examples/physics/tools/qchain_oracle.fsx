// qchain_oracle.fsx — untrusted emitter + oracle for examples/physics/42 (ex-41 pattern).
// Mixed-field Ising chain L=5 (d=32), open boundaries:
//   H = J sum sz_i sz_{i+1} + g sum sx_i + sum h_i sz_i
// chaotic:    g = -1.05, h_i = 0.5 + 0.03*(i-2)  (gradient breaks reflection parity)
// integrable: g = -1.05, h_i = 0                 (transverse-field Ising, free fermion)
// Observables A = sx site 0, B = sz site L-1. Estimator = ex 29's crossing channel:
//   sequence (A(t), B, A(t), B); kappa4 = p - c12*c34 - c13*c24 - c14*c23;
//   qhat = 1 + kappa4/(c13*c24)  [= p - 2*cab^2 at infinite temperature].
// Emits: E/V blade literals (14 dp) + reference values. Two internal routes
// (eigenbasis X/Y real algebra vs computational-basis complex matmul) must agree.

open System
open System.Numerics
open System.Text

let L = 6
let d = 64
let J = 1.0
let g = -1.05
// irregular fields: kill reflection symmetry hard (weak gradients leave
// quasi-degenerate doublets that read as Poisson); stays chaotic
let hVals = [| 0.55; 0.71; 0.44; 0.68; 0.50; 0.62 |]
let hChaotic (i: int) = hVals.[i]
let hInteg (_: int) = 0.0

let bit n k = (n >>> k) &&& 1
let spin n k = 1.0 - 2.0 * float (bit n k)

let buildH (h: int -> float) =
    let H = Array2D.zeroCreate<float> d d
    for n in 0 .. d - 1 do
        let mutable diag = 0.0
        for i in 0 .. L - 2 do diag <- diag + J * spin n i * spin n (i + 1)
        for i in 0 .. L - 1 do diag <- diag + (h i) * spin n i
        H.[n, n] <- diag
        for i in 0 .. L - 1 do
            let m = n ^^^ (1 <<< i)
            H.[m, n] <- H.[m, n] + g
    H

// ---------- cyclic Jacobi for symmetric matrices ----------
let jacobi (A0: float[,]) =
    let A = Array2D.copy A0
    let V = Array2D.init d d (fun i j -> if i = j then 1.0 else 0.0)
    let offNorm () =
        let mutable s = 0.0
        for i in 0 .. d - 1 do
            for j in i + 1 .. d - 1 do s <- s + A.[i, j] * A.[i, j]
        s
    let mutable sweep = 0
    while offNorm () > 1e-28 && sweep < 100 do
        sweep <- sweep + 1
        for p in 0 .. d - 2 do
            for q in p + 1 .. d - 1 do
                if abs A.[p, q] > 1e-20 then
                    let theta = (A.[q, q] - A.[p, p]) / (2.0 * A.[p, q])
                    let t = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (theta * theta + 1.0))
                    let c = 1.0 / sqrt (t * t + 1.0)
                    let s = t * c
                    for k in 0 .. d - 1 do
                        let akp = A.[k, p]
                        let akq = A.[k, q]
                        A.[k, p] <- c * akp - s * akq
                        A.[k, q] <- s * akp + c * akq
                    for k in 0 .. d - 1 do
                        let apk = A.[p, k]
                        let aqk = A.[q, k]
                        A.[p, k] <- c * apk - s * aqk
                        A.[q, k] <- s * apk + c * aqk
                    for k in 0 .. d - 1 do
                        let vkp = V.[k, p]
                        let vkq = V.[k, q]
                        V.[k, p] <- c * vkp - s * vkq
                        V.[k, q] <- s * vkp + c * vkq
    // sort ascending by eigenvalue, permute columns of V
    let idx = [| 0 .. d - 1 |] |> Array.sortBy (fun i -> A.[i, i])
    let E = idx |> Array.map (fun i -> A.[i, i])
    let Vs = Array2D.init d d (fun r c -> V.[r, idx.[c]])
    (E, Vs)

// ---------- helpers ----------
let matmul (A: float[,]) (B: float[,]) =
    Array2D.init d d (fun i j ->
        let mutable s = 0.0
        for k in 0 .. d - 1 do s <- s + A.[i, k] * B.[k, j]
        s)
let transpose (A: float[,]) = Array2D.init d d (fun i j -> A.[j, i])
let maxAbs (A: float[,]) =
    let mutable m = 0.0
    for i in 0 .. d - 1 do
        for j in 0 .. d - 1 do m <- max m (abs A.[i, j])
    m

let checkEig (H: float[,]) (E: float[]) (V: float[,]) =
    let HV = matmul H V
    let VE = Array2D.init d d (fun i j -> V.[i, j] * E.[j])
    let R = Array2D.init d d (fun i j -> HV.[i, j] - VE.[i, j])
    let G = matmul (transpose V) V
    let GI = Array2D.init d d (fun i j -> G.[i, j] - (if i = j then 1.0 else 0.0))
    (maxAbs R, maxAbs GI)

let rStat (E: float[]) =
    // bulk only: drop 20% of levels at each spectral edge (non-universal)
    let lo = d / 5
    let hi = d - d / 5
    let Eb = E.[lo .. hi - 1]
    let m = Eb.Length
    let gaps = Array.init (m - 1) (fun i -> Eb.[i + 1] - Eb.[i])
    let rs =
        Array.init (m - 2) (fun i ->
            let a, b = gaps.[i], gaps.[i + 1]
            if max a b < 1e-12 then 0.0 else (min a b) / (max a b))
    Array.average rs

// observables in computational basis
let Amat = Array2D.init d d (fun m n -> if m = (n ^^^ 1) then 1.0 else 0.0)         // sx site 0
let Bmat = Array2D.init d d (fun m n -> if m = n then spin n (L - 1) else 0.0)      // sz site L-1

// ---------- route 1: eigenbasis real X/Y algebra (what the blade file does) ----------
// weights w over eigenstates (sum w = 1). qhat via ex-29 formula with centering.
let qhatEigen (E: float[]) (At: float[,]) (Bt: float[,]) (w: float[]) (t: float) =
    let X = Array2D.init d d (fun i j -> At.[i, j] * cos ((E.[i] - E.[j]) * t))
    let Y = Array2D.init d d (fun i j -> At.[i, j] * sin ((E.[i] - E.[j]) * t))
    let aM = Array.init d (fun i -> w.[i] * At.[i, i]) |> Array.sum
    let bM = Array.init d (fun i -> w.[i] * Bt.[i, i]) |> Array.sum
    let Xc = Array2D.init d d (fun i j -> X.[i, j] - (if i = j then aM else 0.0))
    let Bc = Array2D.init d d (fun i j -> Bt.[i, j] - (if i = j then bM else 0.0))
    let wdiag (M: float[,]) = Array.init d (fun i -> w.[i] * M.[i, i]) |> Array.sum
    let cAA = wdiag (matmul Xc Xc) - wdiag (matmul Y Y)      // <Ac(t)Ac(t)>, real part
    let cBB = wdiag (matmul Bc Bc)
    let cab = wdiag (matmul Xc Bc)                            // Re <Ac(t)Bc>
    let cabIm = wdiag (matmul Y Bc)                           // Im part (0 at beta=0)
    let XBXB = wdiag (matmul (matmul (matmul Xc Bc) Xc) Bc)
    let YBYB = wdiag (matmul (matmul (matmul Y Bc) Y) Bc)
    let XBYB = wdiag (matmul (matmul (matmul Xc Bc) Y) Bc)
    let YBXB = wdiag (matmul (matmul (matmul Y Bc) Xc) Bc)
    let p = XBXB - YBYB                                       // Re <Ac(t)Bc Ac(t)Bc>
    let pIm = XBYB + YBXB
    let kappa4 = p - 2.0 * cab * cab - cAA * cBB
    let qhat = 1.0 + kappa4 / (cAA * cBB)
    (qhat, kappa4, p, cab, cAA, cBB, cabIm, pIm)

// ---------- route 2: computational-basis complex matmul (independent check) ----------
let qhatComplex (E: float[]) (V: float[,]) (w: float[]) (t: float) =
    let cV = Array2D.init d d (fun i j -> Complex(V.[i, j], 0.0))
    let cmul (A: Complex[,]) (B: Complex[,]) =
        Array2D.init d d (fun i j ->
            let mutable s = Complex.Zero
            for k in 0 .. d - 1 do s <- s + A.[i, k] * B.[k, j]
            s)
    let phase = Array2D.init d d (fun i j -> if i = j then Complex.Exp(Complex(0.0, -E.[i] * t)) else Complex.Zero)
    let cVT = Array2D.init d d (fun i j -> cV.[j, i])
    let W = cmul (cmul cV phase) cVT                          // e^{-iHt}
    let Wd = Array2D.init d d (fun i j -> Complex.Conjugate W.[j, i])
    let cA = Array2D.init d d (fun i j -> Complex(Amat.[i, j], 0.0))
    let At = cmul (cmul Wd cA) W                              // A(t) = e^{iHt} A e^{-iHt}
    // rho diagonal in EIGENBASIS with weights w -> computational basis rho = V diag(w) V^T
    let dw = Array2D.init d d (fun i j -> if i = j then Complex(w.[i], 0.0) else Complex.Zero)
    let rho = cmul (cmul cV dw) cVT
    let cB = Array2D.init d d (fun i j -> Complex(Bmat.[i, j], 0.0))
    let wtr (M: Complex[,]) =
        let P = cmul rho M
        let mutable s = Complex.Zero
        for i in 0 .. d - 1 do s <- s + P.[i, i]
        s
    let aM = (wtr At).Real
    let bM = (wtr cB).Real
    let Ac = Array2D.init d d (fun i j -> At.[i, j] - (if i = j then Complex(aM, 0.0) else Complex.Zero))
    let Bc = Array2D.init d d (fun i j -> cB.[i, j] - (if i = j then Complex(bM, 0.0) else Complex.Zero))
    let cAA = (wtr (cmul Ac Ac)).Real
    let cBB = (wtr (cmul Bc Bc)).Real
    let cab = (wtr (cmul Ac Bc)).Real
    let p = (wtr (cmul (cmul (cmul Ac Bc) Ac) Bc)).Real
    let kappa4 = p - 2.0 * cab * cab - cAA * cBB
    1.0 + kappa4 / (cAA * cBB)

// ---------- run both models ----------
let run name (h: int -> float) =
    let H = buildH h
    let (E, V) = jacobi H
    let (rEig, rOrth) = checkEig H E V
    let r = rStat E
    let VT = transpose V
    let At = matmul (matmul VT Amat) V
    let Bt = matmul (matmul VT Bmat) V
    printfn "=== %s ===" name
    printfn "eig residual %.3e   orth %.3e   rstat %.6f" rEig rOrth r
    printfn "E: %s" (E |> Array.map (sprintf "%.4f") |> String.concat " ")
    (E, V, At, Bt, r)

let (EC, VC, AtC, BtC, rC) = run "CHAOTIC" hChaotic
let (EI, VI, AtI, BtI, rI) = run "INTEGRABLE" hInteg

let wUnif = Array.create d (1.0 / float d)
let thermalW beta (E: float[]) =
    let ws = E |> Array.map (fun e -> exp (-beta * e))
    let z = Array.sum ws
    ws |> Array.map (fun x -> x / z)
let leptoW (E: float[]) =
    // spectrally leptokurtic: w ~ 1 + 3*((E-mean)/sd)^2  (mirror of ex 29's digit reweight)
    let m = Array.average E
    let sd = sqrt (E |> Array.averageBy (fun e -> (e - m) * (e - m)))
    let ws = E |> Array.map (fun e -> 1.0 + 3.0 * ((e - m) / sd) ** 2.0)
    let z = Array.sum ws
    ws |> Array.map (fun x -> x / z)

let times = [| 0.0; 0.5; 1.0; 1.5; 2.0; 3.0; 4.0; 5.0; 6.0; 8.0; 10.0; 12.0; 15.0; 20.0; 25.0; 30.7; 40.0 |]

printfn ""
printfn "%-6s | %-22s | %-22s | %-22s | %-22s" "t" "chaotic b=0" "chaotic thermal .3" "chaotic lepto" "integrable b=0"
for t in times do
    let (q0, _, _, _, _, _, _, _) = qhatEigen EC AtC BtC wUnif t
    let (qT, _, _, _, _, _, _, _) = qhatEigen EC AtC BtC (thermalW 0.3 EC) t
    let (qL, _, _, _, _, _, _, _) = qhatEigen EC AtC BtC (leptoW EC) t
    let (qI, _, _, _, _, _, _, _) = qhatEigen EI AtI BtI wUnif t
    // cross-route check on the chaotic uniform + thermal points
    let q0x = qhatComplex EC VC wUnif t
    let qTx = qhatComplex EC VC (thermalW 0.3 EC) t
    let qIx = qhatComplex EI VI wUnif t
    printfn "%-6.2f | %+.12f (%0.1e) | %+.12f (%0.1e) | %+.12f | %+.12f (%0.1e)" t q0 (abs (q0 - q0x)) qT (abs (qT - qTx)) qL qI (abs (qI - qIx))

// late-time averages + variances over an incommensurate comb
let lateTimes = [| 20.0; 23.7; 27.9; 31.3; 35.1; 38.6 |]
let stats f =
    let vs = lateTimes |> Array.map f
    let m = Array.average vs
    let v = vs |> Array.averageBy (fun x -> (x - m) * (x - m))
    (m, v, vs)
let q1 (E, At, Bt, w) t = let (q, _, _, _, _, _, _, _) = qhatEigen E At Bt w t in q
let (lateC, varC, vsC) = stats (q1 (EC, AtC, BtC, wUnif))
let (lateCT, varCT, vsCT) = stats (q1 (EC, AtC, BtC, thermalW 0.3 EC))
let (lateCL, varCL, vsCL) = stats (q1 (EC, AtC, BtC, leptoW EC))
let (lateI, varI, vsI) = stats (q1 (EI, AtI, BtI, wUnif))
printfn ""
printfn "comb (6 incommensurate t in [20,39]): mean | variance | values"
printfn "  chaotic b0 %.12f | %.6e | %s" lateC varC (vsC |> Array.map (sprintf "%+.4f") |> String.concat " ")
printfn "  thermal    %.12f | %.6e | %s" lateCT varCT (vsCT |> Array.map (sprintf "%+.4f") |> String.concat " ")
printfn "  lepto      %.12f | %.6e | %s" lateCL varCL (vsCL |> Array.map (sprintf "%+.4f") |> String.concat " ")
printfn "  integrable %.12f | %.6e | %s" lateI varI (vsI |> Array.map (sprintf "%+.4f") |> String.concat " ")
printfn "floor scales: 1/d = %.4f, 1/d^2 = %.6f" (1.0 / float d) (1.0 / float d ** 2.0)

// ---------- emit blade literals ----------
let sb = StringBuilder()
let emitVec (name: string) (v: float[]) =
    sb.AppendLine(sprintf "let %s: Array<Float64 like Idx<%d>> = [" name d) |> ignore
    sb.AppendLine("  " + (v |> Array.map (sprintf "%.14f") |> String.concat ", ")) |> ignore
    sb.AppendLine("]") |> ignore
let emitMat (name: string) (M: float[,]) =
    sb.AppendLine(sprintf "let %s: Array<Float64 like Idx<%d>, Idx<%d>> = [" name d d) |> ignore
    for i in 0 .. d - 1 do
        let row = [| for j in 0 .. d - 1 -> sprintf "%.14f" M.[i, j] |] |> String.concat ", "
        sb.AppendLine(sprintf "  [%s]%s" row (if i < d - 1 then "," else "")) |> ignore
    sb.AppendLine("]") |> ignore
emitVec "EC" EC
emitMat "VC" VC
emitVec "EI" EI
emitMat "VI" VI
IO.File.WriteAllText(IO.Path.Combine(__SOURCE_DIRECTORY__, "qchain_literals.blade.txt"), sb.ToString())
printfn ""
printfn "literals written to qchain_literals.blade.txt"
printfn "rstat chaotic %.6f  integrable %.6f" rC rI
