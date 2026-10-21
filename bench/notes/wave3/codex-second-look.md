The strongest next action is **G51**. The other four decisions deserve narrower conclusions than the notes give: teardown dominates but does not establish parity; B2l has a plausible small opportunity; D1’s surrogate is incomplete; and the bandwidth diagnosis is not established.

I made no edits and ran no benchmarks. I treat the recorded timings as measurements; the predictions below are hypotheses for the next experiments.

| Option | Verdict | Best next experiment | Expected signal |
|---|---|---|---|
| G52 teardown | **Revisit** runtime retention policy | Bounded large-block retention, measuring kernel time, exit time, memory and repeated calls | Reported kernel time falls substantially; much of the cost moves to exit |
| B2l licensed epilogue | **Revisit**, prototype before compiler work | Exact-tree blocked epilogue versus current serial licensed recur | Potential saving around 0.1–0.35 ms; spills may erase it |
| D1 persistent syrk | **Revisit the instrument**, defer implementation | Real `dsyrk_tri`: current / final-loop `nowait` / persistent team | Likely modest saving; actual idle time determines whether it exceeds 2% |
| Stencil read-back | **Keep the licence; revisit the diagnosis** | Read-specific bandwidth and checksum sweep with clock/power telemetry | Distinguishes memory saturation from scheduling, clocks and reduction overhead |
| G51 row-view recognition | **Build now**, narrowly | Readable row-view spelling versus gate-off and explicit-range control | Same strict bits, actual row team, potentially meaningful checksum-phase saving |

**1. G52: a legitimate retention policy exists, but it defers reclamation rather than making it free.**

The measured scope-exit cost is compelling. The explanation “pages touched by eight cores, therefore TLB shootdowns caused 4–5 ms” is not established by phase timers. Heap bookkeeping, page reclamation, scheduling and translation invalidation are possible contributors. The code calls typed `delete[]`; it does not directly identify an OS release operation.

Likewise, “the whole gap” and “region parity” overstate the evidence. The pre-touched T=8 figures are **5.38 ms expert versus 5.9–6.3 ms Blade**, approximately **10–17% apart**. That is a small absolute residual, not demonstrated equality. The different phase-table and whole-program ranges should not be subtracted as though they were paired samples. See [recur attribution:53](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/bench/notes/wave3/recur-mt8-attribution.md:53).

The existing allocator already separates logical release from physical reclamation. Arithmetic blocks retain their typed deleters and are flushed at thread exit. Its limits are **4 MiB per block and 8 MiB total per thread**, not merely a 4 MiB ceiling. Raising only `kMaxBlock` breaks the documented `kMaxBlock <= kBudget` policy: the insertion loop can admit an oversized block after emptying the cache. See [pool limits:104](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/cpp/nested_array_utilities.hpp:104), [exit cleanup:144](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/cpp/nested_array_utilities.hpp:144), and [release:180](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/cpp/nested_array_utilities.hpp:180).

| Candidate | Likely effect | Memory cost and benchmark honesty |
|---|---|---|
| **Bounded large-block cache**, reclaimed at exit or eviction | Removes most immediate `delete[]` latency; repeated same-size calls can also avoid allocation faults | Keeps roughly 48 MB for recur, or 24 MB per retained stencil pool, alive longer. **Peak working set need not increase in these one-shot programs**: those pages already coexisted while live. Later allocations can increase the peak. Legitimate if generic, bounded and disclosed. |
| **Codegen lifetime extension to program exit** | Similar timing effect | Safe for proven dead arithmetic storage, but repeated invocations must not accumulate unlimited retired pools. A reusable runtime cache is preferable to benchmark-shaped lifetime extension. |
| **Decommit now, release address reservation later** | May save reservation-management work; does not avoid physical-page reclamation | Physical backing is released while address space remains reserved. No reason to assume the expensive component disappears. Requires a matching virtual-memory allocator, not `VirtualFree` on `new[]` storage. [Microsoft VirtualFree](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualfree). |
| **`MEM_RESET` / `DiscardVirtualMemory`** | Potential compromise between retaining backing and fully decommitting | Commitment/reservation and resident memory are different quantities. Discard can return RAM without decommitting; reuse may fault again. Neither promises cheap execution or immediate working-set reduction. Requires ownership of suitable page-aligned storage. [VirtualAlloc](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc), [DiscardVirtualMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-discardvirtualmemory). |
| **Large pages** | Could reduce translation overhead; allocation/release may still be expensive | Rounded allocation, privileged setup and nonpageable memory. Crucially, Windows excludes large pages from ordinary working-set accounting: the harness’s peak-working-set figure alone would become misleading. Record private bytes and large-page allocation explicitly. [Microsoft large-page support](https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support). |
| **Pre-fault ordinary pages** | Moves demand-fault work earlier; might improve placement | Usually similar eventual peak, earlier residency. Allocation and pre-touch must remain inside the allocation-inclusive timer. Pre-touching before it is diagnostic only. It does not solve teardown. |
| **Helper-thread free** | Moves reclamation off the caller’s critical path | Outstanding frees retain memory; backlog needs a byte cap and backpressure. Reclamation can still interrupt or contend with computation. Thread startup, draining and exit must be reported. A one-shot program may gain little end-to-end. |

