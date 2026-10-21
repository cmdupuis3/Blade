# stencil mt8-licensed: where the ~1 ms goes -- MEASURED 2026-10-20 (wave 3, item 2)

> CAVEAT (found at 20:11): a stray single-core `find` process of the orchestrating session ran
> from 16:50 to 20:11, beside every probe below and the first harness A/B run. The interleaved
> comparisons stand directionally; absolute figures may be inflated. Key probes re-run on the
> quiet box afterwards (`reprobe.ps1`; the re-measure section at the end); the A/B re-run in full.

Same instrument as `recur-mt8-attribution.md`: phase timers (stderr, cumulative ms from the
program's own start) spliced into the EMITTED C++ (`probe/stencil/variants_stencil.py`),
compiled with Blade's flags; the gfortran expert built with the harness's mt8 licensed flags,
and a copy with `bench_start()` moved below the input fill. Quiet box, single process,
`OMP_NUM_THREADS` as stated; 3-7 runs; directional, not the harness protocol. The box ran
~10% slower than during the wave-1 A/B (expert full mt8 8.55 ms here vs 7.75 there).

## Blade C++ phases (the wave-2 source; `stencil_phases`), licensed, T=8 (3 runs)

| phase | cumulative ms | share |
|---|---|---|
| `inputs()` (24 MB generated on a team, first touch) | 4.4-4.8 | |
| input checksum (`reduce(reduce(u, (+)), (+))`, SERIAL in that source) | +1.5-1.9 | the 24 MB re-read |
| `lap` allocate | +0.06 | |
| producer team (lap fill + per-row self-dot, hot-row) | +3.2-3.4 | |
| combine (second team over 1499 row sums) | +0.1 | |
| **`lap` + row-sum pool deallocate** | **+0.9-1.0** | inside the timer |
| program end | 10.7-10.9 | |

Repro (`stencil_repro_phases`, contract off): the serial input checksum is 2.7-2.9 ms (one
ascending chain per row, then the outer chain); the rest within 0.2 ms of the licensed run.

## The expert (gfortran, `-DBENCH_LICENSED -fopenmp`)

| | T=1 | T=8 |
|---|---|---|
| full program | 15.2 | 8.55 |
| region only (timer after the input fill: lap + its checksum + the input sum) | 6.7 | 4.25 |

Blade's equivalent region (program end minus `inputs()`) is 6.1-6.6 ms at T=8: a 2.0 ms gap
against the expert's 4.25, of which **1.0 ms is the `lap` teardown inside the timed region**
(the expert deallocates after its stop line, bench/README item 6) and the other ~1 ms sits in
the bandwidth-bound read phases (input checksum + producer), not split further.

## The input checksum licence (the mt8 read-back licence the Fortran arms have, RESULTS 12)

Three spellings thread it in a licensed build (Path B over the 1501 rows): `where omp(i: 1)` on a
row map, `where omp` on the outer fold kernel, or on the outer kernel over the partial fold.
`stencil.blade` now carries `where omp` on the outer fold kernel (the D44 placement). MEASURED,
T=8: the phase stays 1.5-1.9 ms (`stencil_b_phases`: 6.3-6.6 cumulative after inputs 4.4-4.9):
a 24 MB read is DRAM-bound on this box and does not scale with threads (raw row-stream
bandwidth 10.4 GB/s at T=1, 6.9 at T=8). The licence is kept as the fair spelling (every
expert threads its read-back); it buys nothing measurable here. Under repro the G35 row-slot
form DECLINES this spelling (`row-slot form: not one complete fold per outer index (the row
value is not a fold)`): v1 recognises a nested `reduce(method_for(range<X>) <@> lambda(j) ->
u(i, j), (+))`, not a row-view fold `reduce(u(i), (+))` -- that spelling (`stencil_d`) threads
under repro (`threaded complete rows (repro): 1501 row folds into indexed slots`). Recorded as
a recognizer gap (gap register G48); the kernel keeps the readable spelling.

## D1 instrument (brief D: build only if an instrument shows > 2% fork/barrier/idle)

`probe/expert/d1cost.cpp`: gram's syrk structure at the bench shape -- 3 K blocks, each ONE
region { `omp for static` over 67 column panels (barrier); `omp for dynamic,1` over 67 row
groups (barrier); join } with trivial bodies, T=8: **236-257 us per call** (first run 1045 us
= thread start). The persistent-team shape (1 region, a pack barrier per block, dealt ownership,
no per-block join): 299-376 us -- no cheaper on this libgomp. Against a ~14 ms mt8 gram the
synchronization is ~1.8%, below the bar, and the proposed shape does not reduce it: **D1
DROPPED on the instrument** (no code).

## B2l (brief B: "an optional arm only; exists to measure, not to ship")

Not built. Its targets after E-G5: recur's licensed row reload is gone under mt8 (each worker
folds its own L1-hot chunk right after writing it -- the in-team partial IS the B2l benefit)
and bounded at ~0.35 ms of ~10 ms serially (brief B); stencil's at ~0.02 ms per thread. The
harness cannot resolve a 2-3% serial effect on this box (CI half-widths ~3-4%). `lanes` stays
RESERVED with its recorded decline; a follow-up card if a serial-licensed loss ever needs it.

## Quiet-box re-measure (21:31, after the stray process; `reprobe.ps1`, `secondlook.ps1` run 3)

- Expert at T=8: full program 8.24 ms, region only 5.38 ms (the loaded-box figures above were
  8.55 / 4.25 -- the region-only figure was LOW then, not high: treat the earlier 2.0 ms gap as
  unreliable).
- Blade licensed-source phases at T=8 (3 runs, cumulative ms): inputs 3.1-3.5, input checksum
  +1.8-2.1, producer +3.1-3.5, combine +0.1, dealloc +0.8-1.0, end 9.2-9.8. Blade's region
  (end minus inputs) 6.0-6.4 ms against the expert's 5.38: ~0.8 ms, of which the teardown is
  0.8-1.0 ms (G49; the retention arm takes it to +0.04 ms, end 9.3-9.4).
- **The DRAM diagnosis above is WITHDRAWN for reads.** A READ probe (`rd.cpp`) shows a 24 MB
  stream read scaling 22.2 -> 36.5 -> 53.0 GB/s at T = 1 / 2 / 4 (33.0 at 8); the earlier figure
  was a WRITE probe (writes really are flat, ~10 GB/s at every team size, re-measured quiet).
  So the input checksum's 1.8-2.1 ms for 24 MB (12-13 GB/s) is SLOWER than a streaming read of
  the same bytes (0.73 ms at T=8): a real ~1 ms item in the emitted per-row form (Path B over
  the rows with the lane-unrolled `__wrap` chain per row), not in the box. Follow-up.
- **The read-back licence, interleaved directly** (the two emissions from the same Blade build,
  `old` = the wave-2 source, `new` = `where omp` on the outer kernel; 21 interleaved runs, T=8):
  licensed 8.08 ms (IQR 7.93-8.64) old vs 8.38 (7.97-8.87) new; repro (contract off, 15 runs)
  9.58 vs 9.78 with identical checksums. The licence buys nothing measurable and may cost
  ~0.3 ms licensed; it stays as the fair spelling pending the checksum follow-up (which is
  where its benefit would have to come from), and RESULTS 16.3 reports the cell as is.
