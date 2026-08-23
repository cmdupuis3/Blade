module MomentAlgebra.Program

open MomentAlgebra
open MomentAlgebra.TestHarness

/// Univariate Dist from a cumulant sequence.
let univariate (cum: float[]) : Dist.T =
    Dist.ofIndependent [| cum |] cum.Length

/// The packed cumulant sequence of a univariate Dist.
let univCumulants (dist: Dist.T) : float[] =
    Array.init dist.Order (fun k -> dist.Kappa.[k].Data.[0])

let fmtLabels (labels: int[]) =
    labels |> Array.map string |> String.concat ","

// ---------------------------------------------------------------------------

let testCombinatorics () =
    section "combinatorics"
    let bells = [| 1; 2; 5; 15; 52; 203 |]
    for n in 1 .. 6 do
        check $"Bell({n}) = {bells.[n - 1]}" (Combinatorics.bell n = bells.[n - 1])
    check "C(8,3) = 56" (Combinatorics.binomial 8 3 = 56)
    check "C(6,4) = 15" (Combinatorics.binomial 6 4 = 15)
    checkClose "6!" 1e-9 720.0 (Combinatorics.factorial 6)
    check "compositions(3,2) has 4" (List.length (Combinatorics.compositions 3 2) = 4)

let testSymTensor () =
    section "symtensor packing"
    check "storage d=3 r=4 = 15" (SymTensor.storageSize 3 4 = 15)
    check "storage d=2 r=3 = 4"  (SymTensor.storageSize 2 3 = 4)
    // rankOf over the canonical enumeration is a bijection onto 0..N-1
    for (d, r) in [ (3, 4); (4, 3); (2, 6) ] do
        let ranks = SymTensor.enumerate d r |> Array.map SymTensor.rankOf |> Array.sort
        check $"rank bijection d={d} r={r}"
              (ranks = [| 0 .. SymTensor.storageSize d r - 1 |])
    // sum of joint-r! multiplicities over canonical entries recovers the dense count
    for (d, r) in [ (3, 4); (2, 5) ] do
        let total = SymTensor.enumerate d r |> Array.sumBy SymTensor.multiplicity
        checkClose $"sum multiplicities d={d} r={r} = d^r" 1e-9 (float d ** float r) total
    // symmetric access: any permutation hits the same entry
    let t = SymTensor.create 3 3
    SymTensor.set t [| 2; 0; 1 |] 7.5
    checkClose "get permuted index" 1e-12 7.5 (SymTensor.get t [| 1; 2; 0 |])

let testMomentCumulant () =
    section "moment <-> cumulant (partition lattice)"
    // Round trip on random joint cumulants, d=2, r=5
    let rng = System.Random(11)
    let kappa =
        [| for k in 1 .. 5 ->
             let t = SymTensor.create 2 k
             for i in 0 .. t.Data.Length - 1 do t.Data.[i] <- rng.NextDouble() * 2.0 - 1.0
             t |]
    let back = MomentCumulant.cumulantsFromMoments (MomentCumulant.momentsFromCumulants kappa)
    for k in 1 .. 5 do
        checkArrayClose $"round trip rank {k}" 1e-9 kappa.[k - 1].Data back.[k - 1].Data
    // Isserlis: zero-mean Gaussian pair, rho = 0.5 -- only pairings survive
    let g = Dist.create 2 4
    SymTensor.set g.Kappa.[1] [| 0; 0 |] 1.0
    SymTensor.set g.Kappa.[1] [| 0; 1 |] 0.5
    SymTensor.set g.Kappa.[1] [| 1; 1 |] 1.0
    let mu = Dist.moments g
    checkClose "Isserlis E[X^2]"    1e-12 1.0 (SymTensor.get mu.[1] [| 0; 0 |])
    checkClose "Isserlis E[X^4]"    1e-12 3.0 (SymTensor.get mu.[3] [| 0; 0; 0; 0 |])
    checkClose "Isserlis E[X^3 Y]"  1e-12 1.5 (SymTensor.get mu.[3] [| 0; 0; 0; 1 |])
    checkClose "Isserlis E[X^2 Y^2]" 1e-12 1.5 (SymTensor.get mu.[3] [| 0; 0; 1; 1 |])
    checkClose "Isserlis E[X^3]"    1e-12 0.0 (SymTensor.get mu.[2] [| 0; 0; 0 |])
    // Poisson(2) raw moments: Touchard/Stirling -- 2, 6, 22, 94
    let pois = univariate (Dist.poissonCumulants 2.0 4)
    let pm = Dist.moments pois |> Array.map (fun t -> t.Data.[0])
    checkArrayClose "Poisson(2) raw moments" 1e-9 [| 2.0; 6.0; 22.0; 94.0 |] pm

let testDistTower () =
    section "prototype 1: Dist numeric tower"
    let exp1 = univariate (Dist.exponentialCumulants 1.0 4)
    // Exp(1) + Exp(1) = Gamma(2,1): convolution is cumulant ADDITION, exact
    let erlang = Dist.addIndependent exp1 exp1
    checkArrayClose "Exp+Exp = Gamma(2,1)" 1e-12 (Dist.gammaCumulants 2.0 1.0 4) (univCumulants erlang)
    // 2 * Exp(1) = Exp(1/2): kappa_k scales by 2^k
    checkArrayClose "2*Exp(1) = Exp(0.5)" 1e-12 (Dist.exponentialCumulants 0.5 4) (univCumulants (Dist.scale 2.0 exp1))
    // Affine mixing of independent Exp(1) and Poisson(3), hand-checked entries
    let z = Dist.ofIndependent [| Dist.exponentialCumulants 1.0 4; Dist.poissonCumulants 3.0 4 |] 4
    // independence = structural zeros in every mixed cumulant entry
    check "cross kappa2 exactly 0" (SymTensor.get z.Kappa.[1] [| 0; 1 |] = 0.0)
    check "cross kappa3 exactly 0" (SymTensor.get z.Kappa.[2] [| 0; 0; 1 |] = 0.0)
    check "cross kappa4 exactly 0" (SymTensor.get z.Kappa.[3] [| 0; 1; 1; 1 |] = 0.0)
    let a = [| [| 1.0; 1.0 |]; [| 1.0; -1.0 |] |]
    let y = Dist.affine a [| 0.0; 10.0 |] z
    checkClose "affine k1(0)" 1e-12 4.0 (SymTensor.get y.Kappa.[0] [| 0 |])
    checkClose "affine k1(1)" 1e-12 8.0 (SymTensor.get y.Kappa.[0] [| 1 |])
    checkClose "affine k2(0,0)" 1e-12 4.0  (SymTensor.get y.Kappa.[1] [| 0; 0 |])
    checkClose "affine k2(0,1)" 1e-12 -2.0 (SymTensor.get y.Kappa.[1] [| 0; 1 |])
    checkClose "affine k2(1,1)" 1e-12 4.0  (SymTensor.get y.Kappa.[1] [| 1; 1 |])
    checkClose "affine k3(0,0,0)" 1e-12 5.0  (SymTensor.get y.Kappa.[2] [| 0; 0; 0 |])
    checkClose "affine k3(0,0,1)" 1e-12 -1.0 (SymTensor.get y.Kappa.[2] [| 0; 0; 1 |])
    checkClose "affine k3(0,1,1)" 1e-12 5.0  (SymTensor.get y.Kappa.[2] [| 0; 1; 1 |])
    checkClose "affine k3(1,1,1)" 1e-12 -1.0 (SymTensor.get y.Kappa.[2] [| 1; 1; 1 |])
    checkClose "affine k4(0,0,0,0)" 1e-12 9.0 (SymTensor.get y.Kappa.[3] [| 0; 0; 0; 0 |])
    checkClose "affine k4(0,0,0,1)" 1e-12 3.0 (SymTensor.get y.Kappa.[3] [| 0; 0; 0; 1 |])
    checkClose "affine k4(0,0,1,1)" 1e-12 9.0 (SymTensor.get y.Kappa.[3] [| 0; 0; 1; 1 |])
    checkClose "affine k4(1,1,1,1)" 1e-12 9.0 (SymTensor.get y.Kappa.[3] [| 1; 1; 1; 1 |])
    // Product of independent Exp(1)s: E[(XY)^k] = (k!)^2
    let prod = Dist.mulIndependent1D exp1 exp1
    let prodMoments = Dist.moments prod |> Array.map (fun t -> t.Data.[0])
    checkArrayClose "Exp*Exp raw moments (k!)^2" 1e-9 [| 1.0; 4.0; 36.0; 576.0 |] prodMoments
    let pc = univCumulants prod
    checkClose "Exp*Exp kappa2 = 3"   1e-9 3.0   pc.[1]
    checkClose "Exp*Exp kappa3 = 26"  1e-9 26.0  pc.[2]
    checkClose "Exp*Exp kappa4 = 426" 1e-9 426.0 pc.[3]
    // Exact polynomial pushforward: Y = Z0*Z1 for iid standard normals.
    // kappa(Y) = [0; 1; 0; 6] -- the classic product-normal excess kurtosis.
    let zn = Dist.ofIndependent [| Dist.gaussianCumulants 0.0 1.0 8; Dist.gaussianCumulants 0.0 1.0 8 |] 8
    let prodN = Dist.polyMoments zn [ (1.0, [| 1; 1 |]) ] 4
    checkArrayClose "poly Z0*Z1 cumulants" 1e-9 [| 0.0; 1.0; 0.0; 6.0 |] (univCumulants prodN)
    // The "insufficient stochastic order" error: q*deg exceeds carried order
    let zSmall = Dist.ofIndependent [| Dist.gaussianCumulants 0.0 1.0 4; Dist.gaussianCumulants 0.0 1.0 4 |] 4
    checkThrows "poly order guard" (fun () -> Dist.polyMoments zSmall [ (1.0, [| 1; 1 |]) ] 4 |> ignore)

/// Empirical raw moments (1/N normalization, matching the generated prodsum
/// kernels), ranks 1..rmax. `data.[v].[t]` mirrors the corpus arrays'
/// Array<F like Idx<d>, TimeIdx<N>> layout (variable-major, sample = last
/// axis). Shared by the dump oracles, the free-cumulant recursion, and the
/// jet-pushforward empirical tests, all of which need the raw mu tensors.
let private computeMoments (data: float[][]) (rmax: int) : SymTensor.T[] =
    let d = data.Length
    let n = data.[0].Length
    [| for k in 1 .. rmax ->
         let t = SymTensor.create d k
         for labels in SymTensor.enumerate d k do
             let mutable acc = 0.0
             for s in 0 .. n - 1 do
                 let mutable prod = 1.0
                 for v in labels do prod <- prod * data.[v].[s]
                 acc <- acc + prod
             SymTensor.set t labels (acc / float n)
         t |]

