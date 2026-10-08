namespace BladeML

open System

/// THE LIE-ALGEBRA REFERENCE behind `dump-lie` -- the value oracle for the
/// corpus directory tests/corpus/lie/ (P0 of
/// docs/plans/plan-latent-symmetry-learning.md, sections 3.4 and 5). A
/// plain-F# port of docs/research/latent-symmetry/checks/check_b4.py and
/// check_b4b.py, plus RIGOR.md B1.5's moment-annihilation operator and
/// B3.2's functional (invariance) rows.
///
/// INDEPENDENCE FROM THE COMPILER. This project does not reference
/// Blade.fsproj. The so(3) generator tables are built twice here and
/// compared: once from the closed forms in the header of
/// src/ml/compiler/MLLieDischarge.fs (`blockGenerator`: the ladder radicands
/// a(k), b(k), the extra sqrt(2) at the m = 0 seam, the Condon-Shortley
/// block structure, L_z on (R_m, I_m) = [[0, -m], [m, 0]]), and once by
/// differentiating this oracle's OWN `Rotations.wignerD` -- which is FIT
/// from the real harmonics and never sees a table -- along R_x, R_y, R_z at
/// theta = 0. D_l(R_a(theta)) = exp(theta A) has spectrum {i m : |m| <= l},
/// so every entry is a trigonometric polynomial of degree l in theta, and
/// the derivative at 0 is EXACT from 2K + 1 equally spaced samples on the
/// circle (K >= l; the discrete Fourier sine coefficients times m). No
/// truncation error, only the fit's rounding: the agreement (asserted at
/// 1e-12) certifies that the dumped tables are the compiler's.
///
/// DECOMPOSITIONS. `svdJ` / `eighJ` are oracles/math/Jacobi.fs's cyclic
/// Jacobi routines -- the code src/math/compiler/MathDecls.fs synthesizes
/// for `m.svd` / `m.eigh` -- operation for operation: pivot order,
/// convergence guard, descending selection sort, sign fix. One change: a
/// sweep that rotates nothing ends the loop (every later sweep would be the
/// identity), so a result here equals `m.svd(A, N)` / `m.eigh(S, N)` for any
/// N at or above the rotating sweeps it used (`sweepsHighWater`). Rank
/// decisions are numerical at 1e-9 (RIGOR.md B4's caveat: exact for a
/// closed algebra, heuristic for an estimate).
///
/// CONVENTIONS (RIGOR.md "Conventions"): row-major vec, Frobenius inner
/// product <A, B> = tr(A^T B), rho_k(A) the Leibniz derivation extension,
/// axes indexed 0 = x, 1 = y, 2 = z.
module Lie =

    open MathUtils

    type Mat = float[][]

    // ---------------------------------------------------------------------
    // Dense helpers (jagged, row-major)
    // ---------------------------------------------------------------------

    let zeros (n: int) (m: int) : Mat = Array.init n (fun _ -> Array.zeroCreate m)

    let eye (n: int) : Mat =
        Array.init n (fun i -> Array.init n (fun j -> if i = j then 1.0 else 0.0))

    let madd (a: Mat) (b: Mat) : Mat = Array.map2 (Array.map2 (+)) a b
    let msub (a: Mat) (b: Mat) : Mat = Array.map2 (Array.map2 (-)) a b
    let mscale (s: float) (a: Mat) : Mat = a |> Array.map (Array.map (fun x -> s * x))
    let bracket (a: Mat) (b: Mat) : Mat = msub (matMul a b) (matMul b a)

    let frob (a: Mat) (b: Mat) : float =
        let mutable acc = 0.0
        for i in 0 .. a.Length - 1 do
            for j in 0 .. a.[i].Length - 1 do
                acc <- acc + a.[i].[j] * b.[i].[j]
        acc

    let maxAbsM (a: Mat) : float = a |> Array.fold (fun acc r -> max acc (maxAbs r)) 0.0
    let maxDiffM (a: Mat) (b: Mat) : float = maxAbsM (msub a b)
    let vecRM (a: Mat) : float[] = Array.concat a
    let unvec (rows: int) (cols: int) (v: float[]) : Mat =
        Array.init rows (fun i -> Array.sub v (i * cols) cols)
    let symmetrize (a: Mat) : Mat =
        Array.init a.Length (fun i -> Array.init a.Length (fun j -> 0.5 * (a.[i].[j] + a.[j].[i])))

    let kron (a: Mat) (b: Mat) : Mat =
        let br = b.Length
        let bc = b.[0].Length
        Array.init (a.Length * br) (fun i ->
            Array.init (a.[0].Length * bc) (fun j -> a.[i / br].[j / bc] * b.[i % br].[j % bc]))

    let blockDiag (ms: Mat list) : Mat =
        let n = ms |> List.sumBy (fun m -> m.Length)
        let out = zeros n n
        let mutable o = 0
        for m in ms do
            for i in 0 .. m.Length - 1 do
                for j in 0 .. m.Length - 1 do
                    out.[o + i].[o + j] <- m.[i].[j]
            o <- o + m.Length
        out

    /// The matrix unit E_ij of gl(n).
    let elem (n: int) (i: int) (j: int) : Mat =
        Array.init n (fun r -> Array.init n (fun c -> if r = i && c = j then 1.0 else 0.0))

    /// Sum_k w_k M_k, accumulated k-ascending from +0.0.
    let combo (ws: float[]) (ms: Mat[]) : Mat =
        let r = ms.[0].Length
        let c = ms.[0].[0].Length
        let out = zeros r c
        for k in 0 .. ms.Length - 1 do
            for i in 0 .. r - 1 do
                for j in 0 .. c - 1 do
                    out.[i].[j] <- out.[i].[j] + ws.[k] * ms.[k].[i].[j]
        out

    // ---------------------------------------------------------------------
    // Cyclic Jacobi SVD / eigh (oracles/math/Jacobi.fs, jagged)
    // ---------------------------------------------------------------------

    /// Most ROTATING sweeps any CONVERGED svdJ / eighJ call of this process
    /// needed (a converged call equals m.svd / m.eigh at any budget >= it).
    let mutable sweepsHighWater = 0
    /// Calls that hit the sweep cap. Observed cause: a numerically-ZERO
    /// column (an exact nullspace direction, entries shrunk below ~1e-154 by
    /// earlier rotations) whose squared norm is SUBNORMAL, so the relative
    /// 1e-15 guard -- sqrt of a subnormal has lost its precision -- never
    /// passes against a normal column and the pair keeps rotating. Every
    /// nonzero sigma / vector is converged to rounding; the zero sigma is
    /// ~1e-158 either way. Shapes in `capLog`.
    let mutable cappedCalls = 0
    let capLog = System.Collections.Generic.List<string>()
    let maxSweeps = 80

    let private noteSweeps (what: string) (rotated: bool) (sweeps: int) =
        if rotated then
            cappedCalls <- cappedCalls + 1
            capLog.Add what
        elif sweeps - 1 > sweepsHighWater then sweepsHighWater <- sweeps - 1

    /// Thin SVD, m >= n: (U m x n, S descending, V n x n with the right
    /// singular vectors as COLUMNS); A = U diag(S) V^T.
    let svdJ (a: Mat) : Mat * float[] * Mat =
        let m = a.Length
        let n = a.[0].Length
        if m < n then failwith $"Lie.svdJ: m < n ({m}x{n}); use svdPad"
        let w = Array.init m (fun i -> Array.copy a.[i])
        let vv = eye n
        let mutable sweeps = 0
        let mutable rotated = true
        while rotated && sweeps < maxSweeps do
            rotated <- false
            sweeps <- sweeps + 1
            for p in 0 .. n - 2 do
                for q in p + 1 .. n - 1 do
                    let mutable aa = 0.0
                    let mutable bb = 0.0
                    let mutable cc = 0.0
                    for i in 0 .. m - 1 do
                        let wp = w.[i].[p]
                        let wq = w.[i].[q]
                        aa <- aa + wp * wp
                        bb <- bb + wq * wq
                        cc <- cc + wp * wq
                    let conv = abs cc <= 1.0e-15 * (sqrt aa * sqrt bb)
                    if not conv then
                        rotated <- true
                        let zeta = (bb - aa) / (2.0 * cc)
                        let tt = (if zeta >= 0.0 then 1.0 else -1.0) / (abs zeta + sqrt (1.0 + zeta * zeta))
                        let cs = 1.0 / sqrt (1.0 + tt * tt)
                        let sn = cs * tt
                        for i in 0 .. m - 1 do
                            let tp = w.[i].[p]
                            let tq = w.[i].[q]
                            w.[i].[p] <- cs * tp - sn * tq
                            w.[i].[q] <- sn * tp + cs * tq
                        for i in 0 .. n - 1 do
                            let tp = vv.[i].[p]
                            let tq = vv.[i].[q]
                            vv.[i].[p] <- cs * tp - sn * tq
                            vv.[i].[q] <- sn * tp + cs * tq
        noteSweeps $"svd {m}x{n}" rotated sweeps
        // singular values = column norms
        let s = Array.init n (fun j ->
            let mutable acc = 0.0
            for i in 0 .. m - 1 do acc <- acc + w.[i].[j] * w.[i].[j]
            sqrt acc)
        // selection sort descending (ties keep original order)
        for kk in 0 .. n - 1 do
            let mutable best = kk
            for j in kk + 1 .. n - 1 do
                if s.[j] > s.[best] then best <- j
            let ts = s.[kk]
            s.[kk] <- s.[best]
            s.[best] <- ts
            for i in 0 .. m - 1 do
                let tw = w.[i].[kk]
                w.[i].[kk] <- w.[i].[best]
                w.[i].[best] <- tw
            for i in 0 .. n - 1 do
                let tv = vv.[i].[kk]
                vv.[i].[kk] <- vv.[i].[best]
                vv.[i].[best] <- tv
        // U = normalized columns (zero sigma -> zero column)
        let u = zeros m n
        for j in 0 .. n - 1 do
            let inv = if s.[j] > 1.0e-300 then 1.0 / s.[j] else 0.0
            for i in 0 .. m - 1 do u.[i].[j] <- w.[i].[j] * inv
        // sign fix: first row attaining max |entry| per U column made positive
        for j in 0 .. n - 1 do
            let mutable bigv = 0.0
            let mutable best = 0
            for i in 0 .. m - 1 do
                let mag = abs u.[i].[j]
                if mag > bigv then
                    best <- i
                    bigv <- mag
            let flip = if u.[best].[j] < 0.0 then -1.0 else 1.0
            for i in 0 .. m - 1 do u.[i].[j] <- u.[i].[j] * flip
            for i in 0 .. n - 1 do vv.[i].[j] <- vv.[i].[j] * flip
        (u, s, vv)

    /// Symmetric eigendecomposition: (Q with eigenvector COLUMNS, LAM
    /// descending); S = Q diag(LAM) Q^T. Symmetry is assumed, not checked.
    let eighJ (s0: Mat) : Mat * float[] =
        let n = s0.Length
        let aw = Array.init n (fun i -> Array.copy s0.[i])
        let q = eye n
        let mutable sweeps = 0
        let mutable rotated = true
        while rotated && sweeps < maxSweeps do
            rotated <- false
            sweeps <- sweeps + 1
            for p in 0 .. n - 2 do
                for r in p + 1 .. n - 1 do
                    let apq = aw.[p].[r]
                    let app = aw.[p].[p]
                    let aqq = aw.[r].[r]
                    let conv = abs apq <= 1.0e-15 * (sqrt (abs app) * sqrt (abs aqq) + 1.0e-150)
                    if not conv then
                        rotated <- true
                        let theta = (aqq - app) / (2.0 * apq)
                        let tt = (if theta >= 0.0 then 1.0 else -1.0) / (abs theta + sqrt (1.0 + theta * theta))
                        let cs = 1.0 / sqrt (1.0 + tt * tt)
                        let sn = cs * tt
                        for i in 0 .. n - 1 do
                            let tp = aw.[i].[p]
                            let tq = aw.[i].[r]
                            aw.[i].[p] <- cs * tp - sn * tq
                            aw.[i].[r] <- sn * tp + cs * tq
                        for i in 0 .. n - 1 do
                            let tp = aw.[p].[i]
                            let tq = aw.[r].[i]
                            aw.[p].[i] <- cs * tp - sn * tq
                            aw.[r].[i] <- sn * tp + cs * tq
                        for i in 0 .. n - 1 do
                            let tp = q.[i].[p]
                            let tq = q.[i].[r]
                            q.[i].[p] <- cs * tp - sn * tq
                            q.[i].[r] <- sn * tp + cs * tq
        noteSweeps $"eigh {n}x{n}" rotated sweeps
        let lam = Array.init n (fun j -> aw.[j].[j])
        for kk in 0 .. n - 1 do
            let mutable best = kk
            for j in kk + 1 .. n - 1 do
                if lam.[j] > lam.[best] then best <- j
            let tl = lam.[kk]
            lam.[kk] <- lam.[best]
            lam.[best] <- tl
            for i in 0 .. n - 1 do
                let tq = q.[i].[kk]
                q.[i].[kk] <- q.[i].[best]
                q.[i].[best] <- tq
        for j in 0 .. n - 1 do
            let mutable bigv = 0.0
            let mutable best = 0
            for i in 0 .. n - 1 do
                let mag = abs q.[i].[j]
                if mag > bigv then
                    best <- i
                    bigv <- mag
            let flip = if q.[best].[j] < 0.0 then -1.0 else 1.0
            for i in 0 .. n - 1 do q.[i].[j] <- q.[i].[j] * flip
        (q, lam)

    /// Singular values and right singular vectors of ANY shape: a wide
    /// matrix is padded with zero rows to square first (zero rows change
    /// neither V nor the nonzero sigmas; they surface the n - m implicit
    /// zero sigmas explicitly). S has length n = the unknown dimension.
    let svdPad (a: Mat) : float[] * Mat =
        let m = a.Length
        let n = a.[0].Length
        let ap = if m >= n then a else Array.append a (Array.init (n - m) (fun _ -> Array.zeroCreate n))
        let _, s, v = svdJ ap
        (s, v)

    /// Right null vectors: V columns with sigma <= rtol * sigma_max (scipy
    /// null_space's rcond rule), plus the full sigma list.
    let nullSpace (rtol: float) (a: Mat) : float[][] * float[] =
        let s, v = svdPad a
        let smax = if s.Length = 0 then 0.0 else s.[0]
        let idx = [| for j in 0 .. s.Length - 1 do if s.[j] <= rtol * smax then yield j |]
        (idx |> Array.map (fun j -> Array.init v.Length (fun i -> v.[i].[j]))), s

    /// The rank-decision tolerance of the small-algebra analysis.
    let rankTol = 1e-9

    /// Numerical rank: sigmas above rankTol * max(1, sigma_max).
    let numRank (s: float[]) : int =
        let smax = if s.Length = 0 then 0.0 else Array.max s
        s |> Array.filter (fun x -> x > rankTol * max 1.0 smax) |> Array.length

    /// Q diag(f(lam)) Q^T for symmetric input (matrix functions by eigh).
    let symFun (f: float -> float) (a: Mat) : Mat =
        let q, lam = eighJ a
        let n = a.Length
        Array.init n (fun i -> Array.init n (fun j ->
            let mutable acc = 0.0
            for k in 0 .. n - 1 do
                acc <- acc + q.[i].[k] * f lam.[k] * q.[j].[k]
            acc))

    // ---------------------------------------------------------------------
    // so(3) generator tables
    // ---------------------------------------------------------------------

    let axisName (a: int) = [| "x"; "y"; "z" |].[a]

    /// dpi_l(L_a) in the certifier's real basis (index c = m + l): the
    /// closed forms of MLLieDischarge.blockGenerator, in floats.
    let table (axis: int) (l: int) : Mat =
        let d = 2 * l + 1
        let m = zeros d d
        let a (k: int) = float ((l - k) * (l + k + 1))
        let b (k: int) = float ((l + k) * (l - k + 1))
        match axis with
        | 2 ->
            for k in 1 .. l do
                m.[l + k].[l - k] <- float (-k)
                m.[l - k].[l + k] <- float k
        | 1 ->
            if l >= 1 then
                m.[l].[l + 1] <- -0.5 * sqrt (2.0 * a 0)
                m.[l + 1].[l] <- 0.5 * sqrt (2.0 * b 1)
            for k in 2 .. l do
                m.[l + k].[l + k - 1] <- 0.5 * sqrt (b k)
                m.[l - k].[l - k + 1] <- 0.5 * sqrt (b k)
            for k in 1 .. l - 1 do
                m.[l + k].[l + k + 1] <- -0.5 * sqrt (a k)
                m.[l - k].[l - k - 1] <- -0.5 * sqrt (a k)
        | 0 ->
            if l >= 1 then
                m.[l].[l - 1] <- 0.5 * sqrt (2.0 * a 0)
                m.[l - 1].[l] <- -0.5 * sqrt (2.0 * b 1)
            for k in 2 .. l do
                m.[l + k].[l - k + 1] <- 0.5 * sqrt (b k)
                m.[l - k].[l + k - 1] <- -0.5 * sqrt (b k)
            for k in 1 .. l - 1 do
                m.[l + k].[l - k - 1] <- 0.5 * sqrt (a k)
                m.[l - k].[l + k + 1] <- -0.5 * sqrt (a k)
        | _ -> failwith "Lie.table: axis must be 0 (x), 1 (y) or 2 (z)"
        m

    let rotX (t: float) : Mat =
        [| [| 1.0; 0.0; 0.0 |]
           [| 0.0; cos t; -sin t |]
           [| 0.0; sin t; cos t |] |]

    let rotAbout (axis: int) (t: float) : Mat =
        match axis with
        | 0 -> rotX t
        | 1 -> Rotations.rotY t
        | _ -> Rotations.rotZ t

    /// d/dtheta D_l(R_a(theta)) at 0 from the oracle's FIT Wigner matrices:
    /// exact spectral differentiation of a degree-l trigonometric polynomial
    /// from N = 2K + 1 samples, K = l + 1 (f'(0) = Sum_m m b_m with
    /// b_m = (2/N) Sum_k f(theta_k) sin(m theta_k)).
    let tableFromWigner (axis: int) (l: int) : Mat =
        let d = 2 * l + 1
        let kmax = l + 1
        let n = 2 * kmax + 1
        let acc = zeros d d
        for k in 0 .. n - 1 do
            let th = 2.0 * Math.PI * float k / float n
            let mutable w = 0.0
            for m in 1 .. kmax do
                w <- w + float m * (2.0 / float n) * sin (float m * th)
            if w <> 0.0 then
                let dm = Rotations.wignerD l (rotAbout axis th)
                for i in 0 .. d - 1 do
                    for j in 0 .. d - 1 do
                        acc.[i].[j] <- acc.[i].[j] + w * dm.[i].[j]
        acc

    let leviCivita (i: int) (j: int) (k: int) : float =
        if i = j || j = k || i = k then 0.0
        elif (i, j, k) = (0, 1, 2) || (i, j, k) = (1, 2, 0) || (i, j, k) = (2, 0, 1) then 1.0
        else -1.0

    /// The standard rotation generators (L_a)_jk = -eps_ajk on (x, y, z):
    /// L_a = d/dtheta R_a(theta) at 0.
    let lStd (a: int) : Mat =
        Array.init 3 (fun j -> Array.init 3 (fun k -> 0.0 - leviCivita a j k))

    /// (y, z, x) = P (x, y, z): the l = 1 real basis order.
    let permYZX : Mat =
        [| [| 0.0; 1.0; 0.0 |]
           [| 0.0; 0.0; 1.0 |]
           [| 1.0; 0.0; 0.0 |] |]

    // ---------------------------------------------------------------------
    // Structure constants, Killing form, algebra invariants (check_b4b)
    // ---------------------------------------------------------------------

    /// c(i, j, k) = <E_k, [E_i, E_j]> / <E_k, E_k> -- exact coordinates for
    /// a Frobenius-ORTHOGONAL basis of a closed algebra.
    let structureConstants (basis: Mat[]) : float[][][] =
        let r = basis.Length
        Array.init r (fun i -> Array.init r (fun j ->
            let b = bracket basis.[i] basis.[j]
            Array.init r (fun k -> frob basis.[k] b / frob basis.[k] basis.[k])))

    /// c(i, j, k) = <E_k, [E_i, E_j]> for a Frobenius-ORTHONORMAL basis.
    let structureConstantsOn (basis: Mat[]) : float[][][] =
        let r = basis.Length
        Array.init r (fun i -> Array.init r (fun j ->
            let b = bracket basis.[i] basis.[j]
            Array.init r (fun k -> frob basis.[k] b)))

    /// max_ij |[E_i, E_j] - Sum_k c_ij^k E_k|_F.
    let closureResidual (basis: Mat[]) (c: float[][][]) : float =
        let r = basis.Length
        let mutable worst = 0.0
        for i in 0 .. r - 1 do
            for j in 0 .. r - 1 do
                let dlt = msub (bracket basis.[i] basis.[j]) (combo c.[i].[j] basis)
                worst <- max worst (sqrt (frob dlt dlt))
        worst

    /// kappa_ij = tr(ad_i ad_j) = Sum_kl c_ik^l c_jl^k.
    let killing (c: float[][][]) : Mat =
        let r = c.Length
        Array.init r (fun i -> Array.init r (fun j ->
            let mutable acc = 0.0
            for k in 0 .. r - 1 do
                for l in 0 .. r - 1 do
                    acc <- acc + c.[i].[k].[l] * c.[j].[l].[k]
            acc))

    /// Sum_a B_a^2.
    let casimirSum (basis: Mat[]) : Mat =
        basis |> Array.map (fun b -> matMul b b) |> Array.reduce madd

    /// The Frobenius-orthonormal basis of span(gens): the left singular
    /// vectors of the stacked vec(gens) with sigma above the rank tolerance.
    let orthonormalBasis (gens: Mat[]) : Mat[] * float[] =
        let d = gens.[0].Length
        let vcols = transpose (gens |> Array.map vecRM)
        let u, s, _ = svdJ vcols
        let r = numRank s
        (Array.init r (fun i -> unvec d d (Array.init (d * d) (fun p -> u.[p].[i])))), s

    type AlgebraReport =
        { Dim: int
          Basis: Mat[]
          C: float[][][]
          Closure: float
          Kappa: Mat
          KappaEig: float[]
          Signature: int * int * int
          Derived: int
          Center: int
          Radical: int
          Rank: int }

    /// check_b4b's `analyze`: orthonormal basis, structure constants,
    /// closure, Killing form and signature, dim [g,g], center (ker ad),
    /// radical ([g,g]^perp-kappa, Cartan), and the rank (algebraic
    /// multiplicity of 0 in ad_X for a seeded X = dim ker ad_X^dim).
    let analyze (seed: int) (gens: Mat[]) : AlgebraReport =
        let basis, _ = orthonormalBasis gens
        let r = basis.Length
        let c = structureConstantsOn basis
        let closure = closureResidual basis c
        let kap = killing c
        let _, ev = eighJ kap
        let tol = rankTol * max 1.0 (maxAbs ev)
        let signature =
            (ev |> Array.filter (fun x -> x > tol) |> Array.length,
             ev |> Array.filter (fun x -> x < -tol) |> Array.length,
             ev |> Array.filter (fun x -> abs x <= tol) |> Array.length)
        let pairs = [| for i in 0 .. r - 1 do for j in i + 1 .. r - 1 -> (i, j) |]
        let derived =
            if pairs.Length = 0 then 0
            else
                let dv = transpose (pairs |> Array.map (fun (i, j) -> vecRM (bracket basis.[i] basis.[j])))
                if maxAbsM dv <= 1e-12 then 0 else numRank (fst (svdPad dv))
        let center =
            // rows (i, k), columns a: Sum_a x_a c(a, i, k) = 0
            let z = Array.init (r * r) (fun p -> Array.init r (fun a -> c.[a].[p / r].[p % r]))
            r - numRank (fst (svdPad z))
        let radical =
            if derived = 0 then r
            else
                let rows =
                    pairs |> Array.map (fun (i, j) ->
                        Array.init r (fun x ->
                            let mutable acc = 0.0
                            for y in 0 .. r - 1 do acc <- acc + kap.[x].[y] * c.[i].[j].[y]
                            acc))
                r - numRank (fst (svdPad rows))
        let rng = Random(seed)
        let xs = Array.init r (fun _ -> 2.0 * rng.NextDouble() - 1.0)
        let adx =
            Array.init r (fun k -> Array.init r (fun i ->
                let mutable acc = 0.0
                for a in 0 .. r - 1 do acc <- acc + xs.[a] * c.[a].[i].[k]
                acc))
        let mutable pw = eye r
        for _ in 1 .. r do pw <- matMul pw adx
        let rank = r - numRank (fst (svdPad pw))
        { Dim = r; Basis = basis; C = c; Closure = closure; Kappa = kap; KappaEig = ev
          Signature = signature; Derived = derived; Center = center; Radical = radical; Rank = rank }

    /// dim End_g(V) = dim ker of the stacked (I (x) G^T - G (x) I).
    let commutantDim (gens: Mat[]) : int =
        let d = gens.[0].Length
        let i = eye d
        let a = gens |> Array.collect (fun g -> msub (kron i (transpose g)) (kron g i))
        let nulls, _ = nullSpace 1e-9 a
        nulls.Length

    // The explicit small-algebra bases (check_b4b), each a matrix list.
    let so3Std : Mat[] = Array.init 3 lStd
    /// so(2,1): A^T eta + eta A = 0 for eta = diag(1, -1, -1) -- two boosts
    /// and the rotation of the (1, 2) plane.
    let so21 : Mat[] =
        [| madd (elem 3 0 1) (elem 3 1 0)
           madd (elem 3 0 2) (elem 3 2 0)
           msub (elem 3 2 1) (elem 3 1 2) |]
    let sl2 : Mat[] = [| msub (elem 2 0 0) (elem 2 1 1); elem 2 0 1; elem 2 1 0 |]
    let se2 : Mat[] = [| msub (elem 3 1 0) (elem 3 0 1); elem 3 0 2; elem 3 1 2 |]
    let heis3 : Mat[] = [| elem 3 0 1; elem 3 1 2; elem 3 0 2 |]
    let gl2 : Mat[] = [| elem 2 0 0; elem 2 0 1; elem 2 1 0; elem 2 1 1 |]
    /// so(4): E_ij - E_ji, i < j (6).
    let so4 : Mat[] = [| for i in 0 .. 3 do for j in i + 1 .. 3 -> msub (elem 4 i j) (elem 4 j i) |]
    /// so(3,1) (eta = diag(-1, 1, 1, 1) up to sign): the rotations of axes 1..3, then the boosts E0j + Ej0.
    let so31 : Mat[] =
        Array.append
            [| for i in 1 .. 3 do for j in i + 1 .. 3 -> msub (elem 4 i j) (elem 4 j i) |]
            [| for j in 1 .. 3 -> madd (elem 4 0 j) (elem 4 j 0) |]
    /// se(1,1) homogeneous 3x3: the boost E10 + E01, then the translations.
    let se11 : Mat[] = [| madd (elem 3 1 0) (elem 3 0 1); elem 3 0 2; elem 3 1 2 |]
    /// Embed n x n top-left in (n+1) x (n+1).
    let padOne (a: Mat) : Mat =
        let n = a.Length
        Array.init (n + 1) (fun i -> Array.init (n + 1) (fun j -> if i < n && j < n then a.[i].[j] else 0.0))
    /// se(3) homogeneous 4x4: rotations E21-E12, E02-E20, E10-E01 (padded), then translations E_i3.
    let se3 : Mat[] =
        Array.append
            ([| msub (elem 3 2 1) (elem 3 1 2); msub (elem 3 0 2) (elem 3 2 0); msub (elem 3 1 0) (elem 3 0 1) |]
             |> Array.map padOne)
            [| for i in 0 .. 2 -> elem 4 i 3 |]
    /// sl(2) |x R^2 (the affine sl(2) algebra) homogeneous 3x3.
    let sl2R2 : Mat[] = Array.append (sl2 |> Array.map padOne) [| elem 3 0 2; elem 3 1 2 |]
    /// Realify a + i b (2x2 complex) as [[a, -b], [b, a]] (4x4 real).
    let realify (a: Mat) (b: Mat) : Mat =
        Array.init 4 (fun i -> Array.init 4 (fun j ->
            let ii = i % 2
            let jj = j % 2
            match i / 2, j / 2 with
            | 0, 0 | 1, 1 -> a.[ii].[jj]
            | 0, 1 -> 0.0 - b.[ii].[jj]
            | _ -> b.[ii].[jj]))
    /// u(2) = skew-Hermitian 2x2, realified: i I, i sigma_z, i sigma_x, i sigma_y.
    let u2 : Mat[] =
        let z2 = zeros 2 2
        [| realify z2 (eye 2)
           realify z2 [| [| 1.0; 0.0 |]; [| 0.0; -1.0 |] |]
           realify z2 [| [| 0.0; 1.0 |]; [| 1.0; 0.0 |] |]
           realify [| [| 0.0; 1.0 |]; [| -1.0; 0.0 |] |] z2 |]

    /// Left multiplication by the quaternion (a, b, c, d) on (1, i, j, k).
    let qmat (a: float) (b: float) (c: float) (d: float) : Mat =
        [| [| a; -b; -c; -d |]
           [| b; a; -d; c |]
           [| c; d; a; -b |]
           [| d; -c; b; a |] |]

    // ---------------------------------------------------------------------
    // Moment annihilation (RIGOR.md B1.5)
    // ---------------------------------------------------------------------

    /// Canonical tuples of Sym^k(R^d): i1 <= ... <= ik, lexicographic -- the
    /// SymIdx<k, d> storage order.
    let canonTuples (k: int) (d: int) : int[][] =
        let rec go (k: int) (lo: int) : int list list =
            if k = 0 then [ [] ]
            else [ for i in lo .. d - 1 do for rest in go (k - 1) i -> i :: rest ]
        go k 0 |> List.map Array.ofList |> Array.ofList

    /// sqrt(k! / prod n_i!): the row weight that makes canonical-row norms
    /// Frobenius norms.
    let tupleScale (t: int[]) : float =
        let counts = t |> Array.countBy id |> Array.map snd
        sqrt (factorial.[t.Length] / (counts |> Array.fold (fun acc n -> acc * factorial.[n]) 1.0))

    let flatIndex (d: int) (t: int[]) : int = t |> Array.fold (fun acc i -> acc * d + i) 0

    /// (rho_k(A) T)(i1..ik) = Sum_s Sum_j A(i_s, j) T(i1..j..ik), dense
    /// row-major flat tensors of d^k cells.
    let rhoK (k: int) (d: int) (a: Mat) (t: float[]) : float[] =
        let n = pown d k
        Array.init n (fun idx ->
            let digits = Array.zeroCreate k
            let mutable rem = idx
            for p in k - 1 .. -1 .. 0 do
                digits.[p] <- rem % d
                rem <- rem / d
            let mutable acc = 0.0
            for s in 0 .. k - 1 do
                let is = digits.[s]
                let stride = pown d (k - 1 - s)
                let baseIdx = idx - is * stride
                for j in 0 .. d - 1 do
                    acc <- acc + a.[is].[j] * t.[baseIdx + j * stride]
            acc)

    /// E[z^(x)4] for z uniform on S^2: (d_ij d_kl + d_ik d_jl + d_il d_jk) / 15.
    let iso4 : float[] =
        Array.init 81 (fun idx ->
            let i, j, k, l = idx / 27, (idx / 9) % 3, (idx / 3) % 3, idx % 3
            let dl (a: int) (b: int) = if a = b then 1 else 0
            float (dl i j * dl k l + dl i k * dl j l + dl i l * dl j k) / 15.0)

    /// E[z^(x)4] for z uniform on the octahedron's vertices {+-e_a}:
    /// (1/3) Sum_a e_a^(x)4 -- cubic anisotropy, finite stabilizer.
    let oct4 : float[] =
        Array.init 81 (fun idx ->
            let i, j, k, l = idx / 27, (idx / 9) % 3, (idx / 3) % 3, idx % 3
            if i = j && j = k && k = l then 1.0 / 3.0 else 0.0)

    /// gl(3) unknowns: E_ij, (i, j) row-major.
    let gl3Basis : Mat[] = [| for i in 0 .. 2 do for j in 0 .. 2 -> elem 3 i j |]
    /// so(3) unknowns: E_il - E_li for i < l, (0,1), (0,2), (1,2).
    let so3Basis : Mat[] = [| for i in 0 .. 2 do for l in i + 1 .. 2 -> msub (elem 3 i l) (elem 3 l i) |]

    type RowMode =
        | Scaled
        | Unscaled
        | Dense

    /// The constraint matrix of A |-> rho_k(A) M over the given unknown
    /// basis: canonical rows (scaled or not) or all d^k dense rows.
    let momentOperator (k: int) (d: int) (mom: float[]) (basis: Mat[]) (mode: RowMode) : Mat =
        let cols = basis |> Array.map (fun b -> rhoK k d b mom)
        match mode with
        | Dense -> Array.init (pown d k) (fun r -> cols |> Array.map (fun c -> c.[r]))
        | Scaled | Unscaled ->
            canonTuples k d |> Array.map (fun t ->
                let w = if mode = Scaled then tupleScale t else 1.0
                let r = flatIndex d t
                cols |> Array.map (fun c -> w * c.[r]))

    // ---------------------------------------------------------------------
    // Canonicalization to the certifier's so(3) tables (RIGOR.md B4 (viii))
    // ---------------------------------------------------------------------

    let lighthouseSpec = [ 0; 1; 1; 2 ]
    let disguiseSeed = 20261008
    let disguiseAmp = 0.3

    /// S = I + amp * (2u - 1), u ~ Random(seed) row-major, every entry
    /// rounded to 2 decimals (so the literal is exact to type).
    let disguise (d: int) : Mat =
        let rng = Random(disguiseSeed)
        Array.init d (fun i -> Array.init d (fun j ->
            let u = rng.NextDouble()
            Math.Round((if i = j then 1.0 else 0.0) + disguiseAmp * (2.0 * u - 1.0), 2)))

    type HomBlock =
        { Deg: int
          Mult: int
          TTResid: float
          Ts: Mat[] }

    type CanonReport =
        { D: int
          Target: Mat[]
          S: Mat
          SInv: Mat
          SSv: float[]
          Jt: Mat[]
          E: Mat[]
          ESv: float[]
          EC: float[][][]
          Closure: float
          KappaE: Mat
          KappaEEig: float[]
          OmegaNullR: int
          OmegaNullL: int
          OmegaSv: float[]
          M: Mat
          MEig: float[]
          MInvResid: float
          R: Mat
          RInv: Mat
          LGen: Mat[]
          LAntisym: float
          LBracket: float
          KQ: Mat
          KLam: float[]
          C123: float
          J: Mat[]
          JAntisym: float
          JBracket: float
          JKappa: Mat
          Casimir: Mat
          CasimirEig: float[]
          Blocks: HomBlock list
          U: Mat
          UOrth: float
          UJU: Mat[]
          Resid: float }

    let bracketResidual (j: Mat[]) : float =
        [ maxDiffM (bracket j.[0] j.[1]) j.[2]
          maxDiffM (bracket j.[1] j.[2]) j.[0]
          maxDiffM (bracket j.[2] j.[0]) j.[1] ] |> List.max

    /// check_b4's pipeline on J~_a = S J_a S^-1 for the block-diagonal
    /// 0 + 1 + 1 + 2 representation (d = 12).
    let canonicalize () : CanonReport =
        let target = Array.init 3 (fun a -> blockDiag (lighthouseSpec |> List.map (table a)))
        let d = target.[0].Length
        let s = disguise d
        let sinv = solve s (eye d)
        let _, sSv, _ = svdJ s
        let jt = target |> Array.map (fun j -> matMul (matMul s j) sinv)
        // (0) Frobenius-orthonormal basis of span{J~}; structure constants; Killing
        let e, eSv = orthonormalBasis jt
        let c = structureConstantsOn e
        let closure = closureResidual e c
        let kap = killing c
        let _, kapEig = eighJ kap
        // (i) invariant metric: spectral projector of the Casimir on bilinear forms,
        //     Omega = -2 Sum_ij kinv_ij rhoW(E_i) rhoW(E_j), rhoW(E)(S) = -(E^T S + S E)
        let kinv = solve kap (eye 3)
        let idm = eye d
        let rhoW (m: Mat) = mscale -1.0 (madd (kron (transpose m) idm) (kron idm (transpose m)))
        let rw = e |> Array.map rhoW
        let mutable omega = zeros (d * d) (d * d)
        for i in 0 .. 2 do
            for j in 0 .. 2 do
                omega <- madd omega (mscale (-2.0 * kinv.[i].[j]) (matMul rw.[i] rw.[j]))
        let nr, omegaSv = nullSpace 1e-9 omega
        let nl, _ = nullSpace 1e-9 (transpose omega)
        let nrM = transpose nr
        let g = matMul nl nrM
        let rhs = matMul nl (vecRM idm |> Array.map (fun x -> [| x |]))
        let coef = solve g rhs
        let mvec = matMul nrM coef |> Array.map (fun r -> r.[0])
        let m = symmetrize (unvec d d mvec)
        let _, mEig = eighJ m
        let mInv = jt |> Array.map (fun j -> maxAbsM (madd (matMul (transpose j) m) (matMul m j))) |> Array.max
        // (ii) R = M^(1/2); L_a = R J~_a R^-1 antisymmetric
        let r = symFun sqrt m
        let rinv = symFun (fun x -> 1.0 / sqrt x) m
        let lm = jt |> Array.map (fun j -> matMul (matMul r j) rinv)
        let lAnti = lm |> Array.map (fun x -> maxAbsM (madd x (transpose x))) |> Array.max
        let lBr = bracketResidual lm
        // (iii) (-kappa/2)-orthonormal basis of R E_i R^-1, orientation c_12^3 > 0
        let et = e |> Array.map (fun x -> matMul (matMul r x) rinv)
        let kq, klam = eighJ (mscale -0.5 kap)
        let jf0 = Array.init 3 (fun i -> combo (Array.init 3 (fun j -> kq.[j].[i] / sqrt klam.[i])) et)
        let c123 = frob jf0.[2] (bracket jf0.[0] jf0.[1]) / frob jf0.[2] jf0.[2]
        let jf = if c123 < 0.0 then jf0 |> Array.map (mscale -1.0) else jf0
        let jAnti = jf |> Array.map (fun x -> maxAbsM (madd x (transpose x))) |> Array.max
        let jBr = bracketResidual jf
        let jKappa = killing (structureConstants jf)
        // (iv) Casimir split
        let cas = symmetrize (casimirSum jf)
        let _, casEig = eighJ cas
        // (v) intertwiners T J_a = D_a^(l) T per l, Loewdin-orthonormalized, scaled by sqrt(2l+1)
        let blocks =
            lighthouseSpec |> List.distinct |> List.sort |> List.map (fun l ->
                let n = 2 * l + 1
                let amat =
                    Array.init 3 (fun a -> msub (kron (eye n) (transpose jf.[a])) (kron (table a l) idm))
                    |> Array.concat
                let nulls, _ = nullSpace 1e-8 amat
                let ts = nulls |> Array.map (unvec n d)
                let k = ts.Length
                let gram = Array.init k (fun a -> Array.init k (fun b -> frob ts.[a] ts.[b]))
                let w = symFun (fun x -> 1.0 / sqrt x) gram
                let tsN = Array.init k (fun i -> mscale (sqrt (float n)) (combo w.[i] ts))
                let tt =
                    tsN |> Array.map (fun t -> maxDiffM (matMul t (transpose t)) (eye n)) |> Array.fold max 0.0
                { Deg = l; Mult = k; TTResid = tt; Ts = tsN })
        let u = blocks |> List.collect (fun b -> List.ofArray b.Ts) |> Array.concat
        let uOrth = maxDiffM (matMul u (transpose u)) idm
        let sortedSpec = List.sort lighthouseSpec
        let tgtSorted = Array.init 3 (fun a -> blockDiag (sortedSpec |> List.map (table a)))
        let uju = jf |> Array.map (fun x -> matMul (matMul u x) (transpose u))
        let resid = Array.map2 maxDiffM uju tgtSorted |> Array.max
        { D = d; Target = tgtSorted; S = s; SInv = sinv; SSv = sSv; Jt = jt; E = e; ESv = eSv
          EC = c; Closure = closure; KappaE = kap; KappaEEig = kapEig
          OmegaNullR = nr.Length; OmegaNullL = nl.Length; OmegaSv = omegaSv
          M = m; MEig = mEig; MInvResid = mInv; R = r; RInv = rinv; LGen = lm; LAntisym = lAnti; LBracket = lBr
          KQ = kq; KLam = klam; C123 = c123; J = jf; JAntisym = jAnti; JBracket = jBr; JKappa = jKappa
          Casimir = cas; CasimirEig = casEig; Blocks = blocks; U = u; UOrth = uOrth; UJU = uju; Resid = resid }

    // ---------------------------------------------------------------------
    // Functional (invariance) lane (RIGOR.md B3.2, B = 0)
    // ---------------------------------------------------------------------

    type FunctionalReport =
        { Rows: Mat
          Sv: float[]
          Null: Mat[]
          Proj: Mat
          Report: AlgebraReport option }

    /// Rows vec(grad f(x_n) x_n^T) (row-major); singular values over the
    /// 9-dim gl(3) unknown space (zero-row padded); the nullspace as 3x3
    /// generators, its (basis-free) orthogonal projector, and the algebra
    /// analysis of the nullspace.
    let functionalLane (seed: int) (grad: float[] -> float[]) (pts: Mat) : FunctionalReport =
        let rows =
            pts |> Array.map (fun x ->
                let g = grad x
                [| for i in 0 .. 2 do for j in 0 .. 2 -> g.[i] * x.[j] |])
        let nulls, sv = nullSpace rankTol rows
        let proj =
            Array.init 9 (fun p -> Array.init 9 (fun q ->
                nulls |> Array.sumBy (fun v -> v.[p] * v.[q])))
        let gens = nulls |> Array.map (unvec 3 3)
        let rep = if gens.Length = 0 then None else Some (analyze seed gens)
        { Rows = rows; Sv = sv; Null = gens; Proj = proj; Report = rep }

    /// The exact so(2,1) projector on gl(3): Sum_b vec(b) vec(b)^T / |b|^2
    /// over the Frobenius-orthogonal basis `so21`.
    let so21Projector : Mat =
        let vs = so21 |> Array.map vecRM
        Array.init 9 (fun p -> Array.init 9 (fun q ->
            vs |> Array.sumBy (fun v -> v.[p] * v.[q] / 2.0)))

    /// Eight fixed sample points in R^3 (generic: unisolvent for quadratics).
    let functionalPoints : Mat =
        [| [| 1.0; 0.5; -0.3 |]
           [| -0.7; 1.2; 0.4 |]
           [| 0.3; -0.8; 1.1 |]
           [| 1.5; 0.2; 0.9 |]
           [| -0.4; -1.3; 0.6 |]
           [| 0.8; 0.7; -1.2 |]
           [| -1.1; 0.1; -0.5 |]
           [| 0.2; 1.4; 0.3 |] |]
