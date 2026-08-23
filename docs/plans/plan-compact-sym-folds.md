# Compact symmetric folds — deciding what `reduce` over triangular storage means

**Status (2026-08-18): EXPLORATORY.** No code proposed for landing yet. This document
reproduces the refusal, traces it to six sites, and argues a **semantics decision** (§4)
that must be made before any of the four lanes is touched. The decision is the
deliverable; the phasing in §7 is contingent on it.

Written on `feat/llvm-backend` after the LLVM benchmark work found it could not write
the program its own brief specified. Companion documents:
`docs/plans/plan-simplex-blocked-compute.md` (§4a/§7 licensing model — the consumer that
is currently unreachable from the surface), `docs/plans/plan-llvm-backend.md` §0.1 (which
records the refusal as binding both lanes).

---

## 1. The refusal, reproduced

Probes run against `bin/Release/net10.0/Blade.exe` on `feat/llvm-backend`. Not added to
the corpus; reproduced here so the surface is on the record.

| # | program (core line) | result |
|---|---|---|
| P1 | `let sym = method_for(A,A) <@> (comm kernel) \|> compute` then `reduce(sym, (+))` | **BL3999** "folding the canonical cells and folding the logical (mirrored) cells differ" |
| P2 | …then `reduce(sym, (+), axes = 2)` | **BL3999** "`axes = 2` exceeds the operand's **rank 1**" — a *different* refusal, see §3.2 |
| P3 | `reduce(method_for(A,A) <@> k, (+))` (deferred, no `compute`) | **BL3999**, deferred-output wording |
| P4 | `sym + 1.0` (elementwise map, contrast) | **OK** |
| P5 | `reduce(decompact(sym, 0), (+), axes = 2)` | **OK** |
| P6 | `let S: Array<Float64 like SymIdx<2,3>> = fill_random(10)` then `reduce(S,(+))` | **BL3999** |
| P7 | `prodsum(sym, sym)` | **BL3999**, prodsum wording |
| P8 | `reduce(S, (+), axes = 1)` on a declared `SymIdx<2,3>` | **BL3999** |
| P9 | `reduce(sym, lambda(r) -> reduce(r, (+)))` (leading-axis form) | **BL3999**, reported at the **inner** kernel |
| P10 | rank-3 `SymIdx<3,·>` full fold | **BL3999** |

Two facts fall out immediately:

- **The map path is fine and the fold path is entirely closed.** P4 compiles; every fold
  spelling refuses. There is no partial support to extend — this is a closed door.
- **The refusal is a *typecheck* refusal**, so it stops the C++ lane exactly as it stops
  the LLVM lane. There is no lane-vs-lane divergence today, and therefore no existing
  behavior to preserve. That is the one piece of good news in this document.

### 1.1 The numbers that anchor §4

P5 and a companion probe run end to end (`blade run`) give the two candidate answers for
the same array, `sym(i,j) = A(i)·A(j)` with `A = [1,2,3]`:

```
sym    = [[1, 2, 3], [4, 6], [9]]          // canonical pool, 6 cells
dense  = [[1, 2, 3], [2, 4, 6], [3, 6, 9]] // decompact(sym, 0), 9 cells
full   = 36    // reduce(dense, (+), axes = 2)   -- runs today
canon  = 25    // sum of the 6 pool cells        -- computed by hand from the pool
```

`36 = 2·25 − 14`, where 14 is the diagonal. **These are the two folds the diagnostic says
"differ", quantified.** Every argument in §4 is about which one `reduce` should name.

---

## 2. Where it is refused

Six sites, all in `src/TypeCheckInfer.fs`, all raising `Other` (which `src/TypeEnv.fs:886`
renders as **BL3999**, the generic type-error code — `src/Diagnostics.fs:226`):

| site | line | reached by |
|---|---|---|
| deferred computation with compact output | `src/TypeCheckInfer.fs:1657` | P3 |
| reduction-join leg (`<&!>`) over compact storage | `src/TypeCheckInfer.fs:1848` | join folds |
| explicit `axes = n` over compact trailing axes | `src/TypeCheckInfer.fs:2066` | P8 |
| leading-axis fold over a compact leading axis | `src/TypeCheckInfer.fs:2282` | P9 |
| materialized compact array (the main arm) | `src/TypeCheckInfer.fs:2536` | P1, P6 |
| `prodsum` over compact storage | `src/TypeCheckInfer.fs:2719` | P7 |

