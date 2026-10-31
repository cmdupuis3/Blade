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
let temps = [21.0, 30.5, 26.0, 18.0, 28.0]
let m   = mask(temps, lambda(t) -> t > 25.0)
let hot = compound(temps, m)
let out = sort(hot, lambda(t) -> -t)
// EXPECT: out = [30.5, 28.0, 26.0]
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

```blade sketch
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
- Rank-1 sources, and rank-k PLAIN DENSE sources: the mask keeps the whole
  index space and `compound(A, m)` then compacts every dimension (the masked
  provider read of §2a is built this way). A rank-k packed, ragged, compound
  or sparse source is refused (BL7004).

v7: `TypeCheck.fs inferMask`, IR `IRMask`, codegen `materializeMaskForm`.
Tests: `sql-masks` ("Mask Basic", "SQL WHERE", "Mask Composition").

## 2. `compound(A, m)` — materialize a masked view

```blade sketch
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
- **No partial indexing**: fixing SOME coordinates while freeing others
  (wildcards, short prefixes, residual reads) is a **SparseIdx** feature —
  build the valid tuples as a `SparseIdx<keys>` and index `S((lat, _))` there.
  A compound axis is full-arity only; the reject cases are pinned by
  `corpus/index-types` 002–014. The arrow still enumerates exactly the
  in-bounds mask-true tuples, each once, in lexicographic order (BladeLex).

Canonical application form: FLAT positional subscripts like SymIdx — `B(lat, lon)`,
with trailing regular dims appended (`B(lat, lon, t)`; omitting the trailing
index yields the contiguous trailing-row sub-view). The historical tuple
spelling `B((lat, lon))` and every wildcard form are rejected with a steering
diagnostic pointing at SparseIdx.

v7: `TypeCheck.fs compoundViewType`, IR `IRCompoundMask`, index kinds
`IxKCompound`/`IxKCompoundDynamic`. (`IRCompoundProject` is the residual
carrier, now reached only from a SparseIdx head.) Tests: `sql-masks/001`,
`index-types/001–017`; the sparse counterparts are `index-types/171–184`.

### Single-pass and shared selection (compaction fusion v2)

A rank-1 compound over a let-bound `keep = mask(x, pred)` is a *compaction*:
the kept cells are counted in the mask's own pass, and the value gather (plus
the kept-position fill, when anything reads source positions) is ONE
branchless pass at the compound. The kept-position table is compiler
metadata: a fail-safe demand census elides it when every use reads only the
compact buffer or the cardinality (folds, `extents`, derived maps, printing);
a keyed read, `<|:>`, an argument, an alias or a capture keeps it. Under the
opt-in `BLADE_COMPACTION_FUSION=gather` the gather moves into the mask pass
itself (n + 1 private cells per buffer, capped by `BLADE_TILE_TUNE sel_cap`).

When ONE mask feeds several compounds -- `y_j = compound(c_j, keep)` over
different columns of the mask's index space (the WHERE of a multi-column
SELECT) -- they share **one selection** (T4c): one index, counted in the
mask pass, its positions (if any companion demands them) filled by one joint
pass that gathers every companion at once, each companion keeping its own
compact data store around the shared index. Values, cardinality, positions
and the folds over each companion are exactly those of the separate passes.

```blade
type Ix = Idx<11>
let x = method_for(range<Ix>) <@> lambda(i) -> Float64((Int64(i) * 4) % 11) |> compute
let w = method_for(range<Ix>) <@> lambda(i) -> Int64(i) * 10 - 3 |> compute
let keep = mask(x, lambda(v) -> v > 4.5)
let y1 = compound(x, keep)
let y2 = compound(w, keep)
let k2 = y2(5)
// EXPECT: k2 = 47
let s2 = reduce(y2, (+))
// EXPECT: s2 = 342
```

A mask predicate built from membership probes -- the semijoin `mask(cust,
lambda(c) -> contains(elig, c))`, the antijoin `!contains(...)`, either `&&` a
pure, total cell test -- joins the same plan when membership hoisting (§5)
admitted that probe: its admission record certifies the probe pure and total
(a stored, never-assigned set bound before the host; nothing writes), and every
other operand must be a pure, total scalar of the cell. A probe membership
hoisting declined (or `BLADE_MEMBER_HOIST=0`) keeps its scan, and the
compaction keeps its separate passes. The shared selection declines -- each
compound keeping its own passes -- when the mask is assigned or read between
it and its last companion, when anything in that interval may write, when a
companion is assigned, and when a companion's source is deferred or bound after
the first companion. The mask's source and every companion's source must be
proved STORED -- through any chain of `let B = A` aliases, as membership
hoisting proves a set (a deferred map, directly or behind an alias, a range, a
streamed read decline) -- and never assigned. `blade plan` rule `compaction-fusion`; `BLADE_COMPACTION_FUSION=unshared`
is the pre-T4c form (one index per compound), `b4` the round-2 form, `0` off.
Under `b4` and `unshared` the certificate is never used, but membership
hoisting is independent of both: an admitted probe still reads its hoisted
structure there (only membership hoisting's own refusal, or
`BLADE_MEMBER_HOIST=0`, keeps the per-probe scan).

Which SINGLE compounds changed emission with T4c, and why -- exactly two
shapes, each newly admitted:

- a single `compound(c, keep)` over a stored column `c` that is NOT the mask's
  source, with a total predicate: before, its own count, fill and gather
  passes ("the mask's source is not the compound's dense operand"); now the
  shared-selection path with one companion -- counted in the mask pass, one
  gather pass (the semijoin's `compound(amt, keep)` shape);
- a single compound whose predicate is a CERTIFIED membership probe: before,
  `totalScalar` refused the call and the compaction kept its separate passes;
  now, over the mask's source, the ordinary compaction plan (count in the
  mask pass, one fill+gather pass), and over another column the
  shared-selection path above.

A single `compound(x, keep)` over the mask's OWN source with a total
predicate (the maskagg shape, `index-types/515–531`) emits exactly the text it
did before, in both lanes (pinned against `unshared` by `blade test
optimize`). Tests: `index-types/515–542`.

## 2a. Missing values at a provider boundary — `read_present` / `present`

