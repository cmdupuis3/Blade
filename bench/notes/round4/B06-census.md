# B06 source/design checkpoint: C03 capacity-hook coverage

2026-10-22. **Design only; no build, emission, compilation, execution or performance claim.**
Owned edit: this document only, in `b-measurement`, HEAD `b02ac335`. B05 remains frozen.
C03 source checkout is `c-terminals` at `12a9d632`; accepted integration checkpoints are
`6fbe765b` and `302405bf`. Runtime source was read from integration (HEAD `cbcb0089`
at this census). Graft had no indexed src definitions; targeted inventory/reads followed.

**Answer: B03 can account for this C++ fixture's input pool, but cannot measure the
selection-capacity saving against its fair unshared baseline.** The baseline mask,
selected backing and index object use direct new expressions outside blade_pool.
Reading the existing API would predict the same covered `6*sizeof(double)` in both
arms, with all removed storage unmeasured by that ledger. This is useful coverage-gap
evidence, not a zero saving, a measured whole-kernel peak or a reason to change allocation
policy. No instrumentation is executed by this card.

Enabling hooks makes allocation events observable and can change native DCE. Any future
snapshot measures **the newly instrumented/adapted executable's API-accounted capacity**.
It cannot certify that the old uninstrumented native executable materialized a buffer.
The six constant cells are particularly vulnerable to constant folding/store elimination.
Counter snapshots, emitter comments and allocation markers never replace native evidence.

## Inventory and the fair comparison

Inventoried absolute root:
`C:/Users/cdupu/Documents/GitHub/Blade/.round4-worktrees/c-terminals/bench/.build/C03-native-runner`.
The relevant actual paths are:

- `547-on-on-cpp/runtime-emitted.cpp`, plus its run stdout/stderr/provenance and matching
  CLI emit.cpp. This is the C03 selected-pair fold plus sibling sharing.
- `547-unshared-on-cpp/emit.cpp`, emit provenance/stderr. That directory contains only
  these three emission files: no native run/executable/disassembly is recorded there.
  Do not call this fair baseline an already executed native C03 arm.
- `547-on-on-plan/plan.json` and `547-unshared-on-plan/plan.json` bind selected-terminal,
  sibling-fold and existing positions-elision decisions to the same corpus source.
- `547-on-on-llvm/runtime-emitted.ll` and `547-unshared-on-llvm/emit.ll` establish the
  separate LLVM allocation route; they are not B03-covered capacity measurements.

Corpus 547 stores x=[-2,0,1,3,-1,2], selects v>0, then independently folds selected
[1,3,2] with seeds 7 and -2. Independent results are sum=7+1+3+2=13 and decimal chain
-2 -> -19 -> -187 -> -1868. Printing is result-only. keep/y are private temporaries,
not required printed array outputs. Both original fold graphs and seed positions remain
fixed; do not change demand to print keep/y, which would invalidate storage elision.

The fair baseline is **BLADE_COMPACTION_FUSION=unshared, BLADE_SIBLING_FOLDS=on**. It
already elides positions. The fully disabled arm restores positions and is not the
memory saving attributable to C03. ON/ON joins two guarded chains, without a proven
single native source load or single predicate evaluation. Do not infer either from
source comments or sharing a loop.

## Exact C++ allocation/ownership census

Line anchors below refer to the inventoried emitted C++ or current integration headers.