A seventh, adjacent and **deliberately separate**: the wreath/`OrbIdx` arm at
`src/TypeCheckInfer.fs:2521-2523` raises `OrbitFoldUnsupported` (**BL4003**), not
BL3999, because its remedy differs. Its corpus test carries the doctrine statement this
plan has to answer to — see §4.2.

The site at `:2536` explains itself in two parts, and only the *first* is a semantics
question:

> A single SYMMETRY-CLASS record passes the one-record check but is NOT a rank-1 axis —
> reduce would walk `extents[0]` handing out row pointers (compiled garbage). Also
> ambiguous (canonical vs logical cells). Reject until a semantics is chosen.

So the refusal is doing two jobs: guarding a **representation** hazard (§3.1) and
deferring a **semantics** choice (§4). They must be separated to make progress.

---

## 3. Why it refuses — two independent invariants

### 3.1 Pool shape vs logical shape

A compact array carries **logical** extents and a **triangular** pool. This is invariant
across all three runtimes and is stated at each:

- **C++**: `src/CodeGenExpr.fs:2745-2750` — "a rank-2 compact pool's extents table holds
  the LOGICAL extents, not the packed cell count: `gram(A,A)` emits
  `G_extents[0] = A.extents[0]; G_extents[1] = A.extents[0];` for its packed symmetric
  result." The pool itself is one contiguous DFS-order buffer reachable via
  `nested_array_utilities::pool_base` (`src/cpp/nested_array_utilities.hpp:57-73`).
- **Interpreter**: `src/Interp/ArrayOps.fs:330-354` (`allocCompact`) sets
  `Extents = extents` (logical) with `Data` a **shrinking-row `SNested` skeleton**.
- **LLVM**: `src/EmitLlvm.fs:384` (`shapeCells`) derives the cell count from the group
  descriptors, never from an extents table.

The consequence is sharp. Any fold that reads a trip count from `extents[0]` and then
walks rows is walking a **rectangle over a triangle**. In the interpreter this is not
merely wrong-valued, it is **unsound**: `reduceArray` (`src/Interp/ArrayOps.fs:754-768`)
calls `flatLeaves` (`:737-748`), which iterates `arr.Extents` and calls the *raw*
`readCell` — on a `SymIdx<2,3>` array it would ask row 1 for cell 2, and row 1 of the
jagged pool holds two cells. The guard at `src/Interp/Loops.fs:1951`
(`if a.Extents.Length <> 1 then raise InterpUnsupported`) is the only thing standing
between that and out-of-bounds reads. **This half of the refusal is load-bearing and must
stay** in some form; it is not the part §4 relaxes.

Closed forms for the cell count already exist in all three lanes and agree:
`flatCellCount` (`src/CodeGenLoopNest.fs:1920-1928`, `C(n+r−1,r)` sym / `C(n,r)`
antisym), `SimplexBlocksCore.poolCells2` (`src/SimplexBlocksCore.fs:191`), and
`shapeCells`/`grpCells` on the LLVM side.

### 3.2 The rank dichotomy — why P2 gets a different error

Two functions in the tree answer "what is the rank of this array" and they **disagree on
compact groups**:

| function | definition | `Array<Float64 like SymIdx<2,n>>` |
|---|---|---|
| `operandRank` (typecheck) | `at.IndexTypes.Length` — `src/TypeCheckInfer.fs:1959-1966` | **1** |
| `arrayRank` (codegen) | `List.sumBy (fun i -> i.Rank)` — `src/CodeGenState.fs:1196-1197` | **2** |

They coincide for every all-plain array (each slot has `Rank = 1`), which is why the
divergence has never mattered. It matters here: `reduce`'s axis-range check at
`src/TypeCheckInfer.fs:2004-2013` consumes `operandRank`, so **`axes = 2` on a rank-2
symmetric array is rejected as exceeding "rank 1"** (probe P2) — before the semantics gate
is even reached.

This is a genuine bug independent of §4, and it is the reason the *full fold spelling*
`reduce(A, (+), axes = rank(A))` is not merely refused but **unspellable**. `docs/formalism.md:693`
defines `reduce` over "the innermost `n` dimensions … `n = rank(A)` is the full fold to a
scalar" — dimensions, not index-type records. Under dimensional currying an
`Array<T like SymIdx<2,n>>` *is* `Idx<n> → Idx<n> → T`, so its dimension count is 2.
**`operandRank` is measuring the wrong thing.**

