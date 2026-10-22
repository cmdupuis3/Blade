# A05 PM02: different-extent last-use census

Source-only checkpoint at lane HEAD `0f3c5cdb2e7b35afc54bc5603cf12f6ec4849320`, after root merged integration `09ea6e10`. A04 is accepted/integrated at `38256bb1`; its artifacts and `.r4tmp` remain unchanged. Graft returned no `planPoolReuse` hits in 68 indexed files, so the F# anchors below were read directly. No coder build, lowering, emission, compilation, execution, test, sanitizer, benchmark or Git mutation. Full-suite launches remain **0**.

This authors the source-census bundle authorized by root, not an implementation. PM02 remains open until actual default-schedule allocation/free evidence exists. A03 dead equal-shape donation, A04 LLVM same-cell donation, A01 retained-inline cleanup and C++ PM03 parity retain their separate scope. The wider gaps in integration's `coverage-next-A.md` are unchanged.

## Fixed task and analytic controls

`A05-census-inputs/01_different_extent.blade` is one ordinary serial function with independent stored formals x (7 cells) and y (11 cells):

```blade
let p = x * 2.0 + 1.0 |> compute
let old_sum = reduce(p, (+))
let middle = p(3)
let bias = old_sum + middle
y * 0.5 + bias
```

All array elements are unitless Float64, both named axes have positive odd static extents, and the result uses y plus scalar snapshots only. The direct cell observation is part of the fixed workload, not an opportunistic fallback after emission. It is intended to distinguish a stored p from a fold-only map that fusion might remove. Its bounds must be proved in the actual IR; the source spelling alone is insufficient. Do not disable fusion/CSE or a profitable scheduler to manufacture a retained pool.

The first call uses x0 = 0..6, giving p = 1,3,...,13, old_sum = 49, middle = 7 and bias = 56. Its stored y0 = 0,2,...,20 yields a = 56..66, sum 671. The second uses x1 = 2,4,...,14, giving p = 5,9,...,29, old_sum = 119, middle = 17 and bias = 136. Stored y1 = 2,4,...,22 yields b = 137..147, sum 1562. All four original arrays and both full result arrays remain top-level print demands, and explicit sums of all six occur after the second call. No print narrowing is permitted. These EXPECT comments are **analytic, unexecuted specifications**, not accepted pins. Local snapshots stay local to preserve the ordinary array-return ABI; their actual scalar evaluation/dataflow must be inspected, not inferred solely from output sums.

Two bounded exclusions accompany the positive:

| Input | Required census distinction |
| --- | --- |
| `02_caller_owned` | Same numeric snapshots/final results, but p is a formal containing the would-be producer values. No local ownership, no release of either caller pointer. Two calls and later caller/result demands remain. |
| `03_later_read` | Fresh p, but `p(6)` occurs after result materialization and escapes as a scalar tuple member. A release before the result is illegal for this task; tuple-return context is outside the positive contract. Record actual scheduling/tuple expansion and any copies. This does not establish that a future later boundary is impossible or authorize tuple lifetime work. |

No corpus/harness registration or fsproj change is needed: these diagnostic inputs live outside corpus discovery. View/capture/type/effect exclusions below are proof obligations, not claimed executed negatives. Existing A01 event controls and memfree view controls remain useful references; no duplicate broad matrix is requested now.

## Static source topology and the missing proof

| Current anchor | Static conclusion, pending emitted evidence |
| --- | --- |
| `Lowering.fs:490`, `3139–3182` | Callable forcing, ordinary optimizer, lifted forms, second forcing, CSE and then planPoolReuse determine the final tree. Default scheduler tables are populated afterward. Dump this final tree and its live tables before another lower resets them. |
| `Optimize.fs:571–747`, especially `690–705` | Existing pool reuse requires a donor dead at/after its consumer and identical element/extent lists. A 7-cell p cannot be the 11-cell destination's generic donor. Its logical last-read/peak calculation does not schedule a free. Confirm the actual complete Donors/ReturnDonors tables rather than relying on an expected absence of a decision. |
| `EmitLlvm.fs:7826–7980`, `8113–8151` | A04 is same-shape, final-input donation with its own certificate; this independent different-extent tail must not qualify. A03 is only entered through an existing return donor. Neither authorizes ordinary last-use release. Record actual backend decisions. |
| `CodeGen.fs:769–825` | Ordinary computed return emits `stmts`, then result allocation/fill `combCode`, then `popAllocScopeFrees`, then return. Existing return-donor rewrite is conditional. If p is a distinct tracked nonescaping root, this path predicts its cleanup follows result fill. |
| `CodeGenLoopNest.fs:5142–5211` | Scope frees operate on tracked allocation records. `freeAllocNow` suppresses a tracked name's later cleanup; missing/escaping ownership is distinct. `releaseInlinePools` is currently the A01 rank-one named-pool renderer, with missing/unowned bindings rejected. A wrapper name is not a backing ownership proof. |
| `EmitLlvm.fs:1319–1359`, `8098–8188` | Ordinary functions open one tracking scope. The entire original body is materialized before `popPoolScope(Some result)` frees its remaining records and emits return. That predicts p's exact tracking-slot free follows final fill only if result/root provenance is known. Unknown provenance can instead drop records: record that as a prerequisite, not a normal early-release opportunity. |
| `IR.fs:2665–2669`, `IRValidate.fs:351–475` | Resource events are explicit motion barriers, separate from writes. Validation follows storage aliases, captures and embedded type/extent references; scalar values are snapshots only when their type/metadata hold no storage dependency. Reusing just a direct IRVar scan is insufficient. |
| `EmitLlvm.fs:2738–2753` | A01 release validates named pools against the innermost inline scope, then pops **the whole scope**, not just the names in its list. Inserting a release into the ordinary function scope would be unsound. |

