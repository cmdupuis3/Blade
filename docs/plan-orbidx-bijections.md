# Plan: OrbIdx storage/indexing bijections

Status: phases 0-2 and the storage layer are IMPLEMENTED as standalone,
adversarially-reviewed proof artifacts (2026-08-01):
`src/cpp/orbit_wreath_utilities.hpp` (traversal/canon/rank/unrank +
read/write + nested-pointer skeleton; checker `src/cpp/orb_wreath_tests.cpp`
via `blade test orbwreath`) and `src/OrbRank.fs` (F# reference semantics;
`blade test orbrank`), cross-verified stream- and read-table-identical.

**Wired in on the WRITE side, 2026-08-02** (plan-orbit-index-types §9 step 4).
Both backends now consume these artifacts for a *deduced* wreath class rather
than re-deriving a nest: `CodeGen.genWreathApply` instantiates
`orb_visit<Levels...>` (the levels are compile-time constants at codegen time)
and sizes the pool from `orb_cell_count<Levels...>(n)`; `Interp.Loops`'s
`materializeWreathApply` drives the identical loop from `OrbRank.visitStream`.
The corpus harness diffs the two cell-for-cell, and `blade test orbwreath`
keeps diffing `orb_visit` against `visitStream`, so there is one traversal
order with two checks on it and no third emitter. Phase 2's rank/unrank is
consumed only where it must be: reading a depth ≥ 2 INPUT at a canonical
sub-key (`orb_rank`). The random-access READ path (mirrored tuples, the
character, decompaction) is still unconsumed — those seams refuse. Phase 3
(MPI) not started. Depends on
[plan-orbit-index-types.md](plan-orbit-index-types.md) (the `OrbIdx` class and
its §4 cardinality fold); feeds
[plan-orbidx-decompaction.md](plan-orbidx-decompaction.md).

## 1. What "bijection" means here — two access paths, dual views

The existing index types (`Idx`, `SymIdx`, `AntisymIdx`, `CompoundIdx`,
`SparseIdx`) each come with a storage/index bijection implemented
by the C++ helpers, and the design point that matters is **how it is
consumed**: the common case is a loop nest that traverses *every element of
the pool* in cache-optimal storage order, with **no stride arithmetic
anywhere in the hot path**. `build_skeleton`
(`src/cpp/nested_array_utilities.hpp:217-264`) makes the traversal order *be*
the allocation order (ascending-lex DFS; `pool_base` :54-87 lays the pool out
contiguously in exactly that order), and the same storage is served through
**dual views** — a contiguous pointer-bump view and a nested-pointer view —
chosen per context. Random access is the minority path:
`canonFold`/`canonLeftJustify` (`Interp/ArrayOps.fs:315-337`, mirrored at
`nested_array_utilities.hpp:846-868`) map a logical tuple to the nested
coordinate, again with no offset ever computed as a number.

`OrbIdx` must join that family with the same properties at arbitrary
`[(r₁,s₁),...,(r_d,s_d)]`:

- **Traversal path** (the hot one): a nested loop nest that touches every
  pool cell exactly once, in storage order, bumping a pointer — carrying *no*
  strides and counting *nothing*. Stride/offset math is the same shape as the
  large objects we're trying to run fast; it does not belong in the nest.
- **Random-access path** (the cold one): an arithmetic `rank`/`unrank` pair,
  needed only where an offset must exist *as a value* — decompaction, provider
  block maps (`providers/ZarrTriangularSpec.md` flat-ranges), partial reads,
  and pool slicing.

The previous draft of this plan centered the arithmetic pair; this revision
puts the traversal nest first, because that is what the existing bijections
optimize and what `OrbIdx` must not regress.

## 2. The traversal nest — segment-peeled, branch-free, in stream order