### 3.3 The adjacent seam: BL5200 in `arrayShape` is *not* this bug

The earlier session's note ("packed eigh surface blocked by pre-existing BL5200 in
arrayShape") is a **separate, earlier-phase** refusal and should not be folded into this
work. `MathElaborate.arrayShape` (`src/math/compiler/MathElaborate.fs:124-155`) resolves a
declared shape one plain axis at a time and rejects anything else with BL5200 (a *math
elaboration* code — `src/Diagnostics.fs:280`), running before typecheck. The packed-eigh
situation is documented at `src/CodeGenExpr.fs:2752-2761`: the packed LAPACK route
(`RouteEighPacked`, operand `pool_base(S.data)`) **exists and is verified**, and
"Teaching `arrayShape` compact axes is the one change that lands it." That is a
one-function change with its own gate and belongs in its own commit. It shares only a
theme with this plan. **Out of scope; noted so it is not rediscovered a third time.**

---

## 4. The semantics decision — canonical domain or full domain

This is the section the rest depends on.

### 4.1 The two candidates

- **(C) Canonical-domain.** `reduce(A, k)` folds each *stored* cell exactly once, in pool
  order. On the §1.1 example: **25**. Cost `C(n+r−1,r)` kernel applications, zero
  coordinate math, perfect locality.
- **(F) Full-domain.** `reduce(A, k, axes = r)` folds every *logical* cell `A(i₁..i_r)`
  over the full `n^r` index space, mirrors included. On the §1.1 example: **36**.

### 4.2 The evidence, honestly split

**For (C):** `docs/formalism.md:447-449` says the index type "fixes storage (triangular),
**iteration** (triangular), and access (canonicalizing)". And the LLVM lane has already
*implemented* (C): `emitCompactFold` (`src/EmitLlvm.fs:2591`) is titled "A fold over a
COMPACT (simplex) domain: **every canonical cell, once**", is wired into the reduce
emitter at `:2531` and the join path at `:2717`, and carries the full unlicensed/licensed
split from `plan-simplex-blocked-compute.md` §7. Its own doc comment at `:2581-2590`
states it is unreachable and calls itself "the back end holding up its end of a contract
the front end has not signed."

**For (F):** three arguments, and I judge them decisive.

1. **`reduce` is defined over dimensions.** `docs/formalism.md:693` — "right-to-left fold
   of the innermost `n` dimensions … `n = rank(A)` is the full fold to a scalar." A
   symmetric array has `r` logical dimensions (§3.2). Reading "dimensions" as "stored
   cells" for exactly one storage class makes `reduce` storage-dependent, which is the
   opposite of every other operation in the language.
2. **Decompaction must be value-preserving.** `decompact(A, d)` is documented as a
   *storage* change — `docs/formalism.md:140`, "compact → dense; expand a
   symmetric/antisymmetric compact axis to dense storage". The compiler's own diagnostic
   steers the user to it ("`decompact(A, d)` first for the logical fold"). Today
   `reduce(decompact(sym,0), (+), axes=2)` = **36** (probe P5, measured). If
   `reduce(sym, (+), axes=2)` were to answer **25**, the compiler's steer would land the
   user on a *different number* than the direct spelling — the two would not be a
   workaround and its shortcut, they would be two operations wearing one name.
3. **The house has already written the doctrine down, in the corpus.**
   `tests/corpus/index-types/208_orbidx_wreath_reduce_rejects.blade:2-11` (the wreath
   twin of this refusal) says it outright:

   > reduce over a compact array is not a walk of its POOL: the logical tensor has
   > prod(ri) dense axes and every mirrored tuple contributes, so a correct reduce needs
   > the orbit SIZE of each stored cell … Walking the 21 pool cells … would silently
   > answer for a 21-element array instead of the 81-element tensor it stands for — **a
   > plausible number, wrong**.

   The same file then says this is "exact PARITY with depth-1 compact storage, which
   refuses a fold for the identical canonical-vs-logical reason." The corpus is already
   committed to (F); (C) would put the depth-1 case out of parity with the wreath case
   that was explicitly written to match it.

**Resolving the (C) evidence.** Formalism's "iteration (triangular)" is a claim about the
*map/write* path, where visiting a canonical cell twice would be an outright bug (two
writes to one location). It is not a claim about fold arity. The two coexist: **maps
iterate the pool, folds range over the logical domain.**

