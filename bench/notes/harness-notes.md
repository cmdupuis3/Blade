# Harness notes (harness agent)

Owner: the harness agent. Covers `bench/harness/**` and every `kernel.json`. Everything below
was observed on this host on 2026-10-17 with the worktree's `bin\Release\net10.0\Blade.exe`
(Blade.dll sha256 6c663c49d05dbe7a...), tiny programs only.

## 1. Blade CLI facts the harness relies on

- `Blade.exe compile <src> -o <exe>` builds in `%TEMP%\blade-build\<stem>-<hash>-<pid>-<nonce>`
  and copies the exe (plus any DLL the build placed there, e.g. libopenblas.dll) to `-o`.
  Without `-o` the exe lands beside the source. It prints the exe path on stdout.
- `--verbose` (stderr): `[Emit] <builddir>\<stem>.ll|.cpp`, `[Compile] <exe>`, `[Build] kept
  <builddir>`. The harness uses `[Emit]` + the kept directory's contents as the POSITIVE lane
  evidence (LLVM: a `.ll`, the C shim, no `.cpp`; C++: the `.cpp` + runtime headers).
- `--print a,b` is a mode accepted by `compile` (stripped before verb parsing). It is honoured
  by the C++ lane and **ignored by the LLVM lane**, which prints every top-level binding
  (`BLADE_PRINT` equally ignored). Reproducer: a top-level `let m = method_for(range<I, J>) <@> ...`
  plus `let checksum = reduce(m, (+), axes = 2)`: the LLVM exe echoes `m = [[...]]`.
- `BLADE_EXE_CACHE=0` gives a fresh build every time (`[Compile]` shows a real compile; build
  wall ~1.8 s LLVM, ~2.8 s C++ for a tiny program).
- Floats print with 15 significant digits in both lanes (`setprecision(15)`); integral doubles
  print as `2.0`. The timing line prints e.g. `6.6e-06s` (C++) / `8.34465026855469e-06s`
  (LLVM: timespec_get, ~0.24 us steps). The harness parses both.
