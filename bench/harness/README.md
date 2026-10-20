# bench/harness -- the Blade-vs-Fortran driver

`run.py` is a single-file, standard-library-only Python 3.14 driver (harness version 2). It
discovers kernels, builds every arm of a kernel/regime up front, validates BOTH checksums of
EVERY run against independent pins, runs the interleaved timing protocol, and writes an
immutable session bundle. The protocol's entry point is `bench/README.md` (fairness,
regimes, honesty rules) and its kernel-side rules are `bench/kernels/README.md` (tiers,
output contract, SIZE marker, Blade facts), as amended by `bench/notes/review-method.md`;
this file documents the tool and, since the 2026-10-18 consolidation, carries the
protocol's statistics-and-bookkeeping and hygiene text ("Protocol and statistics" and
"Limits: hygiene, not safety" below, formerly `bench/README.md` §6 and §7).

```
C:\Python314\python.exe bench\harness\run.py list
C:\Python314\python.exe bench\harness\run.py check gram                         # smoke size, serial-licensed-native
C:\Python314\python.exe bench\harness\run.py check gram --regimes mt8-licensed-native
C:\Python314\python.exe bench\harness\run.py smoke --regimes all                 # REQUIRED before bench (smoke evidence)
C:\Python314\python.exe bench\harness\run.py bench                               # RUNNER ONLY
C:\Python314\python.exe bench\harness\run.py bench --kernels gram,dot --regimes serial-licensed-native
```

`python3` on this host is a Store stub; always call `C:\Python314\python.exe`.

## Verbs and exit codes

| verb | what it does | output |
|---|---|---|
| `list` | kernels, sizes, regimes, whether each arm's source exists | stdout |
| `check <kernel>` | build every arm at `--size` (default smoke), run each ONCE, validate against the pin | `bench/.build/sessions/<date>-<host>/session-<HHMMSS>-check/` |
| `smoke` | `check` for every kernel (or `--kernels`) | `.../session-<HHMMSS>-smoke/` |
| `bench` | the measured protocol | `bench/results/<date>-<host>/session-<HHMMSS>/` + `summary.md` beside it |

`smoke`/`check` record per (kernel, regime, arm) the status and the hashes of THAT arm's
committed source, of `bench_support.f90` (Fortran arms), of Blade.dll (Blade arms) and of the
build-relevant config subset (fortran_flags, fortran_twin_flags, blade_knobs, run_env,
tolerance; comments excluded) in `bench/.build/smoke-evidence.json`. Evidence is therefore
per kernel and per arm: editing one kernel's Blade source stales only that kernel's Blade
arms (an `inputonly.blade` edit only its Blade twins); a Blade rebuild stales every Blade arm
and no Fortran arm; a flag/knob/run_env change stales everything. run.py itself is NOT part
of the key (harness edits to statistics or reporting do not require a re-smoke). `bench`
REFUSES to start unless every planned cell has passing evidence for the current hashes and
every kernel has a pin at the bench size (`--skip-smoke-check` / `--allow-unpinned` exist and
taint the session).

Without `--regimes`, smoke/check run `serial-licensed-native`; bench runs every regime the
kernel declares, in the order of work: licensed serial, licensed mt8, repro serial, repro
mt8, fastmath serial/mt8, then blas.

Options (all verbs): `--kernels-dir`, `--regimes a,b|all`, `--arms a,b` (a base arm also
selects its twin), `--build-dir`, `--out-dir`, `--config` (default `harness/config.json`),
`--validation-dir` (default `bench/validation`), `--run-timeout` (default and bench cap
7.5 s = 3 x the 2.5 s run limit), `--build-timeout` (600 s), `--allow-unbuilt` (smoke/check only: an arm whose source is missing is a warning instead of a FAILURE; bench always refuses unbuilt planned cells). bench only:
`--size full|smoke|<name>`, `--rounds N` (below the minimum taints), `--cooldown` (>= 0.5 s),
`--kernel-cooldown` (>= 3 s), `--resamples` (default 10000), `--seed`, `--allow-battery`,
`--allow-unpinned`, `--skip-smoke-check`.

