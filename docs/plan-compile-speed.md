# Plan: Compile-Speed Exploitation (F# pipeline, lexer → codegen)

Status: investigation 2026-08-12; Stages 0–3 implemented the same day on
`claude/blade-compile-speed-ca1877` (see the *Implemented* markers and the
achieved-results table in §5). Stages 4–5 remain future work. All timings
measured on the development box (16 cores, Windows 11, .NET 7, Release build),
warm, per-invocation unless noted.

## 1. Where the time actually goes (measured)

Phase split, per CLI invocation (`BLADE_PHASE_TIMING=1`, stopwatch marks in
`lowerDiag`/`lowerDiagMulti`/`compileFile`):

| Program | parse | typecheck | lower | validateIR | codegen | compileFile total |
|---|---|---|---|---|---|---|
| trivial corpus test (5 lines) | 18 | 169 | 59 | 14 | 71 | 435 ms |
| `examples/lsdft.blade` (91 lines) | 19 | 451 | 134 | 17 | 236 | 1068 ms |
| `examples/01_weather_stations.blade` (181) | 36 | 508 | 87 | 17 | 314 | 1189 ms |
| synthetic, 200 top-level lets | 144 | 413 | 167 | 36 | 186 | 1298 ms |
| synthetic, 800 lets | 845 | 457 | 182 | 41 | 220 | 2878 ms |
| synthetic, 2000 lets | **4330** | 508 | 262 | 62 | 398 | **10399 ms** |

Five structural facts fall out of this table plus the cross-cutting measurements:

1. **Parsing is quadratic and dominates large files.** 200→2000 lines takes parse
   from 144 ms to 4.3 s. Two verified mechanisms (§3 F1, F2).
2. **Every file is parsed twice.** The gap between `frontend-total` and
   parse+typecheck+lower tracks parse time ~1:1 (4834 ms unaccounted at n=2000
   ≈ the 4330 ms parse repeated). `ModuleResolve.scanFile` fully parses the entry
   just to read its imports, then `lowerDiag`/`parseResolved` parses again (§3 F3).
3. **A trivial file costs ~435 ms of pipeline + ~150 ms process floor.** Almost
   all of it is JIT warm-up: no ReadyToRun anywhere in the build. A published R2R
   image cuts lsdft's pipeline 1068→591 ms (−45%) with **byte-identical emitted
   C++** (§3 F4).
4. **`blade compile`/`run` pays ~868 ms of toolchain probes** (`nvidia-smi -L`
   alone is 510 ms) on the plain CPU path that only ever reads `HasGpp` (§3 F5).
5. **`blade test` wall time is ~89% g++ / ~11% F#.** ~40–55 ms F# CPU per test,
   ~70 s F# total in a ~620 s suite. F#-side optimization has an 11% ceiling on
   the suite; only compile-avoidance (caching) reaches the other 89% (§3 F10).

Typecheck, by contrast, is nearly flat with program size (413→508 ms over 10× more
bindings; same for an 800-statement function block), so the classic typechecker
suspects are *not* where the time is — most of its ~170 ms floor on a trivial file
is JIT, recovered by R2R.

## 2. Verification protocol (applies to every stage)

- **Byte-identity gate:** for any change that claims to be perf-only, `blade emit`
  the full corpus before/after and diff the generated C++ byte-for-byte (the R2R
  experiment already passed this via file hashes). Spans are pinned by corpus
  `// ERROR: @ l:c` tests, so parser/span changes are NOT allowed to shift
  positions — the pins are the tripwire.
- **Suite gate:** full `blade test` green (master baseline is 4646/0), plus
  `blade test interp <touched-area>` where behavior could drift; check the
  `, N skipped` suffix.
- **Timing gate:** re-run the §1 table (Stage 0 makes this one command). Never
  benchmark at power-of-two extents; run 3× and take medians (zombie Blade.exe
  processes and Defender scans of freshly written files both add multi-second
  noise — kill strays first).

## 3. Verified findings inventory

Each finding was located by a subagent sweep and independently re-verified against
source; empirical confirmation noted where we have it.

**Front end (the big asymptotics)**

- **F1 — Lexer token append is O(n²).** `Lexer.fs:318`
  `state.Tokens <- state.Tokens @ [tok]` copies the whole token list per token.
  Fix: `ResizeArray` + one `List.ofSeq` at end of `tokenize` (`Lexer.fs:727-730`);
  nothing reads `Tokens` mid-lex. Effort S, risk minimal.
