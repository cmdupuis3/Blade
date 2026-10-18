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