- The LLVM fallback line is exactly `[blade] llvm lane refused: <reason>; falling back to C++`
  on stderr (src/CliCommands.fs:406). Verified with a Float32 program ("a combinator's element
  has type Float32 -- the llvm lane handles Int64/Float64/Bool/String scalars only") and on
  maskagg ("a compound-index initializer").
- The charter's suggested refusal probe `reduce(m, (+), axes = 2)` on a rank-2 array is NO
  LONGER refused by this binary (inside a function, at top level, forced or deferred): the
  61e06042 coverage audit closed it. Float32 is a reliable refusal probe.
- LLVM lane has **no OpenMP**: a `where omp` licence compiles serial with no refusal line
  (src/EmitLlvm.fs:3037, "an omp clause is a LICENCE ... emitting the serial body"). The
  harness marks blade-llvm `serial-only` in every mt8 regime and uses blade-cpp as the mt8
  reference.
- LLVM lane has **no BLAS route**: with `BLADE_BLAS=1`, `blade plan` reports
  `[blas-routing v1] __v24: applied [Syrk (L3, d); HostBlas: blade_linalg::blade_gram_same_d ...]`
  (gram), the C++ exe imports libopenblas.dll, but the LLVM exe imports only KERNEL32.dll (it
  computes natively). The harness marks blade-llvm `no-blas` in the blas regime. Note: `blade
  plan` describes the C++ lane's decisions, not the LLVM lane's.
- The LLVM lane DOES honour `BLADE_FP_REASSOC=1` (the .ll carries `reassoc` fast-math flags in
  the fast regime; 2 in the stub), contrary to a comment in EmitLlvm.fs.
- C++ lane thread pragmas are spelled through macros (`BLADE_OMP_PARALLEL_FOR`,
  `BLADE_OMP_PARALLEL_FOR_DYNAMIC`, `BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION(...)`), not
  `#pragma omp`; a requested-but-serial licence leaves the comment
  `[omp] requested but emitted serial: <why>` in the .cpp. The harness counts all three.
- No `blade.toolchain.json` beside the exe (an unset knob would otherwise fall through to it);
  the harness refuses to run if one appears.

### Compiler finding (architect: G23)

`blade plan` reports `[blas-routing v1] __v24: applied [Syrk (L3, d); HostBlas:
blade_linalg::blade_gram_same_d; tier TierOpenBlasDir]` for gram under `BLADE_BLAS=1` in BOTH
lanes, but only the C++ lane's executable imports libopenblas.dll; the `BLADE_LLVM=1`
executable imports only KERNEL32 + UCRT and computes natively. `blade plan` therefore
describes the C++ lane's decisions and is silent (or wrong) about the LLVM lane's. Repro:
`BLADE_BLAS=1 BLADE_LLVM=1 Blade.exe compile bench/kernels/gram/gram.blade -o g.exe`, then
`objdump -p g.exe | findstr "DLL Name"`. The harness marks such an arm `no-blas` and never
times it.

## 2. Other toolchain facts found while wiring

- `powershell -File script.ps1 ... -LC:\path` -- PowerShell parses `-LC:` as a parameter
  name and drops the drive; gfortran then gets `-LC \Users\...`. Pass `-L` and the directory
  as separate arguments (the harness does).
- `objdump -p` (ucrt64) lists imports: gfortran exes import libgfortran-5.dll (+ libgomp with
  OpenMP directives), ifx serial exes only KERNEL32/imagehlp (static runtime), ifx /Qopenmp
  exes libiomp5md.dll (the wrapper copies the 2026.1 copy beside the exe; its SHA-256 is in
  `evidence.beside_dlls`), LLVM-lane exes only KERNEL32 + UCRT.
- `ifx /QV` through `build_ifx.cmd` prints `Intel(R) Fortran Compiler for applications running
  on Intel(R) 64, Version 2026.1.0 Build 20260617` (stderr); the harness greps it.
- Active power scheme on this host during testing: **"Silent"** (GUID 64a64f24-...). The
  runner should decide whether that plan is acceptable for the published session (it is
  recorded either way).

## 3. Verification log (all tiny sizes)

Harness v1 (superseded): size substitution, parser, stub kernels, a 3-round bench dry run,
real-kernel smoke against blade-cpp cross-checks (gram smoke 230166.373255735 / 943.17593226349
under the three-round generator).

Harness v2 (after the method review; 2026-10-17 evening):
- Unit checks: duplicate contract lines fail; non-LLVM extra lines fail, LLVM scalar extras
  pass, an LLVM array echo fails; durations 0 and negative fail (M14); twin `checksum = 1.0`
  fails, `0.0` passes; SIZE lines with two literals / a param marked twice / a marker not at
  the end of the line all fail (M28); verdicts win / detectable-win / equivalent /
  detectable-loss / loss / inconclusive at delta 0.10 (M17); the LLVM reassoc regex matches
  `%r = fadd reassoc nsz contract double` and not `fadd contract` nor a `;` comment.
- Stubs rebuilt for generator variant D with their own pins (`bench/.build/stub-validation`):
  `check selfcheck` in serial-licensed / mt8-licensed / serial-repro: all 6 arms + 6 twins
  (Fortran twins via `-cpp -DBENCH_INPUTONLY` / `/fpp /DBENCH_INPUTONLY`) validate against
  the pin; licensed and repro checksums differ in the 15th digit (Blade's reassociated fold),
  both within 1e-12; mt8 blade-llvm reported `serial-only`. `dqprobe`: x1.000001 -> DQ rel
  1e-06; changed seed -> input DQ. `fbprobe`: expected fallback, not a failure; its mt8 arms
  are not gated by the by-design serial fallback.
- `bench --size smoke --rounds 3` dry run on the stubs (serial + mt8 licensed, one control):
  random per-round permutations including twins and the control, round-block bootstrap,
  incremental figures, "undefined" incremental ratio when the reference's interval includes 0,
  taint flags for `--rounds` below the minimum, bundle with manifest, sources and
  sources-sha256.json (16 MB on disk incl. binaries; binaries git-ignored). Exit 3 (flags only).
- Real kernels, `smoke` (serial-licensed-native): 101 of 102 arms validate against
  `bench/validation/pins-A.json` / `pins-B.json`; the 102nd is maskagg blade-llvm, the expected
  fallback ("a compound-index initializer"). Exit 0. Every LLVM arm shows a reordered fold
  form in the licensed regime (1-7 reassoc flags).

## 4. Review dispositions (HARN items) -- status

| item | status |
|---|---|
| M12 oracle | DONE: pins*.json merged (conflicts refused); both checksums validated on every run incl. warm-ups and controls; a failure invalidates the arm's block and every ratio that would use it; each mt8 arm gated on its OWN serial arm (validated at smoke size if the serial block was not run). Pins carry no `sizes` yet -- recommended so stale pins are caught. |
| M14 | DONE (parser rejects nonpositive / non-finite durations). Clock change in Blade's runtime pending the next Blade.exe rebuild (ORCH). |
| M16 / M9 | DONE: round-block bootstrap, no draw discarded; "incremental" (arm minus its OWN twin) with CI first, ratio only when the reference's incremental CI excludes 0. |
| M3 | DONE: config.json required with every key; repro serial Blade builds fail on a `SIMD_REDUCTION(` invocation (.cpp) or a reassoc/fast flag on an instruction (.ll). |
| M15 | DONE: seeded random permutation per round of arms, twins and controls; minimums 11 serial / 21 memory-bound (median full-size warm-up < 50 ms) / 7 mt8; `--rounds` lower taints. The pilot that sets final counts is ARCH/RUN. |
| M17 | DONE (delta 0.10 from config). |
| M18 | DONE: within-block trend (5%) for arms and controls, rolling control threshold 7% vs the session baseline, invalid control run flags the block, flagged blocks' verdicts read `suppressed`. Second control = fusedchain gfortran-expert (PROVISIONAL harness choice until ARCH names the 16 MB stream). |
| M19 | DONE: powercfg PROCTHROTTLEMAX/MIN/PERFBOOSTMODE + AC at start and per block; abort on any run > 3x its running median incl. warm-ups and controls (floor 5 ms); AC loss aborts; child watchdog 7.5 s. PERFBOOSTMODE reads `None` on this host (setting hidden in the scheme). |
| M20 | DONE (harness half): OMP_DYNAMIC=FALSE; serial arms pinned to logical CPU 2 via inherited affinity, mask recorded per sample. Thread count/placement probe per runtime is FORT. |
| M21 / M25 | DONE: peak working set + page faults per run (GetProcessMemoryInfo on the exited child's handle); PE stack/heap reserve+commit; imported DLLs resolved along the run PATH and hashed by resolved path. OMP_STACKSIZE is unset (recorded via run_env). |
| M24 | DONE (harness half): `plan_omp` recorded per Blade arm; kernel.json `no_parallel_form` (recur, onepass) and `parallel_notes` (gram natural matmul) shown in the table. Per-kernel placement docs are FORT/BLA/BLB. |
| M27 | DONE: immutable bundle (sources + hashes, size variants, generated code, logs, plan, manifest of every planned cell, raw samples incl. warm-ups/controls, power/affinity), JSON checkpoint after every run; partial blocks survive an abort. |
| M28 | DONE: strict size rewriter, duplicate contract lines rejected, only LLVM scalar extras tolerated, twins must print `checksum = 0.0`, required twins and smoke evidence (by source/bench_support/Blade.dll/config hashes) checked before a bench session. |
| fold form | DONE: per Blade arm, from the artifact; table column "fold form". |
| regime axis | DONE: licensed (headline) / repro / fastmath; all kernel.json updated; recur mt8 for Fortran only; maskagg full n = 1,000,003. |

## 5. Open items / pending other agents

- 2026-10-19 (user decision, design-decisions D47): blas-regime Blade C++ builds that import
  no BLAS DLL are accepted when plan.txt shows a native preemption for the contraction
  (`native_preempt()`: one subject with a non-gate-off `[blas-routing] declined` line AND a
  `[microkernel] ... packed syrk tile` or `[fold-form] ... fold form: syrk tile` line).
  Unit-checked on bench/.build/gram/full/serial-licensed-blas/blade-cpp/plan.txt (accepted,
  subject __v27), the native-regime plan (gate off -> refused), a library-routed plan, free
  text, and a tile line without a routing decision (all refused). `check gram --size smoke
  --regimes serial-licensed-blas --arms blade-cpp`: ok, blas_route native-preempt (the tile
  applies already at P=61). run.py was replaced atomically (temp file + os.replace) while the
  runner was active.

- 2026-10-18 (user: de-clutter bench/results): lean bundles. No `sources/` tree copy
  (sources-sha256.json, same keys, hashed in place), no SIZE-substituted `src/` copies, no
  `*.copy` / `*.mod` / `*.obj`, no runtime header or shim copies in `generated/` (only
  `<kernel>.cpp`/`.ll`), build stdout/stderr kept only on failure, fallback or a diagnostic.
  Nothing read the pruned copies back: evidence (lane, fold form, thread constructs) is taken
  from the artifacts BEFORE pruning, the fallback line from the compile's stderr in memory,
  smoke evidence hashes the committed sources, `check` prints samples. Test: a 1-round smoke-
  size bench of dot (3 arms + twins) produced a 0.51 MB bundle of 27 files.

- 2026-10-18 (user): cooldown floors lowered to 0.5 s / 3 s (defaults = floors) and a 60 s
  between-session cooldown added (bench waits at start until 60 s after the previous bench
  session ended). AC check, excursion rule, serial process discipline unchanged; 64 MB and
  2.5 s kept for the frozen sizes, documented as hygiene limits that may be raised later.
  Cooldowns live in run.py and are NOT in the smoke-evidence key; a running session keeps the
  values it started with.

- 2026-10-18: input_checksum agreement is now relative error <= 1e-14 (was equality of the
  15-digit renderings). Earlier pairkernel passes were genuine but fragile: the pin's double is
  73.6780804120935499668..., which `%.15g` (Python and ucrt, both correctly rounded) renders
  ...935, the same text as Blade's sum (6.8e-16 lower, rel err 7.7e-16 in every recorded
  smoke). A pin or sum one ulp higher would have rendered ...936 and spuriously failed.

- 2026-10-18 (user-approved): the 3x rule now distinguishes isolated excursions. Trigger seen:
  mt8-licensed saxpy blade-cpp-inputonly ran 43 ms once (7x its 6 ms median; the rest of the
  round and both controls normal) and aborted the session. Now the first arm excursion in a
  block is dropped from inference and recorded (`excursion_dropped`, block `excursions`,
  summary note, session warning); a second one in the block, any control excursion, or AC
  loss aborts. RoundBootstrap uses the union of rounds so a dropped sample removes only that
  arm's round. Unit-checked (drop, second-excursion abort, control abort, bootstrap with a
  missing round).
- Smoke-evidence keying (answer to the coordinator): per (kernel, regime, arm) on that arm's
  source hash (+ bench_support.f90 for Fortran, Blade.dll for Blade) + the build-relevant
  config subset. The group-A Blade source changes stale only those kernels' Blade arms (and
  Blade twins if `inputonly.blade` changes); Fortran arms and the other kernels stay valid.
  run.py is not keyed, so this harness change needs no re-smoke.

