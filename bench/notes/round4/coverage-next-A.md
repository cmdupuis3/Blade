# Round-4 coverage crosswalk and next A card

Planning snapshot, 2026-10-22, integration source `85fc91bad63433298ce50c06915056ffc42c8dc8`. Read the [campaign ledger](README.md), [queue](TEST-QUEUE.md), [PM catalog](../peakmem-microkernel-plan.md) and [ST catalog](../peakmem-streaming-optimizer-plan.md), plus current card reviews/results. Graft returned no F# planPoolReuse hits; targeted source inspection supplied the lifetime anchors below. This note is the only authored file. No execution, production/test edit or Git mutation. Full-suite launches remain **0**.

This is family-level accounting of **bounded variants**, not a completion declaration. I = implemented and targeted-tested variant; E = evidenced existing route/diagnostic control; P = prerequisite, refusal/census or active validation; U = no scoped Round-4 result identified. An E/O label in the original plan identifies existing capability/another owner, not new Round-4 acceptance. Latest root dispositions supersede this snapshot.

## PM01–PM60

| Family | Status and exact evidence | Still open |
| --- | --- | --- |
| PM01 | **E/I:** A02 established C++ chain reuse and the LLVM gap; A03 integrated ordinary LLVM rank-one independent-input return donation. [A02](A02-tests.md), [A03](A03-tests.md). | General LLVM let chains, tuple-return cleanup and alias-copy retention; no family-wide peak/capacity claim. |
| PM02 | **P:** integrated A01 repairs retained-inline release boundaries and motion/ownership safety. | General last-use reclamation of function-local roots has no accepted pilot. A01 does not close PM02. |
| PM03 | **P, implemented bounded variant under final validation:** A04 LLVM-only same-cell ordinary return; 151 facts, 653 optimize checks, affected categories and four 033 native arms passed; final sanitizer validation pending. [A04](A04-tests.md). | C++ parity, general let/tuple updates, nonstatic layouts and other alias patterns. Do not mark integrated/complete before root accepts the final gate. |
| PM08 | **E/P:** B03 validates opt-in allocator ledger/cache scenarios; B06 documents uncovered direct selection allocations. [B03](B03-tests.md), [B06](B06-review.md). | Full-kernel accounting, churn/performance sweeps and any retention-policy change. API-accounted bytes are not process peak. |
| PM10 | **I:** C02 plus L1 restores eager row-input/failure order and admits bounded deferred partial folds. [C02-L1](C02-L1-tests.md). | Wider ranks/layouts/backends and complete pipeline ownership. |
| PM11 | **I, components:** D01 joined independent scalar fold states; C03 multiple seeded selection terminals. [D01](D01-tests.md), [C03](C03-tests.md). | Arbitrary statistics/licence/seed combinations and general shared-region scheduling. |
| PM19 | **I:** C01 one stored-input private selected terminal; C03 bounded multiple independently seeded terminals and fair baseline. | Provider selection regions and arbitrary escaping/shared selection demands. |
| PM25 | **I:** C04 anonymous rank-two transpose into one seeded partial fold, with unchanged demanded-output controls. [C04](C04-tests.md). | Other consumers/layouts and external transpose. |
| PM41 | **I:** D01 one local uncompressed rank-one Zarr map to joined folds; D05 supplies bounded uniform segmentation/cursor metadata. | Other providers/codecs, multi-source alignment and larger regions. |
| PM42 | **P:** D03 establishes repeated reads and missing immutable-source identity; D04 safe windows and D05 arithmetic cursor are integrated prerequisites. | No accepted adjacent-chunk residency cache or fetched/decoded-byte saving. |
| PM43 | **I:** D02 bounded Zarr decode/convert into required resident destination. [D02](D02-tests.md). | Other provider/layout/codec destinations; decoder workspace still counts. |
| PM46 | **P, focused implementation validated:** D06 protocol, D07 grant/admission design, D08 explicit-grant statistical-barrier spool with 82 focused assertions/16 native cases. [D08](D08-tests.md). | Shared Zarr regression/root acceptance and wider row/replay policy. No default disk admission or broad out-of-core claim. |
| PM49 | **E:** B01 triad/full-output and process-memory-report controls. [B01](B01-tests.md). | Quiet tier/thread bandwidth measurements and complete native timed-store evidence. |
| PM52 | **E/P:** B04 exact required-output store/consume controls; B05 identifies missing opaque boundary; B07 stage-1 numeric checks pass across eight arms. [B04](B04-tests.md), [B07](B07-tests.md). | Native enclosure/observer audit and performance eligibility remain under root review; no timing gain. |
| PM54 | **E:** B02 page-touch/release controls at two sizes and memory interpretation fixtures. [B02](B02-tests.md). | These do not establish compiler live-capacity savings. |
| PM55 | **P:** D06/D08 grant, quota, cleanup and tracked-workspace foundations; B01–B03 measurement foundations. | No completed fixed-budget largest-validated-problem experiment or whole-process enforcement. |
| PM58 | **P:** C05 census accepted; bounded C++ sparse-parent terminal implementation under review/validation, explicit LLVM refusal. [C05](C05-review.md). | No executed implementation acceptance yet; mapped sparse results/residual demands remain excluded. |

