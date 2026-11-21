# Streaming regions and out-of-core intermediates

Status: PLANNED, 2026-10-21, source snapshot `667250cd`. Companion to the
[60-family peakmem plan](peakmem-microkernel-plan.md). This adds 18 composed pipeline
experiments and an optimizer/runtime design; it does not claim implementation,
benchmark results, or new Blade syntax. Source anchors must be refreshed before work.

Reviewed by Claude on 2026-10-21. The [review and dispositions](peakmem-streaming-review-claude.md)
record four stage prerequisites and four refinements, incorporated below. This is design
review, not validation of an implemented streaming or spill route.

See the [implementation sketchbook](peakmem-sketches/README.md) for the proposed region
analysis/runtime, bounded loops, spools and all 18 composed schedules, with a separate
static second look. No sketch has been compiled or executed.

The target is to remove entire intermediate arrays across computation boundaries, then
bound the storage of intermediates that must survive a barrier. Reading a file in chunks
is only the entrance to this problem. A chain that immediately constructs a full grouped,
normalized, transposed, or mapped array can still consume memory proportional to the
entire dataset.

The proposed sequence is **consumer-driven region analysis in `Optimize.fs`, narrow
terminal fusion, bounded internal blocks, safe replay, checked private spools, then
external blocking operators**. Keep all of this derived from types, access patterns,
demands and kernel contracts. Source authors should continue declaring their computation;
they should not hand-author block loops or annotate individual kernels with memory knobs.

This extends the original plan's disk-staging scope: out-of-core intermediates are now
explicitly a design target. The proposal is a declared toolchain resource policy, with
memory and scratch-storage limits and visible plan decisions. No spill policy is changed
by this document, and no experiment should wait for an actual allocation failure before
deciding how to execute.

## 1 Current implementation: useful pieces and real boundaries

| Evidence in this snapshot | What it provides | What remains missing |
|---|---|---|
| `Optimize.optimizeModule`, `src/Optimize.fs:5913` | Constant-match, elementwise, slice-write and row-fold fusion; invariant hoisting; sibling-fold sharing | No joint region storage/schedule selection across these transformations |
| `recordSegmentStreaming`, `src/Optimize.fs:374–484` | Consumer-shape reporting before those rewrites | An applied report is not evidence of whole-region fusion, bounded outputs, or a verified provider capability |
| `IRReduceCompute`, `src/IR.fs:197`; streamed fold at `src/CodeGenBinding.fs:3807` | Existing terminal semantics and a direct rank-1 streamed fold with one uninterrupted scalar chain | These are separate emission paths: composed producers and joined fold states need a new block-consumer path; the current fused fiber path rejects rank-1 elementwise consumption |
| `src/CodeGenCuda.fs:3456–3512`, 3993–4068 | Ordinary C++ apply streaming, despite the filename: one rank-1 halo source or rank-1/2 elementwise sources | Full output remains allocated; rank 2 uses a full-height row-pointer table; run offsets form a full table; block choice starts with the first source |
| `genGroupByBinding`, `src/CodeGenBinding.fs:2902`, 2962, 2991 | Stream into structural groups without a separate flat input | Full grouped payload remains resident |
| `src/IR.fs:1696`, 1726; `src/CodeGenState.fs:1582` | Special streamed provider reads and named-array writes; BL7004 for unsupported stream value escapes | General internal block/spool representation, random-window protocol, and chunk sink lifecycle |
| `src/providers/ZarrProvider.fs:2609`; `src/ProviderStatics.fs:76` | Zarr fiber/block/window hooks; NetCDF fiber hooks | NetCDF block/window hooks are absent here; CSV and Icechunk lack stream hooks (`CsvProvider.fs:530`, `IcechunkProvider.fs:2836`) |
| `src/CodeGenBinding.fs:2147`; `src/providers/ZarrProvider.fs:2294–2312` | Dense writes can alias the existing pool, avoiding a flatten copy | Writer still receives a whole array; current Zarr writer emits one whole-array chunk |
| `src/CodeGenBinding.fs:2065` | Full nonfinite scan before provider writes, BL8015 | Chunked publication must preserve validation/failure behavior, not expose a partially validated output |
| `src/EmitLlvm.fs:7468`; `src/Interp/Run.fs:363`, 569 | Explicit provider limitations | LLVM rejects provider I/O; interpreter rejects streamed reads and writes. Initial provider experiments are C++ only |
| `src/CodeGenTiles.fs:8`, 26 | Persistent revision cache with useful identity/publication precedents | Current route retains materialized output and excludes computed inputs/reductions; it is not a mandatory-intermediate store |

The [earlier reduction-streaming study](../../docs/plans/structural/03-streaming-reductions.md)
already established bounded pairwise idioms and landed specific fixes. Its general summary
feature remained deferred. Do not reopen that feature merely to implement this plan:
first compare the best current deferred row/reduction-join spelling with the natural
spelling and inspect emitted storage. Its historical measurements are not current results.

## 2 The optimizer should plan regions, not isolated array allocations