- **F2 — Per-node span computation is O(remaining tokens).** `Parser.fs:121-131`
  `consumedEnd` does `List.length before - List.length after` on suffixes of the
  whole token stream, plus `List.truncate n` + `List.filter`, and is reached via
  `rangeSpan`/`mkE`/`mkP` (`Parser.fs:148-159`) for essentially every non-trivial
  AST node. Fix: stamp a monotone `Index: int` on `Token` during lexing; the
  length difference becomes an O(1) index subtraction, and "last meaningful token"
  comes from remembering the last non-newline/semi token consumed. The cursor stays
  a plain list — no signature churn. Effort M. Risk: span pins (corpus `@ l:c`)
  must not move; byte-identity gate covers it.
- **F3 — Double parse on every compile.** `ModuleResolve.fs:156-159` (`scanFile`
  full-parses to extract imports, discards the AST) + `Lowering.fs:2381/2410`
  (parses again). The escape hatch already exists: `resolveParsedEntry`
  (`ModuleResolve.fs:301-304`), built for the IDE, "empty for everyone else."
  Fix: parse once in the CLI lanes, hand the AST to `resolveParsedEntry`; memoize
  member-file parses through `Resolution` for the multi-file lane. Effort S–M.
- Confirmed fine, don't touch: lexer scanners already use StringBuilder; parser
  cursor is O(1) head/tail; `many`/`sepBy` cons+reverse; no backtracking beyond
  1–2 token lookahead; `SourceMap.ofSources` runs once.

**Build/process (the big constants)**

- **F4 — No ReadyToRun.** `Blade.fsproj` / `Directory.Build.props` set nothing:
  stock JIT-from-IL for an 11.5 MB assembly in a short-lived process. Measured
  with `dotnet publish -p:PublishReadyToRun=true`: `check` tiny −32%
  (361→247 ms), `emit` examples/01 −43% (1250→714 ms), lsdft compileFile
  1068→591 ms; emitted C++ hash-identical; DLL 11.5→24.7 MB. `TieredPGO=false`
  measured no benefit; ServerGC wrong for a batch CLI (keep workstation GC).
  Effort S (packaging: ship the published image), risk low.
- **F5 — Eager toolchain probes.** `Build.fs:143-157` `detectCapabilities` probes
  g++ (167 ms) + nvcc (138 ms) + cl (52 ms) + `nvidia-smi -L` (510 ms) behind one
  `lazy`, forced on every compile (`Cli.fs:250`); the CPU-only arm of
  `resolveCompile` (`Build.fs:177-186`) reads only `HasGpp`. Fix: per-field
  `Lazy<bool>` so each arm forces only what it reads (stays a function — honors
  the "env gates are functions" rule). Saves ~700 ms on every `compile`/`run`.
  Effort S, risk low.
- **F6 — Runtime header deploy: 264 KB re-read + rewritten per compile, no
  content compare.** `CodeGen.fs:8003-8110`. Milliseconds are small (~20 ms) but
  unconditional rewrites retrigger Defender scans (observed 3.5–6.9 s variance on
  one-line `blade compile`). Fix: memoize `readCppRuntimeHeader`; skip writes when
  destination content is identical. Effort S.
- **F7 — The just-written .cpp is re-read from disk 3× for backend sniffs**
  (`Build.fs:437,443,482`). Thread the in-memory `cppCode` through instead.
  Effort S. (`takeCodegenRefusalDiagnostics` is already gated and fine.)

**Middle/back end (real but smaller today)**

- **F8 — `mapIRExpr` used as a visitor allocates a full discarded tree copy** at
  ≥5 sites (`IR.fs:4280, 5093, 8224` + 2 more), some inside the HM fixpoint loop
  (≤16 rounds, `IR.fs:4532-4563`) and inside `buildCallablesTableForModule`,
  which runs ≥3× per compile. Fix: add non-allocating `iterIRExpr` on `ExprShape`
  (pattern already exists: `collectVarRefsIR`, `IR.fs:4020-4026`). Effort S.
  Related: `List.distinct` over raw `IRType` values at `IR.fs:4563` — key on
  `canonTypeKey` strings instead.
- **F9 — `lowerArrayBinOpsModule` and `liftInlineFormsModule` run unconditionally**
  (`IR.fs:4834/6712`), unlike their gated neighbors (`monomorphizeModule` et al.).
  Fix: cheap `iterIRExpr` pre-scan gate, mirroring the existing idiom. Effort S–M.
