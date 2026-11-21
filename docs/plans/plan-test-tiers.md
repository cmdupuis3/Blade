# Plan: test tiers -- a fast integration suite in front of the unit suite

> **Status (2026-11-14): DESIGN, nothing built.** Four read-only investigations (two Fable, two
> Opus) surveyed the harness, the corpus and the hand-written test blocks. No test was run for
> this plan: every timing below was read out of existing runner logs (`C:\bt_drf\results\*.log`),
> on a box that was busy at the time, and is marked MEASURED (a logged number) or ESTIMATED.
> The release binary in the main checkout was stale (2026-10-31), so nothing was timed directly.
> Phase P0 exists to replace the estimates with measurements before anything else is built.
> **D1 DECIDED 2026-11-15 (user):** "every corpus test is still run and judged; only the g++
> compile of an unchanged emission is skipped" is the accepted meaning of "still tested". The
> integration tier is therefore not a physically separate set of programs, and merged test
> programs are not needed for the guarantee.
> **D2-D5 DECIDED 2026-11-15 (user):** D2 -- no fixed time target; the whole corpus is in the
> integration tier. D3 -- merged test programs ARE built, binned per corpus category (section 7,
> rewritten). D4 -- thresholds left to the lead: 10 shadow pairs, 90% of seeded faults. D5 --
> the examples stay where they are and run as tests behind a flag.
> **BUILT 2026-11-15 on branch `claude/test-tiers` (worktree `C:\bt_tiers\w`), section 6.1:**
> the P0 timing report (`--timings`), and of P1 the per-program header key and the raised cache
> cap. The module mechanism for the category bins passed a hand-made probe (section 7.4).
> Also built that day: of P2, the interpreter differential prepares its compiled side in
> parallel (`BLADE_INTERP_JOBS`); the header-parsing measurement D8 asked for; and, on the
> user's go-ahead, P6, the precompiled prelude (`BLADE_PCH`, `blade test pch`). The cold
> interpreter differential went from 80-90 minutes to 12.9.
> NOT yet built: the rest of P0's measurements (the box was busy), P1's name sharing with the
> LLVM lane and the four cache-off blocks, the rest of P2 (the LLVM differential, opt-diff),
> P3 onward. No full default suite has been run on the branch.
> **D6-D8 DECIDED 2026-11-15 (user):** D6 -- the interpreter differential enters T1 over the
> curated set first and over the whole corpus once measured, and the user wants it run in
> parallel as well. D7 -- the single runner agent runs T2. D8 -- measure header parsing first.

## 1. Summary

The request: the suite has become unwieldy; carve out an integration suite that an agent can run
routinely, keep the unit tests, and fall back to them only when integration fails -- with
everything that is tested today still tested.

What the investigation found changes the shape of the answer in three ways.

1. **The size of the suite is not what makes it slow.** The corpus (4,242 tests) costs about
   16 minutes when every program must be compiled and an estimated 2 to 2.5 minutes when the
   compiled executables are reused. The time goes to four other places: lanes that compile one
   program at a time (the interpreter differential, 80 to 90 minutes), a block that spawns a
   child compiler per case (`cli`, 21 to 26 minutes for 279 cases), four blocks that switch the
   executable cache off, and a cache that is too small to stay warm across one full gate.
2. **Merged test programs cannot carry the guarantee on their own.** Merging changes what many
   tests exercise (section 7), and about 39% of the corpus (refusals, aborts, warning tests)
   cannot be merged at all. Reusing the executable of an unchanged emission keeps every test run
   and judged individually, so that is what carries "still tested" (D1). Merged programs are
   built as well (D3): one set per corpus category, as the fast first signal and as tests of
   many features in one program.
3. **So the integration tier is four things**: per-category merged programs first, the whole
   corpus on the default lane with a cache that actually stays warm, the test blocks that need
   no C++ compiler, and a curated end-to-end set (the benchmark kernels at smoke size, a thin
   slice of each expensive block, and the examples behind a flag). Estimated 5 to 8 minutes
   typically, 12 to 18 minutes when a change invalidates every emission, against 116 to 131
   minutes for `blade test --interp` today and 3 h 49 min for a full wave gate. The user set no
   fixed target (D2).

The unit suite stays as it is and remains the ground truth, run once per wave and nightly.

## 2. The suite today

### 2.1 Sizes

| Item | Count |
|---|---|
| Corpus files / tests / directories | 4,315 / 4,242 / 73 (multifile: 56 tests in 129 files) |
| Corpus value tests / refusals / aborts | 2,764 / 1,151 / 320 |
| Corpus files with a `// WARN:` / `// WARN-CODEGEN:` pin | 82 / 13 |
| `// EXPECT:` pins | 9,153 |
| Default `blade test` cases | about 10,366: 4,133 corpus + about 6,233 from hand-written F# blocks |
| `blade test --interp` cases | 14,601 (the default suite plus 4,235 interpreter differentials) |
| Harness source | about 55,000 lines in `tests/*.fs`, plus 6,200 in `src/CliSelfTests.fs` |

