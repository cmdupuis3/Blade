# Peak memory and bandwidth microkernel plan

Status: PLANNED, 2026-10-21. Repository evidence starts at `667250cd`; concurrent speed
work may advance the checkout. This is a measurement and implementation backlog, with
60 proposed workload families, plus the companion's 18 composed pipeline experiments.
No new kernel, compiler change, or performance result is claimed here. Existing mechanisms
and historical measurements are identified separately.

The original plan was reviewed by Claude on 2026-10-21; the
[review and dispositions](peakmem-microkernel-review-claude.md) record the scheduling and
measurement/legality refinements applied. The subsequent
[streaming optimizer extension](peakmem-streaming-optimizer-plan.md) adds whole-region
planning in `Optimize.fs` and out-of-core intermediates. Its separate
[Claude review](peakmem-streaming-review-claude.md) is also incorporated: explicit planner
ownership, route-execution assertions, safe scratch recovery, sort semantics and staged
legality/dependency gates.

The companion [implementation sketchbook](peakmem-sketches/README.md) supplies code-shaped
drafts for all 60 families and 18 pipelines, including a recorded second look. These are
uncompiled, unexecuted sketches; they do not change the implementation status of this plan.

Blade should optimize three quantities together: elapsed time, peak storage, and data
movement. They often improve together when an intermediate disappears. They can also
conflict: retaining an allocation saves time but holds memory; recomputing a cheap map
saves storage but performs more work; streaming a reduction can reread its inputs.

The first campaign should establish those distinctions, measure the memory effects of
the speed work already underway, then attack whole-array intermediates. The strongest
new targets are last-use reclamation, bounded recurrence storage, terminal selection and
grouping, bounded provider pipelines, and cotangent storage. Register tiling is one part
of this campaign; many larger wins require a compiler storage decision or an explicitly
different algorithm, and those categories must remain visible.

This plan complements [round 3](round3-microkernel-plan.md), not a second implementation
queue for its items. All quantities below are analytical models or proposed gates unless
explicitly described as existing measurements.

## 1 What already exists

The source anchors below describe the inspected snapshot. Resolve symbols again before
implementation; line numbers and the status of round-3 items can move.

| Mechanism | Evidence | Consequence for this campaign |
|---|---|---|
| Same-shape scratch reuse | `Optimize.planPoolReuse`, `src/Optimize.fs:548`; restrictions at 487–523, estimate at 686–709 | Function-local, plain dense, literal extents; computed applies; dead, unaliased donor of the same shape. Measure it before extending it to dynamic extents, compatible capacities, or last-use updates. |
| Scope-based reclamation | `src/CodeGenLoopNest.fs:4568`, 4863–4905 | A logical last read is not necessarily a physical free. Early release and reuse are separate experiments. |
| Runtime allocation cache | `src/cpp/nested_array_utilities.hpp:104–118` | Arithmetic pools from 16 KiB through 4 MiB are eligible; total retained capacity is at most 8 MiB and 16 slots per thread. Larger trajectory pools bypass this cache. Count retention separately; changing the ceiling is a separate policy arm. |
| Pointwise recurrence destination passing | `Optimize.fuseSliceWritesModule`, `src/Optimize.fs:3982–4400` | Slice copies and stage pools already disappear for admitted pointwise bodies. A recurrence with spatial dependencies is a distinct admission problem. |
| Recurrence initialization elision | `src/TypeCheckInfer.fs:14057–14103`; `IRUninit` in `src/CodeGenExpr.fs:2075` | Do not propose zero-fill removal as entirely new. Verify complete-write proofs and include cases that must retain initialization. |
| Full recurrence history | `src/TypeCheckInfer.fs:14127`; [windowing design](../../docs/plans/plan-rec-array-windowing.md) | The old ring implementation is on an unmerged branch and predates guards, AD and file splits. Port the design, not an old patch wholesale. |
| Compaction fusion | `src/Optimize.fs:2920–2928`; `src/CodeGenExpr.fs:2293`, 2325; round-3 W1-4 | Positions elision is built. Gather fusion is opt-in and may reserve `N+1` cells. The Bool mask still exists. Measure selectivity and capacity, then target remaining storage. |
| Group source streaming | `genGroupByBinding`, `src/CodeGenBinding.fs:2902`, 2959, 2992 | Streaming into a full grouped pool is not bounded-memory aggregation. Round-3 W3-1 owns aggregate-only grouping. |
| Eager layout operations | `src/CodeGenBinding.fs:3381`, 3403, 3432 | Transpose, stack/join and decompaction consumers deserve explicit materialization censuses. |
| Sort workspace | `src/CodeGenExpr.fs:2502–2513` | Permutation plus output are present; lifetime and downstream permutation demand are useful targets. |
| Structural regrouping and sparse views | `genUngroupBinding`, `src/CodeGenBinding.fs:3218`; `src/cpp/nested_array_types.hpp:196–235` | Ungroup copies to a fresh dense result; sparse partial reads allocate residual keys/ranks/values. Private terminal consumers can potentially avoid these intermediates. |
| Reverse-mode primal storage | `src/Grad.fs:71–74` | Loop-body lets already have recomputation machinery; function-level primal lifetimes and selected replay are a different target. |
| Operator actions and factor values | [structured operators](../../docs/plans/structural/05-structured-operators.md), [second performance audit](../../docs/plans/plan-fortran-killer-2.md) | `gram_apply`, `m.lu` and solve actions already exist. Benchmark their intended contracts; do not invent a silent Gram-to-action rewrite. |
| Segment and revision streaming | [segmented domains](../../docs/plans/structural/07-segmented-domains.md), [revision reuse](../../docs/plans/structural/04-revision-reuse.md) | Extend existing demand analysis; distinguish compute, decoded bytes, storage reads, and cache storage. |
| Streamed intermediate boundaries | `recordSegmentStreaming`, `src/Optimize.fs:374–484`; `src/CodeGenCuda.fs:3993–4068` | The optimizer report is a consumer-shape census. Actual applies still allocate full outputs; rank-2 windows use full-height row metadata and full run tables. The companion designs bounded internal regions rather than assuming input streaming solves this. |
| Process memory collection | `_memory_info`, `bench/harness/run.py:162`; `run_exe:1371`; summary near 2045 | Peak working set and page faults reach samples. Peak pagefile usage is collected then dropped. None of these is an allocation-lifetime trace. |

Two existing labels need care before a memory headline. `planPoolReuse` estimates logical
pool liveness, while generated frees follow scopes. Also, `bench/kernels/README.md:167`
describes peak working set as live data distinct from process RSS, but the harness reads
the process's Windows working-set high water. Correct that description when the harness
work lands; this planning change does not alter the running campaign's protocol.

Round-3 dependencies are W1-4 selection, W2-1 membership hoisting, W2-2 shared selection, W2-4 producer input legs,
W3-1 aggregate-only grouping, W3-2 hash join, and the N07/N09/N10 workloads. Attach memory
measurements to their landed forms and knob-off mirrors. Their existing owners retain
the compiler work. The retention experiment A4/G52 remains a separately reported policy
experiment, not a default change implied by this plan.

At the reviewed snapshot only round-3 wave 1 is recorded merged. Shared selection,
membership hoisting, aggregate-only grouping and hash join are gated follow-ons, not
prerequisites for starting the memory campaign. Recheck their status before dispatch.

The rolling-storage port is one **shared prerequisite for PM33–35 and speed workload N01**
(also relevant to N04/N08). Planning ownership sits with PM33; N01 consumes the same port.
If the speed campaign has already assigned it, reuse that owner instead of creating a
second implementation. Section 5.6 specifies the substantial compiler work required.

## 2 Contracts and optimization policy

### 2.1 Three kinds of result