- **F10 — `genModule`'s fold rebuilds the module text lists per item**
  (`CodeGen.fs:19584-19599`, twin at 19663-19694): `bc @ code @ [""]` is O(n²)
  in binding count. Empirically mild at 2000 bindings (codegen 186→398 ms) but
  the wrong shape; fix with `ResizeArray`. Same pattern smaller: hoisted-decl
  dedup `List.contains` + append (`CodeGen.fs:213-219, 19451` → HashSet+order
  list), CUDA kernel cell (`CodeGen.fs:10613/11156/11342`), fresh `Regex` per
  hoisted binding (`CodeGen.fs:184-208`). All Effort S.
- **F11 — Harness `fsharpPipelineLock` serializes all F# across 1547 parallel
  tests** (`tests/Runner.fs:127/165`), and its stated justification is stale:
  `structFieldsCache` is already `AsyncLocal` (`IR.fs:5478`), and
  `codegenStructFieldsCache` no longer exists (stale comment at
  `CodeGen.fs:9465`). Ceiling: −11% of suite wall. Caveat: `AsyncLocal` under
  `Array.Parallel.mapi` leaks values between iterations on the same worker
  thread, and `runOnLargeStack` spawns threads inside the locked region — lock
  removal is an *experiment*, not a cleanup. *(Both comments corrected and the
  experiment landed opt-in as `BLADE_TEST_FSHARP_PARALLEL`; the caveat's failure
  mode reproduced. See Stage 4.2.)*
- Downgraded after measurement (real anti-patterns, negligible at realistic
  scale; fix opportunistically): `inferBlock`/`inferForIn` `typedStmts @ [x]`
  (`TypeCheck.fs:13883-13912, 14059-14078` — flat at 800 statements),
  `staticEnvOf` rebuild (`TypeCheck.fs:173-198`), `ExprShape` IRMatch fold
  (`IR.fs:3921-3926`).
- Needs profiling before acting: `Subst.Resolve` has no path compression
  (`Unify.fs:495-517`; fragile metadata invariants — instrument chain length
  first), unmemoized `IR.typeOf` on deep un-let-bound chains (`IR.fs:5659-5682`),
  `exprToCppCore` sprintf-of-children on the same chains (`CodeGen.fs:2615`).
- Verdicts, don't pursue: intra-compile parallelism (blocked by a genuine
  cross-module data dependency, `TypeCheck.fs:16997-17022`, and ~30 AsyncLocal
  channels; payoff near zero for 1–2-module programs); unify already resolves
  lazily (no env-wide substitution); per-call env-var gates (by design, keep).

## 4. Staged plan

### Stage 0 — Make the profile a feature (S, do first)
Commit the phase-timing instrumentation properly: an env-gated
(`BLADE_PHASE_TIMING`) or `--timing` stopwatch report in
`lowerDiag`/`lowerDiagMulti`/`compileFile`, one line per phase to stderr.
Everything later cites this output; it also becomes the regression tripwire.
Add the §1 synthetic-scaling script to the bench notes.

### Stage 1 — Free constants: packaging + I/O (all S, low risk, ~independent)
*Implemented (F4/F5/F6/F7). See "Building for speed" below for the packaging half.*
1. **ReadyToRun** (F4): publish-based packaging with `PublishReadyToRun=true`;
   keep `dotnet build` for dev. Expected: −30–45% on every `check`/`emit`, −0.5 s
   on every `compile`/`run`. Consider later splitting the ~70 test modules out of
   the shipped binary to shrink the R2R image (M, riskier — `Cli.fs` opens them).
2. **Per-field lazy capability probes** (F5): −~700 ms on every `compile`/`run`.
3. **Header memoize + write-if-changed** (F6) and **stop re-reading the .cpp**
   (F7): small ms, large variance reduction (AV), ~1–2 s across the suite.

Combined expectation: interactive `blade run` on a small program drops from
~3.5 s to ~2.1 s of F#-side + orchestration work before touching g++.

#### Building for speed

`dotnet build` stays the dev loop and is deliberately untouched — it produces
the same `bin/Release/net7.0/Blade.exe` at the same path as before. The fast
image is a *publish*:

```bash
dotnet publish Blade.fsproj -c Release -r win-x64 --self-contained false -o out/r2r
```

