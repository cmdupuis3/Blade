# Round-3 wave-3 Codex reviews (condensed)

The independent Codex diff review of each wave-3 item (W3-1 three looks, W3-2 two looks, W3-4 one look; W3-3 was a measured prototype, no diff review), the salvage assessment of W3-3's P1 results, and the G69 design assessment (the lifetime class of A7c's kept inline). The wave-2 reviews are in `wave2-codex-reviews.md`.

Sections (each is one former scratchpad file, verbatim): [`wave3-codex-reviews.md#codex-diff-w3-4`](#codex-diff-w3-4), [`wave3-codex-reviews.md#codex-diff-w3-1`](#codex-diff-w3-1), [`wave3-codex-reviews.md#codex-diff-w3-1-2`](#codex-diff-w3-1-2), [`wave3-codex-reviews.md#codex-diff-w3-1-3`](#codex-diff-w3-1-3), [`wave3-codex-reviews.md#codex-diff-w3-2`](#codex-diff-w3-2), [`wave3-codex-reviews.md#codex-diff-w3-2-2`](#codex-diff-w3-2-2), [`wave3-codex-reviews.md#codex-w3-3-salvage`](#codex-w3-3-salvage), [`wave3-codex-reviews.md#codex-g69-design`](#codex-g69-design)

---

## codex-diff-W3-4

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-diff-w3-4` (appended at the wave-3 records commit)_

VERDICT: MERGE AFTER FIXES

Reviewed `git diff 361504b6..719a2bce`; source locations below are at 719a2bce. Files/git only; no builds, tests, compiler emission or timings executed. Reported test footers and timings are builder evidence, not independently reproduced.

1. HIGH — The published threaded-route contract is false for LLVM. `docs/features.md:531`, `bench/kernels/pairaction/spec.md:113`, `kernel.json:26`, G73 and the proposed CLAUDE.md row describe a thread request as declining the action and mt8 as retaining the row walk. That is C++ only (`CodeGenBinding.fs:4910`). LLVM explicitly takes the action despite the request (`EmitLlvm.fs:7471`), and has no team. Its accepted element/accumulator/weight types are also narrower than the unqualified “both lanes” presentation: Float64/Int64, whereas C++ admits Float32/Int32 too. Fix all these records to state the lane split: C++ threads-enabled + any map/inner-map/fold request => old emitter; LLVM => serial action when its own admission succeeds, otherwise its own fallback. Qualify the tested raw-bit evidence as Float64, REPRO, contract off. Add the corrected CLAUDE.md row in the integration commit; its absence from this builder commit is acceptable only with that merge-time addition. In that row, replace “more than one channel ... declines, each naming the first failed condition” with “multi-channel is not recognized”: tuple kernel/seed returns `ActionNotAction` (`OrbitFold.fs:1456`), without that promised refusal record.

2. MEDIUM — The signed-zero witness does not cover seedless folds. `tests/OrbitScatterTests.fs:763` explicitly supplies `-0.0`; every generated a/b pair supplies a seed. The action drivers always initialize from a seed (`blade_orbit_scatter.hpp:276`, `EmitLlvm.fs:7546`), never peel the first term. The checker already synthesizes +0.0 for seedless deferred `(+)` (`TypeCheckInfer.fs:2841-2860`), and `Lowering.fs:974-982` carries it into IRReduceCompute. Consequently an all-negative-zero seedless deferred row inherits +0.0 in both the action and the current knob-off walk/interpreter; it does not establish the requested first-value/-0.0 semantics. This is the pre-existing G74 family, not a new action/off mismatch. Fix the evidence text to distinguish explicit-seed preservation from seedless semantics; add an explicit seedless witness to the G74 record, and do not claim this leg closes G74. Resolve head-peeling in the shared fold representation/twins, not by changing only action2.

3. MEDIUM — LLVM knob-off coverage is missing. `tests/OrbitScatterTests.fs:814-824` builds C++ with BLADE_ORBIT_ACTIONS=0, but all four LLVM builds leave the knob on. LLVM is compared with a syntactically different, second-read walk twin and the C++ reference; that is useful evidence but is not the requested identical-source LLVM knob-off A/B. Fix: add an LLVM BLADE_ORBIT_ACTIONS=0 build with expected action-route count 0, retaining the same raw-bit comparisons. Add an LLVM threaded-request plan assertion alongside the existing C++ decline assertion so finding 1's lane distinction is pinned. No new timings are needed.

Static disposition of the requested attacks:

- (a) At canonical (a,b), C++ diagonal-block/tail code calls f(x,a,b) and, only off-diagonal, f(x,b,a); the block path stores separate Zr/Zc terms. LLVM mirrors all three paths. Weight sentinels bind the chain's own/partner coordinates, independent of whether the source spells S(i,j) or S(j,i). Each row receives partners 0..n-1: earlier groups first, the diagonal block in row order, then successive columns. A different role evaluation at the other endpoint does not change this chain order. No one-term-both-endpoints shortcut found.
- (b,d,e) Recognition is direct map-compute, one inner scalar fold, one compact pool/read orientation; remaining indexes and coordinate uses fail closed. Named tags must agree when both are present (`OrbitFold.fs:1281-1286`); missing tags use equal-extent positional compatibility, not an affirmative nominal-identity proof. Weights must be nonvirtual rank-one plain real arrays. Deferred storage, additional contracted levels and unsupported symmetries are refused. The totality walk checks term/kernel/seed, including transitive callees, rejecting the listed panic operations. Static n >= 1 removes an empty action. The planner uses actual scheduleMayWriteIn calls, complete intervalPayloadOf payloads, provider-effect checks and storedArrayProof; all read bindings must precede the action. Its consumer audit includes the inner map/fold/seed; emitter checks supplement it. TOP-LEVEL-only admission is conservative and explicit. There is no newly created parallel body requiring callbackTable; the output receives its own stored pool. No missing admission party or moved abort found in this admitted shape.
- (c) The vector-output action is not the scalar orbit-class fold or outer-fold row-slot shape. C++ rejects requests on all three relevant kernels before emission, preserving any old Path B/team route; HotRow/CompactRow/EpilogueLeg owners also preempt it. Thus no threaded licensed competitor is silently replaced. Without a request, replacing the licensed row association with the strict chain is permitted by the brief and recorded; `strict` restores the licensed walk, and `0` disables the action. Mixed weighted scalar nests are only recorded as declined, in either licence orientation; their emission stays on the old route. The optimizer gate is registered and does not exclude REPRO.
- (f) The action leg checks raw-bit a/b equality, cross-build equality, route counts at G=1/3/4/8, one NaN source, explicit -0.0 seeds and the cancellation row. Interpreter comparison is decimal, not raw bits. Corpus 140's [9,30]/[13,26]/[4,-4] separates endpoint roles; 141's [10,-6] separates antisymmetric mirrors. The mixed-nest refusal is separately pinned in OptimizeTests. `r_late` tests a deferred weight, not a weight declared after the consumer; source forward references are not an admission witness. Do not describe the finite refusal set as exhaustive.
- (g,h) The Fortran serial expert's column sweep gives each row ascending partners from 0.0; its two triangular gathers give the mt8 owner the same sequence. Natural's transposed reads are valid for this symmetric producer. All W/z/u/y stores are read back; input checksums read pts/v. The ordered oracle uses seed-first add.accumulate; checksum summation has the separately stated tolerance. Four fused expert channels versus four Blade passes is disclosed. Directional medians are explicitly loaded-box, producer-subtracted, 11-round evidence, not a harness ratio or established uncertainty interval. Multi-channel, Deduce residual symmetry and the rank-three tower remain excluded.

Hook verification (five groups; actual names/locations):

| Claimed hook | Verified source | Result |
|---|---|---|
| Shared recognition, planning, admission | OrbitFold.fs:1448 / :1363 / :1566 | Present; recogniseActionWalk only records a decline. |
| Audit placement and gate | Optimize.fs:5145 / :190; Lowering.fs:3204-3209 | Facts-only audit after pull, reset at :2962; gate registered. |
| C++ entry and runtime | CodeGenBinding.fs:1414 / :4875; blade_orbit_scatter.hpp:203 / :276 | Direct-compute hook, tri_action/action2; old tri unchanged. |
| LLVM entry and pass | EmitLlvm.fs:4236 / :7419 / :7492 | Actual entry is tryBindOrbitAction, not tryOrbitAction; bindNamed arm follows IRProdSum. |
| Lane-owned decision merge | CliCommands.fs:1073-1085 | Same-span pairing also compares action/scatter kind; an action cannot consume a scatter record. |

(j) No non-admitted-program emission change found by static tracing: decline paths add decisions, not IR/code, and retain walk/scatter/pull precedence; symmetry/132-139 are unchanged. Byte identity against 361504b6 was not executed or established by this review. `git diff --check` is clean.

---

## codex-diff-W3-1

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-diff-w3-1` (appended at the wave-3 records commit)_

VERDICT: DO NOT MERGE

Reviewed `361504b6..eae63c64`; builder worktree HEAD matches eae63c64. Read-only inspection of contracts, source, git and existing generated C++; no builds, tests or executables run. Line numbers below refer to eae63c64. Static witnesses are not reported as executed tests.

1. **BLOCKER — COUNT-only/unused group_by sources escape admission; LLVM drops their failures.** `Optimize.fs:5989-6003` gives COUNT `Sources=[]`, then validates only sources mentioned by states, not every `groupBySites` source. An unused group_by has the same hole. `EmitLlvm.fs:4326-4333` installs null rows without evaluating `values`; ordinary `IRGroupBy` at :5571-5583 consumes and reads them. Static witness: module-level deferred `lazy = method_for(den) <@> lambda(d) -> 10 / d`, `den=[1,0]`; inside a function, `gk=group_keys(k)`, `g=group_by(lazy,gk)`, then only a computed `extents(r)` peel and a scalar sum of its counts; `k=[7,7]`, print only the scalar. The count plan never applies sourceProof to lazy. Pure division passes the write checks. Gate-off LLVM reaches BL8013 while gathering; planned LLVM discards the division. C++ still executes forceDeferredArrayInput (:3512), so this also splits lanes. **Fix:** certify EVERY group_by value source, including count-only and unused operands, as stored/dominating; otherwise decline the entire plan. Retain original forcing until that proof exists. Add routed LLVM gate-off/on abort fixtures for both forms.

2. **BLOCKER — fold result/accumulator types are not certified; strict LLVM values can change.** `Optimize.fs:5953-5977` checks arity, totality and source/output element equality, but never the fold callable's RetType. Typechecking unifies parameters and seed with the element, not the callable result (`TypeCheckInfer.fs:4009-4029`); IRReduce itself reports the source element (`IR.fs:3498`). Ordinary LLVM selects accTy from cl.RetType (`EmitLlvm.fs:7027-7030`) and converts the unseeded first value to it (:7132). The new pass selects the peel's element type and copies directly (:4168-4177). Static witness: singleton Int64 group containing 9007199254740993, folded with `lambda(a,b) -> Float64(a) + Float64(b)`, then returned through an Int64 aggregate output. Gate-off LLVM rounds the first value through double to 9007199254740992; planned LLVM keeps 9007199254740993, even with REPRO=1/CONTRACT=off. The fold body is on the total whitelist. **Fix:** fail closed unless both callable parameter types, callable result, source element, seed and state agree in the supported scalar representation. Pin the singleton and multirow cases per lane; fix the pre-existing ordinary-fold typing discrepancy separately.

3. **BLOCKER — the explicit producer W party is missing despite the five-party claim.** `Optimize.fs:5707-5708` creates the shared predicate, but `sourceProof` (:5849-5883) never calls it: assignedAnywhere plus an independent body-let whitelist supplies admission; storedArrayProof is used only for module bindings and its returned chain is discarded. The interval query (:6030) does visit group_by value references and can chase deferred producers, but excludes gk itself and is not a query over each key/value producer payload. Its IRVar chase deliberately stops at stored/compute boundaries (:1173-1188). Thus it does not establish the expressly required producer W party. The record at :6050 and decision test at `OptimizeTests.fs:4937` merely assert that every party used W. **Fix:** query scheduleMayWriteIn on the actual key/value producer payloads and relevant storage-proof links; reuse/extend the shared storage proof for body bindings and parameter provenance. Keep assignedAnywhere supplementary. Add producer/helper/view-alias writer refusals, including count-only sources; make the matrix describe actual queries.

4. **BLOCKER — panic provenance changes in BOTH lanes.** `CodeGenBinding.fs:3397` emits BL8003 directly in the enclosing function, without the peel frame; the ordinary row kernel emits it in its own frame. Existing generated fixture `generated_cpp_tests/Group_aggregate_unseeded_fold_over_an_empty_positional_group_aborts.cpp:220` uses the function's line 11, while :236-237 retains the ordinary `lambda in s` frame and `nullptr,0` guard. LLVM explicitly substitutes plan.Subject's function span for both gk and every peel (`EmitLlvm.fs:4350-4354`), unlike its gate-off caller/no-location behavior. Matching a different lane is not gate equivalence. The checks remain temporally at each peel and scan the first empty group correctly; 074's intervening BL8013 still precedes them, subject to finding 1. **Fix:** preserve the original guard location and frame separately for gk and each peel. If improving missing spans, change ordinary and planned paths together in a separately identified diagnostic fix. Compare complete failure code/location/frame and order, not only ABORT substrings in 066/073/074.

5. **BLOCKER — (j) fails: LLVM emission changes without any admitted plan, including gate off.** `EmitLlvm.fs:4019-4053,4094-4097` unconditionally replaces dynamic CSR discovery/count/scatter lookups with C-core calls and new BL8005 guards. `OptimizeTests.fs:5068` explicitly expects this with BLADE_GROUP_AGG=0. Thus a top-level dynamic grouping or a function whose rows are read also changes. The brief requests global retirement of O(n*G), but that conflicts with this review's explicit byte-identical/no-plan requirement; the implementation cannot claim both. **Fix:** separate and independently gate/review the global discovery change, retaining tip emission as the off/no-plan control for this diff. The recorded dynamic LLVM OLD already includes the new map, so it is not the integration-tip baseline.

6. **HIGH — “every bit, both lanes” is not what the new runtime tests establish.** `CliSelfTests.fs:1463-1483` compares strings for only checksum and residual. It neither dumps raw floating bits nor compares each stored state/output; aggregate collisions and print rounding can conceal differences. The cancellation check (:1493-1501) discards the strict/default exit status and route evidence, and does not assert the expected default result. Corpus pins and normalized textual differentials are not raw-bit comparisons. **Fix:** add lane-enforced, gate-off/on raw-bit output comparisons for all four states, signed zeros, singleton-at-final-row, nonzero seeds, ordered noncommutative folds and cancellation; assert route, status and expected values for every cancellation arm. Until then label these checks printed-value witnesses, not bit identity.

7. **HIGH — records overstate both coverage and behavior.** `Optimize.fs:6010-6024`, `docs/features/sql.md:748-760`, `CLAUDE.md:207`, and G71 (`bench/notes/gap-register.md:121`) need coordinated correction:
   - A C++ per-group K-lane tree is not the LLVM implementation. Ordinary LLVM emits a recurrence with eligible fast-math flags (`EmitLlvm.fs:7135-7152`), not an explicit eight-lane tree. Its default cancellation result already equals the planned result on this witness. Default LLVM does decline licensed float states, so their accumulation bits are not changed by T3 by default; this is a conservative shared gate, not evidence that LLVM has the C++ tree or that all licensed inputs are bitwise equal. Optimize's “1 in eight lanes” also disagrees with the reported C++ unseeded fixture's sum 0.
   - “064-074 in both lanes” / “both lanes in the default suite” exceeds the reported 23/0/51 LLVM result and its EnumIdx/compound/runtime-return refusals. State which witnesses actually took LLVM and the plan.
   - “top-level ... rows stay stored” is not guaranteed: unchanged `computeExtentsOnlyGroupBys` (`CodeGenExprSupport.fs:999-1015`) still elides an unprinted top-level sizes-only/unused grouped array. T3 declines top-level groupings; it does not establish that stronger invariant.
   **Fix:** distinguish C++ association, LLVM flags, conservative admission, actual routed coverage and the pre-existing gather-elision exception; update the tests that pin the inaccurate prose.

8. **MEDIUM — the licensed arm has not met its required small-group performance gate.** G71 records G=1009 strict and contended licensed numbers, but no G<=3 timed comparison against the >10% loss DROP rule. The two-group cancellation fixture is numerical, not that measurement. **Fix:** obtain the prescribed one-core interleaved G=1/G=3 diagnostic medians and apply the verdict, or leave the licensed arm unshipped/deferred. The recorded strict G=1009 ratios exceed 1.3x; those are builder measurements, not independently rerun here.

Other requested adjudications, bounded by the findings above:

- **State machines:** C++ :3258-3269 and LLVM :4169-4204 copy the unseeded first value and skip exactly one update per group; incrementing count only after every state also handles a first occurrence on the input's last row. Seeded dynamic initialization at the first row is valid: discovered dynamic groups cannot be empty. Static seeded empty groups retain the seed. Prodsum starts at +0 and follows the original multiply/add recurrence under REPRO/CONTRACT=off; the licensed arm may change association. Count is Int64 at output, derived from the offsets/counts. The ordinary same-type 3*a+b chain remains ascending; finding 2 is an admitted exception to the claimed general per-lane identity.
- **Failures/regimes:** totality rejects integer division/modulo, possibly negative integer powers, float-to-int casts and lgamma/digamma in audited fold bodies/seeds. No fold kernel runs for an empty group. Positional groups retain all N slots, today's negative drop and >=N BL8006; EnumIdx uses declared ordinals, including valid negative source members; dynamic numbering is first occurrence, not table order. C-core lookup is one call per surviving row for the planned pass; ordinary LLVM's scan is genuinely retired globally, not merely bypassed.
- **Census/output/kernel:** direct argument/tuple/alias/capture/row reads of grouped values are counted outside recognized peels and decline (several are front-end refusals already); extents(row) is deliberately COUNT. The concrete hole found is its omitted producer, not a demonstrated unseen ordinary consumer. The new plan cannot null a top-level grouped binding. Both benchmark groupings are inside functions; specs explicitly name s/q/c, not g, as outputs, so OLD and NEW request the same output set. Source emission stores/adopts s/q and computes counts; no optimized-machine store audit was established by this review. For the supplied nonnegative, populated benchmark data, the experts' +0 initialization agrees with the unseeded chain; dynamic expert numbering survives rehash in first-seen order. Natural masked SUM does not itself guarantee a portable exact association. The serial-only/no-mt8 statement and input-only twins match the stated scope. No general empty-group equivalence should be inferred for the Fortran/oracle zero-initialized sum.
- **Interpreter:** no interpreter or semantic IR rewrite in this pass; it remains an independent implementation for these plans. Textual interpreter differentials are meaningful, subject to their output precision and routed coverage.

Hook table (five report claims):

| Claim | Hook at eae63c64 | Inspection |
|---|---|---|
| Planner/gate/reset/placement | Optimize.fs:190,5697; Lowering.fs:2963,3211 | Registered; reset; called after planOrbitPull. |
| C++ one-pass entry | CodeGenBinding.fs:1021,3218 | genGroupKeysBinding dispatches to plan emitter; regime handshake. |
| C++ peel and null gather | CodeGen.fs:355; CodeGenBinding.fs:3380,3569 | Computed body peel adopts state; emitted plan permits null rows. |
| LLVM pass/rows/peels | EmitLlvm.fs:3981,4118,4326,4339,4428 | Placed pass and bindLetIn hooks present; provenance/source omissions above. |
| Shared map and five shim entries | cpp/blade_member.h:404,425; cpp/blade_llvm_shim.c:632-650; EmitLlvm.fs:1278-1286 | First-occurrence IDs and record slots; init/group/record/count/fini wired. |

(j): **FAIL** — C++ no-plan paths appear unchanged and corpus 051/056 files are untouched; dynamic LLVM off/no-plan emission is demonstrably different from 361504b6. git diff --check is clean.

---

## codex-diff-W3-1-2

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-diff-w3-1-2` (appended at the wave-3 records commit)_

﻿VERDICT A (89d6fdcb): MERGE AFTER FIXES — repair the mixed-type runtime pin (new finding 2).
VERDICT B (e6eadfa2): DO NOT MERGE — later CSR passes can insert groups beyond the allocated CSR (new finding 1).

Reviewed eae63c64..89d6fdcb, 89d6fdcb..e6eadfa2 and the relevant 361504b6..e6eadfa2 paths. Builder worktree HEAD is e6eadfa2. Files/git only; no builds, tests, Blade invocation or compiler execution. Lines below are at e6eadfa2 unless stated. Counterexamples are static, not executed.

First-look items:

1. CLOSED — Optimize.fs:5961 checks the key; :6095 enumerates EVERY groupBySites source, including count-only and unused arrays. EmitLlvm.fs:4344 requires `(consumeArrOperand c values).Src = APool _` before installing null rows (:4351). A C++-stored source that LLVM cannot represent as a pool refuses LLVM emission and falls back to C++; it does not silently skip evaluation. C++ retains forceDeferredArrayInput at CodeGenBinding.fs:3512 before gather elision.
2. CLOSED — Optimize.fs:6044 compares both parameters, result and source element with the supported Float64/Int64 state; :6066 checks the seed. The wtype optimization is declined before emission, while the program still reaches the ORDINARY emitter. G71 correctly records that path's pre-existing C++ BL9002 versus LLVM double-rounding discrepancy separately. Its runtime pin remains deficient (new finding 2).
3. CLOSED — wrote (:5707) includes the actual scheduleMayWriteIn query. Provenance audits every module alias-chain payload (:5878), a body/block let's value (:5891), and each direct call argument's provenance or literal (:5920). A caller's parameter has no let/module provenance and declines; an indirect function reference makes countProgram differ from the direct-call count (:5914) and declines. No recursive parameter proof is claimed. Peel, fold, seed and interval queries remain present (:6011, :6046, :6060, :6123).
4. CLOSED — retract the first look's C++ frame inference: the ordinary scalar ragged peel inlines callable.Body through exprToCpp (CodeGenCuda.fs:2768,2792), and renderReduceExpr emits its guard inside an IIFE without a peel frame (CodeGenExpr.fs:1620). setCurrentCodegenDecl still names the containing function (CodeGen.fs:2243); the planned guard uses that same declPanicArgs (CodeGenBinding.fs:3397). LLVM's function-span substitution is removed; ordinary applyKernel pushes the peel frame (:6329), its ordinary empty guard uses declLocArgs (:7137), and the planned check does the same (:4381,4392). Tuple-call inlining retains the caller's DeclSpan (:7908); G71 explicitly records the pre-existing cross-lane divergence and 066/cli reproducer split.
5. PARTIAL — A restores the ordinary LLVM CSR text; B isolates and documents the gate-independent dynamic map change, including allocation/growth BL8005. First-occurrence numbering is preserved for stable keys, but B's later find-or-insert calls are unsafe for an inline effectful producer (new finding 1). The 152/152 and 75/75 claims are reported cross-version sweeps, not assertions of the committed emission test; see evidence limits below.
6. PARTIAL — envOf sets BLADE_PRINT_BITS=1 (:1578); gateIdentity compares stdout/status/stderr and asserts route and plan (:1597). Cancellation asserts each arm's route, status, plan and exact bits (:1645): C++ strict/default/licensed 5/1/5, LLVM 5/5/5. gdynstates (dynamic per-group arrays) is explicitly C++ only; gdyn's dynamic scalar fingerprint and gcancel run both lanes. Deferred module-source witnesses compare C++ off/on and check LLVM fallback on the enabled arm. wtype merely reports its route and accepts unrelated matching failures (new finding 2).
7. PARTIAL — C++ K-lane versus LLVM flagged recurrence, conservative shared decline, computeExtentsOnlyGroupBys, the named LLVM coverage subset, cancellation numbers and experimental opt-in status are corrected. G71's blanket route/complete-output assurance still exceeds wtype's assertion, and B's unconditional value-identity claim excludes the new counterexample. The dynamic-array-only limitation should not be read as saying gdyn/gcancel never run LLVM.
8. CLOSED — G71 records CPU-2 affinity/one core, threads suppressed, 11 interleaved whole-program medians, 3–5% load, default declined form versus licensed: G1 C++ 11.48→9.91 ms, LLVM 9.57→8.51; G3 14.76→8.12 and 12.77→6.91. These are gains, so the >10% LOSS rule does not drop the arm. Keeping this association-changing arm opt-in matches the brief; 'never the default' describes current policy, not a prohibition on a future bitwise-equal implementation. Numbers are builder-reported, not independently rerun.

NEW findings:

1. BLOCKER (B) — dynamic discovery is not frozen before count/scatter; an inline writing key producer can create out-of-bounds group IDs.

EmitLlvm.fs:3926 consumes the key expression, but consumeArrOperand's anonymous-expression arm (:6189) returns a producer without materializing it. Named writing maps are protected by Lowering.forceEffectfulModuleLets (:2760); an INLINE map nested in IRGroupKeys is not: that pass walks let spines, and IRLift.fs:746 only recursively preserves the key expression. applyToArr returns an AVirt producer (:6673). Static witness, with BLADE_GROUP_AGG=0 and LLVM requested, printing only sizes:

```blade
type One = Idx<1>
type Two = Idx<2>
let mut calls: Array<Int64 like One> = [0]
let rows: Array<Int64 like Two> = [0, 0]
function next_key(c: mut Array<Int64 like One>, x: Int64) -> Int64 = {
    c(0) = c(0) + 1
    c(0)
}
let gk = group_keys(method_for(rows) <@> lambda(x) -> next_key(calls, x))
let sizes = extents(gk)
```

Discovery reads keys 1,2 and freezes ngOp=2 (:4063); counts/fill allocate two cells (:4069). The count pass reevaluates the producer as 3,4, invokes the SAME groupOf (:4064,4085), and gets newly inserted IDs 2,3. The only guard is g>=0 (:4053), so counts[2] and counts[3] are accessed out of bounds (:4089). Scatter can insert still more IDs. A's linear bucket lookup defaults an absent key to bucket 0 (89d6fdcb EmitLlvm.fs:4066), so this example has bounded writes and sizes [2,0] there. That multiple-evaluation behavior is pre-existing; B introduces the new memory error. T3 admission cannot protect this top-level, gate-off path.

Exact fix: restrict B's map route to a proved stable key pool (APool is a sufficient conservative gate); retain the original scan for unproved producers and narrow the 'every dynamic grouping' records accordingly. Add a routed gate-off A/B regression for the inline witness, checking exit, sizes and write count. Do not silently materialize the key to fix this: that also changes the baseline evaluation count and needs a separate semantic change.

2. HIGH (A) — the mixed-type regression can pass without exercising LLVM or its documented result.

CliSelfTests.fs:1630 captures oe0/oe1, but :1636 never calls routeOk. It checks neither LLVM exit 0 nor 9007199254740992, neither C++ failure nor BL9002. Any equal nonzero exits with equal truncated error prefixes pass; even empty error-prefix arrays compare equal. The route at :1637 is report text only. This is the specific witness for the former fold-type blocker, so losing the LLVM arm can silently remove the regression coverage.

Exact fix: require routeOk for BOTH executions; LLVM must exit 0 and print the expected singleton and multirow arrays (9007199254740992 and 9007199254740994), with complete stdout/stderr equality. C++ must fail with BL9002 in both gates; normalize only the private build path when comparing diagnostics. Assert the type-decline reason, not just any decline. Align G71's evidence claim with those assertions. For the generic failure fixtures at :1612–1616, also pin the expected BL8003/BL8006/BL8013 codes so matching build failures cannot stand in for the intended runtime aborts.

Hook table (five claims):

| Claim | Hook | Static result |
|---|---|---|
| Complete producer admission | Optimize.fs:5878,5891,5914,5961,6095 | Payload W queries and fail-closed provenance; all grouped operands included. |
| One scalar fold representation | Optimize.fs:6044,6066 | Parameters/result/element/state/seed agree before recording a plan. |
| Ordinary versus planned guard provenance | CodeGenCuda.fs:2794; CodeGenExpr.fs:1620; CodeGenBinding.fs:3397; EmitLlvm.fs:4381,6329,7137 | C++ inlined IIFE/function frame; LLVM peel frame/current caller span preserved. |
| B's discovery/CSR boundary | EmitLlvm.fs:4063,4064,4069,4085; cpp/blade_member.h:425,484 | First-seen IDs, but later lookups can still insert beyond the fixed CSR size. |
| Runtime evidence | CliSelfTests.fs:1597,1636,1660; tests/OptimizeTests.fs:5130 | Main identities/cancellation routed; wtype is not; emission needle is not a cross-version census. |

Evidence limits: the inspected committed test group_agg_llvm_one_pass_map checks map slots/needles (A: no map off; B: one slot off); it does NOT compare against a 361504b6 executable or implement the 152/75 censuses. Those totals appear in the commit messages/G71; no linked sweep script or result manifest was supplied. The checkout has 74 sql-group-by files: (74+2 kernels)×2 lanes=152 for A; 74+groupagg_dyn=75 for B. These are not 76 corpus files, nor 75 routed LLVM programs: B says LLVM requested and includes refusals/fallbacks. Its stated .ll census is 18 changed, 57 unchanged. I did not independently verify those runs or the claimed default-gate ten-file delta.

Scope: A's source diff preserves ordinary emission when no plan is admitted in BOTH lanes; its 152/152 off-gate claim remains reported evidence. B changes only LLVM ordinary dynamic discovery plus its tests/records (C++ and static LLVM paths unchanged), intentionally changing no-plan dynamic emission; new finding 1 prevents accepting its claimed value identity. git diff --check is clean.

---

## codex-diff-W3-1-3

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-diff-w3-1-3` (appended at the wave-3 records commit)_

VERDICT A+A' (89d6fdcb + d0f8d293): MERGE AFTER FIXES — prior HIGH closed; repair sweep evidence (#2).
VERDICT B+B' (e6eadfa2 + 862bf814): MERGE AFTER FIXES — prior BLOCKER closed; correct sweep routing/evidence (#1–2).

Files/git only; no builds, tests or program execution. Reviewed both requested diffs and relevant 361504b6..862bf814 paths; builder HEAD is 862bf814. Hooks below are at that commit.

A HIGH: CLOSED — CliSelfTests.fs:1645–1667 requires the exact type-decline reason, routeOk for BOTH executions, LLVM exit 0 / equal stdout+stderr / both expected arrays, and equal nonzero C++ exits with BL9002 in both gates; comparison normalizes the private build path (after the shared verbose/timing filter). At :1609–1627 each abort fixture pins BL8003/BL8006/BL8013 in both gates; equal complete filtered outputs plus the BL9002 exclusion reject it in either gate. G71 accurately describes these assertions and the pre-existing typing/provenance gaps; gdyn/gcancel are not excluded with gdynstates.
B BLOCKER: CLOSED — APool reads are GEP+load, not producer evaluation (:2093). Discovery/count/scatter execute consecutively inside emitGroupKeysBinding: only private map/CSR allocation, bookkeeping and loads intervene; no user kernel, mut-array callback, grouped-value producer or later binding can write the key pool there. Merely aliasing a mutable pool supplies no interleaving writer. Existing-key lookup returns before insertion (blade_member.h:428), so count/scatter cannot discover a new ID. The sole g >= 0 guard (:4062) suffices under this frozen-sequence invariant. Non-APool keys retain the exact baseline scan body and evaluation count; no key materialization. This closes B's introduced OOB, not every pre-existing inline-producer semantic gap.

NEW findings:

1. MEDIUM — B's “36 ran LLVM” census is false. sweep.ps1:66 infers execution solely from absence of “falling back to C++”. manifest-B-value-vs-361504b6.txt:30 labels 026 as “ran the LLVM lane”, although its BL3999 is a TYPECHECK refusal (fixture :2, :11; TypeCheckInfer.fs:15579); :39 similarly labels the BL3017 alias refusal. Fix: record positive backend/execution evidence and distinguish front-end rejection, backend refusal/build failure, LLVM execution and C++ fallback; correct the manifest and G71 (:121). Until rerun, call 36 “no fallback message”, not LLVM runs. Do not infer a replacement runtime count from this manifest.

2. MEDIUM — the sweep does not establish its advertised byte identity. sweep.ps1:49–51 discards emit stderr/status, compares Out-String text with case-insensitive -eq, and counts matching failed/empty emissions as identical. :40 drops WHOLE lines containing blade-build (including diagnostic content); :65 again compares case-insensitively. Thus “57 identical .ll emissions” includes refusals producing no .ll, and “complete stderr” is overstated. Fix: compare successful emitted bytes ordinally; record rejected emissions separately with status/diagnostics; compare runtime output ordinally after replacing only the private path substring and removing explicitly identified timing/verbose records. Regenerate manifests, or immediately narrow G71 to the script's actual filtered-output comparison and withdraw byte/complete-output claims pending replacement evidence.

3. LOW — build provenance is insufficient to reproduce the recorded artifacts. Both manifest headers :2–3 use generic build descriptions; B' has no exact source-tree identity, and neither records Blade.dll hashes or the ambient emission-affecting configuration. Fix: record source commit/tree (including any dirty patch), SHA-256 of each Blade.dll, input checkout/tree, resolved command arguments and effective knobs/toolchain. Generic names are acceptable as attributed builder testimony, not auditable binary identity; do not invent historical hashes.

Hook table (five claims):

| Claim | File:line | Result |
|---|---|---|
| Ordinary map calls all gated | src/EmitLlvm.fs:4032 | APool dynamic arm owns init :4053, group :4060 (discovery :4069; count/scatter through bucketOf :4144), count :4072; fini :4167 requires dynMap=Some, set only at :4050. |
| Planned map and Agg gated | src/EmitLlvm.fs:4204 | APool check precedes all emission; GaDynamic :4291 owns init :4295, record :4303 (row and extraction), group :4311, count :4322, fini :4334. Agg=Some :4336/:4393 shares that gate; ordinary CSR sets None :4169; consumers check placement/Agg :4422/:4428. Shim-table entries :1282–1286 are declarations, not extra calls. |
| Frozen reads / unchanged fallback | src/EmitLlvm.fs:2093,4074,4128 | Pool loads invoke no callback; fallback scan is unchanged from 361504b6, with no new register/label emission on the rejected map arm. Unplanned non-pool emission is statically unchanged; not independently emitted here. |
| Regression distinguishes B/B' | tests/OptimizeTests.fs:5161; src/CliSelfTests.fs:1684 | Both gates must contain no @blade_gkmap; e6eadfa2's unconditional dynamic arm would emit one. Runtime assertions require routed LLVM, exit 0, sizes=[2,0], calls=[6]; C++ explicitly pins BL7004. B itself was not run on this witness. |
| Assertions and census | src/CliSelfTests.fs:1609,1654; bench/notes/round3/w3-1-sweeps/sweep.ps1:49,66 | A assertions close the HIGH. Manifest arithmetic matches G71: 304/284, gate-off 152/152, ten differing files per lane (064–071,073,074), 75/75, labels 36/39 and 18/57; execution/byte interpretations require #1–2. |

Scope: without an admitted plan, static diff inspection preserves baseline C++ and LLVM emission except LLVM dynamic grouping over stored key pools; the manifest records 18 such changed cases, not an exhaustive count of affected programs. Bit identity remains argued plus builder-witnessed; reported test footers were not rerun. git diff --check is clean.

---

## codex-diff-W3-2

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-diff-w3-2` (appended at the wave-3 records commit)_

VERDICT: MERGE AFTER FIXES

Reviewed `git diff 361504b6..bc3ddac9` and the surrounding source/contracts. Read-only: no builds, tests, Blade execution, compiler invocation or timings. Reported test footers and measurements were not independently rerun. Locations below are at bc3ddac9.

1. **HIGH — the critical corpus pins do not require the join route.** `tests/OptimizeTests.fs:5351,5363,5442–5518` checks synthetic `hjSrc`/positional fixtures, not the actual `sql-joins` files. The CLI's three route witnesses (`src/CliSelfTests.fs:1748–1797`) likewise are separate programs. Thus 005 (BL8003), 010 (duplicate order), 011 (out-of-domain probe), etc. can silently revert to the six passes while their value/error pins remain green. Opt-diff explicitly treats identical emission as a pass (`tests/OptDiff.fs:167–170`); the reported three inert files are evidence for this run, not an enforced route contract. **Fix:** add a per-file corpus route census: require applied plan AND emitted CSR/cardinality-only constructor for 001–005 and 008–012; require the particular decline AND no CSR for 006/007/013. Require the hash/positional routes for 008/009, gate-off absence, and retain the existing value/error pins. Extend the failure-admission witnesses beyond `/` to `%`, negative integer `^`, float-to-int casts and unresolved calls on nonmatching pairs. Do not describe the current cases as covering every refusal/matrix row.

2. **HIGH — “stored in every arm” and the DROP clearance exceed the supplied store evidence.** `bench/kernels/hashjoin/expert.f90:79–86` computes both scalars from `v`; in a normal timed build its `matched` stores have no subsequent reader. The reader at :96–102 exists only under `BENCH_DUMP`. This does NOT establish that gfortran eliminated the stores; it establishes why source and a separately compiled dump cannot prove their survival. No optimized-store audit accompanies `spec.md:20–21,74–76,121–128` or G72 (`bench/notes/gap-register.md:121`). `bench/README.md:63–69` explicitly requires that audit before publication. **Fix:** attach per-arm optimized-binary store evidence for the actual measured pilot builds, including inputs and `matched`, compiler flags/identity and dump mode; if an arm lost stores, label it checksum-only and withdraw/repeat the ratio with a compliant arm. Until then label the storage claim and DROP decision provisional. Numerically, under the preamble's expert/Blade convention, 72.8/86.7 = 0.84 clears the 0.8 TOTAL-region line; 41.1/54.9 = 0.75 does not clear an incremental line. State the chosen total-region criterion explicitly; neither a different size nor a different denominator can substitute.

3. **MEDIUM — reject the mixed compile/runtime meaning of `BLADE_MEMBER_VERBOSE`.** `src/CodeGenBinding.fs:2700–2709,2728` omits the report unless the variable is enabled during emission; `src/cpp/blade_member.h:549–553` checks it again at runtime. A default-built executable cannot report its join route when the user subsequently enables this formerly runtime-only diagnostic switch. The CLAUDE.md note discloses this, and existing membership-only emission is unaffected, but the join diagnostic now requires two settings at two different times. **Fix:** either always emit the runtime-controlled report and account for its cost, or introduce a separately named compile-time instrumentation gate, register/document it, and leave `BLADE_MEMBER_VERBOSE` runtime-only. Pin compile-off/run-on and compile-on/run-off behavior independently; the current CLI test sets the same environment for compilation and execution and cannot detect this distinction.

4. **MEDIUM — the cost table omits the work used to explain the remaining gap.** `bench/kernels/hashjoin/spec.md:102–103` counts the build/probe but omits NEW's two stored-relation consumer passes: at J=5000, `total` and `fp` alone add 80,000 logical bytes read and their arithmetic. The expert incorporates those computations into its probe. The table also leaves CSR offset reads as an unquantified “+ reads”, while claiming a logical-traffic total independent of cache residency. **Fix:** give construction-only and complete-kernel totals separately, with the two folds, CSR accesses and a consistent operation-count convention in all arms; propagate corrected totals to G72/kernel notes. Keep logical traffic, allocated/live storage and measured peak WS separate. The ~51 KB construction live set is not itself refuted by this omission.

5. **MEDIUM — cardinality accumulation has no overflow guard.** `src/CodeGenBinding.fs:2723–2729` adds bucket lengths into `size_t` unchecked and then allocates `card + 1`; the fill is not bounded by the allocation. These are 64-bit on the supported target, but “64-bit” is not an overflow proof for J=N*M. The C core bounds its own M-sized storage; it cannot bound the caller's N*M output. **Fix:** checked-add each bucket length against the maximum allocatable element count (including slack), reject before allocation/fill on overflow, and check the dense-extents product retained by the index. Preserve ordinary allocation-failure handling. No overflow occurs at the pilot: even all-equal J=2,021,027 needs about 16.17 MB of payload and is allowed; the general route has no 64 MB cap.

Attack conclusions (static, not additional test results):

- **Order:** `blade_member.h:519–530` counts at b+2, prefixes the starts, and fills `perm[off[b+1]++]` with j increasing in BOTH direct/hash branches. Hash insertion numbers distinct keys; it does not deduplicate their rows. `CodeGenBinding.fs:2732–2737` probes i increasing and each bucket forward. No sorting/prepending path was found. The cardinality-only constructor (:279–283 of `index_types.h`) does not enumerate anything: its buffer order comes from the probe, matching the old lex enumeration. Corpus 010 pins the entire vector and count as well as 171: prepend gives 209, right-major 273, dedup fewer cells. Those three wrong forms cannot coincidentally pass these pins; a general modular fingerprint is not a collision-free proof.
- **Recognizer/failures:** `Optimize.fs:4853–4924,5012–5107` requires exactly two named rank-1 plain sources per product, paired axis tag/Id identity rather than extent equality, the identity predicate, equality of the two parameters in either order, and Int64/plain or positional keys. Conjunctions, float keys, extra axes and named dense/mask operands decline. It records the actual dense kernel and actual mask key operands; no cache keyed merely by `(a0,b0)` substitutes another payload. `totalScalarOver` (:3752–3778) excludes integer `/ % ^`, casts, calls and nonparameter references; pk separately rejects captures. No skipped-failing-pk counterexample found within this whitelist. Consumer folds are not rewritten; 005's unseeded empty check remains at its original site, subject to finding 1's route gap.
- **Admission/census:** the shared predicate is actually installed at `Optimize.fs:4820–4821`; source-chain payloads, all three kernels, J's complete payload, outer fold kernels and seeds reach it (:5108–5150). `storedArrayProof` and dominating bindings are checked; unresolved calls remain writes (`scheduleMayWriteIn`, :1585–1586). There is no cross-binding execution interval or parallel moved body. Seeds that may write are recorded as untouched, not moved. The census rejects keyed/presence/partial reads, aliases, tuples, arguments, captures and uncertified map uses; side operands, function signatures/captures and foreign references are included. No missed executable use among the requested kinds was found. This is inspection, not a general completeness proof. Narrow `docs/features/sql.md`'s “nothing in the five-party write audit writes” wording to the moved/skipped parties: writing seeds are deliberately admitted unchanged.
- **Sizing/domains:** counting and filling call the same lookup against an unchanged CSR; allocation follows counting. M=0 produces an empty direct table; N=0 produces no stores. Unsigned span differences handle negative/extreme keys. Right keys outside a claimed positional range are bucketed by their measured values, with hash fallback for a wide span; left values absent from the measured right set contribute zero matches, never BL8006. A value present on both sides matches regardless of its nominal domain.
- **Printing departure: accept.** `CodeGen.fs:3178–3210` prints only cardinality/size and compact data (or skips unsupported elements); it does not unhash or read the mask. Rank-2 scalar J therefore needs no tuple table for automatic printing. Keep the full printed-vector route witness required by finding 1.
- **Modular-fingerprint departure: accept.** B/13 explicitly requested a bounded/modular large-case fingerprint plus the tiny exact witness. Blade's three threshold subtractions, both Fortran `mod` chains and NumPy implement the same nonnegative modulus from zero; amounts bound each product below 2^20 and each intermediate below 2^32. Input seeds, keys and payloads match by source inspection. The input-only arms intentionally omit the join/fp. Pilot J=5000/K=401, smoke J=522, research full size unbenched, absent mt8, and expert fusion versus Blade's separate folds are disclosed. No ifx execution or store audit is established by that source comparison.
- **Lanes:** only a side-table plan changes; the interpreter still receives the old IR, so its differential is independent of this C core. LLVM refuses the same rank-2 shape, adding the truthful decline at `EmitLlvm.fs:8430–8443`. There is no successful LLVM module whose byte identity can be claimed for an admitted join; refusal text intentionally differs. The new test pins refusal/record, not gate-on/off LLVM byte identity. Thirteen LLVM skips are not thirteen LLVM correctness executions.

Hook verification:

| Builder claim | Verified hook | Result |
|---|---|---|
| Planner/table/gate and shared totality whitelist | `Optimize.fs:185,198,3752,4803,4817` | Gate registered; AsyncLocal plan table; same one-parameter whitelist retained for compaction. |
| Reset and placement after compaction | `Lowering.fs:2960,3194–3200` | Reset per program; planning after compaction; no IR rewrite. |
| Early compound dispatch and C++ count/probe | `CodeGenBinding.fs:2499–2513,2680,2720–2744` | CSR dispatch precedes old materialization; counting precedes allocation and fill. |
| Stable C core | `src/cpp/blade_member.h:443,472,519–530` | Same lookup for count/fill; forward CSR fill in both regimes. |
| Cardinality-only index / LLVM refusal | `src/cpp/index_types.h:279–313`; `EmitLlvm.fs:8430–8443` | Size-only constructor; guarded tuple-dependent lookups; explicit LLVM refusal. |

(j) Static trace supports unchanged generated C++ when no plan is admitted (including gate-off/non-equality single compounds); no identity comparison against the base was executed, and the current gate-off test checks needles rather than byte identity.

---

## codex-diff-W3-2-2

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-diff-w3-2-2` (appended at the wave-3 records commit)_

VERDICT: MERGE AFTER FIXES

Static review of bc3ddac9..a462c983; locations at a462c983. No builds, tests, execution or timings; reported footers and assembly observations were not independently reproduced.

First-look 1 — PARTIAL: actual disk enumeration, applied-plan + CSR + cardinality-only needles, both 012 relations, exact first-failure substrings and gate-off checks are present for all 19 current files. Six-pass emission cannot satisfy the positive needles. Filename coverage and failure-location pins remain incomplete (N1/N3).
First-look 2 — PARTIAL: specific instructions, flags, compiler identities and no-dump evidence now cover NEW, its twin and both gfortran tiers/twins; measured OLD is absent. Total-region 72.8/86.7 is explicitly the criterion; all four records label clearance provisional and disclose ifx. Only spec/G72 disclose the unretimed guard (N4).
First-look 3 — CLOSED: BLADE_JOIN_REPORT is compiler-only, excluded from optimizerGates, cleared by suiteClearedKnobs, and changes emitted text (cache hashes cppText, Build.fs:738). CLI really compiles and runs under opposite environments. CLAUDE.md restores MEMBER_VERBOSE's runtime-only/no-emission-change contract; the join report contains no getenv.
First-look 4 — PARTIAL: construction/complete totals and the extra 80,000 B are supplied and propagated, but logical traffic still mixes source references with assumed register reuse (N6).
First-look 5 — PARTIAL: checked-add safely bounds the relation buffer, including slack; PTRDIFF_MAX/sizeof(T)-1 is a conservative safe bound for the admitted scalar buffers on this 64-bit target. Dense-product and count checks precede relation allocation/fill, NOT all allocation/stores. The ordering needle does not order the checked-add guard itself (N5).

NEW findings:

1. MEDIUM — The census is keyed by three-character prefixes, not files (tests/OptimizeTests.fs:5552). Adding 001_extra.blade copied from 001 passes without adding a table row; a filename shorter than three characters throws. Fix: key the table by complete filenames and compare exact disk/table sets (or enforce unique prefixes explicitly). Every currently listed file does receive its own gate-off check.

2. MEDIUM — Chooser boundary coverage is missing, and the printed span can wrap. Optimize.fs:4827 matches blade_member.h:421's unsigned difference, threshold ordering and positional override on the build's valid inputs. However, CliSelfTests.fs:1684 uses M <= 1009: it never exercises max(4M,4096)'s 4M branch or exact 2^20/2^26 boundaries. The 98-case comparison is real and covers zero/negative/extreme keys, but is not a threshold test. Also Optimize.fs:5214 prints span 0 for [INT64_MIN, INT64_MAX], whose inclusive span is 2^64. Fix: add direct C-chooser/F# comparisons just below/at/above all thresholds, including M > 1024 and both positional flags; format inclusive span using bigint. The existing record clearly says the form is decided at run time.

3. MEDIUM — 014–019 pin only BL8013/BL8014, not the failure site (their final ABORT lines). InterpDiff.fs:417–422 only checks those substrings and nonzero exit, so matching wrong-site panics pass. Fix: add source-location ABORT substrings, including the callee sites in 017/019, checked by both C++ and interpreter. Static inputs do place each fault on (0,4): unmatched key 5 in 014–017/019; right key 0 versus left key 1 in 018, whose predicate itself panics before producing a Boolean. LLVM executes none: LlvmTests.fs:2189 returns a skip on refusal. Only the separate synthetic hjSrc test (OptimizeTests.fs:5606) pins the hash-join LLVM refusal/decision; there is no per-witness LLVM panic/location pin.

4. MEDIUM — Complete the measured-arm audit (spec.md:130): add OLD's optimized input/relation store evidence or explicitly exclude OLD/NEW=108x from the audited ratios. Add “timings are bc3ddac9; guard not re-timed” to kernel.json:29 and kernel-list.md:349. The probe's source is unchanged, but added count-loop control/exception paths can affect optimization; static inspection cannot establish “within noise.” Keep clearance provisional until remeasurement.

5. LOW — Correct the guard claims and strengthen the ordering assertion. CodeGenBinding.fs:2719 builds/allocates/fills CSR before :2730 checks N*M. Move the independent product check ahead of the CSR build; describe checked counting as preceding RELATION allocation/stores. At OptimizeTests.fs:5471, assert product-check < CSR-build < checked-add-test < accumulation < allocation < fill; currently only product-check < accumulation < allocation < fill is pinned. std::length_error does reach BL8005: CodeGen.fs:3625/3705 catch std::exception, then blade_runtime.hpp:397–405 supplies the default code.

6. MEDIUM — Fix or explicitly redefine the traffic convention (spec.md:112). C-core prefix iteration is b=2..nb, 400 iterations at nb=401; off[b] += off[b-1] has two reads and one write, hence 9,600 source-logical bytes, not 6,416. Even a carried-register convention gives 6,400. Likewise NEW's la[i] occurs inside the J-iteration probe, while the table assumes at most N loads. Choose source-logical references or documented optimized register reuse consistently, correct totals, and propagate to G72/kernel-list/kernel.json.

| Hook claim | Verified file:line | Result |
|---|---|---|
| Corpus route census | tests/OptimizeTests.fs:5520 | Real per-file emission checks; prefix loophole N1. |
| F#/C chooser agreement | src/Optimize.fs:4827; src/cpp/blade_member.h:421; src/CliSelfTests.fs:1751 | Actual cross-language comparison; missing boundaries N2. |
| Guard/count/allocate/fill | src/CodeGenBinding.fs:2719,2730,2737,2742 | Relation safety fixed; CSR already built. |
| Compile/runtime report split | src/CliSelfTests.fs:1828; src/cpp/blade_member.h:552 | Separate executable builds/runs; unconditional emitted report. |
| Optimized stores / criterion | bench/kernels/hashjoin/spec.md:118,155 | Specific supplied evidence and total-region criterion; OLD gap N4. |

Scope: static diff supports byte-identical generated C++ for programs without an admitted plan versus bc3ddac9; guard/report emission stays inside genEquiJoinBinding, added planner text is decision-only. No byte comparison was executed; changed runtime-header bytes can invalidate cache keys independently.

---

## codex-w3-3-salvage

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-w3-3-salvage` (appended at the wave-3 records commit)_

VERDICT: NEEDS ONE CHEAP EXPERIMENT FIRST — topology-verified, per-worker physical-core affinity for the existing G=4 schedule and its controls; 0.5–1 builder-day. Keep DROP for production integration pending that result.

Scope: files/git and Python analysis of recorded data only; no builds, executions or new timings. Read CLAUDE.md first. Checkout HEAD is 32a5a429, three commits after requested 3641bfc1. The P1 prototype/data/design/review are unchanged; the header's later row-action insertion shifts pull hooks by 97 lines. Hooks below use checkout lines. No graft index was available; no refreshing query was run.

**What the data shows**

Source: `bench/notes/round3/prototypes/p1-commit/ab-2026-10-21/raw.csv`; all round numbers below are its ZERO-BASED `round` (0–20). Filters use `team` and `config`. Times are milliseconds unless marked otherwise. Mode split `ms < 7.3` is descriptive, chosen inside the observed gap, not an acceptance test.

- T8 `d:4` fast rounds: **2,4,6,7,10,11,14,19** (8/21), range 6.0671–7.0176, median **6.4999**. Other 13: 7.6664–9.0936, median **8.5555**. Fast subset's paired `b:4.ms - d:4.ms` median is 1.8975; selecting that subset after seeing timing is not a valid TAKE estimate.
- T8 `di:4` fast rounds: **10,11,13,16,19** (5/21), median 6.3677; other 16 median 8.4639. These are SEPARATE processes from d, not instrumentation of those same d runs [H4]. Only rounds 10,11,19 are fast in both. Fast d records are CSV lines 118,227,387,401,586,649,789,1093; fast di records are 596,647,769,937,1099 (header is line 1).
- T8 `di:4`, all 21 rows: Pearson correlations with `ms` are **0.575** (`i_wait_frac_core`), **0.732** (`i_wait_ns`), **0.639** (`i_max_wait_ns`), **0.914** (`i_prod_ns`), **0.772** (`i_commit_ns`). Correlations with `i_wall_ns` respectively: 0.583,0.739,0.627,0.911,0.765. `i_handoffs` is **625 in every run**: zero variance, correlation undefined. Spearman with `ms`: wait fraction 0.360, production 0.943. Excluding slow outlier round 3 still gives Pearson 0.704 for wait fraction versus 0.881 for production. Waiting matters; it does not explain the split alone.

| T8 di:4 subset | Mean end-to-end | Mean region | Mean P (summed elapsed ms) | Mean S | Mean W | Mean outside region |
|---|---:|---:|---:|---:|---:|---:|
| Fast, rounds above | 6.4173 | 5.4919 | 38.3592 | 1.1442 | 3.3793 | 0.9254 |
| Other 16 | 8.5143 | 7.5239 | 47.4596 | 1.4940 | 10.3276 | 0.9904 |

Computed from `ms`, `i_wall_ns`, `i_prod_ns`, `i_commit_ns`, `i_wait_ns`; ns divided by 1e6. Use MEANS here so the accounting adds: slow–fast end-to-end difference **2.0970 = 1.1375 production + 0.8685 waiting + 0.0437 service - 0.0178 residual + 0.0650 outside**, dividing summed interval differences by eight. This is elapsed-time accounting, not causal attribution. Across individual runs, median `wall - (P+S+W)/8` is 0.1122 ms. Startup and drain are summed across workers, not additional serial milliseconds [H2].

Round 1 takes 7.5004 ms despite wait fraction 0.0917 (P=44.769); round 9 takes 8.7778 despite 0.0881 (P=54.459). Round 3 takes 10.6149 with P=62.746 and longest wait 496 us. These directly refute “wait fraction alone selects the mode.”

Time structure: d:4 fast counts by round thirds 0–6/7–13/14–20 are **3/3/2**; di:4 counts **0/3/2**. di has a late concentration worth retaining, not a universal early/late switch: d was already fast in rounds 2,4,6. d:4 Pearson versus round is 0.033, adjacent-round correlation -0.281; di:4 versus round -0.256. Same-round d:4 correlations: di:4 0.411, d:8 0.482, producer a 0.421, baseline b:4 only 0.165. Some shared window variation exists, but mode is substantially process-specific. CSV preserves execution order but has no per-process timestamps/topology; it cannot resolve shorter scheduling episodes. The median normalized time across all 56 configurations per round ranges 0.981–1.037, with no common two-mode switch.

T3/R2: no comparable broad two-cluster split established by 21 samples; mostly a central band plus upper outliers. d:4 medians/ranges: T3 **12.4472 / 12.1721–13.6909**, R2 **17.4209 / 17.0376–19.3639**. di:4 median wait fractions **0.006915 / 0.000826**, maxima **0.04298 / 0.03452**; di:4 `ms`–production correlations **0.979 / 0.989**. T8 is qualitatively more synchronization-sensitive. This is not evidence of universally negligible waits at every smaller-team run.

**Diagnosis: inference and limits**

1. Repeated head-of-line amplification is consistent with the record. Median W=10.4776 summed ms across 626 intervals is **16.74 us per interval**, with 402 intervals observed waiting and longest-wait median 101.84 us. It does not require one millisecond-scale freeze: if seven workers waited together, 10.48 core-ms represents about 1.50 ms of collective stall spread over many episodes. The unfinished producer does not literally hold the commit lock: the ticket remains at its interval, and later producers cannot commit or claim more work until their own commit [H1]. A persistently slower worker could repeatedly throttle the group. No retained per-thread/per-interval trace proves which worker did so.
2. Placement/SMT sharing, descheduling, frequency/power variation and page-fault costs can all increase P and then amplify W. T8-only severity and process-specific modes make placement a useful hypothesis, not a finding. No CPU IDs, physical-core mapping, context switches, faults or hardware coherence counters were recorded. Do not turn “625 thread-ID handoffs” into “625 physical-core migrations”: `who` records OpenMP thread numbers [H2].
3. R is 20,024 bytes and different workers repeatedly update overlapping suffixes [H1,H3]. Its coherence migration is plausible. But T8 S=1.396 ms versus pinned T1 S=0.939 does not isolate migration from SMT contention, frequency, preemption or instrument effects. All runs hand off equally, including fast ones. Fast/slow mean service difference contributes only **0.044 ms** to the eight-worker capacity accounting, far less than the 2.10 ms mode gap. Saving the whole 0.457 ms T8/T1 service difference yields only about **0.057 ms** in the work-bound term at fixed P, though secondary effects could be larger.
4. Correct the README's first-touch attribution: allocation creates uninitialized scalar storage and pointer rows; producer stores first write the pool [H5]. Consequently pool demand faults can lie INSIDE `i_prod_ns`; the roughly 0.97 ms outside the region is not a measured “pool first-touch time.” A page-fault/pre-touch diagnostic would distinguish this, but moving pre-touch outside the end-to-end timer would falsify the comparison.
5. No evidence identifies acquire/release ordering as the culprit. It is needed to publish R/out; weakening it is incorrect [H1]. Padded claim/ticket avoid their sharing one cache line, not ticket fan-out or R ownership transfer. Bare `__rdtsc()` is neither serialized nor a CPU-time counter; elapsed intervals include descheduling, and service stops BEFORE the release store. Claims, release/visibility costs and instrumentation gaps are not cleanly isolated [H2]. The numbers are useful accounting, not hardware causal counters.

**Bound and ranked salvage options**

Correct the aggregate comparison first: `d:4` end-to-end median **7.9838** and `di:4` region median **7.4059** are from different runs. di:4 end-to-end median is **8.3792**, and median same-run outside time **0.9682**. Median per-run bound `max(S,(P+S)/8)` is **5.8421**; median region/bound is 1.1993, whereas dividing the two medians gives 1.2677. Neither ratio describes one representative run.

More decisive: median same-run **bound + outside = 6.8669 ms**. The baseline median minus 1.5 is **6.8777 ms**: essentially no startup/drain/wait margin remains. Subtracting only W/8 from each di:4 end-to-end gives median **6.9791 ms**, still short of the rule. These counterfactuals hold measured P fixed; they are not predicted runtimes. P is itself regime-dependent: fast di:4 P median is 38.301 versus slow 46.678. Thus 5.84 is a conditional work bound, not a universal floor; fast runs have lower bounds. A successful salvage must stabilize/reduce production cost as well as waits.

| Rank / option | Expected gain / limit against b:4=8.378 and the 1.5 ms rule | Cheapest deciding experiment, proposed only |
|---|---|---|
| **1. Control placement; retain G=4 commit2** | If fast behavior becomes repeatable, observed 6.1–7.0 ms suggests roughly **1.4–2.3 ms** gain against today's unbound baseline. No guarantee: binding may accelerate b too. This is the only cheap lever supported by a whole fast-regime observation. | **0.5–1 day**: native Windows per-worker affinity, verified one worker per distinct physical core, applied identically to input/producer teams in a, a2:4, b:4, d:4 and di:4. Experiment specified below. |
| **2. Dedicated R owner + seven producers, ordered READY queue** | Removes producer waiting-for-commit and R handoffs; it does NOT allow the committer to skip an unfinished predecessor. New capacity bound is **max(P/7,S)**, not (P+S)/8. With P=45.2, region >=6.46, plus ~0.97 outside => **>=7.43**, at most ~0.95 gain before overhead. At fast P=38.3, optimistic total >=6.44, about 1.94 gain. Producer throughput must improve materially; merely restoring S to T1 cannot buy the rule. | **1–2 days** standalone: seven workers publish completed intervals with release/acquire flags and immediately claim more; one fixed worker owns R/out. Sweep bounded lookahead, measure producer progress, ready backlog and committer starvation; preserve all pool stores. Kill if measured P/7 + outside already exceeds the contemporaneous target. Remote pool reads and queue cache footprint may erase the benefit. |
| **3. Separate production granularity from G; tune only after placement** | G1 is evidence AGAINST blindly finer commits: S=2.600, waits=0.211, paired gain -0.025. G4/G8 have nearly equal T8 S (1.396/1.406); G8 already halves handoffs but gains only 0.397 [-0.059,+1.496]. No recorded tuning result establishes 1.5. G8's conditional region bound 5.47 leaves more room, but its different measured P is not guaranteed by G. | **0.5–1 day**: keep vector-capable G=4 or 8; decouple small production claims/readiness from ordered group commits, or test G16 only as an additional predeclared arm. Record production variance per interval and complete-group readiness. Finer production alone cannot authorize partial/out-of-order commits. G changes also change the G%4 vector path [H3]. |
| **4. Rank-2 row-owned pull** | Removes ticket and shared R ownership. For each owned row r, pull S(j,r), j<r, then S(r,j), j>=r, all ascending; block adjacent owned rows to vectorize distinct chains. But a stored-pool postpass keeps producer median **6.798**, leaving only **0.080 ms** for the entire consumer to reach 6.878. Generic G35 already takes 9.808 total. Expect no rule-level gain from a simple postpass; direct packed addressing may improve G35, with no measured size for that improvement. The 5.84 fused bound does not apply to this two-stage design. | **1–2 days** packed-address row-block prototype, compared with c and b after the SAME producer. About 50.12 MB logical scalar reads and twice-per-off-diagonal f, not necessarily 50 MB DRAM. Fusing producer+owners requires readiness synchronization or duplicate expensive Matern evaluation; that is a larger design, not a direct G66 port. |
| **5. Overlap only tail work** | Literal ragged tail: G4 has 3 rows/6 cells, G8 7 rows/28 cells; negligible versus 3,133,756 cells, cannot buy 1.5 ms. Even the last 256 canonical rows are only 1.05% of pool work (rough service scale ~0.015 ms). The 0.702 ms reported drain is summed; divided by eight it is ~0.088 ms, not a 0.702 ms serial opportunity. | **<0.5 day** instrument service by interval quantile before any variant. Large output-row ownership would be option 4 with new conflict handling; advancing suffix commits before their prefix is illegal [H3]. |

READY order: never skip to the next ready full interval in the current scatter. Different intervals overlap R suffixes; e.g. with n=2 and k(a,z)=10a+z, row 1 must receive z01=1 then z11=2 (12), not 2 then 1 (21). A queue can let PRODUCERS run ahead; only a changed ownership/dependency schedule can relax global commit order [H1,H3].

**Exact next experiment and rule**

In a separately authorized quiet window, add ONLY a native per-worker affinity option and CPU-ID logging to the standalone harness. Query physical-core topology; do not assume alternating CPU numbers. Use all eight physical cores, one logical CPU each, then repeat with the opposite SMT siblings. Bind the master consistently too. Check affinity API success and entry/exit CPU IDs; retain actual-team reporting. Existing P1_AFFINITY is merely a process mask, ignores API failure and is stripped by run_matrix's environment construction; it is not this experiment [H4,H6]. OMP_PROC_BIND alone is not a demonstrated Windows binding mechanism here.

Use two independently shuffled 21-round blocks, each containing bound and unbound a/a2:4/b:4/d:4/di:4. Retain full timing boundaries, allocation, identities and no outlier deletion. Log start/end wall timestamps; diagnostic di additionally retains interval timestamps/worker/CPU, and faults/context switches where available. Keep instrumentation out of d. Predeclare G4 primary; do not select a winning G afterwards.

Decider: bound d must satisfy the original paired **LOWER 95% CI >=1.5 ms versus bound b**, median wait fraction <0.20, and reproduce the gain in the independent block. If it fails, stop P1 under this acceptance target; the fixed-P bound does not justify another planner project. If only binding succeeds, it establishes an opt-in bound configuration. It does not overturn DROP for the current unbound harness. Compare expert Fortran under the same affinity before claiming parity; the historical 6.21/6.42 numbers are not paired controls for this window.

The rule is defensible as an integration-investment threshold: **17.9%** of b's median, nearly all the measured ~1.71 ms scatter increment, in exchange for a new fused planner/synchronization path. It is aggressive, not an arithmetic impossibility. Recomputed T8 paired medians/CIs (4000 bootstrap resamples, seed 7, median statistic) reproduce G1 **-0.0246 [-0.4761,+0.3841]**, G4 **+0.2557 [-0.1671,+1.6079]**, G8 **+0.3971 [-0.0593,+1.4965]**. The wait test is only a guardrail: 15% waits can pass it while leaving nowhere near enough capacity to meet the speed target. Twenty-one rounds and a mixture make the median CI unstable; an upper CI crossing 1.5 is not evidence of acceptance. The fast mode proves attainability on some executions, not controllability. For this unbound configuration, the observed mixture (13/21 d:4 slow) is the honest expectation. Smaller-team gains are real in this record, but changing the target to T3 is a separate product decision.

**Correctness / integration risk**

- No ordering defect found in commit2 for its stated pure/total, stored rank-2 specialization. Ascending unique claims, own-row production, acquire/release publication, identical group/tail calls, and ONE ascending outer accumulator support the proof [H1,H3]. Keep whole-group boundaries, seeds, operand order, all named pool stores and contraction-off. Progress assumes the predecessor's worker runs; preemption/oversubscription still stalls everybody.
- Rechecked recorded 1176 rows: zero rc failures, one common pool hash, one checksum bit pattern and R hash for b/c/d/di. Separate identity logs report **6075 cases each**, unrestricted and actual-team-limited, zero failures under their comparison contract. NOT arbitrary NaN payload bit equality: each log explicitly reports **171 commit-vs-same-G scatter payload disagreements** among multi-payload witnesses [H7]. No production compiler/LLVM/interpreter integration was tested by this standalone executable. Retain original IR/lane fallbacks and gate exact cross-lane behavior separately.
- P1 moves consumption across a producer/materialization boundary. Admission must audit producer, whole consumer/all inner kernels/seeds, outer kernel/seed, all intervening bindings, dominating captures and callback targets; preserve totality and request policy [H8]. The prototype hardcodes the admitted case, so its identity success supplies no general admission evidence.
- G66 already avoids cross-owner R/H transfer and predecessor liveness dependence, uses disjoint private row state, then a completion barrier; it also implements actual-team-one fallback and executed-form reporting [H9]. Its stored source requires no readiness publication. P1 reopens those synchronization and materialization concerns; a dedicated committer additionally needs pool publication flags. G66's five-party and callback-aware audits are existing implementation precedent, not permissions automatically inherited by fusion [H10].

**Hook table — code claims**

Paths relative to repository root. `P` = `bench/notes/round3/prototypes/p1-commit`; `H` = `src/cpp/blade_orbit_scatter.hpp`. Ranges identify inspected bodies; all line numbers refer to HEAD 32a5a429.

| Hook | File:line | Claim supported |
|---|---|---|
| H1 | P/p1_commit.cpp:188–191,238–291 | Separate padded atomics; group claims; production before wait; pause/yield; ordered group/tail and outer chain; acquire/release; region completion. |
| H2 | P/p1_commit.cpp:193–216,271–288,295–323,443–455 | Bare RDTSC; interval definitions and exclusions; worker-ID handoffs; summed startup/drain; wait denominator. |
| H3 | H:44–45,69–160,163–172 | Packed offsets; suffix writes and completed rows; vector G%4 path; tail sizes/order; serial outer chain. |
| H4 | P/run_matrix.py:41–61,83–102,105–110,154–160,183–201 | Team/environment policy; separate shuffled processes; round/order retention; bootstrap and decision computation. |
| H5 | P/p1_commit.cpp:124–170,374–435; src/cpp/nested_array_utilities.hpp:142–145,185–201,398–452 | Allocation versus pool writes; region/end-to-end boundaries; uninitialized scalar allocation and pointer skeleton. |
| H6 | P/p1_commit.cpp:609–617 | Process-only affinity hook; unchecked result. |
| H7 | P/README.md:68–96; P/identity-T1-3-8.txt:38–39; P/identity-thread-limit-2.txt:38–39 | Identity scope, families, noncommutative witnesses and multi-payload exception. |
| H8 | bench/notes/round3/research/D-orbit-regions.md:96–119,134–157; bench/notes/round3/reviews/codex-review-D.md findings 1–4 | Proposed fusion order/admission/lane contract and earlier review requirements; design, not landed integration. |
| H9 | H:317–348,367–428,438–448 | Pull ownership, duplicate f, private state, readiness-free stored source, actual-team fallback, completion barrier and runtime census. |
| H10 | src/Optimize.fs:4933–4961,4982–5038; src/CodeGenBinding.fs:259–302 | G66 stored-source five-party audit, unknown refusal, callback-aware kernel/seed checks and strict request policy. |
| H11 | P/pairkernel_repro_emit.cpp:280–305; P/pairkernel_repro_rowslots_emit.cpp:297–318; P/p1_commit.cpp:99–109,394–427 | Emitted dynamic producer + serial scatter; G35 canonicalizing row walk/slots; prototype dispatch. |
| H12 | bench/kernels/pairkernel/pairkernel.blade:9,25–43; CLAUDE.md:214–216 | n=2503, Matern producer, comm/omp requests, source fold; row-slots/scatter/pull policy. |

Repository status was clean before and after inspection. Only this assessment file was written.

---

## codex-g69-design

_formerly the scratchpad's `round3/reviews/wave3-codex-reviews.md#codex-g69-design` (appended at the wave-3 records commit)_

VERDICT: CLOSE VIA A — use real lifetime scopes, including repeated kernel/tuple expansions; remove the current unsafe kept-inline arm pending that repair.

Read-only assessment at 32a5a429413d5ae937cfd90a633ba62f39f52950 (2026-10-21). Files and git inspected; no builds, tests, Blade, g++, or measurements run. Test assertions below are inspected coverage, not independently verified results. H-numbers identify current-tip file:line hooks in the final table; historical review locations are not substituted for current source.

**The class**

Required invariant: **every allocation the inlined callee's evaluation performs, in either lane, is freed exactly once at the return-boundary event or by a scope that ends no later, and nothing reads it after release.** “Allocation” means each dynamic allocation instance, not one IR binder or one emitted allocation site. Pool-cache release counts as release of ownership; returning pages to the OS is a separate policy. Owning C++ values such as String also count.

For this scalar-returning, non-writing admission, only evaluated scalar results and leg results may cross the boundary. Borrowed caller inputs must never be freed. If evaluation materializes an external deferred binding and publishes its storage back to the caller, the literal invariant above cannot hold: either establish that storage before entering the region, or decline that inline. Silently exempting it weakens the requested contract.

Code shows an enumeration of top-level IRCompute binders, an evaluation-shape check for other units, and a release of those enumerated owners. Owned-map bodies bypass that check; named fold/map kernels are references, not expression children. Totality does resolve callable bodies, but totality is not allocation freedom. The seventh witness therefore remains admitted under explicit opt-in. [H1–H5]

The event now has a durable owned-pool anchor, appears after reconstruction, is shared per call, and is protected against CSE/reuse. These address listed-pool boundary defects. IRValidate follows callable references and only ONE level of local storage provenance; it proves neither complete ownership nor arbitrary alias liveness. [H6–H8]

**Allocation sources — today's classification**

Legend: **R** = refused by current admission; **O** = owned by the event or demonstrably shorter local cleanup; **M** = missed/unproved by the census. A source can have different classifications depending on where it occurs. “M” is not automatically a demonstrated leak in both lanes.

| C++ source | Current classification and consequence |
|---|---|
| Forced rank-1 dense map output | **O**: its binder is listed; event uses the owning allocation frame or main's dense free. This covers the output, not everything evaluating the map can allocate. [H4,H9] |
| Array parameter/let rename, including inside a scalar branch | **R** when visible in the spine walk. C++ copy versus alias depends on mutability/deferred handling; blindly freeing every array-typed name would risk freeing borrowed inputs. Hidden kernel-body versions are **M** to the census; ordinary called functions can clean their own copies. [H5,H10] |
| String literal/copy, String-valued index label, tuple containing String | **R** in checked units; tags recurse into their inner type. **M** behind unaudited kernels or the owned-map exemption. C++ constructs std::string; ordinary function/block destruction may satisfy the lifetime bound, but IRReleasePools does not destroy arbitrary owning values. [H2,H5,H11] |
| Fold accumulators, B1 lane state, fused scalar joins | Scalar/stack state, not heap pools. An independent omp Path B fold DOES allocate a partial array: **O**, through its explicit delete[] at fold completion, not through the event. Thus “every non-owned unit allocates nothing” is already too strong. [H12,H19] |
| Map/fold kernel and helper execution | **M** to ownership analysis, including owned-map kernels. Ordinary C++ callable wrappers have function cleanup; emitted expressions/materializations require their own registration or local cleanup. Purity cannot substitute for checking those routes. [H3,H13] |
| Tuple-component evaluation | Direct user calls and storage-bearing evaluated components are **R** in checked units. Numeric tuples are values. Calls hidden in a kernel remain **M** to the census; ordinary C++ calls retain their callee scope. [H5,H13] |
| Deferred-map materialization | Local deferred/loop-valued units are **R**. External named operands are not positively proved stored by foldTotal; forcing can allocate. **M** as a blanket allocation claim; not by itself evidence of a new A7c leak. [H3,H10] |
| Scope frames, raw/shaped temporaries | Function scopes own registered pool/raw/shaped allocations, with escape/suppression exceptions. The kept inline creates no equivalent child scope; a hidden allocation registered to the caller need not die at the A7c boundary. Shadow frames are diagnostics, not owners. **M** beyond listed pools. [H9,H13,H14] |

| LLVM source | Current classification and consequence |
|---|---|
| Forced rank-1 dense map output | **O**: allocPool creates storage; event frees a listed pool directly in main or loads/frees/nulls its tracking slot in a function. [H15,H19] |
| Array parameter/let rename | **R** when visible. bindNamed copies a stored IRVar/IRParam using copyArr → allocPool. Hidden inside a kernel/helper expansion: **M**, no corresponding event entry. Views are a different route, not automatically copies. [H5,H16] |
| String construction | Direct String/tagged-String uses are **R** across lanes. LLVM literals are interned globals, not per-evaluation heap strings; other unsupported forms may refuse. This does not rescue the C++ lifetime claim. [H2,H11] |
| Fold temporaries | Ordinary scalar/lane accumulators are stack/SSA state; this lane's ordinary fold is serial, without C++'s Path B partial array. Deferred operand forcing and allocations in the executed kernel remain **M**. [H17,H19] |
| Kernel-body expansion | **M**: applyKernel binds arguments, emits shadow-frame markers and evaluates the body without pushPoolScope/popPoolScope. The seventh witness reaches an allocating tuple helper here. [H17] |
| Tuple-component/helper expansion | Direct IRApp in checked units is **R**; hidden IRApp is **M**. emitTupleParts expands even a scalar-returning call in tuple position, with shadow frames but no pool scope. Scalar-let/arithmetic calls instead can outline and free on return. [H18] |
| Deferred-map materialization | Local deferred lets are **R**; external multiply-read names may materialize. **M** without storage provenance. materializeExpr deliberately exempts an upgraded pre-existing binding from scope cleanup via keepPool; an added scope alone would not close this case. [H17,H20] |
| Existing scopes and repeated allocations | Outlined scalar calls are **O** for recorded allocations at return. Main opens no function pool scope. Kernel/tuple expansions borrow the surrounding scope, if any: **M** for the inline's boundary. One slot per static allocation site also cannot remember several live dynamic instances from repeated kernel evaluation. [H15,H17,H18,H20] |

**Is a census closable?**

Yes, for a closed sublanguage backed by emitter allocation contracts; no, by extending today's shallow list and continuing to trust “builtin” nodes. Callable bodies are present in the IR callable table and can be resolved. The hard dependency is that *native allocation is an emitter effect*: a rename copies in LLVM, tuple position changes its call route, and a fold can force a deferred value or create a locally freed partial array. An IR-only theorem without corresponding lowering lemmas is insufficient. [H3,H16–H20]

A finite transitive effect system could classify evaluation as allocation-free, locally scoped, region-owned, escaping or unknown. That is more than another shapeProblem patch. Scopes put ordinary ownership at the point allocations are emitted, where the actual route is known.

**Four models**

Estimates are engineering judgment: one builder familiar with both lanes, including focused regression work and a review correction pass; no measured schedule.

| Model | Exact change/hooks | Builder-days | Risk / proof boundary |
|---|---|---:|---|
| **A — scope** | Replace A7c's pool-list authority with a structured inline region (or durable paired start/end markers) at buildUnits/reconstruction. Open C++ lexical + allocation scopes and LLVM PoolScopes; close after evaluated return, exporting only scalar results. Add scope support to repeated kernel/tuple expansions. Make CSE, reuse, IRLift and validation respect the region. [H6–H9,H13,H15,H17,H18] | **10–15** | Medium/high implementation risk; strongest reusable ownership model. Must prove dynamic-instance tracking, no escapes and complete allocator/RAII coverage. Merely adding two pool-scope calls does NOT close the class. |
| **B — transitive audit** | Replace ownershipProblem's shallow iterator with a cycle-safe evaluation checker over resolved callable bodies, including owned-map kernels, B1 map/combine, other folds, seeds and deferred definitions. Keep the current event/barriers. [H3–H8] | **4–7** | Smaller change, substantial maintenance risk: each newly admitted primitive/emission route renews the proof. Provable only relative to an explicit backend effect contract. |
| **C — no optimizer inline** | Specialize the callee at the call site with extra leg-state/output parameters; attach producer/leg plans inside that specialized callee. Extend C++ genFuncDefCore/call wrappers and LLVM ensureFunction/emitCall/emitFunctionBody signatures. Keep a real scoped call in both lanes, including tuple context. [H13,H18,H19,H28] | **12–20** | Removes A7c's imported-spine ownership problem, but introduces specialization/ABI/publication/fallback correctness. Preserving today's LLVM tuple expansion would defeat the premise. Existing internal expansion leaks are not automatically fixed. |
| **D — exact restricted shape** | Match only one forced map, canonical B1 fold and a builtin scalar return expression; no additional lets/calls. Inspect the owned map's kernel body too, and recognize B1's identity/square/combine bodies structurally. Retain single-call, totality, storage, route and event guards. [H3–H7,H21] | **2–4** | Low implementation cost, narrow coverage. Closed only when the *bodies*, not just their names/result types, match a finite allocation-free grammar whose lowering is audited. |

**A: what it actually needs**

LLVM already has the stack, tracking slots, conditional null initialization, free-at-pop and escape handling. Its outlined scalar-call path demonstrates the mechanism; it does not demonstrate complete cleanup of arbitrarily repeated in-place expansions. Shadow-frame elision must remain independent of allocation scopes. [H15,H17,H18,H20]

Open the inline scope **before its first evaluated unit**, not at today's first-owned-map anchor: hidden scratch may precede y2. Use durable region identity, unaffected by dropped first/last joins or CSE. Keep an evaluated scalar return and published leg values outside the lexical block; input-dependent joins can be reconstructed outside only when their values no longer depend on local storage. Clear CSE availability at BOTH boundaries, prevent storage equivalences/reuse crossing them, and preserve the region through later normalization. [H6–H8]

C++ needs actual braces as well as pushAllocScope: a tracking frame alone does not end std::string lifetime. Reuse genFuncBody's ownership/temporary-registration conventions, without its function-return control flow. Do not apply both the old listed-pool free and the new region pop to the same owner. [H9,H11,H13]

LLVM additionally needs:
- Scope wrappers around scalar/allocation-free-result applyKernel and emitTupleParts expansions, so each dynamic invocation frees its own scratch. An outer scope alone leaves one repeatedly overwritten slot: earlier iteration allocations disappear from the census. Empty scopes should emit no runtime code. [H15,H17,H18]
- Explicit export/ownership transfer for any supported storage-bearing inner result; otherwise decline the A7c specialization before commitment. Do not use the existing unknown-provenance “drop scope without frees” fallback as proof of closure. [H18]
- Positive storage provenance for external dependencies, or pre-materialization outside the region when separately proved legal. keepPool's blanket escape for older bindings is not compliance with this task's invariant. [H20]
- Retirement/restoration of local array/tuple/scalar/deferred bindings at scope exit; no dangling cached emitter value may serve a later expansion. Validate region escapes transitively, rather than relying on check 5's one-level scan. [H8,H16,H18]

Fast-path cost, inferred: C++ lexical/compile-time frames need no runtime allocator registry. LLVM adds initialization/store/load/free for the one producer pool, outside the cell loop; function callers already have much of that tracking. Empty per-kernel scopes add no runtime work. Allocating callbacks acquire real per-invocation cleanup, a necessary cost; a dynamic heap log at every scalar kernel application would be an avoidable design. Preserve the current strict chain/lane-state emission. Any net speed claim needs new measurement: changed cleanup and register allocation can change timings and licensed vectorization.

**B: when it would be sound**

Use visiting/verified states keyed by resolved callable plus relevant argument/storage facts. Reject unresolved targets and cycles; do not treat “visited” as proof. Resolve every named map/fold kernel, including those of owned maps. In the conservative version, encountering any user IRApp, array-valued binder/alias, String evaluation or unknown operation fails closed; resolving a helper for diagnostics does not license it. A more permissive helper summary is a separate proof.

Require stored operands, inspect captures/deferred definitions and every evaluated child, and run/preserve the certificate across IRLift and forceCallableBody. Native effects for the remaining arithmetic, extents, scalar tuples and fold routes must be documented: either no heap allocation or explicit shorter cleanup. Path B may be certified locally scoped or declined. Route-independent allocation freedom avoids dependence on whether LLVM outlines a user call, precisely because such a call is refused. It still depends on how the accepted IR primitives lower. This is a viable conservative repair, not an IR-alone proof. [H3,H5,H12,H17,H22]

**C: the ABI and contracts**

A compiler-generated private variant receives original arguments, dominating leg seeds/captures and caller-owned output-state pointers (or C++ references). Kernel identities, operand positions, extents, licences and lane widths are specialization facts, not per-cell function pointers. Keep accumulators local to the callee's producer loop and copy final scalar results to the outputs before return; passing pointers must not force a memory load/store per cell.

LLVM can keep its scalar return and add writable pointer out-parameters; it need not invent a general tuple-return ABI. Give those outputs an explicit ABI category: they must not inherit the existing array-parameter readonly attributes. [H28] emitTupleParts must call this specialized function rather than expand it. The original entry point remains for other callers. An emitter decline must either select the original call plus joined pass, or a fully defined fallback variant; never read uninitialized output slots. [H18,H19]

The same seed bits, ascending update sequence and operand order preserve strict/repro+contract-off identity. Licensed C++ state can retain feed_sum/finish_sum; LLVM retains each leg's own flags. No new promise of licensed bit identity or universal 1e-12 accuracy follows. Publish/consume at the original join point; retain the existing totality, dominance, no-write and thread refusals. [H19,H23]

This fits “the fastest way is the only way”: compiler-derived interprocedural fusion without source annotations. That slogan does not decide between A and C or prove either faster. C is attractive for a broader interprocedural fusion project; too much ABI machinery solely to repair this kept-inline lifetime class. The preserved outlined scope avoids *new* A7c retention, but absolute cleanup still requires addressing internal repeated expansions or restricting them.

**D: does it cover the target?**

Yes for the serial bench kernel at saxpy.blade:25 (one map, one canonical B1, return s); its input generators are outside that callee. Keep the existing mt8 refusal. “No kernels beyond the map's own” must exempt the structurally recognized B1 square/identity and builtin combine, or it rejects saxpy itself. The producer's own body must be restricted too: merely permitting its name reproduces the owned-map hole. [H4,H21]

The ten emitted-equality witnesses remain covered **as refusals**: 036/037/039/040/041/042/044/045/046/048. Keep the one-call-per-statement guard for 039. They are not ten additional admitted shapes. D loses positive admissions in 030/031/032/033/035/038; their tests must explicitly expect refusal while preserving values, rather than claiming the old routes still execute. 034's scalar return arithmetic can remain admitted. 043 must continue to distinguish independent W1-D inlining. [H24,H25]

**Recommendation, proof obligation and witnesses**

Choose **A**, with dynamic invocation scopes, not a scope-shaped wrapper around the existing census. The deciding argument: the emitter knows that an allocation happened; the optimizer should not need to predict every hidden allocation in executable user bodies. Keep current scalar-return/no-write/totality restrictions initially. Scope support must not silently widen admission to unknown escapes.

Review the following exact obligation: for every admitted region R, every backend route and every dynamic execution reaching R's exit, each allocation made by evaluating R has a unique owner in R or a child scope, is released once after its last read and no later than R's exit, and cannot be observed through any exported alias/deferred value. Borrowed external storage retains its original owner. A repeated allocation site must satisfy this per iteration. Scalar return and leg publication precede exit; the next caller statement follows it. No normalization, CSE, reuse or fallback may cross those ownership boundaries. Caller-after-return failures must observe cleanup already complete; potentially failing callees remain declined.

Required regression matrix, retaining counterexamples from all seven looks:
- Look 1: indirect seed/kernel captures and before/after-producer dominance (028), actual fusion/fallback route and failure pins (026/029), plus lifetime-matched scalar-call and tuple-call baselines.
- Look 2: inner join before later pool read and before B1, including actual emitter decline (030); two producers/plans, one call, one cleanup (031).
- Look 3: dropped final join/earlier atom return (032); equal caller map plus duplicate return with CSE (033); function caller failure after non-atom return (034); reuse ownership (035).
- Look 4: direct alias in main/failing driver (036/037), dropped first join/first scalar CSE (038), two calls in one statement (039), direct/deferred/tuple/mut-argument validator negatives.
- Look 5: nested conditional alias (040/041), long String (042), independent W1-D retained inline (043).
- Look 6: tuple-position helper, failing caller and scalar-let route (044/045/046); tagged String predicate plus 047's earlier refusal; String hidden in projection (048).
- Look 7: ADD the recorded Aux fold → step → tuple extra → array rename, both main and scalar-let failing driver; repeat with direct alias/String in step and in the owned-map kernel. Require actual admission for supported scope cases, or a specific refusal. Vary loop trips and branch outcomes: all dynamic copies, not just the last slot, must be accounted for.
- Scope-specific additions: allocation before y2; nested scopes and two sequential invocations; zero-trip/conditional allocation; local storage returned through an inner tuple; external deferred materialization used again after return; a newly added allocating builtin; reuse/cache on/off. Unsupported escape cases must fail closed.

Use allocation-origin/owner/release assertions and live-byte checks **at the return boundary**, in both lanes; add LLVM runtime allocation accounting. Existing annotated-free scans only validate listed pools. Existing ASan census covers C++ 030/031/032/033/035, not LLVM or the ten decline witnesses. A clean exit census alone misses delayed frees and repeated allocation losses. Preserve strict raw-bit and complete fault/site/frame twins. Keep LLVM refusal equality distinct from emitted-module equality for 047. [H23–H26]

**Meanwhile / default safety**

Remove the A7c **kept-inline opt-in arm** until A meets that obligation; preserve in-region legs and independent W1-D behavior. Do not remove the whole feature.

**The current unset default is SAFE with respect to G69's added lifetime hazard.** The guard rejects inline-needing A7c plans before legInlines/event creation; producers already in-region still qualify. The default test asserts saxpy emits identically to gate-off in C++ and LLVM, no event exists, and 019's in-region case still applies. This is source/test inspection, not a new execution result, and does not certify pre-existing W1-D retention or LLVM expansion behavior. [H1,H6,H24]

**The explicit opt-in path is not lifetime-safe as a class.** The known current witness demonstrates omitted/delayed ownership, not a newly executed use-after-free finding. If retained for research despite the recommendation, YES: emit a normal stderr diagnostic whenever the kept inline is actually retained, including emitter fallback, stating “experimental G69 inline: incomplete lifetime safety; callee allocations may survive return.” A blade plan record alone is inadequate. Keep the explicit mirror and experimental labeling; do not present its old timing gains as default-path or lifetime-matched evidence. No warning is needed for the safe no-inline default.

**Current-tip hook table**

| ID | Code claim / evidence | File:line |
|---|---|---|
| H1 | Explicit switch, incomplete-audit text, optimizer gate/mirror; inline-only default guard | src/Optimize.fs:180, :186, :187, :200, :211, :7706 |
| H2 | Allocation-free value types; tag recursion; String/Nat excluded | src/Optimize.fs:951, :954, :957, :960 |
| H3 | Totality resolves calls; map/fold admission; stored-variable fold test lacks positive storage provenance; scalar-return inliner | src/Optimize.fs:6759, :6781, :6793, :6828, :6855, :6896, :6908, :6923, :6937 |
| H4 | Pool census selects IRCompute; owned map exemption skips evaluation walk | src/Optimize.fs:7639, :7644, :7734, :7802 |
| H5 | Other-call refusal, String/leaf/type tests; expression walker does not resolve IRVar bodies | src/Optimize.fs:7722, :7761, :7775, :7781, :7790; src/IR.fs:2242, :2326, :2665 |
| H6 | Call-keyed ownership; independent W1-D keep set; durable anchor and rebuilt event placement | src/Optimize.fs:7727, :7822, :7829, :7840, :7846, :7904 |
| H7 | CSE pins/clears; named pools excluded from donor/reuser candidates | src/Optimize.fs:641, :652, :1778, :1793, :1804 |
| H8 | One-level local provenance, callable-reference closure and spine validation; statement classification | src/IRValidate.fs:440, :447, :459, :470, :484, :498; src/IRLift.fs:87; src/IR.fs:290 |
| H9 | C++ event dispatch; owning-frame search/suppression or main fallback | src/CodeGenBinding.fs:336, :346; src/CodeGenLoopNest.fs:4976, :4981, :4989 |
| H10 | C++ deferred alias forcing/copy policy differs from blind freeing | src/CodeGenBinding.fs:6656, :6664, :6670, :6782 |
| H11 | C++ owning strings; LLVM interned constant strings | src/CodeGenExprSupport.fs:562, :595; src/EmitLlvm.fs:1362, :1375 |
| H12 | C++ Path B partial allocation and local deletion | src/CodeGenBinding.fs:4264, :4286, :4297 |
| H13 | C++ function scope, temporary hoists, allocator registration and cleanup APIs | src/CodeGen.fs:1092, :1154, :1163, :1166; src/CodeGenLoopNest.fs:4690, :4707, :4739, :4901, :4920, :4945 |
| H14 | Diagnostic frames distinct from ownership | src/CodeGen.fs:1170; src/EmitLlvm.fs:1383, :1398, :1405 |
| H15 | LLVM existing stack/slots, allocation registration, event free/null | src/EmitLlvm.fs:879, :913, :1318, :1339, :2061, :4143, :4153 |
| H16 | LLVM stored rename copy, virtual versus stored copy behavior | src/EmitLlvm.fs:4267, :4274, :5002, :5007 |
| H17 | No scope in counted loop/applyKernel; multiply-read fold materialization; actual kernel execution | src/EmitLlvm.fs:1627, :1641, :6029, :6037, :6713, :6722, :7795 |
| H18 | LLVM scalar call ABI/outlining versus tuple expansion; outlined return scope/free; unknown array-return provenance fallback | src/EmitLlvm.fs:3771, :3797, :3802, :3852, :3858, :7808, :7843, :7860, :8007, :8033, :8051 |
| H19 | Existing producer leg attachment/state/feed/flags; join publication | src/CodeGenCuda.fs:2214; src/CodeGenLoopNest.fs:2584, :2596, :2603, :2610; src/CodeGenBinding.fs:458; src/EmitLlvm.fs:4232, :4848, :4878, :4917, :4930 |
| H20 | LLVM escape/null behavior and older-binding materialization escape | src/EmitLlvm.fs:1327, :3834, :3840, :5899, :5941, :5950 |
| H21 | Actual bench shape; historical design/required cleanup brief; research | bench/kernels/saxpy/saxpy.blade:25; bench/notes/round3/briefs/wave2-builder-briefs.md:278, :393, :425; bench/notes/round3/research/A-census.md:222 |
| H22 | Optimizer phase order and post-rewrite lifting/forcing/CSE/reuse; interpreter event no-op | src/Optimize.fs:7979, :7981, :7984; src/Lowering.fs:3136, :3140, :3163, :3172, :3173; src/Interp/Core.fs:1048 |
| H23 | Raw-bit versus fixture-only licensed tolerance; both-lane fault twins | tests/CellEpilogueTests.fs:508, :543, :553, :582, :610, :656 |
| H24 | Ten equality/route witnesses; W1-D distinction; default guard test; 047 refusal distinction | tests/OptimizeTests.fs:2804, :2844, :2851, :2865, :2882, :2918, :2953, :2965 |
| H25 | Positive lifetime witness bodies and regression assertions | tests/corpus/sibling-folds/030_input_legs_join_inside_inlined_callee.blade:17; tests/corpus/sibling-folds/031_input_legs_two_plans_one_inlined_call.blade:20; tests/corpus/sibling-folds/032_input_legs_unused_final_join_atom_return.blade:19; tests/corpus/sibling-folds/033_input_legs_cse_barrier_function_caller.blade:18; tests/corpus/sibling-folds/034_input_legs_function_caller_failure_after_return.blade:12; tests/corpus/sibling-folds/035_input_legs_scratch_reuse_inside_callee.blade:15; tests/corpus/sibling-folds/038_input_legs_durable_region_start.blade:20; tests/OptimizeTests.fs:2712, :3011, :3045, :3077, :3128 |
| H26 | Annotated-pool scans, slot nulling, free-before-fault; limited C++ ASan census | tests/OptimizeTests.fs:2515, :2591, :2611, :2628, :2664, :2670; tests/CellEpilogueTests.fs:671, :692, :704, :725 |
| H27 | Current known gap; seven historical looks (claims/evidence, not rerun results) | bench/notes/gap-register.md:119; bench/notes/round3/reviews/wave2-codex-reviews.md:314, :360, :412, :538, :606, :674, :758; CLAUDE.md:203, :206 |
| H28 | C++ function signatures/call wrappers; LLVM function signature and array-parameter attributes | src/CodeGen.fs:1419, :1426, :1433; src/CodeGenExprSupport.fs:1159, :1164, :1175; src/EmitLlvm.fs:7904, :7963, :7967 |
