// The simplex-blocks decomposition as COMPUTE math.
//
// docs/plans/plan-simplex-blocked-compute.md is the design; this file is its
// section 8.1 deliverable ("lift SimplexBlocks to a shared module ... extend
// with the per-brick facts compute needs"). The observation it encodes, at
// rank 2: split the triangular domain {i <= j < n} into B-wide tiles. A block
// whose two tiles DIFFER is a full dense rectangle -- for i in tile t1 and j
// in tile t2 > t1, `i < j` holds for every pair, so the canonicalization
// constraint is discharged by the block structure and never costs an
// instruction. Only the T on-diagonal blocks stay triangular, and they hold
// 1/T of the cells.
//
// WHY THIS IS A SEPARATE COPY FROM `Blade.ZarrProvider.SimplexBlocks`.
// The provider's copy is deliberately NOT lifted away: its consumer is the
// zarr/MPI decomposition, which needs the exactly-triangular quadtree
// (`pathMultisets`) because rank load balancing wants EQUAL-SIZE ownership
// units. The compute side coarsens each off-diagonal triangle PAIR into one
// dense brick precisely because backends want boxes -- which produces unequal
// unit shapes (bricks + residue), right for a static brick schedule and wrong
// as per-rank ownership. Section 6's MPI row says so in as many words: "the
// two schemes share the leaf structure and the SimplexBlocks identities
// (agreement property-pins), nothing more". The shared part is exactly the
// identities below, and `blade test llvm goldens` pins them EQUAL to the
// provider's over a grid of (n, B, r, symmetry) -- the differential-twin
// discipline, applied to two modules instead of two lanes.
//
// Everything here is pure integer arithmetic over compile-time constants: no
// IR, no emission, no environment. That is what makes it testable without a
// toolchain and reusable by a second back end.
module Blade.SimplexBlocksCore

// ---------------------------------------------------------------------------
// The landed math (identities shared with the provider)
// ---------------------------------------------------------------------------

/// C(m, k) -- cardinality arithmetic. Byte-for-byte the provider's `binom`
/// (ZarrProvider.fs), including its zero answer for k < 0 or m < k, because
/// the empty-diagonal rule below depends on that zero.
let binom (m: int64) (k: int) : int64 =
    if k < 0 || m < int64 k then 0L
    else
        let mutable num = 1L
        let mutable den = 1L
        for i in 0 .. k - 1 do
            num <- num * (m - int64 i)
            den <- den * int64 (i + 1)
        num / den

/// Combinadic rank of a canonical coordinate tuple in ascending-lex order
/// (sorted; strictly increasing when `strict`). Mirrors
/// `linearized_storage::{symmetric|antisymmetric}::linearize`, which is what
/// makes a rank computed here name the same pool cell the C++ lane's pointer
/// skeleton reaches.
let rankOfCoords (strict: bool) (n: int64) (coords: int64[]) : int64 =
    let r = coords.Length
    let completions (v: int64) (m: int) : int64 =
        if strict then binom (n - v - 1L) m
        else binom ((n - v) + int64 m - 1L) m
    let mutable rank = 0L
    let mutable lo = 0L
    for k in 0 .. r - 1 do
        let mutable v = lo
        while v < coords.[k] do
            rank <- rank + completions v (r - k - 1)
            v <- v + 1L
        lo <- coords.[k] + (if strict then 1L else 0L)
    rank

/// Inverse of `rankOfCoords`.
let unrankToCoords (strict: bool) (n: int64) (r: int) (rank: int64) : int64[] =
    let completions (v: int64) (m: int) : int64 =
        if strict then binom (n - v - 1L) m
        else binom ((n - v) + int64 m - 1L) m
    let coords = Array.zeroCreate r
    let mutable rest = rank
    let mutable lo = 0L
    for k in 0 .. r - 1 do
        let mutable v = lo
        let mutable c = completions v (r - k - 1)
        while rest >= c do
            rest <- rest - c
            v <- v + 1L
            c <- completions v (r - k - 1)
        coords.[k] <- v
        lo <- v + (if strict then 1L else 0L)
    coords

