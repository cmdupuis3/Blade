VERDICT: DO NOT MERGE

9. CLOSED — direct whole-name rebinding now joins initializer and every assignment RHS (Optimize.fs:903-941); fresh→captured and captured→fresh conservatively retain captured provenance. Storage-bearing field/element RHSs are included. 498/022 and the positive fresh→fresh control cover the reported hole; runtime claims were not rerun.
10. NOT CLOSED — choice/fallback/guard/sequence, tuple projections, object_for/>>@/<@> children and unresolved function-typed kernel names are covered, but a named partial ARRAY projection still hides its deferred producer (12).
11. NOT CLOSED — emitReduce and emitReduceCompute both scope FoldOperandWrites, and module entries install the let table; the orbit-class transformation still executes when that flag forbids reassociation (13). Hot-row/producer paths also retain effect holes (14-15).
Merge (a), old writing-join cases: CLOSED — loops/489 moved to mutability-errors/021 with BL4005/message/span; all three G7 writers moved to g7RefusedCases; loops/490 references 021. G7 harness checks the lowering message, not the diagnostic code itself; 021 pins the code.
Merge (b), joined admission: NOT CLOSED semantically — both entry shapes run joinEffectAudit before fusedFoldSimdPlan and before joinThreadAudit; fusedFoldSimdPlan/joinedFoldThreadPlan intersect each leg's foldReorderLicensed. Leaf/shared-producer/fold wiring survives, but it inherits 12's incomplete deferred closure.
Merge (c), storage fields: CLOSED — JoinedFoldChunkPlan/JoinedFoldLeg coexist with FoldOmpDecline; buildLoopNestCodeGen initializes FoldOmpDecline=None (IRStorage.fs:1377), preserving epilogue fields. Incoming scatter audit below.
Merge (d), row-slots decline: CLOSED — row_slots_declined_outer_kernel_write pins the G41 write reason and excludes slots/team.
Merge (e), register: CLOSED — G7/G39 plus G41-G43/G45 retained; G45 and the 68-versus-20 report remain OPEN. G41's blanket FIXED/every-entry claim is contradicted below.
Merge (f), numbering/docs: CLOSED — F's six files are loops/492-497, new rebind is 498; current references in requested docs/tests are updated; tuples/031-033 and mutability-errors/014-024 do not collide. CLAUDE's G7 row names the refusal.
Merge pass order: CLOSED for the reviewed sibling admission — Lowering:2927 refuses written joins BEFORE optimizeModule:2981; Optimize:4515-4522 runs constant folding → elementwise fusion → slice-write fusion → row-fold fusion → invariant hoisting → sibling sharing; then IRLift, S2/S4, CSE and scheduling. Sibling sharing has its own transitive mayWrite barriers (3616-3643), stored-operand restriction, and closed kernelTotal/sfTotal/IRTotality admission (4073,4189-4195); a direct writing map/fold cannot be shared merely because the earlier refusal has already run.
Merge hunk preservation: CLOSED — compared both parents for the requested files; the read-only three-way preview has one textual conflict, OmpTests' tail. Both G41 and G7 runtime matrices survive. No unexplained dropped compiler hunk found; E's extracted IRTotality audit and D's tile argument/scratch changes survive. Existing loops/214 duplicate numbering and the two path_b_reduce_over_computation labels predate this merge; no new 4xx/tuple/refusal/G7/G41 collision found.

Reviewed immutable 29cb3506, d63293e6 (parents 03e88cb4/d332f700), and incoming 906659e1/763cbc3a. Files/git only; NO builds, emits, tests or program executions. All counterexamples and numeric consequences below are source-derived, NOT reproduced. Line numbers are 29cb3506 unless marked incoming. Only this review file was written. Reported test totals, spans and the 103-file rescan are builder evidence. New LLVM goldens check absence of reassoc globally and presence of the substring `fadd contract double`; they do not identify the accumulator instruction specifically.

12. HIGH — src/Optimize.fs:1003-1018 (also 834-843): a named partial array view is wrongly treated as a materialization boundary.

`chase` handles IRTupleProj but not array-valued IRIndex. LLVM's emitArr IRIndex (4625-4630) uses emitArr, then rowView; rowView:2365-2371 explicitly preserves AVirt/AVirtFlat. Thus reading the view executes its source map, although exprMayWriteIn on the view's name returns false. Minimal probe:

```blade
type I = Idx<2>
type J = Idx<3>
let mut G = [0.0]
let m = method_for(range<I>, range<J>) <@> lambda(i, j) -> {
    G(0) = G(0) + 1.0
    G(0)
}
let row = m((0 : I))
let total = reduce(row, (+), 0.0)
```

`row` has one syntactic reader, so emitReduceCore:6155 does not force it; its writer runs inside the fold while foldFmfDecorable admits reassoc. Substituting a reduction join over row also defeats the frontend's named-producer chase. C++ forcing its positional read does not prove that the LLVM value is stored.

Exact fix: close deferred effects through array-valued projections/views (including their named/tuple aliases), preserving explicit IRCompute/materialized boundaries; use the emitter's actual stored/virtual state where its forcing differs. Audit IRApp(IRObjectFor,...) as well: objectForApply:6000-6053 constructs virtual arrays, so the blanket comment that calls are always stored is not a valid LLVM invariant. Add a partial-view fold golden plus pure/materialized controls, and a join refusal control.

13. BLOCKER — src/EmitLlvm.fs:6430-6446,6483-6515: orbit regrouping ignores FoldOperandWrites.

tryOrbitFold accepts `hasFlatRead a`, which includes AVirtFlat (2069). emitOrbitFold reads foldFmfDecorable only to decorate additions; even when false it still performs one canonical-pool pass and the orbit multiplicity combine. OrbitFold.pureScalarBody audits the scalar expression AFTER substituting the compact read, not the deferred producer behind SourceExpr. The wrapper detecting the write therefore does not prevent the transformation.

```blade
type I = Idx<2>
let x = method_for(range<I>) <@> lambda(i) -> Float64(i) |> compute
let g0 = method_for(x, x) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> a + b |> compute
let mut G = [0.0]
let g = method_for(g0) <@> lambda(v: Float64) -> {
    G(0) = G(0) + 1.0
    G(0)
}
let total = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> g(i, j), (+), 0.0), (+), 0.0)
let seen = G(0)
```

The shape-preserving map is AVirtFlat (5706-5719). g has one syntactic read; the ordinary LLVM logical walk consumes its producer at four coordinates (sum 10, counter 4). The orbit arm evaluates three canonical cells and weights the off-diagonal value (sum 8, counter 3). Turning off FMF cannot restore the missing call. Compare total/seen only to avoid print-time forcing obscuring the counter.

Exact fix: decline tryOrbitFold before emitting anything when FoldOperandWrites is true; independently require a stored APool or a proved effect-free virtual source. Preserve the normal operand forcing/read-count policy. Add an orbit-on/off writing-source regression, a pure virtual control, and a materialized writing-source control. Do not fix only the fadd flags.

14. BLOCKER — src/Optimize.fs:2057-2099; src/EmitLlvm.fs:4302-4325: hot-row scheduling moves a read across the producer's writes.

The planner checks the CONSUMER row kernel and intervening bindings for Repeatable, but never checks the producer or read/write interference. A repeatable reader of captured G is not invariant while the producer writes G. With no intervening binding, the following is admitted:

```blade
type I = Idx<2>
type J = Idx<3>
let mut G = [0.0]
let p = method_for(range<I>, range<J>) <@> lambda(i, j) -> {
    G(0) = G(0) + 1.0
    0.0
} |> compute
let total = reduce(method_for(p) <@> lambda(row: Array<Float64 like J>) -> G(0), (+), 0.0)
```

The written two-pass program reads 6 twice: total 12. tryBindHotRowProducer runs the row consumer after each row: it reads 3 then 6, total 9. This also fails under repro; the outer combine's write-aware licence is irrelevant once the row values were changed. G(0) alone is not `hasWork` for invariant hoisting (Optimize:1417-1423), so that pass does not remove this witness.

Exact fix: conservatively refuse hot-row scheduling when the producer may observably write, or prove its write set disjoint from all consumer/transitive capture reads and effects. Add hot-row on/off and repro controls. This is a pre-existing scheduling defect exposed by the requested every-entry audit, not a dropped merge hunk.

15. HIGH — src/CodeGenLoopNest.fs:2571-2579,2813-2816 (and generic fallback 1763-1767): the flat-epilogue producer is not guaranteed to keep its write order.

