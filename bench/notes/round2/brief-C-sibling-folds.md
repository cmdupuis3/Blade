# C -- sibling-fold traversal sharing + V1 automatic sibling-fold discovery

Research brief, round 2. Master 4136f0b5, Release binary `bin\Release\net10.0\Blade.exe`.
Every claim marked VERIFIED was checked against emitted C++/LLVM or a run. Experiments live in
`<scratch>\round2\C\` (`<scratch>` = this session's scratchpad): hand-joined spellings
`dot_joined.blade`, `onepass_joined.blade`, `fusedchain_joined.blade`, the probes `negzero.blade`,
`n3probe*.blade`, and the emitted `*.cpp` / `*.ll` / `*.plan.txt` for each regime.

**Bottom line.** The lowering target exists and already does the right thing. A hand-written
explicit join of the bench spelling (kernel fold plus the input sums) compiles in both lanes into
ONE loop that peels each input once, with per-leg seeds, per-leg chains and per-leg LLVM flags.
Printed values match the current program in both lanes under `BLADE_FP_REPRO=1
BLADE_FP_CONTRACT=off`. What is missing is the discovery rule. It needs three things the
planning docs did not anticipate:

1. **A one-level inliner.** The kernel fold sits inside `kernel()`, which the module binding's
   block calls. Nothing before the lane split inlines it. (The LLVM lane does inline it, but only
   while it emits the tuple: `EmitLlvm.emitTupleParts`, `:6252`.)
2. **An exact seed for unseeded legs.** Surface join inference seeds `(+)` with **+0.0**, which
   changes an all-negative-zero sum from `-0.0` to `0.0` (VERIFIED, both lanes and the
   interpreter). Seeding with **-0.0** instead (1.0 for `(*)`, 0 for ints) gives the same chain
   bit for bit when n >= 1.
3. **A thread gate.** A join has no thread-level form, so under mt8 licensed the rewrite would
   serialize dot's and fusedchain's Path B team (VERIFIED census line). V1 declines whenever a
   leg would actually take a team.

Under that gate the rule fires on the bench spelling exactly as written in every serial cell
(both lanes) and in mt8 repro. It declines in mt8 licensed/fastmath for dot and fusedchain.

## 1. Hook verification

| Plan claim | Current location | Status | Evidence |
|---|---|---|---|
| Join encoding = `IRReduceCompute` with tuple kernel/seed | `src/IR.fs:215` (comment :197-214) | holds | "kernel and init are each an IRTuple of k entries in leaf order" |
| Typed join recognition `inferReductionJoin` | `src/TypeCheckInfer.fs:2982`; legs `joinLegSurface :2907` | holds | stored-array leg becomes an identity map; unseeded `(+)` gets `TExprLit (LitFloat 0.0)` (+0.0); non-`(+)/(*)` unseeded legs are refused ("a joined fold cannot seed from its first element") |
| Sharing-by-name logic `CodeGenBinding.fs:4521-4650` | `genReduceJoinCore` `:4547`; shared operands `:4602-4650` | holds (shifted ~+25) | one-level share only (`:4645`); refuses ragged/grouped/compound (`:4582`) and cuda/mpi legs |
| `CodeGenFusion.fs:239-257` licence remap not implemented | `genFusedLoopNestStreamedWith :187`; comment `:241-255` | holds | "Joining licences ACROSS leaves ... is not attempted here" |
| Join licence handling | `fusedFoldSimdPlan` `CodeGenFusion.fs:408` | holds: **intersection** | any unlicensed leg makes the whole C++ join serial; else ONE `omp simd reduction(op: accs...)`; requires one operator across legs |
| Join `where omp` leg | `CodeGenBinding.fs:4760-4767` | holds | emits census "a reduction join ... has no thread-level form yet"; opens no team (VERIFIED in `dot_joined` plan) |
| `IRStorage.fs:95 FoldChunkPlan` | `src/IRStorage.fs:95` | holds | chunks seed from their own first contributed value; caller's seed enters the combine first |
| `CodeGenLoopNest.fs ~:1517` Path B | `:1508-1580` (repro drop `:1517`, Path B `:1523`) | holds | dot kernel: `blade_fold::dot<double,8>` per chunk, fixed-order combine into seed 0.0 |
| `IRMono.fs:2537 fuseElementwiseChainsModule` / callable inlining facts | `:2537`; `freshen` helper `:2600` | **no callable inliner** | the pass splices map kernels only; named function calls stay `IRApp` (C++ `dot.lic.cpp` calls `kernel(__v37, __v38)`) |
| `Optimize.fs optimizerGates` | `:77` | holds | 6 gates; `CliSelfTests.fs:95` appends them to `suiteClearedKnobs` automatically |
| `optimizeModule` pipeline | `Optimize.fs:2225` | holds | advisory records, then const-match, fusion, slice-write, row-fold, hoist |
| `planHotRowFolds` / `fuseRowFoldsModule` (B) | `Optimize.fs:1547` / `:2004` | holds | hot-row is a side table planned at `Lowering.fs:2911`, after Optimize |
| Effects `isMovable` | `Effects.fs:85` | holds, **unusable as-is** | "No pass reads this yet"; `effectsOfBody` marks EVERY `TExprReduce` MayFail (`TypeCheckSupport.fs` effectsOfBody arm), so `kernel`'s summary is MayFail and an `isMovable` gate would decline the bench |
| `IR.irNodeMayAbort` | `IR.fs` (`irNodeMayAbort`, IRMono `:2435` uses it) | holds | conservative: every fold, `IRCompute` and `IRApplyCombinator` answer true. V1 needs a refined fold-total judge (§2.3) |
| Lowering pass order | `Lowering.fs:2851-2911` | holds | optimizeModule `:2870`, then lift, second S2/S4 force `:2893`, CSE `:2906`, pool reuse, hot-row |
| G7 "LLVM refuses the tuple-state fold" | `EmitLlvm.fs` fused fold `~:6100-6215` | **holds for tuple-state folds only** | the `<&!>` JOIN lowers in LLVM: one loop, k accumulators, per-leg `foldFmfDecorable` flags (VERIFIED `onepass.llvm.*.ll`, `dot_joined.llvm.*.ll`) |
| Input sums never thread | `CodeGenExpr.fs:1482/1528` | holds | "reduce in expression position ... opens no team" |
| Unseeded empty fold panics | `Interp/ArrayOps.fs:844-857` | holds | BL8003 "reduce: empty array"; the C++ uses `__r = a[0]` under a static extent |

New defect found (not C's to fix, but it constrains the emission shape). An explicit join in a
function body's RETURN position fails at codegen with BL7004 "every leg must resolve to an
unforced kernel application". The same join bound to a `let` and then projected works
(`n3probe.blade` vs `n3probe3.blade`; the planning fixture's own `joined()` hits this). **The
rewrite must always let-bind the join.**

## 2. The transformation, precisely

### 2.1 What runs today (VERIFIED from emitted code, n as in the bench)

**dot** (n = 3,000,017; a and b are 24.0 MB each):

| regime / lane | passes over a | passes over b | fold forms (`blade plan`) |
|---|---|---|---|
| serial licensed, C++ | 2 | 2 | kernel `simd K-lanes(8) dot`; input sums `K-lanes(8)` x2 (one record `_(checksum,input_checksum)`) |
| serial licensed, LLVM | 2 | 2 | kernel and both sums: "one accumulator chain with `reassoc nsz`"; kernel inlined into main by `emitTupleParts`; 3 loops (`loop.cond6/35/56`) |
| serial repro, both | 2 | 2 | 3 serial chains in 3 loops: `__v35 = wrap(__v35, a*b)` from 0.0; `__r = a[0]; for ri = 1..` (likewise b) |
| mt8 licensed, C++ | 2 | 2 | kernel `threaded(Path B chunks, simd K-lanes(8) dot per chunk)`; input sums serial `K-lanes(8)` |
| mt8 repro, C++ | 2 | 2 | all serial (`[omp] kernel declined -- BLADE_FP_REPRO=1`) |

The work to remove, verbatim from `dot.rep.cpp` (`main`):
```
double __r = __v37[0]; for (size_t __ri = 1; __ri < 3000017; __ri++) { __r = __wrap_47(__r, __v37[__ri]); }
... double __r = __v38[0]; for (size_t __ri = 1; __ri < 3000017; __ri++) { __r = __wrap_50(__r, __v38[__ri]); }
```
These run after `kernel()`'s loop over `a[__i0] * b[__i0]` has already streamed both arrays.

**onepass** (x = 5,000,011 doubles, 40.0 MB): 2 passes over x. Pass one is the kernel's shared
`<&!>` fold over 4 leaves: `simd-reduction` licensed, serial in repro; the LLVM lane emits one
loop with 4 `fadd reassoc nsz`. Pass two is the input sum: `K-lanes(8)` / serial / its own LLVM
loop `loop.cond83`.

**fusedchain** (a = 2,000,003 doubles, 16.0 MB): 2 passes over a. Pass one is the kernel's
composed chain fold: `simd-reduction` serial-licensed, Path B + simd under mt8, serial in repro.
Pass two is the input sum: `K-lanes(8)` / serial.

**The hand-joined targets** (`*_joined.blade`) emit one loop in both lanes. From
`dot_joined.rep.cpp`:
```
double __v53_0 = 0.0;  double __v53_1 = -(0.0);  double __v53_2 = -(0.0);
for (size_t __i0 = 0; __i0 < 3000017; __i0++) {
    double __v37____i0 = __v37[__i0];  double __v38____i0 = __v38[__i0];
    __v53_0 = __wrap_66___v53_j0(__v53_0, (__v37____i0 * __v38____i0));
    __v53_1 = __wrap_69___v53_j1(__v53_1, __v37____i0);
    __v53_2 = __wrap_72___v53_j2(__v53_2, __v38____i0);
}
```
- Licensed C++ emits the same loop under `BLADE_OMP_SIMD_REDUCTION(+:__v53_0, __v53_1, __v53_2)`.
- LLVM emits one `loop.cond10` with three `fadd [reassoc nsz]`, per leg.
- Printed checksums are identical to the current programs in repro/contract-off for all three
  kernels (C++), and for dot in LLVM.

### 2.2 Recognized input shape

A **region** is one straight-line let spine: a function body, a lambda body, or one module
binding's value (the bench's `{ let a = ..; let b = ..; (..) }` block).

**Candidates** are scalar fold nodes in *unconditional* positions of the spine: let RHS or the
final expression, never under `IRIf`/`IRMatch`/a lambda/`let rec`/`IRForRange`. Admitted forms:
- `IRReduce (arr, k, init?)` over a rank-1 array, which gives a scalar;
- `IRReduceCompute (leaf, k, seed)` with a single leaf, or an existing join or shared-fold tree;
- `IRProdSum args`;
- `IRApp (f, atoms)` where f qualifies for one-level inlining (§2.4) and its body holds at least
  one candidate.

**Groups** are formed by traversal domain:
- every leg's index space is ONE plain dense axis (`IxKPlain`, `SymNone`, rank 1, non-virtual
  source) with a literal extent, and all literals are equal;
- index-type provenance need not match. Position i of each array is cell i, which is the rule
  `inferReductionJoin` already applies.

**Output**: for each group of 2 or more legs, one let-bound join is placed before the earliest
statement that contains a group member. Each member is replaced by `IRTupleProj(j, i)`.

| Original leg | Becomes |
|---|---|
| a single-leaf `IRReduceCompute` | its leaf, kernel and seed |
| `IRReduce(arr, k, Some s)` | identity-map leaf over arr (lift IRMono's local `mapOneArray`, `IRMono.fs:1999`, minus its `IRCompute` wrapper), kernel k, seed s |
| `IRReduce(arr, k, None)` | identity leaf; seed per §2.3 |
| `IRProdSum [x1..xk]` | `zip` leaf with a left-nested product kernel, `(+)`, seed +0.0. Matches `Interp/ArrayOps.prodSum`: seed 0.0, `prod` folded left over args, ascending t |
| an existing shared-fold tree (onepass) | the per-leg encoding: replicate its kernel/seed per leaf, exactly as `EmitLlvm` already reads it, then append legs |

### 2.3 Legality

**Strict (repro, `where repro`, `-ffp-contract=off`).** Each leg keeps its own accumulator,
its own seed and its own operand sequence (ascending cell order). The join emitter writes
`acc_i = wrap_i(acc_i, cell_i)` per leg, per cell (verified above), so sharing the traversal
changes only the interleaving of independent chains. The final combination (`sum_a + sum_b`,
`s1 + 2 s2 + ...`) is untouched.

**Unseeded legs.** For builtin `(+)` over Float64/Float32, seed `-0.0`:
- In round-to-nearest, `-0.0 + x = x` bit-exactly for every x, including +0, -0, inf and qNaN.
- So the chain `(-0 + a0) + a1 ...` equals `a0 + a1 ...` for n >= 1.
- `+0.0` (what surface inference uses) is NOT exact. VERIFIED with `negzero.blade`: unseeded
  `-0.0`, surface join `0.0`, `-0.0`-seeded join `-0.0`, identical in C++, LLVM and the
  interpreter.
- Emit the seed as `IRUnaryOp(Neg, IRLit 0.0)`, the node the surface `-0.0` produced, which
  renders `-(0.0)` / `fneg`. Do not rely on a float-literal printer for negative zero.

Other element types:

| kernel | element | seed |
|---|---|---|
| `(*)` | float | 1.0 (`1*x = x` exactly) |
| `(+)` | Int64/Int32 | 0 (wrapping irrelevant) |
| `(*)` | Int64/Int32 | 1 |
| anything else unseeded | any | DECLINE |
| any | complex or unit-carrying | DECLINE in V1 (the unit seed type is a V1.1 item) |

The only residue is n = 1 with a signalling-NaN input (quieted rather than passed through). It
is unobservable in printing; document it.

**Licensed.** Each leg's reorder licence is its own: `foldReorderLicensed` per callable.
- The joined C++ form is one `omp simd reduction` when every leg is licensed with one builtin
  operator, else fully serial (intersection, never union).
- The LLVM lane decorates per leg.
- Neither lane can grant a licence; the C++ lane can only *lose* one. To keep C++ forms
  monotone, V1 DECLINES in a reordering build (not repro) when legs disagree on licence status.
  Under repro everything is serial, so licence is moot and the rule fires.

**Threads.** Decline iff the build would thread some leg: `ompThreadEmissionEnabled()` and not
`fpReproEnabled()`, and some leg's fold kernel has `IsOmpParallel` with a licence (or a leaf map
kernel requests omp).
- Today Path B gives that leg a team; the join would print the G7 census line and run it serial.
- Joining a threaded leg with unrequested legs inside one team would be union of omp flags,
  which is forbidden.
- Optimize.fs compiles before CodeGenState (`Blade.fsproj:141` vs `:396`), so it re-reads the
  env through `Blade.Toolchain` (same switch semantics). Better: move the two predicates down to
  Toolchain.fs.

**Effects and totality.** Folds are hoisted (pure, so this is unobservable) and interleaved (so
the first failing coordinate could change). V1 therefore requires every leg to be pure-total:
- **Pure:** no writes, output, external reads or unknown callees. Use `cseFactsOf`
  (`Optimize.fs:670`): `Repeatable && not MayWrite` on the leaf kernel, the fold kernel and the
  inlined body.
- **Total** (a new judge, `foldLegTotal`): walk the leaf kernel body and fold kernel body with
  `irNodeMayAbort`. The leg node itself counts as non-aborting only when
  (a) every array extent is a literal and they are equal (no runtime zip agreement check),
  (b) it is seeded, or unseeded with literal n >= 1 (no BL8003),
  (c) it has no `IRIndex`/guard/constraint.
- Do NOT use `Effects.isMovable` on the callee summary. Every reduce is MayFail there, so it
  would refuse all three bench kernels.

**Aliasing and mutation.** Every leg operand is an `IRVar` bound to a non-mutable let or a
parameter. Decline when:
- the region's callable has any `mut` parameter;
- the spine contains any `IRAssign` or `IRForRange`;
- an operand let is a view (`IRCurry`/`IRSlice`) of a mutable binding.

Hoisting needs every free variable of a moved leg (arrays, captured scalars, seed expressions)
to be bound before the join point.

### 2.4 One-level inlining (makes the bench spelling fire)

Inline `IRApp(IRVar f, args)` at its unconditional statement position iff all of these hold:
- f is in `modul.Functions` with `Captures = []`;
- not `IsArityPoly` / `IsStatic` / cuda / mpi;
- **not `IsRepro`**: inlining would drop the per-function veto and the `BLADE_REPRO_FN`
  attribute;
- non-recursive, scalar `RetType`, no mut params;
- every arg is an `IRVar`/`IRLit` atom;
- the body is a pure-total let spine;
- every lambda it references has `Captures = []`;
- **and** at least one of its folds lands in a group of 2 or more. Otherwise leave the call
  alone, so there is no gratuitous inlining.

Mechanics:
- substitute params with args;
- freshen let binders with `builder.FreshId()`, the same as IRMono's `freshen` (`:2603`; lift it
  to a shared helper);
- splice the spine before the statement and replace the call with the body's result.

An expression-position call (`(kernel(a, b), ...)`) is lifted the same way. The C++ lane loses
`BLADE_FRAME("kernel")` there, which is unobservable because the body is total. The dead
`kernel` definition stays emitted in C++ and the LLVM lane never emits it.

**If inlining is refused**, the bench needs a semantics-preserving source fallback that fires
under a function-local V1: move the input sum into `kernel`. For dot:
`function kernel(a, b) -> (Float64, Float64) = { let s = reduce(..); (s, reduce(a,(+)) + reduce(b,(+))) }`
with `let (checksum, input_checksum) = { ..; kernel(a, b) }`. The order and the seeds are
unchanged. The goal remains the rule firing on the spelling as written, which §2.4 achieves.

### 2.5 Refusals (each a `declined -- <reason>` record)

- **Threads:** a leg would take a thread team in this build (where omp + threads on + not
  repro). Reason: reduction joins have no thread-level form (G7).
- **Licence:** legs disagree on reorder licence in a reordering build.
- **Unseeded kernel:** not builtin `(+)`/`(*)`, or an unseeded leg's extent is not a literal
  >= 1.
- **Domain:** legs over different extents, ranks, or non-plain index kinds (ragged, compound,
  grouped, sym/antisym compact, sparse, halo window, closed-form domain), virtual or streamed
  sources.
- **Totality:** a summand or fold kernel may fail (integer `/` `%`, non-literal `^`,
  float-to-int cast, subscript, guard, `lgamma`) or has effects (output, external read, unknown
  callee, assignment).
- **Mutation:** an operand is mutable or a view of a mutable binding, or the region assigns.
- **Inlining:** the callee refuses (repro, captures, recursion, non-atomic args, tuple return).
- **Device:** a leg on a cuda/mpi strategy.
- **Sharing depth:** a leaf whose operand is a deferred map over another deferred map (the
  emitter's one-level share limit, `CodeGenBinding.fs:4645`; relational fixture 03).

## 3. Target cells and the work that disappears

Byte counts are reads of stored inputs; chain counts are dependent-add critical-path length.
RESULTS §14.8 ratios are expert/Blade (gf / ifx).

| kernel | regime (ref lane) | current gf / ifx | fires? | what disappears |
|---|---|---|---|---|
| dot | serial licensed (llvm) | 1.416 * / 0.967 * | yes | 96.0 → 48.0 MB of stream reads (a and b re-read once each); the three vectorized reductions share one loop |
| dot | serial repro (llvm) | 1.219 / 0.755 | yes | critical path 3 x 3,000,017 → 1 x 3,000,017 dependent adds (three independent chains in one loop body), plus 48 MB of reads |
| dot | serial fastmath (llvm) | 1.369 / 0.712 | yes | as licensed (Blade fastmath = licensed knobs) |
| dot | mt8 licensed (cpp) | 0.962 * / 0.962 * | **no** (thread gate) | none in V1; the kernel's Path B team is kept, input sums stay serial K-lanes |
| dot | mt8 repro (cpp) | 0.832 * / 0.842 * | yes | as serial repro: no team exists under repro, so none is lost |
| onepass | serial licensed | 1.525 / 1.031 | yes | 80.0 → 40.0 MB; 4-leg join + input sum → 5-leg join |
| onepass | serial repro | 1.354 / 0.820 | yes | 5,000,010 dependent adds (the separate input-sum loop) leave the critical path; 40 MB |
| onepass | serial fastmath | 1.502 * / 0.723 * | yes | as licensed |
| fusedchain | serial licensed | 1.567 * / 1.023 * | yes | 32.0 → 16.0 MB (may be L3-resident at 16 MB) |
| fusedchain | serial repro | 1.381 * / 0.839 * | yes | the 2,000,002-add input chain overlaps the kernel chain (which is fdiv-throughput-bound) |
| fusedchain | serial fastmath | 1.496 * / 0.788 * | yes | as licensed |
| fusedchain | mt8 licensed (cpp) | 1.021 * / 1.104 * | **no** (thread gate) | none |
| fusedchain | mt8 repro (cpp) | 0.753 * / 0.825 * | yes | as serial repro |
| saxpy | all serial, mt8 repro | e.g. repro 1.082 * / 0.686 * | partly | the input sums over x and y join (2 legs): repro critical path 2 x 2,000,002 → 1 x; **no byte saving** (each array was read once by its own sum). The kernel's `s` over y2 is left for B (§6) |

**G36 incremental (ifx-expert repro dot 0.59 ms vs blade-llvm 2.82 ms).** 2.82 ms is about one
latency-bound chain of 3,000,017 fadds (4-cycle latency). After sharing, the dot chain is a
third independent chain in the loop that already walks a and b. Its marginal cost becomes the
`fmul` and one more add per element issued in parallel, so essentially all of the chain-latency
part of the 2.82 ms can disappear. That is the same mechanism ifx uses.

**Caveat for the harness: the rule ALSO fires on `inputonly.blade`.** That file is
`(0.0, reduce(a,(+)) + reduce(b,(+)))`: two unseeded builtin legs and no omp, so it fires in
every regime, including mt8. The twin's own repro time drops from 2 chain-latencies to 1.
"Incremental" is therefore not comparable across OLD/NEW binaries without noting this; report
totals and twins separately. In licensed mode the saving is bandwidth, so no forecast is made
beyond the byte counts above.

## 4. Implementation design

**New pass** `shareSiblingFoldsModule (builder) (programFuncs) (modul)` in `src/Optimize.fs`:
- Insert after `hoistKernelInvariantsModule` in `optimizeModule` (`:2225`), so hoisted
  `mean(a)`-style folds become candidates.
- It runs before CSE (`Lowering.fs:2906`), so it must dedupe structurally identical legs itself.
  Same operand ids, kernel, and seed bits (use `floatBitsLit`, `:731`) map to one leg with two
  projections. That avoids joining duplicates CSE would have merged.

| Part | Location |
|---|---|
| Region walker over `modul.Functions` bodies (lambdas included) and `modul.Bindings` values; statement spine `IRLet`/`IRSequence` only | new, Optimize.fs |
| `foldLegTotal`, `legOf`, domain key, seed synthesis (§2.3) | new, Optimize.fs |
| One-level inliner (§2.4) | new, Optimize.fs; `freshen` lifted out of `IRMono.fuseElementwiseChainsModule` (`IRMono.fs:2603`) into a shared IRMono helper |
| Identity leaf | lift `mapOneArray` (`IRMono.fs:1999`, local to `lowerArrayBinOpsModule`) to module level, return the bare `IRApplyCombinator`; identity kernel via `IR.mkLambdaCallable` (`IR.fs:1965`), appended to `Functions` as fusion does (`newLambdas`) |
| Join node | `IRLet(j, IRReduceCompute(fusionTree, IRTuple ks, IRTuple ss), body)`; projections with the flat-tuple convention the surface destructure uses (copy what lowering emits for `let (s, sa, sb) = join`; check the `isFlat` bit against `dot_joined`'s IR) |
| Gate | `siblingFoldsEnabled () = gateOn "BLADE_SIBLING_FOLDS"` (default on), added to `optimizerGates` (`:77`). It reaches `suiteClearedKnobs` automatically via `CliSelfTests.fs:95` and is visible to opt-diff. Gate off: analysis still runs and records `declined -- disabled by BLADE_SIBLING_FOLDS` |
| Thread/repro predicates | `Blade.Toolchain`-level twins of `ompThreadEmissionEnabled` / `fpReproEnabled` (move them down from `CodeGenState.fs:633/:898`; CodeGenState re-exports) |

**Plan records** (rule `sibling-folds`, v1, subject = binding or function name):
- `applied [3 folds over {a, b} share one traversal (Ix: 3000017 cells); kernel(a, b) inlined (pure-total, scalar return); unseeded (+) legs seeded -0.0 (exact, n >= 1); per-leg licences kept: licensed, licensed, licensed]`
- `declined -- <reason>` for each §2.5 refusal, including:
  `a leg requests threads (where omp in kernel) and this build threads it; a reduction join has no thread-level form (G7)`

**Lanes.** IR only: no emitter change in C++, LLVM or the interpreter. All three consume the
rewritten tree through existing, already-differential join support (`genReduceJoinCore`,
`EmitLlvm` fused fold, the interpreter's `IRReduceCompute` tuple fold).
- **Twin identity:** under `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` both lanes and the
  interpreter evaluate the same per-leg ascending chains (VERIFIED on the hand-joined forms).
- **Rewrite correctness** is checked by `blade test opt-diff` (gates OFF vs ON, compile + run
  where the C++ differs).
- **LLVM consumption** is checked by `blade test llvm` on the new corpus directory.
- `test interp` cannot see a rewrite bug (both lanes print the same IR); that is stated, not
  hidden.

**Corpus** (new single-file directory `tests/corpus/sibling-folds/`, auto-discovered: default
suite, interp walk, opt-diff). Integer-valued float data keeps pins association-invariant.

| file | shape | pins |
|---|---|---|
| `001_two_folds_one_array_seeds` | in a function: `reduce(x,(+))` unseeded + `reduce(x, lambda(p,q) -> p+q, 10.0)` + `prodsum(x,x)` | `// EXPECT:` three values |
| `002_dot_shape_two_zipped` | the bench block shape at `Idx<1009>`: `kernel(a,b)` with a `where omp` fold, plus `reduce(a,(+)) + reduce(b,(+))` | EXPECT both (fires only under OMP_THREADS=1/repro; values identical either way) |
| `003_negative_zero_unseeded` | all `-0.0` array: unseeded sum sibling of `prodsum(z,z)` | `// EXPECT: s = -0.0` (the +0.0-seed bug would print `0.0`) |
| `004_unseeded_int_and_product` | Int64 unseeded `(+)` and Float64 unseeded `(*)` siblings | EXPECT (int spelled `2`, float `2.0`) |
| `005_join_extension` | onepass shape: explicit 4-leaf `<&!>` fold + sibling `reduce(x,(+))` | EXPECT |
| `006_decline_mutation_between` | `let mut v` array, fold, element write, fold | EXPECT both values (proves no hoist across the write) |
| `007_decline_failure_order` | fold A fails at cell 5 (`100 / v`, Int64, BL8013), later fold B fails at cell 2 (float→int cast of NaN, BL8014) | `// ABORT: BL8013` (a wrong join would raise BL8014 first) |
| `008_decline_domains` | sums over `Idx<7>` and `Idx<11>` arrays in one function | EXPECT |
| `009_empty_unseeded` (if `Idx<0>` is admissible) | unseeded sum over an empty axis beside a seeded sibling | `// ABORT: BL8003` |

