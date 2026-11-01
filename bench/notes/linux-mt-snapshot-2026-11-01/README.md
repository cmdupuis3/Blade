# Linux multithread snapshot, 2026-11-01

Results: `bench/RESULTS.md` §18; cells in `bench/results/stats.csv` under label `cloud-xeon4`.

Not a harness session (`run.py` is Win32-bound, plan-cloud-bench-campaign.md 2.2). A quick
snapshot on a Claude Code cloud sandbox: 4 vCPU Intel Xeon (family 6 model 207), no SMT,
15 GB, Ubuntu 24.04, kernel 6.18. Commit 5ed8af20 (master), Blade.dll d041d86bd92e94d3.

- `mtsnap.py` -- run1: blade-cpp only. Two builds per kernel: `BLADE_OMP_THREADS=1` (the
  harness's serial arm), pinned to CPU 1 with `taskset`; the threaded build at
  `OMP_NUM_THREADS` 1..4, unbound. 9 rounds, arm order rotated per round, one warm-up discarded.
- `allsnap.py` -- run2: run1's Blade binaries plus gfortran-expert and ifx-expert, all 15 arms
  interleaved in the same rounds. Fortran flags are config.json's `licensed` set (+ `serial_extra`
  / `mt_extra`); ifx's in their Linux spellings, gfortran without `-fno-external-blas64`
  (gcc 13 lacks it; no external BLAS is linked). `qpc_shim.c` supplies the two Win32 timer
  calls `bench_support.f90` binds, over `CLOCK_MONOTONIC`. Runs use an unlimited stack rlimit
  in place of the Windows builds' 256 MB main-thread stack. The exact compile lines are in
  `run2/*-build-command.txt`.
- Toolchains: g++ 13.3, gfortran 13.3 (Ubuntu), ifx 2026.2.0 (conda-forge `ifx_linux-64`),
  .NET 10.0.114. The box runs gcc 16.2 / ifx 2026.1 on Windows.
- `summarize.py` / `summarize2.py` print the tables; `ingest.py` appends the stats.csv rows
  (idempotent). `plans/` holds `blade plan` output for each kernel's threaded build.
- The drivers hard-code `/home/user/Blade` and expect run1's build tree beside them.
