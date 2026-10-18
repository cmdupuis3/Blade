# Fastest by default: licensed folds reassociate, reproducibility is the opt-in

Status: BUILT 2026-10-17 (worktree branch `worktree-agent-a20fc80bc8b7d458f`, not
merged). Default = every source licence spent in both back ends; `BLADE_FP_REPRO=1`
(or `BLADE_FP_REASSOC=0`) = serial association in every fold; LLVM contraction default
aligned with the g++ lane. Open: §6.

The owner's policy (2026-10-17): Blade's default emission is "always fastest" -- every
source licence honoured by default, including simd reductions on licensed folds, in BOTH
back ends -- and reproducibility (serial fold association, byte identity with the
interpreter) is the OPT-IN, never the default. `where omp` implies simd. Map loops already
kept their `omp simd` / `BLADE_IVDEP` hints in serial builds; the gap was folds.

Evidence that motivated it (bench worktree `bench-fortran`, read-only):
`bench/notes/llvm-acceptance-B.md` F3/F4/F11, `llvm-acceptance-A.md` F14/F15,
`gap-register.md` G7, G10, G11, G14, G19, G20, G21.

## 1. Knob semantics

| Setting | Licensed fold (builtin `+`/`*` body, declared `comm`, `where omp`, `prodsum`, builtin join legs) | Unlicensed fold | Contraction |
|---|---|---|---|
| nothing set (DEFAULT) | fastest licensed form: C++ `omp simd reduction` / K lanes per site (and a `where omp` fold's thread team when the build opens teams); LLVM `reassoc nsz` on the accumulator | serial | C++ `-ffp-contract=fast`; LLVM per-instruction `contract` (NEW: was off) |
| `BLADE_FP_REPRO=1` | ascending serial association everywhere, thread-level fold forms included (census line `[omp] requested but emitted serial: BLADE_FP_REPRO=1 -- ...`) | serial | unchanged (its own knob) |
| `BLADE_FP_REASSOC=0`/`off` | = `BLADE_FP_REPRO=1` (compatibility spelling; the marker says `BLADE_FP_REASSOC=0`) | serial | unchanged |
| `BLADE_FP_REASSOC=1`/`on` | = default; additionally lets the Reynolds term merger (`canonicalKey`) flatten float `+`/`*` chains -- not a source licence, so never on by default | serial | unchanged |
| `BLADE_FP_REPRO=1` + `BLADE_FP_REASSOC=1` | REPRO wins | serial | unchanged |
| `BLADE_FP_CONTRACT=off` | -- | -- | off in BOTH lanes |
| byte identity with the interpreter | `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` (+ BLAS off, its default without `OPENBLAS_DIR`) | | |

Both knobs are read per call (`CodeGenState.fpReproEnabled` / `fpReassocEnabled` /
`fpReassocExplicitOptIn`; `EmitLlvm.contractLicensed`), never cached. `blade test` clears
both (`suiteClearedKnobs`). Neither lives in `blade.toolchain.json` (the old knob never
did; `envFlag` reads the process environment only), so there is no doctor/toolchain row
to mirror. The exe caches key on the emitted text, the g++/clang argument string (which
carries `-ffp-contract=` and `-DBLADE_RR_REASSOC=`) and the runtime-header digest, so
every new form keys itself -- verified in `Build.exeCacheKey` / `llvmExeCacheKey`; no
cache change.

Decision recorded on contraction (task gap (b)): the LLVM lane's opposite polarity
(unset = no `contract`) existed only to make DEFAULT IR byte-identical to the
interpreter for free (plan-llvm-backend §5's "favorable inversion"). Under this policy
that is an opt-in in both lanes, so the default is aligned with g++ (`fast`). Measured
cost of the old default: 0 `vfmadd` in the gram/comoment3/antisym exes vs 23 + 31 with
the flag (G19 / F14). `BLADE_FP_REPRO` does NOT touch contraction -- the two knobs stay
orthogonal and the harnesses pin both.

## 2. Census (step 1)

### 2.1 The `fpReassocEnabled ()` sites and the form each chooses

| Site | Shape | Form (default build) | Plan line |
|---|---|---|---|
| `CodeGenBinding.genReduceBinding`, Path A, threads on | `where omp` builtin `+`/`*` fold over a materialized rank-1 array, own `let` | `BLADE_OMP_PARALLEL_FOR_SIMD_REDUCTION` | `threaded(parallel for simd reduction)` |
| same, Path A, `BLADE_OMP_THREADS=1` | same | `BLADE_OMP_SIMD_REDUCTION` (`fpReassocSimdStmts`) | `simd-reduction` |
| same, Path B, threads on | `where omp` comm-declared / non-scalar fold | team + per-thread chunks, 8 named lanes per chunk | `threaded(Path B chunks, K-lanes(8) per chunk)` |
| same, no `omp`, licensed | `reduce(x, (+))`, a `comm` kernel | K lanes, `foldLaneCount` = 8 (measured over simd here: 1.12x bandwidth-bound, 1.90x cache-resident) | `K-lanes(8)` |
| `genReduceComputeBindingCore`, one leaf, one level | reduce over a deferred computation (zip map, composed `>>@` chain, map over a computed array) | builtin op + scalar: `omp simd reduction` (1.64x / 3.81x over lanes, measured); `comm` kernel: K lanes, `laneCountForStreams` | `simd-reduction` / `K-lanes(k)` |
| same, `FoldChunk` (`where omp`), threads on | same | Path B team, serial within each chunk | `threaded(Path B chunks, serial within each chunk)` |
| same, `FoldChunk`, `BLADE_OMP_THREADS=1` | same | NEW: the simd / lane form above, with the census line for the dropped team (used to be the serial chain -- G20/F11) | `simd-reduction` |
| same, L1 dispatch | `reduce(<unforced zip under *>, (+))` with BLAS on | `blade_dot` | `blas-dot` |
| fused tree (`reduce(A <&!> B, k)`) | several leaves, one shared kernel | NEW: one `BLADE_OMP_SIMD_REDUCTION(+:a_0, a_1, ...)` over every accumulator (macro made variadic) | `simd-reduction` |
| `genReduceJoinCore` (`object_for(<&!>) <@> (...)`, per-leg kernels) | several legs, shared deferred operands | NEW: same multi-accumulator clause when every leg is the same builtin op | `simd-reduction` |
| `CodeGenExpr` `IRProdSum` IIFE | `prodsum(...)` anywhere (kernel bodies included) | `omp simd reduction` (scalar element; 2.99x geomean), else K lanes by stream count | `simd-reduction` / `K-lanes(k)` |
| `CodeGenExpr.renderReduceExpr` | reduce in expression position (a row kernel's per-row fold, partial folds) | K lanes (8) | `K-lanes(8)` |
| `CodeGenLoopNest.canonicalKey` | Reynolds term merger | NOT the default any more: explicit `BLADE_FP_REASSOC=1` only (`fpReassocExplicitOptIn`) | -- |
| `CodeGenLoopNest.tryGenRowFoldJamNest` | row fold / matvec over rank-2 rows | the gate USED to decline under the knob (leaving per-row lane/simd folds); it no longer reads it, so the bitwise jam -- the shape's old default -- stays the default (measured mixed, §6.2) | `row-fold jam(R rows, bitwise)` |
| `CodeGenLoopNest` FoldChunk consumer | Path B nest | REPRO drops the plan too (`foldChunkLive`) | -- |
| `Build.fs` | run record | `-DBLADE_RR_REASSOC=` follows the default (1) | -- |

LLVM gate: `EmitLlvm.foldFmfLicensed = fpReassocEnabled () && foldReorderLicensed cl`
(and `foldFmfDecorable`: only a builtin-op kernel's single combining instruction carries
the flags); the prodsum accumulator (`withFoldFmf c (fpReassocEnabled ())`); compact-fold
bricks (`brickable = foldFmfLicensed`, unreachable from the surface today). Unchanged
predicate, flipped polarity.

### 2.2 Corpus tests reading licensed float folds

Files under `tests/corpus` (2026-10-17): 507 use a builtin `reduce(.., (+)/(*))`, 88
`prodsum`, 53 a `<&!>` join, 4 a `where omp` fold kernel; essentially every value test
reads some licensed fold. What makes the flip safe for the default suite is the pin
rule: an `// EXPECT:` holds at 1e-9 relative and the printer prints 15 significant
digits, so a reassociation that moves the last ulp is invisible to it. A pin is at risk
only where a licensed fold CANCELS (a result many orders below its summands). The
association-sensitive tests in the corpus (`reynolds/026`, `symmetry/086`, the
`(-)`-fold rule tests) fold UNLICENSED kernels or are not folds at all, so they are
unchanged by construction; integer-valued data is exact under any association. Result
of the full default suite run under the new default: §5.

### 2.3 Gates that compare codegen against a serial twin

| Gate | Comparison | Sees a last-ulp change? | Policy after the flip |
|---|---|---|---|
| `blade test interp` (`tests/InterpDiff.fs`) | normalized stdout BYTE-equal, then the pins | yes (15 printed digits hide most, not all) | pins `BLADE_FP_REPRO=1` + `BLADE_FP_CONTRACT=off` |
| `blade test diff-oracle` (`tests/DiffOracle.fs`) | byte-equal vs a pinned older binary | yes | same pins (reach the oracle subprocess by env inheritance) |
| `blade test opt-diff` (`tests/OptDiff.fs`) | gates off vs on, normalized byte-equal | yes: fusion changes WHICH fold form a shape gets (a fused chain's simd fold vs a materialized array's lanes) | pins `BLADE_FP_REPRO=1` + contract off |
| `blade test llvm` differential | two lanes, 1e-9 relative per token | no (tolerance) | both lanes run their fast defaults |
| `blade test omp-reduce` fp-reassoc block | knob on vs `0`, 1e-9 / exact on integer data | by design | unchanged (`0` is still the serial build) |
| `blade test llvm goldens` / `facts` | emitted `.ll` text | yes | goldens re-pinned (`contract` + `reassoc nsz` on the fold); fact cases rewritten for the new default and the repro spellings |
| `blade test setup` knob grammar | `fpReassocEnabled` default | yes | `BLADE_FP_REPRO` takes the default-off row; `BLADE_FP_REASSOC` gets a default-on row |

## 3. What the change does (steps 2-3)

1. `CodeGenState`: `fpReproEnabled`, `fpReproReason`, `fpReassocEnabled = not repro`,
   `fpReassocExplicitOptIn`, `recordFoldForm` (rule `fold-form`), `serialFoldForm`.
2. Thread-level fold forms honour REPRO: `parallelFold` (Paths A/B) and the Path B nest
   consumer decline under it, each with the census phrase naming the knob.
3. "omp implies simd" for deferred computations: a one-level reduce over a deferred
   computation whose `omp` chunk plan is dead (`BLADE_OMP_THREADS=1`) takes the
   vectorized form instead of the serial chain. Fixes gap (a) for the dot, composed
   chain, saxpy-checksum shapes (F11).
4. Joins and fused folds: `fusedFoldSimdPlan` + `genFusedLoopNestStreamedWith` emit one
   `BLADE_OMP_SIMD_REDUCTION(<op>:<all accumulators>)` (the macro is now variadic) when
   every leg is the same builtin op over a scalar, one rectangular level, no streamed
   source. Gap (a) for `<&!>` (onepass).
5. Gap (d): `where omp` on a join's / fused fold's kernel now prints the census line
   (`a reduction join keeps several accumulators in one traversal and has no
   thread-level form yet (...)`). `reduce(<materialized rank-k>, k where omp, init, axes =
   rank)` used to desugar into a clause-less nest (silent); it is re-spelled as the
   identity map over the operand's cells, so it reaches the fused terminal: Path B threads
   the outer level, `BLADE_OMP_THREADS=1` prints the census line, REPRO prints its own.
6. Gap (c): every C++ fold site records `fold form: ...` for `blade plan`.
7. LLVM: `contract` by default; `reassoc nsz` by default on licensed accumulators.

Before / after (probe `p2_dot`: `reduce(method_for(zip(a, b)) <@> lambda(x, y) -> x * y,
lambda(p, q) where omp -> p + q, 0.0)` in a function, `BLADE_OMP_THREADS=1`):

```
before (default AND BLADE_FP_REASSOC=1):
    // [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- ...
    for (size_t __i0 = 0; __i0 < 1009; __i0++) {
        double a____i0 = a[__i0];
        double b____i0 = b[__i0];
        __v24 = __wrap_29___v24(__v24, (a____i0 * b____i0));
    }
after (default):
    /* [omp] requested but emitted serial: BLADE_OMP_THREADS=1 -- ... */
    /* reduce over computation: accumulator loop (omp simd reduction, licensed reorder, 2 operand streams) */
    BLADE_OMP_SIMD_REDUCTION(+:__v24)
    for (size_t __i0 = 0; __i0 < __rhi; __i0++) { ... __v24 = __v24 + (__rsrc0____i0 * __rsrc1____i0); }
after (BLADE_FP_REPRO=1): the "before" text.
```

Probe survey (`fp_probes/survey.ps1`, `BLADE_OMP_THREADS=1`; C++ `BLADE_OMP_SIMD_REDUCTION(`
count / LLVM `reassoc` instruction count, before -> after; REPRO = 0 / 0 everywhere):

| probe | before default | before `REASSOC=1` | after default |
|---|---|---|---|
| p1 `reduce(x, where omp)` + `reduce(x, (+))`, top level | 1 / 0 | 1 + 8 lanes / 2 | 1 + 8 lanes / 2 |
| p2 dot (zip map, omp fold, in a function) | 0 / 0 | 0 / 1 | 1 / 1 |
| p3 four-leg `<&!>` join (onepass) | 0 / 0 | 0 / 4 | 1 / 4 |
| p4 composed `>>@` chain, omp fold | 0 / 0 | 0 / 1 | 1 / 1 |
| p5 checksum over a computed zip map | 0 / 0 | 0 / 1 | 1 / 1 |
| p6 two-leg join, omp fold kernel | 0 / 0 (silent) | 0 / 2 | 1 / 2 + census line |
| p7 `reduce(a * a, where omp, 0.0, axes = 2)` | silent serial nest | silent | Path B (threads) / census line (serial build) |
| p8 `prodsum(a, b)` | 0 / 0 | 1 / 1 | 1 / 1 |

All eight run in both lanes and both builds; default and repro agree to the printed 15
digits except where the reassociation is real (p2, p8: last printed digit).

## 4. Test policy (step 4)

Option (b): the byte-identity differentials run the REPRO build; the default suite runs
the fast default. Chosen because it keeps the interpreter twin byte-honest (no tolerance
anywhere in a gate whose job is "two evaluators agree") and needs no pin loosening:
`InterpDiff.pinFpContractOff` and `DiffOracle.pinFpContractOff` now pin
`BLADE_FP_REPRO=1` beside `BLADE_FP_CONTRACT=off`, and `OptDiff`'s block-level `withEnv`
does the same. The default suite's pins are 1e-9 relative already, which is exactly the
association-invariant reading. Rule for new tests: a test about a VALUE under
cancellation uses integer-valued data or an unlicensed kernel; never loosen a pin to
absorb a reassociation. Documented in CLAUDE.md (Testing) and formalism §2.4.

## 5. Verification (step 6)

From this worktree, `C:\msys64\ucrt64\bin` first on PATH, one heavy process at a time
(2026-10-17):

| Gate | Total |
|---|---|
| `blade test` (default suite, fast default) | 6554 passed, 0 failed (no skips) |
| `blade test interp` (REPRO + contract off) | 2845 passed, 0 failed, 2 skip-unsupported (the listed `interpExpectedUnsupported` pair), 0 skipped |
| `blade test opt-diff` (REPRO + contract off) | 1934 passed (1800 optimizer-inert), 0 failed, 913 skipped (reject probes both builds refuse) |
| `blade test docs` | 130 passed, 0 failed, 78 skipped (all `sketch` fences) |
| `blade test llvm` | 633 passed, 0 failed, 684 skipped (366 reject probes, 316 lane refusals, 2 simplex fixtures the front end refuses for BL3020 -- none fp-related); coverage 545/861 = 63.3% |
| targeted: `loops`, `symmetry`, `omp-reduce` (84, incl. 3 new emission cases), `omp-pragma`, `omp-coverage`, `llvm loops`, `interp loops`, `linalg`, `shape-spec`, `optimize`, `setup`, `llvm goldens` | all 0 failed |

The first full run had 10 reds, all EMISSION-SHAPE pins written against the old serial
default: three re-spelled for the default form (`(__rsrc0____i0 * __rsrc1____i0)` in two
LinAlg dot cases, `__rn = 5` in two shape-spec cases, two Optimize regexes accepting the
builtin update `acc = acc + (e)` beside the wrapper call), and four (gemv jam, two
lifted-kernel bounds, `plan_row_fold_jam_applied`) by restoring the row-fold jam as the
default (§6.2). No value pin changed.

## 6. Open

1. Shapes that still cannot take a fast form (each prints a `fold form: serial (...)`
   plan line, and a census line where `omp` was asked):
   - multi-level fold nests over a deferred computation (`reduce(.., axes = 2)` and
     friends): no simd/lane form; Path B threads the outer level when teams are on,
     serial (census line) under `BLADE_OMP_THREADS=1`.
   - joins / fused folds with a `comm`-declared (call-combine) leg or mixed operators:
     serial (no multi-accumulator lane form; one reduction clause names one operator).
   - joins have no THREAD form at all (G7): the census line says so; a per-accumulator
     `reduction` clause on a parallel loop is the upgrade path.
   - `prodsum` has no threading licence slot (G14); its reassociation is the default now.
   - the expression-position reduce opens no team (structural; unchanged).
   - simd inside Path B chunks (each thread's chunk is still a serial chain).
2. Row-fold jam vs per-row lane/simd forms. The jam declined under the OLD knob; with
   the knob's forms now the default that would have silently changed every row fold's
   default emission, so it was measured (12 row folds per program, `OMP_NUM_THREADS=1`,
   medians of 9-15 interleaved runs, whole-program time): at 2003 x 1009
   (bandwidth-bound) the jam was faster (1.10x `reduce`, 1.30x `prodsum`); at 403 x 251
   (cache-resident) the per-row forms were faster (1.31x / 1.09x). No winner, so the jam
   (bitwise, the old default) stays the default in every build; a regime-aware choice
   (or lanes inside the jam) is the follow-up.
3. Benchmark harness consequences: "licensed" regime = no knob; "repro" regime =
   `BLADE_FP_REPRO=1` (folds serial INCLUDING the mt8 fold teams -- the old repro regime
   kept Path B teams); add `BLADE_FP_CONTRACT=off` only for interpreter byte identity.
   The suite's D34 (`BLADE_FP_CONTRACT=fast` on LLVM arms) is now the default.