The figure "about 14,600 tests" that has been quoted for the default suite is the `--interp` total.

### 2.2 Where the time goes

From runner step stamps; cold = executables must be compiled, warm = reused from the cache.

| Block | Cases | Wall time | What dominates |
|---|---|---|---|
| `test --interp` (default + interp) | 14,601 | 116-131 min (MEASURED, 12 runs) | the rows below |
| corpus, default lane | 4,077 | about 16 min cold (MEASURED, one run); 2-2.5 min warm (ESTIMATED) | g++, 16 at a time |
| interpreter differential | 4,235 | 80-90 min cold, 4-6 min warm (ESTIMATED) | g++, one at a time; the harness itself uses 5% of the wall time |
| `cli` | 279 | 21-26 min (MEASURED, 5 runs) | 300-400 child `Blade.exe` processes, one at a time |
| cell-epilogue, orbit-scatter, rec-step, owned-move | 277 | 12 min together (MEASURED) | g++ one at a time, executable cache switched off |
| `optimize` | 1,441 | 16-57 s (MEASURED) | in-process emission, no g++ |
| pure in-process blocks | about 1,600 | seconds | F# |
| opt-diff (standalone) | 4,242 | 21-51 min, median 42 (MEASURED) | about 58,000 emissions in sequence, then 981 program pairs |
| llvm (standalone) | 1,182 run, 897 skipped | 3.5-4.5 min warm, 23-36 min cold (MEASURED) | g++ and clang per file, one at a time |
| zarr (standalone) | 855 | 3.3-10.4 min (MEASURED) | g++ one at a time |
| omp-reduce (flag) | 131 | 4.7-5.3 min (MEASURED) | g++ and threaded runs |
| docs, netcdf, csv, hybrid | 217 / 274 / 101 / 32 | 7-48 s / 38 s / 16 s / 12 s (MEASURED) | |

One full wave gate (`--interp`, llvm, opt-diff, zarr, omp-reduce, the rest) took 3 h 49 min.
The harness prints no per-block wall time; these numbers were reverse-engineered from the runner
script's timestamps.

### 2.3 The same program is compiled several times

Per runnable corpus program, one gate compiles: once for the default lane, once for the
interpreter differential (different flags: reproducible folds, contraction off), up to twice for
opt-diff, and once with g++ plus once with clang for the LLVM differential. The LLVM lane's C++
compile is the default lane's program with one string changed (the program name printed in the
timing line), so it never shares a cache entry: about 1,182 redundant g++ compiles per gate.
In total a gate performs about 10,500 native compiles and over 100,000 in-process emissions
(ESTIMATED).

### 2.4 Why the cache is rarely warm

The corpus runner already compiles through the content-addressed executable cache
(`Build.fs`, `exeCacheKey`: compiler identity, flags, CPU, the runtime headers, the emitted C++
text). An unchanged emission should therefore cost no g++ time. In practice:

- The cache holds at most 8,192 entries and evicts down to 6,144. One gate produces more distinct
  executables than that, so a gate evicts its own entries. On the day of the investigation every
  one of the 7,474 entries had been created that day.
- The key hashes all 13 runtime headers, so an edit to `blade_lapack.hpp` invalidates programs
  that never include it.
- The emitted text contains the test's name, so two lanes that compile the same program under
  different names cannot share an entry.
- Four blocks set `BLADE_EXE_CACHE=0` with no stated reason (`CellEpilogueTests.fs:255`,
  `OrbitScatterTests.fs:202`, `RecStepTests.fs:217`, `OwnedMoveTests.fs:67`).

## 3. What "everything currently tested is still tested" means here

Four readings were considered.

| Reading | Verdict |
|---|---|
| Every pin of every unit test also appears in some merged integration program | Checkable on paper, but a pin passing in a merged program does not show that the same compiler path produced it (section 7). Not used as the guarantee. |
| Any compiler change that fails a unit test fails some integration test | Cannot be proved for a subset; it can only be estimated by experiments (section 8). |
| Integration is a filter; the unit suite runs periodically as the ground truth | Honest, and what the wave process already does. Its quality is a measured escape rate, not a proof. |
| Every unit test is still run and judged on each routine run; only the g++ compile of an unchanged emission is skipped | Literal, and already how the corpus runner works when the cache is warm. |

The plan uses the fourth reading where it is cheap and the third where it is not:

- **R1.** No unit test is deleted or weakened. The full default suite and the opt-in lanes keep
  running, uncached, at the end of each wave and nightly.
- **R2.** The integration tier runs the entire corpus on the default lane. Each program is
  lowered, emitted, compiled only if its emission is new, run, and judged against its own pins.
  For this part "still tested" is literally true on every routine run.
- **R3.** What the integration tier does not run in full -- the regime matrices, the
  interpreter, optimizer and LLVM differentials, the provider blocks -- is represented by a thin
  slice plus a table that names the blocks a change can affect. How well the slice stands in for
  the whole is measured before it is trusted (section 8), and the wave-end full run bounds how
  long an escape can live.

