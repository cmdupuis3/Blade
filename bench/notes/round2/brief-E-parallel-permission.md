# E — G5 / G7 parallel-permission design

Research only; checkout inspected: master `4136f0b5a42f532b5d43c6e158f0961635d62b13`.
Recommendation: **G7 first, wave 2 (M); G5 persistent scheduling, wave 3 (L).**
G5's permission-preserving per-step form is an M-sized prerequisite, not the final performance claim.
No repository files were changed. No test suites, builds, or timings were run.
The requested preamble and CLAUDE.md were read before tracing implementation.
All implementation proposals below are designs, not claims of implemented or measured behavior.

## 1. Hook verification

The gap register is useful history, but several assertions have already changed.
Line references below were read in this session; ranges identify the relevant part of the named function.
Graft MCP queries were rejected by the session approval policy; the local graft index contained only orientation text.
Direct source inspection supplied the evidence. No agent delegation was used.

| Plan / task claim | Current file:function:line | Verdict and evidence |
|---|---|---|
| Recursive arrays desugar to allocation, seed writes, and counted loops | `src/TypeCheckInfer.fs:inferRecArray:13955,14116,14333` | Holds. `sliceCopyStmts` binds a row, then builds trailing-axis copy loops; the time loop contains lag bindings and the slice copy. |
| All recursive extents are static | `src/TypeCheckInfer.fs:inferRecArray:14000-14035` | Holds for both leading and trailing axes. |
| Round-1 slice-write fusion already inlines RK4 stages | `src/Optimize.fs:fuseSliceWritesModule:1731,1826-1856,1923-1928` | Holds. Pointwise callees become scalar lets stored directly by an `IRForRange`. |
| An omp map's licence is silently lost by that fusion | `src/IRMono.fs:tilePlainKernel:2523-2527`; `src/Optimize.fs:fuseSliceWritesModule:1828,1843,1848` | **Refuted for this path.** The eligibility predicate rejects `Parallelism` and `IsOmpParallel`; parallel named callees also decline. It does not currently splice their bodies and erase the request. |
| The fused row loop has a pragma site | `src/CodeGenBinding.fs:genForRangeBinding:5255,5303-5308`; `src/CodeGenExpr.fs:renderUnitStmts:1675-1686` | No permission-aware site. Both render ordinary counted loops; the optimizer emits an ordinary `IRForRange`. |
| S13 folds rows immediately after writing them | `src/Optimize.fs:fuseRowFoldsModule:2004,2073-2095,2128-2156` | Holds. The update is `F(acc,K(BUF(row)))`; eligibility checks row stores and repeatable calls. |
| Recur reads spatial neighbors | `bench/kernels/recur/recur.blade:rhs:28;step:31-36;kernel:40-45` | **Refuted.** Each stage is elementwise; the only trajectory read is `prefix(n - 1)`, consumed at the same x. |
| Expert uses a persistent independent-x team | `bench/kernels/recur/expert.f90:recur_expert:52-92` | Holds. One region, contiguous x ranges, local time loops, no per-time barrier. The spec's per-step parallel-do description at lines 54-57 is stale. |
| Joins have one SIMD clause over all accumulators | `src/CodeGenFusion.fs:fusedFoldSimdPlan:408-434`; `src/CodeGenBinding.fs:genReduceComputeBindingCore:4493-4510` | Holds. The emitted onepass kernel has one four-accumulator `BLADE_OMP_SIMD_REDUCTION`. |
| Joins have no thread form | `src/CodeGenFusion.fs:genFusedLoopNestStreamedWith:187-191`; `src/CodeGenBinding.fs:genReduceJoinCore:4786` | Holds. Folds suppress the normal map pragma; join callers pass `hostParallel=false`. |
| Omp on a producer leg gets a census note | `src/CodeGenFusion.fs:genFusedLoopNestStreamedWith:265-275` | Holds. It checks every leaf's `OmpRequested`; the fold reason is shared-scalar races. |
| Omp on the common fold kernel is silently dropped | `src/CodeGenBinding.fs:genReduceComputeBindingCore:4500-4508` | **Already fixed.** Explicit block comment says the fused fold has no thread-level form; `ompSuppressedPhrase` also records the plan decline. |
| Separate per-leg fold requests are silently dropped | `src/CodeGenBinding.fs:genReduceJoinCore:4776-4784` | **Already fixed.** Any requested fold gets the analogous reduction-join census note. |
| Builtin + supplies a reorder licence | `src/CodeGenExprSupport.fs:foldKernelBuiltinOp:1600-1616;foldReorderLicensed:1644-1648` | Holds. The narrow two-parameter builtin body is recognized independently for each fold. |
| A builtin reorder licence also sets IsOmpParallel | `src/CodeGenExprSupport.fs:foldReorderLicensed:1634-1648`; `src/Lowering.fs:extractParallelism:279-294` | **No.** Request and mathematical permission are separate. Extraction sets IsOmpParallel from an actual Omp strategy, not the builtin body. |
| Existing Path B is a suitable threading model | `src/IRStorage.fs:FoldChunkPlan:79-116`; `src/CodeGenLoopNest.fs:genLoopNestStreamed:1520-1637,2187-2200` | Holds. Private chunk state, actual team-size partition, skipped empty chunks, fixed-order combine. |
| LLVM has no thread runtime | `src/EmitLlvm.fs:ompCensusReason:7082-7086` | Holds as an explicit backend limitation; both proposed mt8 compute forms are C++ only. |
| LLVM's missing-thread census is absent | `src/EmitLlvm.fs:ompCensusPhrase:7090;ompRequestedKernels:7095;tryEmitProgramNamed:7164-7165` | **Already fixed.** Requested callables get serial-emission comments. G11's missing-notice wording is stale. |
| LLVM refuses the proposed tuple-state workaround | `src/EmitLlvm.fs:emitReduce:5738-5752;emitReduceCompute:6142-6147` | Scalar accumulator/seed paths support the concern, but the exact historical workaround was **not reproduced**. Its current precise refusal is unverified; do not base this design on it. |
| Change no_parallel_form directly from true to false | `bench/harness/run.py:load_kernel:553-562` | **Needs companion fix.** True becomes ["all"]; false currently reaches `list(False)`. Accept false as [] first, or use [] under the existing schema. |

