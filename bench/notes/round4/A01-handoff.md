# A01: retained-inline pool lifetime event

Status: implemented, frozen for root review / sole-runner validation; uncommitted.
Checkout: `.round4-worktrees/a-lifetimes`, branch `codex/round4-a-lifetimes`, base `361504b6`.
No build, emit, compilation, fixture execution, test, ASan run, benchmark or executed-pin generation was performed by this lane. `git diff --check` passes. New constant-valued corpus pins are derived analytically.

## Contract and implementation

A retained scalar-return inline owns a first-class lifetime region: a unit let binds `IRBeginPoolScope(call)`; `IRReleasePools(IRVar(scopeId, unit), [IRVar(poolId, poolType)...], call)` closes it. The scope and pools are normal canonical IR children, so freshening/substitution preserves their references. Events are inserted while serializing the reconstructed join spine, including after a deleted final join, after the evaluated return and before the caller resumes. One region per retained call covers multiple leg plans and older W1-D join-driven inlines. No lifetime side table remains.

C++ module/function/statement-loop dispatch releases each named dense allocation through the existing tracked/free-and-suppress helper (main uses its dense fallback). The ordinary function-body dispatch handles events explicitly, including non-atom returns. LLVM opens a nested pool-tracking scope at begin and pops/frees it at release: no enclosing scope owns those allocations, so it cannot free twice. Existing `KnownIds`/`keepPool` logic spares any pre-existing caller binding forced within that scope. Inserted joined results are scalar/tuple-of-scalars; their extra accumulators use stack slots. Interpreter events are no-ops.

CSE conservatively excludes entire event-bearing functions, preserving pool definitions and duplicate return evaluations and preventing cross-boundary equivalences. Scratch reuse also excludes entire event-bearing functions, with a recorded reason: internal callee donors/reusers are deliberately not optimized in a retained inline. This may lose a reuse opportunity versus gate-off; no peak-memory or timing gain is claimed. Numeric kernels, seeds, chains and licences are unchanged.

Resource sequencing remains distinct from source writes. A dedicated transitive lifetime-barrier predicate audits downstream hot-row B1/B2, compact-row, compaction shared/gather/in-pass, orbit-pull and membership scheduling; the original optimizer stage preserves event-bearing IR on re-entry. Validation checks nested scope matching, exact pool ownership/type, missing or duplicate releases, and later reads through storage/deferred aliases, callable captures and carried/embedded index extents. Scalar snapshots remain usable after an input pool is released.

## Honest capability bounds

Every computed owned pool is checked before retaining the inline: one static plain nonsymmetric dense axis and a scalar element. Other layouts decline. C++ expression/kernel let rendering cannot perform tracked statement cleanup: functions used as values/kernels decline retained inlining with `lifetime event not honoured by expression/kernel renderer`; an event reaching that renderer produces an explicit refusal. OpenMP coverage mode likewise declines retained inlining before planning (admission and rendering share the per-flow coverage flag). Ordinary direct-call scalar drivers annotated `omp` still use `genFuncBody` and its unconditional `SFunc` frame; functions actually used as kernels are caught by the value/kernel refusal.

## Witnesses authored, not executed

- Keep 030/031; strengthen ordinary function return to `s + 1.0` and assert coverage mode really keeps the calls/no fused legs.
- New 032: unused trailing explicit join deleted, earlier atom returned.
- New 033: identical named pure caller map followed by cube fold, with CSE on/off emission checks.
- New 034: repeated named pure helper as earlier binding and non-atom return.
- New 035: ordinary function caller and non-atom return.
- New 036: caller BL8013 after return; C++ and LLVM emission assert cleanup precedes the failing binding; runtime fault/site/frame twins in both lanes.
- New 037: donor/reuser-shaped callee, with the enclosing lifetime region excluded from scratch reuse.
- Optimize negatives: direct post-release read, deferred alias, captured pool, embedded extent, foreign pool and missing release; untouched and freshened regions must validate; resource barrier must not become a source write; function-value renderer refusal.
- LLVM facts: 019, 031 and 032-037 trace each inline allocation to its own cleanup. Gate-off checks the named outlined kernel allocation/slot/free relationship; only 032 may use its older explicit-join event route. Tuple-member controls 038-040 pin ordinary scalar/array calls and the genuine tuple ABI.
- CellEpilogue: new positive witnesses join strict raw-bit gate on/off checks in C++/LLVM plus ASan/census/cache controls. No new test result is claimed.

## Exact runner request (serialized, private working directory)

Build this frozen checkout once: `dotnet build Blade.fsproj -c Release`.
Then run only these named targeted blocks against this exact content snapshot, recording command, pass/fail/skip totals and toolchain skips:

1. `blade test optimize`
2. `blade test sibling-folds`
3. `blade test cell-epilogue` (includes gate twins, raw bits, failure order and ASan attempts)
4. `blade test llvm facts`
5. `blade test llvm sibling-folds`
6. `blade test interp sibling-folds`
7. `blade test opt-diff sibling-folds`
8. `blade test omp-coverage`
9. `blade test omp-reduce`
10. `blade test cli`
11. `blade test docs`
12. `blade test corpus-wiring`
13. `blade test orbit-scatter`
14. `blade test sql`
15. `blade test index-types`

For the route witnesses use repro / contraction-off / BLAS-off; the new Optimize witnesses explicitly exercise CSE 1/0 and reuse 1, while CellEpilogue's ASan control pins reuse off. Inspect release order and actual fused/refusal route, not numerical success alone. No default/full-suite launch is requested. No performance run is requested; historical LLVM lifetime-matched saxpy attribution remains withdrawn.

## Owned files / frozen identity

No new F# files and no fsproj order change. Changed compiler files: `IR.fs`, `IRLift.fs`, `IRPrint.fs`, `IRValidate.fs`, `Optimize.fs`, `Lowering.fs`, `CodeGen.fs`, `CodeGenBinding.fs`, `CodeGenExpr.fs`, `CodeGenLoopNest.fs`, `CodeGenState.fs`, `EmitLlvm.fs`, `Interp/Core.fs`, `Toolchain.fs`. Changed tests: `OptimizeTests.fs`, `LlvmTests.fs`, `CellEpilogueTests.fs`; new sibling-folds corpus 032-040. Updated `CLAUDE.md`, `docs/features.md` and G69 in `bench/notes/gap-register.md` to describe the implementation and pending validation.

`A01-content-sha256.json` hashes every changed source/test/docs file (excluding these A01 notes). Manifest SHA256: `bafdfccef81332208c2aad293bf80a40525b4f8c83d7fd093d041c873cdc1a1b`.

## Initial runner build repair

The sole runner's initial build failed before any tests: new `OptimizeTests.fs` nested record/lambda expressions at former lines 2656-2712 violated F# strict indentation. This repair formats the `mutate`, `freshened` and `captured` helpers with explicit multiline record fields and named rewrite helpers. All assertions and corruptions are preserved. Only `tests/OptimizeTests.fs` changed from the prior frozen content manifest. The coding lane did not build or run tests; the runner rebuild is pending. `git diff --check` passes.

The subsequent runner build found one remaining test-helper inference error, FS0041 at `OptimizeTests.fs:2652` (`String.Contains` char/string overload). The helper now explicitly accepts `needle: string option`. This is the only source change in this repair; assertions remain unchanged. Rebuild remains reserved for the sole runner.

## Optimize diagnostic checkpoint repair

The sole runner subsequently built successfully and reported `optimize`: 510 passed / 1 failed. The only failure was `inline_lifetime_expression_renderer_declines` (`refusal=false; original call present=true`). Static diagnosis: `judgeAt` correctly refused the retained inline, but `processRegion` placed that reason only in the `refusals` evidence list. The caller's independent x/y sibling folds still form an applied group, so its Outcome was Applied with the capability refusal in Evidence; the test deliberately required an explicit Declined Outcome. The fallback Declined record runs only when no group record exists. This was a diagnostic mismatch, not evidence of an unsafe retained inline.

The repair emits one explicit lifetime-capability Declined decision per memoized `(region statement, call ordinal)` before recording independent group outcomes, includes the original call identity in the reason and its position in evidence, and marks the decision as recorded so the no-group fallback cannot duplicate it. Identical calls at different positions remain separate candidates; repeated discovery/reconstruction reads the memoized cache and adds no second decline. The rewritten IR and renderer admission behavior are unchanged.

The test keeps its original refusal/call-present/no-fused-legs assertions and is strengthened: a BLAS-off/repro/serial baseline must actually fuse, the function-as-value case must produce exactly one capability decline for `kernel(x, y)` in `driver`, and the independent sibling group must still record Applied. Failure output now prints the sibling decision records. This repair changes only `src/Optimize.fs` and `tests/OptimizeTests.fs` relative to the preceding manifest. `git diff --check` passes. No build, emit or test was run by the coding lane; sole-runner revalidation is pending.


## LLVM tuple-member routing repair and probe evidence

The sole runner validated manifest `b8e5529e8e4b57bb768c157e69244544a53613b1c5f09a1143dbff549492704f`: build passed; Optimize 511/0, sibling-folds 37/0/0 and CellEpilogue 87/0/0 (including ASan) passed. LLVM facts failed 114/1/0 only on 019's gate-off cleanup assertion. The subsequent LLVM sibling-folds launch was interrupted at root's stop request after partial output (22 passes, one skip), so no completed result is claimed. Full results/log references are in integration `bench/notes/round4/A01-tests.md`; raw 019 probe artifacts are in this checkout's `bench/.build/A01-runner/019-lifetime-probe`.