Programs are always executed. Caching a program's output so that it need not run again was
considered and is not proposed: runs are a small share of the time, and it would hide
nondeterminism in threaded and random programs.

## 4. The tiers

| Tier | Contents | Who runs it, when |
|---|---|---|
| T0 witness | `blade check` / `blade run` of the change's own witnesses | The builder, on every edit (today's rule) |
| T1 integration | Section 5 | The builder before each commit; any agent as its routine check |
| T2 affected | The block keys T1 names for the change, plus the corpus programs whose emission changed if T1 did not already run them | The builder when it is one block; otherwise the single runner agent on a detached checkout |
| T3 full | Bare `blade test` with the cache off; judged on the `TOTAL` line and the absence of a failed-tests list | The runner, once per wave on the integration tip; the nightly CI lane |
| T4 opt-in lanes | interp, opt-diff, llvm, omp, mpi, cuda, providers, docs run in full | Wave end and nightly; opt-diff after touching any optimizer pass |

Escalation: a red T1 prints the `blade test <key>` to run next for each failure. A green T1 with
any skipped case is reported as not green: T1 checks the toolchain first.

## 5. What T1 contains

| Part | Content | Typical cost (ESTIMATED) |
|---|---|---|
| A. No-compiler blocks | The pure in-process blocks, `optimize`, `omp-pragma`, the emission halves of `linalg`, `lapack`, `shapespec`, the IDE and wiring blocks | about 1.5 min |
| B. Whole corpus, default lane | All 4,133 programs: refusals and warnings judged in-process, value and abort tests run from cached executables, new emissions compiled 16 at a time | 2-2.5 min when little changed; 16 min when everything changed |
| C. Curated end-to-end set | The 32 benchmark kernels at smoke size with their checksum pins, `blade test docs`; with `--examples` (D5) also `examples/` (10 programs, 157 pins) and `examples/physics/` (47 programs, 694 pins), run where they sit | about 1 min |
| F. Category bins (section 7) | Per corpus directory, merged programs built in memory from that directory's value tests; run first, so a broken build shows in the first minute | unmeasured; about 105-145 programs |
| D. Slices of the expensive blocks | `cli`: the command-surface cases only (about 30 child processes); one regime each of cell-epilogue, orbit-scatter, rec-step; the both-lanes boundary cases of each provider; `llvm goldens` and `shim`; `run-record`; the `access` route pins | 2-3 min |
| E. Wiring checks | `corpus-wiring`, plus the new `tier-wiring` check (P4) | seconds |

Total: 5 to 8 minutes typically. The arithmetic for the changed-emission case: a compiler change
that alters 10% of emissions compiles about 300 programs, about 2.3 minutes 16 at a time.

Whether T1 also runs the interpreter over part or all of the corpus is decision D6.

Part B is what makes T1 more than a smoke test. If the T1 budget must be far smaller, the
fallback is a representative subset: a greedy cover found 131 corpus files that touch every
pinned diagnostic code, every pin kind and every optimizer gate and plan rule the corpus names
(283 files with at least three per directory). That subset covers the vocabulary of assertions,
not behaviour, and would not support R2.

## 6. Work items

In order. Items in P1 to P3 drop no check; each says what, if anything, changes about what is
tested.

### 6.1 Built so far (2026-11-15)

| Item | What was built | Checked by |
|---|---|---|
| P0 timing report | Every `blade test` run ends with a `Timings` table: wall seconds per section (a section starts at each block header, so no block had to change), g++ compiles against cache hits, programs run, front-end passes. `--timings <path>` writes it as JSON with each corpus test's wall time. `TestHarness.Timings`, `Build.CompileStats` | `blade test tuples` twice, below |
| P1 header key | The cache key hashes the runtime headers a program includes, closed under their own includes, instead of all of them (`Build.runtimeHeaderDigestFor`). A survey of 3,491 generated programs found they name every runtime header they use on an include line. Key version bumped, so existing entries are not reused | Editing `blade_lapack.hpp` (no `tuples` test includes it): 64 hits, 0 compiles. Editing `index_types.h` (reached only through another header): 0 hits, 64 compiles. Restoring both: 64 hits |
| P1 cache cap | 8,192 entries raised to 32,768; the tile store keeps its own 8,192 | Build only |
| P2 interpreter differential in parallel (D6) | `blade test interp` prepares the compiled side (front-end pass, g++, compiled run) of up to `BLADE_INTERP_JOBS` tests ahead of the one it is judging (default: the processor count; `1` = one at a time, the old order). The interpreter walks and every verdict stay on one thread in corpus order, and each walk holds the pipeline lock, so no walk overlaps another test's front-end pass. One process, no shards. `InterpDiff.runAhead`, `Runner.underPipelineLock` | `tuples`, `multifile`, `numeric-widths` in both arms, below; the whole differential once |
| D8 measurement | Not a code change: `-ftime-report` on six generated programs, with and without a precompiled header | Below |
| P6 precompiled prelude (D8, go-ahead 2026-11-15) | A program's leading block of `#include` lines is compiled once per flag set into a GCC precompiled header and passed to later compiles with `-include` (`BLADE_PCH`; `Build.tryPrecompiledPrelude`). Built at a block's second sighting; any compile that fails with it is re-run without it and that result is the verdict; the emitted text and the executable-cache key are unchanged | A hand prototype, the new `blade test pch` block (8 cases), `tuples` off against on, and the whole interpreter differential, all below |

