# Round 3 -- research-agent preamble (shared by items A-D)

You are a research agent in round 3 of Blade's microkernel work. This round is RESEARCH ONLY and
the box is TIMING-SENSITIVE: a harness A/B is running on it right now. Therefore:
- Do NOT build (`dotnet build`), do NOT run `blade test`, do NOT run `blade run` or any bench
  timing, do NOT spawn g++. You may run `blade check` / `blade plan` / `blade emit` (no C++
  compile) at most a handful of times, one at a time, on small programs, using the existing
  binary `C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran\bin\Release\net10.0\Blade.exe`
  with `C:\msys64\ucrt64\bin` first on PATH (PowerShell tool), from a private cwd under your
  scratch area. Reading source and emitted code is the main method.
- Do not edit any file under the repository; write ONLY your brief to the output path your
  task names (under the scratchpad round3/research/ folder).

## Where things are

- Repo: `C:\Users\cdupu\Documents\GitHub\Blade`; the round-2 integration branch is
  `worktree-bench-fortran` at e64fef3b (15 linear commits over master 4136f0b5), checked out at
  `C:\Users\cdupu\Documents\GitHub\Blade\.claude\worktrees\bench-fortran`. Read `CLAUDE.md` there
  first. Read-only siblings: `.claude\worktrees\magical-cannon-db55bf` (wave 3, in progress:
  recur mt8 threading, stencil mt8 attribution; its `bench/RESULTS.md` section 16 and
  `bench/notes/wave3/`), `.claude\worktrees\vigilant-hellman-c521dc` (effect-analysis
  follow-ups G45-G50, done, unmerged).
- Record of rounds 1-2 (what already landed and how it measured -- do NOT re-propose it):
  `bench/RESULTS.md` sections 13, 14.8 and 15 (ratio = expert / Blade, > 1 Blade faster;
  15.3 and 15.3b are the round-2 A/Bs), `bench/notes/gap-register.md` (G1-G47; open: G40,
  G47 and whatever wave 3 / the spin-offs leave), `bench/notes/round2-microkernel-plan.md`
  (the staged plan and its execution log; section "not done" lists the deferred items),
  `bench/notes/round2/brief-*.md` (the five round-2 briefs: scatter, epilogues, sibling folds,
  tile runtime, parallel permission) and `codex-review-*.md` (their attack reviews),
  `src/microkernels/README.md` (measured NEGATIVE evidence: refuted schedules).
- Planning input (external, read-only): `C:\Users\cdupu\Documents\Blade-kernel-planning-2026-10-19\`
  (README.md priorities; existing-kernels-review.md; optimization-architecture.md;
  deferred-region-planning.md; region-*.md and whole-region-blueprints.md (the region /
  relational track); new-kernel-catalog.md (N01-N10 + 35 more); group-theoretic-region-plan.md,
  orbit-algebra-review.md, orbit-schedule-catalog.md, orbit-review-decisions.md (the strict
  symmetry / orbit track); research-*.md (sources)). It was written against bench-fortran at
  5957c867; rounds 1-2 since landed everything in RESULTS 13-15. Its file:line hooks have
  DRIFTED -- verify every hook against current source and report the current location.

## Hard constraints every proposal must respect (unchanged from round 2)

1. Every kernel's NAMED outputs stay stored; only compiler-inserted temporaries may disappear.
2. Strict / repro (`BLADE_FP_REPRO=1`, `where repro`): every fold chain keeps its exact seed and
   operand order. Licensed forms need the SOURCE licence; never transfer a map licence to a
   reduction; never union omp flags across joined legs (the G7 explicit-request policy).
3. The C++ lane, the LLVM lane and the interpreter are differential twins; say which lanes you
   touch and how they stay byte-identical under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`.
   Optimizer-level changes need an `optimizerGates` knob for `blade test opt-diff`.
4. EVERY scheduling / reordering admission must consult the shared transitive write analysis
   (`Optimize.scheduleMayWrite` / `scheduleMayWriteIn`, and `callbackTable` for parallel bodies)
   for the producer kernel, the consumer kernel, the outer fold kernel, the seed and the
   intervening bindings -- round 2 spent nine review rounds closing holes of that class; name
   the admission predicate for each new pass.
5. Any A/B knob: name, default, registration (`Optimize.fs optimizerGates` or
   `CliSelfTests.fs suiteClearedKnobs`), a CLAUDE.md env-table row, a `blade plan` record.
6. Pins: corpus tests carry `// EXPECT:` pins; `// WARN:` pins are strict both ways.
7. Benchmark discipline: no power-of-two extents; interleaved A/B with knob-off mirrors
   (the layout is in `bench/notes/round2/w2-scripts/`); medians with CIs; OLD = e64fef3b.

## Your brief (markdown, 250-450 lines) MUST contain

1. **Hook verification** table (plan claim -> current `file:function:line` -> holds / changed /
   gone, with evidence).
2. **The transformation(s) or workloads, precisely** -- recognized IR shape, legality (strict vs
   licensed), refusal conditions (aliases, effects, seeds, extents), lanes touched, the write-
   analysis admission (constraint 4).
3. **Target cells** -- kernel x regime with the current ratio from RESULTS 15.3 / 15.3b (or
   "new kernel"), and the mechanism by which work disappears (operation / byte counts).
4. **Implementation design** -- files + functions (current lines), new IR / plan records, knob,
   tests (corpus files + pins; which `blade test <key>` block), interpreter twin, docs rows.
5. **Effort (S/M/L), risks, and the one experiment that should make us DROP it.**
6. **File-overlap list** against the other round-3 items (A census / remaining losses,
   B relational region track, C N01-N10 workloads, D orbit / strict-symmetry region plan).
7. **Open questions for Codex** (the attack reviewer) -- the claims you are least sure of.
