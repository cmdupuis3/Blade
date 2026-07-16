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
        check (sprintf "Bell(%d) = %d" n bells.[n - 1]) (Combinatorics.bell n = bells.[n - 1])
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
        check (sprintf "rank bijection d=%d r=%d" d r)
              (ranks = [| 0 .. SymTensor.storageSize d r - 1 |])
    // sum of joint-r! multiplicities over canonical entries recovers the dense count
    for (d, r) in [ (3, 4); (2, 5) ] do
        let total = SymTensor.enumerate d r |> Array.sumBy SymTensor.multiplicity
        checkClose (sprintf "sum multiplicities d=%d r=%d = d^r" d r) 1e-9 (float d ** float r) total
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
        checkArrayClose (sprintf "round trip rank %d" k) 1e-9 kappa.[k - 1].Data back.[k - 1].Data
    // Isserlis: zero-mean Gaussian pair, rho = 0.5 — only pairings survive
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
    // Poisson(2) raw moments: Touchard/Stirling — 2, 6, 22, 94
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
    // kappa(Y) = [0; 1; 0; 6] — the classic product-normal excess kurtosis.
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

/// Univariate jet: vals.[k-1] = g^(k)(μ), packed as dim-1 rank-k tensors.
let univJet (vals: float[]) : SymTensor.T[] =
    vals |> Array.mapi (fun i v ->
        let t = SymTensor.create 1 (i + 1)
        t.Data.[0] <- v
        t)

let testJetPushforward () =
    section "jet pushforward (full Faà di Bruno, scalar output)"
    // 1) Exact jet of g(x,y) = x·y at μ = (0,0) on iid standard normals:
    //    the product-normal cumulants — agrees with polyMoments exactly.
    let zn = Dist.ofIndependent [| Dist.gaussianCumulants 0.0 1.0 8; Dist.gaussianCumulants 0.0 1.0 8 |] 8
    let d1 = SymTensor.create 2 1
    let d2 = SymTensor.create 2 2
    SymTensor.set d2 [| 0; 1 |] 1.0
    let viaJet = Dist.jetPushforward zn 0.0 [| d1; d2 |] 4 false
    checkArrayClose "jet x·y on iid normals = [0,1,0,6]" 1e-9 [| 0.0; 1.0; 0.0; 6.0 |] (univCumulants viaJet)
    // 2) g(x) = x² on Gamma(3,2): jet (μ², 2μ, 2) vs the exact polynomial pushforward.
    let gam = univariate (Dist.gammaCumulants 3.0 2.0 6)
    let mu = gam.Kappa.[0].Data.[0]
    let viaJet2 = Dist.jetPushforward gam (mu * mu) (univJet [| 2.0 * mu; 2.0 |]) 3 false
    let viaPoly2 = Dist.polyMoments gam [ (1.0, [| 2 |]) ] 3
    checkArrayClose "jet x² = poly x² (Gamma(3,2))" 1e-9 (univCumulants viaPoly2) (univCumulants viaJet2)
    // 3) g(x) = x³ + 2x on Exp(1): mixed-degree jet at μ = 1 vs polyMoments.
    let ex = univariate (Dist.exponentialCumulants 1.0 6)
    let viaJet3 = Dist.jetPushforward ex 3.0 (univJet [| 5.0; 6.0; 6.0 |]) 2 false
    let viaPoly3 = Dist.polyMoments ex [ (1.0, [| 3 |]); (2.0, [| 1 |]) ] 2
    checkArrayClose "jet x³+2x = poly (Exp(1))" 1e-9 (univCumulants viaPoly3) (univCumulants viaJet3)
    // 4) A 1-jet IS the affine map: 2X + 10 on Exp(1) — note g0 = g(μ) = 12,
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
    checkArrayClose "Gaussian closure = strict (x², N(1,2))" 1e-12 (univCumulants strict4) (univCumulants closed2)
    // 6) The strict order guard: q·s exceeds the carried order.
    checkThrows "jet order guard" (fun () -> Dist.jetPushforward g2 1.0 jetSq 2 false |> ignore)
    // 7) THE EMPIRICAL-DISTRIBUTION IDENTITY: pushing the empirical dist of
    //    the data through an exact polynomial jet equals the empirical
    //    cumulants of the transformed data — the property the compiler's
    //    two-route corpus test pins.
    let a1 = [| 1.0; 2.0; 4.0; 6.0; 0.0; 3.0 |]
    let distA1 : Dist.T = { Dim = 1; Order = 6; Kappa = computeCumulants [| a1 |] 6 }
    let m = distA1.Kappa.[0].Data.[0]
    let pushed = Dist.jetPushforward distA1 (m * m) (univJet [| 2.0 * m; 2.0 |]) 3 false
    let direct : Dist.T = { Dim = 1; Order = 3; Kappa = computeCumulants [| a1 |> Array.map (fun x -> x * x) |] 3 }
    checkArrayClose "empirical push x² = cumulants of squared data" 1e-9 (univCumulants direct) (univCumulants pushed)

