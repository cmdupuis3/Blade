namespace MomentAlgebra

/// Prototype 5: closed-form log-densities for the named families, the
/// Edgeworth/Gram-Charlier density and Cornish-Fisher quantile expansions
/// built from a univariate cumulant tower, and conjugate posterior updates.
///
/// ORACLE CODE. Everything here deliberately duplicates algebra the compiler
/// layer (src/ppl/compiler/PplElaborate.fs) will emit independently -- that
/// duplication is the whole point, since this exe generates the EXPECT pins
/// the compiler side is checked against. In particular `lgamma` below is a
/// local Lanczos approximation and is NOT the bit-exact runtime/interp mirror
/// the compiler needs (plan-ppl-proper.md section 8): cross-checks between the
/// two sides are numeric (~1e-12), never bitwise.
module Density =

    let private ln2pi = log (2.0 * System.Math.PI)
    let private neginf = System.Double.NegativeInfinity
    let private posinf = System.Double.PositiveInfinity

    // -----------------------------------------------------------------------
    // lgamma
    // -----------------------------------------------------------------------

    /// Lanczos approximation to log |Gamma(x)|, with the classic g = 7,
    /// n = 9 coefficient set (the "gamma.c" / Numerical Recipes table, itself
    /// Lanczos 1964 evaluated at g = 7):
    ///
    ///   Gamma(z+1) = sqrt(2 pi) (z + g + 1/2)^(z+1/2) e^-(z+g+1/2) A_g(z),
    ///   A_g(z)     = c_0 + sum_{i=1..8} c_i / (z + i)
    ///
    /// Relative accuracy ~1e-15 on the half line x >= 0.5; x < 0.5 is reduced
    /// by the reflection formula Gamma(x) Gamma(1-x) = pi / sin(pi x).
    let private lanczosG = 7.0

    let private lanczosC =
        [| 0.99999999999980993
           676.5203681218851
           -1259.1392167224028
           771.32342877765313
           -176.61502916214059
           12.507343278686905
           -0.13857109526572012
           9.9843695780195716e-6
           1.5056327351493116e-7 |]

    let rec lgamma (x: float) : float =
        if System.Double.IsNaN x then nan
        elif x <= 0.0 && x = floor x then posinf          // poles at 0, -1, -2, ...
        elif x < 0.5 then
            // reflection; abs() because we return log |Gamma|
            log (System.Math.PI / abs (sin (System.Math.PI * x))) - lgamma (1.0 - x)
        else
            let z = x - 1.0
            let mutable a = lanczosC.[0]
            for i in 1 .. lanczosC.Length - 1 do
                a <- a + lanczosC.[i] / (z + float i)
            let t = z + lanczosG + 0.5
            0.5 * ln2pi + (z + 0.5) * log t - t + log a

    /// log B(a, b) = lgamma a + lgamma b - lgamma (a + b)
    let lbeta (a: float) (b: float) : float = lgamma a + lgamma b - lgamma (a + b)

    /// log(n!) for integral n >= 0.
    let logFactorial (n: int) : float = lgamma (float n + 1.0)

    // -----------------------------------------------------------------------
    // closed-form log-densities
    // -----------------------------------------------------------------------
    // Support convention throughout: a point strictly outside the support (or
    // on a boundary where the density is not defined) returns -infinity rather
    // than raising. Invalid PARAMETERS (negative variance, rate <= 0, empty
    // uniform interval, p outside [0,1]) raise -- they are programmer errors,
    // not data.

    /// N(mu, s2), s2 = variance.
    let gaussianLogpdf (mu: float) (s2: float) (x: float) : float =
        if s2 <= 0.0 then failwith "gaussianLogpdf: variance must be positive"
        let d = x - mu
        -0.5 * (ln2pi + log s2) - d * d / (2.0 * s2)

    /// Exp(rate), support x >= 0.
    let exponentialLogpdf (rate: float) (x: float) : float =
        if rate <= 0.0 then failwith "exponentialLogpdf: rate must be positive"
        if x < 0.0 then neginf else log rate - rate * x

    /// Uniform(a, b), support [a, b].
    let uniformLogpdf (a: float) (b: float) (x: float) : float =
        if b <= a then failwith "uniformLogpdf: need a < b"
        if x < a || x > b then neginf else -log (b - a)

    /// LogNormal(mu, s2): log X ~ N(mu, s2). Support x > 0.
    let lognormalLogpdf (mu: float) (s2: float) (x: float) : float =
        if s2 <= 0.0 then failwith "lognormalLogpdf: variance must be positive"
        if x <= 0.0 then neginf
        else
            let d = log x - mu
            -log x - 0.5 * (ln2pi + log s2) - d * d / (2.0 * s2)

    /// Gamma(shape, rate) -- RATE parameterization, mean = shape / rate.
    /// Support x > 0 (the x = 0 boundary is left out of the support for every
    /// shape, so shape = 1 reports -inf there rather than log rate).
    let gammaLogpdf (shape: float) (rate: float) (x: float) : float =
        if shape <= 0.0 then failwith "gammaLogpdf: shape must be positive"
        if rate <= 0.0 then failwith "gammaLogpdf: rate must be positive"
        if x <= 0.0 then neginf
        else shape * log rate - lgamma shape + (shape - 1.0) * log x - rate * x

    /// Poisson(lam) log-PMF, support k in {0, 1, 2, ...}.
    let poissonLogpmf (lam: float) (k: float) : float =
        if lam <= 0.0 then failwith "poissonLogpmf: lam must be positive"
        if k < 0.0 || k <> floor k then neginf
        else k * log lam - lam - lgamma (k + 1.0)

    /// Beta(a, b), support 0 < x < 1.
    let betaLogpdf (a: float) (b: float) (x: float) : float =
        if a <= 0.0 || b <= 0.0 then failwith "betaLogpdf: shapes must be positive"
        if x <= 0.0 || x >= 1.0 then neginf
        else (a - 1.0) * log x + (b - 1.0) * log (1.0 - x) - lbeta a b

    /// Bernoulli(p) log-PMF, support x in {0, 1}.
    let bernoulliLogpmf (p: float) (x: float) : float =
        if p < 0.0 || p > 1.0 then failwith "bernoulliLogpmf: p must lie in [0, 1]"
        if x = 1.0 then log p
        elif x = 0.0 then log (1.0 - p)
        else neginf

    /// InverseGamma(alpha, beta) -- NOT one of the surface families; it is the
    /// scale prior of the Normal-InverseGamma conjugate pair below, and the
    /// brute-force posterior integration needs its density. Support v > 0.
    let invGammaLogpdf (alpha: float) (beta: float) (v: float) : float =
        if alpha <= 0.0 || beta <= 0.0 then failwith "invGammaLogpdf: alpha, beta must be positive"
        if v <= 0.0 then neginf
        else alpha * log beta - lgamma alpha - (alpha + 1.0) * log v - beta / v

    /// The named families, as a tag + parameters. The compiler side treats a
    /// family application symbolically in exactly this shape (plan section 2).
    type Family =
        | Gaussian of mu: float * s2: float
        | Exponential of rate: float
        | Uniform of lo: float * hi: float
        | LogNormal of mu: float * s2: float
        | Gamma of shape: float * rate: float
        | Poisson of lam: float
        | Beta of a: float * b: float
        | Bernoulli of p: float

    let familyName (f: Family) : string =
        match f with
        | Gaussian (m, v) -> sprintf "gaussian(%g, %g)" m v
        | Exponential r -> sprintf "exponential(%g)" r
        | Uniform (a, b) -> sprintf "uniform(%g, %g)" a b
        | LogNormal (m, v) -> sprintf "lognormal(%g, %g)" m v
        | Gamma (k, r) -> sprintf "gamma(%g, %g)" k r
        | Poisson l -> sprintf "poisson(%g)" l
        | Beta (a, b) -> sprintf "beta(%g, %g)" a b
        | Bernoulli p -> sprintf "bernoulli(%g)" p

    let logpdf (f: Family) (x: float) : float =
        match f with
        | Gaussian (m, v) -> gaussianLogpdf m v x
        | Exponential r -> exponentialLogpdf r x
        | Uniform (a, b) -> uniformLogpdf a b x
        | LogNormal (m, v) -> lognormalLogpdf m v x
        | Gamma (k, r) -> gammaLogpdf k r x
        | Poisson l -> poissonLogpmf l x
        | Beta (a, b) -> betaLogpdf a b x
        | Bernoulli p -> bernoulliLogpmf p x

    /// Sum of per-sample log-densities -- the `ppl.loglik` contract.
    let loglik (f: Family) (data: float[]) : float =
        let mutable acc = 0.0
        for x in data do acc <- acc + logpdf f x
        acc


