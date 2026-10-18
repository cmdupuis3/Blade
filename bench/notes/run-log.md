# Run log (runner agent; append-only)

Owner: the runner. Every command executed in the suite is recorded here with a timestamp,
the command, and the outcome. Nothing is edited after the fact; corrections are new entries.
Harness invocation: `C:\Python314\python.exe -P bench\harness\run.py ...` from the worktree root.

## 2026-10-17 20:02 -- power-state audit (read-only; nothing changed)

Commands: `powercfg /getactivescheme`, `powercfg /list`, `powercfg /q SCHEME_CURRENT SUB_PROCESSOR [PROCTHROTTLEMAX|PROCTHROTTLEMIN|PERFBOOSTMODE]`,
`(Get-CimInstance Win32_Battery).BatteryStatus`, `(Get-CimInstance Win32_Processor).Name`.

| item | value |
|---|---|
| CPU | AMD Ryzen 7 5800HS with Radeon Graphics |
| active scheme | **Silent** (GUID 64a64f24-65b9-4b56-befd-5ec1eaced9b3); others installed: Balanced (381b4222-...), ASUS Recommended (7a2a4f3a-...) |
| AC / battery | BatteryStatus = 2 (on AC) |
| max processor state (PROCTHROTTLEMAX) | AC **100 %** (0x64), DC 70 % (0x46)  -- NOT yet capped at ~80 % |
| min processor state (PROCTHROTTLEMIN) | AC **80 %** (0x50), DC 5 % |
| boost mode (PERFBOOSTMODE) | not exposed: `powercfg /q ... PERFBOOSTMODE` prints only the scheme header (setting hidden in this scheme) |

Observations for the user: on AC the Silent scheme currently has max 100 % and min 80 %. A cap at ~80 %
needs max <= 80; with min left at 80 the two coincide, which pins the state (acceptable, but min must
never exceed max). The harness records PROCTHROTTLEMAX/MIN/PERFBOOSTMODE at session start and per block.
Boost cannot be inspected from powercfg here; if a cap is wanted to defeat boost, note that on AMD
a max state < 100 % generally disables boost frequencies in practice but this is not verified here.

## 2026-10-17 20:05 -- `run.py list`

Command: `run.py list` (output saved to `bench/results/smoke/list.txt`). Exit 0.
Blade.dll sha256 (bin\Release\net10.0, written 2026-10-17 18:26:19): 6C663C49D05DBE7A403ACE8EF9640D9FF510C4089B62DC918A1A8751721DCBEE
(same prefix as harness-notes.md). `bench/.build` did not exist before this point (no smoke evidence yet).

Planned kernels (11): antisym, comoment3, gram, pairkernel (group A); fusedchain, maskagg, onepass, recur, stencil (B); dot, saxpy (C).
Regimes declared per kernel:
- six regimes (serial/mt8 x licensed/repro/fastmath, native): antisym, comoment3, fusedchain, onepass, dot, gram
- four (serial/mt8 x licensed/repro): pairkernel, recur, stencil, saxpy
- two (serial-licensed, serial-repro): maskagg (serial only by protocol)
- gram also: serial-licensed-blas, mt8-licensed-blas
Twins (inputonly, 6 per kernel): fusedchain, maskagg, onepass, stencil, dot, saxpy.
Known pre-conditions from `list`: gram `expert_blas.f90` MISSING for gfortran-expert and ifx-expert (blas regime) -> those arms will be `unbuilt`.
maskagg: expected_llvm = fallback (blade-llvm). no_parallel_form (Blade mt8): onepass, recur.
## 2026-10-17 20:03 -- smoke --regimes all STARTED
Command: C:\Python314\python.exe -P bench\harness\run.py smoke --regimes all  (console -> bench/results/smoke/smoke-all.console.txt)
Attempt 1 (20:03:18-20:03:57, 39.7 s): ABORTED after antisym serial-licensed + mt8-licensed (all 11 arm-runs ok, blade-llvm mt8 serial-only as designed).
Harness exception at the end of the antisym mt8 block: `PermissionError [WinError 5] Access is denied: ...session-200318-smoke\session.json.tmp -> session.json`
(run.py line 310 `os.replace` in write_json_atomic, called from checkpoint, run.py:1449, after run_block:1746). Classified: HARNESS DEFECT (non-retried
os.replace on Windows; a transient sharing violation, typically a scanner/indexer holding the target; first checkpoint of the session succeeded 3 times earlier).
Console saved as bench/results/smoke/smoke-all-attempt1-aborted.console.txt. Re-running unchanged (a retry, not a fix).