let private computeCumulants (data: float[][]) (rmax: int) : SymTensor.T[] =
    computeMoments data rmax |> MomentCumulant.cumulantsFromMoments

/// Univariate jet: vals.[k-1] = g^(k)(mu), packed as dim-1 rank-k tensors.
let univJet (vals: float[]) : SymTensor.T[] =
    vals |> Array.mapi (fun i v ->
        let t = SymTensor.create 1 (i + 1)
        t.Data.[0] <- v
        t)

let testJetPushforward () =
    section "jet pushforward (full Faa di Bruno, scalar output)"
    // 1) Exact jet of g(x,y) = x*y at mu = (0,0) on iid standard normals:
    //    the product-normal cumulants -- agrees with polyMoments exactly.
    let zn = Dist.ofIndependent [| Dist.gaussianCumulants 0.0 1.0 8; Dist.gaussianCumulants 0.0 1.0 8 |] 8
    let d1 = SymTensor.create 2 1
    let d2 = SymTensor.create 2 2
    SymTensor.set d2 [| 0; 1 |] 1.0
    let viaJet = Dist.jetPushforward zn 0.0 [| d1; d2 |] 4 false
    checkArrayClose "jet x*y on iid normals = [0,1,0,6]" 1e-9 [| 0.0; 1.0; 0.0; 6.0 |] (univCumulants viaJet)
    // 2) g(x) = x^2 on Gamma(3,2): jet (mu^2, 2mu, 2) vs the exact polynomial pushforward.
    let gam = univariate (Dist.gammaCumulants 3.0 2.0 6)
    let mu = gam.Kappa.[0].Data.[0]
    let viaJet2 = Dist.jetPushforward gam (mu * mu) (univJet [| 2.0 * mu; 2.0 |]) 3 false
    let viaPoly2 = Dist.polyMoments gam [ (1.0, [| 2 |]) ] 3
    checkArrayClose "jet x^2 = poly x^2 (Gamma(3,2))" 1e-9 (univCumulants viaPoly2) (univCumulants viaJet2)
    // 3) g(x) = x^3 + 2x on Exp(1): mixed-degree jet at mu = 1 vs polyMoments.
    let ex = univariate (Dist.exponentialCumulants 1.0 6)
    let viaJet3 = Dist.jetPushforward ex 3.0 (univJet [| 5.0; 6.0; 6.0 |]) 2 false
    let viaPoly3 = Dist.polyMoments ex [ (1.0, [| 3 |]); (2.0, [| 1 |]) ] 2
    checkArrayClose "jet x^3+2x = poly (Exp(1))" 1e-9 (univCumulants viaPoly3) (univCumulants viaJet3)
    // 4) A 1-jet IS the affine map: 2X + 10 on Exp(1) -- note g0 = g(mu) = 12,
    //    not the intercept (the jet is anchored at the mean).
    let ex4 = univariate (Dist.exponentialCumulants 1.0 4)
    let viaJet4 = Dist.jetPushforward ex4 12.0 (univJet [| 2.0 |]) 4 false
    let viaAffine = Dist.affine [| [| 2.0 |] |] [| 10.0 |] ex4
    checkArrayClose "1-jet = affine (2X+10 on Exp(1))" 1e-12 (univCumulants viaAffine) (univCumulants viaJet4)
    // 5) Closure is exact when the dropped cumulants are truly zero:
    //    N(1,2) carried at order 2 (closed) vs carried at order 4 (strict).
    let g2 = univariate (Dist.gaussianCumulants 1.0 2.0 2)
    let g4 = univariate (Dist.gaussianCumulants 1.0 2.0 4)
    let jetSq = univJet [| 2.0; 2.0 |]
    let closed2 = Dist.jetPushforward g2 1.0 jetSq 2 true
    let strict4 = Dist.jetPushforward g4 1.0 jetSq 2 false
    checkArrayClose "Gaussian closure = strict (x^2, N(1,2))" 1e-12 (univCumulants strict4) (univCumulants closed2)
    // 6) The strict order guard: q*s exceeds the carried order.
    checkThrows "jet order guard" (fun () -> Dist.jetPushforward g2 1.0 jetSq 2 false |> ignore)
    // 7) THE EMPIRICAL-DISTRIBUTION IDENTITY: pushing the empirical dist of
    //    the data through an exact polynomial jet equals the empirical
    //    cumulants of the transformed data -- the property the compiler's
    //    two-route corpus test pins.
    let a1 = [| 1.0; 2.0; 4.0; 6.0; 0.0; 3.0 |]
    let distA1 : Dist.T = { Dim = 1; Order = 6; Kappa = computeCumulants [| a1 |] 6 }
    let m = distA1.Kappa.[0].Data.[0]
    let pushed = Dist.jetPushforward distA1 (m * m) (univJet [| 2.0 * m; 2.0 |]) 3 false
    let direct : Dist.T = { Dim = 1; Order = 3; Kappa = computeCumulants [| a1 |> Array.map (fun x -> x * x) |] 3 }
    checkArrayClose "empirical push x^2 = cumulants of squared data" 1e-9 (univCumulants direct) (univCumulants pushed)

let testJetPushforwardVec () =
    section "vector jet pushforward (mixed-block Faa di Bruno, joint output cumulants)"
    // The running map: g(x,y) = (x + y, x*y) on the empirical dist of data B.
    let b = [| [| 1.0; 2.0; 4.0 |]; [| 3.0; 5.0; 4.0 |] |]
    let distB6 : Dist.T = { Dim = 2; Order = 6; Kappa = computeCumulants b 6 }
    let mx = SymTensor.get distB6.Kappa.[0] [| 0 |]
    let my = SymTensor.get distB6.Kappa.[0] [| 1 |]
    // coordinate 0 (x + y): degree-1 jet -- deliberately RAGGED (s_0 = 1)
    let d1sum = SymTensor.create 2 1
    SymTensor.set d1sum [| 0 |] 1.0
    SymTensor.set d1sum [| 1 |] 1.0
    // coordinate 1 (x*y): D1 = [dx, dy] = [y, x] at the mean; D2 = dxdy = 1
    let d1prod = SymTensor.create 2 1
    SymTensor.set d1prod [| 0 |] my
    SymTensor.set d1prod [| 1 |] mx
    let d2prod = SymTensor.create 2 2
    SymTensor.set d2prod [| 0; 1 |] 1.0
    let g0 = [| mx + my; mx * my |]
    let jets = [| [| d1sum |]; [| d1prod; d2prod |] |]
    let vec = Dist.jetPushforwardVec distB6 g0 jets 3 false
    // 1) m = 1 regression: the vec path with one coordinate IS jetPushforward.
    let a1 = [| 1.0; 2.0; 4.0; 6.0; 0.0; 3.0 |]
    let distA1 : Dist.T = { Dim = 1; Order = 6; Kappa = computeCumulants [| a1 |] 6 }
    let m1 = SymTensor.get distA1.Kappa.[0] [| 0 |]
    let scalarJet = Dist.jetPushforward distA1 (m1 * m1) (univJet [| 2.0 * m1; 2.0 |]) 3 false
    let vecOne = Dist.jetPushforwardVec distA1 [| m1 * m1 |] [| univJet [| 2.0 * m1; 2.0 |] |] 3 false
    checkArrayClose "m=1 vec = scalar jet (x^2 on A1)" 1e-12 (univCumulants scalarJet) (univCumulants vecOne)
    // 2) The linear coordinate's marginal tower = the affine pushforward.
    let viaAffine = Dist.affine [| [| 1.0; 1.0 |] |] [| 0.0 |] distB6
    let sumMarginal = Array.init 3 (fun k -> SymTensor.get vec.Kappa.[k] (Array.create (k + 1) 0))
    checkArrayClose "vec coord 0 marginal = affine x+y" 1e-9 (univCumulants viaAffine |> Array.take 3) sumMarginal
    // 3) The product coordinate's marginal diagonal = the scalar J2 jet.
    let scalarProd = Dist.jetPushforward distB6 (mx * my) [| d1prod; d2prod |] 3 false
    let prodMarginal = Array.init 3 (fun k -> SymTensor.get vec.Kappa.[k] (Array.create (k + 1) 1))
    checkArrayClose "vec coord 1 marginal = scalar x*y jet" 1e-9 (univCumulants scalarProd) prodMarginal
    // 4) THE EMPIRICAL IDENTITY, vectorized -- the load-bearing check: joint
    //    cumulants of the transformed 2-row data equal the pushed JOINT
    //    tower, cross-cumulants included (nothing univariate pins those).
    let transformed = [| Array.map2 (+) b.[0] b.[1]; Array.map2 (*) b.[0] b.[1] |]
    let direct = computeCumulants transformed 3
    for k in 1 .. 3 do
        checkArrayClose $"vec empirical identity rank {k}" 1e-9 direct.[k - 1].Data vec.Kappa.[k - 1].Data
    // 5) Guards: the strict order budget, and a mis-shaped derivative slot.
    let distB4 : Dist.T = { Dim = 2; Order = 4; Kappa = computeCumulants b 4 }
    checkThrows "vec order guard (q*s = 6 > 4 strict)"
        (fun () -> Dist.jetPushforwardVec distB4 g0 jets 3 false |> ignore)
    let badD2 = SymTensor.create 1 2
    checkThrows "vec shape guard (D_2 over wrong dim)"
        (fun () -> Dist.jetPushforwardVec distB6 g0 [| [| d1sum |]; [| d1prod; badD2 |] |] 3 false |> ignore)

// ---------------------------------------------------------------------------

let private compareAcc (name: string) (relTol: float) (absTol: float) (reference: Streaming.Acc) (acc: Streaming.Acc) =
    checkClose $"{name}: N" 1e-9 reference.N acc.N
    checkArrayClose $"{name}: mean" 1e-10 reference.Mean acc.Mean
    for p in 2 .. reference.Order do
        let refT = reference.M.[p - 2]
        let accT = acc.M.[p - 2]
        let labels = SymTensor.labelTable reference.Dim p
        for e in 0 .. refT.Data.Length - 1 do
            checkCloseRel $"{name}: M{p}({fmtLabels labels.[e]})" relTol absTol
                          refT.Data.[e] accT.Data.[e]