### 4.3 Recommendation

> **Adopt (F), full-domain, as the meaning of `reduce` / `prodsum` over compact storage.
> Keep the canonical-domain fold as a real operation, but give it its own spelling
> rather than letting it silently inherit `reduce`.**

Three consequences worth stating plainly:

- **Antisym gets the right answer for free.** For `AntisymIdx<2,n>`, `A(j,i) = −A(i,j)`
  and the diagonal is not stored, so the full-domain `(+)` fold is identically **0**.
  That is correct and (C) would report a meaningless nonzero.
- **(F) does not force an `n^r` walk.** Implement it as a *pool* walk that folds each
  canonical cell `m(cell)` times, where `m` is the multinomial permutation count of the
  index tuple (rank 2: 2 off-diagonal, 1 on-diagonal). Cost is `n^r` kernel applications
  but **zero canonicalization and pool-contiguous reads** — strictly better than
  decompact-then-fold, which pays an `n^r` materialization first.
- **…but that implementation is a reordering, so it is license-gated.** Grouping a cell's
  `m` mirror copies adjacently is not the logical fold order. So: **licensed**
  (`foldReorderLicensed`, `src/CodeGenExprSupport.fs:1123`) ⇒ pool walk with
  multiplicity; **unlicensed** ⇒ the true logical order (equivalent to decompact-then-fold).
  This is exactly the split `plan-simplex-blocked-compute.md` §7 already specifies for
  brick folds, at a different granularity, and it needs no new license kind.

**What this costs the LLVM lane:** `emitCompactFold` as written computes (C) and would be
**wrong** for `reduce` under this recommendation. It is not wasted — it is precisely the
licensed canonical primitive, and the (F) fold is that loop with a repetition count. But
the doc comment at `src/EmitLlvm.fs:2561-2590` would need rewriting rather than merely
unblocking, and anyone who assumed "the LLVM arm is already done" should read this
paragraph twice. This is the single largest correction this plan makes to the existing
assumption.

### 4.4 The canonical fold still deserves a name

Canonical-domain folds are genuinely wanted (a checksum over stored cells; the
brick-partial machinery in `plan-simplex-blocked-compute.md` §4a; anything whose kernel is
already symmetry-aware). Candidate spellings, **not decided here**:

- `reduce(pool(A), k)` — a `pool(...)` view whose type is a plain rank-1
  `Array<T like Idx<C(n+r−1,r)>>`. Cheapest to type, reuses the entire existing rank-1
  fold path in all three lanes, and makes the domain visible at the call site. *Current
  preference.*
- `reduce(A, k, domain = canonical)` — a keyword argument, parallel to `axes =`.
- A `canonical(A)` combinator. Most discoverable, largest surface.

Deciding this is **P0's** deliverable (§7), because it determines whether the canonical
path needs any new IR at all — under `pool(A)` it needs none.

---

## 5. What a fix must touch

Honoring the differential-twin rule (`CLAUDE.md`: interp and C++ codegen land together);
the LLVM lane is a third consumer.

### 5.1 Typecheck

- **`operandRank`** (`src/TypeCheckInfer.fs:1959-1966`) — must sum slot arities, matching
  `arrayRank` (`src/CodeGenState.fs:1196`). Risk: this function feeds the **partial-fold
  rewrite** at `:2018-2024` (`reduce` with `n < rank` is rewritten into a `method_for` row
  map), so changing it changes which spellings take the partial path. Must be A/B'd on the
  full suite by itself, before any other change. **This is the highest-blast-radius edit
  in the plan.**
- **The six refusal sites** (§2) — relax to accept, keeping the §3.1 representation guard.
  Each site needs its own decision about *which* fold it is licensing; `:2282`
  (leading-axis) and `:1848` (join leg) are the two whose shapes are least obvious and may
  stay refused through P3.
- **Result type and units.** A full fold yields the element type; units follow the
  existing body-probed exponent rule (memory: *generic return unit transform*) with no
  change, since the kernel is unchanged. A **partial** fold (`axes < r`) over a compact
  group yields a **dense** lower-rank array — peeling one axis of a symmetric group
  destroys the symmetry — and that result-type rule is new and must be written down.

### 5.2 Lowering / IR

