# A04 source census: one same-cell return update

Source/design checkpoint at lane A merge `00163192` (source/tests/fsproj identical to integration `5e208a77`); accepted A03 is `f10ca17f`. No production implementation, build, lowering, emission, execution, test or benchmark by this lane. Graft found no planPoolReuse/returnPoolDestination hits in its 68 indexed files; the F# compiler was inspected directly. Full-suite launch count remains zero.

## One proposed missing route

PM03 in peakmem-microkernel-plan.md §5.3 and peakmem-sketches/01-lifetimes-folds-layout.md §PM03 requires stronger evidence than a donor already dead before a consumer. Select only an ordinary capture-free nonrecursive serial function returning a plain static rank-one Float64 array. Its fresh local p is filled once, a whole-array scalar sum completes, and its final consumer maps p cellwise using that completed scalar:

```blade
let p = x * 2.0 + 1.0 |> compute
let old_sum = reduce(p, (+))
p * 0.5 + old_sum
```

The tail genuinely reads the prospective donor. It cannot use A03's independent-input argument. There is no second dead local map pool to let an unrelated PM01 pair masquerade as PM03. The scalar must retain its original value, and the scalar dependency must finish before the final map. The probe requires both returned arrays after a second call and requires caller x/y to remain unchanged. Odd extent 7 and exact binary halves avoid a numeric tolerance argument.

This source is a workload hypothesis pending actual optimized IR. If fusion removes p, a scheduler retains a deferred producer, or a different return shape reaches either backend, stop: it is not evidence for this candidate. No storage or performance win is claimed from source spelling.

## Current source and ownership topology

| Anchor in this checkout | Consequence |
| --- | --- |
| Lowering.fs 3137–3179 | Optimize, IRLift, forceCallableBody, then CSE precede planPoolReuse. Inspect this final tree and tables before another lowering resets their AsyncLocal state. |
| Optimize.fs 557–570 densePoolShape | Existing planner allows scalar storage, positive literal extents and plain rank-one index slots. This is broader than the proposed single rank-one Float64 route; do not inherit its full shape admission. |
| Optimize.fs 571–747 planPoolReuse | Candidate lets are computed applies; eligible return is also a candidate. refsOf follows deferred lets and callable captures. unaliased rejects retained handles, calls and assignments. Every function containing either A01 resource event declines wholesale. |
| Optimize.fs 690–705 deadFrom and donor selection | It tests member reads starting AT the consumer position. Because the tail names p, p is not a dead donor. Relaxing this comparison alone loses the read/write proof. |
| Types.fs 582–601 PoolReuseTable | Donors and ReturnDonors mean existing dead-root reuse, consumed by both backends. They carry no same-cell dependence certificate. MutableArrayLets (IR.fs 1798) records assignable bindings; membership alone is not proof of an actual write or of sole ownership. |
| CodeGen.fs 770–783, 804–826 | Body/return donor records trigger rewritePoolAlias; return suppresses donor free only after allocation declaration matching. This generic path must NEVER receive same-cell records. |
| CodeGenLoopNest.fs 5009–5033 | rewritePoolAlias changes only an allocation declaration to donor.data. It does not change input pointers, loads, loop promises or dependence analysis. |
| EmitLlvm.fs 7716–7821 returnPoolDestination | A03 requires a direct computed local donor, actual completed APool, matching plain static rank-one Float64 storage and one exact ordinary function-scope allocation/tracking record. It explicitly rejects input backed by donor, all lifetime events and let-donor plans. It is entered only for existing ReturnDonors. This is intentionally insufficient for PM03. |
| EmitLlvm.fs 7921–7976 emitFunctionBody | Ordinary function opens one pool scope. Without a donor record it materializes the original body; a fresh returned pool escapes while the known local p is freed. The new candidate is expected to take that baseline path. |
| EmitLlvm.fs 2063–2092, 5549–5553, 5611–5614 | allocPool registers one allocation/root/slot in the innermost scope. Dense fill computes readCell/coerce before storeCell. Ordinary dense materialization allocates a separate output before that fill. |
| EmitLlvm.fs 1319–1359 | Scope KnownIds separates preexisting arrays; popPoolScope(Some root) spares the exact returned root. A donated return must retain p's ONE record; no second registration, new allocation identity or early release. |
| EmitLlvm.fs 4135–4160 and later bindNamed arms | Stored-array rename can deep-copy. HotRowTable, CompactionTable, CompactRowTable and EpilogueLegTable can change whether compute-shaped source becomes a completed backing. A source annotation is not an ownership certificate. |

