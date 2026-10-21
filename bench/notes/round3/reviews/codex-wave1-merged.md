VERDICT: FIX BEFORE A/B RESULTS ARE TRUSTED

Reviewed 66e04875..9da403a8 against item tips 2e6f5ea5, 88b9b0c5, cab20d31 and 82cc5e99. Files/git only; no builds, tests, emission, programs or timings run. Probe outcomes below are source-derived. Findings are record defects; no lost implementation hunk, duplicate-counted test or cross-item wrong value established.

1. **HIGH ? the merge renumber corrupted G51's FIXED evidence.** `bench/notes/gap-register.md:101` now says **`corpus loops/537-536`**; cab20d31 correctly says `loops/528-536`. A2's nine files remain 528?536; only W1-4's three files moved to 537?539. **Exact fix:** change this one G51 citation back to `loops/528-536`. Keep G63's `loops/537-539`, `CliSelfTests.fs:861` and `OptimizeTests.fs:1589` unchanged. All those files exist.

2. **HIGH ? stale merged records.** G58 (`bench/notes/gap-register.md:108`) attributes its refusal boundary to `unforcedTupleArrayMember`, which no longer exists after W1-3's fix round. **Exact fix:** replace that sentence with ?`explicitComputeTupleMember` and `bareDeferredTupleMember` accept array-typed leaves only; `tupleNeedsMemberOrder` does not descend into a named deferred tuple.? At `bench/RESULTS.md:2690`, replace ?the shared contract is a gap row at merge? with ?the shared contract remains OPEN as G61?; make the same G61 replacement at `gap-register.md:198`. The substantive 15.4 closure is correctly limited to explicit computes; `tuples/041` remains the BL7004 refusal, not a closed bare-member contract.

(a) **Cross-item probes.** These are review text only, not executed or added to the corpus.

A2 ? W1-4, smallest nontrivial row-view base followed by a hidden writer:

```blade
import rand as r
type I = Idx<2>
type J = Idx<2>
function bump(a: mut Array<Float64 like I, J>) -> Int64 = {
    a(0, 0) = 9.0
    7
}
let mut u = method_for(range<I, J>) <@> lambda(i, j) -> 1.0 |> compute
let noise = r.uniform(bump(u), 1)
let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(u(i), (+)), lambda(a, b) where omp -> a + b, 0.0)
let q = reduce(method_for(u) <@> lambda(row) -> prodsum(row, row), (+), 0.0)
```

With REPRO=1, CONTRACT=off and TILE_TUNE=rowslots_min=0, predicted `s = 12.0`, `q = 84.0`. A2 emits row slots **at s**, after noise has written u; it neither moves u's producer nor crosses noise. Its ?intervening = none? record is accurate (`CodeGenBinding.fs:74,134,4818`). The separate hot-row candidate q cannot move to u: `intervalPayloadOf` exposes noise's random-key call, and the interval refuses it (`Optimize.fs:1478,2666`). The licensed row helper likewise reads the post-write base. Neither path resurrects 4 instead of 84.

W1-3 ? W1-4, a compound held beside an explicitly computed derived member, then projected and keyed:

```blade
let x = [1.0, 2.0]
let keep = mask(x, lambda(v) -> v > 1.5)
let y = compound(x, keep)
let g = method_for(y) <@> lambda(v) -> v + 10.0
let held = (g |> compute, y |> compute)
let a = held[0]
let b = held[1]
let ga = a(1)
let yb = b(1)
```

Predicted `ga = 12.0`, `yb = 2.0` under default, b4 and gather. `positionsDemand` tracks g and rejects its uncertified tuple/IRCompute use, as well as y's; positions are **required**, never elided (`Optimize.fs:3329-3439`). The member-ordered emitter computes g before constructing held and carries its context forward (`CodeGenBinding.fs:815`). Replacing held with `(y, 0.0)` also demands positions immediately; projection need not be followed to establish demand. Replacing y's initializer with `compound(method_for(x) <@> lambda(v) -> v * 2.0, keep)` predicts 14.0/4.0: W1-3 forces the inline operand, while the named-source compaction admission declines. Literal `compound(...)` inside the tuple is outside the supported surface and is refused BL6002 (`Lowering.fs:909`). A bare deferred g member remains refused, per G61. No positions-free tuple escape found.