buildFlatEpilogue (CodeGenCuda:2115-2144) checks the fold's licence and parity of existing producer/fold team requests; it does not audit the producer. tryGenFlatEpilogueNest then emits BLADE_IVDEP on an unannotated serial producer. The flat detector only checks storage/peel shape; CodeGenCuda:3843-3846 has no effect gate. BLADE_IVDEP expands to GCC ivdep (blade_portability.hpp:81), asserting away loop-carried dependencies that a writing map can actually have. Probe with BLAS off:

```blade
let x = [1.0, 2.0, 3.0, 4.0, 5.0]
let mut G = [0.0]
let p = method_for(x) <@> lambda(v) -> {
    G(0) = G(0) + v
    G(0)
} |> compute
let total = reduce(method_for(p) <@> lambda(v) -> v, (+), 0.0)
```

The producer's G recurrence is a real memory dependence. The same invalid assertion exists in the ordinary flat materializer; merely declining the epilogue is insufficient. This review does not claim a particular GCC run vectorizes it or produces a particular wrong result.

Exact fix: gate dependence-discarding producer annotations on the transitive write analysis; retain an ordered producer loop for writers, including the generic ivdep fallback. Add emission-negative writer controls plus pure positive controls. Folding already-stored scalar cells with a literal seed and Identity/Square/builtin combine is otherwise independent of producer writes: the LLVM flat epilogue's reassoc flag alone does not reorder memory side effects, and C++'s parity check does not manufacture a team for a serial producer. The unsafe assertion is on production itself.

Remaining fold-site audit (not a claim that every shape was executed):

- C++ ordinary stored-array reduce: CodeGenBinding:3442/3509/3725 reads foldReorderLicensed; deferred single fold:4336-4340 forms licensedFold from fold licence AND map effects; Path B, row slots:4365-4391, threaded lanes:4497, serial lanes/SIMD:4552 all use it. Row slots additionally run rowSlotAdmission/IRTotality.
- Joined tree:4976-5009 and pack:5283-5324 run joinEffectAudit for SIMD and team, with leaf/shared-producer/fold lists; CodeGenFusion:555/598 independently checks every fold licence. The hole is the analysis in 12, not a lost intersection.
- LLVM emitReduce:6071-6074 and emitReduceCompute:6572-6575 scope effects; ordinary, joined and compact combines use foldFmfDecorable. Compact bricking:6345 uses foldFmfLicensed, including FoldOperandWrites. Domain diagonal:6205/6638 requires that same licensed result; C++ uses licensedFold plus domainFoldOrder/domainMapKernelPure. Orbit transformation is the exception in 13.
- Hot-row consumer:4424 combines stored row sums under foldFmfDecorable, but producer scheduling is unsound in 14. Compact-row planning checks consumer/intervening MayWrite, and orbit scalar-body constraints apply; fused compaction uses predOk/squareMap/builtin-plus and intervening-write gates. The flat epilogue's independent stored-cell fold does not excuse 15's producer annotation.

Incoming W2-A scatter (906659e1): planStrict (OrbitFold.fs:698,720) calls foldReorderLicensed for each level, then independently audits the cell function, every kernel and seed with strictWalk/strictCallable AND IRTotality. At that tip foldReorderLicensed is still the OLD predicate without callableMayWrite; merging F makes this call write-aware. C++ scatterPlan (CodeGenBinding:4193) rejects deferred sources/free captures and genOrbitScatterCpp:4088 emits ordered chains. LLVM tryOrbitScatter:6556 requires APool at 6586; emitOrbitScatter:6596 clears FoldFmf at 6607 and restores it. Neither upstream scatter site reads FoldOperandWrites (the field is absent there); this strict stored-pool form consumes no reassociation licence, so its separate closed purity audit is sufficient. Keep its dispatch INSIDE F's emitReduceComputeCore/effect wrapper when merging. Unlike orbit-class folding, scatter already excludes the virtual-source hole in 13.

F-vs-763cbc3a overlapping files (F delta d332f700..29cb3506, A delta 763cbc3a^1..763cbc3a): CLAUDE.md; bench/notes/gap-register.md; src/CodeGen.fs; src/CodeGenBinding.fs; src/CodeGenExprSupport.fs; src/CodeGenLoopNest.fs; src/CodeGenState.fs; src/EmitLlvm.fs; tests/OmpTests.fs. Optimize.fs is NOT changed by that merge delta. Read-only three-way preview of 29cb3506 with 906659e1 reports textual conflicts in CLAUDE.md, gap-register.md and OmpTests.fs; the overlapping compiler files still need semantic reconciliation despite merging textually.