### 2.1 Two analysis seams, one final decision

Current lowering order is explicit at `src/Lowering.fs:3131–3185`:

```text
lowerArrayBinOps
  -> Optimize.optimizeModule
  -> IRLift.liftInlineFormsModule
  -> forceCallableBody (functions)
  -> Optimize.cseModule
  -> planPoolReuse / planHotRowFolds / planCompactionFusion / planCompactRowClasses
```

Discover producer recipes, explicit materialization boundaries and possible regions while
high-level applies, reductions and combinators are still recognizable. Retain origin
metadata through lifting and forcing. Compiler-inserted forcing and an observable snapshot
must remain distinguishable; do not simply delete every `IRCompute` around a candidate.

After CSE, rebuild demand, aliases, intervals and backend capabilities on the actual IR.
Select a final region before allocation and producer/consumer side tables are populated.
The first implementation should annotate a narrow stable IR rather than run a second broad
rewriter after those tables. If a later version rewrites IR, regenerate all affected tables
and analyses. Cached decisions keyed by eliminated or rebound IDs are invalid.

Early discovery is advisory: it cannot authorize codegen to skip a binding. Final plans
carry a compilation generation, region ownership and producer/consumer handshakes. All
members accept a schedule together. If any member cannot emit it, select a complete
compatible fallback before emitting partial declarations. A fallback that exceeds an
enforced budget is a resource-policy refusal, not a successful bounded execution.

Do not independently let pool reuse, hot-row folding, compaction and a new region planner
claim the same storage. A final plan must either consume their compatible subplans or mark
those nodes owned; legacy passes retain non-region nodes. Reset state per compilation
alongside the existing tables at `Lowering.fs:2955–2960`. Repeated compiler calls in one
process need tests, including different environment settings and backend choices.

For S3, make that ownership explicit with one proposed `RegionOwned : Set<IRId>` shared
by the four legacy planners. Exclude claimed producers, consumers, hidden payloads and
their storage-root dependency closure from incompatible candidates, including pool-reuse
donors crossing the region boundary. A top-level node check alone is insufficient. The
set and associated root facts reset with the other tables; emission asserts that no
claimed allocation is also controlled by a conflicting legacy plan. This is coordinated
work across the planners, not just a new side table.

Keep S1 smaller: admit only regions proven disjoint from all four planners' candidates
and assert that none of their table entries claim those nodes or roots. If that leaves
no profitable admission beyond existing fusion, complete the census and defer the change
to S3 rather than quietly broadening S1 into a five-pass integration.

`Blade.fsproj:144–151` compiles `IRTotality`, then `Optimize`, then `ProviderRegistry`.
Optimize therefore cannot directly call the later registry. Supply capability facts in
lane-neutral IR metadata, or inject a capability description from Lowering. Do not couple
optimizer decisions to C++ emitter functions. Any new F# file needs an explicit dependency-
ordered project entry; do not split an existing atomic recursion chain to make room.

### 2.2 Proposed internal plan record

This is a design sketch, not existing IR or a public API:

| Fact | Required content |
|---|---|
| Region identity | Producer/consumer IDs, source spans, generation, backend, excluded observers and fallback |
| Logical values | Element type, units, named axes, extents, compact/sparse/ragged layout, evaluation origin |
| Complete demand | Scalar terminal, required resident array, escape/capture, print, sink, type/extent/side-table use, random access |
| Access | Sequential order, per-axis traversal, bounded lookback/lookahead, affine window, repeated scan or arbitrary lookup |
| Storage roots | Owners, views, versions, physical capacity, last borrower, initialization/complete-write obligations |
| Producer recipe | Deferred operations, invariants, exact arithmetic graph, captures and their snapshot versions |
| Scheduling proof | Effects, totality, seed timing, ordered validations, fold licences, callback closure, required failure ordering |
| Provider contract | Supported reads, chunk geometry, decode/validation unit, snapshot identity, replay and writer publication capabilities |
| Chosen storage | Inline value, borrowed block, bounded shared block, replay recipe, resident materialization, private spool |
| Resource bound | Allocation/metadata/codec/worker/queue expressions, disk high water, unknown terms, minimum feasible reservation |
| Evidence | Applied/declined reason, producer emission acknowledgement, measurement coverage and byte-model assumptions |

Reuse the strongest existing analysis ingredients: `bindingPayload` and `intervalPayloadOf`
(`Optimize.fs:1461`, 1478), callback propagation (`:1262`), and the compaction demand roots
including embedded extents and foreign references (`:3084–3156`). A plain expression-tree
walk misses provider source IDs, masks, RNG operands and other hidden consumers.

`scheduleMayWriteIn` (`:1508`) is a write predicate, not a totality or snapshot certificate.
Current row-fold fusion explicitly permits some first-failure reordering (`:4440–4472`).
The new region admission must require total operations or a specific ordered-failure proof;
it cannot claim the current fusion predicates already establish this stronger contract.
Existing policy discrepancies deserve separate tracked tests/decisions, not a silent
reinterpretation of existing behavior in the memory campaign.