/// Edgeworth/Gram-Charlier density and Cornish-Fisher quantile from a
/// univariate cumulant tower kappa_1 .. kappa_r (r up to 6). Both are formal
/// asymptotic expansions in a bookkeeping parameter eps with
/// lambda_k = kappa_k / sigma^k of order eps^(k-2); an order-r tower supports
/// terms through eps^(r-2), so r = 2 is exactly Gaussian and r = 6 is the
/// usual four-term series.
///
/// Rather than hard-coding the (long, error-prone) term list, the series
/// coefficients are GENERATED here by formally expanding
///
///   phi_Z(t) = e^(-t^2/2) exp( sum_{k>=3} lambda_k eps^(k-2) (it)^k / k! )
///
/// in truncated eps-arithmetic. Inverting term by term with
/// (it)^j e^(-t^2/2)  <->  He_j(z) phi(z) gives the density; integrating gives
/// the CDF; formally inverting the CDF by series Newton gives Cornish-Fisher,
/// which is by definition exactly that inverse. A self-test in Program.fs
/// checks the generated coefficients against the hand-written textbook term
/// list and the generated quantile against the classic third-order closed
/// form.
module Expansion =

    let private sqrt2pi = sqrt (2.0 * System.Math.PI)

    let stdNormalPdf (z: float) : float = exp (-0.5 * z * z) / sqrt2pi

    /// Probabilists' Hermite polynomials He_0(z) .. He_n(z).
    /// He_0 = 1, He_1 = z, He_(m+1) = z He_m - m He_(m-1).
    let hermiteTable (n: int) (z: float) : float[] =
        let h = Array.zeroCreate (max 1 (n + 1))
        h.[0] <- 1.0
        if n >= 1 then h.[1] <- z
        for m in 1 .. n - 1 do
            h.[m + 1] <- z * h.[m] - float m * h.[m - 1]
        h

    let hermite (n: int) (z: float) : float = (hermiteTable n z).[n]

    // -----------------------------------------------------------------------
    // truncated power series in the bookkeeping parameter eps
    // -----------------------------------------------------------------------

    module private Eps =
        let zero (n: int) : float[] = Array.zeroCreate (n + 1)

        let konst (n: int) (c: float) : float[] =
            let a = zero n
            a.[0] <- c
            a

        let add (a: float[]) (b: float[]) = Array.map2 (+) a b
        let sub (a: float[]) (b: float[]) = Array.map2 (-) a b
        let scale (c: float) (a: float[]) = Array.map (fun x -> c * x) a

        let mul (a: float[]) (b: float[]) =
            let n = a.Length - 1
            let out = Array.zeroCreate (n + 1)
            for i in 0 .. n do
                if a.[i] <> 0.0 then
                    for j in 0 .. n - i do
                        out.[i + j] <- out.[i + j] + a.[i] * b.[j]
            out

        /// a / b by forward substitution; requires b.[0] <> 0.
        let div (a: float[]) (b: float[]) =
            let n = a.Length - 1
            let q = Array.zeroCreate (n + 1)
            for i in 0 .. n do
                let mutable s = a.[i]
                for j in 1 .. i do
                    s <- s - b.[j] * q.[i - j]
                q.[i] <- s / b.[0]
            q

        /// Substitute eps = 1.
        let collapse (a: float[]) = Array.sum a

    /// Coefficients c_j of  phi_Z(t) = e^(-t^2/2) sum_j c_j (it)^j, from
    /// exp(sum_{k>=3} lambda_k eps^(k-2) (it)^k / k!) truncated at eps^n.
    /// Indexed jPow -> eps-series. `lam.[k]` is lambda_k (entries 0..2 unused).
    ///
    /// The largest surviving (it) power is 3n: a factor of degree k costs
    /// eps^(k-2) and contributes (it)^k, and k/(k-2) is maximal at k = 3.
    let private charCoeffs (lam: float[]) (n: int) : float[][] =
        let maxJ = 3 * n
        let s = Array.init (maxJ + 1) (fun _ -> Eps.zero n)
        for k in 3 .. lam.Length - 1 do
            if k - 2 <= n && k <= maxJ then
                s.[k].[k - 2] <- lam.[k] / Combinatorics.factorial k
        let res = Array.init (maxJ + 1) (fun _ -> Eps.zero n)
        res.[0].[0] <- 1.0
        let mutable pow = Array.init (maxJ + 1) (fun _ -> Eps.zero n)
        pow.[0].[0] <- 1.0
        for m in 1 .. n do
            let next = Array.init (maxJ + 1) (fun _ -> Eps.zero n)
            for i in 0 .. maxJ do
                for k in 3 .. maxJ do
                    if i + k <= maxJ then
                        next.[i + k] <- Eps.add next.[i + k] (Eps.mul pow.[i] s.[k])
            pow <- next
            let w = 1.0 / Combinatorics.factorial m
            for j in 0 .. maxJ do
                res.[j] <- Eps.add res.[j] (Eps.scale w pow.[j])
        res

    /// mu, sigma, eps order and standardized cumulants of a tower.
    let private standardize (kappa: float list) =
        let k = List.toArray kappa
        if k.Length < 2 then failwith "Expansion: the tower needs at least kappa_1 and kappa_2"
        if k.[1] <= 0.0 then failwith "Expansion: kappa_2 must be positive"
        let r = min k.Length 6
        let sd = sqrt k.[1]
        let lam = Array.zeroCreate (r + 1)
        for j in 3 .. r do
            lam.[j] <- k.[j - 1] / sd ** float j
        (k.[0], sd, r - 2, lam)

    /// The bracket  sum_j c_j He_j(z)  multiplying the standard normal pdf --
    /// exposed so the self-tests can compare it against the textbook term
    /// list without re-deriving mu and sigma.
    let edgeworthFactor (kappa: float list) (x: float) : float =
        let (mu, sd, n, lam) = standardize kappa
        let c = charCoeffs lam n
        let z = (x - mu) / sd
        let he = hermiteTable (3 * n) z
        let mutable acc = 0.0
        for j in 0 .. 3 * n do
            acc <- acc + Eps.collapse c.[j] * he.[j]
        acc

    /// Edgeworth/Gram-Charlier density approximation at x. Exact when the
    /// tower is Gaussian (all kappa_k = 0 for k >= 3). NOT a probability
    /// density in general: the expansion can go negative in the tails, which
    /// is precisely what `dist_negativity` measures on the compiler side.
    let edgeworthPdf (kappa: float list) (x: float) : float =
        let (mu, sd, _, _) = standardize kappa
        let z = (x - mu) / sd
        stdNormalPdf z * edgeworthFactor kappa x / sd

    // -----------------------------------------------------------------------
    // standard normal quantile: Wichura AS241 (PPND16)
    // -----------------------------------------------------------------------

    /// Wichura's AS241 algorithm PPND16 (Applied Statistics 37, 1988): three
    /// rational branches in the central region, the moderate tail and the far
    /// tail, accurate to about 1e-16 over the whole open interval. Chosen over
    /// Acklam's shorter approximation (~1e-9) because the pin sheet is
    /// published at 17 significant digits.
    let normalQuantile (p: float) : float =
        if p <= 0.0 || p >= 1.0 then failwith "normalQuantile: p must lie strictly inside (0, 1)"
        let q = p - 0.5
        if abs q <= 0.425 then
            let r = 0.180625 - q * q
            q *
            (((((((2509.0809287301226727 * r + 33430.575583588128105) * r + 67265.770927008700853) * r +
                 45921.953931549871457) * r + 13731.693765509461125) * r + 1971.5909503065514427) * r +
                 133.14166789178437745) * r + 3.387132872796366608) /
            (((((((5226.495278852854561 * r + 28729.085735721942674) * r + 39307.89580009271061) * r +
                 21213.794301586595867) * r + 5394.1960214247511077) * r + 687.1870074920579083) * r +
                 42.313330701600911252) * r + 1.0)
        else
            let r0 = if q < 0.0 then p else 1.0 - p
            let r1 = sqrt (-(log r0))
            let v =
                if r1 <= 5.0 then
                    let r = r1 - 1.6
                    (((((((7.7454501427834140764e-4 * r + 0.0227238449892691845833) * r +
                          0.24178072517745061177) * r + 1.27045825245236838258) * r +
                          3.64784832476320460504) * r + 5.7694972214606914055) * r +
                          4.6303378461565452959) * r + 1.42343711074968357734) /
                    (((((((1.05075007164441684324e-9 * r + 5.475938084995344946e-4) * r +
                          0.0151986665636164571966) * r + 0.14810397642748007459) * r +
                          0.68976733498510000455) * r + 1.6763848301838038494) * r +
                          2.05319162663775882187) * r + 1.0)
                else
                    let r = r1 - 5.0
                    (((((((2.01033439929228813265e-7 * r + 2.71155556874348757815e-5) * r +
                          0.00124266094738807843860) * r + 0.026532189526576123093) * r +
                          0.29656057182850489123) * r + 1.7848265399172913358) * r +
                          5.4637849111641143699) * r + 6.6579046435011037772) /
                    (((((((2.04426310338993978564e-15 * r + 1.4215117583164458887e-7) * r +
                          1.8463183175100546818e-5) * r + 7.868691311456132591e-4) * r +
                          0.0148753612908506148525) * r + 0.13692988092273580531) * r +
                          0.59983220655588793769) * r + 1.0)
            if q < 0.0 then -v else v

    // -----------------------------------------------------------------------
    // Cornish-Fisher
    // -----------------------------------------------------------------------

    /// Cornish-Fisher quantile of a tower at probability p.
    ///
    /// Solves F_Z(w) = p for the standardized variable, where F_Z is the
    /// Edgeworth CDF
    ///     F_Z(z) = Phi(z) - phi(z) sum_{j>=1} c_j He_(j-1)(z),
    /// then maps back affinely, x_p = mu + sigma w. The solve is Newton's
    /// method carried out in truncated eps-arithmetic, so the answer is the
    /// formal series inverse -- which is what "Cornish-Fisher" means -- rather
    /// than a numeric root of a truncated CDF.
    ///
    /// Note Phi(z) - p is identically zero at the base point z = Phi^-1(p), so
    /// no normal CDF is needed anywhere: only phi and its derivatives appear.
    let cornishFisher (kappa: float list) (p: float) : float =
        let (mu, sd, n, lam) = standardize kappa
        let z = normalQuantile p
        if n = 0 then mu + sd * z
        else
        let c = charCoeffs lam n
        let maxJ = 3 * n
        let phiz = stdNormalPdf z
        let heZ = hermiteTable (maxJ + 1) z
        let mutable u = Eps.zero n
        for _ in 1 .. n + 4 do
            let w = Eps.add (Eps.konst n z) u
            // He_m(w) as eps-series, m = 0 .. maxJ
            let heW = Array.init (maxJ + 1) (fun _ -> Eps.zero n)
            heW.[0] <- Eps.konst n 1.0
            if maxJ >= 1 then heW.[1] <- w
            for m in 1 .. maxJ - 1 do
                heW.[m + 1] <- Eps.sub (Eps.mul w heW.[m]) (Eps.scale (float m) heW.[m - 1])
            // Phi(z+u) - Phi(z) = sum_{i>=1} (-1)^(i-1) He_(i-1)(z) phi(z) u^i / i!
            let mutable dPhi = Eps.zero n
            let mutable up = Eps.konst n 1.0
            for i in 1 .. n do
                up <- Eps.mul up u
                let sgn = if (i - 1) % 2 = 0 then 1.0 else -1.0
                dPhi <- Eps.add dPhi (Eps.scale (sgn * heZ.[i - 1] * phiz / Combinatorics.factorial i) up)
            // phi(z+u) = sum_{i>=0} (-1)^i He_i(z) phi(z) u^i / i!
            let mutable phiW = Eps.zero n
            up <- Eps.konst n 1.0
            for i in 0 .. n do
                if i > 0 then up <- Eps.mul up u
                let sgn = if i % 2 = 0 then 1.0 else -1.0
                phiW <- Eps.add phiW (Eps.scale (sgn * heZ.[i] * phiz / Combinatorics.factorial i) up)
            let mutable tail = Eps.zero n
            for j in 1 .. maxJ do
                tail <- Eps.add tail (Eps.mul c.[j] heW.[j - 1])
            let g = Eps.sub dPhi (Eps.mul phiW tail)
            // G'(w) = f_Z(w) = phi(w) sum_j c_j He_j(w)
            let mutable dens = Eps.zero n
            for j in 0 .. maxJ do
                dens <- Eps.add dens (Eps.mul c.[j] heW.[j])
            u <- Eps.sub u (Eps.div g (Eps.mul phiW dens))
        mu + sd * (z + Eps.collapse u)