**Recommendation: no new IR node.** `IRReduce` (`src/IR.fs:125`) with the compact array as
operand already carries everything needed: the operand's `IRIndexType` list names the
symmetry class and arity, from which the multiplicity rule is derivable in each emitter.
Adding a marker would put the same fact in two places and invite them to drift.

The one thing that *is* needed is a **licensing bit reaching the emitters**, and
`foldReorderLicensed` already reads it off the callable — no plumbing required.

Caveat: `plan-llvm-backend.md` §0.1 records that `reduce(A, (+), axes = k)` lowers through
**`IRForRange`**, which has **no LLVM arm at all**. So the `axes = k` spelling on the LLVM
lane is blocked behind a second, unrelated gap. The bare `reduce(A, k)` spelling is not.

### 5.3 C++ emitter

`genReduceBinding` (`src/CodeGenBinding.fs:2336-2410`). The precedent is already in the
same function: the **compound operand arm** (`:2371-2387`) folds a compact buffer with
bound `cardinality * trailing_stride` and access `.data[i]`. A compact arm is the same
shape — bound `flatCellCount`, access `pool_base(A.data)[i]` — plus the multiplicity
repetition. The flat-pool *map* at `src/CodeGenLoopNest.fs:2144-2155` shows the exact
emission idiom (`pool_base` + `const size_t __fp_cells`) and is the code to mirror.
Effort here is genuinely small.

### 5.4 Interpreter

`src/Interp/Loops.fs:1946-1952`. Add a compact arm before the `Extents.Length <> 1`
guard (**do not relax the guard** — §3.1). The traversal already exists: `emitSymAware`
(`src/Interp/ArrayOps.fs:1894-1924`) walks the compact space in left-justified storage
coordinates with `boundAt` = extent minus prior group coords minus the strict constant,
reading raw cells "canonical by construction, no fold needed". Extracting `boundAt` into a
shared canonical-cell enumerator and folding over it is the whole change. For the
unlicensed logical-order path, `readCompact` (`:512-538`) already does canonicalizing
logical reads.

### 5.5 LLVM lane

Least work, most rewriting. `emitCompactFold` (`src/EmitLlvm.fs:2591`) exists, is wired at
`:2531`/`:2717`, and already carries the licensed/unlicensed split and the brick
decomposition behind `BLADE_LLVM_BRICKS`. Under §4.3 it needs the multiplicity repetition
added and its doc comment (`:2561-2590`) corrected. Note its guard: `soleSimplex2`
(`:393`) handles rank-2 only, and `arrayShapeOf` (`:841`) refuses rank ≥ 3 compact groups,
Hermitian and wreath (`:835-836`) — so the LLVM lane covers rank 2 and refuses the rest by
name, which is fine and pre-existing.

### 5.6 The licensed execution schedule — row bins and partial reuse (added 2026-08-18, user direction)

§4.3's identity `reduce(decompact(A, 0), ⊕) = reduce(A, ⊕)` is the VALUE SPEC only.
Decompaction must never be the implementation — materializing n^r cells from
C(n+r-1, r) forfeits exactly what symmetric storage bought. The full-domain fold runs
directly on the packed pool, and two ideas make it cost pool-cells work with
pool-sequential access:

**Partial reuse, not re-folding.** Under `foldReorderLicensed`, folding every
off-diagonal cell twice is unnecessary: fold each pool cell ONCE into a per-multiplicity-
class partial, then combine each class's partial class-many times —

```
full  =  diag ⊕ off ⊕ off              (rank 2 sym; for (+): total = diagSum + 2·offSum)
```

Work = pool cells exactly; the r! saving survives *inside the fold*. Factoring "⊕ each
element m times" into "⊕ the class partial m times" is precisely
associativity + commutativity, so this path is licensed-only by construction — the same
license the parallel fold already requires, no new kind. Rank r: one partial per
multiplicity class (multinomial r!/∏ multᵢ!), combined class-many times; the class of a
cell is read off its tile-free coordinate multiplicities, and for rank 2 it is simply
"first cell of the row or not". **Antisym**: the mirrored cell carries the sign
character, so the mirror class is a second accumulator over the SAME pass
(`acc⁺ ⊕= v; acc⁻ ⊕= −v`), never a second pass; for `(+)` the classes cancel to 0,
which §4.3 already pinned as correct.