**The boundary rule: a missing value never crosses a provider boundary disguised
as a number.** A stored variable whose cells the store marks missing is
REFUSED by every default read (`alias.read`, `.stream`, `load_compound`,
`read_window`) with BL8015, which names the variable, the store, how many
cells are missing and the first one. One rule per variable, fixed at compile
time from the store's metadata:

| Provider | A cell is missing when it is |
|----------|------------------------------|
| every provider, floating variable | NaN or ±Inf — fill or written alike |
| NetCDF | equal to `_FillValue` or a `missing_value`; outside `valid_min` / `valid_max` / `valid_range` (inclusive bounds); the default fill (`NC_FILL_FLOAT` / `NC_FILL_DOUBLE`) of a floating variable with no `_FillValue` — a never-written cell |
| Zarr / Icechunk | nothing more: a FINITE `fill_value` is a value and reads as one |
| CSV | nothing more: `nan` / `inf` / `-inf` cells parse (the table is Float64) but are refused |

An INTEGER NetCDF variable is refused only on an explicit attribute:
`NC_FILL_INT`, `NC_FILL_SHORT` and `NC_FILL_BYTE` are representable integers,
so an unwritten integer cell is indistinguishable from a stored one.
NaN that a program COMPUTES is untouched — the rule is about values entering
the program, not arithmetic inside it. The rule holds in every lane: the
compiled reader scans the buffer it filled, the interpreter scans the same
cells with the same predicate, and a compile-time fold (`let static ... |>
read`) fails the `let static` assertion (BL4002, the code of every
compile-time fold failure) carrying the same sentence. A streamed read
(`.stream`) checks each fiber as it is read, with no pre-scan at open, so its
refusal can come from inside the consuming loop after earlier output — the
message then names the fiber's site coordinates. A `load_compound` read checks
only the cells its mask marks present. Writes keep the round trip honest: a
provider write of a NaN or infinite cell is refused under the same code, since
every reader of the store would refuse it.

**The opt-in is the masked read** — the present cells, as a compound array
(§2), with no sentinel or NaN ever exposed:

```blade
import csv as c
let obs  = c.load("tests/fixtures/csv_files/ms_nan.csv")  // 2 x 3, one nan and one inf cell
let vals = obs.vars.data |> c.read_present  // compound array of the 4 present cells
let ok   = c.present(obs.vars.data)         // Array<Bool like the table's axes>: true = present
let n    = extents(vals)                    // COUNT(*) of the present cells
```

`alias.read_present` and `alias.present` take the same operand as `read`
(`store.vars.<name>`, a dense variable) in both spellings, `alias.f(view)` and
`view |> alias.f`, and must be the whole value of a module-level binding, like
`compound`. They are built from §1 and §2: ONE raw read of the store, exempt
from the rule and never visible to the program (not printed, not selectable
with `--print`), then `mask(raw, present)` with the variable's rule spelled as
the mask predicate, then `compound(raw, mask)` — so `read_present` is exactly
`compound(raw, present)`, and the mask composes with companion columns like
any other mask. v7: `src/ProviderDesugar.fs` (the masked read),
`ProviderRegistry.MissingRule` (the rule and its two predicates). Tests:
`blade test netcdf|zarr|csv|icechunk` (refusals per provider, the finite-fill
case, the masked read) and the interpreter differential's provider block
(`blade test interp <category>`), which pins the masked read's values and the
refusal's sentence in both lanes.

## 2b. `sparse(values, keys)` — bundle values with an explicit key set

```blade sketch
sparse : Array<T like Idx<n>, Rest...> × keys -> Sparse<T>
```

The `SparseIdx` sibling of `compound`. Where a compound derives validity from
a **mask over a grid**, a sparse takes the valid tuples **explicitly**: `keys`
is a rank-1 array of Nat tuples — a `let static` list (baked at compile time)
or a runtime tuple-array — and `values` supplies one cell per key, already
**in key order**, so construction is a straight copy with no scatter.

- The **leading** `values` dimension is the key axis; any remaining dimensions
  become regular trailing slots whose product is the trailing stride — the
  direct analogue of the mask's leading-prefix rule in `compound`. So
  `sparse(vals, keys)` with `vals : Array<T like E, T2>` gives
  `Array<T like SparseIdx<keys>, T2>`: each key owns a contiguous block, read
  as a row sub-view (`S((i, j))`) or a scalar (`S((i, j), t)`).

- Keys keep their **given order**: iteration and the compact buffer follow it,
  never a sorted order. Duplicate keys are a construction error;
  `|values| ≠ |keys|` panics (BL8001). Rank is implicit from the tuple arity.
- Indexing is tuple-form with wildcards: `S((i, j))` is an O(1) hash lookup
  (a missing key is a runtime error), and `S((i, _))` / short prefixes gather
  the matching entries in key order — with no sorted table there is no
  window/prefix family, so every partial costs one pass.
- `range<SparseIdx<keys>>` is the iteration-side builder (visit the key set,
  compute a value per key).

Choose `CompoundIdx` when validity comes from data over a rectilinear grid
(its lex-sorted table buys contiguous layout); choose `SparseIdx` for an
arbitrary enumerated key set (edge lists, CG triples) or when you need
partial reads.

Tests: `index-types/171–184`.

## 3. `intersect(A, B)` / `union(A, B)` — set operations

