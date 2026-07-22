/// Reference implementation for the math-module general (non-symmetric)
/// eigensolver — the VALUE ORACLE for the generated `m.eig` kernel.
/// Classic real-Schur route: Householder Hessenberg reduction + Francis
/// double-shift QR, eigenvalues extracted from the final quasi-triangular
/// form (1×1 blocks = real, 2×2 blocks = complex pairs via the quadratic).
///
/// Deliberately structured the way the GENERATED code must be (no
/// break/early-exit; a fixed iteration budget with a shrinking window
/// [0, hi); conditional work expressed as empty loop ranges and identity
/// rotations), so the transliteration is operation-for-operation and
/// values agree to the ulp.
///
/// Known limitation (documented): no exceptional shifts, so adversarial
/// cyclic-permutation-like spectra (exact roots of unity with equal
/// moduli and zero Wilkinson shifts) can stall within the budget. Generic
/// data-derived matrices (the Koopman/EDMD use case) converge normally.
module BladeMath.Eig

let private eps = 1.0e-12
let private tiny = 1.0e-300

/// In-place Householder reduction to upper Hessenberg (no balancing);
/// entries below the first subdiagonal are zeroed exactly.
let hessenberg (n: int) (h: float[,]) : unit =
    for k in 0 .. n - 3 do
        let mutable nrm2 = 0.0
        for i in k + 1 .. n - 1 do nrm2 <- nrm2 + h.[i, k] * h.[i, k]
        let nrm = sqrt nrm2
        if nrm > tiny then
            let alpha = if h.[k + 1, k] >= 0.0 then -nrm else nrm
            let v = Array.zeroCreate n
            v.[k + 1] <- h.[k + 1, k] - alpha
            for i in k + 2 .. n - 1 do v.[i] <- h.[i, k]
            let mutable vv = 0.0
            for i in k + 1 .. n - 1 do vv <- vv + v.[i] * v.[i]
            let beta = if vv > tiny then 2.0 / vv else 0.0
            // H <- (I - beta v vT) H
            for j in 0 .. n - 1 do
                let mutable s = 0.0
                for i in k + 1 .. n - 1 do s <- s + v.[i] * h.[i, j]
                for i in k + 1 .. n - 1 do h.[i, j] <- h.[i, j] - beta * s * v.[i]
            // H <- H (I - beta v vT)
            for i in 0 .. n - 1 do
                let mutable s = 0.0
                for j in k + 1 .. n - 1 do s <- s + v.[j] * h.[i, j]
                for j in k + 1 .. n - 1 do h.[i, j] <- h.[i, j] - beta * s * v.[j]
            // exact zeros below the subdiagonal in column k
            h.[k + 1, k] <- alpha
            for i in k + 2 .. n - 1 do h.[i, k] <- 0.0

let private subdiagSmall (h: float[,]) (k: int) : bool =
    abs h.[k, k - 1] <= eps * (abs h.[k - 1, k - 1] + abs h.[k, k]) + tiny