The invariant itself is explicit at `src/CodeGenLoopNest.fs:ompSuppressedMarker:810-825`.
Its shared phrase records a declined `omp` decision at `src/CodeGenState.fs:ompSuppressedPhrase:936-940`.
Applied constructs use `src/CodeGenState.fs:recordOmpConstruct:944-946`.
Do not reimplement the already-landed G7/G11 census fixes; add regressions and correct the gap notes.

## 2. Target cells and emitted evidence

These are published historical cells, not a fresh measurement of 4136f0b5.
RESULTS 14.8 identifies its tree as d3d5722b (`bench/RESULTS.md:2369`).
The later input-generator correction makes same-session OLD/NEW measurements necessary.

| Kernel / regime | Blade LLVM / C++ ms | gfortran / ifx expert ms | Published expert/Blade ratios | Source |
|---|---:|---:|---|---|
| recur, serial licensed | 9.159 / 11.27 | 10.2 / 10.3 | LLVM 1.114* / 1.125* | RESULTS:2480 |
| recur, mt8 licensed | no Blade arm | 7.077 / 7.951 | unavailable | RESULTS:2497 |
| recur, serial repro | 13.83 / 15.74 | 13.63 / 14.07 | LLVM 0.985* / 1.017* | RESULTS:2513 |
| recur, mt8 repro | no Blade arm | 8.005 / 8.117 | unavailable | RESULTS:2530 |
| onepass, serial licensed | 10.94 / 21.03 | 16.69 / 11.29 | LLVM 1.525 / 1.031 | RESULTS:2484 |
| onepass, mt8 licensed | no Blade arm | 9.014 / 9.522 | unavailable | RESULTS:2500 |
| onepass, serial repro | 16.36 / 26.31 | 22.16 / 13.42 | LLVM 1.354 / 0.820 | RESULTS:2517 |
| onepass, mt8 repro | no Blade arm | 11.89 / 11.91 | unavailable | RESULTS:2533 |
| onepass, mt8 fastmath | no Blade arm | 9.43 / 9.602 | unavailable | RESULTS:2559 |

The asterisks and unavailable cells remain as published; no serial-vs-mt8 ratio is invented.
The experts' mt8 repro reductions are not Blade's exact ascending chains: RESULTS:2403-2415 explicitly distinguishes these contracts.

Emission was run from `scratchpad/round2/research/E-private-cwd`, using the existing Release executable.
PATH began with `C:\msys64\ucrt64\bin`; BLADE_LLVM=0, BLADE_BLAS=off, BLADE_EXE_CACHE=off.
Recur used BLADE_FP_REPRO=1, BLADE_FP_CONTRACT=off, BLADE_OMP_THREADS unset.
Onepass used the licensed default (BLADE_FP_REPRO=0; BLADE_FP_REASSOC unset), contraction off.
The command was `Blade.exe emit --verbose <absolute kernel path>`; stdout was inspected in memory.
No emitted files or scratch builds were written. Line numbers below refer to that stdout.