(b) **Shared text.** Actual function bodies differing from every touching item's tip: **`CodeGenBinding.genCompoundInitBinding` and `EmitLlvm.emitCompoundInit` only**. The first combines W1-3 operand materialization with W1-4 compaction; the second combines dense-before-mask order with W1-4's buffers. `materializeMaskFormCounted` itself equals W1-4; the apparent tail difference is W1-3's following `setOpOperandWalk` declaration/documentation. `Optimize.fs` and `CodeGenExprSupport.fs` equal 82cc5e99; the A2 row-slot/reduce bodies equal cab20d31; tuple helpers equal 88b9b0c5. All changed compiler/runtime files equal the union of the item edits, with no lost/duplicated tail. CLI/OptimizeTests integration differences are the intended reference renumbering. W1-5 workload/oracle/pin additions survive.

(c) **Collisions/counts.** No new numeric-prefix or corpus TEST-name collision; no duplicate new OmpTests/OptimizeTests case label or duplicate invocation in their result lists. OmpTests' repeated local label `path_b_reduce_over_computation` belongs to different prefixed test families, not one counted case. Corpus discovery collects each file once; opt-diff's OFF, b4 and gather comparisons are different comparisons with distinct artifact stems. Remaining duplicate prefixes are all baseline: `ad/007,008`; `functions/023,086`; `index-types/238`; `intrinsics/008-011`; `loops/214`; `multifile/011`; `mutability-errors/042`; `reynolds/011-015`; `symmetry/132`; `unit-errors/019-021`; `units/048,077`. They do not create duplicate-counted tests. No merge renumber is needed for these unrelated pairs.

(d) **Register.** G54?G64 each occur exactly once; no duplicate G-number anywhere. Newly cited corpus ranges exist: A2 loops/528?536; W1-3 sql-masks/014?018, sql-set-ops/012?013, sql-unique-contains/009?010, tuples/034?043; W1-4 index-types/522?531, multifile/037?040, loops/537?539, symmetry/137. G51's inverted range is finding 1. RESULTS 15.4 and G61 agree on the open bare-member behavior; their future-tense reference is finding 2.

(e) **Knobs.** All three have CLAUDE rows, plan evidence and suite clearing (`CliSelfTests.fs:83-96`). FOLD_LANES' row-view arm records K/seed/form (`CodeGenBinding.fs:5152`); its 0/4 mirrors are covered by the OmpTests cases, not optimizerGateMirrors. COMPACTION_FUSION is in optimizerGates and both `b4`/`gather` are in optimizerGateMirrors (`Optimize.fs:167,178`); OptDiff iterates those mirrors against defaults (`OptDiff.fs:180-190`), reached by both CLI opt-diff dispatches. TILE_TUNE's sel_cap is recorded by the selection admission (`Optimize.fs:3711-3715`) and cleared as part of TILE_TUNE; explicit cap/fallback cases are in OptimizeTests and CLI smoke. **Scope:** opt-diff pins repro, so it does not validate the licensed FOLD_LANES arm; it exercises gather at the default cap, not an independent sel_cap mirror. No missing applicable registration found.

(f) **Emission:** the three exclusions alone do NOT guarantee unchanged emission (W1-3 also changes pure dense tuples of computes and sparse set operands; A2 changes OMP test instrumentation); none of those new shapes occurs in the ten other existing kernel sources in this tree (14 total including the two new kernels). For those sources, no changed emission predicate was identified. `scheduleMayWriteIn` now resolves `bindingPayloadExpr` and interval checks use `intervalPayloadOf` plus provider effects; absent executable side tables/provider effects they receive exactly the old values. This is a source audit, not a byte-identity measurement.