The positive's source-level hypothesis is two local payload backing sites per call: p's 7 doubles and the independently returned 11 doubles. There are two calls, so body sites and dynamic instances must be reported separately. Caller literal backing, extents and scalar/tracking allocas must not be counted as map payload buffers. If the predicted overlap exists, p contributes 56 payload bytes while the 88-byte result is built; this is only arithmetic about candidate payloads, not a measured peak, allocator retention or whole-process memory claim.

The probe deliberately clears **all** `Optimize.optimizerGates` to their actual defaults and sets only the paired reuse gate. Fusion, CSE, hot-row, cell epilogue, epilogue legs and compaction remain default-enabled wherever that is the compiler default. Strict numeric settings are fixed independently (FP_REPRO=1, FP_CONTRACT=off, BLAS=off, serial threads). Thus no isolated schedule is being passed off as production evidence. Lowering/validation and each backend's decisions are saved separately, and both emissions consume the same final tree/live table state, C++ first. Default scheduler ownership flags are an inventory, not a legality certificate or a complete hidden-dependency analysis.

## Required actual evidence and stop conditions

First inspect the positive under **default scheduling and reuse=1**, with lower/validate only. There must be one ordinary capture-free array-return callable, one direct fresh computed p over Short, completed Float64 old_sum/middle/bias, and a final computed map over already-stored formal y over Long. Trace the exact p ID through every observation and every callable capture/type payload; the final kernel may capture only completed scalar snapshots. Actual metadata must preserve both axis provenances and exclude units, virtual/layout changes, hidden extent references, mutation, events and specialized planner ownership.

If p is erased/deferred, the source is fully fused, a scheduler owns its storage, the call expands into another ABI, or current emission already frees p before the final allocation: stop and return evidence. Classify an existing route/no-gain or precise prerequisite. Do not rewrite the fixture or turn off useful passes in this dispatch. A later isolated or fusion-off control requires root authorization and cannot establish a production gain.

If that shape survives, paired reuse=1/0 raw C++/LLVM emissions must trace:

1. x/y formal identities and caller call sites, plus p's allocation size, actual backing root and one ownership record. A fresh expression or distinct name alone proves none of these.
2. Every p payload read, including fold load(s), the fixed cell observation and any hidden/deferred/captured use. Establish the **last actual read**, not just the final syntactic reference, and show scalar values complete before final allocation.
3. The final result's distinct 11-cell allocation, input y root, exact scalar arithmetic operands/flags, fill, returned root, and caller demands after call two. No reassociation, speculative failure movement or dropped required result is allowed.
4. C++'s allocation-specific p deallocation and suppression/escape disposition; LLVM's p allocation SSA root -> exact tracking slot store -> slot load -> `blade_free` chain. Locate each relative to final allocation/fill/return. A global free count, a free of unrelated storage, a null-slot free or a logical peak estimate is insufficient.
5. The result escapes its function once, p is reclaimed once, and no formal/returned root is freed inside either call. Record current baseline limitations honestly if tuple control expands inline or retains unrelated roots.

Only raw evidence that p is retained until after result allocation/fill establishes the proposed missing route. No runnable workload, sanitizer or broader category is part of this census request. The exact staged commands and stop policy are in `A05-census-inputs/runner-request.md`.

## Conditional release design for root review

If evidence matches, the candidate boundary is after the original `bias` snapshot and before evaluating/materializing the final map. All p reads finish before that point; y and scalar snapshots remain available afterward. Do not alter snapshot association (`old_sum + middle`), producer arithmetic, per-cell `y * 0.5 + bias`, observation order or the original potential-failure sites. Static nonempty extent and fixed in-bounds cell reads are required; effect summaries alone cannot license moving an arbitrary call/kernel/validation across cleanup. Resource exhaustion timing is a separate resource-policy issue, not evidence that a numerical failure can move.

A possible expression using existing first-class events would bracket **only** p's producer and scalar observation prefix in a nested exact ownership scope, and close it before the final map. This is a hypothesis, not authorization to insert events: A01's LLVM release pops every allocation tracked in its nested scope, so all prefix allocations (including any reduction/helper storage) must be inventoried first. Any unrelated/escaping root, unknown tracking record, caller-owned record or other owner requires refusal or a separate design. C++ must emit the same one backing release and suppress its eventual scope cleanup. Preserve current event validation and motion barriers through every pass/CSE; do not add a fragile cleanup side table or pretend resource events are language writes.

The first implementation proposal, if requested, should reject tuple/view/alias storage, captures retaining arrays, deferred readers, index/type/extent metadata containing p, assignments/mutation, array-returning/unknown/may-fail calls, provider/external effects, branches/recursion/parallel/foreign kernels, existing lifetime regions, and all conflicting donor/scheduler owners. Full capture/type payload and alias-root closure plus exact emitted root/record scope are required; a scalar-typed syntactic occurrence does not prove evaluation has completed. Caller-owned and already-released storage cannot become owned by this transformation. No runtime-shaped release, general last-use policy, C++ same-cell reuse or tuple-call cleanup is proposed.

## Frozen ownership and handoff

Only this note and `A05-census-inputs/{01_different_extent.blade,02_caller_owned.blade,03_later_read.blade,probe.fsx,runner-request.md,manifest.json}` are authored. The manifest pins every nonmanifest bundle file, the lane HEAD and relevant source anchors. The probe is unexecuted source; Luna must use a root-approved matching binary identity, stop on any probe inference/API error, and return the exact diagnostic for repair rather than editing the experiment or using a stale DLL. No passing runtime pins, PM02 acceptance or measured benefit is claimed. Stop for root review.