**`blade test optimize` needles** (`tests/OptimizeTests.fs`; env pinned per compile):
- With `BLADE_OMP_THREADS=1 BLADE_FP_REPRO=1`, the dot shape emits exactly one
  `for (size_t __i0 = 0; __i0 < 1009;` loop in `main` over a/b, accumulators seeded `0.0`,
  `-(0.0)`, `-(0.0)`, and the plan says `[sibling-folds v1] ... applied`.
- With threads on (licensed): `declined -- a leg requests threads` and the Path B record is
  still present.
- `BLADE_SIBLING_FOLDS=0`: `declined -- disabled by BLADE_SIBLING_FOLDS`.
- **Convergence needle:** the natural spelling and the hand-written explicit join emit the same
  loop structure (counts of loops, accumulators, `BLADE_OMP_SIMD_REDUCTION` clause arity).
- Decline-reason needles for licence mismatch, unseeded `max`, and a repro callee.

**Docs.**
- CLAUDE.md env-table row: append `BLADE_SIBLING_FOLDS` to the optimizer-gates row (one clause:
  sibling scalar folds over one dense axis share a traversal; per-leg seeds and licences kept;
  declines when a leg would thread).
- `docs/features.md` row.
- The style-guide row "several statistics in ONE pass" gains a note that the natural spelling
  converges (only once landed).