### 2.3 Candidate selection

1. Start at demanded terminals and propagate their access windows backward. Join compatible
   demands; split at escapes, mutations, unsupported access or validation/publication barriers.
2. Enumerate a small bounded set of legal storage choices per boundary. Scalar fusion is
   best when reachable; a reusable block is needed only where batching/halo/ownership needs it.
3. Schedule in dependency order, tracking actual overlapping storage roots, per-worker
   state and queues. Account for shared producers once, but keep their blocks until their
   last borrower releases them.
4. Reject choices whose bound is unknown under a hard budget. Use checked/saturating shape
   arithmetic: an overflowed byte estimate is never a cheap plan.
5. Compare time, logical/physical traffic, peak tracked memory and disk use. Keep the
   nondominated alternatives; choose the fastest validated legal candidate within the
   declared budget. Until timing evidence exists, report the choice as a heuristic.
6. Emit a reason for every retained full intermediate and every refused stream edge.

This is a bounded search, not a globally optimal scheduling claim. Start with one function
and one region; later interprocedural summaries must represent effects and storage escapes.
Do not let module CSE or a guessed selectivity silently turn a local bound into a global one.

## 3 High-level computation opportunities in Optimize.fs

These are proposed admissions, not a claim that every natural spelling currently allocates.
The first deliverable for each is an emission census against its best existing idiom.

| Opportunity | Region-level decision | Key constraint and existing foothold |
|---|---|---|
| Terminal deforestation | Carry `map -> map -> partial/full fold` through compiler-inserted arrays; independent scalar states per demanded row/fold | Preserve axes, seed timing and each strict chain; use `IRReduceCompute`, `planHotRowFolds` and the partial-fold rewrite before inventing new terminals |
| Memory-aware CSE | Compare materialize-once, share one block, or repeat a cheap producer near each consumer | CSE at `Optimize.fs:1557–1743` can lengthen lifetimes. Recoverable recipes need immutable captures and repeatable total evaluation; disabling all CSE is not the proposal |
| Shared fanout | Feed several statistics and required outputs from the same produced block | A single declared external read differs from two independent reads. Current sibling sharing rejects streams; removing that guard alone is unsafe |
| Selection to terminals | Fuse predicate, stable selection, maps and folds; omit private masks, positions and values together | Named masks/keys, captured arrays and extent dependencies remain real demands; empty-fold behavior and stable order survive |
| Structural group regions | Read/produce one segment, run its row computation, consume it or write final destination directly | Remove full group and ungroup pools only when private; segmentation, original-axis identity and group order remain intact |
| Keyed aggregate regions | Push maps/projections before group payload formation; retain only needed state | Reuse W3-1 owner; bounded key domain is distinct from unbounded discovered keys. Strict folds do not permit partial-state merging |
| Join to terminal | Avoid resident joined pairs when only a terminal is demanded | Enumerate the same match pairs in the same order, including duplicates; no distributive sum shortcut. Reuse W3-2 owner and add spill only later |
| Layout propagation | Push permitted transpose/projection/compact access into consumers; fuse stack/ungroup stores into final destinations | Preserve snapshot timing and logical fold order. A source mutation can forbid replacing a copied transpose with a view |
| Statistic barrier | For `producer -> statistic -> transform`, retain a row, replay a recipe, or spool its values | Preserve the written two-pass graph; online variance/softmax is a separate algorithm. Global statistics are genuine barriers |
| Multi-stage halos | Propagate backward demand through `stencil -> map -> stencil`; retain only needed intermediate bands | Compose boundary rules and reach per stage, not only the final halo; temporal dependence may need a ring/slab and PM33 ownership |
| Provider demand pushdown | Restrict columns/windows and decode directly into blocks or final destination | Skipped cells can contain required missing-value/format failures. Projection legality needs provider validation facts, not only data-use facts |
| Generated and structured inputs | Keep ranges, addressed RNG and structural domains virtual through consumers | Preserve RNG parameter checks and logical addresses; do not replace sequenced draws, densify structural domains, or assume every backend supports `_at` |

Computation reduction is often more valuable than spilling. A fold over an unused full
pair matrix should first use admitted terminal composition; an external transpose should
first try legal strided consumption. Conversely, required matrix values, unknown library
calls and arbitrary repeated lookup are legitimate materialization demands. Operator-action
substitutions such as `gram_apply` remain explicit source/algorithm choices when they change
the arithmetic graph, as in the parent plan.

## 4 Intensive streaming: make each stage bounded and efficient

### 4.1 A block is a borrowed logical window

Introduce a small internal descriptor containing backing storage, logical origin, valid
shape, strides/layout, read-only/owned status and lifetime token. Access translates logical
coordinates into this window. Avoid full-axis row pointer skeletons, full run tables and
synthetic shifted pointers as the new general representation. The descriptor is not an
ordinary escaping `Array<T>`; calls requiring that ABI force a proven compatible resident
boundary or decline. Never let a deferred capture outlive a borrowed block.

