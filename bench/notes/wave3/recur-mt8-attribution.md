# recur mt8 (E-G5) and the teardown asymmetry -- MEASURED 2026-10-20 (wave 3)

Box: the bench laptop (Ryzen 7 5800HS, 16 MB L3), AC, Silent scheme, quiet (no builds); ucrt64
g++ 16.2 / gfortran 16.2; `OMP_NUM_THREADS` as stated, otherwise the harness's mt8 environment
(unbound; no wait-policy knobs). Single-process timings, 7 runs each, medians (ms); directional,
not the harness protocol. Scratch: `scratchpad/w3/probe/` (the orchestrating session's area).

## 1. The threaded step works and is bitwise where it must be

`recur.blade` with `where omp(v: 1)` on the step's final map, this branch's binary (E-G5 on):
`checksum = 5495637.67534381`, `input_checksum = 20054.0339541091` for the original source and
the new one at `OMP_NUM_THREADS` 1 and 8, licensed and `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`
(the repro values identical to the serial program's, the licensed ones to every printed digit).
`blade plan`: `rec-step-parallel v1 ... applied -- persistent team; licensed row partials per
worker chunk ...`; threads suppressed: `[omp] requested but emitted serial: BLADE_OMP_THREADS=1`
at the time loop; `BLADE_REC_STEP_OMP=0`: `declined -- disabled by BLADE_REC_STEP_OMP` and the
pre-wave-3 emission (`step(...)` called per step, its map a `BLADE_OMP_PARALLEL_FOR_SIMD` team,
the row copied).

## 2. Team entry / barrier cost on this box (why the form is barrier-free)

`forkcost.cpp`, 300 steps x 20011 cells, trivial body: per-step `parallel for` 47-51 us; one
region with `omp for` + `single` per step 90-97 us; explicit ownership + one `barrier` +
`single nowait` running a 20011-cell serial fold 37-46 us. `OMP_WAIT_POLICY=active`,
`GOMP_SPINCOUNT=infinite`, `OMP_PROC_BIND=true`: unchanged. A team entry or a barrier is ~40 us;
300 of them are 12 ms against a 7 ms expert.

## 3. Where the time goes (phase timers in the emitted C++, `recur_omp_phases.cpp`)

| phase (cumulative, ms) | T=1 | T=8 |
|---|---|---|
| inputs() | 0.08 | 0.09 |
| allocate (48 MB, no fill) | 0.15 | 0.15 |
| row 0 + its self-dot | 0.21 | 0.22 |
| the region (300 steps) | 9.6-11.2 | 8.4-9.3 |
| partial combine | +0.04 | +0.07 |
| **deallocate the trajectory** | **+1.7** | **+4.0-5.2** |
| program end | 11.4-13.0 | 13.0-14.3 |

The teardown of a 48 MB pool inside the timed region costs 1.7 ms serially and 4-5 ms after a
threaded fill (the pages were touched by eight cores: the release shoots every core's TLB). The
pool cache (`blade_pool`, `nested_array_utilities.hpp`) retains blocks of 16 KB .. 4 MB only, so
the trajectory goes back to the OS at `kernel`'s exit. The experts deallocate AFTER the stop
line (bench/README.md item 6 records this as an asymmetry; this is its size on this box).

## 4. The region against the expert's region (same box, same hour)

`expert_regiononly.f90` = `expert.f90` with `bench_start()` moved below the allocation and input
fill (the timer then covers the parallel region + the input sum); `_pretouch_` = the same with
`u = 0` before the timer (no page faults inside it). `recur_omp_pretouch_timed` = the emitted
C++ with a `memset` of the trajectory before the region.

| region only (ms, median of 7) | T=1 | T=8 |
|---|---|---|
| expert, as is (faults inside) | 9.84 | 7.03 |
| expert, pre-touched | 5.13 | 5.38 |
| Blade E-G5, as is (faults inside) | 9.6-9.8 | 7.5-8.2 |
| Blade E-G5, pre-touched | 6.2 | 5.9-6.3 |

Raw bandwidth (`bw.cpp`, 48 MB row-stream writes, pre-touched): 10.4 GB/s at T=1, 9.1 at T=2,
8.3 at T=4, 6.9 at T=8 (NT stores 9.0, memset 8.5): the trajectory store alone is >= 4.6 ms at
ANY team size, and the region does not scale past 2 threads on this box for anyone. Both regions
sit on that floor (expert 5.4, Blade 5.9-6.3; faults add ~1.6 ms to each). The whole program:
expert full mt8 7.5 (7.0-8.6), Blade E-G5 10.6-12.8, of which 4-5 ms is the teardown.

Cell-loop variants (restrict row pointers; `omp simd`): 7.7-8.1 vs 7.5-8.2 -- no effect.

## 5. What this means

- E-G5's region is at parity with the expert's; the mt8 arm is bounded by the teardown
  asymmetry, not by the schedule. The serial arm pays the same asymmetry at 1.7 ms (and still
  wins: RESULTS 15.3 recur serial licensed 1.075 / 1.114).
- A runtime policy that retained the program's largest pool (the cache ceiling is a measured
  policy, "not semantics", per its own contract) would remove the cost from the timed region in
  both arms; it changes the peak working set the harness records. Not changed here: a decision
  for the user (an instrument-first follow-up: a compile-time ceiling knob and an indicative row).
- The same instrument applies to stencil's mt8-licensed gap (item 2): `lap` (24 MB) and `u`
  (24 MB) are block-local and freed inside the timer there too.