Recur's actual time loop, emitted lines 463-480 (only the long checksum IIFE is abbreviated):

```cpp
for (int64_t __k366 = 1L; __k366 < 301L; __k366++) {
    Array<double, 1> __v367 = { __v363.data[(__k366 - 1L)], __v363.extents + 1 };
    for (int64_t __k369 = 0L; __k369 < 20011L; __k369++) {
        double __v602 = __v367[__k369];
        double __v603 = (__v602 - ((__v602 * __v602) * __v602));
        double __v604 = (__v602 + (0.005 * __v603));
        double __v605 = (__v604 - ((__v604 * __v604) * __v604));
        double __v606 = (__v602 + (0.005 * __v605));
        double __v607 = (__v606 - ((__v606 * __v606) * __v606));
        double __v608 = (__v602 + (0.01 * __v607));
        double __v609 = (__v608 - ((__v608 * __v608) * __v608));
        double __v610 = (__v602 + ((0.01 / 6.0) * (((__v603 + (2.0 * __v605)) + (2.0 * __v607)) + __v609)));
        __v363[__k366][__k369] = __v610;
    }
    Array<double, 1> __v612 = { __v363.data[__k366], __v363.extents + 1 };
    // __v613 = one ascending prodsum(__v612, __v612), starting from 0
    __v611 = (__v611 + __v613);
}
```

Emission lines 453-458 allocate the complete 301 x 20011 trajectory and store row zero.
There is no zero-fill or stage allocation in the time loop; those optimizations already landed.
The plan reports both `slice-write-fusion v1` and `row-fold-fusion v1` applied.
G5 must preserve these gains: 6,003,300 cell-steps and 6,023,311 stored trajectory cells remain.
The mandatory trajectory payload is 48,186,488 bytes, calculated from the source extents.
Threading removes no RK4 arithmetic or mandatory stores; it distributes cell work.
Persistence removes roughly 300 repeated team entries versus a per-step team, not 300 time dependencies.
The current checksum still reads each hot row: folding at the cell store is item B, not a new claim here.

Onepass's emitted lines 219-225:

```cpp
BLADE_OMP_SIMD_REDUCTION(+:__v39_0, __v39_1, __v39_2, __v39_3)
for (size_t __i0 = 0; __i0 < 5000011; __i0++) {
    double x____i0 = x[__i0];
    __v39_0 = __v39_0 + (x____i0);
    __v39_1 = __v39_1 + ((x____i0 * x____i0));
    __v39_2 = __v39_2 + (((x____i0 * x____i0) * x____i0));
    __v39_3 = __v39_3 + ((((x____i0 * x____i0) * x____i0) * x____i0));
}
```

There is already one load stream and one traversal, not four traversals to remove.
G7 divides that 40,000,088-byte sweep among threads; four final scalar outputs remain.
It adds O(4P) partial state and combines, not O(N) temporaries.
Sharing the separate input checksum is item C; this proposal does not credit its saved pass.

## 3. G5 — precise transformation and source permission

Choose **candidate (a), existing map syntax**, with the licence on the final spatial map.
Keep the four named RK4 stage lets in `step`; replace only its final lifted expression:

```blade sketch
method_for(zip(u, k1, k2, k3, k4))
    <@> lambda(v, a1, a2, a3, a4) where omp(v: 1) ->
        v + (0.01 / 6.0) * (a1 + 2.0 * a2 + 2.0 * a3 + a4)
    |> compute
```

This is the proposed spelling, not a compiled probe from this session.
The intended syntax is already a map licence; no new parser construct or let-rec annotation is needed.
For a rank-1 co-iteration the licensed level is X, never Step.
Using the final map establishes the permission boundary over the result row explicitly.
Do not scatter licences over arbitrary internal stage maps and union them into a larger permission.
Its pure pointwise producers may be scalarized under the final map after dependency/effect checks.
Do not require users to rewrite RK4 as one hand-fused scalar block.
Reject candidate (b) for v1: a binding-level `omp(x:1)` needs new syntax/name-resolution semantics.
Reject candidate (c) for v1: an arm-level clause duplicates existing map metadata.
A direct `where omp` on the array-valued named function is not equivalent to a proved spatial map.

Recognize the existing slice-copy pattern at Optimize:fuseSliceWritesModule:1865-1874.
Extend only this pass's map admissibility to retain a final map's Omp permission.
Do **not** relax IRMono:tilePlainKernel globally; its current conservatism protects other consumers.
Return both the scalar cell DAG and resolved permission/provenance from the expander.
The old rewrite at Optimize:1928 creates a plain IRForRange and has nowhere to carry that result.
Thus "just insert a pragma" is incomplete even though the source grammar is sufficient.