- FP-default flip (5f4b33ea) applied to config.json: licensed = no Blade fp knob, repro = `BLADE_FP_REPRO=1 BLADE_FP_REASSOC=0`, fastmath = as licensed; repro-mt8 Blade parallelism reads "threaded maps, serial folds"; `plan_fold_form` recorded. Not yet exercised: waiting for the rebuilt Blade.exe (all smoke evidence goes stale with the new Blade.dll hash).

- Runner smoke fixes (2026-10-17): checkpoint replace retried (10 x 50 ms linear backoff) and non-fatal; smoke/check fail on unbuilt arms unless `--allow-unbuilt`; gram blas arms restricted to blade-cpp/blade-llvm/gfortran-expert/ifx-expert; mt8 BLAS arms timed with parallelism "library threads (OPENBLAS_NUM_THREADS=8)"; Fortran blas arms must import libopenblas.dll. Run-time stderr is the only stream the `error` rule reads (compile warnings such as ifx #5474 go to the build record).

- mt8 run env changed (coordinator 2026-10-17): unbound -- OMP_NUM_THREADS=8, OMP_DYNAMIC=FALSE, KMP_AFFINITY=disabled; OMP_PLACES/OMP_PROC_BIND removed. This changes the build-relevant config hash, so mt8 (and all) smoke evidence recorded before it is stale and must be re-recorded.

- The runner must run `smoke --regimes all` (smoke evidence for every planned cell) before
  `bench`; today only serial-licensed-native evidence exists.
- `gram` blas regime: the expert dsyrk source is assumed to be `expert_blas.f90`
  (`blas_sources`); not present yet, so those arms are `unbuilt`.
- Memory control is now saxpy gfortran-expert serial-repro-native at `sizes.control` (n = 700,001), the README choice; it needs a `saxpy/control` pin (until then it runs unvalidated and the session is flagged).
- Blade FP knob mapping: the post-rebuild table (licensed = knobs unset, repro = `BLADE_FP_REPRO=1` + `BLADE_FP_REASSOC=0`) sits in config.json `blade_knobs._flip_after_rebuild`; swap it in only when the coordinator confirms the new binary. Smoke evidence is keyed on the build-relevant config subset (flags, knobs, run_env, tolerance; comments excluded), so the flip invalidates old evidence automatically while edits to controls do not.
- Pins: once a pin carries `sizes`, a mismatch with kernel.json is refused (already implemented in `Pins.get`).
- Blade.exe rebuild with the QPC/steady_clock runtime clock (M14) -- re-run smoke after it
  (Blade.dll hash is part of the smoke evidence, so stale evidence is refused automatically).
- ifx OpenMP jitter: diagnosed by FORT as libiomp5 binding; resolved by running mt8 unbound.
