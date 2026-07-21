# Plan: Recursive Simplex Storage in Zarr ("simplex-blocks", decomposition spec_version 2)

Status: **Phases 0–3 IMPLEMENTED** (2026-07-15; `blade test zarr` 183/0).
Phases 0–2: block math + identities, F# store I/O with the flat-vs-blocks
differential gate, runtime C++ reassembly (restructured to PER-BLOCK chunk
I/O — one chunk buffer, no full physical staging) incl. antisym
empty-diagonal blocks and path order. Phase 3 (decomposition-aligned
access):

- **MPI-distributed reads**: under the mpi emit gate, a packed
  simplex-blocks read is rank-scoped — each rank opens only the chunks
  whose exact pool range (greedy first/last cell + linearize, within-block
  enumeration is pool-monotone) intersects its balanced flat-cell range,
  then MPI_Allgatherv restores the full pool buffer on every rank, so
  downstream codegen is untouched. Flat "packed" stores still read fully
  on every rank (their chunks are pool ranges; distributing them is a
  cheap follow-up if ever needed). Provider writes under MPI are rank-0
  guarded (SPMD ranks racing on store files would tear them).
  Differential gate: serial build vs `mpiexec -n 1/3` — identical stdout,
  identical written pool.
- **Window reads**: `z.read_window(s.vars.C, lo, hi)` materializes the
  translated sub-simplex `Array<T like SymIdx<r, hi-lo>>` (literal bounds,
  packed variables only, loud rejects otherwise). Blocks stores skip
  chunks whose tile intervals miss the window entirely (the O((w/B)^r)
  payoff); flat stores assemble fully then extract. Core seams: a
  `Window` field on ProviderReadSpec, a read_window typing arm, a
  lowering matcher, and `PackedReadOpts {Distribute; Window}` in the
  registry contract.

Phase 4 (adaptive-depth recursion, streaming construction, a tile-config
surface for runtime blocks WRITES) remains future work.

Measured padding (open question 1 resolved): overhead is dominated by
RAGGEDNESS of the last tile, not the diagonal — sym n=100 r=2: B=16 (T=7,
last tile width 4) → **41.9%**, B=20 (T=5, divides n) → 18.8%, B=10 (T=10)
→ 8.9%. Guidance: prefer tile edges dividing the extent, and T ≳ 10;
writers should warn (or refuse) past a configurable overhead threshold. Builds on the landed triangular store spec
(`providers/ZarrTriangularSpec.md`, `blade` attribute spec_version 1, whose
`decomposition.scheme = "flat-ranges"` was designed as the forward-compat
carrier for exactly this extension). Sources: `rewrite/docs/future.md` §2.3
(product-of-simplices decomposition, L^aH^b halving, mixed-radix block paths,
BSP) and the verified identities from the MPI/domain-decomposition arc.

## 1. Why flat ranges are not the end state