Proposed representation: a typed `RecStepPlan` table on IRModule, keyed by loop IRId.
Include buffer ID, time-loop ID, spatial-loop ID, static extents, source callable/span,
licensed spatial levels, read-row facts, same-x proof, and optional row-fold descriptors.
IRModule already carries keyed metadata, e.g. ProviderReads and MutableArrayLets
(`src/IR.fs:IRModule:1765-1798`); this proposal adds a field, not a new imperative language feature.
Keep the ordinary sequential IR as the executable semantics for interpreter/LLVM and the off path.
Clone/remap or invalidate metadata whenever IDs/bodies change; never use a stale proof.
Exact constructor/clone wiring beyond the read IRModule definition remains an implementation audit.
An explicit schedule node is an alternative, but it would require touching every IR visitor.

Legality, checked independently of whether thread emission is disabled:

- Fresh, fully stored trajectory; disjoint output cells, exact static Step and X extents.
- Every time-dependent read resolves to a strictly earlier row, with existing zero-history guards preserved.
- V1 admits the proved `n-1` case; earlier constant lags require the same proof and safe zero slices.
- Never treat the existing alias test at Optimize:1916-1920 as a time-dependence proof: it checks row-view shape, not earlier-row ordering.
- No read of the row being written, future rows, whole prefix, escaping buffer alias, or unknown subscript.
- For barrier-free persistence, every trajectory read additionally uses the same owned x; prior-time reads of other x require synchronization.
- Pure, total scalar DAG, all calls resolved; no provider I/O, random state, display, allocation in a worker, or possible abort moved into a team.
- No captured writes or mut-parameter side effects; local scalar temporaries are private.
- Respect `where repro` scopes and contraction context across inlining; decline if that context cannot be retained.
- Exclude explicit while guards, recognized freeze idioms, early breaks, and dynamic extents.
- Do not infer parallelism from the compiler-generated buffer assignments themselves.

The purity/totality building blocks already appear at Optimize:1828-1832.
The frontend rejects direct captured writes via `TypeCheckSupport.assignTargetError:7980,8005-8016`;
transitive calls are checked in `TypeCheckInfer.fs:inferLambdaFull:12681-12693` (lambda inference region).
For the new automatic movement, recheck the entire inlined DAG; a safe final map does not sanitize an effectful producer.

### G5 schedule tiers and the checksum

**Tier 1: per-step parallel-for.** Each time step creates a team over x, then joins it.
The joined row can be folded by the original serial code, preserving S13's hot-row placement.
This is the simplest implementation/validation stage: about 300 team entries at full size.
It works for previous-row neighbors once a more general, proved row kernel is supported.
V1's pointwise recognizer should decline such neighbor kernels rather than pretend to support them.

**Tier 2: one persistent team.** Partition x by actual team size once, using contiguous ownership.
Each worker executes the time loop and writes only its own x range in every trajectory row.
For this benchmark, stage execution requires **no per-step barrier**: same-x previous values belong to the same worker.
This matches the ownership shape at expert.f90:52-80; it does not transpose storage or eliminate U.
The expert's serial x-block-outer experiments were slower (expert.f90:12-13);
do not introduce that serial schedule as an extra optimization.

With the checksum, choose one of two explicitly different schedules:

1. **Licensed rows:** each worker folds its completed x block for each row into `part[row,tid]`.
   SIMD reduction inside that local row fold requires the row fold's OWN licence.
   After the single team join, combine nonempty partials in ascending x-block/thread order for each row,
   then run the original ascending time fold from its original seed.
   Space at 301 rows and eight double partials is 19,264 bytes plus bookkeeping/padding.
   No per-time barrier is needed; partial slots and trajectory cells are disjoint.
   Keep every row's seed exactly once; do not seed each worker with an arbitrary caller seed.
2. **Repro or unlicensed rows:** persistent team, spatial workshare, barrier, then one serial ascending full-row fold.
   An omp single region performs that fold and the outer accumulator update; its completion barrier precedes the next step.
   This preserves ONE x chain and ONE time chain, including the seed row.
   A deterministic partial-sum tree is still reassociation and is forbidden here.

For a future neighbor-reading step, all workers finish a row before any reads it as history.
That requires a per-step barrier even with licensed checksum partials.
For this recur, the barrier requirement in option 2 comes from the checksum schedule, not the ODE.
An alternative repro schedule computes the whole trajectory without barriers and folds it afterward;
it is legal for pure/total bodies but restores a cold trajectory read, undoing S13's locality.
Treat that alternative as a later measured hypothesis, not the preferred implementation.

### G5 implementation, records, knob, and twins

