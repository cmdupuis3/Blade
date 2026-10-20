# Round-2 baseline summary (master 4136f0b5)

## Build
- bin\Release\net10.0\Blade.dll LastWriteTime 2026-10-19 23:11:09 (6 min old at check; no rebuild needed)
- SHA256 462B9D924F5D48B4BAF7F88CADC766DD78685F1BAA2F7CB61D19AEE0E07D19E1 (identical before and after the suite run)
- OLD snapshot: <scratch>\round2\old-bin-4136f0b5\ (3056 files, whole net10.0 dir); hash in gates\OLD-hash.txt (same value)
  Note: the snapshot (and the live bin) carries an EMPTY stale dir tests\corpus\lie (lie is only on unmerged feat/lie-stdlib-p0).

## Default suite (valid run)
- TOTAL: 6800 passed, 0 failed, 1 skipped    wall 6.88 min (23:29:25 -> 23:36:18)
- The 1 skip: Surface block "knowledge-base docs[] paths resolve in the repo tree -- no ./docs (running beside the binary)" (cwd has no docs/; benign)
- Emission-knob line printed: "test: ignoring ambient OPENBLAS_DIR=C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64 for this run (...)" (only that knob)
- Failures: none. Console: gates\baseline-default.txt ("IR fail" on (rejects) tests is the expected refusal spelling, not a failure)
- Run from C:\bt_r2base with a junction tests -> repo\tests (junction removed afterwards).

## Aborted first attempt (do not use) -- gates\baseline-default-attempt1-ABORTED.txt
Run from a plain private cwd in the scratchpad: no TOTAL line. Corpus Wiring threw BL9001 ICE ("multi-file corpus category ...bin\Release\net10.0\tests\corpus\lie has no test subdirectories"): without a ./tests in the cwd the harness falls back to the DEPLOYED corpus, which has the stale empty lie dir. 9 corpus + ~8 other cwd-relative reds (Optimization Layer, Halo Access x5, Provider Desugar, Surface) came from missing tests/corpus/... paths and the long scratchpad cwd (MAX_PATH). Lesson: private cwd must be SHORT and hold a `tests` junction to the repo's tests (memory: parallel-test-isolation, corpus-deployed-copy-staleness). Prior runs from the repo root would also hit the repo's tests, so no ICE there.

## Harness health (bench\harness\run.py list)
- 12 kernels, all arms "present": group A antisym, band, comoment3, gram, pairkernel; group B fusedchain, maskagg, onepass, recur, stencil; group C dot, saxpy.
- Sizes (smoke / full): antisym p23,n11 / p1201,n401; band n211,w5 / n20011,w101 (+narrow w11); comoment3 p13,n11 / p171,n201; gram p61,n31 / p1601,n1001 (+scale p2003); pairkernel n53 / n2503; fusedchain n20011 / n2000003; maskagg n30011 / n1000003; onepass n50021 / n5000011; recur nt11,nx211 / nt301,nx20011; stencil ny31,nx41 / ny1501,nx2003; dot n30011 / n3000017; saxpy n20011 / n2000003 (+control n700001).
- Arms: blade-llvm, blade-cpp, gfortran-natural/expert, ifx-natural/expert; most kernels also have -inputonly twins (fusedchain, maskagg, onepass, stencil, dot, saxpy); gram adds gfortran-expert (blas) / ifx-expert (blas).
- Regimes: <serial|mt8>-<licensed|repro|fastmath>-<native|blas>. Most kernels: all 6 native regimes; gram also serial/mt8-licensed-blas; maskagg only serial-licensed/repro; pairkernel, recur, stencil, saxpy only licensed+repro (serial+mt8). no_parallel_form (Blade mt8): onepass, recur.

## A/B invocation (bench\harness\README.md)
- Verbs: `C:\Python314\python.exe bench\harness\run.py list | check <kernel> [--regimes ...] | smoke [--regimes all] [--kernels a,b] | bench [--kernels ..] [--regimes ..] [--rounds N] [--size full|smoke]`. check/smoke: one validated run per arm at smoke size, evidence in bench\.build\smoke-evidence.json keyed on source/Blade.dll/config hashes; bench REFUSES without fresh passing smoke evidence for the current Blade.dll (so smoke --regimes all after any binary change). Exit 0 clean / 1 failure / 2 config / 3 flags only.
- OLD vs NEW: there is NO config key or flag for the Blade binary. run.py line 61 hardcodes BLADE_EXE = REPO_DIR\bin\Release\net10.0\Blade.exe, REPO_DIR = parent of bench\ (derived from __file__). The harness also refuses if a blade.toolchain.json sits beside Blade.exe. To A/B without editing: make a mirror root, e.g. <S>\ab-old\{bench\ (copy of repo bench incl. kernels, fortran, validation, harness), bin\Release\net10.0 = junction/copy of old-bin-4136f0b5}, run its run.py (separate results under its own bench\results), same Fortran arms rebuilt there. NOT tested yet. Alternative: swap bin contents (hash-check before/after).
- Drift/suppression: per round seeded random permutation of all arms + twins + 2 controls (compute: recur gfortran-expert serial-repro; stream: saxpy n=700001). Block flagged "within-block drift" if control first-half vs second-half median differs >5%; session flagged if rolling control median moves >7% vs first block. Flagged blocks keep numbers but every verdict prints `suppressed` ("indicative"). Excursion (>3x arm's running median once median >=5ms): first in block dropped+recorded, second or any control excursion or AC loss aborts. Verdict delta 10% (win/detectable-win/equivalent/detectable-loss/loss/inconclusive). Rounds: serial 11, mt8 7, 21 if median <50ms. Cooldowns 0.5s run / 3s kernel / 60s session; one timed process at a time; serial pinned to CPU 2; refuses on battery.

## Fortran toolchain
- gfortran 16.2.0 (MSYS2 ucrt64) works (GNU Fortran Rev4). clang64 clang.exe present (LLVM lane).
- ifx recipe paths all exist: vcvars64.bat at "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\" (fallback VS 18 BuildTools also exists), oneAPI vars.bat "C:\Program Files (x86)\Intel\oneAPI\compiler\latest\env\vars.bat", ifx.exe at ...\compiler\latest\bin\ifx.exe. ifx is not on PATH by design; bench\fortran\build_ifx.cmd sets SETVARS_CALL=1 + vcvars64 per build. Nothing built.