/// Fixed-budget windowed Francis double-shift QR on a Hessenberg matrix.
/// The active window is [0, hi); each iteration finds the window start l
/// (largest small subdiagonal below hi), freezes windows of size <= 2
/// (extraction solves 2×2 blocks exactly), else runs one double-shift
/// bulge chase over [l, hi).
let francis (n: int) (maxIter: int) (h: float[,]) : unit =
    let mutable hi = n
    for _iter in 1 .. maxIter do
        if hi >= 2 then
            let mutable l = 0
            for k in 1 .. hi - 1 do
                if subdiagSmall h k then l <- k
            if hi - l <= 2 then
                hi <- l
            else
                let e = hi - 1
                let s = h.[e - 1, e - 1] + h.[e, e]
                let t = h.[e - 1, e - 1] * h.[e, e] - h.[e - 1, e] * h.[e, e - 1]
                // first column of (H - aI)(H - bI) e1 on the window
                let mutable x = h.[l, l] * h.[l, l] + h.[l, l + 1] * h.[l + 1, l] - s * h.[l, l] + t
                let mutable y = h.[l + 1, l] * (h.[l, l] + h.[l + 1, l + 1] - s)
                let mutable z = h.[l + 2, l + 1] * h.[l + 1, l]
                for k in l .. e - 2 do
                    let nrm = sqrt (x * x + y * y + z * z)
                    if nrm > tiny then
                        let alpha = if x >= 0.0 then -nrm else nrm
                        let v0 = x - alpha
                        let v1 = y
                        let v2 = z
                        let vv = v0 * v0 + v1 * v1 + v2 * v2
                        let beta = if vv > tiny then 2.0 / vv else 0.0
                        // rows k..k+2 from the left
                        for j in 0 .. n - 1 do
                            let sm = v0 * h.[k, j] + v1 * h.[k + 1, j] + v2 * h.[k + 2, j]
                            h.[k, j] <- h.[k, j] - beta * sm * v0
                            h.[k + 1, j] <- h.[k + 1, j] - beta * sm * v1
                            h.[k + 2, j] <- h.[k + 2, j] - beta * sm * v2
                        // cols k..k+2 from the right
                        for i in 0 .. n - 1 do
                            let sm = v0 * h.[i, k] + v1 * h.[i, k + 1] + v2 * h.[i, k + 2]
                            h.[i, k] <- h.[i, k] - beta * sm * v0
                            h.[i, k + 1] <- h.[i, k + 1] - beta * sm * v1
                            h.[i, k + 2] <- h.[i, k + 2] - beta * sm * v2
                        // restore exact Hessenberg zeros behind the bulge
                        if k > l then
                            h.[k + 1, k - 1] <- 0.0
                            h.[k + 2, k - 1] <- 0.0
                    x <- h.[k + 1, k]
                    y <- h.[k + 2, k]
                    z <- if k < e - 2 then h.[k + 3, k] else 0.0
                // final Givens on (x, y) over rows/cols e-1, e
                let nrm = sqrt (x * x + y * y)
                if nrm > tiny then
                    let cs = x / nrm
                    let sn = y / nrm
                    for j in 0 .. n - 1 do
                        let tp = h.[e - 1, j]
                        let tq = h.[e, j]
                        h.[e - 1, j] <- cs * tp + sn * tq
                        h.[e, j] <- -sn * tp + cs * tq
                    for i in 0 .. n - 1 do
                        let tp = h.[i, e - 1]
                        let tq = h.[i, e]
                        h.[i, e - 1] <- cs * tp + sn * tq
                        h.[i, e] <- -sn * tp + cs * tq
                    h.[e, e - 2] <- 0.0

/// Eigenvalues from the quasi-triangular form: 1×1 blocks are real, 2×2
/// blocks solve the quadratic (complex pair emitted +im first). Sorted by
/// DESCENDING modulus (selection, ties keep original order — conjugate
/// pairs stay adjacent).
let extract (n: int) (h: float[,]) : float[] * float[] =
    let lre = Array.zeroCreate n
    let lim = Array.zeroCreate n
    let mutable skip = false
    for k in 0 .. n - 1 do
        if skip then skip <- false
        else
            let isLast = k = n - 1
            let isReal = isLast || subdiagSmall h (k + 1)
            if isReal then
                lre.[k] <- h.[k, k]
                lim.[k] <- 0.0
            else
                let a = h.[k, k]
                let b = h.[k, k + 1]
                let c = h.[k + 1, k]
                let d = h.[k + 1, k + 1]
                let p = 0.5 * (a + d)
                let disc = 0.25 * (a - d) * (a - d) + b * c
                if disc >= 0.0 then
                    let rt = sqrt disc
                    lre.[k] <- p + rt
                    lre.[k + 1] <- p - rt
                    lim.[k] <- 0.0
                    lim.[k + 1] <- 0.0
                else
                    let im = sqrt (-disc)
                    lre.[k] <- p
                    lre.[k + 1] <- p
                    lim.[k] <- im
                    lim.[k + 1] <- -im
                skip <- true
    // selection sort by modulus^2 descending
    for kk in 0 .. n - 1 do
        let mutable best = kk
        for j in kk + 1 .. n - 1 do
            if lre.[j] * lre.[j] + lim.[j] * lim.[j] > lre.[best] * lre.[best] + lim.[best] * lim.[best] then
                best <- j
        let tr = lre.[kk] in lre.[kk] <- lre.[best]; lre.[best] <- tr
        let ti = lim.[kk] in lim.[kk] <- lim.[best]; lim.[best] <- ti
    (lre, lim)

/// General real eigenvalues: (LRE, LIM), descending modulus, conjugate
/// pairs adjacent with +im first. maxIter is the total Francis budget
/// (the generated default is 30·n).
let eig (maxIter: int) (a: float[,]) : float[] * float[] =
    let n = Array2D.length1 a
    if Array2D.length2 a <> n then failwith "eig oracle: square input required"
    let h = Array2D.init n n (fun i j -> a.[i, j])
    hessenberg n h
    francis n maxIter h
    extract n h