| Proposed change | Current hook |
|---|---|
| Preserve final-map permission while scalarizing; add dependence proof and plan | `Optimize.fs:fuseSliceWritesModule:1731,1782,1826,1923` |
| Retain row K, outer F, seeds, buffer/time IDs as scheduling facts | `Optimize.fs:fuseRowFoldsModule:2004,2073,2152` |
| Add RecStepPlan table, initialize/copy/remap it | `IR.fs:IRModule:1765` (new metadata; full wiring audit required) |
| Emit per-step/persistent region and private locals; preserve normal fallback | `CodeGenBinding.fs:genForRangeBinding:5255` and new shared scheduling helper |
| Handle expression-context recurrences consistently | `CodeGenExpr.fs:renderUnitStmts:1662,1675` |
| Use the existing plan/census protocol | `CodeGenState.fs:recordOmpConstruct:944;ompSuppressedPhrase:936` |
| Add emission and optimizer-differential fixtures | `tests/OptimizeTests.fs:sliceWriteRk4Emission:298;rowFoldRk4Emission:280`; `tests/OmpTests.fs:runOmpReduceTests:1513` |

Proposed gate `BLADE_REC_STEP_OMP`: unset/on = automatic persistent schedule when proven,
`step` = per-step A/B, `0/off` = preserve the current unoptimized-licence path.
Register it in `Optimize.fs:optimizerGates:77`, since admission changes shared pre-split optimization.
The existing BLADE_FUSION gate still controls scalarization; disabling either must remain correct.
BLADE_OMP_THREADS=1 disables thread emission while retaining permission/proof metadata and scalar fusion.
Do not conflate that setting with BLADE_REC_STEP_OMP=off: they test different transformations.
Add a CLAUDE.md environment-table row; suite clearing already appends optimizerGates at CliSelfTests:95.

Proposed plan/census text, emitted from the same selected plan:
`rec-step-parallel applied -- X; persistent fixed ownership; same-x earlier-row reads; no step barriers; licensed row partials`.
Repro: `... persistent; barrier + one ascending row fold; original time chain`.
Per-step: `... parallel-for per step; row fold after team join`.
Decline: `... declined -- current-row/unknown read | effect | guard | disabled by BLADE_REC_STEP_OMP`.
Any requested but nonthreaded site also gets `[omp] requested but emitted serial: <reason>`.
A failed fusion may leave a separately threaded map: report **fusion declined**, not falsely "omp dropped".
LLVM reads the sequential IR; retain its existing serial-thread census even if the licensed map was inlined away.
Interpreter consumes the same sequential body. Repro+contract-off must match both twins bitwise.
Run opt-diff as well as interp: shared optimizer errors are invisible to an interpreter comparison alone.

## 4. G7 — precise transformation and permission policy

Handle BOTH current entry shapes:
`IRReduceCompute(IRFusion(...), commonKernel, commonSeed)` through genReduceComputeBindingCore:3712/4465;
and per-leg kernel/seed packs through genReduceJoinCore:4547.
Lowering already recognizes tuple kernel/seed packs at `Lowering.fs:joinDeferrableIdsMany:342`.
Onepass reaches the common-kernel fused-tree path, as the single wrapper in emitted line 214 confirms.
Implementing only genReduceJoinCore would miss this benchmark.

**Source spelling: unchanged.** Every onepass leg is folded with builtin (+), hence has its own reorder licence.
The proposed policy permits automatic C++ threading of an eligible builtin-+ join without IsOmpParallel.
This is a deliberate extension of current scheduling policy, not a claim that (+) already requests threads.
Do not change IsOmpParallel globally or borrow the input generator's map licence.
The existing single-fold Path B request gate is at CodeGenBinding:3897-3905;
the mathematical reorder predicate is separate at CodeGenExprSupport:1634-1648.

V1 requires every fold leg to be builtin scalar +, independently licensed and not repro-vetoed.
Require one common dense rectangular traversal, pure/total map and shared-producer bodies,
no streamed source, device/MPI leaf, halo, ragged/grouped/compound layout, bound dependency, or unknown alias/effect.
Read-only duplicate input aliases are fine; writes through aliases are not.
Mixed operators, custom comm folds, tuple accumulators, and differing grids decline to the existing form.
A map's omp may request execution, but cannot legalize an unlicensed fold leg.
No union of one leg's permissions across the other legs.

Choose **generalized Path B**, not a bare OpenMP reduction as the final implementation.
`#pragma omp parallel for reduction(+:s1,s2,s3,s4)` is legal for this narrow licensed shape,
and useful as a runner's comparison prototype, but its merge order is runtime-controlled.
Path B matches the compiler's private-state/seed/fixed-order-combine discipline and scales to per-leg seeds.