The probe disproves allocation elision: gate-off binding 29 retains capture-free scalar `IRApp func11 : Float64`, whose kernel body owns a computed producer. Raw gate-off LLVM instead expands that body in main, allocates producer `%r40` (1003 cells), has no `@bl_kernel` definition/call and never frees that producer. The separate resident caller pool `%r83` also has no free, but its lifetime is unrelated to the scalar callee defect. Gate-on has its explicit region around pool v71, allocation `%r4` and release `%r60`. The earlier generic assertion/wording that every gate-off example was already outlined was inaccurate; the missing scalar callee cleanup was real, rather than a vacuous allocation-elision control.

Root's localized diagnosis is implemented: `emitTupleParts`' callable `IRApp` arm now additionally requires the expression's stripped result type to be `IRTTuple`. Scalar and array tuple leaves therefore fall through to `classifyValue` and their existing `emitCall` / `emitArrCall` ABI. Capture-free scalar calls use the ordinary outlined function scope, which frees its owned pools before returning; array calls use its existing result-spare ownership logic. Genuine tuple-returning calls retain their current inline tuple ABI. No `applyKernel`, pool-scope algorithm or general closure-lifetime behavior changed.

The LLVM gate-off assertion now locates the specifically named `kernel` definition and proves each actual owned allocation is stored into its own tracking slot, loaded exactly once and freed exactly once before scalar return. It rejects slot overwrites and later direct/GEP-derived backing references, and requires a real call to that definition. 019 specifically requires one such owned allocation. Other outlined witnesses prove every actual owned allocation, allowing an existing reuse optimization to reduce allocation count. Only 032 may lack that outline: its explicit join can still trigger the older retained-inline route with epilogue legs disabled, and it must prove the exact expected event-based allocation cleanup. An unrelated free anywhere in the module can no longer satisfy the check.

New corpus/fact controls: 038 repeats a scalar pool-owning call twice inside a tuple (ordinary definition, two call sites, one owned allocation/free per invocation); 039 repeats an array-returning tuple leaf and consumes both returned arrays (two ordinary calls, each returns its actual live backing pool, no premature callee free); 040 calls a genuinely tuple-returning function (real pre-emission tuple IR call, both reductions and scalar outputs emitted inline, no outlined tuple ABI). CSE, sibling folds and epilogue legs are pinned off for the three ABI facts to keep repeated sites visible. Pins are analytically derived: 038 squares 1..37 => 17575; 039 sums 1..37 => 703; 040 sums 0..36 => 666 and squares => 16206. No generated output pins were used.

Relative to `b8e5529e`, this repair changes only `src/EmitLlvm.fs`, `tests/LlvmTests.fs`, adds corpus 038-040, and refreshes these A01 notes/manifest. Root review and a new sole-runner build are required. Rerun `llvm facts`, `llvm sibling-folds`, `sibling-folds`, `cell-epilogue` and `interp sibling-folds` first on this snapshot; include `llvm tuples` and `llvm functions` to check the tuple/scalar/array routing boundary, then resume the remaining original targeted queue as root directs. Previous passing results apply only to unchanged implementation. The coding lane performed no execution; static `git diff --check` passes. No full/default suite or performance run is requested.


## Genuine tuple control assertion correction

The runner built manifest `24b64af9b3d2b45866e6c34bab3eb424bb81886450cea9f9d7c066dfbd7e6960` successfully and reported LLVM facts 117 passed / 1 failed / 0 skipped. All allocation-specific lifetime checks and repeated scalar/escaping-array controls passed. The sole failure was 040's requirement for a `member_pair` frame-name string. The raw 040 probe under the exact member pins (`bench/.build/A01-runner/040-tuple-probe/040-member-pins.{ll,bindings.txt,callables.txt}`) shows capture-free callable9, a real `IRApp` with `(Float64, Float64)` result in binding21, both reductions expanded in main and both scalar print calls, with no tuple outline. It contains no panicking arithmetic.

This is an inaccurate assertion, not a routing defect: `framePush` writes markers, and `resolveFrames` retains a marker pair/name string only if its region reaches a panic directly or through a callee. Pure floating-point reductions therefore correctly lose the frame and its interned name. Existing fact-layer shadow-frame tests already independently cover kept panicking frames, elided nonpanicking frames and transitive panic reachability; this correction adds no new frame-ownership claim.

Only `tests/LlvmTests.fs` changes from the preceding manifest. Each member test now lowers and validates once under its exact pins, inspects that same IR, then emits it. 040 requires a reachable binding expression calling the named capture-free tuple callable with a two-component tuple result before emission, both fadd reduction chains and the squared-input fmul in main, two scalar print calls and the s/q output names, plus absence of a tuple outline. Corpus040's analytic runtime pins remain s=666.0 and q=16206.0. The production `emitTupleParts` result-type guard is unchanged. This replaces the incorrect frame assertion with positive source-IR/body/output evidence; an absent unused function alone cannot pass it.

No build, emit or test was executed by the coding lane. Static `git diff --check` passes. Request sole-runner rebuild and `llvm facts` first, then resume the root-approved pending targeted queue (including numeric sibling-folds/LLVM differential coverage for 040). Prior source checks remain attributable only to their exact snapshots.