## 5. Effort, risks, drop experiment

**Effort: M.** About 500-700 F# lines (walker, judge, inliner, seed and leaf synthesis,
records), 9 corpus files, about 8 needles. No emitter change.

**Risks.**
1. **C++ licensed form regresses.** Today's `K-lanes(8)` / `blade_fold::dot` becomes one
   `omp simd reduction(+: k accs)` (`fusedFoldSimdPlan` has no K-lane join form). At these
   sizes the loops are bandwidth-bound and k chains supply ILP, but blade-cpp licensed must be
   measured. A K-lane join form would be an emitter change touching E's files; keep it out of
   V1.
2. **Inlining collisions.** Freshen discipline and lambda captures are the classic id bugs
   (memory: capture-forwarding seams).
3. **The twin moves too** (§3). Harness interpretation, not a correctness risk.
4. **Seed rendering.** `-0.0` must not be constant-folded or printed as `0.0` by any IR literal
   path. Use the `Neg(0.0)` node (VERIFIED renders `-(0.0)` / `fneg`).
5. **Join position.** Return-position joins hit the BL7004 defect; always let-bind.
6. **Unit-typed sums** decline in V1, so some user programs see no change.

**The one experiment that should make us DROP it (or descope to licensed-only/repro-only).**
Have the runner time the existing hand-joined sources `dot_joined.blade`,
`onepass_joined.blade` and `fusedchain_joined.blade`:
- they are drop-in replacements of the bench `.blade` (same kernel/twin outputs, same seeds,
  -0.0 seeded input legs);