Proposed `JoinedFoldChunkPlan` has one traversal/team plan plus a list of leg descriptors:
accumulator type/name, builtin op, original seed, wrapper, licence/repro fact, and chunk SIMD form.
Place it beside `IRStorage.fs:FoldChunkPlan:95`; do not create four separate FoldChunk teams.
Share declarations remain per-iteration values before the consumers, as genReduceJoinCore:4694-4748 already arranges.
Generalize the fused emitter with an explicit fold execution plan, separate from map hostParallel.

Schedule:
1. Evaluate inputs and each leg's original seed once, before the team.
2. Bound the requested team by the extent; partition with ACTUAL team size.
3. Use `blade_fold::chunk_lo(N,tid,team)` for both boundaries (header:102; existing emitter:1631-1632).
4. Each worker initializes four private + accumulators to zero and runs one shared x sweep.
5. Use ONE `BLADE_OMP_SIMD_REDUCTION(+:p1,p2,p3,p4)` inside the chunk: SIMD reduction per leg.
6. Publish each nonempty chunk's four partials; after joining, combine in ascending chunk order.
7. The original seed enters once per leg, outside the partials; preserve empty-input behavior.

For the exact onepass, emission proves all four lowered seeds are 0.0 (lines 215-218).
For seeded joins, do not duplicate a nonzero seed per chunk; use the existing FoldChunk contract at IRStorage:90-94.
For future nonidentity kernels, first-contributed-value/has-value tracking is needed per leg.
V1 can decline those cases; a seed-handling shortcut must never silently change their results.

Keep powers as `v2=v*v; v3=v2*v; v4=v3*v`, or the equivalent existing parenthesized AST.
Do not produce `(v*v)*(v*v)`, pow, or enable broad fast-math on the term expression.
The fold licence permits reassociating the SUM, not the product defining each summand.
The four-leg join is not a K-lane dot-class call; its vector interior is the multi-accumulator SIMD loop.
Current fusedFoldSimdPlan:430 explicitly says the joined traversal has no K-lane form.
Do not split into four vector helper calls: that would destroy the one-pass property.

### G7 implementation, records, knob, and twins

Add shared joined-plan construction beside `CodeGenFusion.fs:fusedFoldSimdPlan:408`.
Call it from `genReduceComputeBindingCore:4493` and `genReduceJoinCore:4769`.
Extend `genFusedLoopNestStreamedWith:187` to emit one team, chunk-private accumulators, and fixed combines.
Reuse the partition/actual-team/empty-slot rules of `CodeGenLoopNest.fs:genLoopNestStreamed:1566-1637,2187-2200`.
Prefer extracting a small common prologue/epilogue helper over duplicating divergent partition code.
Keep the existing omp notes at CodeGenBinding:4503 and 4779 for genuine declines.
Successful threading must replace those notes with an applied record, not leave contradictory "serial" text.
A builtin auto-thread candidate declined by a knob gets a fold-form decline; do not falsely say the user explicitly requested omp.
An explicitly requested fold/map still receives the canonical suppressed-request marker.

Proposed `BLADE_JOIN_THREADS`: unset/on = eligible builtin-+ joined fold team; 0/off = current serial/SIMD join.
This is C++ emission-only: register in `CliSelfTests.fs:suiteClearedKnobs:83-95`, not optimizerGates.
Add its CLAUDE.md env row and record disabled decisions.
Honor BLADE_OMP_THREADS and the global/per-function repro veto independently.
BLADE_FOLD_LANES=off may retain a scalar private chain per chunk, matching existing Path B's A/B convention
(CodeGenBinding:3916-3927); it must not change the powers' parentheses or suppress the team itself.

Plan: `fold form: threaded(Path B joined chunks, 4 builtin + legs, simd-reduction within each chunk)`.
Record one applied omp construct and fixed-order combine, with automatic-builtin permission provenance.
Declines name the first failed condition; repro says `serial (repro): four ascending original chains`.
BLADE_OMP_THREADS=1 retains today's single SIMD sweep in licensed mode; repro retains scalar chains.
LLVM and interpreter arithmetic remain unchanged; no LLVM thread implementation is part of G7.
Default licensed results may differ in low bits; repro+contract-off must remain byte-identical across lanes.

## 5. Tests and bench integration

These are proposed tests, not test results. Use fresh corpus numbers assigned during implementation.
Values below are exact small integer-valued floating computations; no loosened tolerance is needed.
Emission needles belong in F# tests, not invented corpus comment directives.