```blade sketch
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

```blade sketch
unique : Array<T> -> Array<T>   // rank-1, dynamic extent ≤ input
```

First-occurrence dedup; two-pass set for exact allocation. Works for integer and
float element types. Tests: `sql-unique-contains/001–003`.

## 5. `contains(A, x)` — membership

```blade sketch
contains : Array<T> × T -> Bool
```

Linear scan; on a compound operand it scans the compact buffer bounded by the
cardinality, so membership over an empty filtered set is safely `false`.
Element type of `x` must unify with A's — mismatch is a type error.
Tests: `sql-unique-contains/004–007`.

**Evaluation contract (every lane).** The set operand is evaluated first — as
the set itself, never a snapshot of its cells — then the target `x` exactly
once, and only then are the cells read — also when the set is empty. A target
that counts its evaluations counts one per probe; a failing target
(`contains(empty, 100 / z)` with `z = 0`) aborts BL8013 even over an empty set;
a target that writes the set through a `mut` parameter is seen by the scan
(overwriting the probed key's cell makes it a miss). This order is a contract
choice (formalism §6.5 delegates relational semantics to this page): the
interpreter's, adopted by both compiled lanes in round 3. Tests:
`sql-unique-contains/015–017`, `023` (a dense set, all lanes), `024` (a
compound set: the C++ lane; the interpreter has no compound element store and
the LLVM lane refuses a compound `mut` parameter).

## 6. Semijoin / antijoin — idiom, not keyword

```blade
let A = [1, 2, 2, 5, 7]
let B = [2, 7]
let semi = compound(A, mask(A, lambda(x) -> contains(B, x)))    // 2, 2, 7: duplicates kept
let anti = compound(A, mask(A, lambda(x) -> !contains(B, x)))   // 1, 5
let n_semi = extents(semi)
let n_anti = extents(anti)
// EXPECT: n_semi = 3
// EXPECT: n_anti = 2
```

Multiplicity-preserving (unlike `intersect`). **Performance status: membership
hoisting** (round 3, `Optimize.planMembershipHoist`, `blade plan` rule
`member-hoist`, knob `BLADE_MEMBER_HOIST`). When the predicate is the kernel of an
eager top-level host (a `mask`, a `|> compute` map, a fold, or the `compound` whose inline mask it is -- the idiom above) and B is a named,
earlier rank-1 Int64 set that nothing writes and whose STORAGE is proved at the
build point — a literal, a `|> compute`, a compound, an eager set operation, a
call result or a provider read, through any chain of `let B = A` aliases (a
deferred map, directly or through an alias, is not stored there and declines) —
ONE search structure is built over B right before the host and every probe reads
it: O(|A| + |B|). The PREFERRED representation (linear for |B| <= 16, a bitmap
when the span is within max(64 |B|, 4096) and 2^26 bits, else a hash) is chosen
from (|B|, min, max) only; an allocation the build cannot make degrades the
ACTUAL one (bitmap -> hash -> linear), and each build reports both and why
(`BLADE_MEMBER_VERBOSE=1` on stderr, both lanes; the C++ run record's
`member_hoist`). All three answer exactly integer `==` — build-side duplicates
collapse, the probe side keeps its order and multiplicity, and conjunctions keep
their short circuit (only the `contains` node reads the structure). One C core
(`src/cpp/blade_member.h`) serves both compiled lanes; the interpreter keeps the
scan. A writing party (the set's producer, the probing kernel, a fold kernel or
seed, the host's payload), an unstored set, a kernel two hosts name, or non-Int64
keys keep the per-probe scan. An admitted probe also lets the semijoin's
compaction fuse: membership hoisting's admission certifies the mask predicate
pure and total (§2, "Single-pass and shared selection"), so `y = compound(amt,
keep)` over a hoisted `keep = mask(cust, lambda(c) -> contains(elig, c))` is
counted in the mask pass and gathered in one pass (corpus
`index-types/537-539`). Tests: `sql-unique-contains/011–026`, the
`sql-semijoins` tests (correctness under the hoist, including "Pattern Does Not
Fire On Conjunction").

## 6a. Inner equijoin — idiom, not keyword

```blade
type LeftRow = Idx<3>
type RightRow = Idx<4>
let left_key: Array<Int64 like LeftRow> = [1, 2, 9]
let left_amount: Array<Int64 like LeftRow> = [2, 3, 7]
let right_key: Array<Int64 like RightRow> = [1, 2, 1, 2]
let right_amount: Array<Int64 like RightRow> = [10, 10, 20, 10]
let matched = compound(method_for(left_amount, right_amount) <@> lambda(l, r) -> l * r, mask(method_for(left_key, right_key) <@> lambda(l, r) -> l == r, lambda(e) -> e))
let match_count = extents(matched)
let total = reduce(matched, (+), 0)
// EXPECT: matched = [20, 40, 30, 30]
// EXPECT: match_count = 4
// EXPECT: total = 120
```

The relation is a rank-2 compound over (left row, right row), stored in lex order: left row
ascending, then every matching right row ascending. It is a BAG — every left row pairs with
every right row of equal key, duplicates on both sides kept (4 matches above; a set-based
probe would find 2). **Performance status: the hash equijoin** (round 3 W3-2, item T2,
`Optimize.planEquiJoin`, `blade plan` rule `hash-join`, knob `BLADE_HASH_JOIN`). Written
INLINE as above — both outer products inside `compound(..)`, so they are compiler
temporaries — the C++ lane never builds the N x M pools: it builds a stable CSR over the
right keys (a counting sort over their measured span, or an open-addressing hash for a wide
one; `src/cpp/blade_member.h`), counts the matches, then probes the left keys in order,
storing each match's payload — O(N + M + J) instead of six passes over all N x M pairs, the
same cells in the same order. Negative keys match like any other (equality has no
negative-key drop, unlike `group_keys`); a left key no right row holds matches nothing,
never an out-of-domain error. `matched` stays stored; `extents`, folds and printing read it
as before. Admitted only when the keys are Int64 (plain or a positional `Idx<K>` tag), the
key kernel is exactly `l == r`, the payload kernel is a pure, total, capture-free scalar of
its two cells, the four sources are stored and never assigned, none of the parties whose
work is SKIPPED or moved writes (the four sources' producers, the payload and key kernels, the
predicate, the relation's own payload -- a fold kernel or seed that writes is left at its own
site unchanged, and is admitted), and every use of the relation reads its cells or its count
alone. A
keyed read `matched(i, j)`, an alias, a call argument, a map over the relation, a writing or
non-total kernel, float keys, a conjunctive key kernel (v1.1) or the NAMED spelling
(`let equal_keys = method_for(..) <@> lambda(l, r) -> l == r |> compute`,
`let keep = mask(equal_keys, ..)`, `let products = .. |> compute`, `compound(products,
keep)` — every named array stays stored) keep the as-written passes, with the reason in
the plan. The LLVM lane refuses rank-2 compounds (the program falls back to C++) and
records the decline; the interpreter runs the as-written passes. Tests: `sql-joins/001–019`
(014–019 are failure witnesses: a payload or key kernel that can panic on a NON-matching
pair keeps the as-written passes, so the panic stays).

## 7. `group_keys(k₁, k₂, ...)` — grouping structure

```blade sketch
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