- compare against the current sources on the OLD binary;
- arms: serial licensed and serial repro, blade-llvm and blade-cpp.

This needs zero compiler change. If the joined spellings do not beat the current ones outside
the 95% CI on blade-llvm serial repro dot, or if blade-cpp serial licensed regresses beyond its
CI (risk 1), do not build the discovery rule as specified. The mechanism predicts a large repro
win (chain overlap) and a bandwidth win in licensed.

## 6. File-overlap list

| File / function | C touches | Also touched by | Note |
|---|---|---|---|
| `src/Optimize.fs` `optimizerGates` (`:77`), `optimizeModule` (`:2225`) | adds gate and pipeline step | **B** (epilogue gates/passes) | one-line merges; sequence the PRs, or agree that C's step goes last in `optimizeModule` |
| `src/Optimize.fs` `planHotRowFolds` `:1547`, `fuseRowFoldsModule` `:2004`, `fuseSliceWritesModule` `:1731` | **not touched** | B | — |
| `src/Optimize.fs` `cseFactsOf` `:670`, `floatBitsLit` `:731` | read only (maybe made non-private) | B may read them | trivial |
| `src/IRMono.fs` `freshen` (local in `fuseElementwiseChainsModule`, `:2603`), `mapOneArray` (local in `lowerArrayBinOpsModule`, `:1999`) | lifted to module level, behavior unchanged | **B** (rank-2 let-spanning map DAG extends `fuseElementwiseChainsModule`) | textual conflict risk in the same function; do the lift first as a tiny separate commit |
| `src/CodeGenState.fs` `fpReproEnabled :633`, `ompThreadEmissionEnabled :898` → `Toolchain.fs` | moved down, re-exported | none known | — |
| `CodeGenLoopNest.genLoopNestStreamed` `:1463`, Path B `:1508-1580` | **not touched** | B, D, E | — |
| `CodeGenBinding.genReduceJoinCore` `:4547`, `CodeGenFusion.fusedFoldSimdPlan` `:408` / `genFusedLoopNestStreamedWith :187`, `EmitLlvm` fused fold `~:6100-6215`, `Interp` join fold | **not touched** (consumed) | **E** (thread-level `<&!>` form); B for maskagg payload | C produces more joins that E's work then threads. E landing lets C relax its thread gate, but only for joins whose legs ALL request threads, never by union |
| `tests/OptimizeTests.fs` | adds needles | B (likely) | append-only |
| CLAUDE.md env table | optimizer-gates row | B (its gate), E (its knob) | append-only |
| A (orbit scatter), D (tile runtime) | — | — | no overlap |