let testStreaming () =
    section "prototype 3: streaming comoments (derived kernel)"
    let rng = System.Random(7)
    let d, r, n = 3, 4, 2000
    let data =
        Array.init n (fun _ ->
            [| Oracle.sampleGaussian rng
               Oracle.sampleExponential 0.7 rng
               Oracle.samplePoisson 2.5 rng |])
    // streaming (derived one-observation kernel) vs two-pass reference
    let acc = Streaming.create d r
    for x in data do Streaming.updateOne acc x
    let reference = Oracle.twoPassCentral data r
    compareAcc "stream=twopass" 1e-9 1e-6 reference acc
    // chunked merges are associative: any split/association gives the same state
    let sizes = [| 137; 401; 262; 500; 300; 250; 150 |]
    let chunks =
        let mutable start = 0
        [| for s in sizes ->
             let c = Streaming.create d r
             for i in start .. start + s - 1 do Streaming.updateOne c data.[i]
             start <- start + s
             c |]
    let foldMerged = Array.fold Streaming.merge (Streaming.create d r) chunks
    compareAcc "fold merge" 1e-9 1e-6 reference foldMerged
    let treeMerged =
        Streaming.merge
            (Streaming.merge chunks.[0] chunks.[1])
            (Streaming.merge
                (Streaming.merge chunks.[2] chunks.[3])
                (Streaming.merge (Streaming.merge chunks.[4] chunks.[5]) chunks.[6]))
    compareAcc "tree merge" 1e-9 1e-6 reference treeMerged

let testStability () =
    section "numerical stability: mean 1e9, sigma 1"
    let rng = System.Random(13)
    let n = 100_000
    let xs = Array.init n (fun _ -> 1.0e9 + Oracle.sampleGaussian rng)
    let reference = Oracle.twoPassCentral (xs |> Array.map (fun x -> [| x |])) 4
    let refM = Array.init 3 (fun i -> reference.M.[i].Data.[0] / float n)   // m2, m3, m4
    let naive = Streaming.naiveCentral xs 4
    let acc = Streaming.create 1 4
    for x in xs do Streaming.updateOne acc [| x |]
    let streamM = Array.init 3 (fun i -> acc.M.[i].Data.[0] / float n)
    printfn "  m2: two-pass %.9g | streaming %.9g | naive raw-moment %.6g" refM.[0] streamM.[0] naive.[0]
    printfn "  m4: two-pass %.9g | streaming %.9g | naive raw-moment %.6g" refM.[2] streamM.[2] naive.[2]
    let relErr a b = abs (a - b) / abs b
    check "naive m2 catastrophically wrong (rel err > 1e-2)" (relErr naive.[0] refM.[0] > 1e-2)
    check "naive m4 catastrophically wrong (rel err > 0.5)"  (relErr naive.[2] refM.[2] > 0.5)
    check "streaming m2 accurate (rel err < 1e-8)" (relErr streamM.[0] refM.[0] < 1e-8)
    check "streaming m4 accurate (rel err < 1e-6)" (relErr streamM.[2] refM.[2] < 1e-6)

let demoDerivedFormulas () =
    section "demo: derived univariate merge formulas (= Pebay 2008)"
    for p in 2 .. 4 do
        printfn "%s" (Streaming.mergeFormulaText p |> fun s -> "  " + s.Replace("\n", "\n  "))
    printfn "  (M1 terms pruned; the p=2 line collapses to the familiar n_A*n_B/n * d^2)"

let testFullCircle () =
    section "full circle: streamed estimate (proto 3) vs algebraic propagation (proto 1)"
    let r = 4
    // ground truth process: Y = A Z + b, Z independent non-Gaussians
    let z = Dist.ofIndependent [| Dist.exponentialCumulants 1.0 r
                                  Dist.gammaCumulants 3.0 2.0 r
                                  Dist.poissonCumulants 4.0 r |] r
    let a = [| [| 1.0; 0.5; 0.0 |]
               [| -1.0; 1.0; 0.25 |]
               [| 0.2; 0.0; 1.0 |] |]
    let b = [| 0.0; 1.0; -2.0 |]
    let exact = Dist.affine a b z
    // stream 1M samples through 16 independently-built accumulators, tree-merge
    let nTotal = 1_000_000
    let nChunks = 16
    let chunkSize = nTotal / nChunks
    let rng = System.Random(42)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let chunks =
        Array.init nChunks (fun _ ->
            let acc = Streaming.create 3 r
            let y = Array.zeroCreate 3
            for _ in 1 .. chunkSize do
                let z0 = Oracle.sampleExponential 1.0 rng
                let z1 = Oracle.sampleGamma 3 2.0 rng
                let z2 = Oracle.samplePoisson 4.0 rng
                for j in 0 .. 2 do
                    y.[j] <- a.[j].[0] * z0 + a.[j].[1] * z1 + a.[j].[2] * z2 + b.[j]
                Streaming.updateOne acc y
            acc)
    let merged = Array.reduce Streaming.merge chunks
    sw.Stop()
    let est = Streaming.finalize merged
    printfn "  streamed %d obs in %d chunks: %.2fs (%.1fk obs/s)"
            nTotal nChunks sw.Elapsed.TotalSeconds (float nTotal / sw.Elapsed.TotalSeconds / 1000.0)
    // per-rank statistical tolerances (seeded run, ~1M samples)
    let tols = [| (0.005, 0.01); (0.02, 0.02); (0.08, 0.08); (0.20, 0.50) |]
    for k in 1 .. r do
        let relTol, absTol = tols.[k - 1]
        let labels = SymTensor.labelTable 3 k
        let mutable worst = 0.0
        for e in 0 .. exact.Kappa.[k - 1].Data.Length - 1 do
            let ev = exact.Kappa.[k - 1].Data.[e]
            let av = est.Kappa.[k - 1].Data.[e]
            worst <- max worst (abs (ev - av))
            checkCloseRel $"kappa{k}({fmtLabels labels.[e]})" relTol absTol ev av
        printfn "  rank %d: max |exact - estimated| = %.4g" k worst
    // a taste of the comparison table
    printfn "  sample entries (exact vs streamed):"
    for (k, lbl) in [ (2, [| 0; 1 |]); (3, [| 0; 1; 2 |]); (4, [| 0; 0; 1; 1 |]) ] do
        printfn "    kappa%d(%s): %10.5f vs %10.5f" k (fmtLabels lbl)
                (SymTensor.get exact.Kappa.[k - 1] lbl) (SymTensor.get est.Kappa.[k - 1] lbl)

/// Oracle dump for the compiler's `cumulants(A, r)` former: cumulants via
/// cumulantsFromMoments, printed per rank in canonical cell order.
let dumpCumulants (data: float[][]) (rmax: int) =
    let d = data.Length
    let kappa = computeCumulants data rmax
    for k in 1 .. rmax do
        let cells =
            SymTensor.enumerate d k
            |> Seq.map (fun labels -> sprintf "%.12g" (SymTensor.get kappa.[k - 1] labels))
            |> String.concat ", "
        printfn "kappa%d = [%s]" k cells

/// Print every canonical cell of the given ranks, labeled by position tuple:
/// "k<rank>[<labels>] = <value>". Lets a specific mixed-block cell (e.g. an
/// X-X-Y cross cumulant) be picked out of packed storage by eye.
let private printLabeledCells (tensors: SymTensor.T[]) (ranks: int list) =
    for k in ranks do
        let t = tensors.[k - 1]
        for labels in SymTensor.enumerate t.Dim t.Rank do
            printfn "k%d[%s] = %.12g" k (fmtLabels labels) (SymTensor.get t labels)

/// A set partition of positions [0..n-1] is CROSSING iff there exist
/// a<b<c<d with a,c in one block and b,d in a different block.
let isNonCrossing (partition: int list list) : bool =
    let n = partition |> List.sumBy List.length
    let blockOf = Array.zeroCreate n
    partition |> List.iteri (fun bi block -> block |> List.iter (fun p -> blockOf.[p] <- bi))
    let mutable crossing = false
    for a in 0 .. n - 1 do
        for b in a + 1 .. n - 1 do
            for c in b + 1 .. n - 1 do
                for d in c + 1 .. n - 1 do
                    if blockOf.[a] = blockOf.[c] && blockOf.[b] = blockOf.[d] && blockOf.[a] <> blockOf.[b] then
                        crossing <- true
    not crossing

/// Free cumulants via the non-crossing-partition triangular recursion:
///   mu_n(labels) = sum over NON-CROSSING pi of [0..n-1]: prod over blocks Bl of fk_|Bl|(labels@Bl)
/// so fk_n(labels) = mu_n(labels) minus the same sum restricted to
/// non-crossing partitions EXCLUDING the single-block partition (whose lone
/// block would just be fk_n itself). Ranks computed ascending so each rank
/// only reads already-computed lower ranks.
let freeCumulants (data: float[][]) (rmax: int) : SymTensor.T[] =
    let d = data.Length
    let mu = computeMoments data rmax
    let fk = Array.zeroCreate<SymTensor.T> rmax
    for n in 1 .. rmax do
        let out = SymTensor.create d n
        let ncMultiBlock =
            Combinatorics.setPartitions n
            |> Array.filter (fun p -> p.Length > 1 && isNonCrossing (p |> Array.map Array.toList |> Array.toList))
        for labels in SymTensor.enumerate d n do
            let mutable correction = 0.0
            for partition in ncMultiBlock do
                let mutable prod = 1.0
                for block in partition do
                    let sub = block |> Array.map (fun pos -> labels.[pos]) |> Array.sort
                    prod <- prod * SymTensor.get fk.[block.Length - 1] sub
                correction <- correction + prod
            SymTensor.set out labels (SymTensor.get mu.[n - 1] labels - correction)
        fk.[n - 1] <- out
    fk

// ---------------------------------------------------------------------------
// Prototype 5: closed-form densities, Edgeworth / Cornish-Fisher, conjugates
// ---------------------------------------------------------------------------

/// Full-precision formatting for the pin sheet (oracles/ppl/ORACLE_PINS.md).
/// 17 significant digits round-trips an IEEE double exactly.
let g17 (x: float) : string =
    if System.Double.IsNegativeInfinity x then "-inf"
    elif System.Double.IsPositiveInfinity x then "inf"
    elif System.Double.IsNaN x then "nan"
    elif x = 0.0 then "0"                     // collapses -0.0, which pins badly
    else sprintf "%.17g" x

/// Shortest round-tripping form -- used for the INPUT columns (x, p, and the
/// worked-example data), where "0.1" is what a corpus author will type and
/// "0.10000000000000001" is only noise. Outputs always use g17.
let gs (x: float) : string =
    if x = 0.0 then "0" else x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)