| Object / path | Emitted source and B03 coverage |
|---|---|
| Required source input x | Both sources call allocate<promote<double,1>,nullptr> with extent 6. `nested_array_utilities.hpp:537..545` counts six leaves then calls blade_pool::alloc<double>(6). Rank-one build_skeleton (`:507`) returns the pool slice; it allocates no pointer skeleton. B03 covers this typed API allocation. |
| ON/ON tuple result | Two scalar accumulators seeded 7/-2 in one six-cell guarded traversal, followed by a std::tuple<double,double>. No mask, compact backing or compound index construction appears. Tuple/locals are not heap capacity counted by B03. |
| Baseline keep | emit.cpp:206 uses **new bool[6]**, then stores all six predicate values and counts m=3. It bypasses blade_pool and B03 entirely. This is a Bool array, not std::vector<bool> bit storage. |
| Baseline selected backing | emit.cpp:220 uses **new double[keep__count+1]**, i.e. four element slots. Branchless gathering exposes only the first three selected values. Capacity is four slots (32 bytes if double is 8); do not equate three logical values with four requested slots or assert every slack byte was touched. Not B03-covered. |
| Baseline compound index | emit.cpp:229 uses **new compound_index_t<1>(...,positions_elided)**. Its object allocation bypasses B03. `index_types.h:444` sets rank_to_tuple.p=nullptr, n=m, cardinality=m: there is no position buffer. |
| Index/string/container detail | `abstract_idx_t<1>` embeds std::string name, size/cardinality and virtual behavior; rank-one compound embeds extent, positions_t and a flag. sizeof(compound_index_t<1>) includes object layout/padding/vptr/member objects, not separate possible string capacity or allocator slack. The rank-one specialization is not the generic vector/hash-map representation; do not invent map/vector heap allocations merely because their headers are included. Actual std::string storage/SSO and temporary/library allocations remain untracked. |
| Cleanup/escape | Array and Compound wrappers are non-owning (`nested_array_types.hpp:16`, `:137`); they have no automatic payload free. Neither inventoried main calls release/deallocate/delete/flush. x and baseline mask/backing/index remain unreleased in this emitted main until process reclamation. The result does not transfer ownership or logically close those allocations. |

The source-side, **unmeasured** baseline extra requested element capacity is
`6*sizeof(bool) + 4*sizeof(double)`, plus `sizeof(compound_index_t<1>)` for the index
object and any separate string/runtime allocation. With sizeof(bool)=1 and
sizeof(double)=8 the two raw arrays request 38 bytes; these sizeof values must be
recorded by a future diagnostic, not silently assumed or called heap usable bytes.
No ratio of covered bytes to total memory is available. The static x__init[6] copy and
extent arrays are image/static metadata, not additional blade_pool heap allocations.

The current compound teardown helpers also use direct delete[]/delete (utilities
`:846`); replacing baseline allocations with blade_pool calls would change policy,
cache eligibility, event coverage and potentially failure/release behavior. That is
not a phase-reading adapter and is not proposed or authorized here.

## What the accepted ledger can and cannot say

With BLADE_POOL_CAPACITY_DIAGNOSTICS=1, alloc<double>(6) enters Operation and native_new
even though it is below the cache threshold. `bytes_for<T>` records requested element
slots times sizeof(T), not new[] cookies, alignment slack, malloc usable capacity or
resident/commit pages. A successful quiescent post-input snapshot should therefore have:

| Within-API quantity | Source-derived expectation in **both** arms, before main return |
|---|---|
| Active / retained / combined bytes | 6*sizeof(double) / 0 / 6*sizeof(double) |
| Active / retained blocks | 1 / 0 |
| Component/combined peaks | peak active=peak combined=6*sizeof(double), peak retained=0; never add component peaks |
| Successful alloc/native wrapper attempts/successes | 1 / 1 / 1; cumulative successful requested bytes=6*sizeof(double) |
| Cache hit/retain/direct delete/evict/retry/failure/flush counts | 0, absent unexpected extra API activity |
| In-flight / validity | zero API/native in-flight at the endpoint, valid within the B03 scope |

These are predictions to verify, not executed results. Use monotonic sequence and
combined-peak witness conservation, C=A+R and block conservation. If any counter is
unexpected, investigate runtime/provenance; do not reset or subtract it away. The legacy
cache Stats fresh count is not the allocation count: this ineligible small input can
have legacy fresh=0 while B03 records one successful API allocation.

The default cache is 16 KiB..4 MiB, 16 slots/8 MiB per thread. Forty-eight requested
input bytes on the expected ABI are below the threshold, and there is no release anyway.
Thus retained=0 is expected, not evidence that all kernel storage was freed. Do not
flush or fabricate input release to make active capacity close to zero. Above-threshold
allocation would still remain active until an actual release; cache eligibility alone
does not prove retention or reuse.

