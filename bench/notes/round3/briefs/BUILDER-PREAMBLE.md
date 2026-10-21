# Round 3 -- builder preamble (shared by every wave-1 item)

You are a builder in round 3 of Blade's microkernel work. Blade: an array-functional language whose F# compiler emits
C++17 (g++ ucrt64) and LLVM, with a tree-walking interpreter as a differential twin. Read `CLAUDE.md` in your worktree
first; it is authoritative on building, testing, pins and style.

## Where you work

- Your own git worktree (the orchestrator created it) cut from the integration branch `worktree-bench-fortran` (the tip
  that passed the round-2/wave-3 gates). Commit there; never push; never touch another worktree.
- Build: PowerShell tool, `C:\msys64\ucrt64\bin` FIRST on PATH, `dotnet build Blade.fsproj -c Release` in your worktree;
  the binary is `bin\Release\net10.0\Blade.exe`. Run `blade test <key>` blocks from YOUR worktree root only (the scratch dir
  is cwd-relative; never two test processes from one directory). The Bash tool cannot spawn g++.
- Record: `bench/RESULTS.md` sections 13-16 (ratio = expert / Blade, > 1 Blade faster), `bench/notes/gap-register.md`
  (G1-G53; G51-G53 are wave 3's), `bench/notes/round2-microkernel-plan.md`, `bench/notes/round2/brief-*.md` and
  `codex-review-*.md` (round-2 designs and attack reviews), `src/microkernels/README.md` (refuted schedules).
- Round-3 research: the brief for your item and its Codex attack review are quoted in your task; the full files are under
  `C:\Users\cdupu\AppData\Local\Temp\claude\C--Users-cdupu--claude-spares-spare-dUbXep\dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b\scratchpad\round3\{research,reviews}\`
  (read-only). The plan is `...\round3\PLAN.md`.

## Hard constraints (every one is checked by the Codex diff review)

1. Named outputs stay stored (bench protocol, `bench/README.md`); only compiler-inserted temporaries may disappear.
2. Strict / repro (`BLADE_FP_REPRO=1`, `where repro`): every fold chain keeps its exact seed, operand order and evaluation
   count; never drop or move a seed; never reassociate a strict chain; a licensed form needs the SOURCE licence and never
   inherits one from a map or another leg.
3. The C++ lane, the LLVM lane and the interpreter are twins: say which lanes you touch; `blade test interp` / `opt-diff` /
   `llvm` must stay green; byte identity under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`.
4. EVERY new scheduling / reordering admission consults the shared transitive write analysis
   (`Optimize.scheduleMayWriteIn programFuncs modul`, and `callbackTable` for parallel bodies) for ALL FIVE parties:
   producer kernel, consumer kernel, outer fold kernel, seed(s), intervening bindings -- an unresolved callee counts as a
   write; a writing party DECLINES (serial as-written) or refuses BL4005 when the source asked for omp on a racing body.
   Write the five-party matrix into the `blade plan` decision record and the gap-register row.
5. First-failure equivalence: a BL8003 / BL8006 / BL8013 panic fires at the same cell with the same partial output as the
   as-written program; stores of earlier named outputs happen before a later failure.
6. Knobs: name, default, registration (`Optimize.fs optimizerGates` for optimizer passes -- `blade test opt-diff` compares
   off vs on -- or `CliSelfTests.fs suiteClearedKnobs`), a CLAUDE.md env-table row, a `blade plan` record
   (`applied` / `declined -- <reason>`), and the census line `[omp] requested but emitted serial: ...` whenever a request
   is dropped.
7. Pins: corpus files carry `// EXPECT:` pins (type-spelled values), refusals `// ERROR: BLxxxx @ l:c`; `// WARN:` pins are
   strict both ways; emission needles go in the `tests/*.fs` block that owns the feature; new corpus files take the next
   free numbers (check `ls tests/corpus/<dir>`); never renumber existing files.
8. Line endings: `.fs` files are CRLF in the worktree (LF in the index), new corpus files LF; `git diff --check` clean.

## Deliverable

ONE commit per fix round (your first commit may be larger), message ending with
`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` (add your own model's line too if your session requires it).
Report: the commit id, the block footers (`<Block>: N passed, 0 failed` lines, verbatim), `git diff --check` result, what
you measured (if anything; one `blade run` A/B of your own program is fine, interleaved, medians), what you did NOT verify,
and every pre-existing defect you found (do not fix those outside your item; report them with a reproducer).