**Blocking = row bins, not quadtree bricks** (user's proposal; the simplex plan's
third-measurement H1 verdict independently demands it). Rows are contiguous in the
ascending-lex pool, so a bin of consecutive rows is ONE contiguous pool span:
pool-sequential reads, zero striding — and a fold has no write side, so bricks' one
surviving rationale (operand reuse in producers) does not apply to pure pool folds at
all. Load balancing is closed-form, not searched: cells before row i (rank-2 sym) is
`i·n − i·(i−1)/2`, so equal-cell bin boundaries come from inverting that quadratic —
the same balanced flat-cell-range math the MPI backend ships (`genMpiNestSimplicial`)
and Zarr v1's `flat-ranges` chunking records on disk. Rank r: leading-index bins with
combinadic prefix counts (`SimplexBlocksCore.rankOfCoords`/`unrankToCoords` already
provide them). Row-aligned bins make the rank-2 class split branch-free: the first cell
of each sym row is the diagonal cell (antisym rows have no diagonal cell — single
class). Each bin carries its class accumulators; bins combine in ascending order —
deterministic-but-reassociated, the `FoldChunkPlan`/K-lane discipline at bin
granularity. Name the plan record **`RowBinPlan`**: the triangular sibling of
`FoldChunkPlan`, whose outermost-rectangular-only restriction (its own doc comment)
this finally lifts for simplex domains.

**The unlicensed path** must reproduce the decompacted logical row-major fold order:
a serial walk over the full square with mirrored packed reads (`readCompact`,
src/Interp/ArrayOps.fs:512-538, is the canonicalizing-read precedent) — strided on the
mirrored half, correct-but-slow, and parallelism simply refuses, matching BL4016
discipline. This is the fallback, not the product; §9.3's question (refuse-and-steer
instead) remains open but the serial walk is small enough that refusing buys little.

**Fit with §4.4/§5.5**: the off-class partial IS the canonical fold restricted to
off-diagonal cells — extend `emitCompactFold` with a multiplicity-class restriction
parameter rather than writing a sibling (one primitive, called once per class), which
keeps §4.4's canonical primitive singular and makes the full-domain surface lowering a
composition, in all three lanes. The C++ arm composes the same way over
`genReduceBinding`'s compact walk (§5.3); the interpreter over the shared
canonical-cell enumerator (§5.4).

**Steering**: if a user literally writes `reduce(decompact(A, 0), ⊕)`, do NOT build a
recognizer/peephole now — P0's formalism update documents the equivalence, and the
BL3999 steer text (pre-P3) plus the native form (post-P3) cover the path. A peephole is
an S-effort follow-on if the spelling ever shows up in real programs.

**Property pins** (integer-valued f64, per the licensed-path testing policy): (a) for
bin counts 1..7 including deliberately ragged boundaries, Σ bin class-partials
recombined == the single-walk pool fold == the decompacted oracle; (b) partition
exactness: Σ bin cell-counts == C(n+r−1, r) / C(n, r) — every cell in exactly one bin.
These two pins are the fence against the new risk class this section introduces:
**bin-boundary off-by-one** (a cell counted twice or dropped at a quadratic-inversion
boundary), which is silent on float data and loud on neither lane without the pins.

---

## 6. Corpus and diagnostics

### 6.1 Existing pins that would need updating

Only two files pin the fold refusal directly, both `// ERROR: BL3999`:

- `tests/corpus/index-types/107_reduce_symmetric_rejects.blade` — the materialized arm
  (`:2536`).
- `tests/corpus/sql-reduce/015_reduce_deferred_packed.blade` — the deferred arm
  (`:1657`).

Both would flip from reject-tests to value-pinned tests. Their comment blocks are
substantive and would need rewriting, not just re-pinning. **Trap:** both carry prose
comments; a prose line containing `=` after `// EXPECT:` parses as a pin and fails the
test (memory: *EXPECT-pin prose trap*) — use plain `//`.

Not affected but adjacent, and each must be **re-read** rather than assumed:

- `tests/corpus/index-types/208_orbidx_wreath_reduce_rejects.blade` — **BL4003**, wreath.
  Stays refused. But its comment asserts "exact PARITY with depth-1 compact storage",
  which becomes false the moment depth-1 folds work. Its prose must be updated in the same
  change even though its pin does not move.
- `tests/fixtures/llvm/bench_sym_map.blade:6-15` and `bench_sym_large.blade` — headers
  documenting "there is no such program to write". Rewrite when the program exists, and
  consider adding the licensed sym fold as the benchmark arm the brief originally wanted.
