# Round 2 microkernels -- builder preamble (shared by every implementation agent)

You are an implementation agent in round 2 of Blade's microkernel work. You work in YOUR OWN
git worktree (the harness created it from master 4136f0b5 on a `worktree-agent-*` branch).
Commit to that branch as you go (small, descriptive commits; end each message with
`Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`). Never touch another worktree,
never switch branches, never push. The orchestrator merges your branch into the integration
branch `worktree-bench-fortran`; a separate RUNNER agent owns full test suites and timing.

## Environment rules that bite (all learned the hard way)

- Read `CLAUDE.md` in your worktree first: repo layout, build, testing, env table, language
  essentials, the style guide.
- Build: `dotnet build Blade.fsproj -c Release` from your worktree root (PowerShell). The binary
  is `<worktree>\bin\Release\net10.0\Blade.exe`. `EnableDefaultItems=false`: a NEW .fs file
  must be added to `Blade.fsproj` by hand in dependency order (read the fsproj group comments).
- Anything that spawns g++ (`blade run/compile/test`, the C++ raw-bit blocks) must run from the
  **PowerShell tool** with `$env:PATH = "C:\msys64\ucrt64\bin;" + $env:PATH` (ucrt64 FIRST, or
  compiles fail silently; the Bash tool cannot spawn g++ at all). In a worktree the Bash tool
  also refuses heredocs / compound PATH+loop commands silently -- use the Edit/Write tools for
  source edits and PowerShell for running things.
- `blade test` writes `generated_cpp_tests/` RELATIVE TO THE CWD: run it from your worktree
  root only, one process at a time. Run TARGETED blocks (`blade test <key>`; bare `blade`
  prints every key) -- NOT the full suite, not `test interp` / `opt-diff` / `llvm` (the runner
  runs those at wave end). The harness EXITS 0 WITH FAILURES: read the printed
  `TOTAL: N passed, M failed[, K skipped]` line, and report the skipped count.
- BLAS: set `$env:OPENBLAS_DIR = "C:\Users\cdupu\tools\OpenBLAS-0.3.34-x64"` and put its `bin`
  on PATH when a block you run turns BLAS on (`blade test linalg`, `syrk-tile`).
- Line endings: the git INDEX is uniformly LF (`i/lf`) while `core.autocrlf=true` makes the
  working tree CRLF (`w/crlf`) -- for `.fs`, `.hpp`, `.h`, `.c`, `.md`, `.json` alike. Check
  `git ls-files --eol <file>` before and after: your edit must not flip a file's stored ending
  (`git diff --stat` showing the whole file changed = you flipped it; fix before committing).
- `tests/corpus/**` and `examples/**` are byte-pinned: never reformat them.
- Env knobs are read per call (functions, not module-level lets) so tests can pin/restore them
  mid-process. Keep it that way.

## Compiler-change rules

1. Differential twins: C++ lane (`src/CodeGen*.fs`, `src/cpp/*`), LLVM lane (`src/EmitLlvm.fs`,
   `src/cpp/blade_llvm_*`) and the interpreter (`src/Interp/`) must agree byte-for-byte under
   `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off`. A change made in `src/Optimize.fs` (before the lane
   split) is invisible to `test interp`: it MUST have a gate in `Optimize.fs optimizerGates` so
   `blade test opt-diff` compares off-vs-on. A change made in an emitter is visible to
   `test interp` (interpreter runs the original IR) and needs an env knob for A/B only.
2. Every new knob: `BLADE_<NAME>`, default documented, registered (optimizerGates or
   `src/CliSelfTests.fs suiteClearedKnobs`), a row in CLAUDE.md's env table, and a `blade plan`
   decision record (`[<rule> v1] <binding>: applied [...]` / `declined -- <reason>`). A declined
   decision is never silent (the omp-census invariant: a dropped licence always prints a line).
3. Diagnostics have stable `BLxxxx` codes (`src/Diagnostics.fs`); a new one has 5 touch points
   (code, message, docs/features, corpus pin, `blade explain`).
4. Strict means strict: every fold keeps its exact seed and operand order under repro; a
   licensed form needs the SOURCE licence on THAT fold (never transferred from a map, never
   unioned across join legs). Every kernel's NAMED outputs stay stored.
5. Tests you must add: corpus tests (`tests/corpus/<cat>/NNN_name.blade`, `// TEST:` first line,
   `// EXPECT:` pins spelled as the type prints -- `2.0` float, `2` int; a value test with no
   pin FAILS unless `// NOPINS: <reason>`), a decline case for each refusal, plan-line /
   emission needles in the owning `tests/*.fs` block (find the block that owns the neighbour
   feature and extend it), and -- for anything claiming bitwise -- a raw-bit block modelled on
   `blade test syrk-tile` / `fold-lanes` (both contraction modes).
6. Docs: CLAUDE.md env row; `docs/features.md` row if a user-visible feature; the plan doc the
   orchestrator names in your task.
7. Before you report: `git status` clean except your commits; list the `blade test <key>`
   blocks you ran with their TOTAL lines; list the files you touched; state what you did NOT
   verify. Report numbers you measured (with the command), never forecasts.

## Benchmark discipline (if your task includes a measurement)

Never time at power-of-two extents; interleave OLD/NEW; medians of >= 5; one process at a
time on the box (no build or test running concurrently); report the command and raw numbers.
The runner does the protocol A/B; your own numbers are directional only.
