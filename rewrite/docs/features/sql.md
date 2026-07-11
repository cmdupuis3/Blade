# Blade Feature Module: Relational (SQL-like) Operations

Status: **implemented and tested in v7** (81 tests across `corpus/sql-*` and
`corpus/index-types`), previously undocumented in the formalism. This document is
the canonical specification for the relational feature set.

Design stance: Blade does not have a query language. It has a small set of
array-level operations that compose into relational queries, staying inside the
S/T model — selections are index-type transformations (masks → compound indices),
groupings are ragged arrays consumed by ordinary loop objects, and aggregation is
`reduce` inside kernels. The relational vocabulary rides on the existing type
system rather than adding a second semantics.

```blade
// SELECT temp FROM temps WHERE temp > 25 ORDER BY temp DESC
let m   = mask(temps, lambda(t) -> t > 25.0)
let hot = compound(temps, m)
let out = sort(hot, lambda(t) -> -t)
```

| SQL | Blade |
|-----|-------|
| `WHERE p` | `compound(A, mask(A, p))` |
| `WHERE p AND q` | `compound(A, mask(A, p) && mask(A, q))` (positional Bool `&&`) |
| `DISTINCT` | `unique(A)` |
| `INTERSECT` / `UNION` | `intersect(A, B)` / `union(A, B)` (value-based, dedup) |
| `x IN B` | `contains(B, x)` |
| Semijoin | `compound(A, mask(A, lambda(x) -> contains(B, x)))` |
| Antijoin | `compound(A, mask(A, lambda(x) -> !contains(B, x)))` |
| `GROUP BY k` | `group_by(values, group_keys(k))` |
| `ORDER BY e` | `sort(A, lambda(x) -> e)` |
| `SUM(...)` etc. | `reduce(A, (+))` (default kernel `(+)`) |
| `COUNT(*) WHERE` | `extents(compound(A, m))` |
| Foreign keys | integer / `EnumIdx` arrays indexed into captured lookup arrays |

---

## 1. `mask(A, pred)` — predicate → presence array

```blade
mask : Array<T like I> × (T -> Bool) -> Array<Bool like I>
```

One pass; `m(i) = pred(A(i))`. The result keeps **A's own index space** — no
values are copied and no compaction happens here. Compaction is deferred to
`compound`, so a mask composes with companion columns over the same index space
(the coordinates still mean the same thing).

- WHERE-AND / WHERE-OR are **positional** boolean combination of masks:
  `mask(A, p) && mask(A, q)`. This is distinct from the value-based set
  operations (§3).
- Predicate composition in a single pass is supported
  (`mask(A, lambda(x) -> p(x) && q(x))`).
- Rank-1 sources only, currently. Rank-k masks are reserved for the compound
  composition round (v7 emits an error).

v7: `TypeCheck.fs inferMask`, IR `IRMask`, codegen `materializeMaskForm`.
Tests: `sql-masks` ("Mask Basic", "SQL WHERE", "Mask Composition").

## 2. `compound(A, m)` — materialize a masked view

```blade
compound : Array<T like I...> × Array<Bool like I...> -> Compound<T>
```

Builds the compact `CompoundIdx` from the mask, scatters the present cells of `A`
into a compact buffer, and returns a view indexed **by original coordinates**:
present cells return their dense value; cardinality is the pass count.

- The mask must cover a **leading prefix** of A's dimensions (matched by index
  identity). The masked leading dims collapse into a single `CompoundIdx` axis;
  remaining dims become a trailing stride.
- `compound(A, mask(A, p))` inline auto-materializes the mask first.
- The **static** type-annotation form `CompoundIdx<mask>` exists (v10 §4.4–4.5)
  and is the reserved compile-time path; the runtime `compound()` builder is the
  exercised route in v7. Both denote the same index semantics.
- **Partial indexing / residual compounds**: fixing some coordinates of a
  compound (including interior and trailing wildcards) yields either a plain
  index (1 free dim) or a **residual compound** (≥ 2 free dims) — the residual of
  a mask is itself a mask. This is pinned by `corpus/index-types` 001–017
  including the reject cases (all-free, short wildcard, interior hole in trailing
  form). The executable semantics of the residual is the proofs-layer
  `has_completion` (BladeCompound); the arrow enumerates exactly the in-bounds
  mask-true tuples, each once, in lexicographic order (BladeLex).

