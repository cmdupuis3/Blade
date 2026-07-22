namespace MomentAlgebra

/// Differential-oracle support (v7 spirit): seeded samplers for distributions
/// with known cumulants, plus a two-pass reference for central comoment sums.
/// The headline test: cumulants ESTIMATED by the streaming accumulator from
/// samples must agree with cumulants PROPAGATED algebraically by Dist.
module Oracle =

    let sampleGaussian (rng: System.Random) : float =
        let u1 = 1.0 - rng.NextDouble()
        let u2 = rng.NextDouble()
        sqrt (-2.0 * log u1) * cos (2.0 * System.Math.PI * u2)

    let sampleExponential (rate: float) (rng: System.Random) : float =
        -log (1.0 - rng.NextDouble()) / rate

    /// Integer shape only (Erlang): sum of `shape` exponentials.
    let sampleGamma (shape: int) (rate: float) (rng: System.Random) : float =
        let mutable acc = 0.0
        for _ in 1 .. shape do acc <- acc + sampleExponential rate rng
        acc

    /// Knuth's method (fine for the small lambdas used here).
    let samplePoisson (lam: float) (rng: System.Random) : float =
        let l = exp (-lam)
        let mutable k = 0
        let mutable p = 1.0
        while p > l do
            k <- k + 1
            p <- p * rng.NextDouble()
        float (k - 1)

    /// Two-pass reference: exact mean, then central comoment sums accumulated
    /// directly. Ground truth for validating the derived streaming kernel.
    let twoPassCentral (data: float[][]) (r: int) : Streaming.Acc =
        let n = data.Length
        let d = data.[0].Length
        let mean = Array.zeroCreate d
        for x in data do
            for i in 0 .. d - 1 do mean.[i] <- mean.[i] + x.[i]
        for i in 0 .. d - 1 do mean.[i] <- mean.[i] / float n
        let tensors =
            [| for p in 2 .. r ->
                 let t = SymTensor.create d p
                 let labels = SymTensor.labelTable d p
                 for e in 0 .. labels.Length - 1 do
                     let lbl = labels.[e]
                     let mutable acc = 0.0
                     for x in data do
                         let mutable prod = 1.0
                         for l in lbl do prod <- prod * (x.[l] - mean.[l])
                         acc <- acc + prod
                     t.Data.[e] <- acc
                 t |]
        { Dim = d; Order = r; N = float n; Mean = mean; M = tensors }