Raw new allocations are outside B03's stated scope, so their existence does **not**
automatically set its sticky invalid flag. A valid snapshot means within-API bookkeeping
is coherent; it does not mean complete kernel allocation coverage. Unknown-size releases
through instrumented bypasses still invalidate that scope as specified by B03. Report
coverage=partial separately from numeric validity. Missing/invalid values are null,
never a synthesized zero. No pointer registry, ledger reset or new native failure seam.

LLVM calls blade_alloc_cells directly: ON/ON has one (6,8) input call; the unshared
baseline has (6,8), (6,1) mask and (m+1,8) selected backing. No positions call appears.
LLVM's scalar/cardinality representation does not allocate the C++ compound object.
B03 does not cover this allocator or shim cache. Linking a C++ snapshot reader beside
LLVM would not make it cover those calls; a zero ledger would be misleading. LLVM
capacity stays unmeasured/null in this proposal.

## One bounded diagnostic adapter, if root approves a next card

Purpose: verify the **coverage limitation and common-pool endpoint**, not quantify the
removed raw allocations. Copy the two hash-pinned C++ artifacts into a new absolute
private B06 directory; preserve originals, all computation/allocation/release statements,
fold graphs and print demand. Add only fixed, source-aware phase calls plus a small
snapshot reporter. Reject a hash, template, phase anchor, type/root or graph mismatch.
No re-emission, compiler/CLI/harness change, pool rerouting, global new interception,
default-policy override, extra required array output or B05 timing boundary is implied.

Compile manually against a **verified source-tree runtime bundle**, including the B03
helper, with a recorded include graph. B03 remains source-tree-only: the ordinary
deployment/project list does not establish that the helper was shipped for an enabled
generated compile. The saved ON C++ build's nested_array_utilities.hpp and index_types.h
hashes match the integration copies below, which is a useful starting check, not
permission to ignore all other runtime-header identities. Avoid adjacent stale headers
winning quoted includes; compile copies in a clean private directory with the frozen
bundle. Hash every actual header, macro/command and executable.

Exactly three proposed capture phases:

1. **entry:** inside main's existing failure context before x allocation/fill. Read the
   initial ledger; do not create/reset it. Unexpected prior API work is a diagnostic
   refusal to use the simple predictions, not zeroed noise.
2. **after_input:** after the original x copy and before mask/selected work or guarded
   folds. Scope is the common input pool endpoint, not proof every payload store survived
   native optimization.
3. **after_result_before_original_stop:** immediately after complete result construction.
   A separate native reporter consumes both scalar result values by value as a receipt,
   captures capacity_snapshot_v1, then formats the copied POD state outside its lock.
   Keep result-only semantic stdout; report diagnostics on a separate channel. Do not
   retain payload pointers in a global registry. Entry/input result receipts are absent,
   not fake zero results. No atexit/TLS-order endpoint and no artificial cleanup/flush.

Report sizeof(double), sizeof(bool), sizeof(compound_index_t<1>) and the default policy
beside snapshot scope. Raw mask/backing/index capacities remain **unmeasured**; a separate
source-request model is labelled as such, not added to the observed API peak. Report
reporter/static-metadata storage separately. stdio, run-record strings, std containers,
heap metadata and observer allocations outside blade_pool remain uncovered.

## Instrumentation perturbation and targeted request

The macro and phase receipts are observable work. They may preserve allocation events,
stop DCE or change scheduling; the reporter itself can force result computation sooner.
API native-success counters are wrapper-event evidence, not proof a native new[] call
or payload store exists in machine code. The original six-cell literal may fold to
constants. Therefore:

- Inspect **the exact newly instrumented executable** for actual input allocation/stores
  and dataflow, plus raw mask/selected/index allocation/stores in the baseline; map all
  native elimination/forwarding honestly. A compaction comment, IR allocation call,
  ledger increment or marker alone is insufficient.
- Initial bounded request after implementation review: two adapted C++ arms (ON/ON and
  fair unshared/on), each enabled and disabled-hook mirror, repro/contract-off, one
  thread, default cache policy: four fresh diagnostic processes maximum. No LLVM
  capacity run, retention arm, broad size sweep or performance run. Pure fixture tests
  cover full coverage labels, null/invalid/missing data, common-byte predictions,
  conservation and provenance. Sole Luna owns every compilation/execution.