The following is the **complete remaining PM complement (U)**, not a statement that Blade lacks every baseline feature:

| Families | Untouched Round-4 task / baseline distinction |
| --- | --- |
| PM04–07 | General sibling stored outputs, fork DAG strategy, worker workspace and complete-write initialization. Existing fusion/destination/initialization mechanisms need case-specific evidence; B04's required stores do not close PM04. |
| PM09 | General deferred full-fold census beyond existing terminal idioms; no new blanket map-elision claim from C02/D01. |
| PM12–18 | Resident normalization, pairwise normalization, array accumulators, ragged reductions, sort projection and materialized/shared selection. Existing idioms/compaction remain controls; PM19 terminal elision does not close materialized selection. |
| PM20–24 | Bounded/dynamic grouping, membership working sets and both join contracts; coordinate existing owners. Prior-round routes and negative controls are not new capacity results. |
| PM26–32 | Required layout/assembly, broadcast, gathers, compact consumers, Gram actions, tensor modes and factors/RHS. Public operator/factor interfaces and the PM25 pilot do not close these tasks. |
| PM33–40 | Recurrence rings/snapshots/statistics/slabs/FIR and AD accumulation/zeroing/checkpoints. Existing slice-write fusion and an old unmerged ring design are not accepted ports. |
| PM44–45 | Provider projection/selection and bounded sink publication; ordinary whole-array writes remain controls. |
| PM47–48 | Addressed RNG consumption/replay and immutable revision reuse experiments; existing APIs/cache are not end-to-end evidence. |
| PM50–51, PM53 | Stride/gather locality and worker saturation sweeps. |
| PM56–57 | Mask representation and structural group/map/ungroup storage experiments. |
| PM59–60 | Function-level AD primal lifetimes and runtime-shape/capacity reuse. Static A03/A04 proofs do not extend here. |

## ST01–ST18

