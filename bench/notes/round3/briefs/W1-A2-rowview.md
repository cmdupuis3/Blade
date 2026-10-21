# W1-2 / A2 -- the row-view inner fold in the strict threaded forms (gap G51), and the licensed helper arm

Read `BUILDER-PREAMBLE.md` first. Brief: `research/A-census.md` item A2 (sections "A2" and its implementation design;
emitted evidence in `scratchpad/round3/A-emit/stencil_lic.cpp` and `stencil_repro.cpp`). Review: `reviews/codex-review-A.md`
findings 4 (BLOCKER) and 5 (HIGH) -- close them exactly.

## The defect

stencil's input checksum is `reduce(method_for(range<I>) <@> lambda(i) -> reduce(u(i), (+)), lambda(a, b) where omp -> a + b, 0.0)`-shaped:
an outer `where omp` fold over rows whose inner fold runs over a ROW VIEW `u(i)` of a stored dense rank-2 array. Today:
- REPRO + threads: the G35 row-slot form (`BLADE_FOLD_ROW_SLOTS`) and Path B decline it -- `isCompleteFold`
  (CodeGenBinding.fs ~85-88) accepts only `IRReduceCompute`, and `IRTotality` (~173-195) has no row-view `IRReduce` arm --
  so the checksum runs serially (2.7-2.9 ms of stencil's mt8 repro time; wave-3 gap G51, formerly G48 on its branch).
- LICENSED: the emitted C++ puts `BLADE_OMP_SIMD_REDUCTION` on the ROW loop around an 8-lane scalar chain (the IIFE
  re-spells the row view per iteration); the vector helper `blade_fold::sum<double,8>` (`src/cpp/blade_fold_lanes.hpp`
  ~345-349; `model::sum` at ~141 is its scalar reference) is not used; the phase takes 1.5-1.9 ms where a plain read of the
  same 24 MB takes 0.7 ms.

## What to build

A. REPRO admission (the gap fix). Admit the row-view inner fold in the G35 row-slot form AND Path B when ALL of:
   base array stored, dominating, non-virtual, dense rank 2, rows contiguous, correct index identity (the view's axis is
   the base's second axis); the inner fold is complete and total (extend `isCompleteFold` and `IRTotality` with the exact
   row-view `IRReduce` case; return the real row work count for `rowslots_min`); BOTH seeds (inner and outer) preserved
   exactly, evaluated once in their original scope and captured; the unseeded inner fold keeps its head-peel semantics and
   its empty-row BL8003 at the original site; no cross-binding motion (say so in the record). Five-party matrix
   `rowViewFoldAdmission` per review A/5: producer (the stored base; no producer evaluation moves), consumer (the whole row
   callable incl. leading `IRLet`s and the inner fold callable through `scheduleMayWriteIn` + `IRTotality.auditCallable`),
   outer kernel (same audit; its own explicit threading request; ascending outer combination kept), seeds (both), intervening
   (every statement whose relative order changes; reject writes through aliases to the base or captured state).
   Result bits: identical to the serial as-written chain under REPRO + CONTRACT=off (each row's chain from its own seed in
   ascending j, the outer chain from its seed in ascending i).
B. LICENSED helper arm (separate, gated, measured -- NOT identity-preserving, so never on a strict chain): when the inner
   fold is reorder-licensed (builtin `+` / `where omp`, no `where repro`, float type, `BLADE_FOLD_LANES` on), lower the
   inner row fold to `blade_fold::sum<T,8>(row, n)` COMBINED WITH THE ORIGINAL SEED exactly once (review A/4: `sum` returns
   the sum of the row; `reduce(u(i), op, s)` is `s op row...`; the literal rewrite that dropped `s` was the BLOCKER), and
   drop the outer `omp simd` around it (an explicit licensed-only choice, recorded). Knob: it rides `BLADE_FOLD_LANES`
   (already in suiteClearedKnobs) -- the plan record must name it; `blade test fold-lanes` gets the needle.

## Tests

- corpus `tests/corpus/omp-reduce/` or `loops/` (next free numbers): the stencil checksum shape with (i) a nonzero inner
  seed, (ii) an unseeded inner fold, (iii) an empty row (BL8003 at the original site), (iv) a singleton row, (v) signed
  zeros, (vi) a non-commutative inner kernel (`3*a + b`) -- all pinned bitwise against the nested-range spelling under
  REPRO; (vii) a writing inner kernel / a view alias writer -> declined with the census line.
- `tests/OmpTests.fs`: row-slot case asserting the row-view fold is TAKEN (`fold form: threaded complete rows` with the
  row-view note) at T=1 and T=8 with identical bits; Path B case likewise; the licensed arm: needle `blade_fold::sum<` and
  NO `BLADE_OMP_SIMD_REDUCTION` on the row loop, values within the licensed tolerance vs the old form, seeds honoured.
- `blade plan bench/kernels/stencil/stencil.blade` under `BLADE_FP_REPRO=1`: the checksum's record says applied.

## Blocks to run and report

omp-reduce, omp-pragma, omp-coverage, fold-lanes, cell-epilogue, optimize, loops, index-types, cli, corpus-wiring,
interp loops / omp-reduce (via `blade test interp <dir>`), opt-diff loops. One `blade run` A/B of stencil at full size,
REPRO, T=8, old vs new binary interleaved (5 rounds each, medians) -- directional only; the harness A/B is the orchestrator's.

Update the G51 row (FIXED, with the matrix and the pins) and `bench/RESULTS.md` only if you add a line to section 16's
gap list (do not renumber anything).
