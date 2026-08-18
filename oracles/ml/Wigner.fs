namespace BladeML

open System.Collections.Generic
open System.Numerics

/// One nonzero entry of a real-basis Clebsch-Gordan coupling tensor.
/// C1/C2/C3 are 0-based m-components (signed m = c - l); Coef is the
/// coupling coefficient: out[C3] += Coef * in1[C1] * in2[C2].
type CGEntry = { C1: int; C2: int; C3: int; Coef: float }

/// Wigner 3j symbols and Clebsch-Gordan coefficients.
///
/// Two bases matter here and the distinction is a real finding about the
/// ml-spec (see ml/README.md):
///   - COMPLEX basis (standard Condon-Shortley Y_l^m): CG selection rule is
///     m1 + m2 = m3. This is the rule the ml-spec writes into CGIndex.
///   - REAL basis (real spherical harmonics, which the ml-spec's section 6
///     uses for its explicit Y formulas and which e3nn uses): the support is
///     NOT m1 + m2 = m3; it is |m3| in { ||m1|-|m2||, |m1|+|m2| } with a
///     sign-parity constraint.
/// The tensor product implementation below therefore computes the real-basis
/// coupling tensor by unitary change of basis from the complex CG, fixes the
/// global phase to make it real, and iterates its true sparse support.
module Wigner =

    open MathUtils

    /// Wigner 3j symbol (j1 j2 j3; m1 m2 m3), integer angular momenta,
    /// via the Racah formula. Returns 0.0 outside the selection rules.
    let wigner3j (j1: int) (j2: int) (j3: int) (m1: int) (m2: int) (m3: int) : float =
        if m1 + m2 + m3 <> 0 then 0.0
        elif j3 < abs (j1 - j2) || j3 > j1 + j2 then 0.0
        elif abs m1 > j1 || abs m2 > j2 || abs m3 > j3 then 0.0
        else
            let delta =
                sqrt (factorial.[j1 + j2 - j3] * factorial.[j1 - j2 + j3]
                      * factorial.[-j1 + j2 + j3] / factorial.[j1 + j2 + j3 + 1])
            let pre =
                sqrt (factorial.[j1 + m1] * factorial.[j1 - m1]
                      * factorial.[j2 + m2] * factorial.[j2 - m2]
                      * factorial.[j3 + m3] * factorial.[j3 - m3])
            let kmin = max 0 (max (j2 - j3 - m1) (j1 - j3 + m2))
            let kmax = min (j1 + j2 - j3) (min (j1 - m1) (j2 + m2))
            let mutable s = 0.0
            for k in kmin .. kmax do
                let denom =
                    factorial.[k] * factorial.[j1 + j2 - j3 - k]
                    * factorial.[j1 - m1 - k] * factorial.[j2 + m2 - k]
                    * factorial.[j3 - j2 + m1 + k] * factorial.[j3 - j1 - m2 + k]
                s <- s + paritySign k / denom
            paritySign (j1 - j2 - m3) * delta * pre * s

    /// Clebsch-Gordan coefficient <j1 m1; j2 m2 | j3 m3> in the complex
    /// (Condon-Shortley) basis. Selection rule: m1 + m2 = m3.
    let clebsch (j1: int) (m1: int) (j2: int) (m2: int) (j3: int) (m3: int) : float =
        if m1 + m2 <> m3 then 0.0
        else
            paritySign (j1 - j2 + m3) * sqrt (float (2 * j3 + 1))
            * wigner3j j1 j2 j3 m1 m2 (-m3)

    /// Unitary change of basis U_l from complex to real spherical harmonics:
    /// Y^real = U Y^complex. Rows indexed by real m (0-based c = m + l),
    /// columns by complex mu (0-based mu + l). Convention (no Condon-Shortley
    /// phase on the real side, matching the ml-spec's explicit Y formulas):
    ///   m > 0: Y^r_{l,m}  = (Y_l^{-m} + (-1)^m Y_l^{m}) / sqrt(2)
    ///   m = 0: Y^r_{l,0}  = Y_l^0
    ///   m < 0: Y^r_{l,m}  = i (Y_l^{-|m|} - (-1)^{|m|} Y_l^{|m|}) / sqrt(2)
    let private uMatrix (l: int) : Complex[][] =
        let d = 2 * l + 1
        let u = Array.init d (fun _ -> Array.zeroCreate<Complex> d)
        let s = 1.0 / sqrt 2.0
        u.[l].[l] <- Complex.One
        for m in 1 .. l do
            let ph = paritySign m
            // row for +m
            u.[l + m].[l - m] <- Complex(s, 0.0)
            u.[l + m].[l + m] <- Complex(ph * s, 0.0)
            // row for -m
            u.[l - m].[l - m] <- Complex(0.0, s)
            u.[l - m].[l + m] <- Complex(0.0, -ph * s)
        u

    let private denseCache = Dictionary<int * int * int, float[][][]>()
    let private sparseCache = Dictionary<int * int * int, CGEntry[]>()

    /// Real-basis coupling tensor C with out[c3] += C[c1][c2][c3] x[c1] y[c2]
    /// for real-SH component vectors x (degree l1) and y (degree l2), where
    /// the output transforms as the real degree-l3 representation.
    ///
    /// Computed as U3 . CG . (U1^dagger x U2^dagger). The result is purely
    /// real when l1+l2+l3 is even and purely imaginary when odd; in the odd
    /// case we multiply by -i (a global phase, harmless for SO(3)
    /// equivariance) to obtain a real tensor, and assert realness.
    let realCGDense (l1: int) (l2: int) (l3: int) : float[][][] =
        match denseCache.TryGetValue((l1, l2, l3)) with
        | true, v -> v
        | _ ->
            let d1, d2, d3 = 2 * l1 + 1, 2 * l2 + 1, 2 * l3 + 1
            let t = Array.init d1 (fun _ -> Array.init d2 (fun _ -> Array.zeroCreate<Complex> d3))
            if l3 >= abs (l1 - l2) && l3 <= l1 + l2 then
                let u1 = uMatrix l1
                let u2 = uMatrix l2
                let u3 = uMatrix l3
                for c1 in 0 .. d1 - 1 do
                    for mu1 in -l1 .. l1 do
                        let a = Complex.Conjugate u1.[c1].[mu1 + l1]
                        if a <> Complex.Zero then
                            for c2 in 0 .. d2 - 1 do
                                for mu2 in -l2 .. l2 do
                                    let b = Complex.Conjugate u2.[c2].[mu2 + l2]
                                    if b <> Complex.Zero then
                                        let mu3 = mu1 + mu2
                                        if abs mu3 <= l3 then
                                            let cgv = clebsch l1 mu1 l2 mu2 l3 mu3
                                            if cgv <> 0.0 then
                                                let ab = a * b * Complex(cgv, 0.0)
                                                for c3 in 0 .. d3 - 1 do
                                                    let w = u3.[c3].[mu3 + l3]
                                                    if w <> Complex.Zero then
                                                        t.[c1].[c2].[c3] <- t.[c1].[c2].[c3] + ab * w
            // Global phase fix: purely-imaginary tensors become real via *(-i).
            let mutable maxRe = 0.0
            let mutable maxIm = 0.0
            for c1 in 0 .. d1 - 1 do
                for c2 in 0 .. d2 - 1 do
                    for c3 in 0 .. d3 - 1 do
                        maxRe <- max maxRe (abs t.[c1].[c2].[c3].Real)
                        maxIm <- max maxIm (abs t.[c1].[c2].[c3].Imaginary)
            let flip = maxIm > maxRe
            let phase = if flip then Complex(0.0, -1.0) else Complex.One
            let mutable residual = 0.0
            let res =
                Array.init d1 (fun c1 ->
                    Array.init d2 (fun c2 ->
                        Array.init d3 (fun c3 ->
                            let v = t.[c1].[c2].[c3] * phase
                            residual <- max residual (abs v.Imaginary)
                            v.Real)))
            if residual > 1e-10 * max 1.0 (max maxRe maxIm) then
                failwithf "realCGDense(%d,%d,%d): tensor not real after phase fix (residual %g)"
                    l1 l2 l3 residual
            denseCache.[(l1, l2, l3)] <- res
            res

    /// Sparse nonzero support of the real coupling tensor, in lexicographic
    /// (c1, c2, c3) order. This is what the CGIndex iteration of the ml-spec
    /// should enumerate (in the real basis).
    let realCGSparse (l1: int) (l2: int) (l3: int) : CGEntry[] =
        match sparseCache.TryGetValue((l1, l2, l3)) with
        | true, v -> v
        | _ ->
            let dense = realCGDense l1 l2 l3
            let entries =
                [| for c1 in 0 .. dense.Length - 1 do
                     for c2 in 0 .. dense.[c1].Length - 1 do
                       for c3 in 0 .. dense.[c1].[c2].Length - 1 do
                         let v = dense.[c1].[c2].[c3]
                         if abs v > 1e-12 then
                           yield { C1 = c1; C2 = c2; C3 = c3; Coef = v } |]
            sparseCache.[(l1, l2, l3)] <- entries
            entries
