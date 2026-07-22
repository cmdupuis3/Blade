namespace BladeML

/// Small numeric utilities shared by the ML reference implementation.
/// Everything here is deliberately dependency-free: plain doubles, jagged
/// arrays, Gaussian elimination. Angular momenta stay small (l <= ~6) so
/// double-precision factorial ratios are accurate to ~1e-12.
module MathUtils =

    /// factorial.[n] = n! as float, for n in 0..80.
    let factorial : float[] =
        let a = Array.zeroCreate 81
        a.[0] <- 1.0
        for i in 1 .. 80 do
            a.[i] <- a.[i - 1] * float i
        a

    /// (-1)^n, robust for negative n (.NET % keeps sign).
    let paritySign (n: int) : float =
        if ((n % 2) + 2) % 2 = 0 then 1.0 else -1.0

    let transpose (a: float[][]) : float[][] =
        let n = a.Length
        let m = a.[0].Length
        Array.init m (fun j -> Array.init n (fun i -> a.[i].[j]))

    let matVec (a: float[][]) (v: float[]) : float[] =
        a |> Array.map (fun row ->
            let mutable acc = 0.0
            for j in 0 .. v.Length - 1 do
                acc <- acc + row.[j] * v.[j]
            acc)

    let matMul (a: float[][]) (b: float[][]) : float[][] =
        let n = a.Length
        let k = b.Length
        let m = b.[0].Length
        Array.init n (fun i ->
            Array.init m (fun j ->
                let mutable acc = 0.0
                for t in 0 .. k - 1 do
                    acc <- acc + a.[i].[t] * b.[t].[j]
                acc))

    /// Solve A X = B (A: n x n, B: n x k) by Gaussian elimination with
    /// partial pivoting. Inputs are not mutated. Raises on singular A.
    let solve (a: float[][]) (b: float[][]) : float[][] =
        let n = a.Length
        let k = b.[0].Length
        let m = Array.init n (fun i -> Array.append (Array.copy a.[i]) (Array.copy b.[i]))
        for col in 0 .. n - 1 do
            let mutable piv = col
            for r in col + 1 .. n - 1 do
                if abs m.[r].[col] > abs m.[piv].[col] then piv <- r
            if abs m.[piv].[col] < 1e-12 then
                failwithf "solve: (near-)singular matrix at column %d" col
            if piv <> col then
                let t = m.[piv]
                m.[piv] <- m.[col]
                m.[col] <- t
            let d = m.[col].[col]
            for j in col .. n + k - 1 do
                m.[col].[j] <- m.[col].[j] / d
            for r in 0 .. n - 1 do
                if r <> col && m.[r].[col] <> 0.0 then
                    let f = m.[r].[col]
                    for j in col .. n + k - 1 do
                        m.[r].[j] <- m.[r].[j] - f * m.[col].[j]
        Array.init n (fun i -> Array.sub m.[i] n k)

    let maxAbsDiff (a: float[]) (b: float[]) : float =
        if a.Length <> b.Length then
            failwithf "maxAbsDiff: length mismatch %d vs %d" a.Length b.Length
        let mutable acc = 0.0
        for i in 0 .. a.Length - 1 do
            acc <- max acc (abs (a.[i] - b.[i]))
        acc

    let maxAbs (a: float[]) : float =
        let mutable acc = 0.0
        for x in a do
            acc <- max acc (abs x)
        acc
