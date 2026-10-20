# Loose ends after the follow-up round: brief for a second opinion

You are giving a second opinion on the loose ends left after the Blade-vs-Fortran
benchmark's follow-up round. Repository: this worktree
(`C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran`, branch
`worktree-bench-fortran`, final compiler tree `d3d5722b`, plus uncommitted round-3
results and write-ups). Read `CLAUDE.md` first for the project's rules.

## Ground rules

- **No commits, no `git add`, no branch changes, no stash.** The user asked for no commits.
- **Do not edit any tracked file.** Write your answer only to
  `bench/notes/loose-ends-codex.md` (new file). Save it incrementally: write the file after
  each item, so a dropped connection loses nothing.
- You MAY read anything, and you MAY run single programs to diagnose
  (`bin\Release\net10.0\Blade.exe run|emit|plan <file>`, with `C:\msys64\ucrt64\bin` first
  on PATH; use PowerShell, the Bash tool cannot spawn g++). Put any scratch files under
  `%TEMP%\codex-loose-ends\`. Do NOT run `blade test` (full or keyed) and do not run
  `bench/harness/run.py bench` sessions: the user's test runner owns those.
- If `C:\Users\cdupu\AppData\Local\Temp\claude\blade-machine\runner-window` exists, a timing
  window is open: run nothing at all, read only.
- Binaries available for A/B diagnosis (each a full build directory):
  - NEW (final tree d3d5722b): `bin\Release\net10.0\Blade.exe` in this worktree.
  - OLD (master's compiler, sources identical to f2315642):
    `C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu-Documents-GitHub-Blade\37548082-70f4-40cd-9b2d-59f490260b72\scratchpad\old-bin-fadccd74\Blade.exe`
  - One binary per follow-up branch, in its worktree's `bin\Release\net10.0\`:
    `.claude\worktrees\{g32-g34, band-vec, blas-repack, maskagg-compact, recur-dest, tri-tiles, fold-simd}`
    (note: recur-dest's binary is built from a merge that contains fold-simd; blas-repack's
    from a fast-forward of d3d5722b plus BLADE_BLAS_PREFER).

## Where to read the state

- `bench/RESULTS.md` §14 (the round being closed) and §14.8 (final table, remaining losses).
- `bench/notes/gap-register.md` (G1-G39; header "Follow-up round ... MEASURED (round 3)").
- `bench/results/full/RUNNER-RECORD.md`, section "Round 3" (every number; the authority).
- Bundles: `bench/results/2026-10-19-LAPTOP-350H6LDB/session-*/` (`session.json` has raw
  samples; `summary.md` per session; `build/<kernel>/.../plan.txt` and `generated/*.cpp|.ll`).
- Kernel sources: `bench/kernels/<k>/{spec.md, <k>.blade, inputonly.blade, natural.f90, expert.f90}`.
- Ratio convention: expert time / Blade time, so > 1 means Blade is faster.

## The loose ends

For each: say what you think the cause is (with evidence you checked, not guesses), what the
fix or decision should be, how big it is, and whether it belongs in this round or a later one.

1. **blade-cpp serial input-only twins got 28-34% slower** than in the pre-fix sessions
   (dot's twin 17.71 -> 23.09 ms), while blade-llvm twins and the mt8 blade-cpp twins are
   flat (gap G37; maskagg's twin +1 ms, G38, looks like one instance). This is the most
   likely real regression of the round. Find which merged change caused it: compare the
   `inputonly.blade` emission (`blade emit`, and timing with a few interleaved single runs) of
   dot (and saxpy) across OLD, NEW and the branch binaries. Suspects to check, not conclusions:
   fold-simd's asm barrier that rounds every product before the add (4e49f46a, in
   `src/cpp/blade_fold_lanes.hpp`) reaching the input checksum; band-vec's frame-scan /
   int64->double conversion changes; blas-repack's hot-row schedule; tri-tiles. Name the commit
   and the emitted-code difference.
2. **pairkernel mt8 stays at 0.80 / 0.80** (G32): threading the orbit-class pass (checksum-pass
   branch) did not move it. Where does the mt8 time go? Compare blade-cpp's generated code and
   plan for pairkernel mt8-licensed against the Fortran expert's threading; say what Blade does
   serially that the expert threads.
3. **mt8-repro losses** (G35: comoment3 0.48 / 0.43, pairkernel 0.37 / 0.36, band 0.30 / 0.52,
   saxpy/stencil/dot ~0.75-0.84). Blade's reproducible build keeps every fold one serial
   ordered chain and does not thread it. The Fortran experts' repro arms thread across cells
   while keeping each cell's own order. Is a "fixed per-thread partition, one chain per cell"
   form (as the tiles already do) sound under Blade's repro contract (byte identity with the
   interpreter under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`)? For which of these kernels
   is the fold a per-cell fold (partitionable without changing bits) versus one global
   chain (not partitionable without a licence)? Sketch the emitter change and its size.
4. **ifx beats Blade on fusedchain / onepass / dot / saxpy in repro and fastmath**
   (0.69-0.84, G36). The earlier diagnosis was that ifx fuses the kernel loop with the
   input-checksum loop. Verify that against the ifx expert's behaviour if you can (read
   `expert.f90`, the build flags in `bench/fortran/`), and say whether Blade's fusion could
   legally do the same in each regime.
5. **maskagg is the remaining serial loss** (0.87 / 0.63 licensed, 0.84 / 0.68 repro;
   blade-llvm 4.7 ms vs ifx 2.9 ms). Diagnose from the generated code.
6. **onepass and recur have no Blade mt8 form** (G39). Is that a licence the sources do not
   give (recur is sequential by nature; onepass?), or a missing emitter path?
7. **Small decisions** (give a recommendation each): keep `BLADE_TILE_TUNE anti_kc=0`
   default or switch to 200 (measured 1-4% better, within noise for llvm); keep
   `BLADE_FOLD_LANES` default K=8 when the suite shows no gain on whole kernels but the
   microbench shows 1.15-1.6x on cache-resident shapes; the `BLADE_BLAS_PREFER` branch
   (blas-repack 5d1a3814, tested green, unmerged): anything to fix before it merges.
8. **Write-up audit.** Spot-check RESULTS §14 and the gap register against RUNNER-RECORD's
   Round 3 tables: list any number, verdict label (live vs indicative) or claim that does
   not match the record, and any loss §14.8 omits.
9. **Anything else** you find left open: stale worktrees/branches, harness rough edges
   (e.g. a fold-simd timing script bug the runner already found), tests that should exist.

## Output

`bench/notes/loose-ends-codex.md`: one section per item above, each ending with a one-line
**Recommendation** and a size (small / medium / large) and "this round" or "later". Then a
short prioritized list at the top of the file: what to do before merging this round to master,
and what can wait.
