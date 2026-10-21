# RESULTS section 16 (wave 3) -- DRAFT, filled as the wave runs

Honesty rules as in sections 14-15: `MEASURED` lines name the command and the bundle; a block
the harness flagged is `indicative`; no Blade arm may be slower than its OLD binary beyond
its CI without the row saying so.

## 16.1 What landed (wave 3)

| item | mechanism | knob | gate / proof |
|---|---|---|---|
| E-G5 threaded `let rec` step (recur mt8) | `where omp(v: 1)` on the step's FINAL map is kept through slice-write fusion and proved (same-x reads of rows `n - c`, `1 <= c <= lo`, or seed rows; fresh rank-2 buffer; no write outside it; no `where repro` splice); the C++ time loop runs on ONE persistent team with contiguous x ownership and no per-step synchronization (a team entry or barrier is ~40 us on the box: 300 of either would be the budget); licensed self-dot row folds ride the team as per-worker chunk partials; strict builds fold complete rows after the team (in parallel only under the outer fold kernel's own `where omp`) | `BLADE_REC_STEP_OMP` (optimizer gate) | corpus recursive-arrays/031-032, mutability-errors/042; optimize 12 decision/emission cases; `blade test rec-step` (threads-on vs threads-off raw bits: trajectory, scalar, observed team; T = 1/3/8, limit 2; licensed 1e-12, repro bitwise); Codex attack review of the brief (19 objections folded in) and of the diff |

## 16.2 Attribution (MEASURED, directional single-process timings; `bench/notes/wave3/recur-mt8-attribution.md`)

recur mt8 licensed: E-G5's region is at parity with the gfortran expert's region (pre-touched
trajectory: expert 5.4 ms, Blade 5.9-6.3 ms at T=8; both at the box's ~4.6 ms write-bandwidth
floor; page faults add ~1.6 ms to each). The whole remaining gap to the expert's 7.5 ms program
is the 48 MB teardown INSIDE Blade's timed region: 1.7 ms serial, 4-5 ms after a threaded fill
(TLB shootdowns across the cores that touched the pages); the experts free after their stop
line (README item 6's recorded asymmetry, now quantified). The retained-block pool cache stops
at 4 MB; a larger retention is a footprint-policy decision left to the user.

## 16.2b Items 2-4 (MEASURED, directional; `stencil-mt8-attribution.md`)

- stencil mt8-licensed: of the ~2 ms gap to the expert's region, 1.0 ms is the 24 MB `lap`
  teardown inside the timed region (G49); the rest sits in DRAM-bound read phases. The input
  checksum now carries the read-back licence every Fortran arm has (`where omp` on its outer
  fold kernel): threaded under licensed (Path B), measured flat (a 24 MB read does not scale
  on this box), serial under repro (G48: the row-slot recognizer misses a row-view fold).
- B2l: not built -- E-G5's in-team partials serve its mt8 target; serial ceiling <= 3.5%
  (brief B), below what the harness resolves here. `lanes` stays reserved.
- D1: instrumented (gram's 3 regions = 236-257 us per call, ~1.8% of mt8 gram; the persistent
  shape is no cheaper on this libgomp): DROPPED on the instrument, no code.
- anti_g: re-measured in the A/B below (3 interleaved sessions per arm, serial + mt8).

## 16.3 Harness A/B -- SUPERSEDED: filled in bench/RESULTS.md section 16.3 (quiet-box re-run 20:12-21:30; anti_g kept at 1)

## 16.4 Gates -- TBD