- `docs/features/sql.md` — the only doc outside `src/` carrying the refusal text.

No corpus file pins the `prodsum` (`:2719`), join-leg (`:1848`), leading-axis (`:2282`) or
`axes =` (`:2066`) sites. Those four are **unpinned refusals** — a real coverage gap, and
it means nothing will fail if one is relaxed incorrectly.

### 6.2 New corpus tests the feature needs

Per lane, per class, in `tests/corpus/index-types/` (the fold cases) and
`tests/corpus/symmetry/`:

1. Rank-2 sym full fold, integer-valued f64, value-pinned against a hand-computed
   full-domain sum (the §1.1 shape: pins **36**, and a sibling pinning the canonical
   spelling at **25**, so the two are visibly distinct in the corpus).
2. Rank-2 antisym full fold pinning **0** — the property check that makes (F) obviously
   right.
3. `reduce(decompact(A,0), …)` vs `reduce(A, …)` **agreement** test — the invariant from
   §4.2 argument 2, pinned as a test rather than an argument.
4. Partial fold (`axes = 1`) over a sym group, pinning the **dense** rank-1 result type.
5. Unlicensed-kernel fold: order-sensitive kernel, pinned to the logical order.
6. Rank-3 sym (C++/interp only; LLVM refuses by name).
7. Reject-tests that stay: Hermitian, wreath, rank ≥ 3 on the LLVM lane.
8. `blade test interp <dir>` and `--diff-oracle` coverage for every one of the above —
   these are the gates that would have caught a (C)/(F) split between lanes.

Licensed paths use integer-valued f64 and invariant checks, never float tolerance in
InterpDiff (`plan-llvm-backend.md` §6).

### 6.3 Diagnostics protocol

If the refusals simply relax and no message changes, **no BL-code work is needed** —
BL3999 is the generic `Other` code and is not being retired.

If (and only if) the narrowed refusals get a dedicated code (worth considering: BL3999 is
uninformative for a refusal this specific), the five touch points apply (memory: *adding a
BL diagnostic code*): `src/Diagnostics.fs`, the raise sites, the corpus pins,
`protocol/surface.json` (**generated** — via `blade ide surface`), and
`protocol/data/diagnostics.json` (**hand-authored**). The last two are guarded only by the
full-suite Surface block, so a miss there is silent until that block runs.

---

## 7. Phasing

Every phase is gated on the previous one being green on `blade test` **and**
`blade test --interp --diff-oracle`. Effort classes are for the compiler work only and
exclude corpus authoring.

| phase | deliverable | gate | effort |
|---|---|---|---|
| **P0** | **Decide §4.** Ratify (F) or reject it; pick the canonical-fold spelling (§4.4). Write the chosen semantics into `docs/formalism.md` §6.4 and §3.9 *before* any code. Update `tests/corpus/index-types/208`'s parity claim | a written semantics; no code | **S** |
| **P1** | Fix the rank dichotomy alone (§3.2): `operandRank` sums slot arities. **Nothing else in the commit** | full suite + `--interp --diff-oracle` A/B'd against `master`, since this changes which folds take the partial-rewrite path. Expect this to surface unrelated latent behavior; budget for it | **M** |
| **P2** | Canonical fold via the P0 spelling. Under `pool(A)` this is a *view* returning a plain rank-1 array — all three lanes reuse their existing rank-1 fold, and the LLVM lane's `emitCompactFold` becomes reachable as-is | new corpus tests 1 (canonical half) and the LLVM three-way `blade test llvm blocks` gate | **M** |
| **P3** | Full-domain `reduce` over rank-2 sym/antisym: typecheck sites `:2536` and `:1657`; C++ arm; interp arm; LLVM multiplicity. Licensed = pool walk × multiplicity, unlicensed = logical order | corpus tests 1–5; **diff-oracle is the load-bearing gate** — it is what proves the three lanes agree on 36 rather than one of them quietly answering 25 | **L** |
| **P4** | Rank ≥ 3 sym, `prodsum` (`:2719`), `axes = n` (`:2066`). LLVM refuses rank ≥ 3 by name (pre-existing) | corpus test 6; LLVM refusal pinned, not skipped | **M** |
| **P5** | Join-leg (`:1848`) and leading-axis (`:2282`) folds — the two shapes whose semantics are least obvious. May stay refused indefinitely | demand-driven; do not build speculatively | **M** |
| **P6** | Feed `plan-simplex-blocked-compute.md` §4a: licensed sym folds become the brick-fold consumer; re-run the P1 measurement gate that §0 recorded as *not passing*, now on a fold rather than a map | the §9 P1 gate in that plan, honestly re-run. A fold may or may not behave like the map did — that is the open question the map result cannot answer | **M** |

