namespace MomentAlgebra

/// Moment <-> cumulant conversion as Möbius inversion on the set-partition
/// lattice — the load-bearing algebra of the moment-algebra PPL idea.
///
///   mu(S)    = sum over partitions pi of the positions of S:
///                prod over blocks B in pi of kappa(labels of S at B)
///   kappa(S) = sum over partitions pi:
///                (-1)^(|pi|-1) * (|pi|-1)! * prod over blocks of mu(...)
///
/// Both directions are ONE weighted sum over the same lattice; in Blade this
/// expansion is a compile-time pass emitting a contraction kernel per rank.
module MomentCumulant =

    /// tensors.[k-1] is the rank-k joint tensor over the same dimension,
    /// k = 1 .. r. Shared partition-lattice convolution; only the weight on
    /// the number of blocks differs between the two directions.
    let private convolve (weight: int -> float) (source: SymTensor.T[]) : SymTensor.T[] =
        let r = source.Length
        let d = source.[0].Dim
        [| for k in 1 .. r ->
             let out = SymTensor.create d k
             for labels in SymTensor.enumerate d k do
                 let mutable total = 0.0
                 for partition in Combinatorics.setPartitions k do
                     let mutable prod = weight partition.Length
                     for block in partition do
                         let sub = block |> Array.map (fun pos -> labels.[pos])
                         prod <- prod * SymTensor.get source.[sub.Length - 1] sub
                     total <- total + prod
                 SymTensor.set out labels total
             out |]

    let momentsFromCumulants (kappa: SymTensor.T[]) : SymTensor.T[] =
        convolve (fun _ -> 1.0) kappa

    let cumulantsFromMoments (mu: SymTensor.T[]) : SymTensor.T[] =
        convolve
            (fun nBlocks ->
                (if nBlocks % 2 = 1 then 1.0 else -1.0) * Combinatorics.factorial (nBlocks - 1))
            mu