`Blade.fsproj` sets `PublishReadyToRun` only when a `RuntimeIdentifier` is
supplied, so the RID on that command line is what turns it on (`-p:PublishReadyToRun=true`
is redundant but harmless). The condition is load-bearing: an unconditional
`PublishReadyToRun=true` makes the SDK infer a RID for plain `dotnet build`
too, which relocates the dev binary to `bin/Release/net7.0/win-x64/` and breaks
every consumer of the documented path.

Measured win (§3 F4): `check` −32%, `emit` −43%, `lsdft` compileFile
1068→591 ms, with hash-identical emitted C++. Re-confirmed on the Stage 1
tree against `examples/lsdft.blade`: `check` 865→605 ms (−30%), `emit`
1205→770 ms (−36%), `emit` output byte-identical between the two images. The
R2R image is ~2× the DLL size (11.5→24.7 MB) and is RID-specific — it is a
shipping artifact, not a dev one.

### Stage 2 — Front-end asymptotics (the scaling cliff)
*Implemented (F1/F2/F3). Achieved: 2000-line `check` 4819→477 ms, 4000-line
22824→582 ms (linear now); full 2000-line emit pipeline 10.4 s→0.6 s. `Token`
gained an `Index` field; `setEofFrom` builds the per-parse span tables, so every
entry point gets O(1) `consumedEnd` for free. One rendering change: in
multi-file mode a diagnostic in the ENTRY file now renders the path as typed
rather than absolutized (single-file mode always did); spans and codes
unchanged.*
1. **Lexer append → ResizeArray** (F1). Trivial, byte-identical.
2. **Token index for O(1) spans** (F2). The one genuinely delicate change:
   corpus span pins are the acceptance test.
3. **Single-parse pipeline** (F3): CLI lanes adopt `resolveParsedEntry`; thread
   member ASTs through `Resolution`.

Expected: n=2000 synthetic compile ~10.4 s → ~1.6 s (parse ~4.3 s → tens of ms,
re-parse eliminated). Real 100–200-line files gain ~150–250 ms (the resolve-layer
gap); every future file gains the headroom — this is what makes 5k-line Blade
programs viable at all.

### Stage 3 — Middle/back-end structural hygiene (S–M each, batch as one PR)
*Implemented, with two notes: (a) 11 `mapIRExpr`-as-visitor sites were converted
(a wider sweep than the 5 in F8), and zero `|> ignore`d `mapIRExpr` calls
remain; (b) the `liftInlineFormsModule` gate was deliberately DROPPED — its
`liftExpr` is a ~100-arm traversal whose sound trigger predicate includes
`IRCompute _`, true for essentially every program, so a gate buys nothing; the
`lowerArrayBinOpsModule` gate shipped and skips the pass on ~98% of modules
(measured 344/352 across four categories).*
`iterIRExpr` + convert the 5 visitor sites (F8); gate the two unconditional IR
passes (F9); `ResizeArray` accumulators in `genModule`/`genModuleSplit` + the
small CodeGen fixes (F10); `canonTypeKey` for the HM dedup (F8). Ride-alongs
while in the area: `inferBlock` append, `staticEnvOf` cache. Expected: modest
today (codegen/lower are 100–400 ms), but removes every known quadratic from the
middle end before programs get big enough to hit them.

### Stage 4 — Suite-level throughput (only lever on the 89%)

#### 4.1 Emitted-C++ → executable cache (M, medium risk)

**Where.** Entirely inside `Build.compileCppWithExtraSource` (Build.fs:457), after
the g++ `args` string is assembled and `cppText` is in hand, before the process
spawn. Every consumer — `Cli.compileToExe`, `blade run`, both harness lanes, the
provider blocks — calls through this function, so nothing else changes and
`tests/Runner.fs` is untouched (that file belongs to 4.2).

**Key.** SHA256 over, in order:
1. compiler identity: resolved `g++` path + first line of `g++ --version`
   (probed once, memoized per process);
2. the `args` string with the two volatile absolute paths (`exeFullPath`,
   `cppFullPath`) replaced by fixed placeholders — everything else in `args`
   (opt flags from `BLADE_MARCH`/`BLADE_FP_CONTRACT`, safety flags, BLAS/LAPACK
   defines and `-I`/link flags, netcdf/mpi flags) is config that must key;
