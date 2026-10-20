# Round 2 microkernels -- research-agent preamble (shared by items A-E)

You are a research agent in round 2 of Blade's microkernel work. This round is RESEARCH ONLY:
do not edit any file under the repository; write ONLY your brief to the output path your task
names. A separate runner owns tests; do not run `blade test` suites or bench timings.

## Where things are

- Repo: `C:\Users\cdupu\Documents\GitHub\Blade`, branch master at 4136f0b5 (identical to
  `worktree-bench-fortran`, the round-1 integration branch). Read `CLAUDE.md` first.
- The repo is indexed by graft (MCP tools `mcp__graft__graft_find_code`,
  `graft_trace_calls`, `graft_file_api`, `graft_find_all`). Prefer them over grep to locate
  symbols and callers; load them in ONE ToolSearch call.
- A fresh Release build of master exists at `bin\Release\net10.0\Blade.exe`. You MAY run
  `blade plan` / `blade emit` / `blade run` on small programs or the bench kernels
  (`bench\kernels\<k>\<k>.blade`; `--verbose` keeps the build dir so you can read the C++).
  Rules: use the PowerShell tool for anything that spawns g++ (the Bash tool cannot), put
  `C:\msys64\ucrt64\bin` FIRST on PATH, set `BLADE_FP_REPRO=1` / `BLADE_OMP_THREADS` etc.
  per experiment, and run from a private cwd under your scratch area (the scratch dir
  `generated_cpp_tests/` is cwd-relative). Reading emitted C++/LLVM is the single best way
  to verify a plan claim.
- Planning input (external, read-only): `C:\Users\cdupu\Documents\Blade-kernel-planning-2026-10-19\`
  -- `README.md` (priorities table), `existing-kernels-review.md` (per-kernel table B,
  section A ordered-scatter proof, section C runtime design), `optimization-architecture.md`,
  `review-decisions.md`, `orbit-review-decisions.md`, `region-compiler-review.md`,
  `whole-region-blueprints.md`, `benchmark-evidence.md`, `orbit-schedule-catalog.md`.
  It was written against bench-fortran at 5957c867 (code merged through 2124edc4). Since
  then landed: 963b9aa1 (G37 context-aware Int64->Float64 conversion), 5d1a3814
  (`BLADE_BLAS_PREFER`), fc5ee91e (harness). Its file:line hooks MAY HAVE DRIFTED -- verify
  every hook you rely on against current source and report the current location.
- Round-1 record (what already landed and how it measured -- do NOT re-propose it):
  `bench/RESULTS.md` section 14.8 (final table, every regime; ratio = expert/Blade, >1 Blade
  faster), `bench/notes/gap-register.md` (G1-G38; G35-G38 are the open round-3 items),
  `bench/notes/phase2-microkernel-plan.md`, `bench/notes/microkernel-sketches-codex.md` +
  `microkernel-sketches-review.md` (S1-S14 verdicts), `src/microkernels/README.md`
  (measured NEGATIVE evidence: refuted schedules -- do not re-propose them),
  `bench/kernels/<k>/{spec.md,<k>.blade,expert.f90,kernel.json}` for your target kernels,
  `bench/README.md` (protocol: named outputs stay stored; serial vs mt8; licensed = Blade
  default, repro = `BLADE_FP_REPRO=1`).

## Hard constraints every proposal must respect

1. Every kernel's NAMED outputs stay stored (benchmark protocol); only compiler-inserted
   temporaries may disappear.
2. Strict / repro (`BLADE_FP_REPRO=1`, and `where repro`) means every fold chain keeps its
   exact seed and operand order: no multiplicity weights, no reassociation, no flattening of
   nested sums. Licensed forms need the SOURCE licence (`where omp` on the fold kernel /
   builtin commutative op); never transfer a map licence to a reduction, never union omp
   flags across joined legs.
3. C++ lane, LLVM lane (`src/EmitLlvm.fs`, `src/cpp/blade_llvm_*`), and the interpreter
   (`src/Interp/`) are differential twins (`blade test interp`, `opt-diff`, `llvm`). Say which
   lanes you touch and how the twins stay byte-identical under
   `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`. Changes that happen in `src/Optimize.fs` (before
   the lane split) are invisible to `test interp` -- they need an `optimizerGates` knob so
   `blade test opt-diff` checks off-vs-on.
4. Any A/B knob: name, default, registration in `Optimize.fs optimizerGates` or
   `CliSelfTests.fs suiteClearedKnobs`, a row in CLAUDE.md's env table, and a `blade plan`
   decision record (`applied` / `declined -- <reason>`).
5. Pins: new corpus tests need `// EXPECT:` pins; `// WARN:` pins are strict both ways; emission
   needles live in `tests/*.fs` blocks (e.g. `blade test linalg`, `syrk-tile`, `optimize`).
6. Benchmark discipline: no power-of-two extents, interleaved A/B, medians. Say what the
   harness A/B is (kernel x regime x arm), with OLD = the 4136f0b5 binary.

## Your brief (markdown, aim for 250-450 lines) MUST contain these sections

1. **Hook verification** -- table: plan claim -> current `file:function:line` -> holds /
   changed / gone, with one line of evidence (what you read in source or emitted code).
2. **The transformation(s), precisely** -- recognized input shape (IR node / pattern),
   legality argument (strict vs licensed), refusal conditions (aliases, effects, seeds,
   extents, coordinate-dependent summands, etc.), lanes touched.
3. **Target cells** -- bench kernel x regime, current ratio from RESULTS 14.8, and the
   mechanism by which work disappears (operation / byte counts, not speedup forecasts). If
   you read emitted code for a target kernel, quote the loop(s) that today do the work you
   intend to remove.
4. **Implementation design** -- files + functions to change (current line numbers), new IR /
   plan records, knob (name, default, where registered), tests to add (corpus files with the
   pins they would carry; which `blade test <key>` block gets needles), interpreter-twin
   handling, docs rows.
5. **Effort (S/M/L), risks, and the one experiment that should make us DROP it.**
6. **File-overlap list** against the other round-2 items (below): which files/functions you
   would touch that another item also touches.
7. **Recommended wave** (1 = first, parallel-safe; 2 = after wave-1 measurements; 3 = later)
   with the A/B protocol.

## Round-2 items (for overlap flagging)

- **A** ordered orbit scatter (strict rank-2 / rank-3 consumer of a stored symmetric pool) +
  G35 parallel complete-row repro folds (mt8 repro).
- **B** store/cell epilogues: rank-1 producer epilogue (saxpy y2 checksum), cell epilogue for
  stencil / recur beyond the landed hot-row pass, licensed compact-producer checksum epilogue
  (pairkernel, sketch S11), maskagg compaction-payload + fold fusion.
- **C** sibling-fold traversal sharing (dot / fusedchain / onepass input checksums share the
  kernel's pass) + a V1 automatic sibling-fold discovery rule (natural spelling -> the
  existing reduction-join lowering).
- **D** tile runtime: persistent syrk team + work-weighted stable ownership, antisym row-group
  reuse with a cache-budget gate, comoment3 edge kernels / weighted prefix tasks, pairkernel
  mt8 team reuse (G32's open direction).
- **E** G5 / G7 parallel-permission design: a `where omp` slot for a `let rec` inductive arm's
  row kernel (recur mt8), and a thread-level form for `<&!>` joins (onepass mt8).