Exit: `0` clean; `1` any failure (build failure, contract/oracle failure, DQ, unexpected
LLVM fallback, abort, harness exception); `2` configuration error; `3` bench completed with
session flags only (taint, suppressed blocks, missing control).

## Arms and regimes

Base arms `blade-llvm`, `blade-cpp`, `gfortran-natural`, `gfortran-expert`, `ifx-natural`,
`ifx-expert`. With `"inputonly": true` EVERY base arm gets its own twin `<arm>-inputonly`
(review M8): Blade twins build `inputonly.blade` (both lanes); Fortran twins build the TIER
source with `fortran_twin_flags` (`-cpp -DBENCH_INPUTONLY` / `/fpp /DBENCH_INPUTONLY`). A
twin must print `checksum = 0.0` and the pinned `input_checksum`.

Regimes are `<serial|mt8>-<licensed|repro|fastmath>-<native|blas>`. Per regime the knobs come
from `config.json`:

| | Blade compile knobs | Fortran flags (`fortran_flags.<compiler>.<fp>` + serial/mt8 extra) |
|---|---|---|
| licensed (headline) | (none: the default since 5f4b33ea honours every licence) | `-O3 -march=native -ffp-contract=fast -fno-external-blas -fno-external-blas64 -cpp -DBENCH_LICENSED` / `/O3 /QxHost /fp:precise -mllvm -fp-contract=fast /fpp /DBENCH_LICENSED` |
| repro | `BLADE_FP_REPRO=1 BLADE_FP_REASSOC=0` (mt8: threaded maps, serial folds) | the same without `BENCH_LICENSED` |
| fastmath | (as licensed) | `-ffast-math` / `/fp:fast` |
| every Blade arm | `BLADE_BLAS=0|1`, `BLADE_EXE_CACHE=0`, `BLADE_LLVM_CLANG`; LLVM: `BLADE_LLVM=1 BLADE_FP_CONTRACT=fast`; serial: `BLADE_OMP_THREADS=1` | serial `-fopenmp-simd` / `/Qopenmp-simd`; mt8 `-fopenmp` / `/Qopenmp` |

Run environment (`run_env`): serial `OMP_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 OMP_DYNAMIC=FALSE`,
pinned to ONE logical CPU (`serial_affinity_cpu`, default 2) from the child's first
instruction (the harness sets its own affinity mask before spawning; children inherit it);
mt8 `OMP_NUM_THREADS=8 OMP_DYNAMIC=FALSE KMP_AFFINITY=disabled`, UNBOUND (no OMP_PLACES / OMP_PROC_BIND: libgomp cannot bind on Windows and libiomp5 binding caused 17-48 ms jitter; fortran-notes.md). Recorded in `machine.run_env`.
Every process: inherited env minus all `BLADE_*`, `OMP_*`, `GOMP_*`, `KMP_*`,
`OPENBLAS_NUM_THREADS`; `C:\msys64\ucrt64\bin` FIRST on PATH, then the Intel runtime (ifx),
then `OPENBLAS_DIR\bin` (blas). Executables are run directly, never via `blade run`.

## Build acceptance (recorded under `build.evidence`)

Build statuses: `ok`, `failed`, `fallback`, `serial-only`, `no-blas`, `no-parallel-form`,
`unbuilt` (source missing), `gated` (mt8 arm whose own serial arm did not validate).

- Blade `compile -o <exe> --verbose --print checksum,input_checksum`; the kept build dir's
  `.ll`/`.cpp`/headers are copied to `<armdir>/generated/`. LLVM arms need positive evidence
  (`[Emit] <stem>.ll`, a `.ll`, no `.cpp`); the stderr line `[blade] llvm lane refused: ...`
  makes the arm `fallback` (a failure unless kernel.json `"expected_llvm": "fallback"`).
- Thread constructs counted in the artifact; a serial build with any FAILS. In mt8 the LLVM
  lane is `serial-only` (no OpenMP); a C++ build with none, or a kernel.json
  `no_parallel_form`, is `no-parallel-form`. Neither is timed.