## 2026-10-17 ~20:20 -- coordinator notice: config.json mt8 run environment changed
The coordinator reported that harness config's mt8 run env changed while the second smoke sweep (started ~20:04, attempt 2) was running
(OMP_PLACES / OMP_PROC_BIND removed; KMP_AFFINITY=disabled added; threads now unbound). Instruction: finish the sweep as a problem-finding pass.
The sweep's evidence is STALE FOR `bench` BY CONSTRUCTION (config.json hash changed mid-sweep; mt8 cells built/run before and after the change used different
environments). A full `smoke --regimes all` re-run is required after the announced Blade.exe rebuild anyway. ifx mt8 jitter seen at smoke size is to be
reported as the thread-binding effect the new env removes, not as a defect.

## 2026-10-17 20:04-20:26 -- smoke --regimes all, attempt 2 (COMPLETED, exit 0: 0 failures, 0 flags)
Command: `C:\Python314\python.exe -P bench\harness\run.py smoke --regimes all` ; console: bench/results/smoke/smoke-all.console.txt (2039 lines, 1326 s).
Session dir: bench\.build\sessions\2026-10-17-LAPTOP-350H6LDB\session-200416-smoke (session.json, manifest.json, summary.md); the harness's own table is in the console file tail.
STALENESS (found afterwards from file mtimes): this sweep ran WHILE other agents edited inputs, so it is a problem-finding pass only and not bench evidence:
- bench\harness\config.json and run.py rewritten 20:16:08 (mt8 env now unbound, KMP_AFFINITY=disabled); this process had loaded the OLD config at 20:04, so every mt8 cell ran with OMP_PLACES=cores OMP_PROC_BIND=close.
- bench\kernels\recur\expert.f90 rewritten 20:04:53 (before recur was built, so the new one was probably smoke-tested);
  bench\kernels\gram\expert_blas.f90 CREATED 20:11:15 (gram's blas block ran 20:09 => arms `unbuilt` at that time, as `list` said);
  bench\fortran\build_gfortran.ps1 rewritten 20:12:51 (mid-sweep: kernels built after this time used a different wrapper than those before);
  kernels/*/spec.md rewritten 20:17.
- A full `smoke --regimes all` re-run is needed after the Blade.exe rebuild anyway.
Earlier same-day harness sessions in the same tree (session-184743 ... 200301-check) were not mine.
Attempt-1 abort (os.replace PermissionError) did NOT recur in attempt 2.

### Smoke attempt 2: per-regime result (arm-level, from session-200416-smoke/session.json; counts include inputonly twins)
| regime | ok | DQ/checksum | fallback | unbuilt | build fail | gated | serial-only (blade-llvm) | no-parallel-form | no-blas |
|---|---|---|---|---|---|---|---|---|---|
| serial-licensed-native | 101 | 0 | 1 (maskagg blade-llvm, expected) | 0 | 0 | 0 | - | - | - |
| mt8-licensed-native | 72 | 0 | 0 | 0 | 0 | 0 | 15 | 3 (onepass blade-cpp + its twin, recur blade-cpp) | - |
| serial-repro-native | 101 | 0 | 1 (maskagg, expected) | 0 | 0 | 0 | - | - | - |
| mt8-repro-native | 72 | 0 | 0 | 0 | 0 | 0 | 15 | 3 | - |
| serial-fastmath-native | 54 | 0 | 0 | 0 | 0 | 0 | - | - | - |
| mt8-fastmath-native | 43 | 0 | 0 | 0 | 0 | 0 | 9 | 2 (onepass blade-cpp + twin) | - |
| serial-licensed-blas (gram) | 3 (blade-cpp, gfortran-natural, ifx-natural) | 0 | 0 | 2 (gfortran-expert, ifx-expert: expert_blas.f90 missing AT THAT TIME) | 0 | 0 | - | - | 1 (blade-llvm) |
| mt8-licensed-blas (gram) | 2 (the two naturals) | 0 | 0 | 0 | 0 | 2 (the two experts, gated on unbuilt serial) | - | 1 (blade-cpp: BLAS route has no thread construct) | 1 (blade-llvm) |
Total manifest cells 580: validated 448, unsupported (regime not declared) 76, serial-only 39, no-parallel-form 9, no-blas 2, unbuilt 2, gated 2, fallback 2. Zero DQ, zero build failures, zero unexpected fallbacks, no flags.
Other regimes (repro/fastmath x blas) are `unsupported` for every kernel (declared only for gram licensed).

### Classification of non-green / surprising cells
1. Attempt 1 abort, PermissionError on os.replace(session.json.tmp): HARNESS DEFECT (transient Windows sharing violation, no retry in write_json_atomic run.py:310); did not recur.
2. gram expert_blas.f90 MISSING -> gfortran-expert / ifx-expert `unbuilt` in serial-licensed-blas, `gated` in mt8-licensed-blas: EXPECTED at that moment (file was created 20:11, after the block ran); harness treats unbuilt as non-failing (no --strict-unbuilt), so exit 0 hides it. Re-smoke needed.
3. maskagg blade-llvm `fallback: a compound-index initializer` (serial licensed + repro): EXPECTED BY PROTOCOL (kernel.json expected_llvm).
4. blade-llvm `serial-only` in mt8 (15/15/9): EXPECTED BY PROTOCOL (no OpenMP in LLVM lane).
5. onepass / recur blade-cpp mt8 `no-parallel-form`: EXPECTED BY PROTOCOL (kernel.json no_parallel_form). Note recur blade-cpp serial-only fold form shows `reordered (3 SIMD_REDUCTION)` in licensed.
6. gram blas regime: blade-llvm `no-blas`: EXPECTED BY PROTOCOL (LLVM lane has no BLAS route). blade-cpp mt8-blas `no-parallel-form` ("mt8 build contains no thread-parallel construct"): the BLAS call is threaded inside OpenBLAS, not by a pragma, so the harness will not time this cell: HARNESS DESIGN QUESTION (mt8-licensed-blas Blade cell is never timed unless the check is relaxed for the blas regime).
7. gram blas regime also lists gfortran-natural / ifx-natural as ok arms: they take no library route (matmul); whether natural belongs in the blas regime table is a harness/kernel.json decision (regime_arms), flagged only.
8. dot ifx-natural mt8: ifx warning #5474 "Statement in an OpenMP PARALLEL WORKSHARE construct..." (src/dot.f90:33), all three mt8 regimes: KERNEL SOURCE DEFECT (Fortran natural, dot) -- the statement is not parallelized by ifx workshare (likely executes in one thread); owner: fortran agent. Full text in console lines 1154/1225/1296.
9. ifx mt8 jitter at smoke size (old config: OMP_PLACES=cores, OMP_PROC_BIND=close): ifx mt8 median 2.6 ms (min 2.0, max 70 ms; n=53 non-twin runs) vs gfortran 1.0 ms (max 3.3) and blade-cpp 0.87 ms (max 1.4). Runs >5 ms (all ifx, none other): fusedchain 6.6-21.3 (natural/expert, 3 regimes), onepass 70.0 (natural), 23.0 (expert), 28.6/28.2 (expert twin), pairkernel 17.2/8.7, recur 37.2/21.0 (natural), stencil 8.4/17.5, antisym 9.2, gram 5.0/7.3, saxpy 7.9/27.9 (twin). This is the binding effect the coordinator says the new unbound environment removes: reported, NOT classified as a defect. Must be re-looked at after re-smoke.
10. serial times at smoke size are 0.005-0.25 ms (stencil/antisym/recur ~0.01 ms), i.e. at or below timer-noise scale; nothing suspicious in serial arms (no run >0.25 ms except maskagg blade-cpp 1.2 ms, which builds a hashed compound index).

### Spot checks (run.py check, serial-licensed-native, smoke size; consoles in bench/results/smoke/check-gram.console.txt, check-dot.console.txt)
Pins (bench/validation/pins-A.json gram / pins-B.json dot, smoke): gram checksum 237924.7174130447, input 952.4563073534518; dot checksum 7442.487571015026, input 29915.989193804096.
- gram: blade-llvm/blade-cpp 237924.717413045 (15 digits, = pin); gfortran-natural ...304471E+5, expert ...304459E+5, ifx-natural ...304474E+5, ifx-expert ...304459E+5 -- all within ~1e-15 relative of the pin; input_checksum 952.45630735345185 in all Fortran arms, 952.456307353452 in Blade = pin. Timings 40-75 us, nothing suspicious. Fold forms: llvm 5 reassoc flags, cpp 5 SIMD_REDUCTION.
- dot: blade 7442.48757101503, gfortran-natural/ifx-natural ...150345E+3, gfortran-expert ...150218E+3, ifx-expert ...150227E+3 -- all within 1.2e-15 relative of the pin; input 29915.9891938041 / 2.9915989193804096E+4 = pin. All six inputonly twins print checksum 0.0 with the pinned input_checksum. Timings 0.11-0.25 ms; twin ~ total (kernel share negligible at smoke).

## 2026-10-17 ~20:30 -- coordinator: rebuild window
Coordinator notice: the compiler's FP-default flip is being merged into the bench branch and Blade.exe will be rebuilt. Instruction: run NOTHING (no smoke, no check)
until the coordinator sends "rebuild done" with the new knob mapping; the harness config will then be flipped and `smoke --regimes all` must be re-run before anything else.
Runner is idle from this entry. No commands executed after this point until that message.

## 2026-10-17 (after 22:10) -- coordinator: "rebuild done", HOLD continues
Blade.dll 74B67280... (bench branch merge 957afd95). Flipped config.json knob mapping: licensed = NO Blade fp knob (new compiler default);
repro = BLADE_FP_REPRO=1 (+ BLADE_FP_REASSOC=0); fastmath = same Blade knobs as licensed. Unbuilt is now a failure by default.
Hold continues until the coordinator sends "go smoke" (the two Blade validation agents are re-running their checks first). Then: `smoke --regimes all`, report as before.
Nothing executed since the previous entry.

## 2026-10-17 $(date) -- "go smoke" received
Coordinator: both Blade validation agents clean on Blade.dll 74B67280... Running `smoke --regimes all` (console -> bench/results/smoke2/smoke-all.console.txt), then check gram, check dot.
(log entry above timestamp: 2026-10-17 22:22; Blade.dll sha256 prefix 74b6728036b170d9 verified)

## 2026-10-17 22:22-22:46 -- smoke --regimes all on Blade.dll 74B67280 (new FP default; flipped config) -- COMPLETED, exit 0, 0 failures, 0 flags
Command: `C:\Python314\python.exe -P bench\harness\run.py smoke --regimes all` (1285 s). Console: bench/results/smoke2/smoke-all.console.txt.
Session: bench\.build\sessions\2026-10-17-LAPTOP-350H6LDB\session-222235-smoke. Follow-ups: `check gram`, `check dot` (bench/results/smoke2/check-*.console.txt), both exit 0.
Manifest 576 cells: validated 449, unsupported (regime not declared) 76, serial-only 39, no-blas 2, fallback 2, no-parallel-form 8. unbuilt 0, gated 0, DQ 0, build failures 0, compiler-diagnostic notes 0.
Per regime (arms incl. twins): serial-licensed-native ok 101 + 1 fallback (maskagg llvm, expected); mt8-licensed-native ok 72, serial-only 15, no-parallel-form 3 (onepass cpp + twin, recur cpp);
serial-repro-native ok 101 + 1 fallback; mt8-repro-native ok 72, serial-only 15, no-parallel-form 3; serial-fastmath-native ok 54; mt8-fastmath-native ok 43, serial-only 9, no-parallel-form 2 (onepass cpp + twin);
serial-licensed-blas (gram) ok 3 (blade-cpp, gfortran-expert, ifx-expert), no-blas 1 (blade-llvm); mt8-licensed-blas ok 3, no-blas 1. (Naturals no longer in the blas regime; expert_blas.f90 now builds and validates.)
Changes vs the 20:04 sweep: expert_blas arms now ok (were unbuilt/gated); gram blade-cpp mt8-blas now ok with "library threads (OPENBLAS_NUM_THREADS=8)" (was no-parallel-form); recur blade-cpp mt8 now no-parallel-form in licensed AND repro (as announced); dot ifx-natural #5474 warning GONE;
repro-mt8 Blade arms read "openmp (1 construct(s)); threaded maps, serial folds (BLADE_FP_REPRO)" with fold form "in-order"; licensed/fastmath Blade arms read "reordered" (reassoc flags / SIMD_REDUCTION), i.e. the flipped default is visible in the artifacts.
ifx mt8 timings (new unbound env), non-twin+twin n=79: min 1.65 ms, median 1.82 ms, max 2.77 ms; zero runs > 5 ms (old env: median 2.6 ms, max 70 ms, 27+ runs > 5 ms). gfortran mt8 median 0.92 ms (max 3.1, recur natural); blade-cpp mt8 (n=32 incl. blas) median 0.90 ms (OpenBLAS-threaded gram 0.093 ms).
ifx mt8 still has ~0.9 ms more fixed smoke-size overhead than gfortran (runtime team start-up), no jitter. Smoke-size mt8 times are start-up dominated for every compiler.
Spot checks (pins: gram 237924.7174130447 / 952.4563073534518; dot 7442.487571015026 / 29915.989193804096): gram all arms within 1.5e-16 rel (ifx-expert 2.3792471741304483E+5 worst); dot all within 1.3e-15 (blade-cpp 7442.48757101502, blade-llvm ...503, Fortran 7.442487571015022E+3..034E+3); input_checksums equal pins; six dot twins print checksum 0.0.

## 2026-10-17 23:13 -- GO PILOT received; power re-verification FAILED, pilot NOT started
Commands: `powercfg /getactivescheme`; `powercfg /q SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX|PROCTHROTTLEMIN`; `(Get-CimInstance Win32_Battery).BatteryStatus`.
Active scheme: Silent (64a64f24-65b9-4b56-befd-5ec1eaced9b3). BatteryStatus = 2 (AC).
PROCTHROTTLEMAX: AC 0x64 = **100%** (DC 0x46 = 70%).  PROCTHROTTLEMIN: AC 0x50 = 80% (DC 5%).  => the cap is NOT 80% on AC in the active scheme.
Blade.dll sha256 prefix 74B6728036B170D9 (unchanged since smoke).
Per the coordinator's instruction (if the cap is not 80 on AC, stop and tell), no pilot or any other run was started. Standing by.

## 2026-10-17 23:16 -- GO PILOT, UNCAPPED (user chose to run at current power settings)
Session label: **uncapped: max processor state 100% (Silent scheme)**. The 80% precondition was waived by the coordinator on the user's decision.
Power at start: scheme Silent (64a64f24-65b9-4b56-befd-5ec1eaced9b3); PROCTHROTTLEMAX AC 0x64 (100%), DC 0x46 (70%); PROCTHROTTLEMIN AC 0x50 (80%), DC 0x05 (5%); BatteryStatus 2 (AC). Blade.dll 74B6728036B170D9.
No other compute processes running (only idle Codex bridge python processes). Thermal and AC aborts armed in the harness.
Command: `C:\Python314\python.exe -P bench\harness\run.py bench --regimes serial-licensed-native --rounds 3 --size full`
Console: bench/results/pilot/pilot-serial-licensed.console.txt. Abort policy: no retry on abort, report.

## 2026-10-17 23:16-23:39 -- PILOT serial-licensed-native, full size, --rounds 3 (UNCAPPED: max processor state 100%, Silent scheme)
Command: `C:\Python314\python.exe -P bench\harness\run.py bench --regimes serial-licensed-native --rounds 3 --size full` (no separate pilot flag exists; `--rounds 3` taints every block "below the minimum", intended).
Console: bench/results/pilot/pilot-serial-licensed.console.txt (exit 3 = flags only). Bundle: bench/results/2026-10-17-LAPTOP-350H6LDB/session-231651/ (session.json, summary.md, manifest.json, build/, sources/).
Outcome: ran to completion, no abort, 0 failures, 18 flags (11 taint-by-rounds, 7 control-drift "inference suppressed": comoment3, pairkernel, fusedchain, maskagg, onepass, recur, dot). Session wall 1349 s; per-block wall 77-145 s (6-arm kernels ~80 s, 12-arm twin kernels ~140 s).
Longest single run 0.801 s (antisym gfortran-natural); no run near the 2.5 s cap. Peak working set over 64 MB: fusedchain gfortran-natural 65.9 MB, fusedchain ifx-natural 65.5 MB (everything else <= 55.7 MB).
Compute control (recur/gfortran-expert) drifted 12.8-13.1 ms (first 4 blocks) -> 14.2-14.8 ms (fusedchain..onepass, +11%) -> 13.2-14.4 (recur, stencil) -> 15.2-18.5 ms (dot) -> 13.05-13.3 ms (saxpy): non-stationary clock/thermal behaviour at 100% max state. Details in the report to the coordinator.
Pilot data are n=3 per arm: intervals are not publishable; ratios and verdict labels are indicative only.

## 2026-10-18 04:22 -- GO FULL MATRIX (uncapped: max processor state 100% (Silent scheme)); README minimum rounds, full size, no --rounds override
Plan (coordinator): 1a serial-licensed {gram,comoment3,antisym,pairkernel,recur,stencil}; 1b serial-licensed {fusedchain,onepass,maskagg,dot,saxpy}; 2 mt8-licensed; 3a/3b serial-repro (same split); 4 mt8-repro; 5 serial-fastmath, mt8-fastmath; 6 gram blas (serial+mt8).
Between sessions: 5 min idle, AC + no-other-compute check logged. Stop conditions: harness abort (thermal/AC) or drift-control suppression in > half the blocks of a session -> stop and report.
Pre-session check 04:22: BatteryStatus 2; Silent; PROCTHROTTLEMAX AC 100%; only idle Codex-bridge python processes running.
[session 1a start 04:22:22]

## 2026-10-18 04:22-04:55 -- SESSION 1a: serial-licensed-native {antisym, comoment3, gram, pairkernel, recur, stencil}, full size, README minimum rounds (11 / 11 / 11 / 11 / 21 / 21)
Label: uncapped: max processor state 100% (Silent scheme). Console: bench/results/full/serial-licensed-1a.console.txt (exit 3 = flags only). Bundle: bench/results/2026-10-18-LAPTOP-350H6LDB/session-042223/ (summary.md, session.json, manifest.json, build/, sources/).
Wall 1976 s (33 min; per block 4-11 min). 0 failures, no abort, no DQ, no fallback; 6 blocks, 5 SUPPRESSED by drift/trend checks (comoment3, gram, pairkernel, recur, stencil); only antisym unsuppressed. 5 flags.
Controls over 86 runs: compute (recur/gfortran-expert) median 13.42 ms, CV 9.0%, range 12.51-17.14 ms (baseline 12.75 ms); memory (saxpy/gfortran-expert) median 5.50 ms, CV 9.7%, range 5.12-7.45 ms.
Longest run 0.790 s (antisym gfortran-natural); no run near the 2.5 s cap; peak working set <= 55.7 MB (nothing over 64 MB).
STOP CONDITION HIT (drift control exceeds threshold in > half the blocks: 5 of 6). Per coordinator instruction the sequence is STOPPED here; sessions 1b onward were NOT started. Reporting to coordinator.

## 2026-10-18 -- coordinator: CONTINUE FROM 1b, UNCAPPED; drift suppression is recorded, not a stop
Label for every session: "uncapped: max processor state 100% (Silent scheme); verdicts suppressed by drift reported as indicative".
Stop conditions now ONLY: harness abort (thermal/AC), any checksum DQ, any build or run failure, any unexpected fallback (harness exit 1). Session 1a bundle (session-042223) stays the result for its six kernels.
Execution: sequential driver script (scratchpad driver.sh): per session pre-check (BatteryStatus==2, no other compute process; both logged to bench/results/full/progress.txt), 300 s idle between sessions, one bench at a time, stop on harness exit other than 0/3.
Sessions: 1b serial-licensed {fusedchain,onepass,maskagg,dot,saxpy}; mt8-licensed (all); serial-repro 3a {gram,comoment3,antisym,pairkernel,recur,stencil}, 3b {fusedchain,onepass,maskagg,dot,saxpy}; mt8-repro; serial-fastmath; mt8-fastmath; gram blas (serial-licensed-blas + mt8-licensed-blas).

## 2026-10-18 09:03-09:58 -- SESSION 1b: serial-licensed-native {fusedchain, onepass, maskagg, dot, saxpy}, 21 rounds each, full size
Label: uncapped: max processor state 100% (Silent scheme); verdicts suppressed by drift reported as indicative.
Pre-check 09:03:43: BatteryStatus 2, no other compute processes. Console bench/results/full/serial-licensed-1b.console.txt (exit 3). Bundle bench/results/2026-10-18-LAPTOP-350H6LDB/session-090344/.
Wall 3282 s (55 min). 0 failures, no abort/DQ; maskagg blade-llvm fallback (expected). 4 of 5 blocks suppressed (fusedchain, maskagg, dot, saxpy; all by within-block TREND checks, controls did not trip); onepass unsuppressed.
Controls over 105 runs: compute median 13.05 ms CV 9.4% (12.43-20.67 ms); memory median 5.40 ms CV 8.9% (5.13-8.28 ms). Longest run 0.083 s (maskagg blade-cpp). Peak WS > 64 MB: fusedchain gfortran-natural 65.9, ifx-natural 65.5 (same as pilot).

## 2026-10-18 10:03-11:12 -- SESSION mt8-licensed-native (all 10 declaring kernels), full size -- HARNESS ABORT, sequence STOPPED
Label: uncapped: max processor state 100% (Silent scheme); verdicts suppressed by drift reported as indicative.
Pre-check 10:03:29: BatteryStatus 2, no other compute processes. Console bench/results/full/mt8-licensed.console.txt (exit 1). Bundle bench/results/2026-10-18-LAPTOP-350H6LDB/session-100329/ (partial but intact).
Wall 4114 s (68.6 min). Blocks completed: antisym, comoment3, gram, pairkernel, fusedchain, onepass, recur, stencil, dot (8 of those 9 suppressed; antisym unsuppressed). 8 flags.
ABORT in the 10th block (saxpy/mt8-licensed-native, round 7 of 21): "saxpy/mt8-licensed-native/blade-cpp-inputonly: run took 0.043s, 7.0x its running median 0.006s -- stopping (charter 2.5)". All other runs in that round were 5-11 ms and both controls normal (compute 12.7 ms, memory 5.4 ms): an isolated single-process stall in a threaded run, not a machine-wide excursion. Per the stop rule (harness abort) no retry was made; driver stopped. saxpy mt8 block has 6 of 21 rounds recorded (not a result). Remaining sessions NOT run: serial-repro 3a/3b, mt8-repro, serial-fastmath, mt8-fastmath, gram blas, and the saxpy mt8-licensed cell.
Controls over 168 runs: compute median 13.43 ms CV 9.5% (12.45-18.05 ms); memory median 5.47 ms CV 9.0% (5.09-7.87 ms). Peak WS > 64 MB in mt8: fusedchain gfortran-natural 66.4 MB, ifx-natural 68.0 MB.

## 2026-10-18 (after 11:12) -- coordinator: PAUSE at the next stopping point
The mt8-licensed-native session had already finished (it ABORTED at 11:12:04, see entry above) and the driver had already stopped itself; nothing was running when the pause was read and no further session was started.
Pending sessions NOT run: saxpy mt8-licensed-native cell (block aborted at round 7/21), serial-repro 3a {gram,comoment3,antisym,pairkernel,recur,stencil}, serial-repro 3b {fusedchain,onepass,maskagg,dot,saxpy}, mt8-repro, serial-fastmath, mt8-fastmath, gram blas (serial-licensed-blas + mt8-licensed-blas).
Runner standing by; awaiting the decision requested in the previous report (re-run saxpy only / relax the 3x abort for mt8 twins / continue).