A general cache policy is compatible with item 6’s allocation-inclusive contract, provided its delayed reclamation is recorded. A special hook that waits for the benchmark stop line would be manipulating the endpoint. The harness already records both reported kernel time and process wall time, plus peak working set and page faults: [run.py:1371](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/bench/harness/run.py:1371).

**Best experiment:** compare current policy with a bounded large-block retention arm—enough budget to hold the trajectory and relevant stencil pools—using unchanged computation and timer endpoints. Measure one call and repeated calls, explicit final flush, process wall time, retained bytes and peak/private memory.

**Expected signal:** approximately the measured 1.7 ms serial / 4–5 ms threaded recur teardown and 1 ms stencil teardown largely disappear from scope exit. One-shot process time should improve much less if cleanup merely moves. Repeated calls may show a genuine additional benefit from reuse. This is worth measuring before asking the user to choose a footprint policy.

**2. B2l: the ceiling is a useful estimate, not a demonstrated upper bound.**

The original brief explicitly calls its costs byte/op and latency arithmetic, not measured forecasts. Its **0.35 ms** estimate assumes a 160 KB recur row reload from L2. The same brief’s shipping threshold is **greater than 2%**, which lies inside the proposed 2–3.5% opportunity. That does not justify shipping, but it does justify the small experiment originally proposed. See [brief B:327](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/bench/notes/round2/brief-B-epilogues.md:327) and [threshold:527](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/bench/notes/round2/brief-B-epilogues.md:527).

There are three distinctions:

- **Serial licensed recur remains the best target.** E-G5 does not replace its whole-row K-lane fold.
- **E-G5 localizes the mt8 reload; it does not eliminate it.** The generated code explicitly calls `blade_fold::sq` over the chunk after producing it. Roughly 20 KB per worker is a much better cache situation than a 160 KB row, so most of B2l’s locality opportunity is already captured. See [CodeGenBinding.fs:6231](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/CodeGenBinding.fs:6231).
- **Stencil remains a smaller possibility**, especially serially. Its approximately 16 KB row already fits L1. The proposed blocked P1 implementation still loads stored values; it chiefly changes timing/locality, rather than deleting all load instructions. The mt8 estimate of 0.02 ms per worker is not multiplied by eight to obtain elapsed-time savings.

The estimated reload cost is a conditional ceiling only if producer performance remains unchanged. Blocking can change vectorization, scheduling, tails and register spills in either direction. A historical 3–4% CI half-width is also not an immutable resolution limit: paired, interleaved diagnostic measurements can resolve smaller differences. It does mean the existing harness evidence cannot certify the gain.

**Best experiment:** hand-patch serial licensed recur’s emitted C++ into the already-supported `lane_state`/`feed_sq`/`finish_sq` form, with a small block-size sweep such as 128/256/512 cells. Preserve all stores and the exact lane assignment, rounding, combine and tail. Compare against the current whole-row fold using raw bits, assembly inspection and interleaved timings. The runtime primitive already exists: [blade_fold_lanes.hpp:216](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/cpp/blade_fold_lanes.hpp:216).

**Expected signal:** a small positive result, plausibly 0.1–0.35 ms, or a regression from spilling/block overhead. Build compiler support only if the gain clears the existing 2% criterion reliably. Stencil is a useful secondary control, not the strongest justification.

**3. D1: the instrument resembles the control structure, but does not measure the proposed gate.**