let fmtTower (cum: float[]) = cum |> Array.map g17 |> String.concat ", "

/// Worked examples shared by the conjugate self-tests and `dump-conjugate`,
/// so the pin sheet and the assertions can never drift apart.
let nnPriorMean, nnPriorVar, nnLikVar = 0.0, 4.0, 2.0
let nnData = [| 1.2; 0.7; 2.3; -0.4; 1.9 |]
let bbPriorA, bbPriorB, bbN, bbK = 2.0, 3.0, 10, 7
let gpPriorA, gpPriorB = 2.0, 1.0
let gpCounts = [| 3.0; 1.0; 4.0; 1.0; 5.0 |]
let nigM0, nigK0, nigA0, nigB0 = 0.0, 1.0, 2.0, 3.0

let private sumOf (a: float[]) = Array.sum a
let private sumSqOf (a: float[]) = a |> Array.sumBy (fun x -> x * x)

/// Trapezoid grid over [lo, hi] with an UNNORMALIZED log-weight; returns the
/// nodes and normalized probabilities. Log-space with a max subtraction so
/// wide-support likelihoods cannot underflow.
let private normalizedGrid (lo: float) (hi: float) (m: int) (logw: float -> float) =
    let h = (hi - lo) / float (m - 1)
    let xs = Array.init m (fun i -> lo + float i * h)
    let lw = xs |> Array.map logw
    let mx = lw |> Array.max
    let w =
        Array.init m (fun i ->
            let t = if System.Double.IsNegativeInfinity lw.[i] then 0.0 else exp (lw.[i] - mx)
            let tr = if i = 0 || i = m - 1 then 0.5 else 1.0
            t * tr)
    let s = Array.sum w
    (xs, w |> Array.map (fun x -> x / s))

let private expectOn (xs: float[]) (p: float[]) (g: float -> float) =
    let mutable acc = 0.0
    for i in 0 .. xs.Length - 1 do acc <- acc + p.[i] * g xs.[i]
    acc

/// (mean, variance) of the identity coordinate under a normalized grid.
let private meanVarOn (xs: float[]) (p: float[]) =
    let m = expectOn xs p id
    let m2 = expectOn xs p (fun x -> x * x)
    (m, m2 - m * m)

/// Numeric total mass of a continuous density on [lo, hi] -- the independent
/// normalization check for the closed-form logpdfs.
let private mass (lo: float) (hi: float) (m: int) (lp: float -> float) =
    let h = (hi - lo) / float (m - 1)
    let mutable acc = 0.0
    for i in 0 .. m - 1 do
        let x = lo + float i * h
        let v = lp x
        let f = if System.Double.IsNegativeInfinity v then 0.0 else exp v
        acc <- acc + (if i = 0 || i = m - 1 then 0.5 else 1.0) * f
    acc * h

/// Total mass under the substitution x = u^2 (dx = 2u du), which turns the
/// integrable x^(shape-1) pole of a sub-unit-shape Gamma into a bounded,
/// smooth integrand that plain trapezoid can actually resolve.
let private massSqrt (hi: float) (m: int) (lp: float -> float) =
    let ulo, uhi = 1e-12, sqrt hi
    let h = (uhi - ulo) / float (m - 1)
    let mutable acc = 0.0
    for i in 0 .. m - 1 do
        let u = ulo + float i * h
        let v = lp (u * u)
        let f = if System.Double.IsNegativeInfinity v then 0.0 else exp v * 2.0 * u
        acc <- acc + (if i = 0 || i = m - 1 then 0.5 else 1.0) * f
    acc * h

/// Composite Simpson weight (unnormalized; the h/3 factor cancels whenever the
/// result is normalized). Requires an odd node count.
let private simpsonW (m: int) (i: int) =
    if i = 0 || i = m - 1 then 1.0 elif i % 2 = 1 then 4.0 else 2.0

let testDensity () =
    section "prototype 5: lgamma + closed-form log-densities"
    // --- lgamma: exact values, factorials, reflection, duplication ---
    checkClose "lgamma(1) = 0" 1e-14 0.0 (Density.lgamma 1.0)
    checkClose "lgamma(2) = 0" 1e-14 0.0 (Density.lgamma 2.0)
    checkClose "lgamma(0.5) = log sqrt(pi)" 1e-14 0.5723649429247001 (Density.lgamma 0.5)
    checkClose "lgamma(5) = log 24" 1e-13 3.1780538303479458 (Density.lgamma 5.0)
    checkClose "lgamma(6) = log 120" 1e-13 4.787491742782046 (Density.lgamma 6.0)
    for n in 0 .. 15 do
        checkCloseRel $"lgamma({n + 1}) = log {n}!" 1e-13 1e-13
                      (log (Combinatorics.factorial n)) (Density.logFactorial n)
    // reflection: lgamma(x) + lgamma(1-x) = log(pi / sin(pi x))
    for x in [ 0.1; 0.3; 0.45 ] do
        checkCloseRel (sprintf "lgamma reflection at %g" x) 1e-13 1e-13
                      (log (System.Math.PI / sin (System.Math.PI * x)))
                      (Density.lgamma x + Density.lgamma (1.0 - x))
    // Legendre duplication: lgamma(2z) = lgamma z + lgamma(z+1/2) + (2z-1)log2 - log(pi)/2
    for z in [ 0.7; 1.7; 3.2; 9.5 ] do
        checkCloseRel (sprintf "lgamma duplication at %g" z) 1e-13 1e-13
                      (Density.lgamma (2.0 * z))
                      (Density.lgamma z + Density.lgamma (z + 0.5)
                       + (2.0 * z - 1.0) * log 2.0 - 0.5 * log System.Math.PI)

    // --- spot values ---
    checkClose "gaussian logpdf(0; 0, 1)" 1e-15 -0.9189385332046727 (Density.gaussianLogpdf 0.0 1.0 0.0)
    checkClose "gaussian logpdf(1.5; 0.5, 4)" 1e-14
               (-0.5 * (log (2.0 * System.Math.PI) + log 4.0) - 0.125)
               (Density.gaussianLogpdf 0.5 4.0 1.5)
    checkClose "exponential logpdf(1; 2)" 1e-15 (log 2.0 - 2.0) (Density.exponentialLogpdf 2.0 1.0)
    checkClose "uniform logpdf(1; 0, 2)" 1e-15 (-log 2.0) (Density.uniformLogpdf 0.0 2.0 1.0)
    checkClose "lognormal logpdf(1; 0, 1)" 1e-15 -0.9189385332046727 (Density.lognormalLogpdf 0.0 1.0 1.0)
    checkClose "bernoulli logpmf(1; 0.3)" 1e-15 (log 0.3) (Density.bernoulliLogpmf 0.3 1.0)
    checkClose "bernoulli sums to 1" 1e-15 1.0
               (exp (Density.bernoulliLogpmf 0.3 0.0) + exp (Density.bernoulliLogpmf 0.3 1.0))

    // --- cross-family identities (independent of the lgamma path where possible) ---
    for x in [ 0.1; 0.5; 1.0; 2.5; 5.0 ] do
        checkClose (sprintf "Gamma(1, 2.5) == Exp(2.5) at %g" x) 1e-13
                   (Density.exponentialLogpdf 2.5 x) (Density.gammaLogpdf 1.0 2.5 x)
        // integer shape: closed form with an exact factorial, no lgamma
        let k, r = 4, 1.7
        checkCloseRel (sprintf "Gamma(4, 1.7) vs factorial form at %g" x) 1e-13 1e-13
                      (float k * log r + float (k - 1) * log x - r * x - log (Combinatorics.factorial (k - 1)))
                      (Density.gammaLogpdf (float k) r x)
        // lognormal is the gaussian of log x, minus the Jacobian
        checkClose (sprintf "LogNormal(0.5, 0.25) vs gaussian(log x) at %g" x) 1e-13
                   (Density.gaussianLogpdf 0.5 0.25 (log x) - log x)
                   (Density.lognormalLogpdf 0.5 0.25 x)
    for x in [ 0.05; 0.25; 0.5; 0.75; 0.95 ] do
        checkClose (sprintf "Beta(1, 1) == Uniform(0, 1) at %g" x) 1e-14
                   (Density.uniformLogpdf 0.0 1.0 x) (Density.betaLogpdf 1.0 1.0 x)
        // integer shapes: 1/B(a,b) = (a+b-1)! / ((a-1)!(b-1)!)
        let a, b = 3, 5
        checkCloseRel (sprintf "Beta(3, 5) vs binomial form at %g" x) 1e-13 1e-13
                      (float (a - 1) * log x + float (b - 1) * log (1.0 - x)
                       + log (Combinatorics.factorial (a + b - 1))
                       - log (Combinatorics.factorial (a - 1)) - log (Combinatorics.factorial (b - 1)))
                      (Density.betaLogpdf (float a) (float b) x)
    for k in 0 .. 8 do
        checkCloseRel $"Poisson(4.5) vs factorial form at {k}" 1e-13 1e-13
                      (float k * log 4.5 - 4.5 - log (Combinatorics.factorial k))
                      (Density.poissonLogpmf 4.5 (float k))

    // --- normalization: every continuous density integrates to 1, each PMF sums to 1 ---
    checkClose "int gaussian(1.5, 4) = 1" 1e-10 1.0 (mass -20.0 23.0 200001 (Density.gaussianLogpdf 1.5 4.0))
    checkClose "int exponential(2.5) = 1" 1e-7 1.0 (mass 0.0 40.0 400001 (Density.exponentialLogpdf 2.5))
    checkClose "int uniform(-2, 3) = 1" 1e-6 1.0 (mass -4.0 5.0 900001 (Density.uniformLogpdf -2.0 3.0))
    checkClose "int lognormal(0, 1) = 1" 1e-7 1.0 (mass 1e-9 400.0 2000001 (Density.lognormalLogpdf 0.0 1.0))
    checkClose "int gamma(3.5, 2) = 1" 1e-9 1.0 (mass 1e-12 40.0 400001 (Density.gammaLogpdf 3.5 2.0))
    // shape < 1 has an integrable pole at 0: integrate in u = sqrt(x)
    checkClose "int gamma(0.5, 1) = 1" 1e-9 1.0 (massSqrt 400.0 400001 (Density.gammaLogpdf 0.5 1.0))
    checkClose "int beta(2, 3) = 1" 1e-9 1.0 (mass 0.0 1.0 200001 (Density.betaLogpdf 2.0 3.0))
    let poissonMass = [ 0 .. 80 ] |> List.sumBy (fun k -> exp (Density.poissonLogpmf 4.5 (float k)))
    checkClose "sum poisson(4.5) = 1" 1e-12 1.0 poissonMass

    // --- support boundaries report -inf, not NaN or an exception ---
    let isNegInf (v: float) = System.Double.IsNegativeInfinity v
    check "exponential below support" (isNegInf (Density.exponentialLogpdf 1.0 -0.5))
    check "uniform below support" (isNegInf (Density.uniformLogpdf 0.0 2.0 -0.1))
    check "uniform above support" (isNegInf (Density.uniformLogpdf 0.0 2.0 2.1))
    check "lognormal at 0" (isNegInf (Density.lognormalLogpdf 0.0 1.0 0.0))
    check "gamma at 0" (isNegInf (Density.gammaLogpdf 1.0 1.0 0.0))
    check "beta at 0" (isNegInf (Density.betaLogpdf 2.0 3.0 0.0))
    check "beta at 1" (isNegInf (Density.betaLogpdf 2.0 3.0 1.0))
    check "poisson at a non-integer" (isNegInf (Density.poissonLogpmf 2.0 2.5))
    check "poisson at a negative count" (isNegInf (Density.poissonLogpmf 2.0 -1.0))
    check "bernoulli off support" (isNegInf (Density.bernoulliLogpmf 0.3 2.0))
    checkThrows "gaussian rejects a non-positive variance"
                (fun () -> Density.gaussianLogpdf 0.0 0.0 1.0 |> ignore)
    checkThrows "gamma rejects a non-positive rate"
                (fun () -> Density.gammaLogpdf 2.0 0.0 1.0 |> ignore)

    // loglik is the summed logpdf over the sample axis
    checkClose "loglik = sum of logpdfs" 1e-13
               (nnData |> Array.sumBy (Density.gaussianLogpdf 1.0 2.0))
               (Density.loglik (Density.Gaussian (1.0, 2.0)) nnData)