/// Conjugate posterior updates: prior hyperparameters plus the sufficient
/// statistics of the data go in, posterior hyperparameters come out. Pure
/// arithmetic -- the compiler side (plan section 6, `ppl.bayes`) synthesizes
/// exactly these formulas as plain Blade source.
///
/// Where the posterior is a family the tower already has a constructor for
/// (Dist.fs:28-40) a `*Tower` function returns it; Beta and the Student-t
/// margin of the Normal-InverseGamma posterior have no such constructor, so
/// they stay in hyperparameter form and expose closed-form moments instead.
module Conjugate =

    // ---- Normal-Normal, known likelihood variance --------------------------

    type NormalNormal = { PostMean: float; PostVar: float }

    /// mu ~ N(m0, v0); x_i ~ N(mu, sigma2) with sigma2 KNOWN.
    /// Sufficient statistics: n and sum_i x_i.
    let normalNormal (m0: float) (v0: float) (sigma2: float) (n: int) (sumX: float) : NormalNormal =
        if v0 <= 0.0 || sigma2 <= 0.0 then failwith "normalNormal: variances must be positive"
        let vn = 1.0 / (1.0 / v0 + float n / sigma2)
        { PostMean = vn * (m0 / v0 + sumX / sigma2); PostVar = vn }

    let normalNormalTower (post: NormalNormal) (r: int) : float[] =
        Dist.gaussianCumulants post.PostMean post.PostVar r

    // ---- Beta-Bernoulli ----------------------------------------------------

    type BetaBernoulli = { A: float; B: float }

    /// p ~ Beta(a0, b0); x_i ~ Bernoulli(p).
    /// Sufficient statistics: n trials and k successes.
    let betaBernoulli (a0: float) (b0: float) (n: int) (k: int) : BetaBernoulli =
        if a0 <= 0.0 || b0 <= 0.0 then failwith "betaBernoulli: prior shapes must be positive"
        if k < 0 || k > n then failwith "betaBernoulli: need 0 <= k <= n"
        { A = a0 + float k; B = b0 + float (n - k) }

    let betaMean (q: BetaBernoulli) : float = q.A / (q.A + q.B)

    let betaVar (q: BetaBernoulli) : float =
        let s = q.A + q.B
        q.A * q.B / (s * s * (s + 1.0))

    // ---- Gamma-Poisson -----------------------------------------------------

    type GammaPoisson = { Shape: float; Rate: float }

    /// lam ~ Gamma(a0, b0) (rate parameterization); x_i ~ Poisson(lam).
    /// Sufficient statistics: n and sum_i x_i.
    let gammaPoisson (a0: float) (b0: float) (n: int) (sumK: float) : GammaPoisson =
        if a0 <= 0.0 || b0 <= 0.0 then failwith "gammaPoisson: prior shape/rate must be positive"
        { Shape = a0 + sumK; Rate = b0 + float n }

    let gammaPoissonTower (q: GammaPoisson) (r: int) : float[] =
        Dist.gammaCumulants q.Shape q.Rate r

    // ---- Normal-InverseGamma (unknown mean AND variance) -------------------

    type NormalInvGamma = { M: float; Kappa: float; Alpha: float; Beta: float }

    /// sigma2 ~ InvGamma(a0, b0), mu | sigma2 ~ N(m0, sigma2 / k0);
    /// x_i ~ N(mu, sigma2). Sufficient statistics: n, sum_i x_i, sum_i x_i^2.
    let normalInvGamma (m0: float) (k0: float) (a0: float) (b0: float)
                       (n: int) (sumX: float) (sumX2: float) : NormalInvGamma =
        if k0 <= 0.0 || a0 <= 0.0 || b0 <= 0.0 then
            failwith "normalInvGamma: k0, a0, b0 must be positive"
        let nf = float n
        let xbar = if n = 0 then 0.0 else sumX / nf
        let ss = sumX2 - nf * xbar * xbar          // sum_i (x_i - xbar)^2
        let d = xbar - m0
        { M = (k0 * m0 + nf * xbar) / (k0 + nf)
          Kappa = k0 + nf
          Alpha = a0 + nf / 2.0
          Beta = b0 + 0.5 * ss + k0 * nf * d * d / (2.0 * (k0 + nf)) }

    /// The posterior PRECISION tau = 1/sigma2 is Gamma(alpha_n, beta_n) -- the
    /// one clean tower in this pair. (The marginal of mu is Student-t with
    /// 2*alpha_n degrees of freedom, whose cumulants past order 2*alpha_n do
    /// not exist, so it deliberately gets no tower.)
    let normalInvGammaPrecisionTower (q: NormalInvGamma) (r: int) : float[] =
        Dist.gammaCumulants q.Alpha q.Beta r

    /// E[mu] under the posterior (defined for alpha > 1/2).
    let nigMeanMu (q: NormalInvGamma) : float = q.M

    /// Var[mu]: the Student-t margin has scale^2 = beta/(kappa alpha) and
    /// nu = 2 alpha, so the variance is beta / (kappa (alpha - 1)).
    let nigVarMu (q: NormalInvGamma) : float = q.Beta / (q.Kappa * (q.Alpha - 1.0))

    /// E[sigma2] = beta / (alpha - 1), defined for alpha > 1.
    let nigMeanSigma2 (q: NormalInvGamma) : float = q.Beta / (q.Alpha - 1.0)

    /// E[1/sigma2] = alpha / beta.
    let nigMeanPrecision (q: NormalInvGamma) : float = q.Alpha / q.Beta