// ---------------------------------------------------------------------------

let private compareAcc (name: string) (relTol: float) (absTol: float) (reference: Streaming.Acc) (acc: Streaming.Acc) =
    checkClose (sprintf "%s: N" name) 1e-9 reference.N acc.N
    checkArrayClose (sprintf "%s: mean" name) 1e-10 reference.Mean acc.Mean
    for p in 2 .. reference.Order do
        let refT = reference.M.[p - 2]
        let accT = acc.M.[p - 2]
        let labels = SymTensor.labelTable reference.Dim p
        for e in 0 .. refT.Data.Length - 1 do
            checkCloseRel (sprintf "%s: M%d(%s)" name p (fmtLabels labels.[e])) relTol absTol
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
            checkCloseRel (sprintf "kappa%d(%s)" k (fmtLabels labels.[e])) relTol absTol ev av
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
        printfn "-- data Z4: [[1,2,4,6],[3,5,4,2]] (N=4) — 2-chunk merge oracle"
        dumpCumulants [| [| 1.0; 2.0; 4.0; 6.0 |]; [| 3.0; 5.0; 4.0; 2.0 |] |] 4
        printfn "-- data Z6: [[1,2,4,6,0,3],[3,5,4,2,1,7]] (N=6) — 3-chunk merge oracle"
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
        // blocks — the N=2 XY5 oracle above has structurally zero rank 3).
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
        // J1: univariate g(x) = x² on A1, order-6 dist, q = 3 strict.
        let a1 = [| 1.0; 2.0; 4.0; 6.0; 0.0; 3.0 |]
        let distA1 : Dist.T = { Dim = 1; Order = 6; Kappa = computeCumulants [| a1 |] 6 }
        let m1 = SymTensor.get distA1.Kappa.[0] [| 0 |]
        printfn "-- J1: x² on A1=[1,2,4,6,0,3], dist order 6, q=3 strict"
        printfn "jet1 = [%s]" (fmt (Dist.jetPushforward distA1 (m1 * m1) (univJet [| 2.0 * m1; 2.0 |]) 3 false))
        printfn "ref1 = [%s] (cumulants of the squared data — must agree)"
            (fmt { Dim = 1; Order = 3; Kappa = computeCumulants [| a1 |> Array.map (fun x -> x * x) |] 3 })
        // J2: bivariate g(x,y) = x·y on data B, order-6 dist, q = 3 strict.
        let b = [| [| 1.0; 2.0; 4.0 |]; [| 3.0; 5.0; 4.0 |] |]
        let distB6 : Dist.T = { Dim = 2; Order = 6; Kappa = computeCumulants b 6 }
        let mx = SymTensor.get distB6.Kappa.[0] [| 0 |]
        let my = SymTensor.get distB6.Kappa.[0] [| 1 |]
        let jd1 = SymTensor.create 2 1
        SymTensor.set jd1 [| 0 |] my
        SymTensor.set jd1 [| 1 |] mx
        let jd2 = SymTensor.create 2 2
        SymTensor.set jd2 [| 0; 1 |] 1.0
        printfn "-- J2: x·y on B=[[1,2,4],[3,5,4]], dist order 6, q=3 strict"
        printfn "jet2 = [%s]" (fmt (Dist.jetPushforward distB6 (mx * my) [| jd1; jd2 |] 3 false))
        printfn "ref2 = [%s] (cumulants of the product data — must agree)"
            (fmt { Dim = 1; Order = 3; Kappa = computeCumulants [| Array.map2 (*) b.[0] b.[1] |] 3 })
        // J3: the same x·y jet but the dist carries only order 4 — CLOSED
        // mode (q·s = 6 > 4; partition blocks past order 4 are dropped).
        let distB4 : Dist.T = { Dim = 2; Order = 4; Kappa = computeCumulants b 4 }
        printfn "-- J3: x·y on B, dist order 4, q=3 CLOSED"
        printfn "jet3 = [%s]" (fmt (Dist.jetPushforward distB4 (mx * my) [| jd1; jd2 |] 3 true))
        // J4: truncated smooth map — exp(x) as its degree-3 jet at the
        // mean of A1 (every derivative = exp(m)), q = 2 strict.
        let e = exp m1
        printfn "-- J4: exp(x) (degree-3 jet) on A1, dist order 6, q=2 strict"
        printfn "jet4 = [%s]" (fmt (Dist.jetPushforward distA1 e (univJet [| e; e; e |]) 2 false))
        0
    | _ ->
    testCombinatorics ()
    testSymTensor ()
    testMomentCumulant ()
    testDistTower ()
    testJetPushforward ()
    testStreaming ()
    testStability ()
    demoDerivedFormulas ()
    testFullCircle ()
    summary ()