/// The textbook Edgeworth bracket through order eps^4, written out by hand
/// from the standard term list, as an independent check on the generated
/// coefficients. Standardized cumulants l3..l6; `groups` selects how many
/// eps-groups to keep (1 => l3 only, ..., 4 => the full list).
let private textbookFactor (l3: float) (l4: float) (l5: float) (l6: float) (groups: int) (z: float) =
    let he = Expansion.hermiteTable 12 z
    let mutable acc = 1.0
    if groups >= 1 then
        acc <- acc + l3 / 6.0 * he.[3]
    if groups >= 2 then
        acc <- acc + l4 / 24.0 * he.[4] + l3 * l3 / 72.0 * he.[6]
    if groups >= 3 then
        acc <- acc + l5 / 120.0 * he.[5] + l3 * l4 / 144.0 * he.[7]
                   + l3 * l3 * l3 / 1296.0 * he.[9]
    if groups >= 4 then
        acc <- acc + l6 / 720.0 * he.[6]
                   + (l3 * l5 / 720.0 + l4 * l4 / 1152.0) * he.[8]
                   + l3 * l3 * l4 / 1728.0 * he.[10]
                   + l3 * l3 * l3 * l3 / 31104.0 * he.[12]
    acc

let testEdgeworth () =
    section "prototype 5: Edgeworth / Gram-Charlier density"
    // Hermite recurrence spot values
    let he = Expansion.hermiteTable 6 1.5
    checkClose "He_2(1.5)" 1e-14 1.25 he.[2]
    checkClose "He_3(1.5)" 1e-14 (1.5 ** 3.0 - 3.0 * 1.5) he.[3]
    checkClose "He_4(1.5)" 1e-14 (1.5 ** 4.0 - 6.0 * 1.5 ** 2.0 + 3.0) he.[4]
    checkClose "He_6(0)" 1e-14 -15.0 (Expansion.hermite 6 0.0)

    // 1) A pure Gaussian tower reproduces the exact Gaussian density: every
    //    correction term must vanish, at r = 2 and at r = 6 with explicit zeros.
    let gTower2 = [ 2.0; 3.0 ]
    let gTower6 = [ 2.0; 3.0; 0.0; 0.0; 0.0; 0.0 ]
    for x in [ -1.0; 0.0; 2.0; 4.0; 6.0 ] do
        let exact = exp (Density.gaussianLogpdf 2.0 3.0 x)
        checkClose (sprintf "Edgeworth r=2 gaussian at %g" x) 1e-15 exact (Expansion.edgeworthPdf gTower2 x)
        checkClose (sprintf "Edgeworth r=6 zero-tower gaussian at %g" x) 1e-15 exact (Expansion.edgeworthPdf gTower6 x)
        checkClose (sprintf "Edgeworth bracket = 1 at %g" x) 1e-15 1.0 (Expansion.edgeworthFactor gTower6 x)

    // 2) The GENERATED coefficients equal the hand-written textbook term list,
    //    group by group. kappa_2 = 1 so lambda_k = kappa_k.
    let l3, l4, l5, l6 = 0.7, -1.3, 2.1, -0.9
    for z in [ -2.0; -0.5; 0.0; 0.8; 1.7 ] do
        for groups in 1 .. 4 do
            let tower = 0.0 :: 1.0 :: [ l3; l4; l5; l6 ] |> List.truncate (groups + 2)
            checkCloseRel (sprintf "generated == textbook, groups=%d, z=%g" groups z) 1e-12 1e-12
                          (textbookFactor l3 l4 l5 l6 groups z)
                          (Expansion.edgeworthFactor tower z)
    // and with a non-trivial mu/sigma the bracket is the same function of z
    let sd = 1.7
    let mu = -0.4
    let scaled = [ mu; sd * sd; l3 * sd ** 3.0; l4 * sd ** 4.0; l5 * sd ** 5.0; l6 * sd ** 6.0 ]
    for z in [ -1.0; 0.0; 1.3 ] do
        checkCloseRel (sprintf "standardization invariance at z=%g" z) 1e-12 1e-12
                      (textbookFactor l3 l4 l5 l6 4 z)
                      (Expansion.edgeworthFactor scaled (mu + sd * z))
        checkCloseRel (sprintf "scaled density = bracket * phi / sd at z=%g" z) 1e-12 1e-12
                      (Expansion.stdNormalPdf z * textbookFactor l3 l4 l5 l6 4 z / sd)
                      (Expansion.edgeworthPdf scaled (mu + sd * z))

    // 3) Honest-approximation territory: Exp(1) has kappa_k = (k-1)!, so the
    //    standardized cumulants (2, 6, 24, 120) are large and the series is
    //    only useful in the bulk. Report the achieved error; the assertions
    //    below pin the measured bulk accuracy, not an aspiration.
    let expTower4 = [ 1.0; 1.0; 2.0; 6.0 ]
    let expTower6 = [ 1.0; 1.0; 2.0; 6.0; 24.0; 120.0 ]
    printfn "  Exp(1) Edgeworth vs exact e^-x (x = 0.2 .. 2.2; * marks the asserted bulk):"
    printfn "      x        exact          r=4 approx     r=4 err        r=6 approx     r=6 err"
    // The "bulk" is the central band z in [-0.4, 0.4] (Exp(1) has mu = sd = 1).
    // Outside it the series degrades fast -- x = 0.2 sits only 0.2 sd from the
    // hard support edge at 0, where no Gaussian-anchored expansion can work.
    let inBulk (x: float) = x >= 0.6 - 1e-9 && x <= 1.4 + 1e-9
    let mutable maxErr4 = 0.0
    let mutable maxErr6 = 0.0
    let mutable bulk4 = 0.0
    let mutable bulk6 = 0.0
    for i in 0 .. 20 do
        let x = 0.2 + 0.1 * float i
        let exact = exp (-x)
        let a4 = Expansion.edgeworthPdf expTower4 x
        let a6 = Expansion.edgeworthPdf expTower6 x
        maxErr4 <- max maxErr4 (abs (a4 - exact))
        maxErr6 <- max maxErr6 (abs (a6 - exact))
        if inBulk x then
            bulk4 <- max bulk4 (abs (a4 - exact))
            bulk6 <- max bulk6 (abs (a6 - exact))
        if i % 2 = 0 then
            printfn "   %6.2f %s %12.8f   %12.8f   %+11.3e   %12.8f   %+11.3e"
                    x (if inBulk x then "*" else " ") exact a4 (a4 - exact) a6 (a6 - exact)
    printfn "    max |err|: whole grid r=4 %.4g / r=6 %.4g; bulk r=4 %.4g / r=6 %.4g"
            maxErr4 maxErr6 bulk4 bulk6
    check (sprintf "Exp(1) r=4 bulk max error %.4g < 0.02" bulk4) (bulk4 < 0.02)
    check (sprintf "Exp(1) r=6 bulk max error %.4g < 0.005" bulk6) (bulk6 < 0.005)
    check (sprintf "Exp(1) r=6 beats r=4 in the bulk (%.4g < %.4g)" bulk6 bulk4) (bulk6 < bulk4)
    // ... and it is genuinely an approximation: the tail goes negative, which
    // is exactly what dist_negativity measures on the compiler side.
    check "Exp(1) r=6 Edgeworth goes negative somewhere in [3, 6]"
          ([ 3.0; 3.5; 4.0; 4.5; 5.0; 5.5; 6.0 ] |> List.exists (fun x -> Expansion.edgeworthPdf expTower6 x < 0.0))