3. `cppText` (the full translation unit);
4. the contents of all 13 runtime headers (`CodeGen.runtimeHeaderNames`, via the
   memoized reader) — the TU `#include`s a subset with quotes, hashing all of
   them over-invalidates, which is the stance;
5. for any explicit `*.dll` path embedded in the link flags (OpenBLAS/NetCDF
   direct-DLL linking): its size + last-write-time (an in-place DLL upgrade
   invalidates without a path change).

**Not cached (v1, over-invalidation bias):** the memcheck lane
(`compileCppMemcheck` — different compilers, measurement builds), any call with
non-empty `extraLinkInputs` or a cuBLAS `deviceInputs` build (multi-compiler
links), and non-Windows (untested here).

**Layout & flow.** `%LOCALAPPDATA%\Blade\exe-cache\<hash>.exe`. Hit: copy the
cached exe to `exeFullPath` (the exe-beside-source contract every caller and
test relies on is preserved), bump the cache file's mtime, return `Ok`. Miss:
run g++; on success, copy the exe into the cache via temp-file + atomic
`File.Move` (parallel harness compiles race here; a lost race is a no-op).
Eviction on store: if the directory exceeds 8192 entries or 6 GB, delete
oldest-mtime entries down to 3/4 of the cap (the full suite populates ~4.6k
entries; caps must exceed one suite generation).

**Gates.** `BLADE_EXE_CACHE`: unset/`1`/`on` = enabled, `0`/`off` = disabled, an
absolute path = enabled at that location. `--no-cache` on
`compile`/`run`/`test` sets the process-local env to `0` (read per-call, so the
existing pin/restore discipline holds). `--verbose` prints `[cache] hit/store
<hash8>`. A cache hit must be observationally identical to a compile: same
`Ok exeFullPath`, same exe bytes at the same path.

**Acceptance.** (a) run→edit→run loop: second identical `blade run` skips g++
(verify with `--verbose` + timing); (b) full suite twice: run 1 (cold) within
noise of baseline wall, run 2 (warm) target −50–80% of the g++ share; both
green with identical totals; (c) key honesty probes: flip `BLADE_MARCH`, edit a
runtime header on disk at the source (`src/cpp/`), rebuild, confirm misses;
(d) `--no-cache` forces a real compile.

*Implemented 2026-08-12* (`Build.compileCppWithExtraSource`). Key = SHA256
over the resolved g++ path + `g++ --version` line (memoized per process),
the command line with the exe/cpp paths replaced by placeholders, the .cpp
text, all 13 runtime header contents, and size+mtime of every DLL named
outright on the link line — so each env gate reaches the key through the
flags or the source text it already changes. Entries land in
`%LOCALAPPDATA%\Blade\exe-cache\<sha>.exe`, published by temp-write +
atomic move (a lost race is a no-op) and evicted oldest-mtime-first to 3/4
of the caps at >8192 entries or >6 GB. Gates: `BLADE_EXE_CACHE`
(unset/`1`/`on` = on, `0`/`off` = off, absolute path = custom location) and
`--no-cache` on `compile`/`run`/`test`; `blade run --verbose` traces
`[cache] hit|store <hash8>` (carried as the process pin
`BLADE_EXE_CACHE_VERBOSE`). v1 skips the memcheck lane, non-Windows, and
any compile with extra link inputs or a cuBLAS device half (their inputs
are not in the key). Measured (this box, Release JIT build; category counts
byte-identical warm and cold):

| Scenario | Cold / `--no-cache` | Warm (cache hit) |
|---|---|---|
| `blade run examples/lsdft.blade` | 2.4–4.0 s | 1.30–1.43 s |
| `blade test basic` (41) | 8.1–12.0 s | 2.7–4.2 s |
| `blade test sql` (103) | 24.3–25.9 s | 5.3–6.8 s |
| `blade test indextypes` (238) | 38.2–44.9 s | 7.9–14.7 s |

A hit is not *byte*-identical to a fresh compile, because a fresh compile is
not byte-identical to itself: two consecutive g++ runs on the same input
differ in 3–4 bytes (the PE `TimeDateStamp` and its debug-directory twin).
Fresh-vs-cached differs in exactly the same 4 bytes, same length. Key-honesty
probes passed: `BLADE_MARCH=x86-64` produced a new key; `BLADE_EXE_CACHE=0`
and `--no-cache` compile for real; a cross-process same-TU race stored once,
both exited 0, no temp residue.

