VERDICT: DO NOT MERGE

Reviewed fixes `9bf284b0..03e88cb4`, with full-range context from `6b3e5293`. W2-E = `ca494bac`; W2-A (including E) = `52d0c4aa`. Source/git only; NO builds, emits, tests, or program executions. Counterexamples below are source-derived, NOT reproduced. Lines refer to W2-F unless prefixed E/A. Only this review file was written.

1. NOT CLOSED — initializer provenance fixes direct views/returned aliases and argument-aware calls, but ignores subsequent rebinding; fresh storage can become captured storage without changing the summary (new 9).
2. CLOSED — Lowering:2787-2798 checks the shared scalar kernel as well as tuple kernels whenever the resolved computation has multiple leaves; 017 covers the earlier omission. Effect-analysis holes remain separate findings.
3. NOT CLOSED — let/callable aliases and direct calls are covered, but the deferred-producer closure still omits supported deferred wrappers; unresolved kernel references also differ from unresolved IRApp heads (new 10).
4. NOT CLOSED — FoldOperandWrites fixes the IRReduceCompute path, including its joined/domain/compact arms, but ordinary IRReduce can consume a writing virtual producer without setting it (new 11).
5. CLOSED — omp-reduce adds 486/487/489 total+counter checks for emission-off, emission-on/T=1, and five T=8 runs; 014-020 pin message+span; LLVM goldens adds writing/pure FMF controls and a native three-leaf emission check. This is source coverage, not independently rerun certification or evidence of an actual eight-thread team.
6. CLOSED — G45 is explicitly OPEN; 491 pins block 36/8 versus function 20/4; 488 states the exception; the 68-versus-20 report remains UNVERIFIED/OPEN. The semantic defect itself is not fixed.
7. CLOSED — formalism 5.2/10.2 identify the acceptance-policy change and local-scratch exception; diagnostic JSON restores the parallel-body case and removes the count; CLAUDE distinguishes observable writes.
8. CLOSED — 486 numbers and census wording corrected; G45 allocated separately from A's G44. Merge preservation requirements remain below.

9. BLOCKER — src/Optimize.fs:881-899,923-931: provenance is frozen at the initializer, despite whole-array rebinding.

The locals table records only IRLet initializers. An IRAssign to a local name is ignored and never joins the RHS provenance into that name. Adapt 489's fold body to:

```blade
let t: Array<Float64 like K> = method_for(range<K>) <@> lambda(k) -> 0.0 |> compute
t = G((0 : K))
t((0 : K)) = t((0 : K)) + 1.0
a + b
```

Here G is 489's rank-2 captured array. `prov(t)` remains fresh, so the store is invisible to both the G41 gate and G43 refusal. The assignment really can repoint the wrapper: CodeGen.fs:248-270 emits ordinary assignment unless copyInPlaceAssign succeeds; CodeGenLoopNest.fs:4238-4256 restricts that optimization's candidates to module bindings, not this function-local t. CodeGenExpr.fs:718-728 has the same fallback. Thus the supposedly local store writes G, reopening Path B/K-lane admission for the comm fold. Assigning a captured view into a local tuple/struct field creates the analogous problem for later projections.

Exact fix: make provenance flow-sensitive across rebindings and aggregate-field stores, merging branches to a conservative fixed point; alternatively, conservatively invalidate freshness for any storage-bearing local/aggregate that can be rebound. Do not treat IRLet freshness as lifetime ownership. Add this rebind variant to the refusal and G41 emission/runtime gates, plus a fresh-to-fresh control.

Freshness audit: `G(0) |> compute` is NOT a guaranteed copy. CodeGenExpr.fs:350-354 unwraps non-combinator compute; genComputeBinding's fallback at CodeGenBinding.fs:1762-1765 also evaluates the view. The new classifier is conservative for this case: IR.fs:2594 recursively unwraps compute and IRIndex is nonfresh. Fresh materialized scalar-cell maps/literals and let-rec's IRUninit buffer are owned; views/projections preserve their base provenance until a rebind breaks it. Function-returned arrays stay External, including through alias/view chains. IRTScalar's variants are numbers/bool/unit/String, not an arbitrary array/reference carrier (Types.fs:1146-1155); no writable array hidden in that scalar classification was found. Recursive-call summaries conservatively mark storage parameters; no separate self-recursion escape found.

10. HIGH — src/Optimize.fs:827-832,955-968: deferred closure stops at wrappers that are not materialization boundaries.

`isDeferredForm` omits IRChoice, IRFallback, IRGuard and IRSequence, although CodeGenBinding registers those as deferred and LLVM explicitly recognizes choice/guard as deferred (EmitLlvm.fs:7183-7185). For example, use a named choice as a join operand:

```blade
let x = [1.0, 2.0, 3.0]
let mut G = [0.0]
let q = (method_for(x) <@> lambda(v) -> { G(0) = G(0) + 1.0; G(0) }) <|> (method_for(x) <@> lambda(v) -> v)
let r = object_for(<&!>) <@> (reduce(q, (+)), reduce(q, (+)))
```

The join uses identity leaves over q; chasing q reaches IRChoice and returns None, so the lowering refusal misses the writing producer. LLVM's choice is an AVirt producer that executes its left read per cell (EmitLlvm.fs:4690-4711), not stored data. C++'s join sharing supports only IRApplyCombinator operands (CodeGenBinding.fs:5227-5231); a later C++ emission failure would not make the claimed lane-independent lowering refusal complete.

Exact fix: share the actual deferred-value classification/closure with lowering and the emitters, traverse every deferred wrapper and nested alias/projection, and stop only at a proved stored/materialized value. Add named-choice/guard and outer-scope alias refusal controls. Also make unknown-effect handling position-aware: unknown IRApp heads are conservative, but an unresolved IRVar/IRParam in a kernel slot currently falls through as pure (Optimize.fs:947-970; IR.fs:2409-2415). Resolve/substitute higher-order loop/kernel parameters or refuse an unproved kernel; do not claim unresolved *kernels* are covered merely because unresolved *calls* are. Update the stale opposite-policy comment at Lowering.fs:2722-2723.

Scope/module audit: Lowering's lets/functions tables cover all modules and all function-body lets, so an ordinary outer let alias, including one from another lowered module, is no longer lost just because it is outside the immediate body. An opaque loop/function parameter has no initializer in that table; the unknown-kernel issue above is the remaining proof obligation. Backend deferred tables are narrower and cannot substitute for this program-wide closure.

11. BLOCKER — src/EmitLlvm.fs:6055,6125-6149,6263 versus 6548-6554: ordinary folds bypass FoldOperandWrites.

Only emitReduceCompute establishes `saved || exprMayWriteIn c.BindingValues compExpr`, with restoration in finally. emitReduce reaches the same foldFmfDecorable helper with the default false flag. A concrete route is `reduce((writing_map) <|> (pure_map), (+))`: TypeCheckInfer.fs:2565-2596 does not classify choice as a fused terminal; Lowering.fs:958-978 emits IRReduce; LLVM's choice stays virtual. The writing producer therefore feeds `fadd reassoc nsz` again. The new golden covers only a direct apply/IRReduceCompute, so it misses this route.

The requested fold-KERNEL probe is also relevant here:

```blade
type I = Idx<3>
let mut G = [0.0]
let m = method_for(range<I>) <@> lambda(i) -> { G(0) = G(0) + 1.0; G(0) }
let k = lambda(a: Float64, b: Float64) where comm(a, b) -> {
    let z = reduce(m, (+))
    a + b + z
}
let total = reduce(method_for(range<I>) <@> lambda(i) where omp(i: 1) -> Float64(i), k, 0.0)
```

C++: binding k forces its deferred capture before constructing the wrapper (CodeGenBinding.fs:5534-5543); the outer team reads stored m, so this named-k spelling does NOT establish a C++ write race. Moving the lambda inline bypasses that forcing path and risks an undeclared captured array, not a demonstrated working race. An explicitly parallel inner reduction instead splices its map into the kernel body, where the analyzer can see the write.

LLVM: callable aliases emit nothing (4451-4455); the fold kernel is inlined (5451-5461). m has one syntactic read in k, so ReadOnce (437-459) allows emitReduce to consume its virtual map at 6146-6149. That inner licensed (+) fold executes the writing map while carrying reassociation flags. The outer custom kernel itself is not FMF-decorable; the demonstrated source-level path is the inner fold, not an eight-thread LLVM team. This is an unsafe reassociation permission even if a particular optimizer leaves the loop scalar, just as for 490.

Exact fix: scope operand effects at EVERY entry that consumes a virtual producer, especially emitReduce, using the producer actually consumed and the current local/module binding environment. Pass that environment into callableMayWrite/foldReorderLicensed when kernels can evaluate deferred captures; Map.empty at CodeGenExprSupport.fs:1673 cannot prove those safe. Preserve materialization boundaries. Add ordinary-IRReduce choice and captured-map-in-kernel goldens, with pure controls and cancellation-sensitive pins. The existing G41 golden is a direct writing sum versus pure twin, not 490's cancellation program; it checks absence of `reassoc` and presence of `reassoc nsz`, not the exact `contract` instruction or every fold route.