let testCornishFisher () =
    section "prototype 5: Cornish-Fisher quantiles"
    // AS241 against published standard-normal quantiles
    checkClose "Phi^-1(0.5)" 1e-15 0.0 (Expansion.normalQuantile 0.5)
    checkClose "Phi^-1(0.75)" 1e-14 0.6744897501960817 (Expansion.normalQuantile 0.75)
    checkClose "Phi^-1(0.95)" 1e-14 1.6448536269514722 (Expansion.normalQuantile 0.95)
    checkClose "Phi^-1(0.975)" 1e-14 1.959963984540054 (Expansion.normalQuantile 0.975)
    checkClose "Phi^-1(0.99)" 1e-14 2.3263478740408408 (Expansion.normalQuantile 0.99)
    checkClose "Phi^-1(0.999)" 1e-13 3.090232306167813 (Expansion.normalQuantile 0.999)
    checkClose "Phi^-1(1e-10)" 1e-12 -6.361340902404056 (Expansion.normalQuantile 1e-10)
    for p in [ 0.001; 0.05; 0.2; 0.4999 ] do
        checkClose (sprintf "Phi^-1 antisymmetry at %g" p) 1e-14
                   (-(Expansion.normalQuantile p)) (Expansion.normalQuantile (1.0 - p))
    checkThrows "Phi^-1 rejects p = 0" (fun () -> Expansion.normalQuantile 0.0 |> ignore)
    checkThrows "Phi^-1 rejects p = 1" (fun () -> Expansion.normalQuantile 1.0 |> ignore)

    // 1) A Gaussian tower gives the exact Gaussian quantiles (all corrections
    //    vanish), at r = 2 and r = 6 with explicit zeros.
    let ps = [ 0.05; 0.25; 0.5; 0.75; 0.95 ]
    let mu, v = 2.0, 3.0
    for p in ps do
        let exact = mu + sqrt v * Expansion.normalQuantile p
        checkClose (sprintf "CF r=2 gaussian at p=%g" p) 1e-14 exact (Expansion.cornishFisher [ mu; v ] p)
        checkClose (sprintf "CF r=6 zero-tower gaussian at p=%g" p) 1e-14 exact
                   (Expansion.cornishFisher [ mu; v; 0.0; 0.0; 0.0; 0.0 ] p)

    // 2) The series inversion equals the classic closed-form expansion:
    //      w = z + (z^2-1) l3/6
    //            + (z^3-3z) l4/24 - (2z^3-5z) l3^2/36
    //            + (z^4-6z^2+3) l5/120 - (z^4-5z^2+2) l3 l4/24
    //            + (12z^4-53z^2+17) l3^3/324
    let l3, l4, l5 = 0.7, -1.3, 2.1
    let classic (z: float) (groups: int) =
        let mutable w = z
        if groups >= 1 then w <- w + (z * z - 1.0) * l3 / 6.0
        if groups >= 2 then
            w <- w + (z ** 3.0 - 3.0 * z) * l4 / 24.0 - (2.0 * z ** 3.0 - 5.0 * z) * l3 * l3 / 36.0
        if groups >= 3 then
            w <- w + (z ** 4.0 - 6.0 * z * z + 3.0) * l5 / 120.0
                   - (z ** 4.0 - 5.0 * z * z + 2.0) * l3 * l4 / 24.0
                   + (12.0 * z ** 4.0 - 53.0 * z * z + 17.0) * l3 * l3 * l3 / 324.0
        w
    for p in [ 0.01; 0.05; 0.25; 0.5; 0.75; 0.95; 0.99 ] do
        let z = Expansion.normalQuantile p
        for groups in 1 .. 3 do
            let tower = 0.0 :: 1.0 :: [ l3; l4; l5 ] |> List.truncate (groups + 2)
            checkCloseRel (sprintf "CF series inversion == classic, groups=%d, p=%g" groups p) 1e-11 1e-12
                          (classic z groups) (Expansion.cornishFisher tower p)
    // affine equivariance: CF(mu + sigma X) = mu + sigma CF(X)
    let sd = 1.7
    let mu2 = -0.4
    let scaled = [ mu2; sd * sd; l3 * sd ** 3.0; l4 * sd ** 4.0; l5 * sd ** 5.0 ]
    for p in ps do
        checkCloseRel (sprintf "CF affine equivariance at p=%g" p) 1e-11 1e-12
                      (mu2 + sd * Expansion.cornishFisher [ 0.0; 1.0; l3; l4; l5 ] p)
                      (Expansion.cornishFisher scaled p)
    // monotone in p on a skewed tower, and honest about its own accuracy
    let expTower6 = [ 1.0; 1.0; 2.0; 6.0; 24.0; 120.0 ]
    let qs = ps |> List.map (Expansion.cornishFisher expTower6)
    check "CF of the Exp(1) tower is increasing in p" (qs = List.sort qs)
    printfn "  Exp(1) Cornish-Fisher vs exact -log(1-p):"
    for p in ps do
        let exact = -log (1.0 - p)
        let q4 = Expansion.cornishFisher [ 1.0; 1.0; 2.0; 6.0 ] p
        let q6 = Expansion.cornishFisher expTower6 p
        printfn "    p=%.2f  exact %8.5f   r=4 %8.5f (%+.3e)   r=6 %8.5f (%+.3e)"
                p exact q4 (q4 - exact) q6 (q6 - exact)

let testConjugate () =
    section "prototype 5: conjugate posteriors vs brute-force quadrature"
    // 1) Normal-Normal, known variance.
    let n = nnData.Length
    let nnPost = Conjugate.normalNormal nnPriorMean nnPriorVar nnLikVar n (sumOf nnData)
    let nnLogw (mu: float) =
        Density.gaussianLogpdf nnPriorMean nnPriorVar mu
        + (nnData |> Array.sumBy (Density.gaussianLogpdf mu nnLikVar))
    let (xs, ps) = normalizedGrid -20.0 22.0 400001 nnLogw
    let (gm, gv) = meanVarOn xs ps
    checkCloseRel "Normal-Normal posterior mean vs quadrature" 1e-10 1e-12 gm nnPost.PostMean
    checkCloseRel "Normal-Normal posterior var vs quadrature" 1e-10 1e-12 gv nnPost.PostVar
    let nnTower = Conjugate.normalNormalTower nnPost 4
    checkArrayClose "Normal-Normal tower = gaussian cumulants" 1e-14
                    [| nnPost.PostMean; nnPost.PostVar; 0.0; 0.0 |] nnTower

    // 2) Beta-Bernoulli.
    let bbPost = Conjugate.betaBernoulli bbPriorA bbPriorB bbN bbK
    check "Beta-Bernoulli hyperparameters" (bbPost.A = 9.0 && bbPost.B = 6.0)
    let bbLogw (p: float) =
        Density.betaLogpdf bbPriorA bbPriorB p
        + float bbK * Density.bernoulliLogpmf p 1.0
        + float (bbN - bbK) * Density.bernoulliLogpmf p 0.0
    let (bx, bp) = normalizedGrid 1e-12 (1.0 - 1e-12) 200001 bbLogw
    let (bm, bv) = meanVarOn bx bp
    checkCloseRel "Beta-Bernoulli posterior mean vs quadrature" 1e-9 1e-12 bm (Conjugate.betaMean bbPost)
    checkCloseRel "Beta-Bernoulli posterior var vs quadrature" 1e-9 1e-12 bv (Conjugate.betaVar bbPost)

    // 3) Gamma-Poisson.
    let gpPost = Conjugate.gammaPoisson gpPriorA gpPriorB gpCounts.Length (sumOf gpCounts)
    check "Gamma-Poisson hyperparameters" (gpPost.Shape = 16.0 && gpPost.Rate = 6.0)
    let gpLogw (lam: float) =
        Density.gammaLogpdf gpPriorA gpPriorB lam
        + (gpCounts |> Array.sumBy (fun k -> Density.poissonLogpmf lam k))
    let (lx, lp) = normalizedGrid 1e-9 30.0 300001 gpLogw
    let (lm, lv) = meanVarOn lx lp
    checkCloseRel "Gamma-Poisson posterior mean vs quadrature" 1e-9 1e-12 lm (gpPost.Shape / gpPost.Rate)
    checkCloseRel "Gamma-Poisson posterior var vs quadrature" 1e-9 1e-12 lv (gpPost.Shape / (gpPost.Rate ** 2.0))
    let gpTower = Conjugate.gammaPoissonTower gpPost 4
    checkArrayClose "Gamma-Poisson tower = gamma cumulants" 1e-13
                    (Dist.gammaCumulants gpPost.Shape gpPost.Rate 4) gpTower

    // 4) Normal-InverseGamma (unknown mean AND variance): 2-D brute force over
    //    (mu, sigma2), the sigma2 axis on a log grid with its Jacobian.
    let nigPost =
        Conjugate.normalInvGamma nigM0 nigK0 nigA0 nigB0 n (sumOf nnData) (sumSqOf nnData)
    let nigLogw (mu: float) (s: float) =
        let v = exp s
        Density.invGammaLogpdf nigA0 nigB0 v
        + Density.gaussianLogpdf nigM0 (v / nigK0) mu
        + (nnData |> Array.sumBy (Density.gaussianLogpdf mu v))
        + s                                  // Jacobian d(sigma2)/ds = sigma2
    // The mu margin is a Student-t with 2*alpha_n = 9 degrees of freedom, so
    // its second moment has an algebraic tail: the mu window has to reach far
    // (about 56 scale units here) before truncation stops dominating the
    // Simpson error. sigma2 rides a log grid with its Jacobian.
    let nm, ns = 2001, 1201
    let mlo, mhi = -25.0, 27.0
    let slo, shi = log 0.005, log 2000.0
    let hm = (mhi - mlo) / float (nm - 1)
    let hs = (shi - slo) / float (ns - 1)
    let mus = Array.init nm (fun i -> mlo + float i * hm)
    let ss = Array.init ns (fun j -> slo + float j * hs)
    let lw = Array.init nm (fun i -> Array.init ns (fun j -> nigLogw mus.[i] ss.[j]))
    let mx = lw |> Array.collect id |> Array.max
    let w =
        Array.init nm (fun i ->
            Array.init ns (fun j -> simpsonW nm i * simpsonW ns j * exp (lw.[i].[j] - mx)))
    let total = w |> Array.sumBy Array.sum
    let expect2 (g: float -> float -> float) =
        let mutable acc = 0.0
        for i in 0 .. nm - 1 do
            for j in 0 .. ns - 1 do
                acc <- acc + w.[i].[j] * g mus.[i] (exp ss.[j])
        acc / total
    let eMu = expect2 (fun m _ -> m)
    let eMu2 = expect2 (fun m _ -> m * m)
    let eV = expect2 (fun _ v -> v)
    let ePrec = expect2 (fun _ v -> 1.0 / v)
    checkCloseRel "Normal-InvGamma E[mu] vs quadrature" 1e-7 1e-9 eMu (Conjugate.nigMeanMu nigPost)
    checkCloseRel "Normal-InvGamma Var[mu] vs quadrature" 1e-6 1e-9 (eMu2 - eMu * eMu) (Conjugate.nigVarMu nigPost)
    checkCloseRel "Normal-InvGamma E[sigma2] vs quadrature" 1e-7 1e-9 eV (Conjugate.nigMeanSigma2 nigPost)
    checkCloseRel "Normal-InvGamma E[1/sigma2] vs quadrature" 1e-8 1e-10 ePrec (Conjugate.nigMeanPrecision nigPost)
    let nigTower = Conjugate.normalInvGammaPrecisionTower nigPost 4
    checkClose "Normal-InvGamma precision tower kappa_1 = alpha/beta" 1e-13
               (Conjugate.nigMeanPrecision nigPost) nigTower.[0]
    checkClose "Normal-InvGamma precision tower kappa_2 = alpha/beta^2" 1e-13
               (nigPost.Alpha / (nigPost.Beta ** 2.0)) nigTower.[1]