**The interpreter differential, parallel against one at a time** (cold = an empty private cache
per arm; the box was busy, so the times are upper bounds and the ratio is the result):

| Directory | Tests | g++ compiles | One at a time, cold | Parallel, cold | Verdict lines that differ |
|---|---|---|---|---|---|
| `tuples` | 89 | 65 | 103.9 s | 27.6 s | 0 |
| `multifile` | 57 | 47 | 71.8 s | 18.8 s | 0 |
| `numeric-widths` (raw-bit channel) | 17 | 17 | 29.1 s | 9.3 s | 0 |

All six runs: 0 failed, 0 skipped. Warm, `tuples` took 8.2 s parallel and 11.8 s one at a time.
A compile costs about 1.4 s when it runs alone and about 3 s when sixteen run together, so the
cold gain is 3.8 times, not sixteen.

**The whole differential, parallel, nearly cold** (`blade test interp`, every corpus directory,
same busy box): **18.9 minutes; 4,235 passed, 0 failed, 7 skip-unsupported, 0 skipped.** The
logged runs of the old order took 80 to 90 minutes. It made 2,977 g++ compiles (9,662 s summed,
3.2 s each, so about 8.5 ran at once on average) against 126 cache hits; 3,091 compiled
programs ran in 123 s summed; the 4,185 front-end passes took 110.6 s summed. The four
interpreter walks over 5 s took 43, 21, 11 and 5 s; the slowest was measured at 25 s on an
idle box (the comment in `InterpDiff.fs`), so a walk is slower beside sixteen compiles, as expected, and none came near the
300 s ceiling.

That run also corrects the front-end figure above: over the whole corpus a front-end pass
averaged 26 ms, not the 90 ms seen on `tuples`. A fully warm corpus run is therefore bounded by
about two minutes of serialized front end, which is what section 2.2 estimated.

**Header parsing (D8)**, six `tuples` programs of 9.5 to 20 KB, medians of five interleaved
repeats, wall seconds inside `cc1plus` (this g++ reports wall time only):

| | Per program | Share of the compile |
|---|---|---|
| The program as emitted | 1.23 s | 100% |
| Its parsing phase | 0.79 s | 64% |
| Only its `#include` lines, nothing else | 0.88 s | 71% |
| The program with a precompiled prelude (28 headers, 77 MB, 2.4 s to build once) | 0.54 s | 44% |
| Link, for scale | 0.17 s | -- |

The headers are about two thirds of a cold compile, and a precompiled header removed 56% of the
compiler's time on these six programs (1.23 s to 0.54 s). The same saving applies to
`blade run`. Not measured: programs that include the heavy optional headers (LAPACK, the tile
kernels, providers), and whether the saving survives sixteen compiles at once, where each
compile already takes twice as long for reasons this measurement does not explain.

**The precompiled prelude, built (P6).** Before any code, a survey and a prototype by hand:

- 4,370 of 4,371 generated programs on disk open with one of two include blocks (24 lines in
  4,327, 23 in 43), with nothing ahead of the first include. Two precompiled files per flag set
  cover the corpus.
- The prototype built the file in one directory with its own copies of the runtime headers,
  renamed the directory, and compiled programs that sit elsewhere with their own copies: g++
  used the file for 8 of 8; **the assembly and the compiler's stderr were byte-identical with
  and without it for 8 of 8**; 32 compiles at sixteen at a time all succeeded (7.4 s without,
  4.1 s with); and a program whose local copy of a prelude header differs from the precompiled
  one FAILS to compile (a redefinition), it does not silently take the wrong header.

Then the implementation, measured cold (an empty executable cache per run; busy box):

| Run | Without | With | g++ time summed | Verdict lines that differ |
|---|---|---|---|---|
| `blade test tuples` (88 tests, 64 compiles) | 22.4 s | 14.3 s | 215.7 s to 132.7 s | 0 |
| `blade test interp`, whole corpus | 18.9 min (2,977 compiles, 126 cache hits) | 12.9 min (3,103 compiles, 0 hits) | 9,662 s to 5,837 s (3.2 s to 1.9 s each) | 0 |

The whole differential with the prelude: 4,235 passed, 0 failed, 7 skip-unsupported, 0 skipped;
3,090 of 3,103 compiles used the prelude, one was built (2.2 s), none had to be re-run.
Together with the parallel preparation the cold differential went from the logged 80-90 minutes
to 12.9. `blade test pch` (8 cases, 13 s) pins the second-sighting rule, reuse, `BLADE_PCH=0`,
the re-run when the precompiled file is garbage, and the drop after three such failures.