### A `group_keys` result is name-keyed, not a value

`group_keys` and `group_by` are joined by a **binding name**. Codegen stores the
whole CSR structure in locals suffixed off that name — `gk__ngroups`,
`gk__offsets`, `gk__perm` — and gives the binding itself only an opaque
sentinel, so `group_by` recovers the grouping by re-deriving those symbols from
the name its grouping argument resolves to. The result is therefore usable only
where that name is written directly: as the value of the `let` that names it, as
a `group_by` grouping argument, and as the argument of the two grouping
accessors — `group_bucket(gk)` (§7a) and `extents(gk)` (§7b), which read the
same CSR locals the same way.

Any indirection is `BL3017`:

```blade sketch
let gk  = group_keys(region)
let gk2 = gk                        // BL3017: aliased
let b   = (sums, gk)                // BL3017: tuple element (struct/array too)
let s   = per_group(v, gk)          // BL3017: function argument
let gv  = group_by(v, group_keys(region))   // BL3017: inline, no name to bind
function mk(k) = group_keys(k)      // BL3017: returned
```

To move the **partition itself** around as ordinary data — past a function
boundary, or into an AD transform — use `group_bucket(gk)` (§7a): the row →
bucket map is a plain `Array<Int64>` with none of these restrictions.

Sharing one grouping is what a single binding is *for* — `group_by(a, gk)` and
`group_by(b, gk)` co-iterate (see §8) — and a function that needs a grouping
takes the **key array** and does both halves itself (`functions/068`). Every one
of these used to typecheck, lower, emit, and then fail in g++ on undeclared
`gk2__offsets`-style symbols; the aliased/tupled/parameter forms silently, the
inline and returned forms as a `BL7001` backend-gap note that invited a bug
report for a hole nobody intends to fill. Pinned by `sql-group-by/035`–`/039`.

This is the same invariant the same-keys co-iteration check in §8 already
relies on: that check decides "same grouping" by comparing binding **names**,
which is only meaningful because a `group_keys` result always *is* its name.

### A grouping is scope-independent

The whole chain — `group_keys`, `group_by`, and both accessors — works inside a
**function body** exactly as it does at module scope, which is the spelling a
loss function whose keys are a *parameter* needs (the grouping is derived per
call, so there is nothing to hoist):

```blade sketch
function mean_by(k: Array<Int64 like Row>, v: Array<Float64 like Row>) = {
    let gk = group_keys(k)
    let n  = extents(gk)
    let sums = method_for(group_by(v, gk)) <@> lambda(r) -> reduce(r, (+)) |> compute
    method_for(zip(sums, n)) <@> lambda(s, c) -> s / c |> compute
}
```

Identical source text answers identically on either side of the boundary,
including the **bucketing regime**: a body `let` carries no binding record to
read the `GroupKeys<I>` type off, so both back ends recover it from the body's
typed uses of `gk` rather than assuming dynamic discovery. Getting that wrong
would be silent — positional buckets number by key value over the declared
`Idx<N>` (empty groups included), dynamic discovery numbers only the keys it
observes, in first-occurrence order. Pinned by `sql-group-by/048`–`/049`.

### Negative keys select rows out

A **negative key means the row belongs to no group**: it is dropped from the
grouping entirely rather than forming a group of its own. This is `WHERE` fused
into `GROUP BY`, and it is what lets the key *function* do the selection:

```blade sketch
let seg  = (t <@> lambda(x) -> floor(x / width)) |> compute  // out-of-range rows key < 0
let gk   = group_keys(seg)
let gt   = group_by(t, gk)                                   // dropped rows never gathered
```

The drop happens in the offsets construction, so a dropped row contributes to no
group's offsets and `group_by` never reads it. It applies to every numeric key
case — integer positional buckets, unannotated dynamic discovery, and float keys
(the `floor` idiom above) — and, for the multi-key form, a row drops when **any**
component is negative. `EnumIdx` keys are exempt: there the admissible values are
declared up front, so a negative entry in that list is a value you asked for, not
a sentinel. String keys have no negative.

Tests: `sql-group-by` cases "Idx Annotated", "Enum First/String",
"Sparse Keys Dynamic", "Compound Two Keys First/Reduce",
"Negative Key Excluded".

## 7a. `group_bucket(gk)` — the row → bucket map

```blade sketch
group_bucket : GroupKeys<I> -> Array<Int64 like I>
```

For each row of the grouped source, which bucket it landed in; **-1** for a row
a negative key dropped. It is the inverse of the CSR (perm, offsets) pair, which
is otherwise reachable only from inside a ragged peel, and it spans the *source*
index space — so it co-iterates with the array that was grouped:

```blade sketch
let gk = group_keys(region)
let b  = group_bucket(gk)                       // Array<Int64 like StationIdx>
let kept = (method_for(zip(b, temps)) <@> lambda(bb, t) -> if bb >= 0 then t else 0.0) |> compute
```

The argument must be the bare `gk` name (§7, BL3017). The answer is the same for
every bucketing regime — positional, `EnumIdx`, dynamic discovery, compound —
because it inverts the tables rather than re-reading the keys; the -1 prefill is
the drop marker, since a dropped row is exactly one the permutation never names.

With the within-group rank, this is the **ungroup**: `gv(bucket(i), rank(i))`
recovers the original value at row `i` by ordinary gather. That is what lets a
per-group aggregation be re-expressed as a dense gather through `bucket`, the
shape reverse-mode AD needs through `group_by`
(`docs/plans/plan-ad-combinators.md` §2.17a). A surface accessor for `rank` does not
exist yet. The rule that makes this compose: a plain integer subscript of a grouped
value is a group POSITION (what `group_bucket` returns), checked against the group count,
while a value of the grouping's key type (`Nat<R>` over an `EnumIdx R` grouping) is a KEY,
mapped to its position (the D2 rule); the row coordinate is checked against that group's
own length (BL8006 at run time, BL4003 for a literal past a static group count).

Under `ad.grad` / `ad.jvp` you no longer write that gather yourself: a fully
reduced per-group loss over a group-linear peel kernel (`reduce(r, (+))`,
`reduce(r, (+)) / extents(r)`, `extents(r)`) is **auto-lowered** to exactly this
form — one loop over the source index space subscripted by `group_bucket(gk)`
and `extents(gk)`, with the group axis never materialized
(`docs/plans/plan-ad-combinators.md` §2.17c).

