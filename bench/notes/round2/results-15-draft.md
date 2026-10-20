## 15. Round 2 (2026-10-19/20) -- the second microkernel round

Plan and record: `bench/notes/round2-microkernel-plan.md`, `bench/notes/round2/`. Baseline
tree master 4136f0b5 (= round-3 tree d3d5722b plus the G37 conversion fix, BLADE_BLAS_PREFER
and harness fixes); baseline gate TOTAL 6800 passed, 0 failed, 1 skipped. Integration branch
`worktree-bench-fortran`.

### 15.1 What landed (wave 1, integrated at 6b3e5293; five gates green)

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| G35 row slots (W1-A) | under `BLADE_FP_REPRO=1` with threads and an `omp` request, each thread folds COMPLETE logical rows into indexed slots; the outer chain runs serially from its seed (no chain split) | `BLADE_FOLD_ROW_SLOTS`, `rowslots_min` (provisional 2^18) | omp-reduce identity T=1/4/7 vs serial; closed admission whitelist incl. the OUTER fold kernel (Codex blocker) |
| Cell epilogues (W1-B) | P1 streaming lane state (same tree as `sq<T,8>`), B1 saxpy rank-1 producer epilogue, B2r register-consume chain interleave for recur/stencil repro | `BLADE_CELL_EPILOGUE` (optimizer gate), `epi_blk`=256, `epi_blk_r`=0 | `blade test cell-epilogue` 56 raw-bit cases (NEW vs gate-off, non-dyadic n=2003, actual teams asserted); gate-off C++ byte-identical to OLD |
| maskagg compaction fusion (W1-C B4) | 6 passes -> 2: count in the mask pass; fused branchless fill+gather with an m+1 slack cell; no `y*y` pool; repro chain fed per 256-cell block; positions always built; `keep` stored | `BLADE_COMPACTION_FUSION` | index-types 515-521; repro raw bits OLD == NEW; licensed 2.8e-16 rel |
| pairkernel compact row classes (W1-C B3) | per-row orbit-class partials written behind each packed row inside the producer's team; combined in row order; no second 25 MB pass; licensed only | `BLADE_COMPACT_ROW_CLASSES` | bits identical at T=1/3/8 (OLD varied with T); symmetry/129-131; LLVM runtime gate at p=2503 within 1e-12 |
| Sibling-fold sharing (W1-D) | `shareSiblingFoldsModule` rewrites sibling scalar folds over one dense axis into the existing `<&!>` join; one-level inliner; unseeded legs seeded with the exact identity (-0.0; formalism 2.4 paragraph, fable ruling) | `BLADE_SIBLING_FOLDS` (optimizer gate) | sibling-folds/001-018; NEW == OLD under REPRO+off both lanes; drop test MEASURED on OLD: hand-joined llvm repro dot 1.22x, onepass 1.27x, licensed flat |

### 15.2 What landed (wave 2, integrated at ___; gates ___)

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| antisym row groups + simplex edge kloops (W2-D) | syrk's row-group pattern in `dantisym_tri` / `blade_dantisym_tri_f64` (visit order only; bytes identical for every G); live-accumulator edge variants with the corrected per-half formula (dead vector halves 7.3% at p=171) | `BLADE_TILE_TUNE anti_g` (default 1; `0` = cache-budget auto), `tri_edge` (default 1) | row-tile 24, syrk-tile, llvm tiles with a positive route assertion; single-chain edge variants run two chains to keep per-step contraction |
| G7 thread-level `<&!>` join (W2-E) | generalized Path B: one team, per-leg identity-initialised private accumulators, one simd reduction per chunk, fixed ascending combine; fires only on an EXPLICIT `where omp` request + per-leg licence (fable ruling Q2); `onepass.blade` gains the request | `BLADE_JOIN_THREADS` | omp-reduce g7 cases incl. observed teams and OMP_THREAD_LIMIT; onepass has an mt8 arm; dot/fusedchain join+thread in the default build |
| Ordered orbit scatter (W2-A) | strict consumer of a stored SymIdx pool: one pass, every original chain fed in source order (G-row-group vectorizable form); rank 3 as slices + same-Z third event + retained H; both lanes | `BLADE_ORBIT_SCATTER`, `scatter_g`=4, `scatter_b`=64, `scatter_vrow` | `blade test orbit-scatter` (52 cases x 8 builds, walk-forced twins, mutation-checked); prototype MEASURED S/W 0.13 rank 2, 0.07-0.10 rank 3 |
| Pre-existing bug fixes (W2-F) | G41 a writing fold kernel / map body is never reorder-licensed (serial K-lane AND Path B were wrong); G42 structural projections of a flat join tuple; G43 writing join legs refused (BL4005, policy change); G45 opened (scope-dependent evaluation count) | -- | loops/486+, tuples/031-033, mutability-errors/014-020 |

### 15.3 Measured (harness A/B, OLD 4136f0b5 vs NEW; cells per the plan)

(fill from `round2/w1-ab.md` and the wave-2 A/B)

### 15.4 Pre-existing defects found by this round (gap register)

G40 licensed signed-zero divergence between lanes; G41-G43 (fixed, W2-F); G44 scatter
timing provenance; G45 scope-dependent evaluation count (OPEN); unforced map over a compact
pool fails BL9002 in the C++ lane (OPEN, card); W1-D's "68 vs 20" plain-fold report
(UNVERIFIED, card); join emitter private +0 accumulators override a user -0.0 seed (G40 note).

### 15.5 Not done this round (follow-up cards)

E-G5 recur let-rec step threading (wave 3 design in brief E); B2l licensed lanes arm;
stencil mt8-licensed attribution (~1 ms unexplained vs an expert with the same schedule);
the relational whole-region track (membership hoisting, hash equijoin, aggregate-only
grouping); the N01-N10 benchmark workloads; D1 persistent syrk team (instrument first);
scatter v1.1 contraction invariance REJECTED (do not revisit without a new argument).