- **fold form** (table column, from the artifact, never inferred from the regime): count of
  `BLADE_OMP_[PARALLEL_FOR_]SIMD_REDUCTION(` invocations, `blade_fold::{sum,dot,sq,dot3}<` calls and scalar K-lane trees (one per typed `__rlane0` / `__pl0` seed declaration, `CodeGenExprSupport.fpReassocLaneStmts`) in the .cpp, of `reassoc`/`fast`
  fast-math flags on fadd/fmul/... instructions in the .ll (comments stripped). A
  **repro serial** build with either FAILS (review M3).
- blas regime (gram: arms blade-cpp, blade-llvm, gfortran-expert, ifx-expert via `regime_arms`): the exe must import libopenblas.dll, EXCEPT a Blade C++ build whose plan.txt records a native kernel preempting the library for the same contraction -- a `[blas-routing vN] S: declined ...` line (not "BLAS gate off") plus `[microkernel vN] S: applied [packed syrk tile ...` or `[fold-form vN] S: applied [fold form: syrk tile ...` for the same subject S (design-decisions D47). Such a build is `ok` with `evidence.blas_route = "native-preempt"`, `evidence.blas_preempt` (the matched lines) and the note "native tile preempted the library"; library-routed builds carry `blas_route = "library"`. Without those plan lines a build that imports no BLAS DLL is a failure. The LLVM lane is `no-blas` as before; a Fortran arm that imports no libopenblas.dll is a failure. `OPENBLAS_NUM_THREADS` is 1 (serial) / 8 (mt8) for every arm; an mt8 BLAS arm with no OpenMP construct is timed, its parallelism being the library's threads.
- **parallelism** (table column): `serial`, `openmp (N construct(s))`, `library threads (OPENBLAS_NUM_THREADS=n)` [+ openmp runtime], or for Fortran mt8 `openmp runtime linked`.
- `blade plan` under the arm's knobs -> `<armdir>/plan.txt`, `plan_decisions`, `plan_omp`, `plan_fold_form` (the plan's per-site `fold form:` lines; C++ lane only, so recorded beside the artifact counts).
- PE imports, stack/heap reserve+commit, and every imported DLL resolved along the run PATH
  and hashed by its resolved path (M25); the Fortran wrappers' `+ <command>` echo.

## Output contract and validation

stdout must hold exactly one `<name> completed in <t>s` (name = kernel), one `checksum = v`,
one `input_checksum = v`; anything else fails, except scalar `name = value` lines from the
LLVM lane (it ignores `--print`). Fails also on a nonzero exit, `error` on stderr, a
nonpositive or non-finite duration (M14), or a watchdog timeout.

Every run (contract, warm-up, round, control) is validated against the pin (M12):
`input_checksum` within 1e-14 relative (was: equal 15-digit renderings, which is fragile at a half-way point); `checksum` within the
relative tolerance (`config.json tolerance`, kernel.json may override); twins `checksum =
0.0`. A run longer than 2.5 s is a DQ. ANY failure invalidates that arm for the whole block
(dropped from later rounds, no ratio uses it). Without a pin, smoke/check cross-check
against blade-cpp and say so; bench refuses.

## Protocol and statistics (the measured session)

The protocol's statistics-and-bookkeeping text (formerly `bench/README.md` §6, moved here
verbatim on 2026-10-18) merged with the driver's implementation of it. Where the two
disagreed the later, driver rule is kept and marked "driver:"; the bullets without a
"driver:" sub-item were already identical.

- **Session sequence (the driver).** (1) Preflight: fingerprint (CPU, cores, RAM, git SHA,
  affinity), `powercfg` processor settings (PROCTHROTTLEMAX/MIN, PERFBOOSTMODE) and AC state,
  toolchain identities; refuse on battery. (2) The pre-session checks above (smoke evidence,
  pins); the planned-cell manifest. (3) Controls (`config.json controls`) built once and
  validated. (4) Per kernel x regime block: power + AC re-read (abort on battery); build all
  arms (mt8 arms only if their own serial arm validated in this session -- otherwise that
  serial block is validated at smoke size first); one validated warm-up per arm; then R rounds,
  each a SEEDED RANDOM PERMUTATION of all live arms, twins and controls (M15). Cooldown >= 0.5 s
  after every run, >= 3 s between kernels, 60 s between bench sessions (enforced at session
  start from `bench/.build/last-bench-session-end.txt`). The session JSON is checkpointed
  after every run.
