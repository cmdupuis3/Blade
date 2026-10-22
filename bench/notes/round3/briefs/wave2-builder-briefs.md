# Round-3 wave-2 builder briefs (condensed)

The wave-2 item briefs as handed to the builders (the preamble is in `wave1-builder-briefs.md`).

Sections (each is one former file, verbatim): [`wave2-builder-briefs.md#w2-0-precedence-antig`](#w2-0-precedence-antig), [`wave2-builder-briefs.md#w2-1-t1-membership`](#w2-1-t1-membership), [`wave2-builder-briefs.md#w2-2-t4c-shared-selection`](#w2-2-t4c-shared-selection), [`wave2-builder-briefs.md#w2-3-p3-pull-scatter`](#w2-3-p3-pull-scatter), [`wave2-builder-briefs.md#w2-4-a7c-epilogue-legs`](#w2-4-a7c-epilogue-legs), [`wave2-builder-briefs.md#w2-4-round4-lifetime`](#w2-4-round4-lifetime), [`wave2-builder-briefs.md#w2-5-n10-workload`](#w2-5-n10-workload), [`wave2-builder-briefs.md#w2-6-a4-pool-retain`](#w2-6-a4-pool-retain)

---

## W2-0-precedence-antig

_formerly `bench/notes/round3/briefs/W2-0-precedence-antig.md`_

### W2-0 -- two measured decisions from the wave-1 A/B: the scatter wins the mt8-repro precedence; anti_g per regime

Read `wave1-builder-briefs.md#builder-preamble` first (it names the integration tip; reset your worktree to it). Evidence: the wave-1 harness A/B,
`bench/notes/round3/w1-ab.md` (committed on the integration branch) -- read its section 0 and the pairkernel / comoment3 /
antisym rows.

### W2-0a: the ordered orbit scatter takes precedence over the G35 row slots when both admit (mt8 repro)

Today `scatterG35Precedence` (src/CodeGenBinding.fs ~4465-4475; `rowSlotAdmission` first, the scatter only when the
row slots decline) lets the threaded complete-row form win under an omp request in a REPRO build. Measured with
`BLADE_FOLD_ROW_SLOTS=0` (the scatter serving the same walk) at full size, 21 rounds, interleaved: pairkernel mt8 repro
9.23 -> 7.76 ms (0.841 [0.811, 0.872]; expert ratio 0.686 / 0.653 -> 0.795 / 0.805), comoment3 4.43 -> 3.74 ms (0.843
[0.817, 0.885]; 1.107 / 0.945 -> 1.303 / 1.120); the serial controls agree across all three mirrors (both strict forms print
the same bits). Both blocks were drift-flagged (indicative) but the effect is large and consistent.

Change: when `OrbitFold.planStrict` admits the walk (a stored SymIdx<2/3> pool, every level strict, the closed purity
whitelist), the scatter is taken even though the row slots would admit -- the row slots remain the form for walks the
scatter does not admit (deferred producers, rank >= 4, AntisymIdx pools, coordinate-dependent terms, mixed nests). Make it
a POLICY knob so the A/B can be re-run either way: `BLADE_ORBIT_SCATTER` keeps its meaning (0 = off); add
`BLADE_FOLD_ROW_SLOTS=prefer` meaning "row slots win when both admit" (the old order) beside unset/1 (admit, scatter wins)
and 0 (off); register in `suiteClearedKnobs`; CLAUDE.md rows for both knobs; `blade plan` records
`fold form: ordered orbit scatter (repro; the threaded complete-row form admitted but yielded: measured slower, round 3)` and,
under `prefer`, the old record. Both forms must stay bitwise identical to the serial walk under REPRO + CONTRACT=off (the
existing omp-reduce identity cases; add the precedence case's inverse: `orbit_scatter_precedes_row_slots` with the needles
`blade_scatter::` present and `__rslot` absent, and `row_slots_prefer_knob` restoring the old emission). omp-coverage: the
census must say why the omp request was not spent on a team ("the ordered orbit scatter runs serially; the threaded
complete-row form admitted but yielded ..."). The LLVM lane is unchanged (it has no row slots).

### W2-0b: anti_g default per regime

Measured (three interleaved sessions per mirror, pooled n = 63): antisym serial licensed, auto (anti_g=0) vs default 1:
1.010 [0.994, 1.016] llvm, 1.020 [1.009, 1.029] cpp -- auto SLOWER serially; antisym mt8 licensed: 0.952 [0.929, 0.987] cpp --
auto FASTER under threads. Change the default of `anti_g` (src/CodeGenExprSupport.fs tile tuning / `Toolchain.tileTune`)
to "1 when the build emits no thread-level OpenMP (BLADE_OMP_THREADS=1/0/off, or the LLVM lane), auto (0) when it does";
an explicit `BLADE_TILE_TUNE anti_g=<G>` forces either; `blade plan` prints the resolved G and why; CLAUDE.md row updated;
`blade test row-tile` and `llvm tiles` stay byte-identical for every G (they do today); add a plan-record case for each
regime in the optimize or linalg block. No change to the kernel arithmetic.

### Tests / blocks

omp-reduce, omp-pragma, omp-coverage, orbit-scatter, symmetry, row-tile, syrk-tile, llvm tiles, linalg, optimize, cli,
corpus-wiring; interp symmetry / loops; opt-diff symmetry. `blade plan bench/kernels/pairkernel/pairkernel.blade` under
`BLADE_FP_REPRO=1` (threads on): the scatter record; `blade plan bench/kernels/antisym/antisym.blade` with and without
`BLADE_OMP_THREADS=1`: the resolved anti_g. One `blade run` A/B of pairkernel at full size, repro, T=8, old vs new binary
interleaved (directional). Gap register: a note on the G35 row and the G44 row (the measured precedence), a line on the
antisym row-group entry (G1's tuning note or wherever anti_g lives).

---

## W2-1-T1-membership

_formerly `bench/notes/round3/briefs/W2-1-T1-membership.md`_

### W2-1 / B-T1 -- membership hoisting (`contains` over a small set), both lanes

Read `wave1-builder-briefs.md#builder-preamble` first (reset to the integration tip it names). Brief: `research/B-relational.md` item T1 and its
probes in `research/B-work/`. Review: `reviews/codex-review-B.md` finding 7 (HIGH) and the T1 lines of finding 11 -- close them.
Round-2/3 context: the write analysis (`Optimize.scheduleMayWriteIn`, `callbackTable`, `intervalPayloadOf`) is the admission
predicate for every party; the compaction census of W1-4 (`Optimize.compactionRoots`, `positionsDemand`) shows the shape of a
module-wide traversal.

### Today

`contains` is a linear scan rendered as an IIFE with the TARGET expression interpolated INSIDE the scan (src/CodeGenExpr.fs
~572-588); a mask predicate calling a named function keeps the call by name (~2121-2124), so the old "set hoist" was a no-op
(the `SubstMap` for contains is unwired: the "NOT YET WIRED UP" note is in CodeGenLoopNest.fs ~37-40; SubstMap has other live
uses -- do not remove it). The LLVM lane has NO `IRContains` arm (the whole program falls back to C++, confirmed by
`B-work/p1.llvm.err.txt`). The interpreter evaluates the target ONCE even for an empty set (src/Interp/Loops.fs ~2487-2508);
the C++ IIFE evaluates it per scanned element -- a pre-existing evaluation-count divergence for a non-trivial target.

### What to build

1. One evaluation contract for the target: evaluate the target expression exactly once, before the scan, in every lane
   (the interpreter's behaviour) -- including an empty set, and with the target's own failure order kept (a failing target
   fails before any element is read). Pin it (a counting / failing target over an empty and a non-empty set, both lanes +
   interpreter).
2. Membership hoisting: when the set operand is a stored array (or a named deferred array the write analysis proves
   effect-free and total), build ONE search structure before the HOST binding (the mask / map / fold that contains the probe)
   and probe it per cell. Chooser: linear / bitmap (span = max - min + 1 within a bound, M >= some minimum) / hash, decided
   deterministically from M, min, max and the knob only -- never from timing. Identical boolean results in all three
   representations (duplicates, empty set (handle M = 0 before min/max), negative and extreme keys, overflow-checked span
   arithmetic; occupancy independent of key values); forced representations fall back safely outside their domain. Integer
   keys only in v1 (Int64; EnumIdx keys if cheap). Knob `BLADE_MEMBER_HOIST` (unset/1 = on, 0 = off, `linear|bitmap|hash` =
   force), registered in `optimizerGates` (the hoist moves the set build before the host: `blade test opt-diff` compares off
   vs on), CLAUDE.md row, `blade plan` record `[member-hoist v1] <host>: applied (bitmap, span 1009, M 53) / declined -- <why>`.
3. The LLVM lane: a real `IRContains` arm (the linear form at least; the hoisted structure shared through the same runtime
   helper as the C++ lane if the helper lives in src/cpp as a C-callable core, as the tiles do) so a `contains` program no
   longer falls back; `blade test llvm` on the new corpus files must run the LLVM lane proper (assert the lane, not "0 failed").
4. Admission `membershipAdmits` (constraint 4): the set producer (stored, or a proved-pure deferred value forced at the hoist
   site, evaluated once -- say so), the enclosing fold kernel and seed, every statement inside the host and the argument /
   seed evaluation before its cell loop, every parallel body affected (the hoisted build itself is serial); a writing party
   declines to the as-written per-cell scan. Five-party matrix in the plan record and the gap row.

### Tests

corpus sql-unique-contains (next free numbers): small / large / empty / negative / duplicate key sets x linear / bitmap / hash
(forced) with identical pins; the target-evaluation-count pins; a writing set producer declines; a probe inside a `where omp`
map (the hoisted structure is read-only shared); interp and llvm on the directory; optimize decision cases; cli needles for
the three representations. Blocks: sql, index-types, loops, optimize, cli, corpus-wiring, interp sql-unique-contains /
sql-masks, llvm sql-unique-contains, opt-diff sql-unique-contains. Kernel: `semiagg` is wave 3's (needs T4c); for now add the
brief's `semiagg` SOURCE as a corpus-sized probe only if cheap. Gap row for T1 (next free number: G65) with the matrix.

---

## W2-2-T4c-shared-selection

_formerly `bench/notes/round3/briefs/W2-2-T4c-shared-selection.md`_

### W2-2 -- T4c shared selection: one index for every compound over the same mask; the membership-probe certificate

Read `wave1-builder-briefs.md#builder-preamble` first (its gap-register range is stale: the register reaches G69 on the integration branch
when you start; this item's row is **G70**). Your worktree is cut from the integration tip named in your task (it
includes W1-4's T4b/T4a and W2-1's T1 membership hoisting). Research: `round3/research/B-relational.md` section 2 "T4c"
(lines ~227-242), the `filtergroup` / `semiagg` rows of section 3, section 4 (`CompactionPlan` gains `Companions`;
"today a second compound overwrites the entry"), section 5 row T4; attack review `round3/reviews/codex-review-B.md`
findings 8 and 9 (T4c obligations: sharing an index must free it exactly once and retain each companion's data store;
demand must cover every consumer and alias; the borrowed `totalScalar` whitelist rejects captures and `contains`, so T1
does NOT automatically make the semiagg predicate eligible -- a specific pure/total membership-probe certificate with the
same snapshot/effect proof is required). W1-4's record: gap row G63, `bench/notes/round3/reviews/codex-diff-W1-4*.md`,
corpus index-types 515-531.

### The shape

`keep = mask(x, pred)`, then J companions `y_j = compound(c_j, keep)` with every `c_j` over x's index space (the R1
`filtergroup` shape; `semiagg` = `keep = mask(cust, lambda(c) -> contains(elig, c))`, `y = compound(amt, keep)`). Today
each `y_j` builds its OWN `compound_index_t<1>` (count pass + fill pass each; `index_types.h` ~357, adopting form ~386 /
~432), and B4/T4 declines every companion whose dense operand is not the mask's source ("the mask's source is not the
compound's dense operand"). `CompactionTable.ByMask` (`Optimize.fs` ~3037-3053) holds ONE plan per mask: a second
compound over the same mask overwrites the first entry -- verify that on the tip and treat it as the first thing to fix.

### The rewrite

ONE selection index is built in the mask pass (T4b's census decides whether positions exist at all; a companion's keyed
read demands them for the shared index exactly as it would for its own), owned by the plan, with a `deallocate_compound_shared`
(or equivalent single owner) so that no companion frees it and it is freed exactly once, after the last companion's
last use (hard constraint 1: each companion's DATA store stays its own named array; only the index object is shared).
The J gathers become one pass over the selected positions (or fuse into T4a's mask-site pass under
`BLADE_COMPACTION_FUSION=gather` when J <= 4), ascending, serial: identical values, cardinality and positions, so
byte-identical in both regimes. The folds over the companions are untouched (B4a / B4c keep their chains and seeds).

Part B -- the membership-probe certificate: a mask predicate that is `contains(B, v)` (or `contains(B, v) && p(v)`,
`!contains(...)` for the antijoin) with the T1 hoist ADMITTED for that host becomes eligible for the T4 single pass and
for T4c: certify it as pure and total (the probe reads a stored, never-assigned Int64 set bound before the host, no
capture of a writer, no panic site in the target expression; the T1 planner's own admission record is the proof input,
not a re-derivation) and keep the SAME snapshot / effect proof T4 uses for `totalScalar`; a predicate T1 declined stays
on the per-probe scan and T4c declines with the reason. Never widen `totalScalar` itself.

### Admission -- `selectionAdmits` extended (the compaction-fusion row; `Optimize.planCompactionFusion` ~3178,
`selectionAdmits` ~2939 / ~3638-3694, `positionsDemand` ~3329)

- producers x and every `c_j`: `assignedAnywhere` false; `c_j` dominates the mask (bound before it) or is a stored
  binding between the mask and its compound that nothing writes.
- the predicate: `totalScalar`, or the Part B certificate.
- everything from the mask through the LAST companion passes the write + force audit (`scheduleMayWriteIn programFuncs
  modul` by payload, `intervalPayloadOf`): a `keep` assigned, or read between the mask and the last companion by anything
  but a planned compound, declines; a companion written in between declines; a planned companion whose compound is
  itself read before a later companion is built is fine (its data is its own) but its INDEX must then already be complete
  (the shared index is complete at the end of the mask pass -- state that invariant in the record).
- the outer kernel and seed as in B4; `callbackTable` for parallel bodies.
- refusals: rank >= 2 or trailing dims; a companion over a different index space (BL3016 territory: never admit);
  more than one mask; a companion that escapes its index identity (returned, captured by a callable, provider output)
  keeps T4b's demand rules.
- the five-party matrix (producers / predicate / outer kernel / seed / intervening) in the `blade plan` record and G70.

First-failure (hard constraint 5): B4's barriers stay -- an intervening BL8013 fires before a later empty-fold BL8003
(pin 521's shape), a keyed BL8006 at its original site, `keep` and every named companion stored before any later failure.

### Lanes

Both: C++ `materializeMaskFormCounted` (`CodeGenBinding.fs` ~2809; counted arm `CodeGenExpr.materializeMaskForm` ~2146),
`genCompoundInitBinding` ~2374 / `genFusedCompaction` ~2541; LLVM `emitCompoundInit` and the fused compaction
(`EmitLlvm.fs` ~7824-7971; LLVM is maskagg's reference arm). Interpreter unchanged. Identity: every pin of index-types
515-531 unchanged in VALUE; new pins bitwise equal under gate-off; `interp` on the new files; `opt-diff` index-types.

### Knob, record, docs

Rides `BLADE_COMPACTION_FUSION` (unset/1 = v2 with T4c; `b4` and `gather` mirrors keep their meanings; `0` off) --
if you need a separate escape hatch for the shared index, `BLADE_COMPACTION_FUSION=unshared` (one index per compound,
the pre-T4c v2) is acceptable and must be in `optimizerGateMirrors`. Plan record `compaction-fusion ... applied -- one
selection index shared by J companions (y1, y2, ...), freed once after <last>` / `declined -- <why>`; the certificate's
own line (`membership probe certified pure/total by member-hoist's admission` / `not certified: <why>`). CLAUDE.md row
updated; `docs/features/sql.md` section 2 (single-pass and shared selection). Gap row **G70**: mechanism, matrix, the
`sel_cap` interaction (T4a's n+1 capacity applies per companion; the shared index is one object), what is NOT built.

### Tests

- corpus `tests/corpus/index-types/532+` (next free at the tip; check `ls`): two companions sharing an index (`y1(k)`,
  `y2(k)` agree with gate-off; `interp` runs it); three companions with one keyed read (positions kept for the shared
  index; BL8006 pinned at its site); a companion over a different index space refused; `keep` read between the mask and
  the last companion declines (values unchanged); a companion written in between declines; a `contains` predicate with
  the T1 hoist admitted -> T4 single pass + shared index (the certificate line), and with T1 declined -> per-probe scan,
  T4c declined; the antijoin `!contains`; the first-failure ABORT case (intervening BL8013 before an empty-fold BL8003);
  the `gather` mirror with J = 2; `sel_cap` fallback with two companions.
- `tests/OptimizeTests.fs` decision cases per matrix row; emission needles both lanes (ONE index build, ONE free, each
  companion's data store present; gate-off / `unshared` restores one index per compound); `cli` plan needles;
  `llvm` sql-* and index-types; `interp index-types`; `opt-diff index-types`; `docs`; `corpus-wiring`; `sql`.
- Directional, one core, in-session: a `filtergroup`-shaped program (the R1 sketch minus `unique`, J = 3 companions,
  n = 1,000,003, non-power-of-two) and the `semiagg` shape (M = 1009), OLD binary vs NEW interleaved, >= 11 rounds, both
  lanes, licensed and repro. The research's DROP line: < 10% on filtergroup means record and stop.

### Not in scope

T3 grouping; T2 joins; `unique` (D-B2 is fixed; keep it out of the fixtures); the interpreter; any bench kernel source.

### Report

The commit sha (ONE squashed commit on your branch on top of the tip; both co-author lines), files touched per lane, the
matrix as recorded, the directional numbers, footers for: index-types, sql, optimize, cli, llvm (index-types, sql-*),
interp index-types, opt-diff index-types, docs, corpus-wiring. Name what you could not verify.

---

## W2-3-P3-pull-scatter

_formerly `bench/notes/round3/briefs/W2-3-P3-pull-scatter.md`_

### W2-3 / D-P3 -- a row-owned PULL scatter at rank 3 (strict mt8), measured against the scatter precedence of W2-0a

Read `wave1-builder-briefs.md#builder-preamble` first (reset to the integration tip it names). Brief: `research/D-orbit-regions.md` piece P3 (and
its admission section 5). Review: `reviews/codex-review-D.md` findings 3, 4 (the P3 bullet), 6 (knob contract) and 7 (the
ownership proof and what is NOT shared) -- close them. Round-2 base: the ordered orbit scatter (`src/OrbitFold.fs` planStrict
~698; `src/cpp/blade_orbit_scatter.hpp` scatter2 / scatter3 with the same-Z third event and the retained packed H;
`src/CodeGenBinding.fs` genOrbitScatterCpp ~4120 / scatterPlan ~4237; `src/EmitLlvm.fs` ~6581 / ~6621; `blade test
orbit-scatter`; `bench/notes/round2/brief-A-ordered-scatter.md`). The wave-1 A/B (`bench/notes/round3/w1-ab.md`) measured the
SERIAL scatter beating the G35 row slots under mt8 repro on comoment3 (3.74 vs 4.43 ms, expert 1.303 / 1.120) -- that is the
baseline P3 must beat.

### The form

Rank 3, strict (every level repro), a stored SymIdx<3, p> pool: each thread OWNS whole H rows (owner a <= b: it seeds
H[a,b] once, PULLS T(c,a,b) for c < a in ascending c, then runs the THIRD=false slice on its own rows), so every pair chain
receives its source sequence from its own seed exactly as the serial scatter does; a mandatory team-completion barrier
precedes the scatter2 tail over the retained H (the middle and outer chains serial from their seeds). What is NOT preserved:
the same-Z third-event reuse across owners (f is evaluated more than once per canonical cell: 848,046 slice cells +
C(172,3) = 833,340 pulls at p = 171 vs 2,514,726 leaf applications in the serial scatter -- the gain is fewer reads and address
computations than G35's 5.0 M canonicalizing reads, not fewer f evaluations; say so in the record). Identity: bitwise the
serial scatter / the walk under REPRO + CONTRACT=off, in BOTH lanes (the LLVM lane keeps its serial scatter; the C++ pull form
is taken only when the build emits threads and the omp request is present at the exact G35 request / depth gate).

### Admission (constraint 4, review D/4)

The pool is stored and no producer or binding is moved: still audit the source use and the complete scheduled body with the
shared analysis (producer = the pool's producer, consumer = the walk's cell function, outer kernels = every level's fold kernel,
every seed, the intervening bindings between the pool's materialization and the fold -- `intervalPayloadOf`), the closed
purity whitelist of planStrict, and the request gate; unknown callbacks decline; `BLADE_ORBIT_PULL` (unset/1 = on, 0 = off) in
`suiteClearedKnobs`; CLAUDE.md row; `blade plan` record with the five-party matrix and the ownership plan (rows per thread,
pulls per row).

### Tests

`blade test orbit-scatter`: pull-on vs pull-off vs serial scatter vs the walk, BIT-PATTERN identity (not NaN-equivalence or
printed text: compare raw bits of every H cell and the total) under REPRO + CONTRACT=off, at T = 1, 3, 8 observed, with
all-equal / two-equal / distinct triples, ragged tails (p = 13, 23, 171 smoke), non-commutative kernels and nonzero / signed-zero
seeds; a route assertion (the pull form's needle). omp-coverage: the team observed. Corpus symmetry/ (next free numbers):
value pins with the knob on / off. Blocks: orbit-scatter, symmetry, omp-reduce, omp-coverage, optimize, cli, corpus-wiring,
interp symmetry, llvm symmetry, opt-diff symmetry. Directional measurement: comoment3 full size, repro, T=8, pull vs
`BLADE_FOLD_ROW_SLOTS=0` (the serial scatter) vs default, interleaved 5 rounds -- report; the harness A/B is the orchestrator's.
Gap row G66 (the measured form) at the end; update G44's note.

---

## W2-4-A7c-epilogue-legs

_formerly `bench/notes/round3/briefs/W2-4-A7c-epilogue-legs.md`_

### W2-4 -- A7c producer-input legs: saxpy's input sums ride the y2 producer pass

Read `wave1-builder-briefs.md#builder-preamble` first (its gap-register range is stale: the register now reaches G68; this item's row is
**G69**). Integration tip at launch: `e4dec0ba` on `worktree-bench-fortran` (your worktree is cut from it). Research:
`round3/research/A-census.md` Part 2 rows 9-10 and section 5 "A7c", section 6 row A7c, section 7 row A7c; attack review
`round3/reviews/codex-review-A.md` finding 11 (A7c: BUILD AFTER FIXES -- totality, dominating captures / seeds, operand /
index equality, alias checks, a complete intervening audit, per-leg licence, results bound at their original points).

### The cell and the shape

`bench/kernels/saxpy/saxpy.blade`: `y2 = method_for(zip(x, y)) <@> lambda(u, v) where omp(u: 1) -> 1.5 * u + v |> compute`
(named output, stored), `checksum = reduce(map(y2) w*w, where omp (+), 0.0)` (B1's cell epilogue already streams it
behind the store), and `input_checksum = reduce(x, (+)) + reduce(y, (+))` -- two STRICT legs (no source licence) over the
producer's INPUT operands, emitted today as a separate joined pass over x and y (32 MB read; emitted
`saxpy_repro.cpp`: the joined 2-leg input sums seeded `-(0.0)`, after the y2 pass). ifx updates the kernel and the
input-checksum chains in ONE loop (G36, asm-confirmed). Measured (w1, live): saxpy serial repro 1.375 / **0.878** vs ifx,
serial licensed 1.377 / 0.972; Blade's repro net (total minus the input-only twin) 3.30 ms vs ifx 1.97 ms -- one extra pass.
Target: serial repro ifx ~1.0, serial licensed ~1.03. The mt8 cells are NOT a target of this item (see "threads").

The region after W1-D's one-level inlining has the y2 producer and the two legs in one scheduling region; VERIFY that on
the tip (`blade plan` and the emitted C++ at `e4dec0ba`, `BLADE_FP_REPRO=1`, `BLADE_OMP_THREADS=1`) before designing --
if the function boundary is still there, say so in your report and admit through it only with the same proof.

### The rewrite

The producer loop carries each admitted leg as extra fold state: a strict leg is its own ascending chain from its own
seed (W1-D's `-0.0` identity for an unseeded `(+)` leg, exactly as the standalone fold seeds it), fed by the value the
loop already loads for that operand; a licensed leg (one with its OWN `where omp` fold licence) keeps its own K-lane
tree, never unioned with another leg's licence (G7 policy) and never inherited from the map. Each leg's result is
bound where the original fold was (the combine `+` of the two legs and anything downstream stay at their original
points; nothing is printed or stored earlier). B1's own output leg (the y2 checksum) is untouched and the input legs
stay disjoint from it: a leg over the producer's OUTPUT is not an input leg. The sibling-fold join's existing exclusion
(`Optimize.fs` ~4827: "a leg over a fresh `|> compute`d rank-1 map of the same region ... while BLADE_CELL_EPILOGUE is
on") is about the output leg; do not weaken it.

Strict legs are never threaded: when the producer is emitted with a thread team (mt8), the strict legs do not ride
(each would be split into per-thread chains) -- DECLINE with the record `declined -- the producer runs a thread team;
the strict legs x-sum, y-sum keep their serial pass` (and no `[omp]` census line: the legs requested nothing). A licensed
leg under a threaded producer may follow B1's Path B per-chunk partial scheme only if you can show the chunk partition
and the combine order are exactly the standalone licensed fold's (same tree, same thread-order combine); otherwise
decline it too. Serial builds (`BLADE_OMP_THREADS=1`, or no team emitted) take the legs in both lanes.

### Admission -- `producerEpilogueLegAdmission` (new, in `Optimize.fs` beside `planHotRowFolds`' rank-1 arm /
`shareSiblingFoldsModule`; a side table, IR untouched)

- producer: a forced, stored rank-1 map (`|> compute`) over a plain co-iteration nest (the epilogue-capable form; a
  tile / dispatched / non-plain nest declines as B1 does), not written after its binding (`assignedAnywhere` false).
- each leg: a complete fold whose operand is EXACTLY one of the producer's input operands -- the same named stored array
  binding (not a view, slice, alias or a deferred map of it), the same index space / extent (operand / index equality),
  the array never assigned anywhere; the leg's kernel and seed are closed-total (`IRTotality`: no panic site -- a
  kernel that can BL8013 / BL8006 / BL8003 declines, because hoisting it ahead of an intervening binding could change
  the first failure); every capture of the kernel and of the seed, and the seed itself, DOMINATE the producer (bound
  before it); the seed is evaluated once, at the chain's start.
- intervening bindings (producer .. each leg, inclusive of the leg's own statement): `scheduleMayWriteIn programFuncs
  modul` over every one, by payload (`bindingPayload` / `intervalPayloadOf`, the W1-4 side-table writers): none may write
  the operand arrays, the captures or the seed's inputs; a writing LEG declines (G43).
- the outer kernel / seed around the legs (the `+` combine; a fold the legs feed): audited by the same predicate.
- `callbackTable` for every parallel body in the region when the producer requests omp.
- the five-party matrix (producer kernel / leg kernels / outer kernel / seeds / intervening) goes into the `blade plan`
  decision record and the G69 row.

Failure equivalence (hard constraint 5): with total legs and results bound at their original points, a panic in an
intervening binding fires at the same cell with the same partial output as as-written; pin it (an `// ABORT:` case
with a leg admitted and a later intervening BL8013).

### Lanes

Both (hard constraint 3): C++ (`CodeGenLoopNest.tryGenFlatEpilogueNest` ~2584 and the cell-epilogue sites in
`CodeGenBinding.fs` ~374-424, the B1 lane-state machinery) and LLVM (`EmitLlvm.tryBindFlatEpilogueProducer` ~4541, called
from ~4054: the flat-epilogue twin) -- the serial saxpy reference lane is blade-llvm,
so a C++-only change would not move the headline cell. Interpreter unchanged (values identical). Identity: REPRO
(`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`) bitwise against gate-off in both lanes (each leg is the same ascending chain
from the same seed; only the interleaving with the stores changes); a licensed leg bitwise too where its lane tree is
the standalone fold's (say so), else within 1e-12 and recorded as a licensed association change.

### Knob, record, docs

`BLADE_EPILOGUE_LEGS` unset/1 = on, 0/off = off, registered in `optimizerGates` (opt-diff compares off vs on). Plan
rule `epilogue-legs v1`: `applied -- legs x-sum, y-sum ride y2's producer pass (strict chains, ascending, own seeds)` /
`declined -- <why>` with the matrix. CLAUDE.md env-table row. `docs/features.md` sibling-folds / cell-epilogue entry.
Gap row **G69** (after G68; W2-1 and W2-3 take G65 / G66 on their branches -- do not reuse those numbers): mechanism,
the matrix, the measured numbers, what is NOT built (threaded strict legs = G35-class; generator fusion = A7a).

### Tests

- corpus `tests/corpus/sibling-folds/019+` (the directory's next free number at the tip is 019; pins type-spelled):
  two strict legs over the producer's inputs (values = gate-off; `interp` runs it); an unseeded `(+)` leg and a seeded
  non-commutative leg (`lambda(a, b) -> a - b`, strict order visible); a licensed leg; a leg over the producer's OUTPUT
  (not admitted as an input leg; B1 unchanged); a writing leg declines (G43); an intervening write to x declines; a
  capture bound between producer and leg declines; a seed bound after the producer declines; a non-total leg kernel
  declines; a leg over an alias / slice of x declines; the first-failure ABORT case; the mt8 decline (a `where omp`
  producer with threads on: plan record only).
- `tests/OptimizeTests.fs` decision cases (one per matrix row) and emission needles in both lanes (the legs inside the
  producer loop; no separate joined pass; gate-off restores it); `cell-epilogue` raw bits T=1 both lanes;
  `omp-coverage`; `llvm` facts; `opt-diff` on sibling-folds and the bench dir; `interp sibling-folds`; `docs`; `cli`
  plan needles; `corpus-wiring`.
- Directional, in-session, ONE core (keep the box quiet: no harness A/B, no full suite): saxpy at full size
  (n = 2,000,003), serial repro and serial licensed, OLD binary (tip) vs NEW interleaved, >= 11 rounds each, both lanes,
  `BLADE_FP_CONTRACT=off` for the repro arm; report medians and the total minus the input-only twin. The research's
  DROP line: < 0.8 ms gained on serial repro means record the result and stop (do not widen the admission to chase it).

### Not in scope

Generator fusion (A7a); the G7 join team; B1's output leg and `BLADE_CELL_EPILOGUE`'s semantics; the interpreter;
any change to `saxpy.blade` or the twins (the bench source is frozen); ordered-commit for threaded strict legs (A6, dropped).

### Report

The commit sha (ONE squashed commit on your branch, on top of `e4dec0ba`; both co-author lines), files touched per lane,
the five-party matrix as recorded, the directional numbers, footers for: sibling-folds, optimize, cell-epilogue,
omp-coverage, omp-reduce, llvm (facts + sibling-folds), interp sibling-folds, opt-diff sibling-folds, docs, cli,
corpus-wiring. Name what you could not verify.

---

## W2-4-round4-lifetime

_formerly `bench/notes/round3/briefs/W2-4-round4-lifetime.md`_

### W2-4 round 4 -- close the lifetime class: a cleanup EVENT every pass, every renderer and both lanes honour

Context: your round 2 (merged as 3a3925ac on worktree-bench-fortran) replaced the join-inferred release with
`InlineReturnTable`. The third Codex look (`round3/reviews/wave2-codex-reviews.md#codex-diff-w2-4-3`) is DO NOT MERGE: NEW 2 resolved, NEW 1
partial, and FOUR holes of the same class remain. Read it in full first. The class: the release boundary is a side-table fact
about an IR position, so every pass that reshapes the IR after it is recorded, every renderer that bypasses `genBinding`, and
the other lane's scope machinery can each silently invalidate it. The fix must make the boundary a FIRST-CLASS part of the IR.

### The four holes (all must close)

1. The sibling rewrite can delete the recorded boundary: the key is the last SfInl unit chosen BEFORE the joins are rebuilt;
   an unused explicit per-leg join at the end of the callee is accepted as a member, dropped (`dropUnits`), and the entry
   dangles -- no release in main (witness: 031's two extents, the callee ending with an unused Jx join and returning the
   earlier scalar s).
2. CSE runs AFTER the table is recorded (Lowering ~3136 then ~3172): its available-value map ignores cleanup, so a caller's
   map equal to the callee's producer is merged INTO the callee's pool and its later fold reads the freed pool (witness: the
   same named pure cell kernel for y2 and a caller z, z folded by a cube kernel, serial repro, BLAS off, reuse off, CSE on);
   independently CSE can drop the fresh return let (a duplicate `helper(a)` call) and the key dies.
3. Ordinary function-body return lets never reach the new first dispatch arm: `genFuncBodyScoped` dispatches its own lets
   (CodeGen.fs ~214 / ~232), scalar expressions go through exprToCpp (~749-756), plain computed maps bypass genBinding
   (~329-358) -- a function caller gets no release (witness: the existing function-body test with the return `s + 1.0`, plus
   a caller BL8013 after the call: the as-written program had released y2, the inline never does).
4. The LLVM lane DOES free the uninlined callee's pools: a non-capturing scalar call is outlined, its body opens a pool
   scope, B1 allocates through allocPool, and the scalar return pops the scope with emitFrees (EmitLlvm ~7605 / ~4721 /
   ~7649 / ~1352). The inline moves the allocation into main where no scope records it and the lane never reads the table:
   019 / saxpy's y2 is retained to exit. The "frees no pool in either form" claim and the LLVM lifetime-matched attribution
   are WITHDRAWN until implemented; a no-free assertion would pin the wrong behaviour.

### Required design: an IR-level cleanup event

- A new IR statement node (name it `IRReleasePools` or similar: the inlined call's identity + the ordered pool bindings it
  owns), INSERTED by the sibling pass after join reconstruction and after the scalar return has been evaluated into its
  final let -- at the position the as-written call returned. Not a table: a node in the let chain.
- Pass discipline: NO pass removes or moves it. CSE treats it as a barrier for the pools it names (no value equivalence
  may cross it: a later expression equal to a callee computation is NOT merged into the callee's pool; the fresh return let
  is kept); let-hoisting, freeze-idiom, slice-write fusion, kernel-invariant hoisting, pool reuse and the join rewrite
  must not move a read of a named pool past it, nor drop it. Scratch pool reuse must not join a callee pool with a caller
  pool across the event (retain lifetime regions in reuse planning, or exclude donors / reusers across the boundary and
  say so). Add an IR validation rule (`IRValidate`): no reference to a named pool after its release node; a release node
  names only pools bound inside the inline it closes.
- Every renderer honours it: the C++ dispatch (`genBinding`), `genFuncBodyScoped`, expression / loop-body let renderers;
  the LLVM lane renders it as the pool frees plus removal from the enclosing scope's tracking (so the scope's own pop does
  not free twice); the interpreter executes it as a no-op (values unaffected) but must accept the node (`test interp`).
- Fail closed: where a lane or renderer cannot honour the event (document which), the planner DECLINES the kept inline for
  that program (the call stays a call; no legs; recorded `declined -- lifetime event not honoured by <where>`), never a
  silent retention.
- First-failure: the release sits after the scalar return's evaluation and before the next caller statement, so a caller
  panic after the call sees the pools released exactly as the as-written program did; a panic inside the callee leaks on
  both (equal).

### Witnesses (corpus + Optimize / CellEpilogue / LLVM tests, both lanes, gate on vs off, ASan where executable)

The four above (unused final join + earlier atom return; the CSE-merged caller map with a cube fold, CSE on; the function
caller with a non-atom return and a caller BL8013 after the call, release before the failing binding; LLVM 019 / 031
alloc / free relationships and ORDER in main and in a function caller), plus: scratch reuse donor / reuser inside the callee
(no lifetime change vs gate off, or the documented exclusion); the omp-test-mode arm asserting the ACTUAL decline (no fused
route), not just safe releases; the IR validation rule firing on a hand-built violation. Keep 030 / 031.

### Records

G69: the event design, the four closed holes, what is declined where; CLAUDE.md row and docs/features.md: "released once
per inlined call at an IR-level return boundary honoured by every pass and both lanes" ONLY if that is what landed; the
LLVM lifetime-matched saxpy attribution stays withdrawn until the orchestrator re-measures the saxpy cells after this round.

### Scope / timing

ONE commit on top of the integration tip at launch (the orchestrator names it; it will be after the wave-2 A/B). No full
suite; blocks: sibling-folds, cell-epilogue, optimize, omp-coverage, omp-reduce, cli, llvm facts, llvm sibling-folds,
interp sibling-folds, opt-diff sibling-folds, docs, corpus-wiring, plus orbit-scatter, sql, index-types once. A fourth Codex
look follows; nothing merges before it passes.

---

## W2-5-N10-workload

_formerly `bench/notes/round3/briefs/W2-5-N10-workload.md`_

### W2-5 / C -- workload N10 `groupls` (grouped nonlinear least-squares local models), with corrected N02 as the fallback

Read `wave1-builder-briefs.md#builder-preamble` first (reset to the integration tip it names). Brief: `research/C-workloads.md` section N10 (source
`scratchpad/c-work/n10.blade`) and N02 (`n02.blade`). Review: `reviews/codex-review-C.md` -- N10 verdict ADD AFTER FIXES and
finding 9 (forced r / jt tapes vs "compiler temporaries"), 8 (storage), 10 (regimes / power-of-two component axes / outer omp),
11 (inputonly rule); N02 verdict ADD AFTER FIXES and finding 2 (the strict AD reference must be an explicit ordered update list,
repeated-offset −2 included). W1-5's two kernels (`bench/kernels/twofir`, `tensorop`, commit 6459275b) are the templates for
kernel.json / spec.md / twins / oracles / pins / `run.py check`; the harness procedure is `bench/kernels/README.md` and
`bench/harness/README.md`; the protocol is `bench/README.md`.

### Gate first: does the per-group packed H emit?

N10's shape returns a packed symmetric H per group from a row map (RESULTS 15.4-adjacent; gap G64 is the whole-array kernel
over a compound, a different shape). Run `blade emit` and `blade run` at smoke size on the corrected N10 source in BOTH lanes
first. If it refuses or BL9002s: record the exact diagnostic as a gap row (next free number, G67) with the probe, and switch to
the N02 fallback below; do not spend the item on a compiler fix.

### N10 as a kernel (if it emits)

Outputs: cost, gradient and the packed H per group ONLY (the catalog asks for no per-sample Jacobian); spell the residual and
derivative producers with the highest deferred / shared-summary idiom (no forced `r` / `jt` tapes -- if a tape is needed for
the shared summaries, it must be a compiler temporary, otherwise keep a SEPARATE "materialized control" variant); +0 seeds kept
exactly as the catalog; the logical-H checksum weighting (off-diagonal cells twice through their mirrors) matched by the expert;
equal-sized groups and hand-derived derivatives DECLARED as the specialization (ragged / AD variants are later); non-power-of-two
sizes everywhere (P = 4 components is power-of-two: use 5, or record an explicit exception); legal `where omp` on the
independent maps and the checksums (no captured writes); inputonly twin (input generation share > 5%). Natural = per-group
plain loops forming J explicitly; expert = fused per-group accumulation of cost / gradient / H with the same chains and no
Jacobian storage. Oracle ordered (python, no einsum). `run.py check groupls --regimes all` clean; validate_B ALL OK.

### N02 fallback (if N10 does not emit): `haloadjoint`

1-D weighted nonlinear halo primal + reverse action: the strict derivative reference is an EXPLICIT ORDERED UPDATE LIST
(both −2 occurrences, +0 initial dx, the source multiplication parentheses, skipped nonexistent windows, stable order within
an offset -- derived from the emitted AD's actual sweep order and written in spec.md), both Fortran twins implement exactly
that list; y stored (n−4 interior cells) and dx stored (n cells, zero boundary); loss / dw accounting disclosed; inputonly twin.

### Report

Which workload landed, the gate's outcome, checksums / sizes / regimes, the forms `blade plan` reports, `run.py check`
footers, validate_B, `git diff --check`; no compiler change; no full-size timing.

---

## W2-6-A4-pool-retain

_formerly `bench/notes/round3/briefs/W2-6-A4-pool-retain.md`_

### W2-6 -- A4: the pool-retention policy as an OPT-IN arm (`BLADE_POOL_RETAIN`), measured beside the default

Read `wave1-builder-briefs.md#builder-preamble` first (its gap-register range is stale: the register reaches G68/G69 on the integration
branch; this item does not open a new row -- it updates **G52**). Your worktree is cut from the integration tip named in
your task. The user's decision (2026-10-21): "build the retention policy as an opt-in arm (BLADE_POOL_RETAIN), measured
beside the default; no silent default change." Research: `round3/research/A-census.md` Part 2 rows 4-5 (recur mt8), the
A4 rows of sections 5-7 (`**A4 (retention).**` ~207-210; the hook row ~251; the DROP row ~266); attack review
`round3/reviews/codex-review-A.md` finding 6 (its corrections are part of this contract, below) and its verdict line
"A4 -- MEASURE FIRST: separate retention-policy arm ... no silent default change". Wave-3 record: gap row G52
(`bench/notes/gap-register.md`; the probe arm `kMaxBlock` 64 MB / `kBudget` 96 MB measured recur T=8 program end
11.5-11.8 -> 7.9-8.2 ms, stencil T=8 9.7-10.2 -> 9.3-9.4), `bench/notes/wave3/recur-mt8-attribution.md` section 3,
`stencil-mt8-attribution.md`, `bench/README.md` item 6 (the protocol asymmetry: the Fortran experts deallocate after
their stop line; Blade frees a kernel's large pool inside its timed region).

### What exists

C++ lane: `src/cpp/nested_array_utilities.hpp` `blade_pool` -- a per-thread (thread_local) block cache with
`kMaxBlock = 4 MB` / `kBudget = 8 MB` constexpr (~104-115; `kMaxBlock <= kBudget`); a released block above kMaxBlock
goes straight to `delete[]` (so recur's 48 MB trajectory and stencil's 24 MB `lap` are freed inside the timer);
retention lasts until reuse, eviction (oldest slot first), flush or THREAD EXIT -- not unconditionally until process
exit; `BLADE_POOL_CACHE_OFF` (memcheck / ASan builds) disables the cache; `BLADE_POOL_CACHE_STATS=1` (test builds)
counts fresh / reused / retained / evicted. LLVM lane: `src/cpp/blade_llvm_shim.c` ~159-200 is the twin allocator
(64-byte aligned pools with a header; "freed pools stay COMMITTED here and go back to the next request of the SAME
rounded byte size"; off under BLADE_POOL_CACHE_OFF) -- FIND its size policy (does it cap retained pools, per thread or
process-wide?) and state it in your report; the arm must be applied and recorded in BOTH lane runtimes (review A/6).
Build flags: `Build.fs optFlags` ~107 / `llvmOptFlags` ~140; the run record carries the FP policy and library routes
as `-DBLADE_RR_*` build defines (`src/cpp/blade_run_record.hpp`). The exe cache keys on the EMITTED TEXT and the CPU --
NOT on arbitrary compiler flags: verify this (`Build.fs` / the cache key) before choosing how to pass the policy.

### The arm

`BLADE_POOL_RETAIN`: unset / `0` / `off` = the default policy (4 MB / 8 MB, unchanged, byte-for-byte the current
emission apart from the policy line below); `1` / `on` = the measured probe's retention (kMaxBlock 64 MB, kBudget
96 MB per thread); `<maxMB>:<budgetMB>` = an explicit pair for A/B sweeps (validated: max <= budget, both positive,
a malformed value = the default with a warning line). A toolchain key of the same name (`blade setup` persists an env
value; the env wins). Delivery: the SAFE route is to put the policy into the emitted text (a `#define
BLADE_POOL_MAX_BLOCK ...` / `BLADE_POOL_BUDGET ...` pair ahead of the runtime include in the generated .cpp, with the
header's constexprs reading the defines when present and keeping today's values otherwise), so the exe cache keys on it
for free; for the LLVM lane the shim's compile must carry the same defines AND they must enter the shim identity hash
(as blade_member.h's deployment did) -- if you pass flags instead, show that the cache key includes them. Record the
policy in the run record (`-DBLADE_RR_POOL_RETAIN=...` or the emitted define) so the harness knows which arm ran, and
in `blade plan`'s build line (the line that names the compiler flags / FP policy: add `pool retention: default (4 MB /
8 MB per thread)` or `BLADE_POOL_RETAIN=1 (64 MB / 96 MB per thread)`). Register in `CliSelfTests.fs suiteClearedKnobs`.
No value, schedule or store changes: every test's output is identical under either policy; `memcheck` builds keep the
cache OFF under either.

Honesty (review A/6, carry these into the record and the docs): retention relocates destruction out of the timer, it
is not less total application work; the budget is PER THREAD, so T threads may retain T x budget; retention lasts until
reuse / eviction / thread exit, not until process exit; the harness's peak working set (`run.py` reads
PeakWorkingSetSize after exit) can RISE under the arm without any page reuse (retaining A while B allocates), and can
also stay unchanged (keeping the last allocation alive longer need not change an attained peak) -- the A/B reports
kernel time, wall_s AND peak_working_set per arm, never the time alone.

### Tests

- The pool-cache block (find it: the cases that set BLADE_POOL_CACHE_STATS / read the `[blade-pool]` line): under the
  arm a 48 MB release is `retained` (not freed) and `evicted` at thread exit; under the default it is freed inside; a
  budget smaller than the block keeps today's path; the `<max>:<budget>` parser (valid pair, max > budget refused,
  garbage = default + warning); the policy line in `blade plan`; the run-record field; the emitted define present only
  when the knob is set (default emission unchanged byte-for-byte vs the tip apart from nothing -- assert the default
  build's .cpp is identical to the tip's for one program); LLVM: the shim built with the defines, the identity hash
  changing with the policy, the same retained/evicted observation if its allocator exposes counters (else say so and
  record the LLVM policy as "retains by rounded size, uncapped / capped at N" as you find it).
- `cli` plan needles; `docs`; `corpus-wiring`; `blade test memcheck` smoke if it exists (cache off under both).
- Directional (small load, allowed once): `blade run` of `bench/kernels/recur/recur.blade` at full size, T=8 (mt8
  licensed), default vs `BLADE_POOL_RETAIN=1`, 5 interleaved pairs, whole-program `completed in` medians -- expected
  ~11.5 -> ~8 ms; and stencil once. The wave-end harness A/B (the orchestrator's, mirror `r3w2retain`) is the measured
  record: recur / stencil mt8 licensed + repro with peak_working_set per arm.

### Records / docs

Gap row G52 (BUILT as an opt-in arm, the user's decision, the directional numbers, the honesty paragraph, "harness A/B
pending"); CLAUDE.md env-table row (`BLADE_POOL_RETAIN`); `bench/README.md` item 6: one paragraph naming the arm as a
RECORDED runtime-policy arm beside the default (never the default); `docs/features.md` if it lists the pool cache.

### Not in scope

Changing the default; A5 (lagged in-team row folds); the harness A/B; any kernel source or twin.

### Report

The commit sha (ONE squashed commit on your branch on top of the tip; both co-author lines), files per lane, the LLVM
allocator's policy as found, how the policy reaches the exe cache key and the run record, the directional numbers,
footers for every block run (the pool block, cli, docs, corpus-wiring, llvm facts / shim, memcheck if run), what you
could not verify.
