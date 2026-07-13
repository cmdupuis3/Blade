namespace MomentAlgebra

/// Prototype 3: streaming/mergeable central comoment accumulators whose
/// update kernel is DERIVED, not hand-coded — the arbitrary-order
/// multivariate generalization of Welford's algorithm (Pébay's formulas).
///
/// Derivation (runs once per (d, r) as plan construction; in Blade this is a
/// compile-time pass): with delta = mu_B - mu_A and n = n_A + n_B,
///
///   i in A:  x_i - mu' = (x_i - mu_A) - (n_B/n) delta
///   i in B:  x_i - mu' = (x_i - mu_B) + (n_A/n) delta
///
/// Expanding the rank-p central product over WHICH positions take the delta
/// factor (the subset lattice) gives, for every canonical entry S:
///
///   M_S' = sum over K subset of positions(S):
///            M_{S\K}(A) * prod_{k in K} (-(n_B/n) delta)
///          + M_{S\K}(B) * prod_{k in K} (+(n_A/n) delta)
///
/// with the boundary cases M_empty = n_block and M_single = 0 (terms with
/// |S\K| = 1 are pruned at plan time). Numerically stable because only
/// centered quantities are ever accumulated.
module Streaming =

    /// One derived term of the merge kernel for one canonical entry.
    type private Term = {
        DeltaLabels: int[]   // coordinate labels of the positions in K
        SrcRank: int         // |S \ K|; 0 => count, >= 2 => tensor lookup
        SrcOffset: int       // packed offset in the rank-SrcRank tensor
    }

    type private Plan = {
        Entries: Term[][][]      // [p-2][entryOffset] -> term list
        LabelsByRank: int[][][]  // [p-2][entryOffset] -> canonical labels
    }

    let private planCache = System.Collections.Generic.Dictionary<int * int, Plan>()

    let private buildPlan (d: int) (r: int) : Plan =
        let labelsByRank = [| for p in 2 .. r -> SymTensor.labelTable d p |]
        let entries =
            [| for p in 2 .. r ->
                 labelsByRank.[p - 2]
                 |> Array.map (fun labels ->
                     [| for mask in 0 .. (1 <<< p) - 1 do
                          let inK    = [| for k in 0 .. p - 1 do if (mask >>> k) &&& 1 = 1 then yield labels.[k] |]
                          let rest   = [| for k in 0 .. p - 1 do if (mask >>> k) &&& 1 = 0 then yield labels.[k] |]
                          if rest.Length <> 1 then  // M_single = 0: prune at "compile time"
                              yield { DeltaLabels = inK
                                      SrcRank = rest.Length
                                      SrcOffset = if rest.Length >= 2 then SymTensor.rankOf rest else 0 } |]) |]
        { Entries = entries; LabelsByRank = labelsByRank }

    let private getPlan (d: int) (r: int) : Plan =
        match planCache.TryGetValue((d, r)) with
        | true, v -> v
        | _ ->
            let p = buildPlan d r
            planCache.[(d, r)] <- p
            p

    type Acc = {
        Dim: int
        Order: int
        mutable N: float
        Mean: float[]
        M: SymTensor.T[]   // M.[p-2]: rank-p central comoment SUMS, p = 2..r
    }

    let create (d: int) (r: int) : Acc =
        if r < 2 then failwith "Streaming.create: order must be >= 2"
        getPlan d r |> ignore
        { Dim = d; Order = r; N = 0.0
          Mean = Array.zeroCreate d
          M = [| for p in 2 .. r -> SymTensor.create d p |] }

    /// M_S lookup with boundary cases M_empty = n, M_single = 0.
    let inline private mOf (acc: Acc) (rank: int) (offset: int) : float =
        if rank = 0 then acc.N
        elif rank = 1 then 0.0
        else acc.M.[rank - 2].Data.[offset]

    /// Merge two accumulators (pure; used for chunked/parallel reduction —
    /// the streaming analogue of Blade's comm-group reduction).
    let merge (a: Acc) (b: Acc) : Acc =
        if a.Dim <> b.Dim || a.Order <> b.Order then failwith "Streaming.merge: shape mismatch"
        let n = a.N + b.N
        if n = 0.0 then create a.Dim a.Order
        else
            let plan = getPlan a.Dim a.Order
            let delta = Array.init a.Dim (fun i -> b.Mean.[i] - a.Mean.[i])
            let cA = -b.N / n
            let cB =  a.N / n
            let out = create a.Dim a.Order
            for p in 2 .. a.Order do
                let entries = plan.Entries.[p - 2]
                let data = out.M.[p - 2].Data
                for e in 0 .. entries.Length - 1 do
                    let mutable tot = 0.0
                    for term in entries.[e] do
                        let mutable prodA = mOf a term.SrcRank term.SrcOffset
                        let mutable prodB = mOf b term.SrcRank term.SrcOffset
                        for lbl in term.DeltaLabels do
                            prodA <- prodA * (cA * delta.[lbl])
                            prodB <- prodB * (cB * delta.[lbl])
                        tot <- tot + prodA + prodB
                    data.[e] <- tot
            out.N <- n
            for i in 0 .. a.Dim - 1 do
                out.Mean.[i] <- a.Mean.[i] + (b.N / n) * delta.[i]
            out

    /// In-place single-observation update: the merge specialized to a
    /// singleton block (n_B = 1, all central sums of B zero, so B contributes
    /// only through the K = S term). Ranks are updated in DESCENDING order:
    /// rank p reads only itself (K = empty) and lower ranks, so in-place is safe.
    let updateOne (acc: Acc) (x: float[]) =
        let plan = getPlan acc.Dim acc.Order
        let n = acc.N + 1.0
        let cA = -1.0 / n
        let cB = acc.N / n
        let delta = Array.init acc.Dim (fun i -> x.[i] - acc.Mean.[i])
        for p = acc.Order downto 2 do
            let entries = plan.Entries.[p - 2]
            let data = acc.M.[p - 2].Data
            for e in 0 .. entries.Length - 1 do
                let mutable tot = 0.0
                for term in entries.[e] do
                    let mutable prodA = mOf acc term.SrcRank term.SrcOffset
                    for lbl in term.DeltaLabels do
                        prodA <- prodA * (cA * delta.[lbl])
                    tot <- tot + prodA
                    if term.SrcRank = 0 then      // B-side survives only at K = S
                        let mutable prodB = 1.0
                        for lbl in term.DeltaLabels do
                            prodB <- prodB * (cB * delta.[lbl])
                        tot <- tot + prodB
                data.[e] <- tot
        acc.N <- n
        for i in 0 .. acc.Dim - 1 do
            acc.Mean.[i] <- acc.Mean.[i] + delta.[i] / n

    /// Freeze into a Dist: central sums / n are central moments; the partition
    /// formula with mu_1 = 0 turns central moments into cumulants of the
    /// centered variable; kappa_1 is then the mean.
    let finalize (acc: Acc) : Dist.T =
        let d, r = acc.Dim, acc.Order
        let centralMu =
            [| for k in 1 .. r ->
                 if k = 1 then SymTensor.create d 1
                 else SymTensor.scale (1.0 / acc.N) acc.M.[k - 2] |]
        let kappa = MomentCumulant.cumulantsFromMoments centralMu
        for i in 0 .. d - 1 do
            SymTensor.set kappa.[0] [| i |] acc.Mean.[i]
        { Dim = d; Order = r; Kappa = kappa }

    /// Human-readable derived merge formula for univariate order p — the
    /// artifact a Blade compiler pass would document. Grouping the subset
    /// terms by |K| collapses them into binomial coefficients; the printed
    /// p = 2, 3, 4 lines reproduce Pébay (2008) exactly.
    let mergeFormulaText (p: int) : string =
        let sb = System.Text.StringBuilder()
        sb.Append(sprintf "M%d' = M%d_A + M%d_B" p p p) |> ignore
        for k in 1 .. p do
            let src = p - k
            if src <> 1 then
                let c = Combinatorics.binomial p k
                let coef = if c = 1 then "" else sprintf "%d*" c
                let mA = if src = 0 then "n_A" else sprintf "M%d_A" src
                let mB = if src = 0 then "n_B" else sprintf "M%d_B" src
                sb.Append(sprintf "\n        + %s[ %s*(-n_B*d/n)^%d + %s*(n_A*d/n)^%d ]" coef mA k mB k) |> ignore
        sb.ToString()

    /// The numerically DOOMED baseline: accumulate raw power sums, recover
    /// central moments at the end by binomial recombination. Kept for the
    /// catastrophic-cancellation demonstration.
    let naiveCentral (xs: float[]) (r: int) : float[] =
        let n = float xs.Length
        let s = Array.zeroCreate (r + 1)   // s.[k] = sum x^k
        s.[0] <- n
        for x in xs do
            let mutable p = 1.0
            for k in 1 .. r do
                p <- p * x
                s.[k] <- s.[k] + p
        let mean = s.[1] / n
        [| for p in 2 .. r ->
             let mutable m = 0.0
             for k in 0 .. p do
                 m <- m + float (Combinatorics.binomial p k) * (s.[k] / n) * (-mean) ** float (p - k)
             m |]