/// Number of blocks with T tiles at rank r. The block grid IS SymIdx<r, T>:
/// blocks are tile MULTISETS in both the symmetric and the antisymmetric case
/// (a repeated tile holds the strict cells inside itself), so this is
/// C(T + r - 1, r) either way.
let blockCount (r: int) (T: int64) : int64 = binom (T + int64 r - 1L) r

/// Number of tiles covering [0, n) with edge B.
let tileCount (n: int64) (B: int64) : int64 = if B <= 0L then 0L else (n + B - 1L) / B

/// Width of tile t over [0, n) with edge B. The last tile may be ragged.
let tileWidth (n: int64) (B: int64) (t: int64) : int64 = min B (n - t * B)

/// Cells in one block, given its tile multiset (ascending): group tiles by
/// value; a tile of width w with multiplicity m contributes C(w+m-1, m)
/// symmetric, C(w, m) antisymmetric. The antisymmetric factor is ZERO once
/// m > w -- the EMPTY DIAGONAL BLOCKS, which is why an antisym decomposition
/// at B = 1 has no repeated-tile blocks at all and covers 100% of its cells
/// with dense bricks.
let blockCellCount (strict: bool) (n: int64) (B: int64) (tiles: int64[]) : int64 =
    tiles
    |> Array.countBy id
    |> Array.fold (fun acc (t, m) ->
        let w = tileWidth n B t
        let f = if strict then binom w m else binom (w + int64 m - 1L) m
        acc * f) 1L

/// A block's cells in absolute ascending-lex order: the branch-free bounds
/// i_k in [max(tile_k*B, i_{k-1} + strict), min((tile_k+1)*B, n)). This IS
/// the iteration the emitter lays down -- uniform bounds, no per-shape
/// codegen, and (the point of the whole design) no `if` testing canonicality.
let enumBlockCells (strict: bool) (n: int64) (B: int64) (tiles: int64[]) : seq<int64[]> =
    let r = tiles.Length
    let rec go (k: int) (prev: int64) (acc: int64 list) : seq<int64[]> =
        seq {
            if k = r then
                yield (List.rev acc |> Array.ofList)
            else
                let tileLo = tiles.[k] * B
                let lo =
                    if k = 0 then tileLo
                    else max tileLo (prev + (if strict then 1L else 0L))
                let hi = min ((tiles.[k] + 1L) * B) n
                for i in lo .. hi - 1L do
                    yield! go (k + 1) i (i :: acc)
        }
    go 0 -1L []

/// Every tile multiset of size r over T tiles, in ascending-lex order -- the
/// CANONICAL BLOCK SEQUENCE. Fold partials combine in this order and no
/// other: it is what makes a bricked fold deterministic-but-reassociated
/// rather than merely reassociated (plan section 7).
let blockSequence (r: int) (T: int64) : seq<int64[]> =
    let rec go (k: int) (lo: int64) (acc: int64 list) : seq<int64[]> =
        seq {
            if k = r then yield (List.rev acc |> Array.ofList)
            else
                for t in lo .. T - 1L do
                    yield! go (k + 1) t (t :: acc)
        }
    if T <= 0L then Seq.empty else go 0 0L []

// ---------------------------------------------------------------------------
// Compute-side additions
// ---------------------------------------------------------------------------

/// Is this block a fully DENSE BRICK -- every tile distinct? Those are the
/// blocks with no symmetry constraint left inside them: a product of disjoint
/// ordered tile ranges is entirely canonical. C(T, r) of the C(T+r-1, r)
/// blocks qualify, which needs T >= r to be non-empty at all.
let isDenseBrick (tiles: int64[]) : bool =
    Array.length (Array.distinct tiles) = Array.length tiles

/// The fraction of a rank-r simplex covered by dense bricks at T tiles:
/// r! * C(T, r) / T^r. The table in plan section 3 (75% at r=2/T=4, 93.8% at
/// r=2/T=16) is this function; it is the profitability question in one number.
let denseBrickFraction (r: int) (T: int64) : float =
    if T <= 0L then 0.0
    else
        let mutable fact = 1.0
        for k in 1 .. r do fact <- fact * float k
        let mutable denom = 1.0
        for _ in 1 .. r do denom <- denom * float T
        fact * float (binom T r) / denom