The actual full shape is `p=1601`, `k=1001`. With `NR=8`, there are **201 packing panels**, while the default `G=4`, `MR=6` gives **67 compute row groups**. The note describes **67 of each**. This could be a transcription error, but without the external probe source it is an unresolved mismatch. The code’s packing and compute loops are separate: [dsyrk_tri:291](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/cpp/blade_packed_gemm.hpp:291), [worksharing:343](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/cpp/blade_packed_gemm.hpp:343), and [default row grouping:2146](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/CodeGenExprSupport.fs:2146).

More importantly, trivial bodies omit:

- Real packing traffic and scratch allocation.
- Unequal triangular group costs and arrival skew.
- Scheduling overhead amortized over actual tile work.
- Cache effects of stable ownership and any second B buffer.

The original D1 gate asks for **fork + barrier + idle as a fraction of tile time**. Wave 3 compares the surrogate’s synchronization against approximately 14 ms **whole-program time**. Against the brief’s approximately 12 ms tile phase, 236–257 µs is already **1.97–2.14%**—straddling the threshold before establishing actual idle time. See [original experiment:351](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/bench/notes/round2/brief-D-tile-runtime.md:351).

The persistent surrogate’s 299–376 µs is evidence against that surrogate, not proof that persistence cannot help the real kernel.

Barrier tuning has three different prospects:

- **Environment knobs:** already measured ineffective in the recur probe. Upstream MinGW selects the `mingw32 posix` implementation, whose POSIX barrier uses semaphore waits rather than the Linux spin/futex path. This offers a concrete explanation for ineffective `GOMP_SPINCOUNT`; it is not verification of the exact installed DLL. [GCC target configuration](https://gnu.googlesource.com/gcc/+/trunk/libgomp/configure.tgt), [POSIX barrier implementation](https://raw.githubusercontent.com/gcc-mirror/gcc/master/libgomp/config/posix/bar.c).
- **Setting variables inside the executable:** ordinary `setenv`/`_putenv` in `main` is generally too late; libgomp initializes environment controls in a constructor. A controlled runtime change or pre-initialization configuration is different, and should be a declared benchmark arm rather than a concealed override of stripped variables. [libgomp initialization](https://gnu.googlesource.com/gcc/+/trunk/libgomp/env.c).
- **Custom spin barrier:** technically plausible for this task-free, controlled team. It needs correct acquire/release publication, actual-team sizing, reusable generations and protection against repacking `bp` while readers remain. Oversubscription, preemption and laptop power limits can make spinning worse. It is substantial runtime work, not a free pragma replacement.

There is a cheaper candidate first: **`nowait` on the final dynamic worksharing loop**, retaining the parallel-region join. That join still protects the next K block; the separate worksharing barrier appears redundant here.

**Best experiment:** instrument and compare real `dsyrk_tri` in three forms: current, final-loop `nowait`, and single-buffer persistent team. Preserve real packing and triangular work. Record per-thread arrival/exit times, but judge savings by critical-path wall time, not summed thread waiting. Keep process/team startup representative of the real program.

**Expected signal:** likely tens to low hundreds of microseconds. Persistence earns implementation only if actual saved wall time—not merely recorded waiting—clears the tile-phase threshold. Faster barriers alone could instead strengthen the decision to keep the simpler nonpersistent form.

**4. Stencil read-back: flat timing is established; “DRAM cannot scale on this box” is not.**

The principal problem is that **`bw.cpp` measured writes**, while the diagnosis concerns a read reduction.

There are also useful numerical cross-checks:

- A 24 MB read in 1.5–1.9 ms delivers approximately **12.6–16 GB/s**, already above the quoted 6.9 GB/s threaded write rate.
- At 6.9 GB/s, writing 48 MB takes approximately **7 ms**. Yet the pre-touched mt8 expert region takes 5.38 ms. Therefore that write probe cannot be treated as a transferable bandwidth ceiling for the region.
- Ordinary write throughput counts payload bytes, while memory traffic may also include ownership reads and writebacks. Comparing its GB/s directly with read bandwidth is misleading.

These observations challenge the interpretation, not the recorded measurements.

The Silent scheme could plausibly explain lower all-core throughput through package power limits, effective core clocks, or fabric/controller behavior. “Maximum processor state 100%” does not establish fixed clocks or unrestricted package power. But the notes provide no telemetry proving a memory-controller clock explanation. Unbound scheduling, SMT placement, migrations, team overhead, store policy and the probe’s exact loop structure remain alternatives.

Even assuming dual-channel DDR4-3200, the theoretical 51.2 GB/s is a bus-rate calculation, not an expected application result. The unusually poor scaling deserves investigation rather than normalization.

**Best experiment:** one read-specific size/thread sweep containing both an independent-accumulator streaming read and the exact row-checksum computation. Include the real 24 MB shape and an odd-sized buffer comfortably above 64 MB; sweep T=1/2/4/8. Measure an already-running team separately from team-inclusive execution, consume results, verify vectorization, and separate first-touch from pre-touched runs.

Interleave the declared Silent configuration with a separately labelled performance-power diagnostic, recording effective clocks, temperature, package power and available memory/fabric telemetry. STREAM’s sizing guidance is at least four times the aggregate last-level cache per array; AMD documents tools for frequency, thermal and energy observation. [STREAM guidance](https://www.cs.virginia.edu/stream/ref.html), [AMD power profiling](https://www.amd.com/en/developer/uprof/uprof-power-profiling.html).

**Expected signal:** if streaming reads scale but the checksum does not, investigate reduction/codegen/scheduling. If both flatten and track power or clocks, the platform diagnosis gains support. Keep the read-back licence regardless: flat performance on one configuration does not invalidate its semantics or fairness.

**5. G51: build the narrow recognizer extension; it is small, but not a one-line fix.**

There are **two admission barriers**:

1. `rowSlotAdmission` recognizes only an `IRReduceCompute` at the row-body spine; ordinary `IRReduce` fails immediately. [CodeGenBinding.fs:81](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/CodeGenBinding.fs:81).
2. The shared `IRTotality.walk` accepts full proven subscripts and deferred range folds, but lacks an `IRReduce` case and does not accept this partial row subscript. Simply adding the first pattern would still decline—or become unsafe if the audit were bypassed. [IRTotality.fs:173](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/IRTotality.fs:173).

The emission machinery already exists. It evaluates the original row expression into an indexed slot, then applies the original outer wrapper serially in ascending order. Ordinary inline `IRReduce` also already has serial emission. See [slot store:2283](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/CodeGenLoopNest.fs:2283), [outer combine:2377](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/CodeGenLoopNest.fs:2377), and [inline reduction:1395](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/src/CodeGenExpr.fs:1395).

A safe first version should admit only:

- A materialized, dense, nonvirtual rank-2 source and a rank-1 row view.
- An outer coordinate proven to belong to the source’s leading index space.
- A static positive inner extent initially, avoiding seedless-empty failure ordering.
- A resolved scalar inner fold and optional seed passing the existing purity/totality audit.
- The unchanged outer-kernel audit, threading permission and strict FP context.

Do not normalize a seedless row reduction into a zero-seeded range fold casually: first-element seeding, signed zero and FP boundaries matter. Audit the shape, then retain its existing expression emission.

My source-based estimate is **roughly 60–150 production lines across two files**, plus focused tests; not a lowering redesign or new runtime. The shared `IRTotality` checker also serves threaded joins, so its new acceptance must stay precisely scoped. Existing G35 tests begin at [OmpTests.fs:965](C:/Users/cdupu/Documents/GitHub/Blade/.claude/worktrees/magical-cannon-db55bf/tests/OmpTests.fs:965).

**Best experiment:** after that narrow patch, compare the readable stencil row-view spelling with gate-off serial and the already-accepted explicit-range spelling under repro/contract-off, at actual teams 1/3/8 and a constrained team. Check raw checksum bits, actual row-team execution and the isolated input-checksum phase; include refusal tests for effects, unsafe indices and unsupported views.

**Expected signal:** acceptance and strict-bit equivalence are the decisive correctness results. The current 2.7–2.9 ms serial checksum leaves a plausible order-of-1-ms opportunity, potentially approaching the observed 1.5–1.9 ms licensed phase. That performance estimate remains conditional on the bandwidth investigation; the recognizer fix is worthwhile independently.

Automatic approval review rejected the attempted read-only peer consultation because approval is unavailable in this session. This report is my local review; no peer findings were used.

Codex session ID: 01a15b6b-18ab-7dd2-aaaf-4846c3073817
Resume in Codex: codex resume 01a15b6b-18ab-7dd2-aaaf-4846c3073817