P3's licensed lowering is §5.6's row-bin schedule (`RowBinPlan`, partial reuse per
multiplicity class); its unlicensed lowering is §5.6's serial logical walk. P6's
measurement should race §5.6 row bins against the simplex plan's §4a bricks *on the
fold*: the row-bin prior is now strongly favored on H1 grounds (pool-sequential, no
write side), with bricks reserved for operand-reuse producers.

Separately and independently: **teach `MathElaborate.arrayShape` compact axes** (§3.3) to
land packed `eigh`. Effort **S**, own commit, own gate, no dependency on any of the above.

---

## 8. Risks

1. **P1 is the dangerous commit, not P3.** `operandRank` feeds the partial-fold rewrite
   (`src/TypeCheckInfer.fs:2018-2024`). Changing a rank function that has silently
   disagreed with its codegen twin since forever will move behavior somewhere unexpected.
   Land it alone, A/B the full suite, and do not bundle it.
2. **A silent (C)/(F) split across lanes is the failure mode this plan exists to prevent.**
   The LLVM lane implements (C) *today* in code that compiles. If P3 relaxes typecheck
   without correcting `emitCompactFold`, the C++ and LLVM lanes will disagree by exactly
   the diagonal, on programs that both compile and both look right. Only
   `--diff-oracle` catches it. Never run P3's gate without it.
3. **The interpreter's default is unsound, not merely different.** `flatLeaves` over
   logical extents on a jagged pool reads past short rows (§3.1). Relaxing
   `src/Interp/Loops.fs:1951` without adding a compact arm is a memory-safety bug in the
   interpreter, and the interpreter has no ASan.
4. **Four of six refusal sites are unpinned** (§6.1). There is no safety net for
   `prodsum`, the join leg, the leading-axis form or `axes =`. Add reject-pins for the
   sites that stay refused *before* touching the ones that don't.
5. **Multiplicity is only closed-form for depth-1 compact groups.** Rank-`r` sym gives a
   multinomial over repeated indices; wreath/`OrbIdx` needs a Burnside count that is not
   computed (`tests/corpus/index-types/208`). Wreath stays refused — and the P0 doc update
   must say *why* it stays refused once depth-1 works, or the parity claim rots.
6. **Hermitian is ambiguous in the sources.** `docs/formalism.md:329` says `HermitianIdx<n>`
   stores **n²**, but `canonFold` (`src/Interp/ArrayOps.fs:294-297`) canonicalizes it
   triangularly and `decompact` treats rank-2 Hermitian as dissolving to a dense conjugate-
   mirrored matrix (`src/TypeCheckInfer.fs:2971-2977`). Resolve this before including
   Hermitian in any phase; **exclude it from P0–P4**.
7. **P6 may find nothing.** `plan-simplex-blocked-compute.md` §0 already measured bricks as
   1.07x *slower* on the map at n = 6007. A fold has a different arithmetic intensity, so
   the map result does not transfer either way — but the honest prior is "no win", and P6
   should be scoped as a measurement, not a deliverable.

---

## 9. Open questions

1. **Does `axes = 1` on a rank-2 sym array mean anything useful?** It peels the innermost
   *logical* axis, yielding dense row sums (symmetric matvec against ones). Well-defined
   and useful — but is it what a user writing bare `reduce(A, (+))` expects, given the bare
   form defaults to `axes = 1`? A case can be made that the **default should be the full
   fold for compact operands**, breaking the "default is partial" rule for exactly this
   class. That would be a defensible special case or an ugly wart, and P0 should rule.
2. Should `pool(A)` (§4.4) be a general combinator over any storage class (compound and
   sparse already have compact buffers that `genReduceBinding` walks — `:2371-2387`),
   rather than a symmetry-specific one? Generalizing might retire a special case instead of
   adding one.
3. Does the multiplicity-weighted pool walk deserve to be the **only** licensed path, with
   the unlicensed logical-order path simply refusing and steering to `decompact`? That
   would cut P3's scope roughly in half at the cost of a refusal the user must work around.