// ---------------------------------------------------------------------------

/// Standard case matrix for `dump-logpdf` -- 2-3 parameter points per family,
/// 5 evaluation points each.
let private logpdfMatrix : (Density.Family * float list) list =
    [ Density.Gaussian (0.0, 1.0),      [ -2.0; -0.5; 0.0; 1.0; 2.5 ]
      Density.Gaussian (1.5, 4.0),      [ -2.0; -0.5; 0.0; 1.0; 2.5 ]
      Density.Exponential 1.0,          [ 0.1; 0.5; 1.0; 2.5; 5.0 ]
      Density.Exponential 2.5,          [ 0.1; 0.5; 1.0; 2.5; 5.0 ]
      Density.Uniform (0.0, 1.0),       [ -1.0; 0.0; 0.5; 1.0; 2.0 ]
      Density.Uniform (-2.0, 3.0),      [ -3.0; -2.0; 0.5; 3.0; 4.0 ]
      Density.LogNormal (0.0, 1.0),     [ 0.25; 0.5; 1.0; 2.0; 5.0 ]
      Density.LogNormal (0.5, 0.25),    [ 0.25; 0.5; 1.0; 2.0; 5.0 ]
      Density.Gamma (2.0, 1.0),         [ 0.1; 0.5; 1.0; 2.5; 5.0 ]
      Density.Gamma (3.5, 2.0),         [ 0.1; 0.5; 1.0; 2.5; 5.0 ]
      Density.Gamma (0.5, 1.0),         [ 0.1; 0.5; 1.0; 2.5; 5.0 ]
      Density.Poisson 1.0,              [ 0.0; 1.0; 2.0; 5.0; 10.0 ]
      Density.Poisson 4.5,              [ 0.0; 1.0; 2.0; 5.0; 10.0 ]
      Density.Beta (2.0, 3.0),          [ 0.05; 0.25; 0.5; 0.75; 0.95 ]
      Density.Beta (0.5, 0.5),          [ 0.05; 0.25; 0.5; 0.75; 0.95 ]
      Density.Beta (1.0, 1.0),          [ 0.05; 0.25; 0.5; 0.75; 0.95 ]
      Density.Bernoulli 0.3,            [ 0.0; 1.0 ]
      Density.Bernoulli 0.9,            [ 0.0; 1.0 ] ]

/// Named towers for `dump-edgeworth` / `dump-cf`, each truncated to the orders
/// the expansions actually consume.
let private towerMatrix : (string * float[] * float list) list =
    [ "gaussian(0, 1) r=2", Dist.gaussianCumulants 0.0 1.0 2, [ -2.0; -1.0; 0.0; 1.0; 2.0 ]
      "gaussian(0, 1) r=6", Dist.gaussianCumulants 0.0 1.0 6, [ -2.0; -1.0; 0.0; 1.0; 2.0 ]
      "exponential(1) r=4", Dist.exponentialCumulants 1.0 4, [ 0.25; 0.5; 1.0; 2.0; 3.0 ]
      "exponential(1) r=6", Dist.exponentialCumulants 1.0 6, [ 0.25; 0.5; 1.0; 2.0; 3.0 ]
      "gamma(3, 1) r=4", Dist.gammaCumulants 3.0 1.0 4, [ 1.0; 2.0; 3.0; 4.0; 6.0 ]
      "gamma(3, 1) r=6", Dist.gammaCumulants 3.0 1.0 6, [ 1.0; 2.0; 3.0; 4.0; 6.0 ]
      "poisson(4) r=4", Dist.poissonCumulants 4.0 4, [ 2.0; 3.0; 4.0; 5.0; 6.0 ]
      "poisson(4) r=6", Dist.poissonCumulants 4.0 6, [ 2.0; 3.0; 4.0; 5.0; 6.0 ] ]

let private cfProbabilities = [ 0.05; 0.25; 0.5; 0.75; 0.95 ]

