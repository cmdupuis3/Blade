VERDICT: DO NOT MERGE
12. NOT CLOSED — direct/nested array views and object_for applications are followed, but an inline view inside a named tuple is filtered out of the chase (19).
13. CLOSED — EmitLlvm.fs:6441-6448 checks FoldOperandWrites and exprMayWriteIn before emitArr; emitOrbitFold has exactly one caller, through this guard. Stored cells must not rerun their producer: 8 / 3 is correct for the materialized writer (1 + 2 + 2 + 3), consistent with shared-value semantics; 10 / 4 applies to the unforced writer's logical walk.
14. NOT CLOSED — the reported direct writing-producer witness is fixed, but hot-row scheduling still moves row reads across the outer fold's writes (17).
15. NOT CLOSED — all three BLADE_IVDEP producer sites now have the write veto, but their OpenMP SIMD alternatives bypass it; BL4005 does not close the alias case (18).

Reviewed 62b87c31..bd34627b in bench-fortran, HEAD bd34627b. Files/git only; NO builds, emits, tests or probe executions. Counterexamples and values below are source-derived, not reproduced. Reported green totals and BL9002 are builder evidence. Findings 16-19 are remaining same-class holes, not claims that this commit introduced them.

16. BLOCKER — src/Optimize.fs:2766-2787; src/CodeGenCuda.fs:2239-2265; src/EmitLlvm.fs:4129-4179. Compact-row classes still move scalar capture reads across producer writes. The planner checks the consumer and intervening bindings, never the producer. pureScalarBody accepts free scalar IRVars (OrbitFold.fs:249-252); bodyDominated proves availability, not invariance. Probe (default licensed build, compact-row classes on/off):

```blade
type I = Idx<2>
let x = method_for(range<I>) <@> lambda(i) -> Float64(i) |> compute
let mut G = 0.0
let p = method_for(x, x) <@> lambda(a: Float64, b: Float64) where comm(a, b) -> {
    G = G + 1.0
    0.0
} |> compute
let total = reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> p(i, j) + G, (+), 0.0), (+), 0.0)
let seen = G
```

The stored producer has three canonical cells. Two-pass: total 12, seen 3. Row partials: first packed row reads G=2 (weight 1+2), last reads G=3 (weight 1): total 9. Exact fix: reject compact-row scheduling when the producer's transitive evaluation may write, unless a read/write-disjointness proof covers the class body; retain the post-materialization orbit fold. Add value pins with scheduling on/off in both lanes and a pure positive control.

17. BLOCKER — src/Optimize.fs:2096-2113,2157; src/EmitLlvm.fs:4324,4422-4430; src/CodeGenBinding.fs:332-339. A pure producer does not make the row map movable ahead of the OUTER fold kernel/seed. No MayWrite check covers fk or seed. Probe:

```blade
type I = Idx<2>
type J = Idx<3>
let mut G = [0.0]
function k(a: Float64, b: Float64) -> Float64 = {
    G(0) = G(0) + 1.0
    a + b
}
let p = method_for(range<I>, range<J>) <@> lambda(i, j) -> Float64(i + j) |> compute
let total = reduce(method_for(p) <@> lambda(row: Array<Float64 like J>) -> G(0), k, 0.0)
```

Ordinary fused row-map/fold reads 0 then 1: total 1, G=[2]. Hot-row precomputes both row values as 0 before either k call: total 0, G=[2], including repro. Disabling reassociation does not repair this. Exact fix: decline RowFold when the seed or outer combine may write (or prove their writes disjoint from row-map reads), using the shared transitive/provenance analysis; pin values with hot-row on/off and repro. Direct row-map writers/reverse producer-read interference are already excluded by Repeatable. Unknown/unresolved callees are pessimistic in cseFactsOf, but it does not chase named deferred array initializers; do not equate it with writeAnalyzer.

18. HIGH — src/CodeGenLoopNest.fs:2578,2606,2825-2830. allParallel wins before producerWrites, emitting BLADE_OMP_SIMD / BLADE_OMP_PARALLEL_FOR_SIMD even when the new predicate says the body writes. The frontend's check is lexical (TypeCheckSupport.fs:8005-8010), not storage-provenance-aware; a local row view evades it:

```blade
type K = Idx<2>
let mut G = method_for(range<K>, range<K>) <@> lambda(i, j) -> 0.0 |> compute
let x = [1.0, 2.0, 3.0, 4.0, 5.0]
let p = method_for(x) <@> lambda(v) where omp -> {
    let t = G((0 : K))
    t((0 : K)) = t((0 : K)) + v
    t((0 : K))
} |> compute
```