Additional audit results:

- Span discrepancy is intentional, not a wrong anchor: Expect.fs:531-532 documents 1-based positions AFTER removing `// TEST:`; remaining pin comments count. E.g. 017's physical line 12 is harness line 11.
- g41 runtime cases use OMP_NUM_THREADS only; runOmpReduceTests sets OMP test mode false (OmpTests.fs:1853). They contain no coverage counter/actual-team-size assertion. These are negative serial-admission tests, so requiring a team inside the tested fold would contradict the fix; add a separate positive observed-team control if claiming T>1 runtime infrastructure was certified. Environment requests alone are not that evidence.
- G42 follow-up: CodeGen.fs:476-489 routes a flat-holder alias through genBinding and propagates TupleChildren. 033 covers the four-leaf projection, three-leaf alias, returned nested subtree and genuine nested tuple. No new defect found in this patch by source inspection; builder's execution result was not rerun.

Merge hazards (read-only three-way previews with base 6b3e5293):

- Actual textual conflicts with E: tests/OmpTests.fs, appended runOmpReduceTests block near W2-F:2787 (G41 matrix versus G7 matrix). With A+E: that block, the row_slots_captured_write expectation near E:953, and bench/notes/gap-register.md's tail (G41-G43/G45 versus G44). Keep both matrices and all gap rows; reconcile the changed decline reason.
- Compiler files are changed on both sides but merge textually in this preview. IRStorage.fs: E adds JoinedFoldChunkPlan/JoinedFoldLeg near 130; F adds LoopNestCodeGen.FoldOmpDecline near 303 and initializes it near 1327. These are NOT competing replacements of one field. Preserve F's None default and existing epilogue fields in every constructor; retain E's team plan types.
- CodeGenBinding.fs: preserve F's write-aware licensedFold at 4472-4476 and foldCg decline/flat-tuple registration; combine E's genReduceComputeBindingCore fused-tree dispatch (E:4932-4963) and genReduceJoinCore pack dispatch (E:5230-5264) with them. Joined admission MUST intersect each leg's write-aware foldReorderLicensed with effects of every leaf AND shared producer, including deferred captures. E's joinThreadAudit/IRTotality closure is additional purity/totality evidence, not a replacement for either gate. Check the serial SIMD join admission as well as the team path: lowering's refusal is not a license to omit backend gates.
- Optimize.fs: F inserts writeAnalyzer near 858; E changes SfLeg/mkLeg/decideGroup in shareSiblingFoldsModule (E:3218,3801,4036). No textual conflict, but retain E's exact admission parity between synthesized joins and the emitter. A synthetic join created after lowering must still pass effect admission. Lowering.fs has no E-side change in this range: retain F's refusal and repair its closure; there is no actual E/F textual Lowering conflict.
- EmitLlvm.fs/A: put A's tryOrbitScatter dispatch INSIDE F's emitReduceComputeCore, under the saved/restored effect scope, not before the wrapper. Joined LLVM emission already calls foldFmfDecorable c per leg at F:6601; E adds C++ joined teams, not a separate LLVM team emitter. Keep the flag checks at emitReduce, emitCompactFold (F:6324-6325), emitOrbitFold (F:6425), tryBindCompactRowProducer (F:4136), tryBindFlatEpilogueProducer (F:4384), emitHotRowConsumer (F:4422), and emitFusedCompaction (F:7348); entry paths bypassing emitReduceCompute must establish their own operand facts or carry a checked purity invariant. A's tryOrbitScatter (A:6568-6580) now requires APool, and emitOrbitScatter (A:6592-6596,6821) clears/restores FoldFmf and uses distinct ordered chains, so it does not need to consume a reassociation license. Preserve that stored-pool restriction and strict kernel audit; merely checking FMF cannot justify reordering an effectful producer into scatter order.
- E's tests encode the OLD acceptance policy: loops/489_join_serial_captured_write_declines_team.blade expects successful execution; OmpTests cases g7_fused_join_serial_captured_write_declines, g7_pack_join_writing_shared_producer_declines and g7_pack_join_comm_writing_custom_fold_declines (E:2144,2150,2168) expect emission/decline. Under G43 these must become join-specific BL4005 refusals or use already-materialized inputs for genuine backend-decline controls. Both branches also allocate loops/486-491 with different filenames: no Git collision, but numeric references in prose become ambiguous; renumber consistently.

No claim that the reported old/new numerical results or 94-file rescan was independently reproduced. Fix-range git diff --check is clean; W2-F worktree status is clean.