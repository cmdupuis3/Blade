namespace BladeML

/// The block-structured index type IrrepsIdx<spec> (ml-spec section 3.1),
/// realized as its index-type contract per formalism.md section 3.2:
///   - Domain:      valid (block, mult, m) triples for the spec
///   - Cardinality: total_dim(spec)
///   - Bijection:   triples <-> offsets [0, cardinality)
///   - Enumeration: lexicographic on (block, mult, m) == offset order
///
/// This is the DepIdx specialization: outer index = block, inner index type
/// depends on the block (Idx<mult(b)> x Idx<dim(irrep(b))>). Storage is the
/// left-justified concatenation of blocks, multiplicity-major within a block.
/// The m component is stored 0-based; the signed m value is (m - L).
module IrrepsIdx =

    /// Start offset of each block; length = spec.Length + 1, last = extent.
    let blockStarts (spec: SpecEntry[]) : int[] =
        let starts = Array.zeroCreate (spec.Length + 1)
        for b in 0 .. spec.Length - 1 do
            starts.[b + 1] <- starts.[b] + Irreps.blockDim spec.[b]
        starts

    /// Cardinality of the index type.
    let extent (spec: SpecEntry[]) : int = Irreps.totalDim spec

    /// Storage bijection, forward: (block, mult, m) -> offset.
    let offsetOf (spec: SpecEntry[]) (b: int, mu: int, m: int) : int =
        if b < 0 || b >= spec.Length then
            invalidArg "b" (sprintf "block %d out of range [0, %d)" b spec.Length)
        let e = spec.[b]
        let d = Irreps.dim e.Ir
        if mu < 0 || mu >= e.Mult then
            invalidArg "mu" (sprintf "multiplicity %d out of range [0, %d)" mu e.Mult)
        if m < 0 || m >= d then
            invalidArg "m" (sprintf "m-component %d out of range [0, %d)" m d)
        (blockStarts spec).[b] + mu * d + m

    /// Storage bijection, backward: offset -> (block, mult, m).
    let ofOffset (spec: SpecEntry[]) (off: int) : int * int * int =
        if off < 0 || off >= extent spec then
            invalidArg "off" (sprintf "offset %d out of range [0, %d)" off (extent spec))
        let starts = blockStarts spec
        let mutable b = 0
        while off >= starts.[b + 1] do
            b <- b + 1
        let rel = off - starts.[b]
        let d = Irreps.dim spec.[b].Ir
        (b, rel / d, rel % d)

    /// Enumeration in offset (= lexicographic) order.
    let enumerate (spec: SpecEntry[]) : (int * int * int)[] =
        [| for b in 0 .. spec.Length - 1 do
             let e = spec.[b]
             let d = Irreps.dim e.Ir
             for mu in 0 .. e.Mult - 1 do
               for m in 0 .. d - 1 do
                 yield (b, mu, m) |]