- Enabled cases must preserve result (13,-1868) and only assert within-API predictions
  when quiescent/valid; disabled cases explicitly report metrics unavailable, not zero.
  Keep actual source/runtime/compiler/flags/executable/output/audit identities separate.
- Compare native enabled/disabled materialization before inferring even unchanged
  allocation behavior between those mirrors. Phase calls also change observability, so
  their agreement alone does not certify the old **unadapted** executable. Any transfer
  to uninstrumented native memory needs its own original-shape native build/audit and
  exact correspondence evidence. This census requests no such inference or measurement.
- Failure/refusal gates: missing helper/wrong runtime, mismatched artifact/template,
  changed demand/graph, unexpected API activity or invalidity, non-quiescent endpoint,
  missing native store/dataflow evidence, missing disabled mirror for a correspondence
  claim, or an attempt to score raw-new/LLVM memory from the API ledger. Preserve
  unmeasured/partial status instead of forcing equality or claiming zero total saving.

No odd-size source is necessary to establish this decisive coverage gap: scaling it
would leave the same raw new paths untracked. No new input is authored. Also, blindly
repeating the decimal-chain fixture to thousands of positive selected cells overflows
its arithmetic and is not a sound cache control. A later specifically approved cache
question could use one odd source with only the same three positive selections and
independently derived finite pins, but size alone would still not produce a release/
reuse event in this main. It is not part of this proposal.

All existing timer lines remain diagnostic and performance-ineligible; the adapter
adds work inside them and B05 enclosure remains unimplemented. Whole-process resident/
commit/fault observations, if retained by the runner, include startup/reporting/teardown
and cannot estimate this missing capacity. No RSS subtraction, total-allocation peak,
retention reduction, DRAM traffic or bandwidth claim follows.

## Frozen census identities

Artifact paths below are relative to the inventoried C03-native-runner root; runtime
paths are relative to integration. Raw working-tree SHA-256, no execution.

| file | SHA-256 |
|---|---|
| 547-on-on-cpp/runtime-emitted.cpp | D1315E1B131D6E14FAE4AFFA37E59C9241E4AE836765C8276B085EB5B32E9E7C |
| 547-unshared-on-cpp/emit.cpp | 76C0EBFB6F68088B63F33AAD96F9688C12630283FEAE98E2B05BAC1E82FE1A89 |
| 547-on-on-plan/plan.json | AB38105429AB76972AA4FC4F0A909647CEFEE5B75C1A46CF566AFB4F45A4C8E9 |
| 547-unshared-on-plan/plan.json | F072DD3B5CCF4736C3B7E3E72D7C25D67FC10D7AE952AEA4AE12B7489CCF91BC |
| 547-on-on-llvm/runtime-emitted.ll | 2CD2281D5E626F94EB8271BF3875DE6338A12C7031CB4222200D7AEB2DEFDB4E |
| 547-unshared-on-llvm/emit.ll | AA27782FF155306C9EF8542B33B91815CF468AD907FC31883778DE409D61FBF7 |
| c-terminals/tests/corpus/index-types/547_selected_tuple_two_seeded_chains.blade | A757B2A709410CFF750BA515AE9BF57A0B97B315848FEEF13BC181A2D9BDE9FD |
| src/cpp/blade_pool_capacity.hpp | 4533447AF2766437DB20A332A26CDB3FBA78EFB818D6A7386D340E4EC4B761D5 |
| src/cpp/nested_array_utilities.hpp | FA12D552F27D4757C73B764E384A3F883510BFDBE15655DA352CEE4E32146DB9 |
| src/cpp/nested_array_types.hpp | 350695E1C2206E4661B6414D6986328248F5A10ED49297E4A63323C80948AC12 |
| src/cpp/index_types.h | 8B0276185FCC73C61C4019223491D398F85442D4EE62A53E08629F345952C49F |

The original saved ON build's nested_array_utilities.hpp and index_types.h have the
same hashes, read from its kept build directory recorded in run.provenance.txt. This
does not make a new instrumented executable identical to that historical binary.
Stop here for root review: no adapter, input, build, native audit or runtime capacity
measurement has been implemented/executed by this source/design card.