Canonical application form: ONE tuple per compound axis — `B((lat, lon))`, wildcards inside the tuple `B((lat, _))`; rank-1 compounds take a bare scalar. The flat form `B(lat, lon)` is rejected with a steering diagnostic (a compound is one slot filled by one joint tuple; formalism §3.2).

v7: `TypeCheck.fs compoundViewType`, IR `IRCompoundMask`/`IRCompoundProject`,
index kinds `IxKCompound`/`IxKCompoundDynamic`. Tests: `sql-masks/001`,
`index-types/001–017`.

## 3. `intersect(A, B)` / `union(A, B)` — set operations

```blade
intersect, union : Array<T> × Array<T> -> Array<T>   // rank-1, dynamic extent
```

Full **SQL set semantics** — value-based and deduplicating:

- `intersect`: distinct values present in both, in first-occurrence order
  **from A**; multiplicity in B is irrelevant (membership only).
- `union`: distinct values from either; A's first occurrences before B's.

Result extent is dynamic (runtime cardinality). Implementation: two-pass
`unordered_set`. Contrast with §1's positional mask combination — masks preserve
coordinates, set ops produce fresh dense value arrays.

Tests: `sql-set-ops` ("Intersect Dedups A", "Union Dedups Both",
"Union A Subsumes B"); Reynolds composition in `reynolds/019–020`.

## 4. `unique(A)` — DISTINCT

```blade
unique : Array<T> -> Array<T>   // rank-1, dynamic extent ≤ input
```

First-occurrence dedup; two-pass set for exact allocation. Works for integer and
float element types. Tests: `sql-unique-contains/001–003`.

## 5. `contains(A, x)` — membership

```blade
contains : Array<T> × T -> Bool
```

Linear scan; on a compound operand it scans the compact buffer bounded by the
cardinality, so membership over an empty filtered set is safely `false`.
Element type of `x` must unify with A's — mismatch is a type error.
Tests: `sql-unique-contains/004–007`.

## 6. Semijoin / antijoin — idiom, not keyword

```blade
let semi = compound(A, mask(A, lambda(x) -> contains(B, x)))
let anti = compound(A, mask(A, lambda(x) -> !contains(B, x)))
```

Multiplicity-preserving (unlike `intersect`). **Performance status**: the
O(|A|+|B|) hash-set fusion (pre-building a set from B) was attempted, found to be
a no-op as wired, and removed; every `contains` is currently a linear scan, so the
idiom is O(|A|·|B|). Re-landing the set-hoist is planned ([future.md](../future.md)).
The `sql-semijoins` tests (7) guard correctness only, including "Pattern Does Not
Fire On Conjunction" (the fusion must not misfire when the predicate is a
conjunction).

## 7. `group_keys(k₁, k₂, ...)` — grouping structure

```blade
group_keys : Array<K like I> × ... -> GroupKeys<I>
```

Builds a CSR structure (offsets + permutation) partitioning positions of the
shared outer index space into buckets. Three single-key dispatch cases by the key
array's annotation:

1. **Positional** `Idx<N>` annotation → static bucket count N.
2. **`EnumIdx<[...]>`** → static reverse lookup over sparse/string key domains.
3. **Unannotated** → dynamic group discovery (hash, first-occurrence order).

Multi-key form requires rank-1 key arrays over the same outer extent; the
compound key is always dynamic (tuple-keyed hash).

Tests: `sql-group-by` cases "Idx Annotated", "Enum First/String",
"Sparse Keys Dynamic", "Compound Two Keys First/Reduce".

## 8. `group_by(values, gk)` — ragged grouped view

```blade
group_by : Array<T like I> × GroupKeys<I> -> Array<T like GroupOuter, GroupMember>
```

A first-class **ragged rank-2 array** (uneven group sizes), consumed by ordinary
loop objects:

```blade
let gk      = group_keys(region)
let grouped = group_by(temps, gk)
method_for(grouped) <@> lambda(g) -> reduce(g, (+)) |> compute   // SUM ... GROUP BY
```

- Each kernel argument `g` is a per-group sub-array; group size via `extents(g)`.
- Direct rank-2 indexing works (`grouped(i)(j)`, `grouped(i, j)`); a let-bound row
  carries its length from the offsets table.
- Kernel parameters that treat `g` as an array value (not just index it) need a
  `Array<T like RaggedIdx<_>>` annotation.