Conditional baseline for the positive, if the intended tree survives: one actual local p backing and a distinct final-result backing per call in each backend, existing dead-return donor map empty for this function, p released at ordinary function exit, final returned alive. Caller literal buffers, tracking slots and extents metadata are separate. Those are predictions for Luna to confirm, not executed facts.

## C++ alias and dependence obligations

CodeGenLoopNest.fs 3020–3028 creates a const BLADE_RESTRICT pointer per distinct INPUT NAME; 3104–3111 creates a separate writable BLADE_RESTRICT output pointer. Two different names can refer to one donated backing. The flat loop at 3112 onward writes out[k] from its kernel expression; its existing dependence explanation (3046–3065) explicitly relies on a fresh output. The chosen pragma (3080–3084) can be BLADE_IVDEP, BLADE_OMP_SIMD or BLADE_OMP_PARALLEL_FOR_SIMD. Generic nest output/row peeling has similar restricted paths and IVDEP obligations at 1900–1940. Cell epilogue variants reuse the same head/pointers (2778–2817); changing only the ordinary loop would not cover them.

blade_portability.hpp 44–84 expands restrict for GCC/clang/MSVC and IVDEP for GCC only. Comments describe current GCC's weak treatment of local restrict, but that is not permission to emit conflicting restricted access paths for other compilers or future optimization. Disabling OMP threads still allows SIMD, and disabling restrict macros globally would change unrelated kernels. A same-cell loop CAN have no loop-carried dependence when every old load is cell i and its store is cell i, but it requires its own proof and one legitimate shared read/write pointer path. No shifted, gathered or captured reads can share that assertion.

Therefore the first implementation option is LLVM-only. C++ keeps its ordinary fresh destination and receives no same-cell ReturnDonors record. A later C++ consumer would need a dedicated emission path or proof-aware pointer binding that maps the scalar old cell and the destination through one writable root, with each SIMD/thread promise independently discharged. Do not admit that wider work through this census.

## LLVM facts and proposed certificate boundary

LLVM's only emitted noalias is the fresh allocation return (EmitLlvm.fs 1222–1249). Array parameters (7870–7888) may be readonly but are never noalias; array returns (7889–7893) are not noalias. Current counted loops/emitNest (1629–1666) have no parallel access, alias.scope, access.group or loop metadata disjointness claims; targeted searches found no such annotations. readCell(APool p) is a GEP/load from p, and fillDensePool evaluates its scalar before its GEP/store. A same-cell result may thus use the SAME allocation SSA root for both accesses without an invented disjoint pair. Allocator noalias remains truthful for p's original fresh allocation; donation neither frees/reallocates it nor claims a second fresh root. Formal readonly remains truthful because caller storage is never a destination.

Pending topology evidence and root approval, prefer one separate explicit same-cell RETURN certificate plus backend admission decision, distinct from generic PoolReuseTable.ReturnDonors. It identifies function, direct local donor ID, exact tail apply/kernel and read pattern, and static type/extent. It must be reset per lowering and fail closed on mismatch. Shared recognition must NOT authorize C++'s existing declaration rewrite. No general let, tuple, applyToArr or last-use release change is proposed.

For the first bounded proof, require all of the following together:

1. Direct straight-line outer IRLet spine and computed scalar-map return; exact single named array operand is p. One static plain rank-one Float64 type with positive literal extent, same logical index identity, no units/virtual/temporal/layout conversion, triangular/peel/tdim/Reynolds/parallel strategy. Direct fresh computed p definition, not a call result, parameter, alias, view, row or conditional result.
2. Whole body has no assignment, mutation, branch/loop, tuple result, array alias let, capture/escape/call involving p, unknown call or lifetime event. Every p occurrence is accounted for by the direct producer definition, completed allowed scalar observation or final same-cell input. Follow callable captures, deferred payloads and embedded dependencies, rather than counting only direct IRVar occurrences. Literal closed shapes remove runtime extent references. Do not infer sole ownership from names, reference count, MutableArrayLets or separate SSA registers alone.
3. Tail callable has one scalar Float64 parameter and only Float64 literals, that parameter, completed scalar snapshots and closed arithmetic (initially +, -, * is enough). No indexing, gather, halo, reshape, array-valued capture, nested apply/fold/call, unknown or may-fail kernel, effect, mutation or deferred computation. Preserve original per-cell scalar DAG/operands/flags; bind old before writing. Earlier sum association and evaluation site are unchanged. A scalar snapshot carries values, not delayed array reads.
4. Reject any overlapping plan owner on donor/tail/observations: generic reuse members or return donor, hot-row/cell-fold/epilogue-leg, compaction/compact-row/other specialized scheduler. Do not overwrite another table or force a pending AVirt during speculative admission. Primary probe disables competing schedulers so the exact unowned route is visible; a default-schedule probe records any real owner rather than assuming no conflict.
5. At LLVM realization, p is a completed matching APool, allocated as exactly one root/slot in the exact current ordinary function scope; it is absent from KnownIds, has not escaped, been nulled/released or joined another owner, and is not a formal/global/loaded unknown pointer or subview GEP. Source proof and actual storage proof must agree. Preflight emits nothing and falls back without extra evaluation. After normal producer preparation once, use the same p root, one old-cell load before each same-index store, one retained ownership record and the existing ordinary array-return escape path. Any contradiction is a truthful decline, not an unsafe optimization.

This is a proposed certificate contract, not a new implemented analysis or complete proof for arbitrary aliases. Direct syntax plus exact realization is intentionally restrictive. The remaining contexts (shifted/gather/stencil, tuple/view/capture/escape/assignment/caller-owned, dynamic shapes, unknown/may-fail kernels, lifetime regions or another planner owner) retain original allocation. They need independent refusal witnesses if a follow-on implementation is approved.

## Tiny fixed controls and disposition

Owned files are this census, A04-runner-request.md, three A04-census-inputs/*.blade and A04-content-sha256.json. No production/test harness/fsproj edits. These are diagnostic inputs outside corpus discovery, not registered passing tests. Values below were derived analytically without running Blade.

| Input | Intended observation |
| --- | --- |
| 01_same_cell_return | p is 1,3,…,13 (sum 49); tail 49.5,…,55.5 (sum 367.5). Second input gives p 5,9,…,29 (sum 119), tail 121.5,123.5,…,133.5 (sum 892.5). Both inputs and both results remain required after the second call. |
| 02_caller_owned | Same-cell formal x has no fresh local donor. Two calls preserve x/y and produce 21,…,24 by halves (sum 157.5) and 57,…,63 (sum 420). No write to formal storage is licensed. |
| 03_retained_name | held plus updated plus old_sum escape in a tuple; old cells must remain 1,3,…,13, sum 49, alongside updated sum 367.5. Existing copy-on-alias may physically copy held, so this is a conservative retained-name/tuple refusal, NOT proof that two names share one physical root. It must not be used to solve tuple cleanup or alias-copy retention. |

Existing A02/031 supplies a real event-bearing refusal and exact release boundary; memfree022 supplies the view control. A03/030/032 remain independent-input dead-donor positives, not PM03 positives. Additional cross-cell/unknown-kernel synthetic negatives belong to an approved implementation gate after a real certificate exists.

The bounded request stages IR/plan and raw C++/LLVM evidence first and stops on changed shape. Root decides whether a later LLVM-only return certificate is worth implementing. If the candidate is already optimized by another route, record that route and do not add a duplicate pass. No new memory metric, capacity/RSS/retention claim, sanitizer support or benchmark follows from this checkpoint. Stop for root review.
