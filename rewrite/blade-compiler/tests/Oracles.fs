// Independent F# oracles for the differential harnesses: permutation-sum
// Reynolds (symmetric/antisymmetric), Hermitian Gram, and the exact
// simplex cell-count math used by the timing harness. These are the
// correctness references the optimized Blade paths must match — kept in
// their own file so they can be reviewed against independently verified
// truth (audit §2.3 / plan Phase 0.2). Extracted verbatim from Main.fs.
//
// REVIEWED against hand-computed / analytic values (Phase 0.2, 2026-07-11):
// tests/Test_Oracles.fs pins each oracle to independently derived truth
// (Vandermonde identity, degenerate-kernel vanishing, a hand-multiplied
// complex Gram, the factorial form of C(n+r-1, r), ...) and documents the
// conventions: Reynolds sums are UNNORMALIZED (no 1/r!), the symmetric
// rank-2 oracle doubles on the diagonal, strict tuples are lexicographic.
// Change an oracle -> re-derive the affected values there BY HAND.
module Blade.Tests.Oracles

open System

/// Deterministic PRNG (fixed seed per call site) so runs are reproducible while
/// still exercising "random" values rather than one hand-picked input.
let mkRng (seed: int) = System.Random(seed)

/// Oracle: antisymmetric Reynolds of a rank-r kernel over A (length n), computed
/// by summing sgn(sigma)*g(permuted components) over all permutations, stored on
/// strict canonical tuples i0<i1<...<i_{r-1}. Independent of Blade's strict
/// iteration — this is the reference the optimized path must match.
let oracleAntisymReynolds (a: float[]) (r: int) (g: float[] -> float) : float list =
    let n = a.Length
    // permutations of [0..r-1] with sign
    let rec perms lst =
        match lst with
        | [] -> [[]]
        | _ -> lst |> List.collect (fun x -> perms (List.filter ((<>) x) lst) |> List.map (fun p -> x :: p))
    let sign (p: int list) =
        let arr = List.toArray p
        let mutable s = 1
        for i in 0 .. arr.Length - 1 do
            for j in i+1 .. arr.Length - 1 do
                if arr.[i] > arr.[j] then s <- -s
        float s
    let allPerms = perms [0 .. r-1]
    let out = System.Collections.Generic.List<float>()
    // strict canonical tuples
    let rec rec_ (start: int) (acc: int list) =
        if List.length acc = r then
            let tup = List.toArray (List.rev acc)
            let mutable v = 0.0
            for sigma in allPerms do
                let sg = List.toArray sigma
                let vals = [| for k in 0 .. r-1 -> a.[tup.[sg.[k]]] |]
                v <- v + sign sigma * g vals
            out.Add v
        else
            for i in start .. n-1 do rec_ (i+1) (i :: acc)
    rec_ 0 []
    List.ofSeq out

/// Oracle: gram(A,A) for complex A (m x k), result[i][j] = sum_k A[i][k]*conj(A[j][k]),
/// returned as the upper-triangle canonical print order [i][jr] (jr = j-i),
/// matching how a SymHermitian array prints. Re/im pairs.
let oracleGramHermitian (re: float[,]) (im: float[,]) : (float * float) list =
    let m = Array2D.length1 re
    let k = Array2D.length2 re
    let out = System.Collections.Generic.List<float * float>()
    for i in 0 .. m-1 do
        for j in i .. m-1 do
            let mutable sr = 0.0
            let mutable si = 0.0
            for t in 0 .. k-1 do
                // A[i][t] * conj(A[j][t]) = (ar+i*ai)(br - i*bi)
                let ar, ai = re.[i,t], im.[i,t]
                let br, bi = re.[j,t], im.[j,t]
                sr <- sr + (ar*br + ai*bi)
                si <- si + (ai*br - ar*bi)
            out.Add (sr, si)
    List.ofSeq out

/// Oracle: symmetric Reynolds of a rank-2 kernel over A (length n) — sum over
/// permutations WITHOUT sign, on inclusive canonical pairs i<=j. The symmetric
/// analog of oracleAntisymReynolds; used as the compact source for symmetric
/// decompact.
let oracleSymReynolds2 (a: float[]) (g: float[] -> float) : Map<int*int, float> =
    let n = a.Length
    let mutable m = Map.empty
    for i in 0 .. n-1 do
        for j in i .. n-1 do
            // perms of (i,j): identity + swap, both unsigned
            let v = g [| a.[i]; a.[j] |] + g [| a.[j]; a.[i] |]
            m <- Map.add (i, j) v m
    m

/// Oracle: antisymmetric Reynolds rank-2, strict pairs i<j (signed). Returns the
/// compact source map used for antisym decompact.
let oracleAntiReynolds2 (a: float[]) (g: float[] -> float) : Map<int*int, float> =
    let n = a.Length
    let mutable m = Map.empty
    for i in 0 .. n-1 do
        for j in i+1 .. n-1 do
            // identity (+) minus swap (-)
            let v = g [| a.[i]; a.[j] |] - g [| a.[j]; a.[i] |]
            m <- Map.add (i, j) v m
    m

/// Factorial (small r only).
let fact (n: int) : float =
    let mutable r = 1.0
    for i in 2 .. n do r <- r * float i
    r

/// Inclusive-simplex count C(n+r-1, r): the number of distinct multisets of
/// size r from n values — i.e. the cells a single rank-r SYMMETRIC group stores
/// / iterates over an extent-n axis (the canonical triangular region with its
/// diagonal). Computed as a falling/rising product to avoid large factorials.
let binomIncl (n: int) (r: int) : float =
    let mutable acc = 1.0
    for k in 0 .. r - 1 do
        acc <- acc * float (n + k) / float (k + 1)
    acc

/// Exact finite-n speedup limit for a product-symmetric application: the dense
/// cell count divided by the symmetric (simplex) cell count, per axis. Each
/// entry of `axisExtents` is one S-dim shared by a rank-r group; the symmetric
/// arm visits C(ext+r-1, r) on that axis while dense visits ext^r, so the exact
/// limit is the product over axes of ext^r / C(ext+r-1, r). As every ext → ∞
/// this approaches (r!)^d (d = number of axes); at finite n it is strictly
/// below, and is the genuinely ACHIEVABLE target at that problem size. For d=1
/// (a single axis) this is just n^r / C(n+r-1, r).
let exactSimplexRatio (r: int) (axisExtents: int list) : float =
    axisExtents
    |> List.fold (fun acc ext ->
        let dense = (float ext) ** float r
        let sym = binomIncl ext r
        if sym > 0.0 then acc * (dense / sym) else acc) 1.0