[<EntryPoint>]
let main argv =
    match argv with
    | [| "dump-cumulants" |] ->
        // Corpus oracle datasets (ppl/ cumulant tests). First: the pair-test
        // array (symmetric two-point; degenerate but analytic). Second: an
        // asymmetric N = 3 set exercising nonzero odd cumulants.
        printfn "-- data A: [[1,2],[3,4]] (N=2)"
        dumpCumulants [| [| 1.0; 2.0 |]; [| 3.0; 4.0 |] |] 4
        printfn "-- data B: [[1,2,4],[3,5,4]] (N=3)"
        dumpCumulants [| [| 1.0; 2.0; 4.0 |]; [| 3.0; 5.0; 4.0 |] |] 4
        printfn "-- data Z4: [[1,2,4,6],[3,5,4,2]] (N=4) -- 2-chunk merge oracle"
        dumpCumulants [| [| 1.0; 2.0; 4.0; 6.0 |]; [| 3.0; 5.0; 4.0; 2.0 |] |] 4
        printfn "-- data Z6: [[1,2,4,6,0,3],[3,5,4,2,1,7]] (N=6) -- 3-chunk merge oracle"
        dumpCumulants [| [| 1.0; 2.0; 4.0; 6.0; 0.0; 3.0 |]; [| 3.0; 5.0; 4.0; 2.0; 1.0; 7.0 |] |] 3

        // SECTION 1: joint 5-variable cumulants (mixed-cumulant blocks).
        printfn "-- data XY5: stacked X(2 vars) ++ Y(3 vars), N=2 (mixed-block oracle)"
        let stacked =
            [| [| 1.0; 2.0 |]; [| 3.0; 5.0 |]; [| 2.0; 4.0 |]; [| 1.0; 1.0 |]; [| 0.0; 2.0 |] |]
        dumpCumulants stacked 3
        printfn "-- labeled cells, data XY5 (rank 2, 3):"
        printLabeledCells (computeCumulants stacked 3) [ 2; 3 ]

        // SECTION 2: affine pushforward kappa'_k = W^(tensor k) kappa_k.
        printfn "-- affine: W=[[1,2],[0,1]] applied to data A2 dist"
        let a2 = [| [| 1.0; 2.0 |]; [| 3.0; 4.0 |] |]
        let w = [| [| 1.0; 2.0 |]; [| 0.0; 1.0 |] |]
        let distA2 : Dist.T = { Dim = 2; Order = 3; Kappa = computeCumulants a2 3 }
        let pushed = Dist.affine w [| 0.0; 0.0 |] distA2
        for k in 1 .. 3 do
            let cells =
                SymTensor.enumerate pushed.Kappa.[k - 1].Dim pushed.Kappa.[k - 1].Rank
                |> Seq.map (fun labels -> sprintf "%.12g" (SymTensor.get pushed.Kappa.[k - 1] labels))
                |> String.concat ", "
            printfn "kp%d = [%s]" k cells

        // SECTION 4: joint 5-variable cumulants at N=3 (nonzero mixed rank-3
        // blocks -- the N=2 XY5 oracle above has structurally zero rank 3).
        printfn "-- data XY5b: stacked N=3 (mixed-block oracle, nonzero rank 3)"
        let stacked3 =
            [| [| 1.0; 2.0; 4.0 |]; [| 3.0; 5.0; 4.0 |]; [| 2.0; 4.0; 1.0 |]
               [| 1.0; 1.0; 5.0 |]; [| 0.0; 2.0; 3.0 |] |]
        dumpCumulants stacked3 3
        printfn "-- labeled cells, data XY5b (rank 2, 3):"
        printLabeledCells (computeCumulants stacked3 3) [ 2; 3 ]

        // SECTION 5: affine pushforward on data B (nonzero kappa3).
        printfn "-- affine: W=[[1,2],[0,1]] applied to data B3 dist"
        let b3 = [| [| 1.0; 2.0; 4.0 |]; [| 3.0; 5.0; 4.0 |] |]
        let distB3 : Dist.T = { Dim = 2; Order = 3; Kappa = computeCumulants b3 3 }
        let pushedB = Dist.affine w [| 0.0; 0.0 |] distB3
        for k in 1 .. 3 do
            let cells =
                SymTensor.enumerate pushedB.Kappa.[k - 1].Dim pushedB.Kappa.[k - 1].Rank
                |> Seq.map (fun labels -> sprintf "%.12g" (SymTensor.get pushedB.Kappa.[k - 1] labels))
                |> String.concat ", "
            printfn "kp%d = [%s]" k cells

        // SECTION 3: free cumulants (non-crossing lattice).
        printfn "-- free cumulants (non-crossing), data B3"
        let fk = freeCumulants b3 4
        for k in 1 .. 4 do
            let cells =
                SymTensor.enumerate fk.[k - 1].Dim fk.[k - 1].Rank
                |> Seq.map (fun labels -> sprintf "%.12g" (SymTensor.get fk.[k - 1] labels))
                |> String.concat ", "
            printfn "fk%d = [%s]" k cells
        0
    | [| "dump-jet" |] ->
        // Corpus oracle scenarios for the compiler's dist_jet former. Every
        // jet is the exact (or deliberately truncated) set of derivatives
        // at the EMPIRICAL mean of the same datasets the corpus tests read,
        // so the corpus programs can rebuild the identical jets in-language
        // from cumulant(d, 1).
        let fmt (dist: Dist.T) =
            univCumulants dist |> Array.map (sprintf "%.12g") |> String.concat ", "
        // J1: univariate g(x) = x^2 on A1, order-6 dist, q = 3 strict.
        let a1 = [| 1.0; 2.0; 4.0; 6.0; 0.0; 3.0 |]
        let distA1 : Dist.T = { Dim = 1; Order = 6; Kappa = computeCumulants [| a1 |] 6 }
        let m1 = SymTensor.get distA1.Kappa.[0] [| 0 |]
        printfn "-- J1: x^2 on A1=[1,2,4,6,0,3], dist order 6, q=3 strict"
        printfn "jet1 = [%s]" (fmt (Dist.jetPushforward distA1 (m1 * m1) (univJet [| 2.0 * m1; 2.0 |]) 3 false))
        printfn "ref1 = [%s] (cumulants of the squared data -- must agree)"
            (fmt { Dim = 1; Order = 3; Kappa = computeCumulants [| a1 |> Array.map (fun x -> x * x) |] 3 })
        // J2: bivariate g(x,y) = x*y on data B, order-6 dist, q = 3 strict.
        let b = [| [| 1.0; 2.0; 4.0 |]; [| 3.0; 5.0; 4.0 |] |]
        let distB6 : Dist.T = { Dim = 2; Order = 6; Kappa = computeCumulants b 6 }
        let mx = SymTensor.get distB6.Kappa.[0] [| 0 |]
        let my = SymTensor.get distB6.Kappa.[0] [| 1 |]
        let jd1 = SymTensor.create 2 1
        SymTensor.set jd1 [| 0 |] my
        SymTensor.set jd1 [| 1 |] mx
        let jd2 = SymTensor.create 2 2
        SymTensor.set jd2 [| 0; 1 |] 1.0
        printfn "-- J2: x*y on B=[[1,2,4],[3,5,4]], dist order 6, q=3 strict"
        printfn "jet2 = [%s]" (fmt (Dist.jetPushforward distB6 (mx * my) [| jd1; jd2 |] 3 false))
        printfn "ref2 = [%s] (cumulants of the product data -- must agree)"
            (fmt { Dim = 1; Order = 3; Kappa = computeCumulants [| Array.map2 (*) b.[0] b.[1] |] 3 })
        // J3: the same x*y jet but the dist carries only order 4 -- CLOSED
        // mode (q*s = 6 > 4; partition blocks past order 4 are dropped).
        let distB4 : Dist.T = { Dim = 2; Order = 4; Kappa = computeCumulants b 4 }
        printfn "-- J3: x*y on B, dist order 4, q=3 CLOSED"
        printfn "jet3 = [%s]" (fmt (Dist.jetPushforward distB4 (mx * my) [| jd1; jd2 |] 3 true))
        // J4: truncated smooth map -- exp(x) as its degree-3 jet at the
        // mean of A1 (every derivative = exp(m)), q = 2 strict.
        let e = exp m1
        printfn "-- J4: exp(x) (degree-3 jet) on A1, dist order 6, q=2 strict"
        printfn "jet4 = [%s]" (fmt (Dist.jetPushforward distA1 e (univJet [| e; e; e |]) 2 false))
        // J5/J6: VECTOR map g(x,y) = (x + y, x*y) on data B -- the mixed-block
        // Faa di Bruno oracle. Joint output cumulant tensors printed per
        // order as flat cells in SymTensor.enumerate (canonical lex) order,
        // which is exactly the corpus flat-ArrayLit cell order.
        let fmtJoint (dist: Dist.T) (k: int) =
            SymTensor.enumerate dist.Dim k
            |> Seq.map (fun labels -> sprintf "%.12g" (SymTensor.get dist.Kappa.[k - 1] labels))
            |> String.concat ", "
        let jv1sum = SymTensor.create 2 1
        SymTensor.set jv1sum [| 0 |] 1.0
        SymTensor.set jv1sum [| 1 |] 1.0
        let g0v = [| mx + my; mx * my |]
        let jetsV = [| [| jv1sum |]; [| jd1; jd2 |] |]
        printfn "-- J5: (x+y, x*y) on B, dist order 6, q=3 strict (joint output)"
        let jv5 = Dist.jetPushforwardVec distB6 g0v jetsV 3 false
        for k in 1 .. 3 do printfn "jv5_k%d = [%s]" k (fmtJoint jv5 k)
        let transformed = [| Array.map2 (+) b.[0] b.[1]; Array.map2 (*) b.[0] b.[1] |]
        let rv5 : Dist.T = { Dim = 2; Order = 3; Kappa = computeCumulants transformed 3 }
        for k in 1 .. 3 do printfn "rv5_k%d = [%s] (cumulants of transformed data -- must agree)" k (fmtJoint rv5 k)
        printfn "-- J6: (x+y, x*y) on B, dist order 4, q=3 CLOSED"
        let jv6 = Dist.jetPushforwardVec distB4 g0v jetsV 3 true
        for k in 1 .. 3 do printfn "jv6_k%d = [%s]" k (fmtJoint jv6 k)
        0
    | [| "dump-logpdf" |] ->
        // Closed-form log-densities: every named family at 2-3 parameter
        // points, 5 evaluation points each (2 for the Bernoulli support).
        // -inf marks a point outside the support.
        for (fam, xs) in logpdfMatrix do
            printfn "-- %s" (Density.familyName fam)
            for x in xs do
                printfn "  x=%s  logpdf=%s" (gs x) (g17 (Density.logpdf fam x))
        printfn "-- lgamma (Lanczos g=7, n=9)"
        for x in [ 0.1; 0.5; 1.0; 1.5; 2.0; 3.5; 5.0; 10.0; 0.5 + 1e-3 ] do
            printfn "  x=%s  lgamma=%s" (gs x) (g17 (Density.lgamma x))
        0
    | [| "dump-edgeworth" |] ->
        // Edgeworth/Gram-Charlier density from a univariate cumulant tower.
        // NOT a density in general -- the tail can go negative, which is the
        // honesty check dist_negativity measures on the compiler side.
        for (name, tower, xs) in towerMatrix do
            printfn "-- %s: kappa = [%s]" name (fmtTower tower)
            let t = List.ofArray tower
            for x in xs do
                printfn "  x=%s  edgeworth_pdf=%s" (gs x) (g17 (Expansion.edgeworthPdf t x))
        printfn "-- exponential(1) r=6 tail (negativity probe)"
        for x in [ 3.0; 4.0; 5.0; 6.0; 7.0 ] do
            printfn "  x=%s  edgeworth_pdf=%s" (gs x)
                    (g17 (Expansion.edgeworthPdf (List.ofArray (Dist.exponentialCumulants 1.0 6)) x))
        0
    | [| "dump-cf" |] ->
        // Cornish-Fisher quantiles from a univariate cumulant tower, plus the
        // standard-normal quantiles (Wichura AS241) they are built on.
        printfn "-- standard normal quantile (AS241 PPND16)"
        for p in cfProbabilities @ [ 0.01; 0.99; 0.975; 0.999 ] do
            printfn "  p=%s  z=%s" (gs p) (g17 (Expansion.normalQuantile p))
        for (name, tower, _) in towerMatrix do
            printfn "-- %s: kappa = [%s]" name (fmtTower tower)
            let t = List.ofArray tower
            for p in cfProbabilities do
                printfn "  p=%s  cf_quantile=%s" (gs p) (g17 (Expansion.cornishFisher t p))
        0
    | [| "dump-conjugate" |] ->
        // Conjugate posteriors: prior hyperparameters + sufficient statistics
        // in, posterior hyperparameters out. One worked example per pair.
        let n = nnData.Length
        let nnPost = Conjugate.normalNormal nnPriorMean nnPriorVar nnLikVar n (sumOf nnData)
        printfn "-- Normal-Normal (known variance)"
        printfn "  prior: N(m0=%s, v0=%s), likelihood variance sigma2=%s"
                (gs nnPriorMean) (gs nnPriorVar) (gs nnLikVar)
        printfn "  data: [%s] (n=%d, sum=%s)"
                (nnData |> Array.map gs |> String.concat ", ") n (g17 (sumOf nnData))
        printfn "  post_mean=%s" (g17 nnPost.PostMean)
        printfn "  post_var=%s" (g17 nnPost.PostVar)
        printfn "  tower r=4 = [%s]" (fmtTower (Conjugate.normalNormalTower nnPost 4))

        let bbPost = Conjugate.betaBernoulli bbPriorA bbPriorB bbN bbK
        printfn "-- Beta-Bernoulli"
        printfn "  prior: Beta(a0=%s, b0=%s); data: n=%d, k=%d" (gs bbPriorA) (gs bbPriorB) bbN bbK
        printfn "  post_a=%s" (g17 bbPost.A)
        printfn "  post_b=%s" (g17 bbPost.B)
        printfn "  post_mean=%s" (g17 (Conjugate.betaMean bbPost))
        printfn "  post_var=%s" (g17 (Conjugate.betaVar bbPost))

        let gpPost = Conjugate.gammaPoisson gpPriorA gpPriorB gpCounts.Length (sumOf gpCounts)
        printfn "-- Gamma-Poisson"
        printfn "  prior: Gamma(a0=%s, b0=%s) on the rate" (gs gpPriorA) (gs gpPriorB)
        printfn "  data: [%s] (n=%d, sum=%s)"
                (gpCounts |> Array.map gs |> String.concat ", ") gpCounts.Length (g17 (sumOf gpCounts))
        printfn "  post_shape=%s" (g17 gpPost.Shape)
        printfn "  post_rate=%s" (g17 gpPost.Rate)
        printfn "  post_mean=%s" (g17 (gpPost.Shape / gpPost.Rate))
        printfn "  post_var=%s" (g17 (gpPost.Shape / (gpPost.Rate ** 2.0)))
        printfn "  tower r=4 = [%s]" (fmtTower (Conjugate.gammaPoissonTower gpPost 4))

        let nigPost =
            Conjugate.normalInvGamma nigM0 nigK0 nigA0 nigB0 n (sumOf nnData) (sumSqOf nnData)
        printfn "-- Normal-InverseGamma (unknown mean and variance)"
        printfn "  prior: m0=%s, k0=%s, a0=%s, b0=%s" (gs nigM0) (gs nigK0) (gs nigA0) (gs nigB0)
        printfn "  data: [%s] (n=%d, sum=%s, sumsq=%s)"
                (nnData |> Array.map gs |> String.concat ", ") n (g17 (sumOf nnData)) (g17 (sumSqOf nnData))
        printfn "  post_m=%s" (g17 nigPost.M)
        printfn "  post_kappa=%s" (g17 nigPost.Kappa)
        printfn "  post_alpha=%s" (g17 nigPost.Alpha)
        printfn "  post_beta=%s" (g17 nigPost.Beta)
        printfn "  post_mean_mu=%s" (g17 (Conjugate.nigMeanMu nigPost))
        printfn "  post_var_mu=%s" (g17 (Conjugate.nigVarMu nigPost))
        printfn "  post_mean_sigma2=%s" (g17 (Conjugate.nigMeanSigma2 nigPost))
        printfn "  post_mean_precision=%s" (g17 (Conjugate.nigMeanPrecision nigPost))
        printfn "  precision tower r=4 = [%s]" (fmtTower (Conjugate.normalInvGammaPrecisionTower nigPost 4))
        0
    | _ ->
    testCombinatorics ()
    testSymTensor ()
    testMomentCumulant ()
    testDistTower ()
    testJetPushforward ()
    testJetPushforwardVec ()
    testDensity ()
    testEdgeworth ()
    testCornishFisher ()
    testConjugate ()
    testStreaming ()
    testStability ()
    demoDerivedFormulas ()
    testFullCircle ()
    summary ()
