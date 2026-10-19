# bench/harness -- the Blade-vs-Fortran driver

`run.py` is a single-file, standard-library-only Python 3.14 driver (harness version 2). It
discovers kernels, builds every arm of a kernel/regime up front, validates BOTH checksums of
EVERY run against independent pins, runs the interleaved timing protocol, and writes an
immutable session bundle. The protocol is `bench/README.md`, as amended by
`bench/notes/review-method.md`; this file documents the tool.

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
  `BLADE_OMP_[PARALLEL_FOR_]SIMD_REDUCTION(` invocations in the .cpp, of `reassoc`/`fast`
  fast-math flags on fadd/fmul/... instructions in the .ll (comments stripped). A
  **repro serial** build with either FAILS (review M3).
- blas regime (gram: arms blade-cpp, blade-llvm, gfortran-expert, ifx-expert via `regime_arms`): the exe must import libopenblas.dll; the C++ lane does, the LLVM lane does not (`no-blas`), a Fortran/C++ arm that does not is a failure. `OPENBLAS_NUM_THREADS` is 1 (serial) / 8 (mt8) for every arm; an mt8 BLAS arm with no OpenMP construct is timed, its parallelism being the library's threads.
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

## Protocol (bench)

1. Preflight: fingerprint (CPU, cores, RAM, git SHA, affinity), `powercfg` processor settings
   (PROCTHROTTLEMAX/MIN, PERFBOOSTMODE) and AC state, toolchain identities; refuse on battery.
2. Pre-session checks (above); planned-cell manifest.
3. Controls (`config.json controls`; compute = recur gfortran-expert at full, memory =
   saxpy gfortran-expert at sizes.control; both serial x repro) built once and validated.
4. Per kernel x regime block: power + AC re-read (abort on battery); build all arms (mt8 arms
   only if their own serial arm validated in this session -- otherwise that serial block is
   validated at smoke size first); one validated warm-up per arm; then R rounds, each a
   SEEDED RANDOM PERMUTATION of all live arms, twins and controls (M15). R = max(serial 11 /
   mt8 7, 21 when the median full-size warm-up < 50 ms); `--rounds` overrides and taints when
   lower. Cooldown >= 0.5 s after every run, >= 3 s between kernels, 60 s between bench sessions (enforced at session start from `bench/.build/last-bench-session-end.txt`). The session JSON is
   checkpointed after every run.
5. Excursions (charter 2.5 as amended 2026-10-18, user-approved). An arm run more than 3x
   that arm's running median in the block (once the median is >= 5 ms) is an EXCURSION:
   - the first one in a block (the round's controls necessarily in bounds, since a control
     excursion aborts at once) is DROPPED from inference and RECORDED: the sample stays in
     session.json with `excursion_dropped: true` and its `excursion_ratio`, the block lists it
     under `excursions`, summary.md prints a note, the round continues. The bootstrap then
     uses that arm's remaining rounds (each draw takes the arm's samples in the drawn rounds);
   - a SECOND excursion in the same block, ANY control run more than 3x the control's own
     running median, or AC power lost ABORTS the session; the partial bundle is written.

## Statistics

- Primary estimand: the complete timed region. Ratio = median(arm) / median(Blade reference)
  (> 1 = Blade faster). Reference: blade-llvm in serial regimes, blade-cpp in mt8 or when the
  LLVM lane fell back; none when neither is valid (no ratios).
- Round-block bootstrap (M15/M16): rounds are resampled jointly, every statistic of a draw
  uses the same rounds, 10000 draws, seeded; no draw is ever discarded.
- Verdict (M17, delta = `verdict_delta` 0.10): `win` (CI > 1 and ratio >= 1.10),
  `detectable-win` (CI > 1, ratio < 1.10), `equivalent` (CI within [1/1.10, 1.10]),
  `detectable-loss`, `loss`, `inconclusive`. Intervals are pointwise.
- Incremental (M9): `median(arm) - median(its own twin)` with its interval; incremental ratio
  only when the reference's incremental interval excludes 0, else "undefined".
- Drift (M18): per arm and control, first-half vs second-half median > 5% flags the block;
  a control's block median more than 7% from its session baseline flags the block; an
  invalid control run flags the block; flagged blocks keep their numbers but every verdict
  reads `suppressed` (the unsuppressed one is kept in the JSON).

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

The host-crash history behind the original defensive rules was an integrated-graphics overload
issue that has since been resolved (user, 2026-10-18). The cooldown floors were lowered to
0.5 s between runs, 3 s between kernels and 60 s between sessions. The AC check, the excursion
rule and one-timed-process-at-a-time remain (measurement hygiene). The 64 MB working-set and
2.5 s run limits stay in force for the current, frozen sizes but are NO LONGER safety limits
and may be raised in a later session. Cooldowns are not part of the smoke-evidence key.