| New fixture / purpose | Pins and checks |
|---|---|
| recursive-arrays: licensed step, stored trajectory | T=4, X=3, row zero all 1.0, omp map step v+1.0; `// EXPECT: last = 4.0`, `// EXPECT: total = 30.0`. Exposing the trajectory also checks that named storage survives. |
| recursive-arrays: S13 plus licensed step | Same recurrence inside a function whose sole consumer is the row prodsum fold; `// EXPECT: checksum = 90.0`. Covers the plan-aware hot-row path. |
| recursive-arrays: long lag / guard / spatial gather declines | Small hand-computable recurrences with EXPECT pins; assert plan decline and retained legal fallback. Existing while exclusion test is OptimizeTests:261; extend it rather than assuming the IR still resembles the simple loop. |
| recursive-arrays: captured write (rejects) | Step's omp lambda writes outer let mut; `// ERROR: BL4005`, optional exact existing message substring; no EXPECT pin on a rejection. Repeat with OMP_THREADS=1 to prove the knob does not legalize it. |
| loops: four builtin joined legs | x=[1.0,2.0,3.0]; pins s1=6.0, s2=14.0, s3=36.0, s4=98.0, checksum=534.0. Run mt8 and threads-suppressed; also larger non-power-of-two extent for thread coverage. |
| loops: per-leg seeds | Two/three joined sums over the same x, seeds 10.0/20.0/30.0; pin 16.0/26.0/36.0. Test common-seed and kernel-pack entry paths separately. |
| loops: auto-thread decline for a serial captured write | A producer leg increments a captured counter once for each of three x cells; pin counter=3 and the intended sums. No explicit omp: retain serial semantics, decline automatic threading on effects. |
| loops: explicit omp captured write (rejects) | Pin BL4005; also test a transitive helper write. This is distinct from the legal serial captured-write decline. |
| loops: one repro / unlicensed leg | Four-leg join where one fold vetoes reassociation; pin exact ordered result and assert no team/SIMD reduction for the joined fold. Never borrow the other three licences. |
| loops: small/empty/ragged cases | Pin existing empty-seed result or existing rejection, n=1 and n<team behavior; assert no out-of-range partials and no new acceptance of unsupported shapes. |

For G5, compare identical source with BLADE_OMP_THREADS=1 versus unset, OMP_NUM_THREADS=8.
Under repro+contract-off compare complete trajectory cells and checksum bytes, not only the decimal total.
Under licensed mode require identical trajectory cells under the same contraction setting and valid checksum tolerance.
Test per-step, persistent, and off gates; ensure no step calls, row temporaries, or extra trajectory copy return.
Extend `tests/OptimizeTests.fs:sliceWriteRk4Emission:298` and its row-fold emission test at 280.
Run `blade test optimize`, `blade test recursive-arrays`, `blade test omp`, `blade test opt-diff recursive-arrays`,
`blade test interp recursive-arrays`, and LLVM checks through the separate runner.

For G7, extend `tests/OmpTests.fs:runOmpReduceTests:1513`, including current fused_join_simd/pack_join_simd cases at 2033/2039.
Pin exactly one team and one input traversal, four private accumulator slots, chunk_lo, actual team size,
fixed combines, source-order products, and absence of K-lane dot calls in the joined kernel.
Add requested-fold census tests for BOTH entry shapes, with threads off and repro on.
Use dynamic/reduced runtime team sizes as well as eight threads; a request for eight is not a guarantee.
Runner checks `blade test omp`, loops corpus, interp loops, and LLVM lane acceptance/differentials.
Any new pre-split rewrite added during implementation additionally requires its own optimizer gate and opt-diff.

Change each kernel.json's `no_parallel_form: true` to **false only with the load_kernel false-normalization fix**.
Existing-compatible alternative is `[]`; the requested literal false alone would break loading.
Update stale source/spec/notes text at recur.blade:6-7, recur/spec.md:54-60,
onepass.blade:8-10, onepass/spec.md:70-83, and both kernel.json notes.
Recur additionally adopts the final-map spelling above after corpus/emission validation.
Onepass needs no kernel arithmetic/source-licence change.
Preserve LLVM `serial-only`: harness `_build_blade:1158-1162` checks it before the kernel flag.
For onepass repro, only its existing input map threads; the four joined fold chains remain serial.
Do not label a threaded input generator as evidence that the join ran in parallel.
Harness `_build_blade:1166-1177` checks any construct; add kernel-specific plan/emission evidence to the experiment.
Record this distinction in parallel_notes; preserve all named outputs and the inputonly twin.

## 6. Effort, risks, and DROP experiments

