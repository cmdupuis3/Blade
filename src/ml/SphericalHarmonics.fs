namespace BladeML

/// Real solid spherical harmonics (ml-spec section 6).
///
/// For each degree l these are homogeneous polynomials of degree l in
/// (x, y, z); restricted to the unit sphere they are the orthonormal real
/// spherical harmonics. Component order within degree l is m = -l .. l
/// (0-based c = m + l), matching the ml-spec's explicit tables:
///   l=1: (y, z, x) * 0.48860251,  l=2: (xy, yz, 3z^2-r^2, xz, x^2-y^2) ...
///
/// Evaluation is the sin-free associated-Legendre recurrence over
/// P~_l^m(z, r^2) (homogenized, no sqrt(1-t^2) factors) combined with
/// A_m + i B_m = (x + i y)^m, which is exact for solid harmonics and has no
/// coordinate singularities. This is the "explicit low-L polynomials,
/// recurrence above" strategy the module doc prescribes, in closed form for
/// all L. Y_to<L_max> is the only L-raising primitive of the library.
module SphericalHarmonics =

    open MathUtils

    /// All degrees 0..lmax at once. Result.[l] has length 2l+1.
    let evalUpTo (lmax: int) (x: float) (y: float) (z: float) : float[][] =
        if lmax < 0 then invalidArg "lmax" "lmax must be >= 0"
        let r2 = x * x + y * y + z * z
        // A.[m] + i B.[m] = (x + i y)^m
        let a = Array.zeroCreate (lmax + 1)
        let b = Array.zeroCreate (lmax + 1)
        a.[0] <- 1.0
        for m in 1 .. lmax do
            a.[m] <- x * a.[m - 1] - y * b.[m - 1]
            b.[m] <- x * b.[m - 1] + y * a.[m - 1]
        // p.[l].[m]: homogenized sin-free associated Legendre.
        //   p_m^m     = (2m-1)!!
        //   p_{m+1}^m = (2m+1) z p_m^m
        //   p_l^m     = ((2l-1) z p_{l-1}^m - (l+m-1) r^2 p_{l-2}^m) / (l-m)
        let p = Array.init (lmax + 1) (fun l -> Array.zeroCreate (l + 1))
        p.[0].[0] <- 1.0
        for m in 0 .. lmax - 1 do
            p.[m + 1].[m + 1] <- float (2 * m + 1) * p.[m].[m]
            p.[m + 1].[m] <- float (2 * m + 1) * z * p.[m].[m]
        for m in 0 .. lmax do
            for l in m + 2 .. lmax do
                p.[l].[m] <-
                    (float (2 * l - 1) * z * p.[l - 1].[m]
                     - float (l + m - 1) * r2 * p.[l - 2].[m]) / float (l - m)
        let fourPi = 4.0 * System.Math.PI
        Array.init (lmax + 1) (fun l ->
            let out = Array.zeroCreate (2 * l + 1)
            out.[l] <- sqrt (float (2 * l + 1) / fourPi) * p.[l].[0]
            for m in 1 .. l do
                let n =
                    sqrt (float (2 * l + 1) / (2.0 * System.Math.PI)
                          * factorial.[l - m] / factorial.[l + m])
                out.[l + m] <- n * p.[l].[m] * a.[m]   // cosine-type, +m
                out.[l - m] <- n * p.[l].[m] * b.[m]   // sine-type,   -m
            out)

    /// Single degree L: Y<L>(v) as a (2L+1)-vector.
    let eval (l: int) (x: float) (y: float) (z: float) : float[] =
        (evalUpTo l x y z).[l]

    /// Y_to<L_max>(v): all degrees concatenated, laid out exactly as
    /// IrrepsIdx<sh_spec<L_max>> (block = degree, mult = 1, m ascending).
    let yTo (lmax: int) (x: float) (y: float) (z: float) : float[] =
        evalUpTo lmax x y z |> Array.concat
