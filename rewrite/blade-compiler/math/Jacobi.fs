/// Reference implementations for the math-module Jacobi kernels — the
/// VALUE ORACLE for the generated Blade code (math/compiler/MathDecls.fs).
/// Same sweep schedule (cyclic p<q, fixed budget), same convergence and
/// zero guards, same sort and sign conventions, so values agree to the ulp.
/// This project is a SEPARATE fsproj; the compiler never builds it.
module BladeMath.Jacobi

/// Thin SVD, m >= n: returns (U m×n, S length-n descending, V n×n) with
/// A ≈ U·diag(S)·Vᵀ, i.e. A[i,j] = Σ_k U[i,k]·S[k]·V[j,k].
let svd (sweeps: int) (a: float[,]) : float[,] * float[] * float[,] =
    let m = Array2D.length1 a
    let n = Array2D.length2 a
    if m < n then failwithf "svd oracle: m < n (%d×%d)" m n
    let w = Array2D.init m n (fun i j -> a.[i, j])
    let vv = Array2D.init n n (fun i j -> if i = j then 1.0 else 0.0)
    for _sweep in 1 .. sweeps do
        for p in 0 .. n - 2 do
            for q in p + 1 .. n - 1 do
                let mutable aa = 0.0
                let mutable bb = 0.0
                let mutable cc = 0.0
                for i in 0 .. m - 1 do
                    let wp = w.[i, p]
                    let wq = w.[i, q]
                    aa <- aa + wp * wp
                    bb <- bb + wq * wq
                    cc <- cc + wp * wq
                let conv = abs cc <= 1.0e-15 * sqrt (aa * bb)
                let zeta = (bb - aa) / (if conv then 1.0 else 2.0 * cc)
                let tt = (if zeta >= 0.0 then 1.0 else -1.0) / (abs zeta + sqrt (1.0 + zeta * zeta))
                let cs = if conv then 1.0 else 1.0 / sqrt (1.0 + tt * tt)
                let sn = if conv then 0.0 else cs * tt
                for i in 0 .. m - 1 do
                    let tp = w.[i, p]
                    let tq = w.[i, q]
                    w.[i, p] <- cs * tp - sn * tq
                    w.[i, q] <- sn * tp + cs * tq
                for i in 0 .. n - 1 do
                    let tp = vv.[i, p]
                    let tq = vv.[i, q]
                    vv.[i, p] <- cs * tp - sn * tq
                    vv.[i, q] <- sn * tp + cs * tq
    // singular values = column norms
    let s = Array.init n (fun j ->
        let mutable acc = 0.0
        for i in 0 .. m - 1 do acc <- acc + w.[i, j] * w.[i, j]
        sqrt acc)
    // selection sort descending (ties keep original order)
    for kk in 0 .. n - 1 do
        let mutable best = kk
        for j in kk + 1 .. n - 1 do
            if s.[j] > s.[best] then best <- j
        let ts = s.[kk] in s.[kk] <- s.[best]; s.[best] <- ts
        for i in 0 .. m - 1 do
            let tw = w.[i, kk] in w.[i, kk] <- w.[i, best]; w.[i, best] <- tw
        for i in 0 .. n - 1 do
            let tv = vv.[i, kk] in vv.[i, kk] <- vv.[i, best]; vv.[i, best] <- tv
    // U = normalized columns (zero σ -> zero column)
    let u = Array2D.zeroCreate m n
    for j in 0 .. n - 1 do
        let inv = if s.[j] > 1.0e-300 then 1.0 / s.[j] else 0.0
        for i in 0 .. m - 1 do u.[i, j] <- w.[i, j] * inv
    // sign fix: first row attaining max |entry| per U column made positive
    for j in 0 .. n - 1 do
        let mutable bigv = 0.0
        let mutable best = 0
        for i in 0 .. m - 1 do
            let mag = abs u.[i, j]
            if mag > bigv then
                best <- i
                bigv <- mag
        let flip = if u.[best, j] < 0.0 then -1.0 else 1.0
        for i in 0 .. m - 1 do u.[i, j] <- u.[i, j] * flip
        for i in 0 .. n - 1 do vv.[i, j] <- vv.[i, j] * flip
    (u, s, vv)

/// Symmetric eigendecomposition via cyclic two-sided Jacobi: returns
/// (Q n×n, LAM length-n descending) with S ≈ Q·diag(LAM)·Qᵀ. Same
/// conventions as the generated eigh (phase 2).
let eigh (sweeps: int) (s0: float[,]) : float[,] * float[] =
    let n = Array2D.length1 s0
    let aw = Array2D.init n n (fun i j -> s0.[i, j])
    let q = Array2D.init n n (fun i j -> if i = j then 1.0 else 0.0)
    for _sweep in 1 .. sweeps do
        for p in 0 .. n - 2 do
            for r in p + 1 .. n - 1 do
                let apq = aw.[p, r]
                let app = aw.[p, p]
                let aqq = aw.[r, r]
                let conv = abs apq <= 1.0e-15 * sqrt (abs app * abs aqq + 1.0e-300)
                let theta = (aqq - app) / (if conv then 1.0 else 2.0 * apq)
                let tt = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (1.0 + theta * theta))
                let cs = if conv then 1.0 else 1.0 / sqrt (1.0 + tt * tt)
                let sn = if conv then 0.0 else cs * tt
                // A <- Rᵀ A R on the (p, r) plane, columns transform like V
                for i in 0 .. n - 1 do
                    let tp = aw.[i, p]
                    let tq = aw.[i, r]
                    aw.[i, p] <- cs * tp - sn * tq
                    aw.[i, r] <- sn * tp + cs * tq
                for i in 0 .. n - 1 do
                    let tp = aw.[p, i]
                    let tq = aw.[r, i]
                    aw.[p, i] <- cs * tp - sn * tq
                    aw.[r, i] <- sn * tp + cs * tq
                for i in 0 .. n - 1 do
                    let tp = q.[i, p]
                    let tq = q.[i, r]
                    q.[i, p] <- cs * tp - sn * tq
                    q.[i, r] <- sn * tp + cs * tq
    let lam = Array.init n (fun j -> aw.[j, j])
    // selection sort descending + column swaps
    for kk in 0 .. n - 1 do
        let mutable best = kk
        for j in kk + 1 .. n - 1 do
            if lam.[j] > lam.[best] then best <- j
        let tl = lam.[kk] in lam.[kk] <- lam.[best]; lam.[best] <- tl
        for i in 0 .. n - 1 do
            let tq = q.[i, kk] in q.[i, kk] <- q.[i, best]; q.[i, best] <- tq
    // sign fix: first row attaining max |entry| per column made positive
    for j in 0 .. n - 1 do
        let mutable bigv = 0.0
        let mutable best = 0
        for i in 0 .. n - 1 do
            let mag = abs q.[i, j]
            if mag > bigv then
                best <- i
                bigv <- mag
        let flip = if q.[best, j] < 0.0 then -1.0 else 1.0
        for i in 0 .. n - 1 do q.[i, j] <- q.[i, j] * flip
    (q, lam)