Not covered: the default suite as a whole has not been run with it; no run has exercised a
flag set with BLAS on, MPI or a tile-cache program; and nothing measures `blade run`, where the
first program after a flag change pays about 2 s to build the file.

First numbers from the new report, `blade test tuples` (88 tests, 64 of them compiled), with
another session's suite running on the box, so they are upper bounds:

| Run | Wall | g++ compiles | Cache hits | Front end, summed | Programs, summed |
|---|---|---|---|---|---|
| Cold (new key) | 46.0 s | 64 (492 s summed over 16 workers) | 0 | 8.3 s | 9.8 s |
| Warm | 9.8 s | 0 | 64 | 8.1 s | 5.4 s |

Both runs: 88 passed, 0 failed, 0 skipped. Two things follow. Reuse works as intended: the warm
run compiled nothing. And the warm run is bounded by the front end, which runs one program at a
time under the harness's pipeline lock: 88 passes took 8.1 of the 9.8 seconds, about 90 ms each
on the loaded box. At that rate the whole corpus would take about 6 minutes warm, not the 2 to
2.5 minutes estimated in section 2.2 from older logs -- but the whole-corpus run further down
measured 26 ms per pass, so `tuples` was not representative. The lock already has an experiment gate
(`BLADE_TEST_FSHARP_PARALLEL=1`, `tests/Runner.fs`); P0's quiet-box measurement must include the
front-end rate with and without it, because it is now the largest term in a warm run.

Not done from P1, and why: sharing a cache entry between the default lane and the LLVM
differential means changing the program name that differential passes, which can only be
checked by running `blade test llvm`; and re-enabling the cache in the four blocks needs their
12 minutes of runs. Git history gives no reason for the four pins (they arrived with each block).
Both wait for a quiet box.

### P0. Instrument and measure

- Add per-block and per-test wall time to the harness footers and a `--timings` JSON output
  (`tests/TestHarness.fs`). Today timings cannot be read from a run.
- On a quiet box with a current build, measure: the corpus cold and warm; the share of a cold
  compile spent parsing headers (`-ftime-report`); why 16-way compiling is only about 3.5 times
  faster than one at a time (antivirus scanning of new executables, thermal limits and the linker
  are the candidates).
- Find out why the four blocks switch the executable cache off.

Nothing after P0 should be sized from this document's estimates.

### P1. Make an unchanged emission cost nothing

| Change | Where | Effect on what is tested |
|---|---|---|
| Raise the cache entry cap to about 32,000 (about 4 GB at the observed 126 KB average, under the existing 6 GB byte cap) | `Build.fs:748` | None |
| Hash only the headers a program includes | `Build.fs`, `runtimeHeaderDigest` | None; a header edit still invalidates every program that includes it |
| Keep the program name out of the hashed text, or have the LLVM differential pass the same name as the default lane | `CodeGen.fs:4219-4298`, `LlvmTests.fs:3325` | None; removes about 1,182 duplicate compiles per gate |
| Re-enable the cache in the four blocks, if P0 finds no reason against it | the four `pinEnv` lines in section 2.4 | None for value and raw-bit checks; the key covers text and flags |

### P2. Stop running the long lanes one program at a time

- **Interpreter differential. BUILT (section 6.1), in one process instead of shards.** The
  question shards were meant to avoid -- is the interpreter safe beside other F# work -- is
  answered by not asking it: only g++ and the compiled runs overlap. The front-end passes stay
  serialized by the existing lock, and each interpreter walk takes the same lock. What is new
  compared with the old order is that other tests' front-end passes now run between a test's
  own pass and its walk; the interpreter takes all its input from the lowered program and
  installs its own analysis state, and the three directories above gave identical verdicts in
  both orders. `BLADE_INTERP_JOBS=1` is the old order, kept as the comparison arm.
- **LLVM differential.** The same sharding. Estimated 34 minutes down to about 8.
- **opt-diff.** It computes which optimizer gate moved each program's emission for all 981
  differing programs, though the answer is only printed for failures (`OptDiff.fs:194-203`).
  Compute it for failures only (up to about 13 minutes), and shard the emission phase by
  process. Passing lines lose the gate names; no verdict changes.

### P3. Condense the hand-written blocks

| Change | Where | Saving (ESTIMATED) |
|---|---|---|
| In the `cli` run matrices, emit each (program, settings) pair first and compile and run once per distinct emitted text. Identical text gives an identical executable, which is the rule opt-diff already relies on. One compaction-fusion group alone is 96 child runs for 16 cases | `CliSelfTests.fs` lines 916-2560 | several of the 21-26 minutes |
| Replace the 38 child `blade plan` processes in `gateIdentity` with the in-process decision reader, keeping one spawned `plan` per rule to cover the command itself | `CliSelfTests.fs` lines 1599-1640 | about 38 process starts |
| Share one executable across regimes that differ only in thread count, which is a run-time setting | `CellEpilogueTests.fs:148-158`, `RecStepTests.fs`, `OrbitScatterTests.fs` | about 30% of those blocks' compiles |
| Remember the result of lowering and emission per (source, emission settings) inside `optimize`: 308 decision checks cover 224 distinct fixtures, and 30 fixtures are both emitted and decision-checked | `OptimizeTests.fs` | seconds to a couple of minutes |
| One shared end-to-end helper for the provider blocks, where the sequence lower, emit, write, compile, run, parse is copied about 38 times in zarr, 11 in netcdf, 5 in csv | `ZarrTests.fs`, `NetcdfTests.fs`, `CsvTests.fs` | about 2,000 lines; makes the rows above apply there |

