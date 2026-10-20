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

### 15.2 What landed (wave 2, integrated at b161cf10; gates ___)

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| antisym row groups + simplex edge kloops (W2-D) | syrk's row-group pattern in `dantisym_tri` / `blade_dantisym_tri_f64` (visit order only; bytes identical for every G); live-accumulator edge variants with the corrected per-half formula (dead vector halves 7.3% at p=171) | `BLADE_TILE_TUNE anti_g` (default 1; `0` = cache-budget auto), `tri_edge` (default 1) | row-tile 24, syrk-tile, llvm tiles with a positive route assertion; single-chain edge variants run two chains to keep per-step contraction |
| G7 thread-level `<&!>` join (W2-E) | generalized Path B: one team, per-leg identity-initialised private accumulators, one simd reduction per chunk, fixed ascending combine; fires only on an EXPLICIT `where omp` request + per-leg licence (fable ruling Q2); `onepass.blade` gains the request | `BLADE_JOIN_THREADS` | omp-reduce g7 cases incl. observed teams and OMP_THREAD_LIMIT; onepass has an mt8 arm; dot/fusedchain join+thread in the default build |
| Ordered orbit scatter (W2-A) | strict consumer of a stored SymIdx pool: one pass, every original chain fed in source order (G-row-group vectorizable form); rank 3 as slices + same-Z third event + retained H; both lanes | `BLADE_ORBIT_SCATTER`, `scatter_g`=4, `scatter_b`=64, `scatter_vrow` | `blade test orbit-scatter` (52 cases x 8 builds, walk-forced twins, mutation-checked); prototype MEASURED S/W 0.13 rank 2, 0.07-0.10 rank 3 |
| Pre-existing bug fixes (W2-F) | G41 a fold whose kernel or map body WRITES is never reorder-licensed (the serial K lanes AND Path B were wrong: five mt8 totals for one program); storage-provenance write analysis (a write is local only when its storage is proved fresh; rebinding joins the initializer with every later assignment; deferred wrappers chased; an unresolved callee counts as a write); every LLVM fold entry consults `FoldOperandWrites`; G42 inline projections of a flat `<&!>` join tuple (and its aliases) use flat leaf positions; G43 a join leg that writes is refused BL4005 (an acceptance-policy change; `joinEffectAudit` gates the serial simd form and the G7 team alike); G45 opened (scope-dependent evaluation count of a named writing map) | -- | loops/492-498, tuples/031-033, mutability-errors/014-024, omp-pragma / omp-reduce g41 runtime cases (T=1 and five T=8 runs), llvm goldens emission pins |

### 15.3 Measured: wave-1 A/B (OLD master 4136f0b5 vs NEW integration 6b3e5293)

