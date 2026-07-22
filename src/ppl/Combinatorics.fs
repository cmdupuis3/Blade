namespace MomentAlgebra

/// Compile-time combinatorics: the set-partition lattice, subset masks,
/// multinomials. In a Blade integration this is machinery the COMPILER runs
/// during code generation (the same joint-r! bookkeeping the symmetry pass
/// already does); in this prototype it runs once at startup and is cached.
module Combinatorics =

    let factorial (n: int) : float =
        let mutable acc = 1.0
        for i in 2 .. n do acc <- acc * float i
        acc

    /// Binomial coefficient (exact for the small n used here).
    /// Stepwise num holds C(n-k+i, i), which is always integral.
    let binomial (n: int) (k: int) : int =
        if k < 0 || k > n then 0
        else
            let k = min k (n - k)
            let mutable num = 1L
            for i in 1 .. k do
                num <- num * int64 (n - k + i) / int64 i
            int num

    /// All set partitions of positions [0 .. n-1], each partition an array of
    /// blocks (each block a sorted int array). Count = Bell(n). Cached per n:
    /// the moment<->cumulant conversions hit this hard for repeated ranks.
    let private partitionCache = System.Collections.Generic.Dictionary<int, int[][][]>()

    let setPartitions (n: int) : int[][][] =
        match partitionCache.TryGetValue n with
        | true, v -> v
        | _ ->
            // Insert element e into each block of each partition of [0..e-1],
            // or open a new block.
            let mutable parts : int list list list = [ [] ]
            for e in 0 .. n - 1 do
                parts <-
                    parts
                    |> List.collect (fun p ->
                        let withNew = [ e ] :: p
                        let withExisting =
                            p |> List.mapi (fun i _ ->
                                p |> List.mapi (fun j b -> if i = j then e :: b else b))
                        withNew :: withExisting)
            let arr =
                parts
                |> List.map (fun p ->
                    p |> List.map (fun b -> b |> List.sort |> List.toArray) |> List.toArray)
                |> List.toArray
            partitionCache.[n] <- arr
            arr

    let bell (n: int) : int = (setPartitions n).Length

    /// All ways to write m as an ordered sum of t nonnegative integers
    /// (used by the exact polynomial pushforward's multinomial expansion).
    let rec compositions (m: int) (t: int) : int[] list =
        if t = 1 then [ [| m |] ]
        else
            [ for first in 0 .. m do
                for rest in compositions (m - first) (t - 1) do
                    yield Array.append [| first |] rest ]
