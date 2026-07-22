# Triangular-Decomposed Zarr Stores — the `blade` layout attribute (spec_version 1)

Blade's Zarr provider reads and writes **packed symmetric/antisymmetric tensors**
stored as ordinary Zarr arrays. Any Zarr tool sees a plain dense array; Blade
interprets a namespaced attribute to recover the packed index structure. This is
the same interop posture as xarray's `_ARRAY_DIMENSIONS`.

## Physical layout

A triangular-decomposed variable is a **physically ordinary, uncompressed Zarr
array** (v2 or v3) whose **leading dimension is a packed simplex pool**:

- For a symmetric group `SymIdx<r, n>`: pool length = C(n+r-1, r) (multisets).
- For an antisymmetric group `AntisymIdx<r, n>`: pool length = C(n, r) (strict subsets).
- Pool cells are ordered **ascending-lex** over canonical coordinates
  (i₀ ≤ i₁ ≤ … for sym, i₀ < i₁ < … for antisym). This is exactly
  `linearized_storage::{symmetric|antisymmetric}::linearize`'s order, which is
  differentially pinned equal to the allocator's DFS pool order — so a Blade
  runtime read is a straight pool copy, and `unlinearize` recovers coordinates
  for any external consumer.
- Trailing dimensions (if any) are ordinary dense axes: physical shape is
  `[cardinality, d₁, d₂, …]`, row-major.

## Metadata

The attribute lives in `.zattrs` (v2) or `attributes` (v3), key `blade`:

```json
"blade": {
  "spec_version": 1,
  "layout": "packed",
  "order": "ascending-lex",
  "index_types": [
    { "kind": "sym",   "rank": 2, "extent": 100 },
    { "kind": "dense", "extent": 12 }
  ],
  "decomposition": { "scheme": "flat-ranges" }
}
```

- `spec_version` (required): this document describes version 1.
- `layout` (required): `"packed"`.
- `order` (optional, default `"ascending-lex"`): only `"ascending-lex"` is valid
  in version 1. Present so future layouts can name alternatives explicitly.
- `index_types` (required): one entry per **logical** index group.
  - Version-1 rules: **exactly one packed group** (`"sym"` or `"antisym"`,
    `rank >= 2`, positive `extent`), and it must be the **first** entry.
    All remaining entries are `{"kind": "dense", "extent": d}` and must match
    the physical trailing shape exactly. `"herm"` is reserved (hermitian cells
    are constraint-coupled, not independently stored) and rejected.
  - The physical pool dimension (shape[0]) **must equal** the group's
    cardinality; a mismatch is a loud load error, never a reinterpretation.
- `decomposition` (optional, informational for `layout: "packed"`):
  `"flat-ranges"` records that chunk boundaries are contiguous flat-cell
  ranges (see below). Version-1 readers only need `layout` + `index_types`
  to read a "packed" store, so unknown extra fields inside `decomposition`
  must be ignored.

### `layout: "packed-blocks"` (simplex-blocks)

A second physical layout stores the pool as PADDED BLOCK ROWS — physical
shape `[blockCount, tile^rank, …trailing]`, one block = one chunk — where
blocks are tile multisets of the simplex (the block grid of a rank-r simplex
with T tiles is itself SymIdx<r, T>). `decomposition` is then REQUIRED:
`{"scheme": "simplex-blocks", "tile": B, "grid": T, "block_order":
"ascending-lex" | "path"}`. Within a block, cells are in absolute
ascending-lex order; rows are padded with fill_value up to `tile^rank`.
Antisymmetric note: a block whose repeated tile is narrower than its
multiplicity is EMPTY (all padding) — e.g. every repeated-tile block when
`tile` = 1 — because the diagonal is excluded. Full details, math, and the
phased plan: providers/ZarrSimplexBlocksPlan.md.

## Chunking = decomposition

Ordinary Zarr chunking of the pool dimension IS the triangular decomposition:
`chunks = [poolChunk, d₁, d₂, …]` makes every chunk a contiguous flat-cell
range `[lo, hi)` × whole trailing block — exactly the ranges Blade's MPI
backend distributes (`genMpiNestSimplicial`), so "one decomposition block =
one chunk" falls out of the format. Readers accept ANY regular chunking
(assembly is chunk-agnostic); writers SHOULD chunk only the pool dimension.

Blade's own writer (v1) writes a single whole-array chunk; external writers
(e.g. Python) may chunk the pool dimension freely.

## Reading and writing

- Blade types such a variable as `Array<T like SymIdx<r, n>, Idx<d₁>, …>`; the
  packed group engages the compiler's compact storage codegen unchanged.
- Missing chunk files read as `fill_value` (Zarr semantics); a missing chunk
  with a null `fill_value` is a loud error.
- Compile-time folding (`let static … |> z.read`) of packed variables is
  refused in version 1 with steering (StaticValue has no packed carrier);
  bind with a plain `let` for the runtime schedule.
- `z.read_window(var, lo, hi)` (literal bounds) materializes the translated
  sub-simplex `Array<T like SymIdx<r, hi-lo>>`; packed-blocks stores read
  only the chunks whose tile intervals intersect the window.
- Under Blade's MPI backend, packed-blocks reads are rank-scoped (each rank
  reads its owned blocks, an Allgatherv restores the pool) and provider
  writes run on rank 0 only.
- Uncompressed only in version 1 (the provider-wide codec constraint).

## Writing a conforming store from Python

```python
import json, numpy as np, math, itertools, pathlib

n, r = 4, 2
cells = list(itertools.combinations_with_replacement(range(n), r))  # ascending-lex
pool = np.array([f(i, j) for (i, j) in cells], dtype="<f8")

root = pathlib.Path("C.zarr"); d = root / "C"; d.mkdir(parents=True)
(root / ".zgroup").write_text('{"zarr_format": 2}')
(d / ".zarray").write_text(json.dumps({
    "zarr_format": 2, "shape": [len(cells)], "chunks": [len(cells)],
    "dtype": "<f8", "compressor": None, "fill_value": 0.0,
    "order": "C", "filters": None}))
(d / ".zattrs").write_text(json.dumps({"blade": {
    "spec_version": 1, "layout": "packed", "order": "ascending-lex",
    "index_types": [{"kind": "sym", "rank": r, "extent": n}],
    "decomposition": {"scheme": "flat-ranges"}}}))
(d / "0").write_bytes(pool.tobytes())
```

`itertools.combinations_with_replacement` enumerates ascending-lex order
directly (use `itertools.combinations` for antisym).