Regular runs should use arithmetic cursors. Irregular segment/index metadata may itself
require a paged or sequential representation; account for it explicitly. If one preexisting
input index is already resident, report that resident requirement separately rather than
claiming constant total memory. Compiler memory and emitted-code size must also be measured
when a source with many chunks could generate a huge literal schedule.

### 4.2 Scheduling, chunk alignment and overlap

Begin with a synchronous pull pipeline. The terminal requests a logical window; upstream
nodes supply only the needed values. Multiple synchronized consumers borrow a shared block
until all release it. A branch that needs a global summary cannot hold an unbounded queue
while another branch runs; retain, replay, spool or split the region explicitly.

For multiple input streams, choose windows from their logical coordinate mapping and
physical chunk boundaries. Do not use the first provider's chunk size as a universal answer,
or compute an enormous least-common-multiple block. Compare intersection windows, bounded
decoded-chunk reuse and deliberate rechunking. Zip and outer product remain different
computations; matching extents alone never licenses co-iteration.

Then study bounded double buffering and prefetch. Reserve **bytes**, including pending
compressed fetches, decoded buffers and conversion scratch, before issuing work. Bound both
queue entries and bytes. Preserve consumption order even if reads complete out of order;
reorder-buffer capacity counts against the same budget. Earlier speculative errors must
not become newly visible before the logical earlier operation. Cancellation drains or
cancels all outstanding work and releases their reservations.

Tune chunk/work-block sizes and concurrency jointly. Sweep decode amplification, useful
bytes per read, scheduling overhead, cache residency and worker saturation. Fewer workers
can leave room for larger reusable tiles. Separate storage fetch size, codec decode unit,
compute block and output block: insisting they be identical can waste I/O or memory.
Do not promise sub-chunk memory if a codec must decode a whole chunk.

### 4.3 Strict folds and validating producers

A strict fold carries its accumulator continuously across block boundaries. It does not
sum each block and then sum those totals. Each reduction-join leg keeps its own seed,
operator, association and licence. A partial fold has one chain per output cell; retain
enough states or traverse an admissible tile order, with their bytes included.

An eager producer may validate every input before the consumer begins. Interleaving a
consumer that can fail with that read can change the first diagnostic. Total consumer
kernels are the narrow starting point, but totality does not make early printing or writing
safe. Defer externally visible results until required validation completes. If the region
cannot preserve the failure sequence, materialize, add a proven validation stage, or refuse.
A validation prepass is itself another read and needs snapshot/replay evidence.

For S1–S4, moved/interleaved compute kernels and seeds must pass a concrete purity/totality
admission, reusing `IRTotality.fs` where applicable. Provider I/O may still fail and keeps
its prescribed validation order; it is not declared total by this gate. A general
ordered-failure proof is a deferred research facility, not an analysis provided by
`IRTotality` or `scheduleMayWriteIn`. Do not use that alternative to admit v1 callbacks
that the totality check declines. Revisit it only with a separately implemented proof
and tests, no earlier than the relevant later-stage admission.

## 5 Barriers: choose replay, private spooling, or an external operator

### 5.1 Safe replay is a semantic contract

A replay recipe records captures and source versions, validation placement, evaluation
counts where observable, FP configuration and addressed RNG identity. Stored immutable
inputs plus total pure operations are the first case. External sources require a real
immutable snapshot or equally strong provider guarantee. Size/mtime in the run record is
not content identity (`src/cpp/blade_run_record.hpp:19`). Holding a path or handle alone
does not prove the contents cannot change.

Compare replay cost with retention and spool traffic. For a value stream of S bytes,
one uncompressed full spool write plus one full read adds about 2S logical payload bytes,
before metadata, filesystem effects and extra passes. Replay may avoid those bytes but
repeat expensive arithmetic or provider decoding. Keep those mechanisms visible in the
report, and never assume a warm OS cache eliminates the storage capacity requirement.

### 5.2 First disk prototype: one private sequential spool

Use a private computed row/stream needed after its statistic is known: produce values once,
update the prescribed statistic, append exact values to the spool, then replay those bytes
in the original order through the transform and a scalar terminal. This preserves the
two-pass arithmetic graph without requiring a public incremental writer, provided the
producer and statistic are pure/total or have a specific ordered-failure proof. Interleaving
their evaluation can otherwise let an early statistic failure overtake a later producer
failure; apply section 4.3's gate here too. S4 uses the pure/total case only; the proof
alternative is deferred. Start with fixed-width scalar elements and a single writer/reader;
no compression or shared random access.

Required runtime contract:

- A declared scratch root, per-run private directory, tracked memory allowance, disk quota
  and bounded file-handle count. These are proposed toolchain settings, not existing flags.
- A versioned format with element representation, shape/index descriptor, block sequence,
  logical origin, lengths and integrity checks. Serialize values, never process pointers.
- Checked reads, short writes, close/flush results, truncation and checksum failures. Do
  not treat a mandatory intermediate like a disposable revision cache entry: current tile
  cache store failure may be ignored (`src/cpp/blade_tilecache.hpp:224`).