#### 4.2 Harness F# parallelism experiment (M, high risk, opt-in only)

**Free part (riskless, do unconditionally):** the lock's justification comment
(`tests/Runner.fs:110-126`) names two caches — `structFieldsCache` is already
`AsyncLocal` (IR.fs:5478) and `codegenStructFieldsCache` no longer exists (its
stale mention at CodeGen.fs:9465 goes too). Rewrite the comment to state the
real, current situation: the lock serializes the F# pipeline as a *conservative
guard* over ~30 AsyncLocal side-channels whose isolation under
`Array.Parallel.mapi` is unproven, because `AsyncLocal` values written in one
iteration persist into later iterations scheduled on the same worker thread.

**Experiment part:** `BLADE_TEST_FSHARP_PARALLEL=1` (read per-call) turns the
lock body into a no-op. Default stays LOCKED. Why it may be sound anyway: every
per-compile channel is reset at `typeCheck` entry, and `runOnLargeStack` gives
each test a fresh dedicated thread whose AsyncLocal writes do not flow back —
but that reasoning is exactly what the flake hunt must test, not assume.

**Promotion criterion:** flip the default only after ≥10 full-suite greens with
the gate on across ≥2 sessions, with zero unexplained diffs vs the locked run's
totals. Expected ceiling −11% suite wall (the freed F# CPU overlaps g++), so
abandon without guilt at the first flake.

**Implemented (opt-in), 2026-08-12 — and the expected failure mode FIRED.**
Gate: `BLADE_TEST_FSHARP_PARALLEL=1|on` runs the harness F# pipeline without
taking `fsharpPipelineLock` (`tests/Runner.fs`, `fsharpParallelEnabled` /
`withFsharpPipelineLock`; the lock has exactly one use site and it goes
through the helper). Read per call, not cached. Default (unset/`0`/anything
else) keeps the lock, byte-for-byte as before. The two stale comments are
corrected in the same change (`tests/Runner.fs` lock docstring,
`src/CodeGen.fs` `genScalarBinding`): `structFieldsCache` is AsyncLocal
(`IR.fs`, `structFieldsCacheStorage`), and `setCodegenStructFieldsCache`
merely forwards into it — there is only ONE struct-fields cache.

Stress evidence (gate ON, Release, ucrt64 g++, category counts vs the locked
reference `238 passed / 0 failed / 0 skipped` and `163 passed / 0 failed /
0 skipped`):

| Category | Run | Result | Wall |
|---|---|---|---|
| indextypes | 1 | 238/0/0, C++ 159, Compiled 158, Full 158, Values 153/153 | 39.7 s |
| indextypes | 2 | identical | 37.6 s |
| indextypes | 3 | **CRASH after ~10 tests** | 8.8 s |
| indextypes | 4 | identical to run 1 | 38.4 s |
| loops | 1 | **CRASH after ~29 tests** | 27.3 s |
| loops | 2 | 163/0/0, C++ 150, Compiled 148, Full 146, Values 146/146 | 33.6 s |
| loops | 3 | identical | 32.6 s |

Verbatim failure (both crashes, identical text, no test name attached):

```
error[BL9001]: internal compiler error: One or more errors occurred. (Index was outside the bounds of the array.)
  = note: this is a bug in the Blade compiler, not in your program -- please report it
```

It aborts the whole category run, not one test, and it is intermittent (2 of
7 unlocked runs; 0 of 4 locked runs of the same two categories, before and
after the change). Not chased — this is precisely the AsyncLocal-under-
`Array.Parallel.mapi` hazard the caveat predicted, and it lands on some
shared per-flow list/array read at an index another iteration's value made
invalid.

Wall-clock, locked vs unlocked (clean runs only): `indextypes` 55.3 s / 43.2 s
locked vs 37.6–39.7 s unlocked (~−15% against the faster locked run);
`loops` 32.6 s / 34.2 s locked vs 32.6–33.6 s unlocked (no measurable gain —
the category is g++-bound). So even the upside is smaller than the −11%
ceiling suggests for the shorter categories.

**Default stays LOCKED.** Promotion criterion is unchanged and currently not
met: ≥10 consecutive clean full-suite runs with the gate on. The gate exists
so the intermittent can be reproduced and diagnosed on demand, not because it
is ready.

### Stage 5 — Profile-gated follow-ups (instrument first, act on data)

#### 5.1 Perf counters (`BLADE_PERF_COUNTERS=1`)

A `PerfCounters` module (TypeEnv.fs or its own file before Unify.fs): Interlocked
counters + an enabled flag *refreshed at pipeline entry* (`compileFile`,
`lowerDiag`, harness compile entry) rather than read per increment — a getenv
per `Resolve` call would itself be a hotspot; refresh-per-compile keeps the
test pin/restore discipline at compile granularity. Counters:
- `Subst.Resolve` (Unify.fs:495): calls, chain hops walked, max chain length;
- `IR.typeOf` (IR.fs:5659): calls, plus calls that recursed (non-CarriedType);
- `validateIR` per-walk share is already visible via `BLADE_PHASE_TIMING`.
Printed once per compile to stderr as `[perf] name: value` lines when enabled.

**Measurement set:** lsdft, 01_weather_stations, synth-2000 (flat lets),
block-800 (one big block), plus a deep-chain synthetic built for the purpose
(a single expression of ~200+ chained binops, un-let-bound) to probe the
typeOf/exprToCpp quadratics specifically.

#### 5.2 Entry criteria — implement only what the data clears

| Candidate | Implement iff | Fix |
|---|---|---|
| `Subst.Resolve` path compression (Unify.fs:495-517) | max chain > ~16 on real programs, or chain hops grow superlinearly in program size | compress on resolve; the id-keyed side tables (arity/rank/literal/poly marks) must keep their travel-with-the-bind semantics — audit each `Bind` caller |
| `typeOf` memoization (IR.fs:5659) | typeOf calls ≳ 5× IR node count, or deep-chain synthetic shows superlinear codegen time | `ConditionalWeakTable<IRExpr, IRType>` keyed on node reference; pure-structural results only |
| `validateIR` walk fusion | validateIR > 10% of compileFile on the measurement set (today ~2–6% — expected NOT to trigger; record the numbers and close it) | fuse the 5 walks into one descent |
| n-ary flattening for emit chains | only if typeOf memo is insufficient on the deep-chain probe | flatten at lowering, never in `exprToCppCore` |

Byte-identity gate applies to every 5.2 change; counters themselves must be
output-neutral (stderr only, gated off by default).

**Implemented 2026-08-12.** Counters first (`src/PerfCounters.fs`,
`BLADE_PERF_COUNTERS=1`, `[perf] name: value` on stderr beside `[phase]`), then
only the one candidate whose numbers cleared its bar.

`PerfCounters` is an int64[] behind a plain static `bool`: a disabled increment
is one field load and a predicted branch, no getenv (the gate is refreshed at
`Cli.compileFile` and the three `Lowering.lower*Diag` entries). Instrumented:
`Subst.Resolve` (invocations; chain walks, hops, longest chain -- the `IRTInfer`
self-recursion became an equivalent tail-recursive member so the hop count is
observable without changing what it returns) and `IR.typeOf` (invocations,
`CarriedType` fast-path hits, memo hits), plus `IR.countProgramNodes` for the
calls-per-node ratio.

Measurement set -- min of 5 `blade emit` runs per file, JIT dev build, counters
off for the timings and on for the counts. The two chain probes are one
un-let-bound expression of n `x * k` terms:

| Probe | IR nodes | typeOf calls | calls/node | Resolve calls | chains | max chain | codegen before | codegen after | total before | total after |
|---|---|---|---|---|---|---|---|---|---|---|
| `examples/lsdft.blade` | 153 | 296 | 1.9 | 1532 | 346 | **2** | 220 ms | 220 ms | 1006 ms | 1004 ms |
| `examples/01_weather_stations.blade` | 418 | 560 | 1.3 | 5233 | 531 | **2** | 307 ms | 308 ms | 1021 ms | 1028 ms |
| 2000 flat top-level lets | 6003 | 4002 | 0.7 | 30015 | 0 | 0 | 90 ms | 79 ms | 577 ms | 552 ms |
| one 800-statement block | 3205 | 1611 | 0.5 | 11234 | 0 | 0 | 127 ms | 118 ms | 645 ms | 611 ms |
| chain, 200 terms | 800 | 80398 | 100.5 | 3403 | 0 | 0 | 80 ms | 75 ms | 443 ms | 442 ms |
| chain, 500 terms | 2000 | 500998 | 250.5 | 8503 | 0 | 0 | 96 ms | 76 ms | 480 ms | 446 ms |
| chain, 1000 terms | 4000 | 2001998 | 500.5 | 17003 | 0 | 0 | 165 ms | 84 ms | 583 ms | 487 ms |
| chain, 2000 terms | 8000 | 8003998 | 1000.5 | 34003 | 0 | 0 | 376 ms | **98 ms** | 875 ms | 592 ms |

("before" = this branch without Stage 5. Both sweeps ran back-to-back on the
same quiet machine with parse times agreeing within 3%; an earlier pair was
thrown out for machine-load drift -- a concurrently building agent moved parse
time 1.8x, which is larger than everything measured here. typeOf counts are the
*before* traffic: after the memo, chain-2000 falls 8,003,998 -> 15,994.)

Verdicts, one per candidate:

- **`Subst.Resolve` path compression -- CLOSED, bar not met.** Longest inference
  chain anywhere: **2 hops** (both examples; <= 1 across all 199 corpus programs
  swept), 0.39-0.89 hops per chain walk, and zero chain walks on every
  synthetic. The bar was a max chain > ~16 or superlinear hop growth. There is
  nothing to compress, so the travel-with-the-bind audit of `arityConstraints` /
  `rankLowerBounds` / `literalDefaults` / `polymorphicIds` was not needed and is
  not owed.
- **`typeOf` memoization -- IMPLEMENTED.** typeOf traffic on the chain probes is
  exactly 2n^2 (100x the node count at n=200, 1000x at n=2000), far past the
  "≳5x node count" bar, and the wall clock bends with it. A
  `ConditionalWeakTable<IRExpr, IRType>` sits BEHIND the `CarriedType` fast path
  (a table probe would cost more than a carried answer), so only reconstructing
  arms consult it. Soundness: every arm is a pure function of node structure
  except `IRFieldAccess`, which reads the struct-fields cache -- so the memo is
  dropped whenever `setStructFieldsCache` installs a new generation, and an
  entry can only outlive a generation in which nothing changed. Real programs
  are unaffected either way (1.3-1.9 calls/node), but it is not synthetic-only:
  `index-types/049_decompact_anti_interior_r5` runs 35.8 calls/node and takes
  979 memo hits.
- **`validateIR` walk fusion -- CLOSED, as predicted.** Its share of a compile:
  1.5% (lsdft, 01_weather_stations), 2.2% (800-statement block), 3.5% (2000 flat
  lets) -- inside the estimated 2-6%. The only probe where it grows (9.5% at
  chain-2000) is the degenerate one the memo already fixed.

Byte-identity: 281 files (`tests/corpus/basic`, `tests/corpus/index-types`, both
examples) x cpp/stdout/stderr/exit plus the deployed headers = 1055 artifacts,
zero diffs before vs after. With `BLADE_PERF_COUNTERS=1` the only change is
added `[perf]` lines on stderr (199 files -- the ones that reach codegen), never
a `.cpp` byte. `blade test basic` 41/0/0 and `blade test indextypes` 238/0/0,
both unchanged from the pre-Stage-5 build.

## 5. Expected vs achieved (Stages 0–3 landed 2026-08-12)

| Scenario | Before | Predicted after 1–3 | Achieved (JIT dev build) | Mechanism |
|---|---|---|---|---|
| `blade check` small file | ~460 ms | ~250 ms | ~370 ms JIT; ~250 ms on the R2R image | R2R + single parse |
| `blade emit` lsdft (pipeline) | 1068 ms | ~0.55 s | 994 ms JIT / **591 ms R2R** | R2R + parse fixes |
| `blade compile` (probe overhead) | +165–870 ms | −~700 ms | −127 ms warm GPU; scales with GPU idle state | lazy probes |
| 2000-line file, F# pipeline | 10399 ms | ~1.5 s | **602 ms** | front-end asymptotics |
| 4000-line `blade check` | 22.8 s | — | **582 ms** (linear now) | front-end asymptotics |
| full `blade test` | ~10 min | ~9 min; 2–5 min re-runs with Stage 4 cache | not re-measured; per-category re-runs 3.0–4.8× faster with the 4.1 cache | Stage 4.1 landed, 4.2 pending |

Byte-identity held at every merge point: 291-file sweep (1164 artifacts:
cpp/stdout/stderr/exit), zero diffs after Stage 1+3 and again after Stage 2 +
the master merge; the Stage 2 agent independently verified 439 files including
the diagnostics-heavy categories.