Tests: `sql-group-by` cases "Group Bucket Roundtrip", "Group Bucket Negative
Key", and the four refusals "Group Bucket Inline Argument", "Group Bucket Non
Grouping Argument", "Group Keys Alias", "Group Keys In Tuple".

## 7b. `extents(gk)` — per-group sizes, without materializing

```blade sketch
extents : GroupKeys<I> -> Array<Int64 like GroupOuter>
```

`extents` on a grouped **array** is refused: a ragged dimension has no scalar
extent. Asked of the **grouping**, the honest answer exists — one length per
group — and that is what this returns:

```blade
let region = [0, 1, 0, 2, 1]
let temps  = [20.0, 25.0, 22.0, 30.0, 27.0]
let gk    = group_keys(region)
let sums  = method_for(group_by(temps, gk)) <@> lambda(g) -> reduce(g, (+)) |> compute
let sizes = extents(gk)                      // Array<Int64 like GroupOuter>
let means = (method_for(zip(sums, sizes)) <@> lambda(s, n) -> s / Float32(n)) |> compute
// EXPECT: sizes = [2, 2, 1]
// EXPECT: means = [21.0, 26.0, 30.0]
```

Sizes are `offsets[g+1] - offsets[g]`, so **nothing is gathered** — a count-only
query never allocates or copies the values it would ignore. Rows dropped by a
negative key are counted nowhere, so the totals fall short of the source length
by exactly the dropped rows. Bare `gk` name required, as in §7a.

### The gather elision

`extents(row)` inside a peel gives the same numbers, and now costs the same. A
`group_by` whose every consumer reads only `extents(row)` never has its values
read, so codegen skips the per-group allocation and the `O(n)` copy, leaving the
row pointers null:

```blade sketch
let sizes = method_for(group_by(v, gk)) <@> lambda(r) -> extents(r) |> compute   // no gather
```

The analysis is fail-safe — any use it cannot classify as extents-only keeps the
gather, so co-iteration (`zip`) and any values-reading consumer are untouched.
Prefer `extents(gk)`: it says what you mean and needs no analysis to be fast.

Tests: `sql-group-by` cases "Group Extents", "Group Gather Elision", "Group
Extents Inline Argument"; the emission shape (which a value check cannot see) is
pinned by the "Group Gather Elision" block in `tests/Test_Sqlish.fs`.

## 7c. `segments(A)`, `files(A)`, `ungroup` — structural groupings of a segmented axis

A chunked dimension is a grouping of its axis whose keys are given by STRUCTURE
rather than by values (docs/plans/structural/07). The surface is one type and
three name-keyed forms:

```blade sketch
type I  = Idx<10>
type CI = Chunked<I, 4>                       // I segmented into runs of four: [0,4) [4,8) [8,10)
type CX = Chunked<s.index.x, store>           // a store axis, chunked as the store chunks it
type T  = Chunked<s1.index.x, [[s1, store], [s2, 2]]>   // x TILED over two stores, each with its own grid

let seg = segments(CI)                        // GroupKeys<I>: the run partition, no key array, no permutation
let g   = group_by(a, seg)                    // groups x members, as for any grouping
let back = ungroup(g)                         // Array<Float like I>: the runs reassembled over I
let fs  = files(T)                            // the FILE level of a tiled axis: one run per store, labelled
let v   = ungroup([v1, v2], T)                // a variable living in both stores, named over T
let tiles = segments(C0, C1)                  // a rank-2 array's TILE grouping: one group per (g0, g1), slot order
let t2  = segments(a)                         // the same, read off a's own annotation `Array<T like C0, C1>`
let A: Array<Float64 like CX> = s.vars.A |> z.stream   // NOT materialized; consumers below stream it
let total = reduce(A, (+))                    // block-by-block in storage order: bitwise the flat fold
let g = group_by(A, segments(A))              // one run read per group (rank 2: one TILE window per group)
let d = method_for(halo<CX, [-1, 0, 1]>) <@> lambda(w) -> A(w(1)) - A(w(-1)) |> compute   // run + ghost cells per segment
let e = A * 2.0                               // elementwise: one block (rank 2: one band of rows) at a time
let T: Array<Float64 like CLat, CLon> = s.vars.T |> z.stream    // a 2-D variable chunked in both dims
let tiles = group_by(T, segments(T))          // each tile one window read; with inherited edges, one chunk file
```

- `Chunked<I, spec>` **is** `I`: the alias adopts I's record, so arrays over `CI` and
  over `I` are one type. The segmentation lives beside the alias and is read only
  by `segments`/`files`. A file-tiled axis is a NEW axis of the summed extent,
  named by the alias; its inner only names the dimension the stores tile, and each
  store keeps its own chunk grid (a `store` entry inherits it, a literal sets it,
  none leaves the file unchunked). Only extents and chunk edges are consulted --
  never a coordinate value: the declaration order *is* the axis order.
- `segments(A)` and `files(A)` are groupings on exactly the terms of `group_keys`
  (section 7): name-keyed (BL3017), sharing one binding co-iterates,
  `group_bucket`/`extents` read them. They emit no permutation: the position
  accessor every grouping now carries (`<gk>__at`) is the identity here and
  `__perm` for a CSR grouping, so downstream consumers do not know which regime
  built the table. `segments` of a tiled axis is its innermost (per-file chunk)
  level; `files` its outermost. In a static position `segments(A)` is the tuple
  of run sizes, so `let static n = length(segments(A))` is a compile-time segment
  count.
- `segments(C0, C1)` over a rank-2 array whose slots are exactly those axes, in
  slot order (decisions D8/D10 -- the reversed order is refused, not transposed),
  is the PRODUCT grouping: one group per tile, row-major over the tile grid, the
  member the tile's cells row-major; `extents` gives tile sizes and `ungroup`
  puts the tiles back over both axes. `group_bucket` is refused for it (its
  source is two-dimensional). The cross-slot condition of the design's §4.0 holds
  by construction here, since both slots are single-slot segmentations; a
  two-dimensional mosaic of stores has no declaration form yet.