| Pipeline | Coverage at this snapshot |
| --- | --- |
| ST01 | **I/E, bounded components:** resident terminals/partial folds and existing fused idioms. No general region planner or arbitrary map-chain closure. |
| ST02 | **I:** D01/D05 narrow Zarr map/joined-fold route and bounded metadata; provider limits remain. |
| ST03 | **U:** differently chunked multi-source zip/selection pipeline; D04/D05 are useful window/metadata prerequisites only. |
| ST04 | **I, narrow component:** D01 shares one producer into synchronous joined terminal states. Generic forks/borrowed blocks/consumer lag remain open. |
| ST05 | **P:** D06/D08 and B07 provide spool/observation prerequisites. No materialize/replay/share/spool policy comparison for distant consumers. |
| ST06 | **P:** C01/C03 validate resident selection-terminal components. The provider stream -> mask -> compound region is not implemented by those cards. |
| ST07–08 | **U:** complete structural-group and keyed-group streaming terminal regions. |
| ST09 | **P:** D08's one explicit-grant statistical barrier passes its focused gate; broader provider acceptance and general normalization remain pending. |
| ST10 | **P:** D03–D05 expose/fix prerequisites; no accepted composed two-halo terminal region/cache. |
| ST11 | **U:** multi-file/rechunked projection/group terminal; cursor support alone is not pipeline coverage. |
| ST12 | **I, resident subset:** C04 transpose/partial-fold consumer. External tiled layout and other contraction paths remain open. |
| ST13 | **U:** tensor-mode chain/slab or spool task; coordinate its speed owner. |
| ST14–16 | **U:** external sort/projection, join/aggregate and provider sinks. D06 protocol alone admits none. |
| ST17–18 | **U:** recurrence/AD storage and addressed-RNG repeated terminals. No replay/tape/ring closure. |

S0 measurement foundations, S1/S2 bounded consumers and a focused S4 spool variant have evidence. S3 composition, S5 wider external operators and S6 policy/throughput are not completed stages. No family or pipeline has a measured whole-process memory/performance win solely from the counts above.

## Proposed next A card: A05 PM02 different-extent early-release census

**Source/design census only, after A04 final acceptance/integration and explicit root dispatch.** No census artifacts are authorized by this note. This advances the open band-1 lifetime task using existing ownership knowledge; C++ PM03 parity stays separately open.

Contract: one ordinary capture-free serial function, fresh stored positive static rank-one unitless Float64 p over an odd extent (initially 7), completed scalar observations of p, then a required final map over an independent already-stored formal y of a DIFFERENT odd extent (initially 11). The final map consumes the saved scalars and y, never p. Include two calls, both results and unchanged caller arrays demanded afterward. An additional direct provably in-bounds scalar cell observation can help retain p if a fold-only source disappears, but must be part of the fixed reviewed task and failure-order proof.

Current source hypothesis: planPoolReuse's equal-element/equal-literal-extent guard (Optimize.fs around 700) cannot donate p into this destination. Existing C++ popAllocScopeFrees and LLVM ordinary popPoolScope reclaim known-owned p after result materialization. A01's begin/release events prove a cleanup mechanism exists, not that an ordinary last-use transformation is legal. Logical lastRead/peak estimates establish no actual earlier free.

The census must establish p survives **fusion-on** lowering/materialization, its exact one backing/owner and complete scalar snapshots, and its current emitted free AFTER final allocation/fill. If current fusion removes p or a different route already releases it, record existing-route/no-gain evidence and stop. Do not disable a useful optimization to manufacture the production target; an off arm is only a control. Isolate competing schedulers for diagnosis, then inspect the actual default schedule and every owner before proposing admission.

Required boundaries: resolve aliases, retained tuples/views/closures, callable capture/type payloads, hidden extent reads, assignments, deferred consumers, other planner owners and existing resource events. Caller-owned/released/unknown roots and later p readers must refuse. Preserve producer/observation/final failure order and every scalar arithmetic graph. Identify whether current first-class events can express an exact ordinary scope without freeing unrelated allocations; no new side table or broad last-use/free policy is presumed.

Deliverable for that future card: a small owned census/design bundle plus bounded sole-Luna lower/IR/paired-emission request. Trace p allocation, final last read, proposed release boundary, y/result allocation, actual free/slot ownership and return; distinguish one backing free from logical liveness, cache retention and OS memory. Only after root reviews this evidence may an implementation be proposed. Later validation would require failure/alias negatives, interpreter/backend all-output controls and sanitizer checking of actual accesses, with no full suite or performance claim at census stage.