**Storage rewrite:** identical required outputs, observable effects, failure behavior and
per-cell arithmetic; only placement, lifetime, or traversal changes. A storage-only
traversal change preserves each consuming fold's source order and parenthesized arithmetic
graph; changing that order requires the licensed-schedule contract below. This is the primary
compiler campaign. A bitwise check uses `BLADE_FP_REPRO=1 BLADE_FP_CONTRACT=off` within the
supported execution environment. Default contraction can expose new FMAs after fusion;
check it separately instead of claiming universal bit identity.

**Licensed schedule:** the same task, with only the source's permitted reassociation and
parallelism. Every fold leg keeps its own seed and licence. Record numerical error and
the selected tree; do not require a changed licensed tree to match serial bits.

**Algorithm study:** a different arithmetic graph or output contract, explicitly named in
the workload. Examples are `gram_apply` versus forming a Gram matrix, online softmax versus
a two-pass normalizer, and checkpointed replay versus retaining a full tape. These can be
valuable language idioms or future features. They are not automatic compiler admissions
just because their real-number formulas agree.

### 2.2 What must remain observable

Every workload gets a spec before an implementation. List resident inputs, required array
outputs, scalar outputs, index identity, stable ordering, seeds, failure order, aliases,
and the lifetime boundary through which each output must remain available.

Use separate **materialized** and **terminal** contracts. A materialized FIR must store
both output arrays; a terminal FIR may only return their statistics. A terminal pairwise
sum need not store the pair matrix; a benchmark requiring that matrix does. These are
separate workload cells, not a speedup ratio between supposedly identical programs.
Full-output validation occurs on smoke sizes; full-size store audits establish that the
optimizer retained the promised stores. A checksum alone does not establish that fact.

Printing, `BLADE_PRINT`, captured/deferred consumers, provider writes, escaped views,
returned tuples, and side-table operands all belong in the demand census. A user-visible
array cannot disappear merely because this benchmark's checksum does not need it.
Pin print selection identically across on/off comparisons and audit actual lane support.
Library and unknown-call escapes retain storage conservatively.

All schedule-changing admissions use the round-3 five-party audit: producer, consumer,
outer fold kernel, seed, and intervening bindings, including their complete side-table
payloads and transitive callbacks. Reuse `scheduleMayWriteIn` and the current interval
payload machinery. Purity without totality does not permit moving a possible failure.
Keep BL8003 empty folds, BL8006 index checks, integer overflow diagnostics, and the first
observable failure in their source order. Add negative cases with a hidden writer and a
failing seed; a positive numerical example cannot prove these obligations.

This is the proposed admission contract, not a claim that every existing pass already
meets it. Row-fold fusion explicitly accepts some first-failure reordering in its current
comments (`Optimize.fs:4440–4472`). New streaming regions require totality or an explicit
ordered-failure proof; a no-write predicate is insufficient. The companion records this
policy discrepancy as a prerequisite for broader reuse of existing scheduling machinery.

### 2.3 Choosing a route

Report the nondominated choices in `(time, memory, traffic)`. Do not combine them into a
single score or call a lower-RSS but slower implementation universally better. A strict
reduction of both time and storage is a candidate default. A tradeoff needs a stated
shape/toolchain rule or a separate resource-budget policy decision.

Planning should not introduce per-kernel source tuning knobs, a new `where peakmem`
annotation, lossy precision, or automatic algorithm substitution. Out-of-core intermediates
are now explicitly in scope through the companion: a proposed declared toolchain policy
for tracked memory, scratch location and disk quota, with visible plans and checked failure
handling. This is design work, not an enabled spill default. First find the frontier, then
select the fastest legal plan within the declared budget, retaining refusal or a compatible
fallback when it cannot be met. Never select a different arithmetic graph from current
free RAM, or wait for OOM before inventing a spill route.

### 2.4 Shared facts before more special cases

Use one backing-storage description across these families: owner/root, views and aliases,
logical shape, physical layout/capacity, element width/alignment, required initialization,
last use, escape/print/sink demand, and effects/failures. Keep shape identity distinct from
physical capacity. The same facts should justify pool reuse, destination passing, early
release and bounded consumer execution rather than four incompatible ownership analyses.

Represent a candidate's storage as allocation intervals and symbolic byte expressions,
including worker multiplicity and metadata. A temporary's elimination, a buffer's reuse,
and a lifetime's shortening are distinct decisions. Combine them before estimating peak:
two independent savings do not necessarily subtract from the same maximum. Mark dynamic
cardinality as a bound or unknown rather than assuming selectivity. Reuse the existing
decision collector for applied/declined evidence; extend its report only when the model
can state its coverage honestly. A local model is not a proof of globally minimal memory.

## 3 Measurements that can support a claim

### 3.1 Memory has several different meanings

| Quantity | Proposed measurement | What it answers |
|---|---|---|
| Demanded value bytes | Compiler demand model | Which logical values must remain available, independently of their current representation? |
| Modeled live backing payload | Compiler liveness model, alias roots counted once | How many payload bytes are pinned by the chosen backing representation? |
| Live allocations | Runtime allocation/free events, requested and actual capacity | What storage has been allocated but not released by this program? |
| Retained allocator capacity | Allocator cache events, including per-thread caches | What storage remains held after logical deallocation? |
| Peak process resident memory | Existing Windows `PeakWorkingSetSize`, per fresh child | What was the process's resident high water, including runtime and libraries? |
| Peak process commit | Preserve `PeakPagefileUsage` with an accurate platform label | How much commit did the process require at its high water? |
| Provider and external workspace | Explicit decoder, hash-table, BLAS, and I/O buffer accounting where available | Which bytes escape the ordinary array allocator? |
| Allocation count and cumulative bytes | Allocation event totals, not peaks | Is churn expensive even when instantaneous storage is small? |

Microsoft defines working-set peak and peak commit separately; despite its name,
`PeakPagefileUsage` is peak commit charge, not bytes read from the page file.
[Windows counter definitions](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters).

Requested payload excludes padding and unused capacity. Allocation capacity includes
them. Aggregate allocations by backing root so a hundred views do not count as a hundred
buffers. A one-element escaped view can pin an N-element root; its demanded values and
pinned backing payload differ. Events need phase, allocation ID, owner/thread,
size/capacity, escape, retain and release information. Label coverage: Blade pools alone
are not whole-process allocation
coverage, and externally measured Fortran RSS is not directly comparable to Blade-only
allocation counters. Stack and thread-local workspaces must enter the model too.

Use diagnostic binaries for detailed event collection. Take headline time, process peak
working set and peak commit from the same uninstrumented timed children. Trace collection
can perturb memory as well as scheduling; label diagnostic trace-buffer overhead and
coverage, and do not use its process footprint as the headline. Validate runtime counters
first with allocate/touch/release, reuse, nested-view, and per-thread-cache fixtures.
The process counter is a lifetime high water, not a
resettable kernel-local peak. A sampled phase trace can miss a spike and must say so.
The existing `examples/tools/peakmem.fsx` polls at 100 ms and can miss a short kernel
entirely; it is not a replacement for these microkernel measurements. Identify whether
reported maxima include warm-ups or samples excluded from timing statistics, as the
current summary does. Primary memory distributions use successful measured rounds;
warm-ups and failed runs are separate populations. Preserve and flag timing-excluded
samples in the memory record: a timing excursion alone does not invalidate a memory peak.

Never subtract the peak of an input-only process from the peak of a full process. The
maxima can occur at different times, with different allocator and residency behavior.
Report input-only measurements beside the total; derive workspace from an allocation
trace or an explicitly scoped model. Preserve each run's peak, median and spread as well
as the maximum across runs. Do not publish a bootstrap CI for time as if it also applied
to memory.

### 3.2 Traffic and bandwidth

Keep three traffic columns: **logical bytes** from the specified passes, **modeled cache
traffic** with stated assumptions, and **measured traffic** from documented counters if
the host exposes suitable events. Mark unavailable counters as unavailable. Process page
faults, cache misses, and allocations are not DRAM byte counters. Random gathers may be
latency- or TLB-bound even when useful GB/s is low.