- `segments(a)` over a VALUE reads the tiling its annotation declared: an array
  whose slots name `Chunked` aliases already says its tiling, so one slot gives the
  rank-1 grouping and two the tile grouping. An unannotated value is refused with
  both spellings that work.
- A rank-1 variable bound with `.stream` on a `Chunked` axis is never
  materialized; its consumers stream it, and each choice is recorded for
  `blade plan` under the rule `segment-streaming`:
  `reduce(A, k)` walks the store one block at a time (the store's chunk edge) and
  folds the cells in storage order -- the same operation sequence as the flat
  fold, so bitwise the same answer, no reorder licence (an `omp` on the kernel is
  noted and ignored); `group_by(A, segments(..))` reads one run per group into
  its row (a rank-2 variable's tile grouping reads one rectangular window per
  tile, which is exactly one chunk file when the edges are the store's); an
  ELEMENTWISE consumer -- a map, a scalar-broadcast binop, a zip with a
  materialized array -- runs one block of the leading chunk edge at a time (a
  band of rows above rank 1), each streamed operand its own window; a halo map
  over `A`'s axis runs one segment at a time, reading each run plus the ghost
  cells its reach demands (docs/plans/structural/07 §2.3). The traversal order is
  the store's own. A fiber kernel over a streamed rank-2 variable keeps the older
  fiber path; a key grouping over a streamed variable keeps its refusal. Zarr
  today; the hooks exist for the other providers.
- `ungroup(G)` restores the axis from a `group_by(_, segments(A))` result;
  `ungroup(G, A)` names the axis when G derives from one (a map over it keeps
  neither the outer id nor the grouping registration). `ungroup([r1, .., rF], A)`
  assembles one array per store over a tiled axis; rows are bare names whose
  extents must match the stores'. `join` is untouched: it concatenates into a
  fresh axis, `ungroup` reassembles an existing one.

Folds (decision D6): a flat `reduce` over an array on a `Chunked` axis is the plain
flat fold -- the alias IS the axis, nothing is reassociated, no license is asked.
A per-segment fold is what `group_by(a, segments(A))` and an explicit combine
spell; nothing implicit exists to refuse.

Not yet: an elementwise map over a grouped array (the ragged-map emitter's
standing refusal; stream the source instead, or `ungroup` then map); streaming
of rank >= 3 variables, of a rank-2 stencil, and of netcdf/icechunk stores (zarr
today); a stencil streaming MORE than one source; a two-dimensional mosaic of
stores; string-label indexing of the `files` outer axis; static `segments` for
provider axes; chunk edges for netcdf and icechunk (zarr only). Tests:
`tests/corpus/segments/`, and `blade test zarr` sections 10c (inherited edge),
10d (two stores), 10e (streamed runs), 10f (streamed fold), 10g (stencil over
segments), 10h (elementwise consumers), 10i (rank 2: tiles and row bands).

## 8. `group_by(values, gk)` — ragged grouped view

```blade sketch
group_by : Array<T like I> × GroupKeys<I> -> Array<T like GroupOuter, GroupMember>
```

A first-class **ragged rank-2 array** (uneven group sizes), consumed by ordinary
loop objects:

```blade
let region  = [0, 1, 0, 2, 1]
let temps   = [20.0, 25.0, 22.0, 30.0, 27.0]
let gk      = group_keys(region)
let grouped = group_by(temps, gk)
let sums = method_for(grouped) <@> lambda(g) -> reduce(g, (+)) |> compute   // SUM ... GROUP BY
// EXPECT: sums = [42.0, 52.0, 30.0]
```

- Each kernel argument `g` is a per-group sub-array; group size via `extents(g)`.
- Direct rank-2 indexing works (`grouped(i)(j)`, `grouped(i, j)`); a let-bound row
  carries its length from the offsets table.
- Kernel parameters that treat `g` as an array value (not just index it) need a
  rank-1 annotation — `Array<T like RaggedIdx<_>>`, or the abstract `T^1` /
  `T<unit>^1` spelling (see "Co-iterating several grouped arrays" below).
- Grand totals = per-group reduce, then dense reduce over the results.
- **Elementwise map over a grouped result is rejected by design** ("map before
  grouping") — pinned by `sql-group-by/020`.
- A grouped source may itself be a **compound** (masked) view: the
  `mask → compound → group_keys → group_by` chain partitions what survives the
  filter — pinned by `sql-group-by/027`.

### Co-iterating several grouped arrays

Grouped arrays partitioned by the **same `group_keys` binding** co-iterate: the
rows correspond one-to-one, and the kernel receives one row per operand.

```blade
let region = [0, 1, 0, 2, 1]
let a = [1.0, 2.0, 3.0, 4.0, 5.0]
let b = [1.0, 1.0, 2.0, 2.0, 3.0]
let gk = group_keys(region)
let ga = group_by(a, gk)
let gb = group_by(b, gk)
let dots = method_for(zip(ga, gb)) <@> lambda(ra: Array<Float32 like RaggedIdx<_>>,
                                              rb: Array<Float32 like RaggedIdx<_>>) -> prodsum(ra, rb) |> compute
// per-group dot product
// EXPECT: dots = [7.0, 17.0, 8.0]
```

One offsets table drives the whole walk, so this is the ordinary ragged peel with
k row params bound at the same group — the ragged axis is *not* a product axis
and no outer product is formed.

- **A parameter binds the ROW when its written annotation is rank-1**, and the
  element otherwise. Any rank-1 spelling counts — `Array<T like RaggedIdx<_>>`,
  the abstract `T^1` / `T<unit>^1`, or a concrete rank-1 array type — and a
  `T<unit>^1` row carries its unit onto the row's element type, so a mismatched
  unit rejects. The annotation decides, not the body: a kernel that only
  *forwards* its rows (into a call, or a typed tuple) has no array-shaped use
  for a body scan to find, and used to bind one element instead. Pinned by
  `sql-group-by/029` and `/030`, with the unit mismatch in `unit-errors/014`.
- **Mixed row/element annotations are refused.** One offsets table drives the
  shared walk, so a rank-1 parameter beside a rank-0 one has no single step to
  take — annotate every parameter, or none (`sql-group-by/031`).
- **Same keys is required, and is checked on the expressions, not the types.**
  Two `group_keys` calls over identical key values are two independent
  partitions with structurally identical index records; each operand is chased
  to its `group_by` and the `group_keys` operands must resolve to the same
  binding. Mismatched keys are `BL3027` — pinned by `sql-group-by/026`.
- The kernel must **consume** its rows to a scalar (`prodsum`, `reduce`, …). A
  row-shaped result would need a grouped output type, which has no downstream
  consumers — the same reason the elementwise map above is gated.
- Ragged-beside-dense operands remain refused.

Tests: `sql-group-by` (25).

### Aggregate-only groupings run in one pass

A grouping inside a function body whose grouped arrays are consumed only by
per-group aggregates — `reduce(r, k[, init])`, `prodsum(r, r)`, `prodsum(ra,
rb)` over the same grouping, `extents(r)`, plus `extents(gk)` — compiles to ONE
pass over the rows in source order: each row's key finds its group (positional:
the key value; EnumIdx: its declared position; dynamic: one find-or-insert,
groups numbered by first occurrence) and advances every aggregate's state there.
No permutation is built and the rows are never gathered; the aggregate outputs
are the same named, stored arrays.

```blade sketch
function stats(key: Array<G like Row>, x: Array<Float64 like Row>) = {
    let gk = group_keys(key)
    let g = group_by(x, gk)          // never gathered: every consumer below aggregates
    let s = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> reduce(r, (+)) |> compute
    let q = method_for(g) <@> lambda(r: Array<Float64 like RaggedIdx<_>>) -> prodsum(r, r) |> compute
    let c = extents(gk)
    (s, q, c)
}
```

The values are the CSR form's (argued, and checked gate off against gate on bit
for bit on witnesses -- not proved): each group's fold still visits its rows
in ascending source order, from its own seed (or its first value), with its own
kernel — the pass only interleaves independent chains — and the failures are
where they were (a bad positional / EnumIdx key aborts at the grouping, row by
row; an unseeded fold's empty group raises BL8003 at that fold, with the same
location and frames as before). In the default build a LICENSED float fold (a
builtin `+` over Float64, a Float64 prodsum) keeps today's form in both lanes --
the C++ lane's per-group lane tree, the LLVM lane's flagged recurrence --
because the one pass's ascending chain is a different association;
`BLADE_FP_REPRO=1` takes the pass, and the experimental
`BLADE_GROUP_AGG=licensed` opts into the association change. Integer folds,
counts and kernels with no reorder licence take it in every build. Every
grouped value source -- count-only and unused ones included -- must be proved
stored with a write-free producer (a parameter through every call site's
argument), and each fold's state, kernel parameters, kernel result, element and
seed must share one scalar type; anything else keeps the CSR, as does a
top-level grouping (the plan covers function bodies only) and anything else the
plan does not recognize (a row read such as `r(0)`, a `group_bucket`, a
`where omp` peel, a deferred or late source or seed). `blade plan` names the
decision (`group-aggregate v1`). Tests: `sql-group-by/064-074` and the `cli`
block's gate-off / gate-on raw-bit comparisons (gap G71).

## 9. `sort(A, keyFn)` — ORDER BY

```blade sketch
sort : Array<T like I> × (T -> K) -> Array<T>   // fresh anonymous rank-1 index
```

Takes a **key extractor**, not a comparator; ascending by key
(`lambda(t) -> -t` for descending). Stable. On a compound operand, sorts the
compact buffer; the result is honestly **dense** — sorting discards coordinate
meaning, so the output gets a fresh anonymous index of the same extent. Sort
order is not tracked in the type system (lazy key-map chains — sort-skip, merge
joins — are a documented future direction).

Tests: `sql-sort` (2) + type-recovery probe.

## 10. `reduce(A[, kernel[, init]][, axes = n])` — aggregation

```blade sketch
reduce : Array<T like I₁..I_k> × (T × T -> T) -> Array<T like I₁..I_{k−n}>
reduce(A) ≡ reduce(A, (+))
```

A **left fold in ascending storage order** (`reduce([1, 2, 3, 4], lambda(a, b)
-> a - b)` is `((1 - 2) - 3) - 4 = -8`) over the **innermost axis, one axis by
default**. A rank-k operand yields a rank-(k−n) result, so

```blade
let rows = reduce([[1.0, 2.0, 3.0], [10.0, 20.0, 30.0]], (+))   // rank 1
let left = reduce([1, 2, 3, 4], lambda(a, b) -> a - b)
// EXPECT: rows = [6.0, 60.0]
// EXPECT: left = -8
```

The axis count is the optional **named final argument** `axes = n`, with
1 ≤ n ≤ rank(A). `n = rank(A)` is the full fold to a scalar:

```blade
let M = [[1.0, 2.0, 3.0], [10.0, 20.0, 30.0]]
let r1  = reduce(M, (+))                      // 1 axis  (default)
let r1s = reduce(M, (+), 100.0)               // 1 axis, seeded (per row)
let r2  = reduce(M, (+), axes = 2)            // 2 axes
let r2s = reduce(M, (+), 100.0, axes = 2)     // 2 axes, seeded (once)
// EXPECT: r1 = [6.0, 60.0]
// EXPECT: r1s = [106.0, 160.0]
// EXPECT: r2 = 66.0
// EXPECT: r2s = 166.0
```

It is a NAMED slot, not a fourth positional one, because the third positional
argument is already the seed — a bare `reduce(A, op, 2)` would be ambiguous
between "seed 2" and "fold 2 axes". `n` must be an integer literal: the result
rank is `rank(A) − n`, part of the type, so a symbolic count has no static
result type (deferred, and refused explicitly).

The row-wise `<@>` spelling remains exactly equivalent to the default, and is
still the form to reach for when the row kernel does more than fold:

```blade sketch
reduce(A, (+))  ≡  method_for(A) <@> lambda(r) -> reduce(r, (+)) |> compute
```

Composes inline in arithmetic (`100.0 + reduce(A, (+), axes = 2)`) and inside
kernels (per-group aggregation, captured-array reduction).

**Empty-input rule** (3-arg form landed, arc 4): `reduce(A, op, init)` seeds
the fold with `init` (`init ⊕ a₀ ⊕ a₁ ⊕ ...`), `init` unifies with the element
type, and the empty fold is defined as `init` — statically-empty arrays are
legal and dynamically-empty operands return `init` with no guard. WITHOUT an
init, statically-empty arrays remain a compile-time rejection and
dynamic-extent operands keep the runtime non-emptiness guard (no identity, no
defined empty fold). That rule is the STORED spelling's: an unseeded fused
fold over a DEFERRED computation (`reduce(method_for(v) <@> k, (+))`) is
seeded by the implementation and its empty fold is that seed -- `+0.0` on a
licensed route, the exact identity `-0.0` on a strict one (`BLADE_FP_REPRO=1`,
a `where repro` body, a deferred map body that may write; gap G74, formalism
§6.4); corpus `sql-reduce/031`
(stored, BL8003 before a later failure), `032` / `033` (deferred). In the PARTIAL form the seed applies **per folded group**,
not once globally: `reduce(grid, (+), 100.0)` adds 100 to every row, while
`reduce(grid, (+), 100.0, axes = 2)` adds it once to the grand total.

**Restrictions**, inherited unchanged by both forms: the folded axes must be
plain (non-compact) storage — folding the canonical cells and the logical
(mirrored) cells of a symmetric/antisymmetric/Hermitian record differ, so those
are rejected with a `decompact(A, d)` steer — and the kernel must be
**unit-endomorphic** (`T × T -> T` above is literal: `+`/`-` preserve an
element's unit, `*`/`/` do not, since folding n of them yields a grade that
depends on the extent). **The `omp` fold licence is honoured by the rank-1 fold
and by the full fold (`axes = rank`) only**: a partial fold is the row-wise
`<@>` form, whose inner fold sits in expression position and deliberately opens
no team (its context may already be a parallel region). The clause is not
silently ignored — the generated C++ carries an `[omp] requested but emitted
serial` marker at the fold, naming `axes = rank` as the spelling that threads.
A multi-axis partial fold (1 < n < rank) additionally
needs the folded slice to be dense, statically sized, untagged and unitless;
outside that envelope, write the row-wise form with the slice type spelled out.
One partial fold never builds its operand: an ANONYMOUS deferred outer product
under the default `axes = 1` -- `reduce(method_for(A, B) <@> lambda(a, b) ->
f(a, b), op[, init])` with two named rank-1 plain sources, no `where` clause on
the map kernel, and a `(+)`/`(*)` section or a seeded fold -- is rewritten into
an outer apply over `A` whose row kernel is the fused fold over `B`, so the
|A| x |B| product is never materialized (docs/plans/structural/03, piece D;
`tests/corpus/loops/206` pins it bit-for-bit against the forced spelling). A
named operand, a `where comm` map kernel, or three or more sources keep the
materialized route.

A fused `<&!>` tree terminal (`reduce((L₁ <@> k₁) <&!> (L₂ <@> k₂), (+))`) has
no partial form and stays the full fold it has always been: its leaves may have
different ranks and its result is a tuple of scalars, so "the innermost axis"
names nothing there.

> **History.** This section originally documented the innermost-axis fold; on
> 2026-08-08 it was rewritten to a full fold (plan-array-expression-fixes D1),
> which described the interim compiler rather than the intended design. The
> language owner ruled on 2026-08-09 that the original claim was the design —
> the compiler was the drift — and this section is back to it, now with the
> explicit `axes = n` count the full fold needs.

Tests: `sql-reduce` (12, incl. init basic / static-empty / dynamic-empty, the
rank-k full fold `021`, the partial default `023`, and the seed/axes ladder
`024`), `diagnostics/066–068`, `unit-errors/013`, `sql-regressions/003–004`.

## 10a. `prodsum(A, B)` — fused dot-product reduction

```blade sketch
prodsum : Array<T like I...> × Array<T like I...> -> T
```

Elementwise-multiplies two same-shape arrays and folds the products to a
single scalar in one pass — the standard dot product for real element types.
Used both at top level and as the row-reducing kernel inside a
`method_for(zip(A, B))` apply, the standard idiom for a batch of dot products
/ Gram matrices (`tests/corpus/loops/085_zip_rank2_row_prodsum.blade`).

**Complex operands are NOT conjugated** (ruled 2026-08-08, not an oversight).
`prodsum(e, e)` on a `Complex128` array means `Σ eₜ²`, not the Hermitian
inner product `Σ|eₜ|²` — neither argument is implicitly `conj`-ed. A caller
that wants the Hermitian form must conjugate explicitly with `conj` before
reducing. A generalized Lomb–Scargle DFT kernel relies on the unconjugated
reading to recover `Σ e^{2iωt}` from a single complex multiply; the Hermitian
reading would silently collapse that to `n`.

Semantics pin: `tests/corpus/index-types/235_prodsum_complex_unconjugated.blade`
(validated 2026-08-08 against the compiler: `prodsum(z, z)` over
`[1, i, -1+i]` returns `(0,-2)` = Σz², not the Hermitian `4`).

## 11. `extents(A)` — COUNT / dimensions

```blade sketch
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
let region  = [0, 1, 0, 2, 1]                  // FK: station -> region id
let weights = [5.0, 6.0, 4.0]                  // one weight per region
let deref = method_for(region) <@> lambda(r) -> weights(r) |> compute
// EXPECT: deref = [5.0, 6.0, 5.0, 4.0, 6.0]
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

## Open items

1. ~~Semijoin/antijoin hash fusion (set-hoist).~~ **Landed as membership
   hoisting (round 3 B-T1)**; see §6. Open: a deferred set forced once at the
   host, Int32 / float keys, probes in nested lambdas and function bodies.
2. ~~`reduce(A, op, init)` — empty-input identity.~~ **Landed (arc 4)**; see §10.
3. Rank-k `mask` — with the compound composition round.
4. Sort laziness / order-in-types — lazy key-map chains, merge joins.
5. Static `CompoundIdx<mask>` type path — exercise the reserved route.
6. Elementwise-over-grouped: stays rejected; revisit if a use case appears.
7. Equijoin (§6a), v1.1: the LLVM lane's CSR build and probe; conjunctive (multi-key)
   equality kernels; a fold fused into the probe (needs a snapshot proof that nothing
   between the relation and the fold writes its sources); keyed reads of the relation
   without the hashed index; the full-size bench run after a footprint / store audit.