The landed v1 chunks the canonical pool into contiguous flat-cell ranges.
That is optimal for whole-array streaming (and exactly matches the MPI
backend's balanced cell-range split), but it has no *spatial* structure:

- A sub-simplex query ("all cells with every coordinate in [lo, hi)") touches
  O(all) chunks — flat ranges interleave spatially distant cells.
- Out-of-core block algorithms (tiled contractions, BSP with halo-free
  ownership) want chunk boundaries aligned with **block** boundaries of the
  simplex, so one owner reads one chunk per owned block.
- Recursive (depth-adaptive) refinement wants a subtree of blocks to be a
  contiguous key range, so a coarse region is one directory listing / one
  range scan.

## 2. The block structure (verified identities)

Fix tile edge B over the index interval [0, n), giving T = ceil(n/B) tiles.

- **Block grid = SymIdx<r, T>.** The set of tile-multisets (t₁ ≤ … ≤ t_r)
  enumerates the blocks of a rank-r simplex: C(T+r-1, r) blocks (this holds
  for antisym too — a block with a repeated tile still contains strict cells
  *within* the tile). This is the load-bearing identity: block enumeration,
  ranking, and unranking REUSE the existing combinadics
  (`linearized_storage`, `gridCoords`-style walks) at the tile level.
- **Each block is a product of smaller simplices.** Group the block's tiles
  by multiplicity: a tile appearing m times with width w contributes a factor
  of C(w+m-1, m) cells (sym) or C(w, m) (antisym); distinct tiles multiply.
  Off-diagonal blocks (all tiles distinct) are dense boxes of B^r cells;
  the all-in-one-tile block is a B-wide simplex. Closed forms — no sidecar
  index is *required* (see 4).
- **Recursive halving is the T = 2^k special case.** future.md §2.3's
  "each simplex halves into r+1 valid L^aH^b cells, recursively" produces,
  at depth k, exactly the tile grid with T = 2^k: leaf-block count
  C(2^k + r - 1, r) (NOT (r+1)^k — that's per-split branching). The recursion
  adds a **tree** over the same leaf blocks; its DFS order is the
  "mixed-radix path" order (radix r+1 at simplex nodes, componentwise at
  product nodes). Subtrees = contiguous path-order ranges.
- **Intra-block iteration is branch-free**: i_m ranges over
  max(tile_m·B, i_{m-1} + strict) .. min((tile_m+1)·B, n) — uniform bounds,
  no per-block-shape codegen.

So "recursive simplex storage" = (a) the tile-block layout, plus (b) an
optional path-order permutation of blocks giving subtree locality. (a) is
useful on its own and is where implementation starts; (b) is a pure
re-ordering knob on top.

## 3. On-disk encoding

Zarr requires a REGULAR chunk grid, and block cell-counts vary — the two
zarr-conformant encodings:

**Option A — padded block rows (RECOMMENDED v2 default).**
Physical array shape `[numBlocks, maxBlockCells] (+ trailing dense dims)`,
chunks `[1, maxBlockCells] (+ trailing)` → one block = one chunk, addressable
by block rank. Cells within a block in canonical ascending-lex order of the
block's product-of-simplices space; the tail of each row padded with
fill_value. Space overhead is asymptotically negligible: Σ blockCells =
C(n+r-1, r) ≈ n^r/r! while numBlocks·maxBlockCells = C(T+r-1, r)·B^r has the
same leading term — padding is a lower-order diagonal correction (measure in
Phase 0; expect single-digit % for T ≳ 8).

**Option B — sharded pool (deferred).** Keep the 1-D pool but permuted to
block order, with zarr v3 `sharding_indexed` making each block an inner chunk.
Zero padding, but requires implementing the sharding codec (currently
rejected by the provider) — revisit when codec support lands.

**Metadata** (the `decomposition` object grows; `layout`/`order`/`index_types`
unchanged, so spec_version stays 1 with a versioned decomposition object):

```json
"blade": {
  "spec_version": 1,
  "layout": "packed-blocks",
  "order": "ascending-lex",
  "index_types": [ { "kind": "sym", "rank": 3, "extent": 4096 }, ... ],
  "decomposition": {
    "scheme": "simplex-blocks",
    "tile": 512,
    "grid": 8,
    "block_order": "ascending-lex",
    "depth": 3
  }
}
```

- `layout: "packed-blocks"` (new) — physical shape is `[numBlocks,
  maxBlockCells, …]`, distinguishing it loudly from v1 `"packed"` (pool
  shape); v1 readers reject it with a clear message instead of misreading.
- `tile`, `grid` — B and T; `grid` must equal ceil(extent / tile) and
  shape[0] must equal C(grid+r-1, r) (sym) — both validated loudly.
- `block_order` — `"ascending-lex"` (tile-multiset combinadic rank; default)
  or `"path"` (mixed-radix DFS; requires `grid` a power of two and `depth` =
  log2(grid)). Readers support both; the permutation is a pure bijection
  blockRank ↔ tileCoords.
- Trailing dense dims append after `maxBlockCells` (shape
  `[numBlocks, maxBlockCells, d₁, …]`), keeping "one block × full trailing
  slab = one chunk".

## 4. Implementation phases

**Phase 0 — block math + measurements (F#, hermetic).**
`providers/` module (`ZarrSimplexBlocks.fs` or a ZarrProvider section):
`blockCount T r`, `blockCells (tiles, widths)` closed forms, `cellToBlock`
(coords div B + within-block offset via per-tile grouped linearization),
`blockUnrank` (combinadic unrank at tile level), the path-order bijection,
and a padding-overhead report for representative (n, r, B). Property tests:
Σ blockCells == C(n+r-1, r); cellToBlock ∘ blockToCell == id; path order is
a permutation. All pure — no compiler surface.

**Phase 1 — store read/write (F# side).**
`ZarrWrite` gains packed-blocks output (pool → block rows, padded);
`parseBladeLayout` gains the `packed-blocks` arm + validations; `readVarData`
/ fold adapter reassembles the canonical pool from block rows (inverse
permutation), so a packed-blocks store is READ-equivalent to a v1 packed
store everywhere above the metadata layer. Differential gate: same tensor
written flat-ranges and simplex-blocks reads back identical pools (both
versions, sym + antisym + mixed trailing).

**Phase 2 — runtime C++ read/write.**
Extend `CppZarr.genReadPacked` to dispatch on scheme: block-loop assembly
(per block: read chunk row, scatter its cells into the flat pool via the
Phase-0 offset math — emitted as compile-time-baked loops, mirroring the v1
chunk-intersection pattern). The codegen intercept (`genPackedPoolCopy`) is
UNCHANGED — it consumes the same `<v>_flat` canonical pool. Write side mirrors
(pool → block rows). e2e: read→write roundtrip vs independent oracle, both
block orders.

**Phase 3 — decomposition-aligned access (the payoff).**
- MPI: block ownership = BSP; each rank reads ONLY its owned chunks
  (needs a rank-scoped provider-read variant under the mpi emit gate —
  today's reads materialize the full array on every rank before Allgatherv;
  this phase makes the READ itself distributed).
- Out-of-core: sub-simplex window reads (all coords in [lo, hi)) touch only
  the O((w/B)^r) intersecting blocks.
- Ties into the tile()/PlaceBlocked compiler phases of the domain-
  decomposition plan when those land — the store layout is then the
  in-memory layout, and chunk I/O is a straight block copy.

**Phase 4 — recursion proper.**
Path block-order as default for power-of-two grids; adaptive depth (leaf
blocks of heterogeneous depth = a pruned tree, keys = variable-length paths —
needs a block index sidecar array, the first thing in this design that does);
streaming out-of-core CONSTRUCTION (write blocks as their cell ranges
complete — the staging-contract streaming story from ppl/NOTES.md).

## 5. Open questions (decide at Phase 0 with measurements)

1. Padding overhead in practice for small T (T ≤ 4 wastes the most —
   maybe reject tile configs with overhead > threshold, steering to
   flat-ranges).
2. `maxBlockCells` = B^r assumes uniform tiles; a ragged last tile shrinks
   some blocks — keep B^r (simpler, slightly more padding) or
   max-over-actual-blocks (tighter, still closed-form)?
3. Whether `block_order: "path"` earns its complexity before Phase 4
   (subtree contiguity only pays with adaptive depth or range scans) —
   default to ascending-lex until then.
4. Antisym: same block grid, strict per-tile factors; the dead-diagonal
   host-pool gotcha (see blade-zarr-provider memory / genPackedPoolCopy)
   lives entirely on the materialization side and is untouched by this
   format — but Phase-2 scatter tests must cover antisym explicitly.
5. Hermitian: still reserved (constraint-coupled cells); revisit only with
   a canonical-half storage story.

## 6. Effort sketch

Phase 0 ≈ small (pure math + tests). Phase 1 ≈ moderate (parser arm +
writer + assembly, all in providers/). Phase 2 ≈ moderate (one new emitter
arm; core intercept untouched). Phase 3 is the first phase needing core
compiler work (MPI-scoped reads) — separate approval gate. Phase 4 is
open-ended; gate on a real out-of-core workload.
