namespace MomentAlgebra

/// Prototype 1: Dist — the cumulant numeric tower. A d-dimensional random
/// value represented by its joint cumulant tensors kappa_1 .. kappa_r
/// (each rank-k symmetric, packed). The tower's laws, all EXACT:
///
///   X + Y (independent)  -> tensor addition of cumulants
///   c * X                -> kappa_k scales by c^k
///   A X + b (affine)     -> kappa_k contracts with A^(tensor k); b only moves kappa_1
///   polynomial P(X)      -> exact up to the carried order via moment expansion
///
/// Independence between coordinates = structural zeros in every mixed entry
/// (idea 2: independence as sparsity). The Order field is the prototype's
/// stand-in for a type-level truncation parameter Dist<r>.
module Dist =

    type T = {
        Dim: int
        Order: int
        Kappa: SymTensor.T[]   // Kappa.[k-1] : rank-k cumulant tensor
    }

    let create (d: int) (r: int) : T =
        { Dim = d; Order = r; Kappa = [| for k in 1 .. r -> SymTensor.create d k |] }

    // ---- scalar marginal constructors: cumulant sequences kappa_1..kappa_r ----

    let gaussianCumulants (mean: float) (variance: float) (r: int) : float[] =
        Array.init r (fun i -> if i = 0 then mean elif i = 1 then variance else 0.0)

    /// Exp(rate): kappa_k = (k-1)! / rate^k
    let exponentialCumulants (rate: float) (r: int) : float[] =
        Array.init r (fun i -> Combinatorics.factorial i / rate ** float (i + 1))

    /// Gamma(shape, rate): kappa_k = shape * (k-1)! / rate^k
    let gammaCumulants (shape: float) (rate: float) (r: int) : float[] =
        Array.init r (fun i -> shape * Combinatorics.factorial i / rate ** float (i + 1))

    /// Poisson(lambda): every cumulant equals lambda
    let poissonCumulants (lam: float) (r: int) : float[] = Array.create r lam

    /// Joint distribution of independent scalar marginals. Cross-cumulants are
    /// structural zeros; only diagonal entries kappa(i,i,...,i) are populated.
    let ofIndependent (marginals: float[][]) (r: int) : T =
        let d = marginals.Length
        let t = create d r
        for i in 0 .. d - 1 do
            for k in 1 .. r do
                SymTensor.set t.Kappa.[k - 1] (Array.create k i) marginals.[i].[k - 1]
        t

    /// Sum of INDEPENDENT variables: cumulants add. This is the whole reason
    /// cumulants are the right basis — convolution becomes tensor addition.
    let addIndependent (a: T) (b: T) : T =
        if a.Dim <> b.Dim then failwith "addIndependent: dimension mismatch"
        let r = min a.Order b.Order
        { Dim = a.Dim
          Order = r
          Kappa = Array.init r (fun k -> SymTensor.map2 (+) a.Kappa.[k] b.Kappa.[k]) }

    let scale (c: float) (dist: T) : T =
        { dist with Kappa = dist.Kappa |> Array.mapi (fun k t -> SymTensor.scale (c ** float (k + 1)) t) }

    /// Exact pushforward through y = A x + b (A jagged, m x d). Cumulants are
    /// multilinear: kappa_Y(j_1..j_k) = sum_i A[j_1,i_1]..A[j_k,i_k] kappa_X(i).
    /// The prototype enumerates all d^k source tuples for clarity; Blade's
    /// codegen would walk canonical entries with the joint-r! multiplicity.
    let affine (A: float[][]) (b: float[]) (dist: T) : T =
        let m = A.Length
        let d = dist.Dim
        let out = create m dist.Order
        for k in 1 .. dist.Order do
            let src = dist.Kappa.[k - 1]
            let dst = out.Kappa.[k - 1]
            let idx = Array.zeroCreate k
            for outLabels in SymTensor.enumerate m k do
                let mutable total = 0.0
                let rec go pos (acc: float) =
                    if pos = k then total <- total + acc * SymTensor.get src idx
                    else
                        for i in 0 .. d - 1 do
                            idx.[pos] <- i
                            go (pos + 1) (acc * A.[outLabels.[pos]].[i])
                go 0 1.0
                SymTensor.set dst outLabels total
        for j in 0 .. m - 1 do
            SymTensor.set out.Kappa.[0] [| j |] (SymTensor.get out.Kappa.[0] [| j |] + b.[j])
        out

    /// Raw (non-central) joint moment tensors, orders 1..r.
    let moments (dist: T) : SymTensor.T[] =
        MomentCumulant.momentsFromCumulants dist.Kappa

    let private ofUnivariateMoments (mu: float[]) : T =
        let r = mu.Length
        let muT =
            [| for k in 1 .. r ->
                 let s = SymTensor.create 1 k
                 s.Data.[0] <- mu.[k - 1]
                 s |]
        { Dim = 1; Order = r; Kappa = MomentCumulant.cumulantsFromMoments muT }

    /// Product of independent SCALAR variables: raw moments multiply orderwise
    /// (E[(XY)^k] = E[X^k] E[Y^k]), then convert back to cumulants.
    let mulIndependent1D (a: T) (b: T) : T =
        if a.Dim <> 1 || b.Dim <> 1 then failwith "mulIndependent1D: univariate only"
        let r = min a.Order b.Order
        let ma = MomentCumulant.momentsFromCumulants a.Kappa.[0 .. r - 1]
        let mb = MomentCumulant.momentsFromCumulants b.Kappa.[0 .. r - 1]
        ofUnivariateMoments (Array.init r (fun k -> ma.[k].Data.[0] * mb.[k].Data.[0]))

    /// Exact polynomial pushforward (the exact fragment of idea 4):
    /// Y = sum_t coeff_t * prod_i X_i^(alpha_t_i), returned as a scalar Dist of
    /// order q. Needs joint moments of X up to q * max total degree — enforced
    /// here at runtime; in Blade this is the type-level "insufficient
    /// stochastic order" error, caught before any kernel is emitted.
    /// Full Faà di Bruno pushforward, scalar output: Y = g(X) for a smooth g
    /// supplied as its degree-s JET AT THE MEAN — g0 = g(μ) and
    /// derivs.[k-1] = the rank-k symmetric derivative tensor g^(k)(μ).
    /// Y − g0 is the Taylor polynomial in Z = X − μ, so raw moments of
    /// Y' = Y − g0 expand over compositions of m into jet degrees,
    /// derivative reads contracted with CENTRAL moment tensors of X
    /// (partition sums over κ with every block of size ≥ 2 — singleton
    /// blocks vanish because κ_1(Z) = 0); univariate Möbius inversion
    /// returns cumulants and κ_1 shifts back by g0.
    ///
    /// Exact when g is a polynomial of degree ≤ s (its jet is finite) and
    /// q·s ≤ the carried order. `closed = false` demands that budget — the
    /// "insufficient stochastic order" contract; `closed = true` instead
    /// zero-fills cumulants beyond the carried order (the moments(d,k)
    /// closure convention: partition blocks larger than Order are dropped).
    let jetPushforward (dist: T) (g0: float) (derivs: SymTensor.T[]) (q: int) (closed: bool) : T =
        let d = dist.Dim
        let s = derivs.Length
        if s < 1 then failwith "jetPushforward: the jet needs at least D_1"
        derivs |> Array.iteri (fun i dk ->
            if dk.Dim <> d || dk.Rank <> i + 1 then
                failwithf "jetPushforward: D_%d must be a rank-%d symmetric tensor over dim %d" (i + 1) (i + 1) d)
        let tMax = q * s
        if not closed && tMax > dist.Order then
            failwithf "jetPushforward: needs input moments up to order %d but Dist carries order %d (closure required)"
                tMax dist.Order
        // central moment tensors of X, orders 1..tMax (order 1 = zeros)
        let cm =
            [| for t in 1 .. tMax ->
                 let out = SymTensor.create d t
                 if t >= 2 then
                     let parts =
                         Combinatorics.setPartitions t
                         |> Array.filter (fun p ->
                             p |> Array.forall (fun b -> b.Length >= 2 && b.Length <= dist.Order))
                     for labels in SymTensor.enumerate d t do
                         let mutable acc = 0.0
                         for p in parts do
                             let mutable prod = 1.0
                             for b in p do
                                 let sub = b |> Array.map (fun pos -> labels.[pos]) |> Array.sort
                                 prod <- prod * SymTensor.get dist.Kappa.[b.Length - 1] sub
                             acc <- acc + prod
                         SymTensor.set out labels acc
                 out |]
        // raw moments of Y' = Y − g0: multinomial over jet-degree compositions
        let rawY =
            Array.init q (fun mi ->
                let m = mi + 1
                let mutable total = 0.0
                for comp in Combinatorics.compositions m s do
                    // comp.[k-1] factors take jet degree k; total Z-degree t
                    let t = comp |> Array.mapi (fun i c -> (i + 1) * c) |> Array.sum
                    if t >= 2 then   // t = 1 ⇒ D_1·E[Z] = 0; t = 0 impossible for m ≥ 1
                        let mutable w = Combinatorics.factorial m
                        for i in 0 .. s - 1 do
                            w <- w / Combinatorics.factorial comp.[i]
                            w <- w / (Combinatorics.factorial (i + 1) ** float comp.[i])
                        let degs = [| for i in 0 .. s - 1 do for _ in 1 .. comp.[i] -> i + 1 |]
                        let labels = Array.zeroCreate t
                        let mutable sum = 0.0
                        // per factor: enumerate its k labels, read D_k; at the
                        // leaf read the central moment of ALL t labels
                        let rec go (fi: int) (pos: int) (acc: float) =
                            if fi = degs.Length then
                                sum <- sum + acc * SymTensor.get cm.[t - 1] labels
                            else
                                let k = degs.[fi]
                                let rec fill (j: int) =
                                    if j = k then
                                        let seg = Array.sub labels pos k
                                        let dv = SymTensor.get derivs.[k - 1] seg
                                        if dv <> 0.0 then go (fi + 1) (pos + k) (acc * dv)
                                    else
                                        for lab in 0 .. d - 1 do
                                            labels.[pos + j] <- lab
                                            fill (j + 1)
                                fill 0
                        go 0 0 1.0
                        total <- total + w * sum
                total)
        let out = ofUnivariateMoments rawY
        SymTensor.set out.Kappa.[0] [| 0 |] (SymTensor.get out.Kappa.[0] [| 0 |] + g0)
        out

    let polyMoments (dist: T) (terms: (float * int[]) list) (q: int) : T =
        let maxDeg = terms |> List.map (fun (_, a) -> Array.sum a) |> List.max
        if q * maxDeg > dist.Order then
            failwithf "polyMoments: needs input moments up to order %d but Dist carries order %d (closure required)"
                (q * maxDeg) dist.Order
        let mu = moments dist
        let termArr = List.toArray terms
        let t = termArr.Length
        let rawY =
            Array.init q (fun mi ->
                let m = mi + 1
                let mutable total = 0.0
                for comp in Combinatorics.compositions m t do
                    let mutable w = Combinatorics.factorial m
                    for c in comp do w <- w / Combinatorics.factorial c
                    let mutable coefProd = 1.0
                    let labels = ResizeArray<int>()
                    for ti in 0 .. t - 1 do
                        let (c0, alpha) = termArr.[ti]
                        coefProd <- coefProd * c0 ** float comp.[ti]
                        for i in 0 .. alpha.Length - 1 do
                            for _ in 1 .. alpha.[i] * comp.[ti] do labels.Add i
                    let momentVal =
                        if labels.Count = 0 then 1.0
                        else SymTensor.get mu.[labels.Count - 1] (labels.ToArray())
                    total <- total + w * coefProd * momentVal
                total)
        ofUnivariateMoments rawY