- Reserve disk capacity for overlapping live generations and metadata before growth;
  filesystem free-space checks alone are not reservations. Bound append buffers and
  catalogs. Fixed-width regular blocks should use arithmetic offsets, not an in-memory
  per-element index. Variable-length formats need a separately bounded index design.
- Delete owned scratch on normal completion, runtime failure and cancellation. Existing
  provider paths can call `std::exit`, so C++ destructors alone are insufficient. Use an
  explicit cleanup/exit path and an ownership manifest. Register normal-exit cleanup and,
  if used, a distinct quick-exit handler. `abort`, immediate termination, forced process
  kill and host failure can bypass cleanup; they may leave owned scratch for recovery.
- Hold an exclusive ownership lock for each private run directory and record a run nonce,
  owner PID and process-start identity. Recovery must acquire exclusive ownership and
  establish that the recorded owner is dead before deleting; PID alone is vulnerable to
  reuse. If ownership/liveness is uncertain, retain the directory. Never infer staleness
  merely from age, or sweep arbitrary temporary files. Test a live concurrent run and
  PID-reuse/stale-manifest scenarios. Harness private working directories do not replace
  this rule for a shared configured scratch root.
- State durability honestly: scratch need not survive a machine crash, but a successful
  replay must check its data. Resource-limit and I/O failures need deliberate diagnostics
  and tests; this plan does not assign new BL codes.

The initial planner chooses a bounded strategy before execution. A later hybrid may switch
from resident pages to its prevalidated spill route at a budget reservation threshold,
before allocation, with a reserved migration buffer and unchanged logical state. This is
different from catching OOM and trying a new algorithm. Disable disk routes when the policy
does not grant scratch storage; refuse an unmet hard budget explicitly.

### 5.3 Blocking operators need individual algorithms

| Barrier | Proposed route | Proof/resource obligation |
|---|---|---|
| Global/row normalization | Retain one fitting row; otherwise replay or sequential spool between statistic and transform | Exact written pass structure and nonfinite/empty behavior; an enormous single row is not a bounded-state exemption |
| Transpose/rechunk | Write bounded indexed tiles, then consume in required order | Preserve index identity and per-cell values; count partial output tiles and both old/new generations. Try direct strided consumption first |
| Stable sort | Bounded run generation, bounded-fan-in merge, original ordinals for ties | Pure total key/comparator, proven ordering domain and existing tie/NaN contract; handle limits, merge-buffer reservation; global permutation is still output when demanded |
| Dynamic groups | Stable partitioned raw rows, followed by per-key ordered evaluation; separately licensed partial-state combination | Per-key original row order, seed timing, output group order. One skewed partition can still exceed memory; use a bounded sort/merge fallback or refuse |
| Join to aggregate | Partition or externally order sides, enumerate same pairs, restore required pair order before strict fold | Duplicate-heavy many-to-many output, stable match order, skew, ordinal storage and bounded reorder mechanism. No presumed algebraic shortcut |
| Tensor contraction stages | Stream aligned slabs; spool/reblock a private intermediate when subsequent access disagrees | Same contraction ordering and per-cell sum graph; working set includes all live operands and accumulators. Reuse N09/PM31 owner |
| AD tape/checkpoints | Spill prescribed primal blocks and fetch in reverse-use order; compare separately with replay | Gradient accumulation order and caller-owned buffers, checkpoint reuse, replay workspace and failures; PM40/59 own AD semantics |

A stable partitioner does not by itself preserve the observable order of all group or join
results. Carry ordinals and show a bounded restoration schedule. Pre-aggregating partitions
changes strict floating-point association; it is allowed only under the relevant licence.
External sort changes key/comparator invocation counts and order, so totality alone is
insufficient when those callbacks mutate, print or read external state. Start with proven
ordered keys; ordinals do not repair an invalid comparison relation for NaNs. Preserve a
defined current contract or refuse that route until its ordering semantics are established.
Before ST14 admission, locate and pin the supported ordering, ties and NaN behavior in
the canonical semantics and corpus. Current C++ emission calls `std::stable_sort` with
`key(a) < key(b)` (`CodeGenExpr.fs:2504–2505`); that alone establishes no total NaN order.
Undefined or unpinned cases are not a valid reference oracle. Keep them outside the
external route until an explicit semantic decision and matching lane tests land.
If one group's state itself grows with the group, spilling input rows alone is insufficient.
Recursive repartitioning needs a progress criterion, bounded depth/handles and a skew
fallback. Do not label these blockers solved by the presence of a generic spool API.