`t` is local, but its storage is G's. The new analyzer detects the write; the allParallel arm ignores the answer. Threads-off still asserts SIMD independence; threads-on races. Exact fix: make the transitive write veto dominate every producer SIMD/team route, including the epilogue's FlatLanesChunks admission and generic fallback, or reject this aliasing parallel kernel with that same analysis before emission. Add alias-writer negatives for both thread settings and the epilogue, retaining fresh-scratch positives.

19. HIGH — src/Optimize.fs:1022-1027. The tuple chase's live filter omits IRIndex/IRSlice/IRShift/IRReverse/IRCurry even though the new sibling arm now recognizes them. Using 025's writing m, replace its row declaration with:

```blade
let pair = (m((0 : I)), 0.0)
let row = pair[0]
let r = object_for(<&!>) <@> (reduce(row, (+)), reduce(row, (+)))
```

The chase reaches IRTuple, drops both components, and reports no deferred writer. LLVM emitTupleParts (6987) retains the virtual row via classifyValue/rowView; it does not materialize it. Thus the join refusal and fold operand fact still lose the writer. Exact fix: recursively chase tuple components using the same view predicate (prefer projecting the selected component), instead of the separate syntactic whitelist; add a BL4005 tuple-view join refusal and LLVM fold flags control.

Audit details:
- (a,d) exprMayWriteIn and callableMayWrite share Optimize.writeAnalyzer; codegen resolves through BindingValues then programLetValuesCell. Frontend uses the same walker with different resolution/CopiedLet facts. Alias cycles terminate via seen/walkedLets; view-of-view chains descend correctly. Explicit compute remains a boundary. CopiedLet is not consulted by the deferred chase: a stored copy stops at its stored source; an LLVM virtual rename remains virtual (copyArr:4436-4438), so it must not be assumed forced merely because it says let mut. Ordinary array-returning calls produce APool (7056-7061); tuple-returning calls inline packs (7014-7041), so the blanket claim that every call is a stored boundary is too broad.
- (c) Annotation census: BLADE_IVDEP at CodeGenLoopNest:1995,2578,2830 is write-gated; CodeGenExpr:3764 is intrinsic matmul over stored inputs/fresh output. blade_portability.hpp:81 merely expands it to GCC ivdep; no separate GCC_IVDEP emitter. Producer SIMD at LoopNest:2578,2827-2828 is NOT write-gated (18). Reduction SIMD sites: ExprSupport:1945 via Expr:503 (stored/forced prodsum), Binding:3619/3633 (stored fold + fold licence), Binding:4895 (licensedFold includes map writes); Binding:3948 (orbit recognizer + stored source/pure scalar body); Fusion:377/380 (joined admission/effect audit); LoopNest:172/178/182 and 2014 (licensed domain/chunk fold plans). No llvm.loop.vectorize, parallel_loop_access or parallel_accesses emission exists in this tree; EmitLlvm:1600-1620 emits plain loop branches.
- (5) Scatter placement is unchanged: LLVM dispatch at 6876 remains inside the 6864-6870 operand-write wrapper and accepts APool only (6611); C++ dispatch remains in genReduceComputeBindingCore, rejecting deferred source/free bindings at 4238-4247. It does not reject all free captures, only unavailable/deferred ones.
- (6) G41 now scopes its coverage and drops the every-entry claim; its assertion that views and holes 12-15 are closed still overstates the above.
- (f) Writer ivdep tests ban ONLY the literal BLADE_IVDEP, not bare ivdep/GCC ivdep or producer SIMD; hasOmpPin does not normalize ivdep (TestHarness.fs:206-229). Epilogue/nest negatives have no required-path marker. accumulatorFadds checks load/store slot membership and exact flags, not values or full accumulator dataflow, and does not partition functions. G41-13 checks comments; its materialized control checks only nonempty emission. Hot-row decision tests pin only decline reasons; loops/499 separately pins total=12.0 and G=[6.0]. Strengthen spelling/path negatives and add runtime pins when gates can be run.

(e) YES for some pure programs: the new hot-row gate rejects locally mutating scratch/scalar bodies or unresolved pure callees because cseFactsOf is conservative; no new predicate decline is indicated for the bench lcg/prodsum/matern52 kernels (named callees resolve, arithmetic/casts/prodsum/sqrt/exp carry no observable writes), while codegen's write analyzer can also conservatively decline unresolved calls/copy provenance.