Harness protocol (`bench/notes/round2/w1-ab.md`; bundles under `C:\bt_r2ab\{old,new}\bench\results\
2026-10-20-LAPTOP-350H6LDB\`): 20 sessions, 05:13-08:15, OLD and NEW interleaved per kernel, 21
rounds, full size, quiet box (3-5% load, AC, Silent, uncapped), harness drift controls within
0.95-1.04, zero failures/aborts/excursions. Ratio = expert median / Blade reference median
(> 1 Blade faster; ref blade-llvm serial, blade-cpp mt8); "ind" = the harness suppressed the
block (indicative). NEW/OLD = Blade NEW median / OLD median (< 1 NEW faster), unpaired
bootstrap CI.

| cell | item | OLD gf / ifx | NEW gf / ifx | RESULTS 14.8 | NEW/OLD (ref lane) | block |
|---|---|---|---|---|---|---|
| comoment3 mt8 repro | G35 | 0.497 / 0.436 | **1.080 / 0.950** | 0.478 / 0.427 | 0.458 [0.443, 0.472] | live |
| pairkernel mt8 repro | G35 | 0.383 / 0.372 | **0.687 / 0.678** | 0.370 / 0.359 | 0.546 [0.534, 0.563] | live (OLD ind) |
| pairkernel mt8 licensed | B3 | 0.797 / 0.791 | **1.020 / 0.967** | 0.800 / 0.798 | 0.801 [0.773, 0.843] | ind / ind |
| pairkernel serial licensed | B3 | 1.119 / 1.041 | 1.164 / 1.086 | 1.122 / 1.047 | 0.960 | live |
| maskagg serial licensed | B4 | 0.856 / 0.619 | **1.062 / 0.788** | 0.871 / 0.628 | 0.795 [0.744, 0.813] | live |
| maskagg serial repro | B4 | 0.893 / 0.688 | **1.062 / 0.813** | 0.839 / 0.676 | 0.843 | live |
| recur serial repro | B2r | 0.993 / 1.016 | **1.337 / 1.369** | 0.985 / 1.017 | 0.745 [0.721, 0.762] | live |
| stencil serial repro | B2r | 1.229 / 0.986 | **1.416 / 1.134** | 1.224 / 1.001 | 0.862 | live |
| stencil mt8 repro | B2r | 0.792 / 0.855 | 0.822 / 0.875 | 0.815 / 0.891 | 0.966 [0.925, 1.033] | ind / ind |
| saxpy serial licensed | B1 | 1.255 / 0.925 | 1.377 / 0.972 | 1.244 / 0.903 | 0.942 | live |
| saxpy mt8 licensed | B1 | 0.824 / 0.840 | 0.938 / 0.970 | 0.821 / 0.858 | 0.900 [0.836, 0.954] | ind / ind |
| saxpy serial repro | B1 | 1.062 / 0.667 | **1.375 / 0.878** | 1.082 / 0.686 | 0.785 | live |
| saxpy mt8 repro | (control) | 0.732 / 0.763 | 0.811 / 0.871 | 0.765 / 0.748 | 0.879 | live (OLD ind) |
| dot serial licensed | C | 1.455 / 0.971 | 1.642 / **1.090** | 1.416 / 0.967 | 0.894 | live |
| dot serial repro | C | 1.213 / 0.739 | **1.696 / 1.012** | 1.219 / 0.755 | 0.715 [0.706, 0.728] | live |
| dot serial fastmath | C | 1.395 / 0.671 | 1.631 / 0.782 | 1.369 / 0.712 | 0.853 | live |
| dot mt8 repro | C | 0.814 / 0.859 | **1.163 / 1.182** | 0.832 / 0.842 | 0.707 | live (OLD ind) |
| onepass serial licensed | C | 1.449 / 1.005 | 1.635 / 1.119 | 1.525 / 1.031 | 0.894 | live |
| onepass serial repro | C | 1.348 / 0.803 | **1.714 / 1.044** | 1.354 / 0.820 | 0.787 | live |
| onepass serial fastmath | C | 1.477 / 0.745 | 1.664 / 0.820 | 1.502 / 0.723 | 0.887 | live |
| fusedchain serial licensed | C | 1.561 / 1.032 | 1.709 / 1.110 | 1.567 / 1.023 | 0.943 | live (OLD ind) |
| fusedchain serial repro | C | 1.393 / 0.823 | **1.852 / 1.093** | 1.381 / 0.839 | 0.754 | live |
| fusedchain serial fastmath | C | 1.479 / 0.808 | 1.688 / 0.941 | 1.496 / 0.788 | 0.857 | ind / ind |
| fusedchain mt8 repro | C | 0.731 / 0.827 | 1.017 / 1.128 | 0.753 / 0.825 | 0.727 | ind / ind |
| gram mt8 licensed | (control) | 1.284 / 1.217 | 1.267 / 1.218 | 1.393 / 1.332 | 1.016 [0.982, 1.047] | live |
| comoment3 serial licensed | (control) | 1.300 / 1.305 | 1.303 / 1.323 | 1.325 / 1.338 | 0.998 | live |
| recur serial licensed | (control) | 1.116 / 1.166 | 1.075 / 1.114 | 1.114 / 1.125 | 1.032 [0.993, 1.053] | live |
| stencil serial licensed | (control) | 1.343 / 1.035 | 1.341 / 1.049 | 1.375 / 1.089 | 0.986 | live (OLD ind) |

No Blade arm is slower than OLD beyond its CI; the Fortran reference arms moved 0.95-1.07
(noise). Item C also rewrites the input-only twins (dot repro twin 0.849, saxpy repro twin
0.889), so "incremental" is not comparable OLD -> NEW; nets are in `w1-ab.md` 2b. Every
mt8-licensed block except gram is indicative on both sides (harness drift flags).

Losses remaining after wave 1: pairkernel serial repro 0.932 / 0.879 and comoment3 serial
repro 0.770 / 0.780 (the ordered scatter's targets, wave 2); pairkernel mt8 repro
0.687 / 0.678; stencil mt8 repro 0.822 / 0.875 and mt8 licensed 0.903 / 0.970 (ind); saxpy
mt8 repro 0.811 / 0.871; ifx fastmath dot 0.782 / onepass 0.820 / fusedchain 0.941 (ind).

### 15.3b Measured: wave-2 A/B

(fill after the wave-2 gates)

### 15.4 Pre-existing defects found by this round (gap register)

G40 licensed signed-zero divergence between lanes; G41-G43 (fixed, W2-F); G44 scatter
timing provenance; G45 scope-dependent evaluation count (OPEN); unforced map over a compact
pool fails BL9002 in the C++ lane (OPEN, card); W1-D's "68 vs 20" plain-fold report
(UNVERIFIED, card); join emitter private +0 accumulators override a user -0.0 seed (G40 note).
The Codex every-fold-entry audit of W2-F (`codex-diff-W2F-3.md`) found four more holes of
the G41 class, all pre-existing on master: the LLVM orbit-class fold ran its canonical-pool
pass over a writing virtual source (3 kernel applications for 4 logical reads), hot-row
scheduling ran the row consumer after each producer row without checking the producer's
writes (12 two-pass vs 9), the flat-epilogue nest and the flat materializer asserted
`BLADE_IVDEP` on a writing producer, and a named partial view hid its deferred producer from
the write analysis -- closed by bd34627b (llvm goldens G41-12/13, optimize hot-row decline, omp-pragma ivdep cases, loops/499, mutability-errors/025). A second audit found four more of the same class (`codex-diff-W2F-4.md`): wave 1's compact row classes never checked the PRODUCER (a consumer reading a capture the `comm` producer writes: 12 two-pass vs 9), hot-row scheduling ahead of a writing OUTER kernel or seed (1 vs 0), producer SIMD / team pragmas on a writing producer the lexical BL4005 check misses (a local row view), and the tuple chase dropping views -- closed as a CLASS by b161cf10: one shared predicate, `Optimize.scheduleMayWrite`, consulted by every moving pass for the producer kernel, the consumer kernel, the outer fold kernel, the seed and the intervening bindings (the per-pass matrix is in the G41 row; loops/500-501, mutability-errors/026-028, llvm goldens G41-19, optimize decision cases). Still open: the probe's writing virtual source is BL9002 in the C++ lane (pre-existing), and `scheduleMayWrite` treats a call into another module as a possible write (a decline, never a wrong answer).

### 15.5 Not done this round (follow-up cards)

E-G5 recur let-rec step threading (wave 3 design in brief E); B2l licensed lanes arm;
stencil mt8-licensed attribution (~1 ms unexplained vs an expert with the same schedule);
the relational whole-region track (membership hoisting, hash equijoin, aggregate-only
grouping); the N01-N10 benchmark workloads; D1 persistent syrk team (instrument first);
scatter v1.1 contraction invariance REJECTED (do not revisit without a new argument).
