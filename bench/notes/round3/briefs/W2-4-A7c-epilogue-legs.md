# W2-4 -- A7c producer-input legs: saxpy's input sums ride the y2 producer pass

Read `BUILDER-PREAMBLE.md` first (its gap-register range is stale: the register now reaches G68; this item's row is
**G69**). Integration tip at launch: `e4dec0ba` on `worktree-bench-fortran` (your worktree is cut from it). Research:
`round3/research/A-census.md` Part 2 rows 9-10 and section 5 "A7c", section 6 row A7c, section 7 row A7c; attack review
`round3/reviews/codex-review-A.md` finding 11 (A7c: BUILD AFTER FIXES -- totality, dominating captures / seeds, operand /
index equality, alias checks, a complete intervening audit, per-leg licence, results bound at their original points).

## The cell and the shape

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

## The rewrite

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

## Admission -- `producerEpilogueLegAdmission` (new, in `Optimize.fs` beside `planHotRowFolds`' rank-1 arm /
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

## Lanes

Both (hard constraint 3): C++ (`CodeGenLoopNest.tryGenFlatEpilogueNest` ~2584 and the cell-epilogue sites in
`CodeGenBinding.fs` ~374-424, the B1 lane-state machinery) and LLVM (`EmitLlvm.tryBindFlatEpilogueProducer` ~4541, called
from ~4054: the flat-epilogue twin) -- the serial saxpy reference lane is blade-llvm,
so a C++-only change would not move the headline cell. Interpreter unchanged (values identical). Identity: REPRO
(`BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`) bitwise against gate-off in both lanes (each leg is the same ascending chain
from the same seed; only the interleaving with the stores changes); a licensed leg bitwise too where its lane tree is
the standalone fold's (say so), else within 1e-12 and recorded as a licensed association change.

## Knob, record, docs

`BLADE_EPILOGUE_LEGS` unset/1 = on, 0/off = off, registered in `optimizerGates` (opt-diff compares off vs on). Plan
rule `epilogue-legs v1`: `applied -- legs x-sum, y-sum ride y2's producer pass (strict chains, ascending, own seeds)` /
`declined -- <why>` with the matrix. CLAUDE.md env-table row. `docs/features.md` sibling-folds / cell-epilogue entry.
Gap row **G69** (after G68; W2-1 and W2-3 take G65 / G66 on their branches -- do not reuse those numbers): mechanism,
the matrix, the measured numbers, what is NOT built (threaded strict legs = G35-class; generator fusion = A7a).

## Tests

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

## Not in scope

Generator fusion (A7a); the G7 join team; B1's output leg and `BLADE_CELL_EPILOGUE`'s semantics; the interpreter;
any change to `saxpy.blade` or the twins (the bench source is frozen); ordered-commit for threaded strict legs (A6, dropped).

## Report

The commit sha (ONE squashed commit on your branch, on top of `e4dec0ba`; both co-author lines), files touched per lane,
the five-party matrix as recorded, the directional numbers, footers for: sibling-folds, optimize, cell-epilogue,
omp-coverage, omp-reduce, llvm (facts + sibling-folds), interp sibling-folds, opt-diff sibling-folds, docs, cli,
corpus-wiring. Name what you could not verify.
