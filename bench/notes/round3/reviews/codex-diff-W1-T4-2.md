VERDICT: MERGE AFTER FIXES

Read-only review of 9012128c..df90b999, with 66e04875..df90b999 for provenance. References are at df90b999. No builds, tests, emission or executables run; probe outcomes below are source-derived.

1. NOT CLOSED in the literal full-traversal sense (new 8); the reported foreign-demand escapes ARE fixed. Both call sites use compactionRoots: moduleReferencedIds at src/Optimize.fs:3116 and positionsDemand at :3361; Lowering.fs:3092 and :3177 supply the other modules' union. This is one shared root enumerator, not one cached visit per module. RandomInits (including address/modulus), SparseInits values, sparse keys through RootType -> IRSparseKeys(SkRuntime ...), function bodies/captures, provider-write SourceId and the listed embedded types are covered. A sparse key reading y is demanded, not an escape. Provider MaskName is a STORE FIELD name (Lowering.fs:1988), not a reference to an in-memory compound: a load_compound read constructs its own index; ignoring RootNameRef in the foreign ID set does not create that proposed escape.
2. CLOSED for compaction admissions. bindingPayload reaches assignedAnywhere (:3155), payloadWrites/payloadRefs/readsMask (:3161-3172), B4's between check (:3452-3455), fold-host snapshot (:3527), in-pass interval (:3548), producer party (:3614), and T4a between party (:3635); predicate/fold kernels are checked directly and seeds are absent. scheduleMayWriteIn stores bindingPayloadExpr at :1489-1491. A random binding's alias reaches its IRSequence payload through OuterLet/chase (:1138-1164; IRSequence is deferred at :920), so that attempted escape declines. Provider writes explicitly bar T4a (:3636). The hidden x writer keeps legal B4 and declines gather; no remaining compaction wrong-value probe established.
3. CLOSED — default/1/on is T4b; only gather enables T4a (Optimize.fs:128,3136-3137,3652). Both b4 and gather are mirrors (:178); OptDiff.fs:142,180-186 compares each with defaults under REPRO=1/CONTRACT=off. No compound means no compaction plan and no foreign census. The shared resolver change CAN make other schedules more conservative in compound-free programs; identical emission is not promised, though this preserves their source semantics.
4. CLOSED — Optimize.fs:3678 states both x loads, keep store/load and the per-source-cell value store; gap-register.md:104 separates executed 34n+8m / 26n+8m / 26n+8m from the explicitly assumed cache model 30/26/17 and 26/22/13 MB.
5. CLOSED — sharedDecline now applies in every enabled mode, including b4 (Optimize.fs:3485-3501); the overwritten first plan records declined and keeps its previous unfused emission.
6. CLOSED — derivedMap requires nonempty, all-zero input ranks and rank-1 compound output (Optimize.fs:3294-3302); whole-array at0 is uncertified and requires positions. Its separate C++ failure does not invalidate this demand fix.

7. HIGH — the other scheduling intervals still audit placeholders: src/Optimize.fs:2636-2648 (hot-row), :2772-2780 (B1 cell epilogue), :3875-3877 (compact row classes); their module chains explicitly carry b.Value at :2835/:3921. Passing IRUnit to the improved resolver cannot reveal a RandomInits/CompoundInits operand. These are live source-derived wrong-answer candidates, not declines: the producer and consumer remain pure, while an intervening random key mutates the producer. No execution claim is made.

Smallest B1 probe (identity regime; gate on predicts s=13, BLADE_CELL_EPILOGUE=off predicts the correct s=90):
```blade
import rand as r
type I = Idx<2>
function bump(a: mut Array<Float64 like I>) -> Int64 = {
    a(0) = 9.0
    7
}
let x: Array<Float64 like I> = [1.0, 2.0]
let mut p = method_for(x) <@> lambda(v) -> v + 1.0 |> compute
let noise = r.uniform(bump(p), 1)
let s = reduce(method_for(p) <@> lambda(v) -> v * v, (+), 0.0)
```

Hot-row variant: add type J = Idx<2>; change bump's parameter to Array<Float64 like I, J> and assignment to a(0, 0) = 9.0; replace p with method_for(range<I, J>) <@> lambda(i, j) -> 1.0 |> compute and s with reduce(method_for(p) <@> lambda(row) -> prodsum(row, row), (+), 0.0). Predicted hot-row on/off: 4 versus correct 84 (disable CELL_EPILOGUE to isolate hot-row; enabling its row epilogue does not repair the interval).

Compact-row variant: keep x above, change bump's parameter to Array<Float64 like SymIdx<2, I>> and assignment to a(0, 1) = 9.0; replace p with method_for(x, x) <@> lambda(a, b) where comm(a, b) -> a + b |> compute; replace s with reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> p(i, j), lambda(a, b) where omp -> a + b), lambda(a, b) where omp -> a + b, 0.0). In the licensed regime, predicted compact-row on/off: 12 versus correct 24. REPRO declines the licensed compact-row emission and therefore hides this probe.

A CompoundInits variant uses a distinct pre-existing all-true mask and compound(bumped(p), other), with bumped mutating p and returning a fresh dense array (the pattern already pinned by index-types/531). It has the same placeholder hole; do not insert an ordinary binding that reads p and accidentally triggers the existing barrier.

Exact fix: retain the original values for producer/consumer recognition, but resolve every intervening module binding by ID and apply read, repeatability and write checks to its complete bindingPayload (plus relevant provider effects); nested ordinary IRLet values keep their existing checks. Add these hidden-operand variants to the three planner decision/value regressions. Merely replacing the shared initializer map is insufficient.

8. MEDIUM — src/Optimize.fs:3049-3064 is not the claimed complete embedded-index traversal. IRUngroup, IRUngroupRows and IRUngroupGrid carry source index records, but neither this match nor childrenOf visits them (IR.fs:2304-2306). A nested ungroup whose source extent contains a y lookup is consequently invisible at this seam unless another root happens to expose it. No frontend-reachable positions-free lookup established. Exact fix: add those three source-index cases to embeddedExtentExprs and recursively visit embedded types within extracted extent expressions; pin local and foreign census coverage with an IR fixture whose only y reference is in that index extent.

(4) Other planners: bare values, not a repaired payload audit; new 7 constructs the missed hidden writers. Pre-existing, still live after this fix.
(5) D-W14-2: real, separate pre-existing zero-loop-level parameter-binding defect, consistent with the supplied baseline failure: CodeGenLoopNest.fs:1520 initializes an empty parameter map, fills it only while peeling levels (:2064-2095), then renders with it (:2099); a whole-array kernel has no such level. Those sites are unchanged from 66e04875. Add a distinct gap row with the at0 probe and baseline BL9002; fix by binding zero-S-dimension parameters to their whole operands. The exact '__v15' diagnostic was not independently reproduced here.