The remembered results must be keyed on a snapshot of the environment settings, and must hold
outputs (decisions, emitted text), never the intermediate program, because the two back ends
each install their own analysis state on the program they consume (`LlvmTests.fs:3315`).

### P4. The integration verb and the mapping

- **Named cases.** The large blocks are single eager functions (`runCliSmokeTests` is about
  2,850 lines, `runZarrTests` about 5,000) and the harness has no name filter. Selecting a slice
  means first splitting each into named cases that can be run individually. This is the largest
  refactor in the plan.
- **`blade test integration`.** Because of compile order the verb must be assembled in
  `src/CliSelfTests.fs` (the `cli` and IDE blocks are private there). It needs an arm in the
  dispatcher, the usage text, and a guard so that a corpus directory named `integration` cannot
  shadow it.
- **A block table in F#** (`TestMap`): for each block key, its tier, the source files and
  optimizer gates it covers, and its toolchain needs. A failure in T1 prints the keys from this
  table.
- **A `tier-wiring` check**, modelled on the existing corpus-wiring block, that fails when a
  test block has no row in the table, when a source file under `src/` maps to no block, when one
  of the 51 optimizer gates has no T1 case that depends on it, or when a corpus directory has no
  program in T1. Because the table is compiled code that the dispatcher reads, a new block
  without a row is a failure, not a silent omission.
- **Examples behind a flag (D5).** The examples stay in `examples/`. They load data relative to
  their own directory, which is why they are not in `blade test` today;
  `examples/check_pins.ps1` re-implements the pin rule in PowerShell. Add `--examples` (and the
  standalone key `blade test examples`): a runner that executes each program from its source
  directory and judges it with the harness's own pin checker. The PowerShell script can then
  call the verb instead of carrying a second copy of the rule.
- **`blade test --affected <ref>`** prints the corpus programs whose emission differs from the
  cached one and the table's block keys for the changed source files.

### P5. Validate T1 before relying on it

Section 8.

### P6. A precompiled header for the runtime prelude

The median emitted program is 11.8 KB but includes about 17 standard headers and at least five
runtime headers, re-parsed for every program. A precompiled header per flag set leaves the
emitted text unchanged. Programs that define macros ahead of the includes (non-default pool
retention, the workspace census, memory-check builds) must bypass it. MEASURED 2026-11-15
(section 6.1): the headers are about two thirds of a cold compile and a precompiled prelude
removed 56% of the compiler's time on six programs. It shortens the everything-changed case and
it also speeds up `blade run`.

**BUILT 2026-11-15 (section 6.1).** How it keeps the compiled program the same:

- The prelude is the program's own leading block: the include lines that precede everything
  else in its text, in its order. A program with anything ahead of its first include has no
  leading block and compiles as before, which is how the macro-defining programs bypass it
  without a list of them. Programs carrying an `#error` guard are not offered one.
- The file is built with the compile's own flags, from the header text every compile deploys
  beside its `.cpp`, and keyed on the compiler, those flags, the CPU `-march=native` resolves
  to, the block, and the runtime headers the block reaches.
- Any failure of a compile that used the prelude is re-run without it, and the second result
  is the one reported. A stale or broken file costs time, never a verdict.
- The emitted text and the executable-cache key do not change.
- A block is precompiled at its second sighting, so a hand-written harness program with its
  own include block never pays for a file nobody reuses. At most 12 files are kept.

### P7. Documentation and CI

- Rewrite the Testing section of `CLAUDE.md` around the tiers: run `blade test integration`
  routinely; on red run the keys it names; the bare `blade test` is the wave runner's; a skip is
  not green.
- CI today runs only the build and `llvm goldens` on every push; the default suite reaches CI
  nightly. T1 is a candidate for the per-push lane once its time is measured.

## 7. Category bins: merged test programs (D3, decided: build)

For each corpus directory the harness builds merged programs ("bins") out of that directory's
value tests, in memory. The corpus files are not rewritten: 76 of them are read by name from
hand-written tests, and each stays its own unit test.

### 7.1 What a bin is for

Because the whole corpus also runs test by test (part B, decision D1), the bins do not have to
carry the "still tested" guarantee. They do two other jobs:

- **The fast first signal.** About 105 to 145 programs stand in for about 2,600 value tests, so
  a broken build shows up in the first minute even when a change has invalidated every cached
  executable. A red bin is re-run member by member, which is the unit fallback the request
  described.