- **Builds.** Every arm of a kernel is built up front from `harness/config.json`; the
  harness records the exact command line, compiler version string, flags, environment,
  SHA-256 of each executable AND of every resolved runtime DLL (GNU, Intel, Clang,
  OpenBLAS), stack reserve/commit and temporary-placement settings, and for Blade the
  `blade plan` output (which loops carry `[omp v1]`, which routes) and whether the LLVM
  lane printed `[blade] llvm lane refused: <reason>; falling back to C++`. A fallback arm is
  **marked `fallback`** and reported under `blade-cpp` semantics; the `blade-llvm` cell
  shows "refused: <reason>".
  - driver: the acceptance statuses and the artifact evidence are "Build acceptance" above.
- **Smoke first.** `--smoke` runs every arm once at smoke size and checks the contract
  (three lines, pins agree, no unexpected fallback, no extra output), runs the cell-by-cell
  validation, and verifies that every required twin exists and prints `checksum = 0.0`. It
  is a precondition of a session, and the gate ENFORCES that smoke evidence is
  re-established after every binary or configuration change (the exact source, executable
  and config hashes are checked). Status: the runner's first all-regime sweep validated
  448 cells with zero checksum disqualifications (`notes/run-log.md`).
  - driver: the evidence key (per kernel, per arm; run.py itself excluded) is under "Verbs
    and exit codes" above.
- **Pilot and sample size (M15).** A 3-round full-size pilot sets the production round
  counts against a declared precision target of ±5% on the headline ratio; the defaults
  below are minimums, enforced (a `--rounds` override below the minimum taints the session
  and the table says so): serial 11, memory-bound kernels (full-size serial median < 50 ms)
  21, mt8 7. Thirty to fifty observations per arm across sessions is the working
  expectation for the short kernels, not a guarantee; measurement never stops because
  significance first appeared.
  - driver: R = max(serial 11 / mt8 7, 21 when the median full-size warm-up < 50 ms);
    `--rounds` overrides and taints when lower.
- **Rounds.** Per kernel and regime: one discarded warm-up run per arm, then R rounds. In
  each round every arm — main arms, twins and both controls — runs once in a **seeded random
  permutation** (recorded), not a cyclic rotation. Cooldowns (D46, from the session after
  serial-repro 3b): 0.5 s between runs, 3 s between kernels, 60 s between sessions; the
  sessions up to and including 3b ran with 2 s / 10 s / ~5 min, recorded per session in
  RESULTS.md §2.
- **Controls (M18).** Two fixed binaries run once per round of every block: the **compute
  control** (`recur`, gfortran-expert, serial x repro: deterministic, ~0.1 s, tracks core
  clock and thermal state; note its region also allocates and writes a 48 MB trajectory —
  "L2-resident" describes a step, not the region) and the **stream control** (`saxpy`,
  gfortran-expert, serial x repro, built at n = 700,001 — the `sizes.control` variant of
  `saxpy/kernel.json`, which the harness builds like any size variant — a 16.8 MB
  three-stream pass: tracks memory-subsystem contention and background load). BOTH
  controls are serial x `repro` builds; `harness/config.json` follows this text. Every control run is validated
  like any other run; an invalid or thermal-excursion control run flags the block. Within
  a block, a first-half vs second-half median difference above 5% in either control flags
  the block ("within-block drift"); a rolling control median moving more than 7% against
  the session's first block flags the session from that block on. Flagged blocks carry the
  flag in every cell; no result is ever rescaled by a control.
  - driver: flagged blocks keep their numbers but every verdict prints `suppressed` (the
    unsuppressed verdict is kept in the JSON); the report calls such blocks "indicative".