- Grand totals = per-group reduce, then dense reduce over the results.
- **Elementwise map over a grouped result is rejected by design** ("map before
  grouping") — pinned by `sql-group-by/020`.

Tests: `sql-group-by` (21).

## 9. `sort(A, keyFn)` — ORDER BY

```blade
sort : Array<T like I> × (T -> K) -> Array<T>   // fresh anonymous rank-1 index
```

Takes a **key extractor**, not a comparator; ascending by key
(`lambda(t) -> -t` for descending). Stable. On a compound operand, sorts the
compact buffer; the result is honestly **dense** — sorting discards coordinate
meaning, so the output gets a fresh anonymous index of the same extent. Sort
order is not tracked in the type system (lazy key-map chains — sort-skip, merge
joins — are a documented future direction).

Tests: `sql-sort` (2) + type-recovery probe.

## 10. `reduce(A[, kernel])` — aggregation

```blade
reduce : Array<T like I..., J> × (T × T -> T) -> <result over I...>
reduce(A) ≡ reduce(A, (+))
```

Folds the innermost dimension. Composes inline in arithmetic
(`100.0 + reduce(A)`) and inside kernels (per-group aggregation, captured-array
reduction).

**Empty-input rule** (3-arg form landed, arc 4): `reduce(A, op, init)` seeds
the fold with `init` (`init ⊕ a₀ ⊕ a₁ ⊕ ...`), `init` unifies with the element
type, and the empty fold is defined as `init` — statically-empty arrays are
legal and dynamically-empty operands return `init` with no guard. WITHOUT an
init, statically-empty arrays remain a compile-time rejection and
dynamic-extent operands keep the runtime non-emptiness guard (no identity, no
defined empty fold).

Tests: `sql-reduce` (10, incl. init basic / static-empty / dynamic-empty),
`sql-regressions/003–004`.

## 11. `extents(A)` — COUNT / dimensions

```blade
extents : Array<T like I>          -> Int64          // rank-1
extents : Array<T like I₁,...,Iₖ>  -> (Int64, ...)   // dense rank-k, outermost first
```

Static-first: emits a compile-time literal when statically evaluable, else a
runtime read. On a rank-1 compound it returns the **cardinality** — the
`COUNT(*) ... WHERE` idiom. Rejected (with guidance to use `extents(row)`) on
ragged/grouped/multi-rank-compound arrays, where a scalar answer per dimension
does not exist.

Tests: `sql-extents` (3), `sql-extents-multi-rank` (1), group sizes in
`sql-group-by/017`.

## 12. Foreign keys — arrays as references

No dedicated construct: integer (or `EnumIdx`-tagged) arrays hold key values;
lookups are ordinary captured-array indexing.

```blade
let region  : Array<Int64 like StationIdx> = ...    // FK: station -> region id
let weights : Array<Float like RegionIdx>  = ...
method_for(region) <@> lambda(r) -> weights(r) |> compute   // deref
```

- Cross-reference (`weights(region(i))`), co-iteration via `zip`, outer products
  via `method_for(a, b)`.
- `EnumIdx<[...]>` gives named key domains; enum values usable as elements.
- Struct fields as FKs ride the `ETIndexRef` infrastructure.
- Nominal index typing (formalism §4.18-equivalent) is what makes the deref safe:
  the FK array's *element* unit must match the target array's index unit.

Tests: `sql-foreign-keys` (10).

---

## Interactions with the rest of the language

- **Reynolds**: relational results compose with `reynolds`-wrapped kernels,
  including over runtime extents (`reynolds/017–021`).
- **Loop objects**: `group_by` results and compound views are ordinary
  `method_for` operands; `contains` inside mask predicates is the semijoin hook.
- **Type recovery**: mask/sort/struct-field pipelines preserve named types and
  shape through IR to codegen (pinned by `sql-v24d-probes`).
- **Symmetry**: relational ops are orthogonal to the symmetry system; masks and
  compounds live on the index level (and the compound arrow inherits
  lex-sortedness, BladeLex).

## Open items (also listed in [future.md](../future.md))

1. Semijoin/antijoin hash fusion (set-hoist) — removed no-op; redesign.
2. ~~`reduce(A, op, init)` — empty-input identity.~~ **Landed (arc 4)**; see §10.
3. Rank-k `mask` — with the compound composition round.
4. Sort laziness / order-in-types — lazy key-map chains, merge joins.
5. Static `CompoundIdx<mask>` type path — exercise the reserved route.
6. Elementwise-over-grouped: stays rejected; revisit if a use case appears.