- **Tests of many features in one program.** A member's pinned values must still hold when it
  is one module among twenty-five. That is a different route through the compiler than the
  member takes alone, and a real check in its own right.

The second point is also the caveat. Many optimizer decisions depend on the whole program (one
reader, a single module, main's top level, the binding immediately after). Inside a bin a member
may take a different route and pass the same pins. A green bin therefore does not show that the
member's own optimizer witness still fires; the member's solo run in part B shows that.

### 7.2 Mechanism

Three were considered.

| Mechanism | How | Verdict |
|---|---|---|
| Module per member | Each member's source becomes a module of a generated multi-file program; a generated main imports them all. Collisions between members' names need no renaming, and printed labels come out qualified (`M.x`), which is how the multi-file tests already pin them | **First choice**, subject to the prototype below |
| Rename and concatenate | One module; every top-level name, type, unit and struct of a member gets a prefix, and the pins and printed struct names are rewritten through the same map. 1,487 tests share a type name with another test, so type renaming is mandatory | Fallback if modules do not scale |
| C++ namespace per member | Each member is lowered and emitted alone, and the emitted bodies of a bin are compiled as one translation unit | Rejected: it saves only the repeated header parsing, which a precompiled header (P6) saves too, and a change to one member recompiles the whole bin |

### 7.3 Membership and failure handling

- **Members**: value tests with `// EXPECT:` pins. By the survey about 1,750 are plain value
  tests and up to 836 more are optimizer witnesses that can ride along (their pins must hold on
  any route).
- **Not members** (about 39% of the corpus): refusals, aborts, tests with a warning pin, tests
  marked `// NOPINS:`, the multi-file tests, and tests that write a store, emit to the display,
  or use sequenced random draws or a device.
- **Bin size**: about 25 members, capped by emitted C++ size; bins never cross a directory.
- **Pins**: each member's pins are read from its own file and matched under its module's label.
  The harness matches pins by name, so a failure names the member and the pin.
- **A bin that fails as a whole** (a member is refused in this setting, the bin aborts, or g++
  rejects it) is split and re-run until the failing members are isolated; those run alone and
  are reported by name. A member that passes alone and fails in a bin is a finding, not a flake:
  it is either a real cross-module defect or a member that must be excluded with a recorded
  reason.
- **Warnings**: a member without a warning pin asserts that no warning fires. A warning raised
  in a bin is attributed to its member through its source span and judged by the same rule.

### 7.4 Prototype first

Before the bin runner is written, one hand-made bin from a plain directory (`basic`) answers:

1. Does lowering a 25-module program cost about the sum of its members, or much more?
2. Are types, units and structs scoped per module, so that two members declaring `type I` or
   `Unit meters` can coexist?
3. Do imported modules' top-level bindings all print, with labels the pin checker can match?
4. How long does g++ take on one merged `main` compared with the members' separate compiles?
5. How often does a member that passes alone fail, or warn, as an imported module?

**Probe result (2026-11-15).** The first 25 plain value tests of `basic`, each prefixed with a
`module` line, plus a main that imports them: the program typechecks (about 4 seconds including
process start) and compiles and runs in 9 seconds on a loaded box. All 47 pinned bindings print
under qualified labels (`B003.arr1d = [1.0, 2.0, 3.0]`). 38 match their pin text exactly; the
other 9 differ only in ways the harness's pin checker already accepts (nested against flat
array shape, quoted strings, digits beyond the tolerance), which a text comparison does not.
So questions 2 and 3 are answered yes for this directory, with no renaming. Still open:
questions 1 and 4 at scale and on a quiet box, and question 5 over a directory of optimizer
witnesses rather than plain tests.

If answers 1 or 2 rule modules out, the fallback is rename and concatenate. If question 5 shows
a large share of members failing as modules for reasons that are not defects, the bins are
limited to the plain value tests.

## 8. Validating the integration tier

| Experiment | What it does | Accept when |
|---|---|---|
| Shadow runs | Run the full suite with the cache on and with it off; compare the lists of failing test names | 10 consecutive identical pairs (the threshold the harness already uses for its lock experiment) |
| Gate switch-off | For each of the 51 optimizer gates, run T1 with that one gate forced to its other setting. Gates are read per call, so no rebuild is needed | Every gate turns at least one T1 case red |
| Seeded faults | About 20 deliberate compiler faults, each a short rebuild: change a diagnostic code, drop a bounds guard, swap a fold's operand order, perturb the float printer. Run T1 and the full suite on each | T1 catches at least 90% of what the full suite catches; every miss gets a T1 case or a written exemption |
| Past fixes | 82 commits since 2026-06 are small fixes that added a corpus witness. Old compilers cannot run today's corpus, so re-apply each fix's source change in reverse on the current tree where it still applies | The witness goes red under T1 and T2 every time |

The 90% and the 10 runs are proposals (decision D4).

## 9. Findings to fix regardless of this plan

- **A test group is unreachable from its block.** `runLlvmBlockForceTwins` is not in
  `runLlvmTests` (`LlvmTests.fs:4621-4632`); it runs only under `blade test llvm facts`. The
  wave gate that runs `blade test llvm` does not run it.