- **Per arm:** n, min, median, IQR. **Headline ratio** = median(arm) / median(blade-llvm)
  in serial regimes (> 1 means Blade faster); in **mt8 the denominator is `blade-cpp`**
  (the LLVM lane has no OpenMP); where `blade-llvm` is a fallback or refused, the
  denominator is `blade-cpp`. The denominator is printed explicitly in every row. Intervals
  are 95% bootstrap percentile intervals of the ratio of medians obtained by **resampling
  whole rounds jointly** (10,000 resamples; twins and controls travel with their round, so
  shared-twin correlations are preserved). Intervals are **pointwise**; the suite makes no
  simultaneous suite-wide superiority claim and applies no multiplicity correction because
  it states none.
  - driver: reference = blade-llvm in serial regimes, blade-cpp in mt8 or when the LLVM lane
    fell back; none when neither is valid (no ratios). Every statistic of a bootstrap draw uses
    the same rounds; the draw seed is recorded.
- **Incremental estimates (M9, M16).** For kernels with twins: the incremental difference
  `median(total) − median(own twin)` per arm with its joint-round bootstrap interval,
  always; an incremental RATIO only when the reference arm's incremental interval excludes
  0, otherwise the cell reads `undefined`. No resample is ever discarded.
- **Verdicts (M17), practical threshold δ = 10%.** For a ratio r = median(arm)/median(Blade)
  with interval [lo, hi]: *win*: lo > 1 and median r >= 1.10; *detectable*: lo > 1 and
  median r < 1.10; *equivalent*: [lo, hi] ⊂ [1/1.10, 1.10] (the interval need not contain
  1); *loss*: hi < 1 and median r <= 1/1.10; *detectable loss*: hi < 1 and median r >
  1/1.10; otherwise *inconclusive*. Failure to detect a difference is not equivalence.
  Verdicts are per arm and per regime, compared with the spec's hypothesis; a mismatch is
  investigated, never resolved in the hypothesis's favour.
  - driver spellings: `win`, `detectable-win`, `equivalent`, `detectable-loss`, `loss`,
    `inconclusive` (`verdict_delta` 0.10 in `config.json`).
- **Disqualification:** pin mismatch (kernels/README.md §3.4); a missing, duplicate or malformed line; a
  nonpositive duration; a run longer than 2.5 s (charter safety rule 3; the child
  watchdog is 3x that, 7.5 s); a thermal excursion — any run, warm-up or control included,
  slower than 3x the block's running median aborts the block and the session pauses (M19);
  the machine on battery (the whole session is refused). **Excursion rule (DECIDED by the
  user, 2026-10-18; D45):** an ISOLATED single-run excursion (one run above 3x the block's
  running median while both controls in that round are normal) is DROPPED AND RECORDED —
  the sample stays in the bundle, marked, excluded from inference, and the planned-cell
  manifest says so; the session ABORTS on a second excursion in the same block, on any
  control excursion, or on AC loss. (Session 2's saxpy mt8 block was lost to a single 43
  ms stall of a 6 ms input-only twin under the previous always-abort rule; RESULTS.md
  §12.2.)
  - driver: an arm run more than 3x that arm's running median in the block (once the median
    is >= 5 ms) is an EXCURSION. The first one in a block (the round's controls necessarily in
    bounds, since a control excursion aborts at once) is DROPPED from inference and RECORDED:
    the sample stays in session.json with `excursion_dropped: true` and its `excursion_ratio`,
    the block lists it under `excursions`, summary.md prints a note, the round continues; the
    bootstrap then uses that arm's remaining rounds. A SECOND excursion in the same block, ANY
    control run more than 3x the control's own running median, or AC power lost ABORTS the
    session; the partial bundle is written. The run timeout is 7.5 s (3 x the 2.5 s limit).
- **Host conditions (M19).** The session records at start and per block: AC state, the
  active power plan and `powercfg /q` processor settings (maximum processor state, boost
  policy), OEM thermal mode where readable. The reference configuration caps the maximum
  processor state at ~80%; every result table is labelled **"max processor state capped
  (~80%)"** and no number is extrapolated to the uncapped machine — the cap can shift the
  balance between compute- and memory-bound kernels.
  - superseded label: after the pilot the user chose to publish UNCAPPED runs (maximum
    processor state 100%, `bench/README.md` §5); the "capped (~80%)" label applies only to a
    capped re-run, which remains the documented way to obtain unsuppressed verdicts.
