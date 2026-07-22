namespace MomentAlgebra

/// Packed symmetric tensor: rank r over dimension d, one float per canonical
/// multiset (non-decreasing index tuple), C(d+r-1, r) entries. This mirrors
/// Blade's SymIdx<r, d> inclusive-combinadic placement class
/// (PlaceCombinatorial SymSymmetric): storage and ranking are exactly what
/// the compiler's CNS machinery emits; here it is a hand-rolled model.
module SymTensor =

    type T = {
        Dim: int
        Rank: int
        Data: float[]
    }

    /// Multisets of size r from d symbols.
    let storageSize (d: int) (r: int) : int = Combinatorics.binomial (d + r - 1) r

    let create (d: int) (r: int) : T =
        { Dim = d; Rank = r; Data = Array.zeroCreate (storageSize d r) }

    /// Colex combinadic rank of a canonical (non-decreasing) index tuple:
    /// map i_k -> j_k = i_k + k (strictly increasing), rank = sum C(j_k, k+1).
    let rankOf (idx: int[]) : int =
        let mutable acc = 0
        for k in 0 .. idx.Length - 1 do
            acc <- acc + Combinatorics.binomial (idx.[k] + k) (k + 1)
        acc

    /// All canonical index tuples of rank r over dim d (non-decreasing).
    /// Enumeration order is lexicographic, NOT storage order; use rankOf to
    /// map a tuple to its packed offset.
    let enumerate (d: int) (r: int) : int[][] =
        if r = 0 then [| [||] |]
        else
            let result = ResizeArray<int[]>()
            let idx = Array.zeroCreate r
            let rec go (pos: int) (minV: int) =
                if pos = r then result.Add(Array.copy idx)
                else
                    for v in minV .. d - 1 do
                        idx.[pos] <- v
                        go (pos + 1) v
            go 0 0
            result.ToArray()

    /// Packed offset -> canonical labels, for walking a tensor in storage order.
    let labelTable (d: int) (r: int) : int[][] =
        let arr = Array.zeroCreate (storageSize d r)
        for labels in enumerate d r do
            arr.[rankOf labels] <- labels
        arr

    /// Symmetric access: any index order maps to the canonical entry.
    let get (t: T) (idx: int[]) : float =
        t.Data.[rankOf (Array.sort idx)]

    let set (t: T) (idx: int[]) (v: float) =
        t.Data.[rankOf (Array.sort idx)] <- v

    /// r! / prod(multiplicity of each distinct label)! — the number of
    /// distinct position orderings a canonical entry stands for. This is the
    /// joint-r! weight from Blade's product-symmetry accounting.
    let multiplicity (idx: int[]) : float =
        let mutable res = Combinatorics.factorial idx.Length
        let mutable i = 0
        while i < idx.Length do
            let mutable j = i
            while j < idx.Length && idx.[j] = idx.[i] do j <- j + 1
            res <- res / Combinatorics.factorial (j - i)
            i <- j
        res

    let map2 (f: float -> float -> float) (a: T) (b: T) : T =
        if a.Dim <> b.Dim || a.Rank <> b.Rank then failwith "SymTensor.map2: shape mismatch"
        { a with Data = Array.map2 f a.Data b.Data }

    let scale (c: float) (t: T) : T =
        { t with Data = Array.map (fun v -> c * v) t.Data }

    let copy (t: T) : T = { t with Data = Array.copy t.Data }