External operator architectures offer useful precedent: DuckDB separates streaming
execution from spilling stateful intermediates, and its external aggregation work uses a
spillable page representation. Those ideas motivate explicit storage accounting here;
they do not establish Blade's FP, stable-order or failure guarantees.
[Memory management](https://www.duckdb.org/2024/07/09/memory-management),
[external aggregation paper](https://duckdb.org/library/robust-external-hash-aggregation/).

### 5.4 Public sinks are a separate boundary

A chunk sink requires begin/stage/finalize/abort capabilities and an explicit visibility
contract. The existing writer's whole-array nonfinite validation must still finish before
public output appears. Staging private chunks and publishing a manifest after successful
validation is a candidate only for providers with a proven suitable commit operation;
multi-file stores and overwrite behavior need their own design. Do not assume a directory
rename is an atomic portable transaction or make every provider inherit one provider's rule.

A private spool can therefore precede sink support. A returned ordinary array still needs
the representation required by its ABI. Printing requires the complete snapshot and its
publication order, not inherently resident storage: a later admitted printer could read a
validated spool. The initial implementation retains existing resident printing; changing
that requires a separate proof and timing endpoints that include the entire print/replay.
An external output is a different task contract unless the language already expresses
that sink and all its semantics are kept. Unknown selected cardinality also requires final
shape/metadata handling before publication.

## 6 Resource model and measurement

For a fixed plan, use a conservative overlapping-allocation bound, avoiding double-counting
borrowed aliases of the same root:

```text
Mtracked <= Mmandatory_resident + Mshared_state + Mmetadata + Mcache_retained
            + sum(active_workers: input_blocks + intermediate_blocks + halos
                                  + accumulators + decode/encode_scratch)
            + Mqueued_compressed + Mqueued_decoded + Mreorder + Mmigration

Dpeak = max_t(sum(live_spools + staged_outputs + merge_generations + disk_metadata))
```

All terms are bytes, with capacities rather than just occupied lengths. Codec memory must
have a supported bound; otherwise mark that portion unknown and do not claim hard-budget
coverage. Memory-mapped file backing, reserved address space, allocator retention and OS
page cache require separate reporting. A small working set is not proof of little backing
storage. The enforceable first budget should name **tracked runtime allocations**; it is
not automatically a cap on process RSS, total machine memory or filesystem cache.

Use one execution-wide reservation ledger across overlapping regions, workers, providers
and retained pools. Giving each region or worker the entire allowance independently is
not a budget. Include required resident inputs/outputs in the declared coverage and reserve
minimum progress/cleanup buffers so a fully occupied budget cannot deadlock a consumer
waiting for the block needed to release another block.

For S3's fixed synchronous schedule pi, derive a sufficient admission threshold by
walking its allocation/release transitions:

```text
Bmin(pi) = max_k bytes(LiveRoots(k) union NextStepAllocations(k) union CleanupReserve)
```

Count root capacities once, including pinned fanout blocks, metadata, codec scratch,
retained cache and any required resident values. Reserve all allocations for the next
step before starting it; the schedule must reach a release without waiting on an
unscheduled consumer that also needs memory. Reject a budget below this sufficient bound
before entering the region. This is a bound for the chosen schedule, not a universal
minimum or a graph min-cut theorem. Concurrent schedules must extend the accounting to
all admitted in-flight steps and prove progress before S6 enables them. Add sub-minimum
budget tests that refuse promptly instead of hanging.

The ledger belongs to an explicit execution context, initialized for a program invocation
and shared by its nested regions/workers. Tear it down after tasks and scratch cleanup
finish; do not reuse stale reservations in a later invocation in the same process. Current
benchmark subprocesses naturally isolate runs, but repeated entry into a runtime must
also be tested for reset, balanced reservations and policy-governed retained caches.

For a pipeline over N values with fixed block capacity B, worker count P and fixed halo
geometry H, extra tracked storage should be independent of N only if metadata and terminal
state are also bounded. A required N-cell result adds its full bytes. A G-group result may
require O(G) resident storage. A width-W row fold may require O(W) state. A full block index
can grow as O(N/B); a full row-pointer table as O(N). Spell out these terms, not a bare
"O(B) memory" claim.

Report time, useful input/output bytes, modeled memory traffic, provider bytes fetched,
decoded bytes, spill bytes written/read, disk high water, active block/queue bytes and
replay counts. Distinguish storage cache hits from decoded-cache hits and physical I/O
from logical file reads. Instrumented diagnostics and uninstrumented timings remain
separate, following the parent campaign. Include setup, complete validation, sink commit
and required cleanup in a defined end-to-end measurement; also expose phase times.

At fixed block shape, increase odd total extents and number of chunks independently. At
fixed dataset size, sweep block shape, workers, codec, source chunk alignment and consumer
lag. Test the budget boundary with deterministic small allowances before attempting
larger-than-RAM datasets. All timed A/B runs remain interleaved, serialized with the speed
campaign, and use medians/confidence intervals. A failed/OOM baseline is a capacity result,
not an infinite speedup. Fresh process does not mean cold filesystem cache.

## 7 Eighteen composed experiments

`ST` IDs identify pipeline experiments composed from the 60 `PM` families, not 18 unrelated
microkernel owners. Arrows below are conceptual dataflow, not proposed Blade syntax. Every
case gets terminal versus resident-output contracts where relevant.

| ID | Pipeline and proposed experiment | Storage target / decisive counterexample | PM links |
|---|---|---|---|
| ST01 | Stored input -> several private maps -> seeded scalar fold | No full map intermediate; compare best existing fused idiom. Required print/capture keeps arrays alive | 01, 09, 10 |
| ST02 | Rank-1 Zarr stream -> maps -> sum/sumsq/count | Fixed chunk/codec storage plus exact fold states; maps total, no early publication. Failing callback must decline if diagnostic order changes | 11, 41, 43 |
| ST03 | Differently chunked streams -> zip -> selected statistics | Bounded aligned windows and decode reuse; unequal axes/extents and second-source failures are barriers | 19, 41, 44 |
| ST04 | Expensive producer -> synchronized terminal branches | Produce each cell once, share borrowed blocks; a branch with unbounded lag forces another strategy | 05, 11, 41 |
| ST05 | Cheap/expensive producer -> distant consumers | Compare CSE materialization, total replay, block share and spool; captured source mutation forbids replay | 01, 02, 05, 59 |
| ST06 | Stream -> mask -> compound -> map -> folds | Eliminate private values/positions/mask; demanded keys/index/selection output retain corresponding storage | 17, 18, 19, 56 |
| ST07 | Structural group -> row map -> row fold -> outer fold | Remove grouped payload; ordered outer chain, max-group state. One huge group and escaped group view expose false bounds | 10, 15, 41, 57 |
| ST08 | Stream -> keyed grouping -> aggregates | Compare bounded-domain state with explicit external dynamic keys; all distinct and one heavily skewed key | 20, 21 |
| ST09 | Producer -> row/global statistics -> normalized terminal | Retain, replay or spool the identical two-pass values; total row size may exceed budget, online algorithm kept separate | 12, 13, 46 |
| ST10 | Stream -> halo -> map -> halo -> terminal | Composed demand and bounded intermediate bands; halo wider than block, boundary cells and incompatible stage traversals | 36, 37, 42 |
| ST11 | Multi-file/rechunked streams -> projection -> group terminal | Bound decoded cache and metadata across files; validation of skipped columns/chunks can prevent pushdown | 15, 41, 44, 48 |
| ST12 | Layout change -> contraction/fold | View/consumer propagation versus external tiled transpose; required transpose and mutated source prevent elision | 25, 26, 27, 31 |
| ST13 | Tensor mode-product chain -> terminal/required final tensor | Aligned slab fusion versus spooled intermediate; preserve contraction order and include unavoidable final tensor | 31, 43 |
| ST14 | Stable sort -> projection/group terminal | External runs feeding terminal, bounded merge fan-in; equal keys, NaNs, demanded permutation, huge variable records | 16, 21 |
| ST15 | Join -> predicate -> strict aggregate | Avoid resident match array but retain same pair sequence; duplicate explosion, skew and order restoration | 22, 23, 24 |
| ST16 | Producer/selection/pairwise tiles -> provider sink | Bounded staging and final publication; resident-output control, unknown selected extent, late nonfinite/write failure | 29, 30, 43, 45 |
| ST17 | Recurrence/AD -> selected snapshots or tape -> consumer | Rings/slabs plus explicit tape spool/replay, not full accidental history; caller cotangent and failure ordering | 33–40, 59 |
| ST18 | Indexed RNG -> stochastic map -> repeated terminals | Address-preserving replay versus stored/spooled tape; sequenced RNG and unsupported lanes refuse | 40, 47, 48 |

## 8 Implementation sequence and acceptance

| Stage | Bounded deliverable | Completion gate |
|---|---|---|
| S0 Census and contracts | Read-only region graph, current capability matrix, full-intermediate reasons, resource ledger | Explain selected examples' actual emitted allocations including metadata; no claim of optimization |
| S1 Resident terminal prototype | ST01, one private pure/total region, reuse terminal semantics, atomic fallback | Demonstrate a missing admission beyond best current idiom or record no-gain and move on; exact opt-on/off semantics and storage evidence |
| S2 Provider terminal prototype | ST02, one local uncompressed dense rank-1 Zarr source; connect total scalar map recipes and separately typed joined-fold states in a new synchronous block-consumer emitter | Assert that the new route actually emitted and ran; no full map arrays or per-chunk schedule table; input-size scaling flat in tracked workspace |
| S3 Region composition | ST03/04/06/07/10/11, bounded descriptors, shared blocks, accesses and two-phase analysis | Negative escapes/effects/failures; sum queue/worker/metadata bytes; coordinate W2/W3 and PM33 owners; gate ST11 on provider capability facts |
| S4 Private spool | ST09, one fixed-width sequential intermediate, explicit budget/temp policy | Assert spool route taken; exact replay, integrity/quota/cleanup faults, bounded metadata, writer/reader ownership lifecycle, disk high-water accounting |
| S5 Wider external operators and sinks | ST08/12–17, individually admitted barriers | Assert each external route taken; pin sort ordering/ties/NaNs before ST14 admission; skew fallback, bounded merge/partition metadata, provider-specific commit semantics; no blanket out-of-core claim |
| S6 Throughput and policy | ST05; prefetch/double buffering, tile choices, cache and worker allocation, memory-aware CSE | Legal alternatives measured on a common time/memory/traffic frontier; default selection based on evidence |

S0–S2 can start before the speed campaign's unbuilt later waves. S3's shared-selection,
grouping and join variants consume their landed owners' work; they do not create competing
passes. S4 does not depend on a complete query engine, a public stream type or every
provider. S6 is listed last to establish a correct bounded baseline; narrow throughput
experiments may overlap once their own prerequisites pass.

ST10's temporal/recursive variants and ST17's bounded recurrence-history variants wait
for the shared PM33/N01 port to land and pass its gates. Pure spatial halo composition
does not inherently depend on that port; independent non-recursive tape experiments
retain their own AD prerequisites. Recheck ownership/status before dispatch; this plan
does not establish whether another session has since assigned the shared port.

ST18 is a gated S4/S5 follow-on after PM47/N08 establishes addressed-RNG reachability in
the selected lane; unavailable RNG support stays an explicit prerequisite. Every ST case
therefore has a stage or a named gate, rather than an implied independent implementation.

For S1/S2 the implementation brief should require: source-reachable cases; a nonzero-seed
strict fold; odd tails and zero/empty semantics; depth and input-size scaling; allocation
and emitted-store audits; explicit backend refusals; mutation/escape/capture negatives;
and paired failures whose first diagnostic would differ under illegal interleaving.
Multiple statistics retain each original chain, not one invented combined algebra.
Pin identical print selection and demanded outputs for the streamed and materialized
references; a `.read` baseline that also prints the entire input is a different contract.

S2 specifically needs one arithmetic block loop using `GenStreamRows`, composed total
scalar producers inside it, and independent typed accumulators/seeds for each fold leg.
The existing direct streamed fold does not connect those pieces. Current reduction joins
use `streamedNestSetup` (`CodeGenBinding.fs:5961`) and the fused fiber binding path
(`CodeGenFusion.fs:405`), which rejects rank-1 elementwise consumption
(`CodeGenLoopNest.fs:601`). Reusing the ordinary map segment wrapper unchanged would
retain the full result and run table. A successful materialized fallback is useful for
correctness but cannot satisfy S2's route or storage gate.

For tree rewrites, use optimizer-on/off differential gates as well as interpreter/backend
comparison. Both lanes consume the optimized tree, so interpreter agreement alone can
agree on the same bug (`Lowering.fs:3124–3130`, `tests/OptDiff.fs`). For provider-only routes,
compare against a materialized `.read` reference and independent expected results; do not
mark the unsupported interpreter `.stream` route as covered. Run relevant provider suites
standalone and record skips. No tests or benchmarks were run for this planning-only change.

For S4/S5, require an emitted-route assertion and runtime evidence that spool/external
work actually occurred, alongside small materialized-reference outputs and independent
pins. `tests/OptDiff.fs:35–36` explicitly allows identical C++ as an optimizer-inert pass;
that cannot prove an optimization was exercised. The initial provider-backed spool routes
are C++ only. An interpreter may validate a supported materialized reference, but it does
not exercise the spill execution, its cleanup, or its faults. Route assertions and
fault-injection gates are mandatory even when ordinary differential output agrees.

For S4/S5 also inject disk-full, quota, short-write, truncation, corrupt block, missing
snapshot, cancellation and late-provider-failure cases. Check final-output visibility,
owned scratch cleanup and recovery after forced process exit. Include an early statistic
failure paired with a later producer failure to reject illegal spool interleaving. Exercise one huge group,
all-distinct keys, duplicate-heavy joins, a slow fanout branch and a codec whose decode
unit exceeds the proposed block. A resident required output exceeding budget must be
reported honestly, not quietly changed into a handle or checksum.

## 9 Review and open decisions

The original [Claude review](peakmem-microkernel-review-claude.md) covers the parent plan;
the subsequent [streaming review](peakmem-streaming-review-claude.md) covers this extension.
Claude found no hard blocker, with four prerequisites to resolve before their relevant
stages. The caller verified and incorporated the findings, narrowing recommendations
where a blanket dependency or generic progress formula would overstate the evidence.
Two independent repository investigations also informed the design, including the S2
emission gap. The graft symbol index did not cover these F#/C++ paths, so targeted source
inspection supplied the anchors. Failed session-resumption attempts were followed by a
successful fresh Claude review; no bridge configuration was changed.

Decisions to settle with the first prototypes: the exact tracked-budget scope and reserve;
how materialization origin is retained through lowering; the minimal lane-neutral provider
capability record; the deterministic candidate cost rule; scratch ownership/diagnostics;
and each sink's commit semantics. No choice here requires changing Blade's declarative
iteration surface. The immediate target is a compiler explanation and one provably bounded
terminal pipeline; disk-backed blockers follow with explicit contracts and their own gates.