**Semantic overlap with B on saxpy.** C runs inside `optimizeModule`, before B's side-table
planner (`Lowering.fs:2911`). If C joins `s` (over the freshly stored y2) with the input sums,
B's rank-1 producer-epilogue planner would find a join instead of a single fold consumer.

Rule for V1: C does not take a leg whose operand is a `|> compute`d rank-1 dense map produced
in the same region with no other non-fold use, while B's gate is on. Reason recorded:
`left for the producer epilogue (B)`. Only the input-sum pair joins. When B lands, B's planner
may absorb those sums as extra epilogue states.

## 7. Recommended wave

**Wave 1** (parallel-safe; no emitter overlap):
- first the zero-cost drop experiment (§5);
- then the rule.
- Land the IRMono helper lifts as a separate first commit, so B's rank-2 work rebases trivially.

**A/B protocol.**
- **OLD** = 4136f0b5 binary, **NEW** = the branch.
- **Kernels:** dot, onepass, fusedchain, saxpy, their `inputonly` twins, plus one non-target
  control (stencil).
- **Regimes:** serial licensed, serial repro, serial fastmath (blade-llvm ref plus blade-cpp),
  and mt8 licensed / mt8 repro (blade-cpp).
- Interleaved, n = 21, medians with 95% CI, bench extents (non-power-of-two).
- **Expectations:**
  - mt8 licensed/fastmath dot and fusedchain must be **unchanged** (the gate declines there,
    and the plan must show the decline record);
  - the twins move;
  - report totals AND twins, and say incremental is not comparable OLD→NEW.

**Gates before measuring:**
- `blade test optimize`, `blade test opt-diff` (whole corpus);
- `blade test sibling-folds`, `blade test llvm sibling-folds`, `blade test interp sibling-folds`;
- the full default suite. Gate on totals: the harness exits 0 with failures.