- **Session bundle (M27)** in `results/<yyyy-mm-dd>-<host>/`, immutable and checkpointed
  after every run, with raw per-run values including warm-ups, controls and aborted partial
  blocks, a **planned-cell manifest** marking every cell as run / unrun / unsupported /
  fallback / no-parallel-form / failed / disqualified, and exact reproduction commands. The
  layout is the "Session bundle (M27)" section below (owner: harness agent) — the current
  one; the pre-consolidation protocol's longer contents list (per-session source-tree copies,
  runtime header copies, build logs) was dropped with commit 5dd4f7ec and is not restated.
- Nothing is extrapolated or estimated in a result table. An unrun cell is reported as
  unrun.

## Schemas

**kernel.json** (`bench/kernels/<name>/`, owner: harness):

```
name (= directory), group, title, notes, hypothesis {natural, expert},
sizes {smoke {param: int}, full {...}, [scale ...]}   -- full = the committed literals
regimes [..]                                          -- REQUIRED, e.g. "serial-licensed-native"
arms [base arms]                                      -- default: all six
inputonly true                                        -- every arm gets its own twin
inputonly_blade "inputonly.blade", sources {blade, natural, expert}, blas_sources {expert: ...}
regime_arms {"<regime>": [base arms]}                 -- narrow one regime
rounds {serial: n, mt8: n}                            -- raise the minimum
tolerance {licensed|repro|fastmath: rel}, input_tolerance (default 1e-14 relative; a kernel may only widen it)
expected_llvm "fallback" + expected_llvm_reason       -- a fallback is then not a failure
no_parallel_form true | ["<mt8 regime>"]              -- Blade has no licensed parallel spelling
parallel_notes {"<arm>": "parallel preparation, serial kernel ..."}   -- shown in mt8 rows
size_exempt {"<file>": [params]}
```

SIZE markers: each param is declared on exactly ONE line per source, ending with `// SIZE p`
(Blade) or `! SIZE p` (Fortran), with exactly ONE integer literal equal to `sizes.full`
(M28); the harness rewrites it in a scratch copy (`<armdir>/src/<kernel>.blade` keeps the
stem, so the timing line names the kernel).

**pins** (`bench/validation/pins.json`, `pins-A.json`, `pins-B.json`, merged; a duplicate
entry must agree): `{"<kernel>": {"<size>": {"checksum": x, "input_checksum": y, "sizes":
{...}}}}` (optionally wrapped in `"kernels"`). `sizes` is optional but recommended: when present
it must equal kernel.json's, which catches a stale pin.

**config.json** (required, every key; M3): `controls[]` {name, kernel, arm, regime, size},
`tolerance`, `verdict_delta`, `serial_affinity_cpu`, `run_env {serial, mt8}`,
`blade_knobs {all, llvm, licensed, repro, fastmath}`, `fortran_flags {gfortran, ifx} x
{licensed, repro, fastmath, serial_extra, mt8_extra}`, `fortran_twin_flags`. Keys starting
with `_` are comments.

## Session bundle (M27, lean form since 2026-10-18)

`bench/results/<date>-<host>/session-<HHMMSS>/` holds:

- `session.json` (checkpointed after every run; `os.replace` retried 10x with backoff on a
  sharing violation, a failed checkpoint is logged in `checkpoint_failures` and the session
  continues), `summary.md`, `manifest.json` (argv + every planned cell with its final status:
  measured, validated, unsupported, unrun, fallback, serial-only, no-blas, no-parallel-form,
  gated, failed, invalid, dq, unbuilt);
- `sources-sha256.json`: the SHA-256 of every input (kernels, fortran, harness, validation,
  README, charter, .gitignore), keyed by path relative to the bench tree and hashed in place;
  the content itself is in git at `machine.git_sha` (no `sources/` copy is written);
- `build/<kernel>/<size>/<regime>/<arm>/` and `build/_controls/...`: `build-command.txt` and
  `plan.txt` (always), `generated/<kernel>.cpp` / `.ll` (the program's own translation unit
  only), `build-stdout.txt` / `build-stderr.txt` only when the build failed or fell back or
  stderr carries a diagnostic, and the executable and copied DLLs (git-ignored, hashes in
  session.json).