/// The first pool cell of storage row i, for a rank-2 group of extent n:
/// sum over k < i of the row lengths (n - k - strict). Closed form so the
/// emitter can compute a row base with three instructions instead of a
/// running counter, which is what makes a brick's rows independent (and
/// therefore parallelizable and vectorizable).
///
/// i * (2n - i + 1 - 2*strict) is always even -- one of the two factors is --
/// so the halving is exact in integer arithmetic.
let rowBase2 (strict: bool) (n: int64) (i: int64) : int64 =
    let s = if strict then 1L else 0L
    i * (2L * n - i + 1L - 2L * s) / 2L

/// Pool offset of a canonical rank-2 cell (i <= j, or i < j when strict).
/// Equals `rankOfCoords strict n [| i; j |]`; the property pins assert that,
/// and the emitter uses THIS form because it is affine in j.
let packedOffset2 (strict: bool) (n: int64) (i: int64) (j: int64) : int64 =
    rowBase2 strict n i + (j - i - (if strict then 1L else 0L))

/// Cells in a whole rank-2 pool: C(n+1, 2) symmetric, C(n, 2) antisymmetric.
let poolCells2 (strict: bool) (n: int64) : int64 =
    if strict then binom n 2 else binom (n + 1L) 2

/// THE RANK-r GENERALIZATION OF `rowBase2`, and the reason an arbitrary-rank
/// simplex can be emitted without per-cell combinadic arithmetic.
///
/// `rankOfCoords` accumulates `completions v m` for v in [lo, i), where
/// m = r-k-1 is the number of coordinates still to place and
///     completions v m = C(n-v-1, m)      (strict)
///                     = C(n-v+m-1, m)    (symmetric).
/// Both sums telescope under the hockey-stick identity
/// (sum_{u <= U} C(u, m) = C(U+1, m+1)) into a DIFFERENCE OF TWO BINOMIALS:
///     strict:     C(n-lo,   m+1) - C(n-i,   m+1)
///     symmetric:  C(n-lo+m, m+1) - C(n-i+m, m+1)
/// i.e. one polynomial of degree m+1 in each endpoint -- no loop, no
/// factorial, and (the point of writing it this way) a form the emitter lays
/// down as a handful of integer instructions HOISTED OUT of the inner loops:
/// level k's term is invariant under everything inside level k.
///
/// Two degenerate cases carry the whole design. At r = 2, k = 0, lo = 0 this
/// is exactly `rowBase2` -- C(n,2) - C(n-i,2) = i*(2n-i-1)/2 when strict. At
/// the LAST level (k = r-1, so m = 0) it degenerates to `i - lo`: the
/// innermost run is AFFINE, which is what makes consecutive final coordinates
/// consecutive pool cells at every rank, not just at rank 2.
let prefixTerm (strict: bool) (n: int64) (r: int) (k: int) (lo: int64) (i: int64) : int64 =
    let m = r - k - 1
    let d = if strict then 0L else int64 m
    binom (n - lo + d) (m + 1) - binom (n - i + d) (m + 1)

/// Pool offset of a canonical rank-r cell (ascending; strictly ascending when
/// `strict`). Equals `rankOfCoords strict n coords` -- the property pins
/// assert exactly that over a grid -- but is built from `prefixTerm`, so it
/// mirrors what the emitter computes level by level.
let packedOffsetR (strict: bool) (n: int64) (coords: int64[]) : int64 =
    let r = coords.Length
    let s = if strict then 1L else 0L
    let mutable acc = 0L
    let mutable lo = 0L
    for k in 0 .. r - 1 do
        acc <- acc + prefixTerm strict n r k lo coords.[k]
        lo <- coords.[k] + s
    acc

/// Cells in a whole rank-r pool: C(n+r-1, r) symmetric, C(n, r) antisymmetric.
let poolCellsR (strict: bool) (n: int64) (r: int) : int64 =
    if strict then binom n r else binom (n + int64 r - 1L) r

/// One rank-2 block, in the shape an emitter lays down. Rows and columns are
/// ABSOLUTE coordinates; a dense brick needs no canonicality test anywhere
/// inside it, and the on-diagonal residue keeps its serial triangle.
type Brick2 =
    { /// The tile multiset (t1 <= t2) this block ranks under.
      Tiles: int64 * int64
      RowLo: int64
      RowHi: int64
      ColLo: int64
      ColHi: int64
      /// t1 = t2: the triangular residue. Its cells are NOT a rectangle and
      /// its bound depends on the row index, so it runs serial (plan section
      /// 8.2: "leaf simplex blocks run serial triangular -- they are small by
      /// construction, and dense-with-mask wastes half of a tiny block").
      IsDiagonal: bool }