- **The opt-diff CI lane is at its limit.** Its timeout is 45 minutes and its comment says it
  compiles "a few dozen" pairs; the logs show 981 pairs and a 42-minute median locally.
- **Provider blocks under-report.** Their skips are printed but not counted, and their footers
  are not `TOTAL:` lines, so the runner script's totals check does not see them; only the exit
  code gates them.
- **25 diagnostic codes are never pinned by the corpus** (87 pinned of about 112 defined).
- **Corpus hygiene.** 15 groups of files with identical code (31 files; for example loops/003,
  loops/007 and symmetry/007 are the same program, and static/012's comment claims a difference
  from static/001 that does not exist); 13 value tests whose every pin is a literal;
  `symmetry/035` is not valid UTF-8; 7 files carry a byte-order mark; 173 files share a number
  prefix with another file in their directory; 13 directories hold eight tests or fewer.
- **About 80 corpus files exist to witness opt-in optimizer settings** that the default suite
  clears, so in the default corpus run they exercise only the default path; their decisions are
  pinned by the hand-written blocks. Worth knowing when reading a green corpus run.
- **The release binary in the main checkout was two weeks stale** on the day of the survey.

## 10. Decisions for the user

| # | Decision | Recommendation |
|---|---|---|
| D1 | Is "every corpus test still run and judged, only the compile of an unchanged emission skipped" an acceptable meaning of "still tested", or must the integration suite be a physically separate set of programs? | **DECIDED 2026-11-15: accepted.** Rules R1-R3 of section 3 stand as written |
| D2 | T1 time budget | **DECIDED 2026-11-15:** no fixed target; the whole corpus is in T1 |
| D3 | Merged test programs | **DECIDED 2026-11-15: build them**, binned per corpus category as modules or namespaces (section 7) |
| D4 | Acceptance thresholds for section 8 | **DECIDED 2026-11-15 (left to the lead):** 10 shadow pairs; 90% of seeded faults |
| D5 | Bring `examples/` and `examples/physics/` into the harness as owned test assets | **DECIDED 2026-11-15:** they stay where they are; a flag (`--examples`) runs them as tests |
| D6 | Does T1 include the interpreter differential? | **DECIDED 2026-11-15:** over the curated set at first, over the whole corpus once measured; the differential is also to run in parallel (P2) |
| D7 | Who runs T2 when the affected set is large | **DECIDED 2026-11-15:** always the single runner agent |
| D8 | Build the precompiled header (P6) | **DECIDED 2026-11-15:** measure first. MEASURED the same day (section 6.1): 56% of compiler time saved on six programs. **Go-ahead given 2026-11-15; built** (P6) |

## 11. Stop rules

- If P0 measures the warm corpus at well above the estimated 2.5 minutes, part B of T1 is
  re-sized before P4 starts, and D2 is asked again with the real number.
- If the shadow runs ever disagree, the cache is not trusted for T1 until the cause is found.
- If the gate switch-off or the seeded faults fall short of the thresholds after one round of
  added cases, T1 is described as a smoke test in `CLAUDE.md`, not as a substitute for the
  affected unit blocks.
- If splitting the large blocks into named cases (P4) proves too invasive for `cli` or `zarr`,
  their T1 slice is a small new block written for the purpose, and the original stays whole in
  T3.

## 12. Evidence

| Source | Covers | Basis |
|---|---|---|
| Cost model investigation (Fable) | Sections 2.2-2.4, P1, P2, P6 | Read the harness and `Build.fs`; 1,430 timestamped steps in `C:\bt_drf\results\*.log`; cache directory file times; nothing timed directly |
| Coverage investigation (Fable) | Sections 3, 4, 8, parts of P4 | Read the harness, the corpus README, `ci.yml`, git history; nothing run |
| Corpus investigation (Opus) | Sections 2.1, 5 (the subset), 7, 9 | Scripts over the corpus (kept outside the repo in `C:\bt_tcorp`); 30 files read to check the classification; the merge classes are heuristic |
| Hand-written block investigation (Opus) | Sections 2.2, P3, P4, 9 | Read the blocks; counts from `C:\bt_drf\results\20261114-105034-integ.log` and from grep |

Spot-checked by the lead against the source: the cache key and its caps (`Build.fs:722-749`),
the program name in the emitted text (`CodeGen.fs:4219`), the four cache-off blocks, the LLVM
differential's second lowering and its naming (`LlvmTests.fs:3304-3325`), the sequential
interpreter loop (`InterpDiff.fs:401-413`), opt-diff computing attributions for every pending
program (`OptDiff.fs:194-203`), and `runLlvmBlockForceTwins` being absent from `runLlvmTests`.

The investigators disagreed on one point: the corpus investigation recommended merged programs
with the decision-record guard; the cost and coverage investigations recommended against merging.
The first draft of this plan sided with the latter and deferred merging; the user decided
to build the bins (D3), and section 7 now gives them a role that does not depend on the
guarantee.