**G5: M for admission + per-step; L for the complete persistent/S13 design.**
Risk is semantic: metadata loss during cloning, misproved earlier-row reads, moved effects,
and accidentally parallelizing either checksum chain under repro.
Performance risks are team/barrier cost, trajectory page/store behavior, false sharing at x-block edges,
and losing the already-landed scalarization or hot-row fold.
No speedup forecast is justified by the source inspection.

**G5 DROP experiment:** runner compares OLD 4136f0b5 serial C++ against NEW off/step/persistent,
at full 301x20011, identical newly licensed source, licensed and repro, 1 and 8 threads.
Also measure OLD on original source: adding omp alone may decline fusion and create a misleading slow baseline.
Verify every stored trajectory cell, complete row-fold order in repro, allocations, and census before timing.
If persistent fixed ownership cannot beat NEW's fused serial baseline beyond the paired uncertainty,
or needs a cold checksum pass/extra output storage to do so, DROP it as the benchmark fast path.
Retain sound permission preservation and truthful census independently; do not ship a slower default just to fill mt8.

**G7: M**, because it must cover common-fold and per-leg joins, seeds, shared leaves, effects, and actual-team handling.
The pragma-only special case is S but provides weaker ordering control and does not reuse the established machinery.
Risks: lost traversal sharing, accidentally duplicated seeds, serial writes admitted into an automatic team,
and register pressure/vectorizer behavior with four reductions.
Input generation can dominate whole-program differences, so use the inputonly twin and keep total times too.

**G7 DROP experiment:** runner compares NEW gate-on vs gate-off, plus OLD 4136f0b5,
onepass full 5,000,011, C++ mt8 licensed; collect total and paired inputonly measurements.
Include the simple parallel-for-reduction prototype only as a performance control.
If the joined Path B kernel yields no reliable reduction in incremental work time and no total-time benefit,
or regresses serial/small inputs materially, DROP automatic joined threading pending a measured size policy.
Never "recover" the win by dropping outputs, splitting into four passes, or reassociating powers.
Keep census regressions and corrected historical notes regardless of the performance verdict.

## 7. File overlaps, wave order, and A/B protocol

| Other item | Shared files/functions or design boundary |
|---|---|
| A: ordered scatter / G35 | CodeGenBinding fold routing, CodeGenLoopNest team helpers, CodeGenState records, IRStorage plans, OmpTests. Reuse complete-chain repro reasoning; never conflate it with partial-sum reproducibility. |
| B: store/cell epilogues | Direct overlap with Optimize.fuseSliceWritesModule/fuseRowFoldsModule, RecStepPlan row-fold descriptors, OptimizeTests. Agree on row completion, seed ownership, and whether a checksum is a row pass or cell epilogue before coding. |
| C: sibling folds / discovery | Direct overlap with CodeGenBinding.genReduceComputeBindingCore/genReduceJoinCore and CodeGenFusion. New joins must use the same per-leg plan; discovery must not union permissions. Keep input-checksum fusion out of E's measured credit. |
| D: persistent tile teams | Shared team/partition design, CodeGenState, OmpTests, and knob/docs surfaces. Recur's independent x chains are not a triangular work-balancing problem; avoid transplanting that scheduler. |
| All items | CLAUDE.md env table, CliSelfTests.suiteClearedKnobs, bench gap register/RESULTS integration, harness controls. Coordinate edits rather than creating competing plan vocabularies. |

**Wave 2: G7 first.** It uses an existing source licence, a single well-defined traversal,
and current chunking/census primitives; it does not require a new cross-time dependence abstraction.
Land the false-normalization harness fix and census regressions alongside it.
**Wave 3: G5 persistent form**, after B's row/cell epilogue choices settle.
The per-step M-sized prerequisite can be prepared in wave 2, but its speed must be measured before declaring the gap closed.

OLD is the supplied 4136f0b5 binary, not the earlier RESULTS tree.
Use private working directories; the separate runner owns builds, corpus runs, and benchmarks.
Interleave OLD/NEW and NEW on/off arms, non-power-of-two smoke/full extents, at least 21 paired rounds;
report medians, paired uncertainty, inputonly where applicable, and relevant plan/census lines.
Matrix: kernel x {serial,mt8} x {licensed,repro} x {OLD,NEW-off,NEW-on}; G5 adds step/persistent.
Keep LLVM serial controls and record no LLVM mt8 claim.
Pin BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off for byte comparisons; benchmark contraction settings follow the harness regime.
Set BLADE_OMP_THREADS=1 for serial emission and unset for mt8 with OMP_NUM_THREADS=8.
Retain the benchmark's stored trajectory and all named scalar outputs.
Both proposals are permission/scheduling work; neither is permission to replace the language idiom with hand-optimized benchmark source.