/// Every rank-2 block of the {i <= j < n} (or {i < j < n}) domain at tile
/// edge B, in ascending-lex tile order. Empty blocks are dropped: an
/// antisymmetric diagonal block of a width-1 tile holds no strict cell, and
/// emitting a loop for it would be dead code.
let bricks2 (strict: bool) (n: int64) (B: int64) : Brick2 list =
    let T = tileCount n B
    [ for t1 in 0L .. T - 1L do
        for t2 in t1 .. T - 1L do
            let rowLo = t1 * B
            let rowHi = min ((t1 + 1L) * B) n
            let colLo = t2 * B
            let colHi = min ((t2 + 1L) * B) n
            let diag = (t1 = t2)
            let cells = blockCellCount strict n B [| t1; t2 |]
            if cells > 0L then
                yield { Tiles = (t1, t2)
                        RowLo = rowLo; RowHi = rowHi
                        ColLo = colLo; ColHi = colHi
                        IsDiagonal = diag } ]

/// The largest-preference divisor policy: the tile edge in [lo, hi] that
/// DIVIDES n exactly (so no brick row is ragged), preferring the divisor
/// closest to `target` (ties to the larger), requiring T >= 2. Trial division
/// over a ~100-wide window is negligible at compile time. Rationale: the
/// 2026-08-18 serial-vs-bricked verdict was measured at PRIME extents (2003,
/// 6007), where every brick row ends ragged; real extents are usually
/// composite (any factor of 2 in n admits exact halving -- the number of 2s
/// in n's factorization bounds how deep an exact quadtree recursion can go),
/// and the Zarr simplex-blocks measurements showed raggedness dominates
/// block overhead. This function is the candidate replacement policy; whether
/// it becomes the default is decided by measurement (llvm-bench, shape 3c).
let divisorTileEdgeIn (lo: int64) (hi: int64) (target: int64) (n: int64) : int64 option =
    let mutable best : int64 option = None
    for b in lo .. hi do
        if b >= 2L && b < n && n % b = 0L then
            best <-
                match best with
                | None -> Some b
                | Some cur ->
                    let d x = abs (x - target)
                    if d b < d cur || (d b = d cur && b > cur) then Some b else Some cur
    best

/// The derived tile edge for a rank-2 domain of extent n, or None for "do not
/// block" (plan section 8.3: B/depth policy is derived, not user-tuned).
///
/// Measured 2026-08-18 (`blade test llvm-bench`: non-power-of-two extents,
/// rotated arms, medians): S0 brick iteration over the existing packed pool
/// ran 1.07x SLOWER than the serial triangle at n = 6007 and indistinguishable
/// at n = 2003 -- no benchmarked extent showed a win. The derived policy is
/// therefore "do not block": the default emission must be the fastest measured
/// path. The blocked nest stays reachable through an explicit
/// `BLADE_LLVM_BRICKS=<B>` (read in the emitter, not here) so the next variant
/// -- S1 brick-major layout, or a mirror-expansion contraction -- can be
/// A/B'd against a control the three-way blocks gate already proves correct.
/// A nonzero policy returns only with a measurement that beats serial at
/// matched extents.
let autoTileEdge (_n: int64) : int64 option = None

/// The derived tile edge for a rank-2 simplex whose PRODUCER re-reads O(row)
/// operand data per cell (row-operand kernels). Measured 2026-08-18 (plan
/// section 0, third block): bricks win 1.05-2.76x once the row-operand
/// working set is ~9 MB or more, and raggedness is noise at that scale --
/// prefer an exact divisor near 64, fall back to 64 ragged. The caller
/// (EmitLlvm.brickTileEdge) decides WHEN this policy applies (the reuse
/// hint against its threshold); this function only says what edge to use.
let reuseTileEdge (n: int64) : int64 option =
    if n <= 128L then None
    else
        match divisorTileEdgeIn 32L 128L 64L n with
        | Some b -> Some b
        | None -> Some 64L