The present harness has no validated DRAM-counter backend for the recorded Windows/Zen 3
host. The initial campaign therefore reports logical and modeled traffic; controls measure
elapsed throughput and test the model's predictions, not hardware traffic itself. A vendor
or platform counter integration is separate work requiring event/coverage validation.
This is a statement about available evidence, not a claim that the host can never expose
such counters.

For Float64 copy and scale, useful traffic is `16N` bytes; for add and triad it is `24N`. These useful-byte
counts exclude write-allocate traffic and are fixed for the stated operations. Hardware
traffic depends on the cache/store policy. STREAM makes this same
distinction between requested bytes and hardware traffic. Use its approach for controls,
without labeling modified kernels official STREAM results.
[STREAM definitions](https://www.cs.virginia.edu/stream/ref.html).

Publish useful throughput using a fixed workload numerator, and executed traffic using
the actual algorithm's pass model. Eliminating a pass can lower total traffic and time
while lowering the reported *executed-byte* GB/s; that is not a regression. Conversely,
copying an unnecessary array can inflate GB/s without useful progress. Roofline intensity
uses traffic at a specified memory level, not allocation capacity.
[Roofline paper](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2008/EECS-2008-134.pdf).

### 3.3 Sizes and execution protocol

Keep the existing frozen speed sizes intact. The documented 64 MB rule is a historical
size choice, not a universal memory-optimization limit (`bench/harness/README.md:357`).
Add named memory tiers in a future harness change:

1. **Smoke:** independent exact references, empty/singleton/ragged tails, failure cases.
2. **Cache ladder:** working sets below and above the measured L2 and shared LLC, using
   nearby odd extents; use the full simultaneously touched footprint, not one array's size.
3. **DRAM:** a configurable footprint comfortably beyond LLC. For STREAM-like controls,
   use the published per-array size rule; start at 128 MiB total and grow only if that is
   insufficient on the recorded host. The number is a proposed pilot, not a fixed claim.
4. **Capacity:** a bounded series selected from explicit predicted baseline peaks. Start
   with 256 MiB and 512 MiB only when the runner's configured resource budget permits;
   scale farther as a separate campaign. Larger mathematical problem sizes are selected
   from the worst arm, including natural dense outputs, temporary and thread workspace.

That worst-arm rule applies to common-size timing comparisons. A separate capacity search
can continue one candidate beyond another's feasible size; it reports validated sizes and
limits, not a same-size timing ratio for a baseline that did not run. Each budget names
its quantity, coverage, modeled/measured status and enforcement mechanism. A compiler
pool model does not enforce a whole-process resident or external-library memory limit.

Never measure exact power-of-two extents. Include adjacent awkward sizes and vary shape
independently of payload: a skinny and a square array of equal bytes need not behave alike.
Use selectivity, lag, group cardinality, skew, reuse distance, chunk size and thread count
as independent axes rather than one arbitrary large-N case.

Run fresh processes, interleave A/B rounds, retain the existing noise controls, report
paired median time and uncertainty, and use one runner on a quiet machine. No concurrent
builds, tests or other benchmark sessions during timing. The harness's current `<50 ms`
"memory_bound" rule chooses repetition count; it does not diagnose the hardware bottleneck.
Short compute-bound kernels exist.

A fresh child does not guarantee a cold CPU or filesystem cache. Record process freshness,
allocator reuse, first read after fill, repeated read, and provider-cache state separately.
Use "cold storage" only when that state has been established; otherwise describe the
normal filesystem-cache baseline honestly.

End-to-end timing includes creation, first touch, input generation, outputs, validation
work and a documented destruction boundary. Keep current historical comparisons under
their existing endpoints. Add matched-cleanup arms where useful, and report them under a
new protocol identity. Warm allocation reuse, pre-touched kernel-only timing, and warm
provider cache runs are secondary labeled experiments. Do not hide allocation, paging,
compression, checksums, teardown, or filesystem cache effects inside a headline change.

Exclude whole-trajectory print-replay fixtures from timed memory comparisons. The historical
windowing design can run its sole-consumer sweep after the `completed in` timestamp, or
replay a mixed-consumer sweep in the print phase. Keep those cases in correctness tests;
any future timed print/replay study needs a separately defined endpoint covering all work.

No run deliberately exhausts the host. Distinguish `not-run: predicted budget`,
`measured budget exceeded`, `allocation failure`, `timeout`, and `unsupported` outcomes.
A timeout is not a memory-capacity observation, and a modeled skip does not establish a
measured limit. Report the largest validated tested size; claim a capacity bracket only
after a bounded search establishes one. Do not turn OOM or swapping into a finite speedup.
Automatic free-memory checks are execution hygiene, never an optimization input.

## 4 Workload catalogue

`N` is total element count; `n` is a common per-axis extent for a rank-`r` array, so a
dense equal-extent array has `N=n^r` cells. `b` is element bytes, `R`/`C` rows/columns,
`M` query count, `q` output/statistic count, and `L` stage/allocation count. `T` counts
stored trajectory rows, including an initial row if the contract stores one; it need
not equal the number of updates. `K` is lag or window width, `P` worker count, `G` group
count, `m` selected count, `J` join cardinality, and `B` tile/chunk capacity.

Check all sizes and byte formulas for overflow before allocation. Each storage target
identifies removable workspace, required output, or a combined bound for that component.
It is not a whole-process prediction. Add other resident inputs/outputs once; for example,
PM10 already counts its row-result output and PM26 already counts its final destination.

**E** measures an existing path. **X** proposes an extension, subject to an emission census.
**O** overlaps the active speed plan and belongs to that owner. **R** is research or an
explicit algorithm study. Every entry has a negative/control variant; the highest-return
entries receive implementation cards in section 5.

### 4.1 Lifetimes and allocation

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM01 Barrier chain | E/X. Equal-shape maps separated by real scalar consumers that prevent fusion; later stages reuse dead pools. Store the final array and every required scalar. | Distinguish `L` allocations from simultaneous live payload; target bounded physical backing instead of scope-long accumulation. | Chain length 2/4/8/16, expensive/cheap kernels; a retained view must block donation. |
| PM02 Early release with escaped views | X. Independent large temporaries die at different points; free at last use while preserving returned/captured aliases. | Physical allocation peak approaches the actual liveness envelope, not the sum until frame exit. | View, tuple, closure, deferred read, mut alias and final print variants; free only after the last backing-root use. |
| PM03 Last-use pointwise update | X. A fresh local buffer's final consumer is an elementwise map; compute into that donor. | Remove one `bN` destination, with a single read/write pass. | Same-cell reads versus shifted gather, stencil, overlapping row views, caller-owned and escaped buffers. Current dead-before-use reuse is insufficient. |
| PM04 Sibling materialized outputs | E/O/X. Two or four maps share an input traversal but store every named result. | Reduce repeated input reads; no claim that required output bytes disappear. | Two FIRs and cheap maps, 2/4/8 outputs, register pressure, immediate versus distant consumers. |
| PM05 Forked producer DAG | R. A shared intermediate feeds multiple consumers; compare materialize-once, tile-share, and recompute under a fixed task. | `bN` temporary versus `bB` tile versus extra arithmetic/reads. | Fanout 2/4/8, cheap affine and costly transcendental producer; recomputation requires repeatability and unchanged failure behavior. |
| PM06 Per-worker workspace | E/X. Parallel independent rows use scratch scoped to a worker or a bounded shared scheduler. | Expose `P * workspace` plus retained caches; cap scratch independently of problem size where possible. | P=1/2/4/8, row skew, nested teams; never share mutable scratch across active workers. |
| PM07 Complete-write initialization | E/X. Output or adjoint storage is fully assigned before any read. Compare admitted uninitialized output with required zero-fill controls. | Remove an otherwise redundant `bN` write, not the output allocation. | Partial masks, early exit, empty domains, implicit zero history and sparse scatters must retain necessary initialization. |
| PM08 Allocation churn and retention | E/R. Repeated kernels alternate tiny/large and equal/unequal sizes, with a fixed number of calls. | Measure cumulative allocations, retained bytes and process peak against latency. | Sweep below/inside/above the 16 KiB–4 MiB eligible band and 8 MiB per-thread budget. Current cache behavior and proposed large-pool retention are separate arms. |

### 4.2 Folds and producer consumption

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM09 Deferred full fold | E/X. Pure mapped input consumed only by a full scalar fold; retain exactly the written chain/seed in strict mode. | `bN` producer to bounded accumulator state; remove intermediate write/read. | Rank 1/2/3, composed maps, captured names, full versus printed producer, seeded/unseeded empty cases. |
| PM10 Deferred partial fold | X. Fold each row of an unstored rank-2/rank-3 producer, storing only row results. | `bRC` intermediate to `O(bR)` result plus bounded row state. | Long/short rows, named observable producer control, ragged/compact exclusions, exact nesting order. |
| PM11 Multiple statistics | E/O/X. Sum, sumsq, min/max and count share traversal; every statistic retains its own arithmetic. | `q` full reads to one shared read; `O(q)` accumulator state. | q=2/4/8/16, mixed licences and seed types, input-checksum legs; register spills can reverse the win. |
| PM12 Row normalization | X. Stored normalized output, original sequence of row statistics and element operations. | Elide centering/scaling temporaries; use scalar or row-sized state while storing `bRC` output. | Row width, statistic count, NaNs, signed zeros and zero variance. Do not replace sumsq/mean formulas with Welford silently. |
| PM13 Pairwise weighted normalization | E/R. Terminal weighted result per query; compare existing deferred two-pass idiom and a separately specified online algorithm. | Avoid an `M*N` score/weight array; bound query-block state. | M/N/value width, masks, all-masked rows, extreme scores; two-pass and online FP graphs get separate pins. |
| PM14 Array-valued accumulator | X. A fold returns an array accumulator, with exact source update order. | Repeated row allocations/copies to two reusable `bC` buffers, or proven pointwise destination. | Iteration count, vector width, captured alias, return alias, may-fail kernel; no unsafe overwrite of the previous state. |
| PM15 Existing ragged segment reductions | E/X. Input already has offsets/groups; return per-segment statistics without rectangular padding. | Workspace proportional to actual segments/active state, not `G * max_length`. | Empty segments, one huge segment, heavy tails, mixed seeds; preserve per-segment order and index identity. |
| PM16 Sorted projection | X. Stable sort keys and return specified projected columns; reuse/drop the permutation only after all consumers. | Avoid repeated permutation copies and premature materialized sorted columns. | 1/4/16 columns, ties, NaNs and stable-order contract; a requested permutation remains an output. Top-k is a separate task. |

### 4.3 Selection and relational regions

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM17 Stable materialized selection | E/O/X. Return all selected values in original order; compare exact-size two-pass, current gather capacity, and bounded tile staging. | Sweep `bm` live output versus `b(N+1)` capacity; do not count untouched reserve as absent. | Selectivity 0, .001, .01, .1, .5, 1; random and runs; keyed index demand forces its metadata. |
| PM18 Shared selection across columns | O/X. One predicate selects q aligned columns, all outputs stored. | One shared selection representation, not q copies; fused column reads where worthwhile. | q=1/4/16, selective downstream demand, index queries, nullable/missing schema if supported. |
| PM19 Selection to statistics | X. Only count/sum/sumsq of selected values escape; no selected array or mask is output. | Elide Bool mask, positions and gathered values; bounded accumulator state. | Stable strict fold, empty selection, expensive predicate, second mask consumer and may-fail producer. |
| PM20 Bounded-key group aggregates | O. Return G aggregates in declared key order, not grouped rows. | `O(N)` grouped payload/CSR to `O(G*q)` accumulators plus required key metadata. | G small to near N, empty groups, negative EnumIdx members, nonzero seeds, strict and licensed forms. |
| PM21 Dynamic group aggregates | O/X. First-occurrence group ordering, exact key equality, per-group strict order. | Hash/index state `O(G)` plus aggregates; avoid `O(N)` grouped value storage. | Uniform/Zipf/all-distinct keys, hash load, collision handling, resizing peaks; when G≈N, report that savings may vanish. |
| PM22 Membership working set | O/X. Same Boolean decisions under linear, bitmap and hash implementations. | Compare resident set, key bytes, probe locality and build amortization. | Span/cardinality, negative/extreme keys, duplicates, one versus many probes; overflow-checked bitmap span. |
| PM23 Join to aggregate | X/R. A separate terminal join task returns only stated aggregates in stable pair order. | Avoid `bJ` matched rows, retaining build-side index and bounded accumulator state. | Duplicate fanout, skew, empty sides; strict order is left-major/right-minor. No distributive shortcut unless a separate algorithm contract. |
| PM24 Materialized join | O/X. Return J matches with the established stable ordering. | Compare exact count-then-fill, geometric growth and chunked staging; required `bJ` remains. | Bounded J at pilot sizes, duplicate hot key, no matches, worst-arm capacity guard; include growth's old+new capacity overlap. |

### 4.4 Layout and structured operators

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM25 Transpose into consumer | X. A nonescaping transpose is consumed by a map/fold or contraction. | Full transpose copy to stride-aware consumption or bounded packing. | Rectangular shapes, consumer reuse 1/2/8, cache-sized versus DRAM; required transposed output blocks elision. |
| PM26 Required transpose and assembly | E/X. Transpose, stack or join output must exist. Fill final destination once using bounded tiles. | `bN` mandatory output plus `O(bB)` scratch; remove extra assembly copies. | Odd tiles, overlapping source/destination, rank/stride, mixed source lifetimes; do not relabel a view as an owning output. |
| PM27 Broadcast affine operations | E/X. Row/column scaling and bias with stored output. Keep small coefficients unexpanded. | Avoid a `bRC` replicated coefficient pool; reuse small vectors across tiles. | Both axis orientations, singleton extents, unit types, multiple outputs, coefficient cache fit. |
| PM28 Gather terminals | X. Fuse a gather into its terminal map/fold. Initial admission excludes scatter consumers and their writes. | Avoid `bm` gathered temporary; bound any prefetch state. | Sorted, random, repeated and clustered indices; a duplicate-scatter consumer, alias overlap and BL8006 ordering are adversarial/refusal cases. Scatter optimization is PM38's separate task. |
| PM29 Compact storage consumers | E/X. Consume symmetric/antisymmetric/Hermitian pools directly when the operation permits. | Avoid the `b*n^r = bN` dense decompaction buffer; retain canonical pool plus required output/ordered accumulators. | Rank 2/3/4, diagonals, signs/conjugation, partial folds; distinguish stored-orbit and full-logical fold contracts. |
| PM30 Gram operator actions | E/R. The task requests `gram_apply(A,B,x)` or derivative actions, not the matrix itself. | Avoid `bMN` operator storage; two vector intermediates for the existing action. | Tall/skinny shapes, repeated RHS, complex conjugation, JVP/VJP. Materialize-then-apply is a labeled algorithm baseline with a distinct FP graph. |
| PM31 Tensor mode products | O/X. N09's required output and contraction order remain fixed; tile or destination-pass unnamed intermediates. | Full T1/T2 arrays to bounded slabs where dependencies allow. | Each mode long/short, anisotropic extents, shared intermediate consumers; changing contraction order is a separate algorithm study. |
| PM32 Factor once and stream RHS | E/X. Preserve `m.lu` factors as a value; solve a stream of independent RHS with bounded solve workspace. | Elide repeated factor copies and batch-sized temporary storage, not the factors or required solutions. | RHS count, transposed solves, factor reuse, aliasing, singular errors; no destructive factor mutation visible to later solves. |

### 4.5 Recurrences and derivatives

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM33 Final-state bounded-lag recurrence | R. Only final/trailing state escapes; port conservative ring classification. | `bTC` trajectory to `b(K+1)C`, plus required snapshots and metadata. | T independently of C/K; zero history, dynamic lag, bare escape, full print, AD, guard. First version declines guard/AD cases. |
| PM34 Sparse recurrence snapshots | R. Fixed snapshot set plus final state, no whole history output. | `bTC` to `b(K+1+S)C` for S distinct snapshots, subject to lifetime reuse. | Snapshot count and span, repeated ordinal, dynamic ordinal, early freeze; dense snapshot demand must demote. |
| PM35 Statistics riding recurrence | R. Only final state and in-order diagnostics escape; diagnostics advance with each generated row. | Ring plus scalar/vector fold state; no full trajectory reread. | Strict nested fold order, nonzero seed, multiple consumers, early convergence semantics; failures may prevent schedule fusion. |
| PM36 Spatial and temporal stencil slabs | R. Fixed stencil and boundary semantics; only prescribed output times survive. | Plane/row rings or time tiles replace stored trajectories; include ghost regions and redundant halo work. | 1D/2D/3D, radius, tile seams, K-step outputs, boundary modes; no red-black/Gauss–Seidel substitution for Jacobi. |
| PM37 Multiple FIRs over chunks | O/X. Extend the resident two-output FIR with declared chunk/halo structure; both outputs stored or streamed to an explicit sink. | Reuse halo/input reads across kernels and adjacent chunks; bounded ghost residency. | Radius/chunk ratio, two/four filters, cache fit; resident twofir alone proves no provider halo reuse. |
| PM38 Destination cotangent accumulation | E/X. Reverse gather/map/stencil adds into the caller's gradient under the existing AD contract. | Avoid a full fresh adjoint and a second add pass, or a P-fold private gradient. | Fan-in, repeated indices, signed compact indices, aliasing, exact accumulation parentheses including incoming gradient, and atomics versus ownership. |
| PM39 Sparse cotangent initialization | R. A provably sparse update domain touches part of a fresh zero-initialized cotangent. | Compare full zero sweep with touched-list/epoch scheme or sparse output where the type already permits. | Touch density .001–1, repeated updates, epoch rollover, reading untouched cells; untouched fresh cells read zero, while accumulated caller buffers retain their prior values. |
| PM40 Recurrence AD checkpoints | R. Fixed-step pure recurrence and prescribed adjoint; bounded checkpoints plus deterministic replay. | Full `O(TC)` tape versus checkpoint schedule, recomputation and replay workspace. | T/C/checkpoint budget, indexed RNG, replay count, sparse snapshots; external effects and unrecorded guards decline. |

### 4.6 Providers and generated inputs

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM41 Chunked map to fold | E/X. Read one logical chunk at a time, map and fold into the specified strict/licensed state. | Full input/deferred output to bounded chunks plus accumulator; model actual decoder buffers. | Chunk count with fixed chunk size, rank 1/2, rechunking, cold/warm cache; strict fold order cannot follow asynchronous arrival. |
| PM42 Halo chunk residency | E/X. Reuse adjacent chunk ghosts across stencil consumers with a bounded cache. | Fewer fetched/decoded bytes; memory bound in chunk count plus halo, not total file size. | Chunk shape, radius, traversal, multiple consumers; count compressed fetches separately from decoded bytes. |
| PM43 Decode into final destination | X. Provider values require a resident array; decode/convert directly into its correct destination region when supported. | Full decode+conversion+assembly buffers to final `bN` output plus bounded decoder workspace. | Codec, dtype/endianness, partial edge chunks, missing values; validation and decoder ownership must survive. |
| PM44 Provider selection and projection | X. Terminal or selected-column task over immutable input; avoid reading unused columns/chunks where the provider admits it. | Reduce fetched/decoded bytes and peak intermediate storage. | Selectivity, column count, physical chunk layout, cold/warm runs; do not infer byte savings merely from selected row count. |
| PM45 Producer into provider sink | E/X. Existing dense write aliasing as control; extend toward bounded chunk production/write without flattening copies. | Full result+flattening buffer to one final buffer or bounded chunks, depending on sink contract. | NetCDF/Zarr/CSV separately, required in-memory output control, compression workspace, write failure and partial publication semantics. |
| PM46 Chunked row normalization | X/R. Same two-pass numerical algorithm over a provider; choose retain-row, proven replay, or a checked private spool under the companion's declared resource policy. | Row/chunk workspace versus extra I/O; full data need not remain resident. | Row spans many chunks, codec cost, cache eviction, source snapshot identity and read amplification; explicit disk quota, cleanup and failure tests before spill admission. |
| PM47 Indexed RNG to consumer | E/R. Samples specified by logical address, consumed in chunks; full random array is not a required output. | `bN` random tape to `bB` chunks or scalar generation; sample identity unchanged by chunking. | Stream/offset, chunk tails, distribution, replay and AD; compare `_at` with itself, never replace sequenced RNG silently. |
| PM48 Immutable revision reuse | E/X. Same task on related immutable snapshots; unchanged tiles may be reused. | Avoid reads/recompute while bounding in-memory cache and reporting disk-cache footprint. | Changed-tile fraction, tile size, halo invalidation, cold/warm store, output retention; disk growth is part of the result. |

### 4.7 Bandwidth and capacity controls

These controls diagnose a mechanism. They do not each justify a new compiler pass.

| ID and workload | Purpose | Measurement | Sweep and counterexample |
|---|---|---|---|
| PM49 Copy scale add triad | Calibrate plain streaming reads/writes at each tier. | Useful bytes/time, actual stores, first-touch and warmed secondary runs. | Odd N, two/three streams, 32/64-bit elements, P sweep; maintain all output stores. |
| PM50 Strided traversal | Separate contiguous bandwidth from stride/cache-set effects. | Time, useful bytes, cache/TLB counters where available. | Stride 1/3/7/17 and neighbors of page/cache boundaries; no power-of-two-only chart. |
| PM51 Gather locality | Distinguish bandwidth saturation from random-access latency. | Fixed useful work, physical traffic if measurable, miss and page-fault context. | Permutation versus repeated indices, clustered versus random; all arms use identical index sequences. |
| PM52 Store then consume | Determine when bypassing cache or fusing a consumer helps. | End-to-end producer plus consumer time/traffic, same memory requirement. | Immediate/distant/no reuse, materialized output. Non-temporal stores are a separate toolchain-policy experiment, not silently added intrinsics to expert arms. |
| PM53 Worker bandwidth saturation | Locate useful concurrency and its workspace cost. | Time, peak allocation, retained bytes, measured bandwidth where available. | P=1/2/4/8, multiple independent streams and nested work; bandwidth plateau can favor fewer workers. |
| PM54 Page touch and allocation | Explain resident-versus-commit gaps and page-fault overhead. | Requested/reserved/touched bytes, process commit and working set, faults and time. | Dense touch, sparse touch, reused capacity and large alignment; never score untouched output as completed work. |
| PM55 Fixed-budget capacity | Ask which tested problems fit a stated budget while computing the same task. | Largest validated tested N, budget metric/coverage/enforcement, time, and distinct failure/skip statuses. | Same contract and stopping rule for all arms; no OOM-derived infinite ratios, no runtime guessing of a new algorithm. |
| PM56 Mask representation | Explore byte mask, bitset, positions and run encoding as compiler-internal choices. | Capacity, conversion costs, scan/probe time and stable iteration order. | Selectivity, run length, reuse count, random lookup; externally observable representation/index semantics must remain unchanged. |

### 4.8 Additional concrete compiler seams

These four families exercise storage boundaries not isolated by the controls above.

| ID and workload | Contract and proposed mechanism | Storage or traffic target | Discriminating sweep and refusal |
|---|---|---|---|
| PM57 Structural group map ungroup | X. Contiguous structural segments feed a map, then return to the original axis; expose private group views or write each group directly into the final result. | Remove grouped and ungroup-copy pools; retain required final output plus bounded group workspace. | Contiguous versus scattered groups, rectangular tile groups, source mutation after grouping, multiple observers; preserve snapshot semantics and original axis identity. |
| PM58 Sparse wildcard terminal | X. A sparse partial read feeds only a scalar fold terminal with no escaping residual array or keyed lookup. | Avoid residual keys, rank vectors and copied values; scan matching parent entries in existing order. | Match fraction, residual dimension, key order, repeated partial reads; parent mutation, demanded residual index or escaping mapped sparse output prevents elision. Mapped array results need a separate representation contract. |
| PM59 Function-level AD primals | X/R. Track last reverse use of saved primals; rematerialize selected cheap repeatable expressions only under a replay contract. | Release dead primal arrays earlier and compare tape bytes against recomputation cost. | Deep function chain, branches, expensive/cheap primals, captured values and external reads; preserve reverse accumulation order and failure behavior. |
| PM60 Runtime-shaped reuse | X. Extend same-shape literal pool reuse to proven runtime-equal shape, then investigate capacity-compatible backing. | Bound physical capacity across runtime-sized temporaries; count skeleton/extent metadata independently. | Equal bytes with unequal shapes/types, changing extents, alignment, views and repeated calls; a runtime shape equality check cannot erase an earlier shape failure. |

## 5 First implementation cards

### 5.1 Measurement infrastructure before optimization

Add a memory report alongside the existing timing report, preserving old result schema
compatibility or versioning it explicitly. Retain peak commit in `Sample`; aggregate
per-run memory distributions; build new per-allocation byte/capacity and high-water
accounting with coverage labels. Existing `blade_pool::Stats` counts cache events, not
allocation bytes or peaks (`src/cpp/nested_array_utilities.hpp:80–101`). Diagnostics are
opt-in at run time to avoid perturbing headline runs, but this implementation is required
for measured live-capacity/retention claims. M0 can initially deliver process-footprint
reports without it only if live-capacity fields remain explicitly unmeasured.
Use a sidecar or structured stderr channel so diagnostic output does not corrupt the
existing stdout timing/checksum contract.
Inputs include contract ID, shape, executable hash, compiler revision, lane, FP policy,
thread count, knobs, cleanup endpoint, and actual input/output/capacity byte models.

The report should answer: which allocation was live at the peak, which bytes were only
retained, which external allocations are unobserved, and whether required output stores
survived. Add a negative fixture where retained memory rises although live payload falls,
and one where commit rises while residency does not because capacity is untouched.

The minimum record for a candidate is:

```text
contract / shape / regime / code and toolchain identity
required inputs and outputs / observability lifetime
selected route / declined alternatives / legality evidence
modeled live payload / measured live capacity / retained capacity / coverage
process peak resident / process peak commit / page faults
logical read bytes / logical write bytes / modeled cache bytes / measured DRAM bytes
allocation count / cumulative allocation bytes / provider fetched and decoded bytes
median end-to-end time and uncertainty / memory distribution / cleanup endpoint
numerical validation / store audit / status: accepted, declined, inconclusive, censored
```

These are proposed report fields, not a claim that `blade plan` or the harness already
provides them. Do not run the full catalogue until a control and one real case produce
an intelligible record.

### 5.2 PM17 and PM19 selection frontier

Start with stored Float64 `x` and a total predicate; vary only the selectivity distribution.
Required PM17 output is all `m` selected values in stable order. PM19 returns seeded
statistics only and names no mask/index/value array output. Keep those contracts separate.

For PM17 the current gather arm can reserve `8(N+1)` value bytes while an exact-size output
needs `8m`; masks and demanded position metadata are additional. At `m/N=0.001`, compare
these capacities explicitly even if only a prefix of the larger block becomes resident.
Count-then-fill can win memory while doing additional scans. Distinguish a stored-mask
arm, which can count from the mask and gather selected values, from a predicate-recomputed
arm, which can reread the source and reevaluate the predicate. Count mask scans, predicate
evaluations, source reads, output writes and staging copies separately. Tile staging adds
chunk metadata/copies and may reduce rereads. Evaluate these choices instead of assuming
one pass is best. The current positions-elision pass is the starting point, not new work.

PM19's target is scalar state plus any unavoidable producer storage. It removes the mask
as well as compact values and positions. First admission: stored dominating input, total
predicate and fold kernel, explicit safe seed, no mask escape, complete demand census.
Then add unseeded folds with the original empty-selection failure point. A keyed read,
second mask consumer, hidden writer, or required selected output is a refusal test.

Gate: emitted capacity explains the predicted curve across six selectivities; diagnostic
allocation traces corroborate it; strict results and failure order agree. A timing win
with a materially larger capacity is reported as a tradeoff, not an unconditional default.

### 5.3 PM01 through PM03 lifetime and destination reuse

Use a function-local chain with scalar observations between stages so fusion cannot erase
the experiment. Inspect the baseline plan/emission before adding new code. Existing reuse
may already bound allocations, while its logical liveness estimate can be unchanged.
Compare physical capacity, actual release points and allocator retention independently.

For early release, compute backing-root last use across view aliases, tuples, deferred
captures, callable captures, assignments and side tables. For last-use updates add the
stronger read-before-write proof: output cell `i` reads only unchanged values or that same
cell, and no other observer retains the donor. A dead donor of equal shape is not that
proof. Start with one fresh dense local map; unknown calls and cross-cell reads decline.
Re-prove emitter alias metadata too: the flat emitter uses separate `BLADE_RESTRICT`
input paths and fresh-output dependence assumptions (`src/CodeGenLoopNest.fs:2783–2796`,
2818–2830). Donation needs a legitimate shared read/write pointer path or revised
restrict/noalias and vector/thread assertions; correct Blade-level values alone do not
make stale C++/LLVM alias promises sound.

Gate: allocation peak follows the maximal overlapping allocation intervals, not the sum
of every allocation in a frame. Required live input/output bytes remain in the model.
Add destruction-count tests to catch double free, and alias tests to catch use-after-free;
use the existing memcheck/optimization differential routes where supported.

### 5.4 PM10 partial reductions without producer storage

Use a rank-2 anonymous producer whose only consumer is a row reduction. Initially require
pure, total producer, fold, seed and transitive callees. Preserve the existing row chain
and explicit seed; store `R` results. For higher rank, `R` is the product of unreduced
extents: each row has its own chain and seed, not one global accumulator. The removable
intermediate is `bRC`, and its write/read pair is `2bRC` logical traffic. That is a model conditional on
the emission census showing the materialization, not a claim about every current spelling.

Compare plain, composed, and block-kernel producers; all remain idiomatic Blade. Separate
named output, full fold, partial fold, and joined partial-fold cases. The old streaming
memo is historical evidence, not proof that every listed decline remains today.

Gate: fix C while increasing R, then fix R while increasing C. No full producer allocation
in the admitted terminal case; named-output controls keep their stores. Empty dimensions,
nonzero seeds, axis provenance and failures remain identical to the unoptimized reference.
Include a paired-failure negative: a later producer row fails and an earlier row's fold
also fails. Full materialization reaches the producer failure first; row streaming would
reach the fold failure first, so that admission must decline without a stronger proof.

### 5.5 PM20 and PM21 aggregate-only grouping

Attach this card to round-3 W3-1. Inputs are already stored keys and values. Outputs are
group keys/order and requested aggregates; grouped rows/CSR are not outputs. Positional,
EnumIdx and discovered-key regimes get distinct specs. A strict group accumulator sees
each group's values in the original encounter order, starting from the exact seed or
first element. Parallel merge is a separate licensed form.

Measure both small G and G close to N. In the latter case hash capacity and per-group
metadata can outweigh eliminated grouped values. Resizing can temporarily retain old and
new tables, so final table size is not peak storage. Preallocation needs a justified bound,
not an assumed key distribution. Keep grouped-output and captured-counter refusal cases.

Gate: at fixed G, extra aggregate state is independent of N apart from required inputs;
at varying G, report the measured hash/state slope. No duplicated grouping implementation
lands under this memory plan while the speed owner is building the same admission.

### 5.6 PM33 through PM35 recurrence rings

Treat the shared rolling-storage port as a substantial compiler prerequisite, not a
microkernel patch. PM33 owns one implementation for this plan and speed-N01. Its work
packages are: a demand/lag classifier with a dense-coverage demotion gate; preservation
of conceptual types while replacing physical storage with rings/snapshots; dissolution
of the original binding only when every use has a valid replacement; current block/module
statement-splice plumbing; fresh diagnostics; and interpreter/codegen parity. First ship
classifier-only decisions and refusal tests, then block-local fixed-step rings. Module
scope, snapshots, riding diagnostics and print replay are separately gated extensions.
If another campaign already owns the port, consume its result rather than implementing
these packages twice. No worker is assigned by this planning document.

First port the conservative fixed-step, no-AD, no-guard case: bounded constant lag,
final/trailing reads, no escape, no whole-history output. Preserve conceptual index extent
and zero-history semantics. Only physical backing becomes a ring. Initial proof uses
`K+1` slots so writing the next slice cannot overwrite any needed lagged slice.

Illustrative analytical payload: `T=10001`, `C=1003`, Float64 gives `80,248,024` trajectory
bytes. With maximum lag 2, three rows use `24,072` bytes, before inputs, final-result copies,
metadata, thread scratch and snapshots. These numbers are not predicted process RSS.
Double T at fixed C/K: ring storage should stay bounded. Then grow K and verify its slope.

Snapshots and riding diagnostics follow only after that gate. A guard can freeze the rest
of a conceptual trajectory; streamed diagnostics must reproduce those frozen values if
they are part of the task. AD may require earlier primal states. Both are deliberate
first-version refusals. Full printing stays full or uses an independently proved replay
scheme; hiding the print is not a storage optimization.

The old branch reports a 64 MB probe at roughly 5.3 MB versus 68.1 MB process peak. Treat
that as historical feasibility evidence with its own program and environment, not a
measurement of this checkout. Its diagnostic number and code organization are stale.

### 5.7 PM25 through PM27 layout and assembly

Begin with anonymous transpose/stack/join intermediates feeding one consumer. Derive a
strided view or write directly to the final destination only when ownership and layout
permit. A view may save memory but degrade bandwidth; bounded packing is a third candidate.
For required transpose outputs, block into a mandatory destination and measure scratch.

Use rectangular and awkward shapes, include reuse count, and compare equal-byte layouts.
Reject aliasing cycles unless a specifically correct in-place permutation algorithm is
implemented; that algorithm has its own workspace and ordering proof. Keep materialized
outputs and source index identities. Do not treat a change of storage orientation as a
change of logical dimensional currying.

Gate: explain each saved full-array copy in the emission and pass model; test immediate
versus repeated consumers so the planner sees where packing pays for itself.

### 5.8 PM38 cotangent accumulation

Start with one existing scatter/gather adjoint path and an already allocated destination.
Compare fresh-contribution-array plus add against direct accumulation. Save the redundant
gradient buffer and its write/read pass while preserving all required primal/tape state.
The strict comparator fixes the complete parenthesized arithmetic graph, including the
incoming gradient and final add. Contribution order alone is insufficient: computing
`g + ((0 + a) + b)` is not generally the same as `(g + a) + b`. With binary64
`g=1e16, a=-1e16, b=1`, these give 0 and 1 respectively. Preserve the original subtotal
and final add in bounded cell/tile state, prove a narrower case, or admit a separately
licensed form. Atomics or a different owner traversal require their own numerical proof.

Repeated indices, aliasing between primal/seed/destination, compact signs and conjugation,
multiple VJP calls, and nonzero initial gradient values are mandatory tests. Measure both
low and high fan-in; full per-worker gradients can dwarf the original temporary.
Primal agreement is insufficient: use independent small derivatives, adjoint identities
under the appropriate tolerance, and optimizer-on/off raw-bit checks where promised.

Gate: extra peak capacity is bounded by the declared tile/worker scheme, and no path
silently replaces accumulation with assignment. Sparse initialization PM39 comes later.

### 5.9 PM40 checkpointed recurrence derivatives

This is a research track, not an immediate extension of ring storage. Reverse traversal
needs primal information that the forward ring intentionally discarded. Fixed-step pure
recurrences are the first scope; define complete checkpoint state and count recomputation.
A checkpoint schedule trades tape storage for replay work; the optimum depends on the
checkpoint model. [Revolve](https://doi.org/10.1145/347837.347846) is prior art for that
tradeoff, not evidence that Blade can replay arbitrary effects.

Start with deterministic fixed-spacing checkpoints and explicit segment replay before
considering a more sophisticated schedule. Verify replayed states, gradients, accumulation
order, and runtime workspace. Indexed RNG is reproducible only when keys, addresses,
distribution algorithms and relevant FP behavior remain identical. External reads, I/O,
mutations and data-dependent stopping need a recorded replay contract or a refusal.

Gate: publish `(checkpoint bytes, temporary tape bytes, replay steps, time, error)` over
several budgets. A small checkpoint store that reconstructs a full trajectory during every
reverse segment has not solved the memory problem.

### 5.10 PM41 through PM45 bounded provider pipelines

Use local immutable fixtures, first uncompressed then one supported codec. Record raw
fetched bytes, decoded bytes, cache hits, active chunks and peak decoder workspace. Keep
filesystem cache state explicit. A streamed consumer can still accidentally retain every
decoded chunk through a container or captured view; increase chunk count at fixed chunk
size to expose that.

The target is a stated bound such as active chunk buffers plus ghost buffers plus decoder
workspace plus required outputs. For a scalar terminal, that bound should not grow with
total file size. For a resident output, include the mandatory full output; only workspace
is bounded. For a sink, define what partial output exists if a later chunk fails; preserving
input validation and failure ordering takes priority over early publication.

Gate: at fixed chunk/halo geometry, extra capacity is flat as file size grows only when
metadata is also bounded; otherwise report its growing term. Current rank-2 windows and
run tables need explicit attention. Keep standalone netcdf/zarr/csv/hybrid checks; the
default suite does not cover them. Provider capabilities differ: start with the C++ rank-1
Zarr block-fold route; LLVM provider I/O and interpreter streams are unsupported in this
snapshot. A cache improvement must not be sold as a cold-storage improvement.

The [streaming optimizer companion](peakmem-streaming-optimizer-plan.md) supplies the
whole-region design, 18 composed experiments, and S0–S6 rollout. It extends this card into
high-level computation optimization: terminal deforestation, memory-aware CSE, shared
fanout, selection/group regions, layout propagation, multi-stage halos and statistic
barriers. It separates private spooling from public chunk sinks; current write aliasing
avoids a flatten copy but still requires the full source array.

### 5.11 PM13 normalized pairwise consumers

Start from the existing deferred row kernel plus reduction-join idiom. Its task returns
weighted results, not the score matrix. Compare retaining one row, recomputing scores in
the established two-pass order, and blocking multiple queries. Only then add an online
normalizer as a separately specified algorithm, including nonfinite/all-masked behavior.

FlashAttention demonstrates why reducing data movement can matter more than arithmetic
count for attention. Its GPU IO results do not predict a CPU Blade speedup or bitwise
equivalence for a changed softmax evaluation graph.
[FlashAttention](https://arxiv.org/abs/2205.14135).

Gate: no hidden M-by-N pool, each named output stored, numerical checks for extreme scores,
and measured recomputation/traffic costs. A common scalar accumulator does not justify
claiming constant total memory when inputs and output vectors grow.

### 5.12 PM06 and PM08 thread workspace and allocator policy

Measure current policy first. For P workers with per-worker scratch W and cache ceiling H,
the model must allow `P*(W+H)` in addition to shared data, with actual active workers and
nested teams recorded. Allocator reuse may reduce allocation count without lowering
resident high water; release can reduce later pressure while costing teardown time.

The current arithmetic-pool cache admits 16 KiB through 4 MiB blocks, with H=8 MiB and
16 slots per thread. A 48 MB trajectory is outside that band. Exercise eligible-size
churn separately from large-pool scope release; sums of small retained blocks still count
at DRAM-sized total footprints. Blocks outside the band go to `delete[]`, which does not
itself prove that the underlying heap immediately returns resident pages to the OS.
Round-3 A4/G52's proposed larger retention ceiling is a distinct policy experiment with
its own measured memory/time tradeoff, not a description of today's cache behavior.

Alternate phases with different sizes to expose retained obsolete blocks. Compare cold
and repeated-call runs using the same task and matched cleanup endpoints. If a retention
arm wins time but costs memory, preserve both points on the frontier. This plan does not
resolve round-3 A4/G52's default-policy decision.

## 6 Acceptance and implementation sequence

### 6.1 Common acceptance gates

1. **Reachability:** the idiomatic Blade source compiles or records a precise current
   refusal. Preserve that refusal as a prerequisite; do not invent working syntax.
2. **Legality:** positive and negative admission cases, complete demand/effect census,
   stable diagnostics and failure order, strict and licensed numerical contracts.
3. **Storage evidence:** emitted allocation/store audit plus diagnostic live-capacity
   trace and process measurements. Prove the predicted scaling, not just one smaller peak.
4. **Runtime evidence:** uninstrumented paired runs, same inputs/output lifetime and
   endpoints, independently checked outputs, control stability and confidence intervals.
5. **Backend evidence:** C++ and interpreter semantics, LLVM where supported; unsupported
   LLVM cells stay explicit. A silent lane switch is not a passing memory test.

Proposed engineering triage threshold: pursue a constant-factor memory change when it
removes a full dominant buffer or reduces extra live capacity by at least 20% at two
DRAM-sized points. Pursue an asymptotic change when the predicted slope is observed across
at least three sizes. These are campaign triage numbers, not language guarantees.
For a default route candidate use a predeclared timing noninferiority margin on designated
representative cells, for example an upper confidence bound on `time(new)/time(old)` of
at most 1.05. An interval crossing that bound is inconclusive, not evidence of no regression.
The 5% margin is a proposed campaign decision, not a universal acceptance law. Larger
memory wins with a time cost remain useful explicitly labeled frontier points.

Drop or defer a candidate if baseline emission already eliminates the proposed storage,
the required output dominates and nothing removable remains, counters cannot resolve the
claim, the new route only wins by changing the contract, or the implementation relies on
an unproven alias/failure/FP assumption. An honest no-gain control is retained in the suite.

### 6.2 Staged work

| Wave | Deliverables | Dependencies and completion gate |
|---|---|---|
| M0 Measurement | Correct metric labels; preserve commit; implement byte/high-water diagnostics; PM49/54 controls; one memory report. | No compiler optimization. Process-footprint reporting can precede byte instrumentation, but measured live-capacity/retention claims cannot. |
| M1 Available speed work through a memory lens | PM04/06/07/08/11/17 at admitted current forms; selectivity, worker and capacity sweeps. | Starts independently of unbuilt round-3 waves. Reuse current knobs/binaries and report tradeoffs without duplicating owners. |
| M1 Gated follow-ons | PM18 after W2-2; PM22 after W2-1; PM20/21 after their respective W3-1 key regimes; PM24 after W3-2. | Measure as each upstream implementation lands, even if M2 has begun. Missing upstream work stays blocked/deferred, not a second compiler assignment under this plan. |
| M2 Small storage admissions | PM01/02/03, PM10, PM19, PM25/26, then PM57/58/60; one bounded implementation per owner. | Emission census first; shared legality facts; local positive and refusal tests; controlled A/B. |
| M3 Bounded state | Shared PM33/N01 windowing prerequisite, then PM34/35; PM41/42/43/45; PM38. | One shared port owner; classifier-only gate before ring lowering and interpreter/codegen validation. Provider and AD work have separate owners and scaling gates. |
| M4 Research frontier | PM05/13/23/36/39/40/46/48/59 and PM55/56. | Explicit algorithm contracts or prerequisites; memory/time frontier, not automatic defaults. |

The companion's streaming track crosses these waves: S0 census and S1 resident terminals
support M2; S2–S3 provider/region composition supports M3; S4 private spooling and S5 external
operators/sinks extend M4. S6 throughput tuning follows each route's bounded-storage proof.
ST01–ST18 are composed experiments over PM families, not duplicate compiler assignments.

The remaining catalogue entries have explicit follow-on dispositions rather than implied
commitments to the first five waves:

| Entries | Disposition and prerequisite |
|---|---|
| PM09/12/14/15/16 | Follow-on fold, accumulator, ragged and sort census after M2. Existing routes become controls; new admissions get separate briefs. Sort-permutation early release can be a bounded PM02 pilot. |
| PM27/28/29/30/31/32 | Follow-on layout/operator campaign after M2's destination/consumer facts. PM31 reuses N09 and its active speed owner; PM30/32 first measure existing public operator/factor forms. |
| PM37 | Chunked FIR extension after PM42 halo residency and PM45 sink contracts, reusing N07. Resident twofir is already covered by PM04. |
| PM44 | Provider-specific projection/selection extension after PM41/43; require actual provider reachability and physical read/decode evidence. |
| PM47 | Deferred until addressed-RNG reachability is checked in each lane; reuse the N08 backend/Philox prerequisite where relevant. Do not replace unsupported `_at` with sequenced RNG. |
| PM50/51/52/53 | Diagnostic controls deployed by relevant waves, not separate compiler work: layout/gather locality, producer-consumer stores and worker saturation. Optional non-temporal-store experiments retain their own toolchain policy. |

Within M2 the recommended first three builds are **terminal selection, lifetime-aware
reclamation, and deferred partial folds**. They isolate a missing buffer with limited
surface change. **Recurrence windowing** is the largest structural opportunity but needs
an intentional port and stronger observability tests. Provider streaming and AD follow
with their own failure/replay contracts.

### 6.3 Handoff boundaries

Keep microkernel workload IDs under `PM` and companion pipeline experiments under `ST`,
so neither collides with round-3 `N` or `G` numbers.
Do not renumber the existing gap register or change its statuses without new evidence.
Each implemented workload receives a `spec.md`, normal kernel manifest, idiomatic Blade
source, natural/expert twins where applicable, independent reference/pins, required-store
audit, memory model and selected/declined plan evidence. Research-only prototypes stay
out of headline comparisons until they meet the same contract.

Reuse the existing harness rather than building an unrelated memory benchmark runner.
Schema/configuration changes belong to its owner; compiler changes need separate owned
files/worktrees because the speed campaign is active. Run each test process from a private
working directory; full suites and all timing remain serialized. Record skip counts.

No implementation requires every family at once. The catalogue supplies hypotheses and
counterexamples; each wave earns the next one by producing a measured, legal improvement
or a useful refusal. The policy questions to settle after measurements are allocator
retention defaults, whether an explicit memory budget belongs in the toolchain contract,
and which separately specified algorithms deserve public library forms.