For depth-1 classes the triangular nest is easy: `for i; for j >= i` (or
`> i` strict), bounds shrinking per `lastIndex` exactly as `build_skeleton`
threads them. At a wreath level the loop variables are **composite keys**
(each sub-block's own coordinate tuple) ordered lexicographically — and lex
order on composites has an *equality-prefix* case that must NOT become a
conditional bound (`j2 >= (i2 == i1 ? j1 : i2)` is a data-dependent branch
in the hot path and inhibits vectorization). The right shape is **loop
peeling**: `K₂ ⋛ K₁` decomposes by equality-prefix length into `L+1`
disjoint regions (`L` = sub-key length), each a straight-line affine nest,
**emitted consecutively inside the shared `K₁` body** — which preserves the
exact ascending-lex stream, so the pool pointer just bumps:

```
# level (2,+) over sub-blocks that are themselves (2,+) pairs (i,j):
for i1 in 0..n-1:
  for j1 in i1..n-1:               # K1 = (i1,j1)
    emit (i1,j1,i1,j1)             # segment E: K2 = K1  (the "diagonal"; '+' only)
    for j2 in j1+1..n-1:           # segment B: i2 = i1, j2 > j1
      emit (i1,j1,i1,j2)
    for i2 in i1+1..n-1:           # segment A: i2 > i1, inner simplex free
      for j2 in i2..n-1:
        emit (i1,j1,i2,j2)
```

Every bound is `var`, `var + 1`, or a constant — the vocabulary
`BoundDependencies`/`StrictOffset` already has. Pinned-equal coordinates
(`i2 = i1` in segment B) are not loops at all, just reuse of the outer
variable. A `-` outer level drops segment E; a `-` inner level bumps the
inner lower bounds — same peeling either way. The equality segment is the
"diagonal" one might be tempted to compute separately and merge back in;
peeling makes that unnecessary — it is emitted inline at exactly its stream
position, so there is no second pass, no copy, and no order repair.

**Verified**: the reference emitter above (`segmentedNestDepth2` in
`proofs/OrbitEnum.fsx`) reproduces the ascending-lex canonical stream
*exactly* — cell-for-cell, in order — for all four sign combinations at
`n = 3` and `4` (21/6/15/3 and 55/21/45/15 cells).

**Deeper levels and higher rank compose — peel by KEY, not by flat
coordinate.** Implementation finding (2026-08-01, `orbit_wreath_utilities.hpp`):
the equality-prefix decomposition must split on the first differing
*sub-key*, not the first differing flat coordinate — pinning a partial
coordinate prefix of a composite key leaves the completion constrained by a
lex comparison against a *runtime* tuple, which is exactly the
data-dependent bound this section exists to eliminate. At key granularity
the recursion needs only two mutually recursive primitives over the level
list ("all canonical tuples" and "canonical tuples ⋛ a given one"), every
bound stays affine, and ascending order falls out of one fact: a later
first-differing key is a smaller successor, so segments run
equality-first, then descending first-difference index — the E/B/A order.
Sub-keys recurse because canonical sets are prefix-closed (each prefix
determines its valid suffix range). The
emitted-nest count is compile-time-known and grows **multiplicatively**:
`∏` over levels of (segments per adjacent key pair). Measured visitor call
sites (`-O2`, one class per translation unit): 3 for the depth-2 all-`2`
class, 18 for depth-3 all-`2`, 16 for `3+,3+`, 263 for `3+,3+,3+` — fine at
the §7.2 realistic ceiling (depth ≤ 3, `r` mostly 2), but a deep all-`3`
class gets genuinely large, and a codegen consumer should surface the count
in its cost model rather than assume "tens". That
verbosity is deliberate: fully unrolled segments, zero runtime tests,
affine innermost loops that vectorize like today's simplex nests, one
forward pass over contiguous storage.

Both dual views survive: the nested-pointer view is the wreath
generalization of `build_skeleton` (row lengths per level are the shrinking
key-suffix counts, segment by segment), and the contiguous view is the same
memory walked linearly. Which view a context gets follows the existing
per-context rules unchanged.

**Codegen shape.** The emitter extends the existing triangular machinery
(`buildLoopLevelStructure` / `LoopIndexBinding`) with one structural
concept: emitting *multiple peeled segments* per level body (a
compile-time-known unrolling), instead of one nest with a new bound form.
No new bound vocabulary is needed. On the C++ side the level signs are
template parameters, and every sign-dependent piece of the nest dispatches
at compile time — segment E in particular is `if constexpr (sign is '+')`,
so a `-` level's instantiation contains no trace of the diagonal segment,
not even a skipped test.

## 3. The random-access pair — rank/unrank for the cold path

For the minority path, one recursive definition serves every class. A
canonical tuple at level `i` is a sequence of `rᵢ` sub-keys, each a
level-(i−1) rank in `[0, Mᵢ₋₁)`; the `+` case reduces to strict via
`s_j = k_j + (j−1)` — the same strict↔weak correspondence `canonLeftJustify`
relies on, in a different encoding (that helper stores successive gaps; this
is a fixed per-position shift; same bijection, not the same map) — and
strict sequences rank by
the standard lex combinadic (binomial partial sums), computed with the
checked binomial from `proofs/OrbitEnum.fsx` (`binomChecked`: exact
overflow detection, gcd-reduced). One signature fact discovered in
implementation: **lex rank is not extent-free** — the predecessor count of
a tuple depends on `n` (only colex ranks without the extent, and §3's
DFS-order constraint pins us to lex) — so `rank` takes `n` alongside the
tuple. `unrank` is the greedy inverse (binary search on the monotone
partial sums, since it is cold-path);
`successor` exists for completeness but the traversal nest of §2 is the
iteration mechanism — `successor` is for resumable/streamed cold paths
(provider range reads), not loops.

**The one hard constraint: rank order = DFS order.** `pool_base` linear
copies, `genMpiNestSimplicial`, and the Zarr spec all assume ascending-lex
DFS, and read→write roundtrips cannot catch an order mismatch (both sides
shift together — the antisym storage post-mortem). So "rank agrees with the
§2 nest's visit order" is a stated invariant with its own test, not an
implementation detail. Order innovations (colex, Gray, blocked) are out of
scope — that is a layout-pass decision, and Blade has no layout pass.

## 4. Implementation sketch, phased

**Phase 0 — sanity anchors.** The existing `SymIdx`/`AntisymIdx` bijections
are the depth-1 instances and stay exactly as they are; every OrbIdx path
must reproduce them bit-for-bit on `[(r,+)]`/`[(r,-)]` classes
(`BLADE_CHECK_RANK=1` assert during the transition).

**Phase 1 — traversal nest (non-MPI).** Extend the loop-level builder with
segment peeling (§2 — multiple straight-line nests per level body); wreath
skeleton allocation (`count_leaves`/`build_skeleton` generalized per level);
both dual views.
This alone makes `OrbIdx` storage *usable* end-to-end in single-process
compiled and interp backends.

**Phase 2 — rank/unrank (non-MPI consumers).** Pure functions first (new
module beside `binomI64`, `IR.fs:2438`), then the interp read path
(generalize `readCompact`, `ArrayOps.fs:415-442`), then C++
`orb_rank`/`orb_unrank` beside `canon_fold` for decompaction and provider
block maps.

**Phase 3 — MPI.** `genMpiNestSimplicial`'s wreath sibling: pool slicing by
rank ranges, halo/boundary exchange over canonical blocks. Deliberately
last, per review — nothing in phases 1-2 depends on it.

## 5. Proof targets

- **Iterated hockey-stick**: extend `BladeBinomial.v`'s `mscard_binom` by one
  fold level (the missing theorem the OrbIdx plan's §4 flags).
- **Nest completeness**: the §2 segment union is disjoint and exhaustive and
  emits each canonical tuple exactly once, in ascending-lex order (the
  depth-2 instances are already enumeration-checked in `OrbitEnum.fsx`; the
  compositional general case is the lemma).
- **Bijectivity + order agreement**: `orbRank` is a bijection onto
  `[0, M_d)` and is monotone w.r.t. the nest's visit order (§3's invariant
  as a lemma).

## 6. Verification

- `proofs/OrbitEnum.fsx` enumerates canonical tuples in brute order for ~12
  configurations: assert the §2 nest visits the same sequence, `orbRank`
  maps it to `0..M_d−1`, `orbUnrank ∘ orbRank = id`.
- Depth-1 anchor: OrbIdx `[(r,±)]` paths vs the existing SymIdx/AntisymIdx
  machinery, bit-for-bit (Phase 0).
- Kernel-produced pool vs an independent oracle — never a read→write
  roundtrip (`tests/ZarrTests.fs` kernel-write block precedent).
- Overflow: depth-3 at `n = 1000` diagnoses rather than wraps — in
  `cellCount` **and** in rank/unrank; `binomChecked` is pinned at the exact
  int64 edge (`C(66,33)` exact, `C(67,33)` errors), and depth-3 `n = 360`
  round-trips at `M−1` (the largest count under the wall).
- **The test menu is a stated closure, not a curated list** (2026-08-01
  hardening): every class over the full level alphabet (r ≤ 3, both signs)
  up to depth 2, plus every sign pattern at depth 3 over rank 2 —
  1 + 6 + 36 + 8 = 51 classes, generated by template recursion in
  `src/cpp/orb_wreath_tests.cpp` (`closure`/`exact`, spec strings derived
  from the pack) and by comprehension in `tests/Test_OrbRank.fs`, with the
  count pinned in both. A missing family is a statement about the bound,
  never an oversight inside it (the empty class and rank-1 levels were
  exactly the rows a curated menu forgot).
- **Domain sweeps instead of hand probes**: every tuple in the box
  `{-1..n}^axes` either is a stream cell (rank = its index) or is refused —
  the box contains every negative, every `= n` off-by-one, and every ordered
  perturbation where only the bounds check can object. Unrank is swept over
  `[-2, M+1]`. C++ runs the box over the whole menu (≤ 5⁹ ≈ 2M probes per
  class); F# runs it where the box fits 100k, plus shape/malformed-class
  probes the box cannot express.
- **Group-character oracle** (2026-08-01 hardening): build the class's wreath
  group as explicit permutations with characters (`buildWreath` of
  `proofs/OrbitEnum.fsx`, signed) and assert `canon(g·t) = χ(g)·canon(t)`
  over the full group × raw tuples — the one check that pins the
  canonicalizer's *character* without a second canonicalizer. Runs in both
  harnesses: `tests/Test_OrbRank.fs` (~70k pairs) and
  `src/cpp/orb_wreath_tests.cpp` §(g) over the whole closure (~11M pairs),
  extent chosen by a 5M-pair budget rule rather than by hand.
- **Cross-implementation diff**: the C++ `--dump` stream is diffed against
  `OrbRank.visitStream` on every `blade test orbwreath` run, for every menu
  row — the exe's `--specs` enumerates its own menu, so the diff extends
  automatically as the closure grows and no hand copy can drift. The two
  emitters are independent constructions and drifted once before review
  caught it.
- Negative extents get one verdict from every entry point: `validateLevels`
  on the F# side, `orb_visit` returning `false` beside `orb_cell_count`'s
  `ORB_OVERFLOW` in C++.
- Cache claims measured, never at power-of-two extents (~7× artifact); the
  bar is parity with today's simplex nests on depth-1 classes.