Not written: the SIZE-substituted `src/` copies (the sizes are in the manifest and each build
record's `source.size_lines`; `source.compiled_sha256` keeps the identity of the rewritten
file), `*.copy` files, and the runtime header / LLVM shim copies (identified by the Blade.dll
hash; their per-file hashes stay in `evidence.runtime_headers_sha256`). Smoke and check
sessions write only `session.json`, `summary.md` and `manifest.json` (their builds live in
the git-ignored `bench/.build/`, unpruned).

session.json top level: `schema`, `harness_version`, `mode`, `argv`, `size`, `started`,
`finished`, `duration_s`, `aborted`, `machine`, `power`, `toolchain`, `pins`, `protocol`,
`failures`, `flags`, `warnings`, `controls[]` (spec, build, samples incl. warm-up, per_block,
baseline), `manifest`, `blocks[]`. A block: kernel, group, regime, size, sizes, tolerance,
hypothesis, gate_only, power, checksum_reference, ratio_reference, rounds {n, rule},
order_seed, bootstrap {rounds, resamples, seed}, controls, flags, suppressed, and `arms`:
per arm `status`, `reason`, `note`, `build` (the build record), `samples[]` (kind, ok,
reason, time_s, both checksums + texts, validation, round, position, wall_s, peak_working_set,
page_faults, affinity_mask, started), `stats` (n, min, median, q1, q3, iqr, max, mean over
the timed rounds), `peak_working_set_max`, `trend`, `ratio`, `ratio_vs_blade_cpp`,
`incremental`, `incremental_ratio`.

## Test kernels

`bench/.build/stub-kernels/` (selfcheck, dqprobe, fbprobe) with pins in
`bench/.build/stub-validation/` and `bench/.build/stub-config.json`:
`run.py smoke --kernels-dir bench\.build\stub-kernels --validation-dir bench\.build\stub-validation --config bench\.build\stub-config.json`.

## Limits: hygiene, not safety (2026-10-18)

The protocol's measurement-hygiene rules (formerly `bench/README.md` §7, D46; the same text
the charter's §2 carries), moved here verbatim on 2026-10-18.

History in one sentence: the charter's "safety rules" came from an August 2026 run of
host crashes that has since been traced to an integrated-graphics overload and resolved,
so what remains below is measurement hygiene, not crash protection.

1. **One timed process at a time.** Only the RUNNER executes timing runs; no other heavy
   process (a `dotnet build`, a compile fleet, another benchmark) runs concurrently with a
   session, because it would perturb the measurement. Other agents run programs for
   correctness at smoke size only.
2. **Interleaved rounds and drift controls** (the "Protocol and statistics" section above): seeded random permutation of all arms
   per round, two controls per round, within-block and rolling drift checks; a block that
   trips them is reported as indicative.
3. **The excursion rule** (the "Protocol and statistics" section above, D45): an isolated single-run excursion with normal controls
   is dropped and recorded; a second excursion in the block, a control excursion, or AC
   loss aborts the block.
4. **AC check** (free): the harness records AC/battery and the power plan, refuses the
   suite on battery, and rechecks per block.
5. **Cooldowns:** 0.5 s between runs, 3 s between kernels, 60 s between sessions (D46;
   previously 2 s / 10 s and ~5 min between sessions — the values in force for each
   session are listed in RESULTS.md §2, because inter-run spacing can change thermal state
   and a reader should see where it changed).
6. **Frozen for this run, no longer safety limits:** the 64 MB working-set cap (natural
   dense arrays counted) and the 2.5 s single-run cap stay as the sizes were frozen under
   them; later sessions may raise them. Threaded arms still run only after the serial arms
   of the same kernel validated (correctness gating, not safety).

Cooldowns are not part of the smoke-evidence key. The resume plan after the session-2 abort
that followed this list in the old protocol document is recorded in `bench/RESULTS.md` §12
and `bench/notes/design-decisions.md` (D45); its two spelling facts are
`bench/kernels/README.md` §4.10.
