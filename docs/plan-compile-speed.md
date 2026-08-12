# Plan: Compile-Speed Exploitation (F# pipeline, lexer → codegen)

Status: investigation complete 2026-08-12; nothing below is implemented except the
env-gated phase-timing instrumentation used to gather the numbers (uncommitted in
this worktree, proposed as Stage 0). All timings measured on the development box
(16 cores, Windows 11, .NET 7, Release build), warm, per-invocation unless noted.

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
  removal is an *experiment*, not a cleanup.
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
1. **ReadyToRun** (F4): publish-based packaging with `PublishReadyToRun=true`;
   keep `dotnet build` for dev. Expected: −30–45% on every `check`/`emit`, −0.5 s
   on every `compile`/`run`. Consider later splitting the ~70 test modules out of
   the shipped binary to shrink the R2R image (M, riskier — `Cli.fs` opens them).
2. **Per-field lazy capability probes** (F5): −~700 ms on every `compile`/`run`.
3. **Header memoize + write-if-changed** (F6) and **stop re-reading the .cpp**
   (F7): small ms, large variance reduction (AV), ~1–2 s across the suite.

Combined expectation: interactive `blade run` on a small program drops from
~3.5 s to ~2.1 s of F#-side + orchestration work before touching g++.

### Stage 2 — Front-end asymptotics (the scaling cliff)
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
`iterIRExpr` + convert the 5 visitor sites (F8); gate the two unconditional IR
passes (F9); `ResizeArray` accumulators in `genModule`/`genModuleSplit` + the
small CodeGen fixes (F10); `canonTypeKey` for the HM dedup (F8). Ride-alongs
while in the area: `inferBlock` append, `staticEnvOf` cache. Expected: modest
today (codegen/lower are 100–400 ms), but removes every known quadratic from the
middle end before programs get big enough to hit them.

### Stage 4 — Suite-level throughput (only lever on the 89%)
1. **Emitted-C++ → executable cache** (M–L, medium risk): content-addressed on
   hash(emitted .cpp + flags + deployed header contents + every emission-relevant
   env gate: BLADE_BLAS/CUBLAS/OMP_THREADS/FP_REASSOC/MARCH/FP_CONTRACT/MEMCHECK,
   OPENBLAS_DIR, NETCDF_DIR). Skip g++ on hit; explicit `--no-cache`; key errs
   toward over-invalidation. Side-channels don't block it — the cache stores
   artifacts, not pipeline state. Expected: near-total on warm `blade run`;
   −50–80% on suite re-runs where sources didn't change.
2. **`fsharpPipelineLock` removal experiment** (M, high risk): first the free
   part — delete the stale `codegenStructFieldsCache` comment and correct the
   lock's justification (S, riskless). Then, behind a flag, remove the lock and
   run the full suite ≥10× hunting intermittents (AsyncLocal-under-Parallel leak
   is the expected failure mode). Ceiling −11% suite wall; abandon without guilt
   if flaky.

### Stage 5 — Profile-gated follow-ups (do not start without data)
With Stage 0 timing + a real profiler pass (dotnet-trace/ETW on a large program):
`Subst.Resolve` path compression (instrument chain lengths first), `typeOf`
memoization / n-ary flattening for deep un-let-bound chains, fusing
`validateIR`'s 5 walks. Each is only worth it if the profile says so.

## 5. Expected end state

| Scenario | Today | After 1–3 | Mechanism |
|---|---|---|---|
| `blade check` small file | ~460 ms | ~250 ms | R2R + single parse |
| `blade emit` lsdft | ~1.4 s | ~0.55 s | R2R (measured 591 ms) + parse fixes |
| `blade run` small file (F# side) | ~3.5 s | ~2.1 s | + lazy probes, header/IO hygiene |
| 2000-line file, F# pipeline | ~10.4 s | ~1.5 s | front-end asymptotics |
| full `blade test` | ~10 min | ~9 min; **2–5 min re-runs with Stage 4 cache** | 89% is g++ |
