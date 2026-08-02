# Triangular-Decomposed Zarr Stores — the `blade` layout attribute

Blade's Zarr provider reads and writes **packed symmetric/antisymmetric tensors**
stored as ordinary Zarr arrays. Any Zarr tool sees a plain dense array; Blade
interprets a namespaced attribute to recover the packed index structure. This is
the same interop posture as xarray's `_ARRAY_DIMENSIONS`.

Two spec versions exist. **Version 1** (below) covers the depth-1 simplex
classes `SymIdx<r,n>` / `AntisymIdx<r,n>`. **Version 2**
([its own section](#spec_version-2--the-orbit-iterated-wreath-head)) adds the
`"orbit"` head for iterated-wreath classes (`OrbIdx` of depth >= 2) and changes
nothing else. Version 2 is a strict superset: it still admits `"sym"` and
`"antisym"` heads, and Blade's own writer stamps `spec_version: 1` whenever the
head it emits is one of those, so a depth-1 store written today is byte-identical
to one written before version 2 existed.

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

---

# spec_version 2 — the `orbit` (iterated-wreath) head

Version 2 adds ONE thing: a packed head that names an **iterated-wreath class**,
`OrbIdx<[(r₁,s₁), …, (r_d,s_d)], n>` of depth `d >= 2`
(docs/plan-orbit-index-types.md §2). Everything else — the physical posture, the
chunking rule, `order: "ascending-lex"`, the uncompressed/little-endian/C-order
constraints, the missing-chunk semantics — is version 1's, inherited verbatim.

## Physical layout

A wreath-decomposed variable is a **physically ordinary Zarr array** (v2 or v3)
whose **leading dimension is the flat canonical pool**:

- Pool length = the §4 **iterated fold** over the level list, OUTERMOST-LAST:

  ```
  M₀ = n
  Mᵢ = C(Mᵢ₋₁ + rᵢ - 1, rᵢ)   at a '+' level
     = C(Mᵢ₋₁,          rᵢ)   at a '-' level
  cardinality = M_d
  ```

  This is exactly `orbit_wreath_utilities::orb_cell_count<Levels…>(n)` (C++) and
  `Blade.OrbRank.cellCountChecked` (F#) — the two independent implementations the
  runtime already pins against each other. Readers and writers MUST use one of
  those, never a re-derivation. Example: `[(2,+),(2,+)]` at `n = 3` folds
  3 → C(4,2) = 6 → C(7,2) = **21**; `[(2,-),(2,+)]` at `n = 4` folds
  4 → C(4,2) = 6 → C(7,2) = **21** as well (a coincidence at these numbers, not
  a rule).
- Cells are in **ascending-lex canonical order** — the order `orb_visit`
  emits and `OrbRank.visitStream` reproduces, cell for cell, and the order
  `orb_rank` is monotone with respect to (plan-orbidx-bijections §3: *rank order
  = visit order* is THE invariant). It is the same one order every other part of
  the system uses; there is no second order and no tolerated alternative.
- **No trailing dense dims.** Physical shape is exactly `[cardinality]`. See
  "Trailing dimensions" below.

Because the pool IS the in-memory storage (a wreath array is a bare flat `T*` of
`orb_cell_count` cells — CodeGen's `AllocWreath`, the interpreter's `allocWreath`),
both the read and the write are a **straight linear copy**, exactly like the
depth-1 `layout: "packed"` path. No unlinearize, no per-cell address arithmetic,
no reordering.

## Metadata

```json
"blade": {
  "spec_version": 2,
  "layout": "packed",
  "order": "ascending-lex",
  "index_types": [
    { "kind": "orbit", "levels": [[2, "-"], [2, "+"]], "extent": 4 }
  ],
  "decomposition": { "scheme": "flat-ranges" }
}
```

- `spec_version` is `2`. A reader that implements only version 1 MUST reject the
  store loudly; version 1's `index_types` rules already admit only
  `sym`/`antisym`/`dense`/(reserved)`herm`, so an unknown kind — `"orbit"`
  included — is a named error there, never a reinterpretation.
- `layout` is `"packed"`. `"packed-blocks"` (simplex-blocks) is **not defined**
  for an orbit head: the block grid of a simplex is itself a `SymIdx`, and a
  wreath pool's rows shrink per LEVEL, so there is no tile multiset to decompose
  it by. A `packed-blocks` store with an orbit head is a loud error.
- `order`, if present, must be `"ascending-lex"` (version 1's rule).
- The packed head becomes
  `{ "kind": "orbit", "levels": [[r₁, s₁], …, [r_d, s_d]], "extent": n }`:
  - `levels` is a JSON array of `[rank, sign]` pairs, `rank` an integer `>= 2`
    (rule 3) and `sign` the string `"+"` or `"-"`. **OUTERMOST-LAST** — the same order as
    the surface syntax `OrbIdx<[(2,-),(2,+)], 4>` and as every internal
    representation (`IROrbitClass`'s level list, `OrbRank.Level list`,
    `orb_level<…>` template packs). There is exactly one direction in the
    system; a reader must not reverse it.
  - `extent` is the class's BASE extent `n` (the fold's `M₀`), positive.

### Version-2 rules

1. **Exactly one packed group, and it is the FIRST `index_types` entry** —
   version 1's rule, unchanged.
2. `"sym"` and `"antisym"` heads stay valid under `spec_version: 2`, with
   version 1's semantics. A version-2 store may use either kind.
3. **A depth-1 orbit head is ILLEGAL**, and so is a **rank-1 level**.
   `{"kind": "orbit", "levels": [[2,"+"]], …}` is rejected, `{"kind": "orbit",
   "levels": []}` is rejected, and every level's `rank` must be `>= 2`.
   The reason is one rule: **one class, one spelling on disk.**
   `OrbIdx<[(r,+)],n>` *is* `SymIdx<r,n>` and `OrbIdx<[(r,-)],n>` *is*
   `AntisymIdx<r,n>`, exactly, so a depth-1 orbit head would be a second
   spelling of a class that already has one — two code paths that can disagree.
   A rank-1 level is the trivial group `S₁` and normalizes away at either sign
   (`OrbRank.normalizeLevels`), so admitting one would likewise let
   `[[1,"+"],[2,"+"],[2,"+"]]` and `[[2,"+"],[2,"+"]]` both name the same class.
   **Writers MUST emit `"sym"`/`"antisym"` for depth 1** (Blade's writer stamps
   `spec_version: 1` when it does) and must never emit a rank-1 level. Note the
   asymmetry with the SURFACE type, which does normalize: `OrbIdx<[(2,+),(1,-)],
   n>` is a legal thing to write in a program and lowers to `SymIdx<2,n>` — it
   is the on-disk encoding that is canonicalized, not the source language.
4. **Pool length vs. cardinality**: `shape[0]` MUST equal the level list's
   iterated-fold cardinality. A mismatch is a loud load error, never a
   reinterpretation — version 1's rule, inherited, and the reason is sharper
   here: the fold is the only thing that distinguishes `[(2,+),(2,+)]` at `n = 3`
   (21 cells) from a plain `SymIdx<4,3>` (15) or two juxtaposed `SymIdx<2,3>`
   blocks (36).
5. An `extent` or a level `rank` that overflows the fold is a diagnostic, not a
   wrap: `cellCountChecked` is exactly overflow-checked and its failure text
   propagates.

### Trailing dimensions

Version 1 allows `{"kind": "dense", "extent": d}` entries after the packed
group. **Version 2 does not, for an orbit head.** This is not a format
restriction chosen for convenience — it is what the in-memory representation
supports:

- The only producer of a wreath-typed array is deduction (`IR.deduceWreathTie`,
  a comm tie over every argument), and that rule **refuses kernel T-dims**, so a
  deduced wreath array never has trailing axes to begin with.
- `IR.classifyOutputStorage` answers `AllocWreath` only for a **sole** wreath
  index group and refuses any combination: a wreath pool is a flat cell array
  with no nested skeleton to juxtapose a dense block against, and no runtime
  layout mixes the two.

So a version-2 **writer never emits** trailing dense entries beside an orbit
head, and a version-2 **reader rejects** them with a "not yet supported"
diagnostic rather than silently mis-shaping the pool. When the in-memory side
grows trailing dims, this section is the one place that changes, and the
physical layout it would take is already fixed by version 1: `[cardinality, d₁,
…]`, row-major, cells major.

## Round-trip integrity

A reader MUST validate the pool length against the level list's cardinality
(rule 4) and MUST NOT reorder: the bytes are canonical order by definition, and
version 2 defines no alternative order to tolerate or detect. There is nothing
for a reader to "fix up". This matters more than it does at depth 1 because a
read→write round trip **cannot** catch an order mismatch — both sides shift
together (the antisymmetric-storage post-mortem, plan-orbidx-bijections §3) — so
order is pinned by comparing the pool against independently computed values, and
the format's contribution is to leave exactly one order legal.

## Reading and writing in Blade

- `z.load(store)` types a variable with an orbit head as
  `Array<T like OrbIdx<[(r₁,s₁),…], n>>` — the *same* `SymWreath` index record
  the surface type and deduction build (`IR.mkWreathIndexRecord`, reached through
  `IR.orbitNormalForm`), so a loaded array is indistinguishable from a deduced
  one: it prints its pool cells in storage order, subscripts at an arbitrary raw
  tuple (mirrored reads with the accumulated character, zero-set reads at a `'-'`
  level), and decompacts.
- `z.write(path, A)` accepts a wreath-typed `A` and writes the pool verbatim
  plus this attribute.
- Compile-time folding (`let static … |> z.read`) stays refused for every packed
  variable, orbit heads included.
- `z.read_window` is **not** defined for an orbit head (a wreath pool has no
  translated sub-class), and neither is the MPI-distributed read: version 2 is
  the flat single-pool layout only, with no distribution and no simplex-blocks
  decomposition.

## Writing a conforming store from Python

```python
import json, itertools, math, numpy as np, pathlib

# OrbIdx<[(2,+),(2,+)], 3>: pairs-of-pairs, symmetric within and between.
n, levels = 3, [(2, "+"), (2, "+")]

def cells(levels, n):
    """Canonical tuples in ascending-lex order — the ONE order (see §3 of
    docs/plan-orbidx-bijections.md). Sub-keys recurse; a '+' level takes
    non-decreasing sub-key sequences, a '-' level strictly increasing ones."""
    if not levels:
        return [(i,) for i in range(n)]
    r, s = levels[-1]
    sub = cells(levels[:-1], n)
    combo = itertools.combinations if s == "-" else itertools.combinations_with_replacement
    return [sum(keys, ()) for keys in combo(sub, r)]

pool = np.array([float(i) for i in range(len(cells(levels, n)))], dtype="<f8")
assert len(pool) == 21

root = pathlib.Path("W.zarr"); d = root / "W"; d.mkdir(parents=True)
(root / ".zgroup").write_text('{"zarr_format": 2}')
(d / ".zarray").write_text(json.dumps({
    "zarr_format": 2, "shape": [len(pool)], "chunks": [len(pool)],
    "dtype": "<f8", "compressor": None, "fill_value": 0.0,
    "order": "C", "filters": None}))
(d / ".zattrs").write_text(json.dumps({"blade": {
    "spec_version": 2, "layout": "packed", "order": "ascending-lex",
    "index_types": [{"kind": "orbit",
                     "levels": [[r, s] for (r, s) in levels],
                     "extent": n}],
    "decomposition": {"scheme": "flat-ranges"}}}))
(d / "0").write_bytes(pool.tobytes())
```

`combinations_with_replacement` over the sub-key list is the `'+'` level and
`combinations` the `'-'` level; recursing on sub-keys (not on flat coordinates)
is what makes the enumeration agree with `orb_visit` — see
plan-orbidx-bijections §2 ("peel by KEY, not by flat coordinate").
