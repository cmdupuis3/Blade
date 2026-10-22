# Claude review of the peak memory and bandwidth plan

Status: REVIEWED AND REVISED, 2026-10-21. Claude returned no hard blocker and two
high-priority scheduling corrections. The caller verified the material findings against
the repository and revised [the plan](peakmem-microkernel-plan.md). This is a design
review, not execution evidence or approval of future compiler implementations.

Scope note: the later [streaming optimizer extension](peakmem-streaming-optimizer-plan.md)
and its integration into the parent plan have a separate
[completed Claude review](peakmem-streaming-review-claude.md). Initial follow-up attempts
failed at session reconstruction; a fresh Claude review succeeded. The original findings
and reviewed input hashes below are preserved.

The user explicitly authorized sharing the plan and relevant repository files with
Claude for this review. Repository HEAD was `667250cd`; the reviewed plan SHA256 was
`3780E33916B126E78BA3C504804A096953696362DEA2A75680A4499E333FC73A`.
The reviewed round-3 plan SHA256 was
`B64603338AB60741CD07405C66DE4B72DEA9AEAB56DFAA13A8FFF4685475BC78`.
Line references in Claude's report below refer to that input, before these revisions.

## Dispositions

| Finding | Disposition |
|---|---|
| H1 Shared recurrence prerequisite and ownership | Accepted. Section 1 and card 5.6 now name one PM33/N01 port, reuse an existing assignee if there is one, and enumerate classifier, coverage, binding replacement, block/module plumbing, diagnostic and interpreter/codegen work. M3 gates classifier-only evidence before lowering. No worker has been dispatched. |
| H2 Unbuilt speed work scheduled as already available | Accepted. M1's immediately available cells are separate from explicitly gated PM18/20/21/22/24 follow-ons. They wait for the relevant round-3 implementation and never duplicate its owner. |
| M1 Fold traversal classification | Accepted. Storage-only traversal changes must preserve each fold's source order and complete parenthesized arithmetic graph. A reordered fold needs its own licence. |
| M2 Allocator retention boundaries | Accepted with a scope clarification. Source confirms 16 KiB–4 MiB eligible arithmetic blocks, an 8 MiB budget and 16 slots per thread. Current eligible-block churn and proposed large-pool retention are distinct experiments. Large blocks bypass this cache, but small retained blocks can still contribute at large total footprints; `delete[]` does not prove immediate OS page release. |
| M3 DRAM counter availability | Narrowed to verified evidence. The harness has no validated DRAM-counter backend; initial traffic claims are logical/modeled. The review did not establish that all vendor/platform counter access is impossible on this host, so the plan makes no such claim. |
| L1 Print-replay timing | Accepted. Whole-trajectory print-replay cases remain correctness tests, excluded from timed memory comparisons until an explicit endpoint covers all replay work. The historical windowing design documents the timestamp mismatch. |
| L2 Control byte counts | Accepted. Float64 copy/scale use 16N useful bytes; add/triad use 24N. Hardware traffic remains separately modeled/measured. |
| L3 Byte instrumentation scope | Accepted. The current allocator Stats counts events, not bytes/high water. M0 explicitly includes new byte/capacity instrumentation. Runtime activation is optional, but measured live-capacity/retention claims require it. |

The concurrent local consistency audit also corrected catalogue notation (`N` total
cells versus `n` per-axis extent), trajectory-row counting, and output/workspace double
counting. It made PM28 a gather-terminal task and PM58 initially scalar-terminal-only.
Every remaining workload now has a named wave, upstream gate, follow-on disposition or
diagnostic-control role, including provider, RNG and sort prerequisites.

## Verification

The caller checked the allocator constants and event-only Stats, historical print-replay
timing, recurrence-port work packages, round-3 wave-2/wave-3 dependencies and execution
log, and current harness counter fields. The plan hash was unchanged when Claude
returned, and repository status showed no peer edits. No builds, tests or benchmarks
were run. Documentation checks cover all 60 workload IDs, table shape, relative links
and whitespace.

## Original Claude findings

**Baseline HEAD 667250cd. Read-only: no edits, build, test, bench, git change, or contact with the other session.**

### Verdict

**No hard blocker.** This is a planning artifact (no code), and the hazards the task flagged as already-addressed are genuinely addressed. Every source anchor I spot-checked resolves correctly — 11 of ~16 citations, all accurate — including the plan's own *correct* catch that `bench/kernels/README.md:167` mislabels process working set as "live data distinct from process RSS" (the harness in fact reads `PeakWorkingSetSize`, `run.py:166`). Findings are refinements plus **two high-priority revisions that should land before work is scheduled**.

### Anchor audit (category 1)
Verified accurate: `planPoolReuse` @548 + restrictions 487–523 + peak estimate 686–709; compaction-fusion T4b/T4a 2920–2934; zero-fill elision `TypeCheckInfer.fs:14057–14103` and full-buffer size @14127; `Grad.fs:71–74`; restrict/noalias `CodeGenLoopNest.fs:2783–2796`/2818–2830; `nested_array_utilities.hpp:99–104`; `run.py:162`/`run_exe:1371` (confirms `peak_pagefile` collected-then-dropped at 1380)/summary @2045; `bench/kernels/README.md:167`; `bench/harness/README.md:357`. Recurrence current-state (full buffer still at N; zero-fill elision shipped; ring only on the unmerged branch) is correct. **No stale anchor found.**

### High priority

**H1 — Missing shared prerequisite + ownership gap: the recurrence-windowing port.** (plan:36, §5.6/498–518, 662–664; §1 deps 53–54; `plan-rec-array-windowing.md` status+§11; `round3:77` N01 deferred; round3 log = only wave 1 merged.) The plan names windowing "the largest structural opportunity" and schedules PM33–35 in **M3 as a conservative port**, but the design shows minimal v1 is a substantial compiler change (classifier + coverage gate, ring/snapshot desugar, **`NAME` dissolution**, `inferBlock`/`checkModule` statement-splice plumbing, a new diagnostic, interpreter twin). Round 3 **defers N01 for the exact same unmerged rolling-storage branch.** The §1 dependency list (W1-4/W2-2/W2-4/W3-1/W3-2/N07/N09/N10) omits it. → Add the windowing port as a named shared prerequisite, assign one owner (speed-N01 *or* peakmem-PM33, not both), and size the M3 gate honestly.

**H2 — M1 sequencing inversion: PM20 (and PM18) are gated on unbuilt round-3 waves.** (plan:657, PM20 @306/§5.5 482, PM18 @304; `round3:33` census = aggregate-only grouping is the *missing* part; round3 §4/log = W3-1 is wave 3, unbuilt.) M1 is "existing speed work … at **admitted current forms**," yet **PM20's form does not exist** (W3-1 unbuilt) and PM18 needs W2-2 (wave 2). The nominal first wave can't run until round 3 is nearly done — and risks a duplicate grouping impl that §5.5:496 forbids. → Move PM20 to M3/M4 or gate it on W3-1; gate PM18 on W2-2. PM04/07/08/11/17 legitimately stay in M1.

### Medium

**M1 — "Storage rewrite" definition (§2.1, 63–67) is too permissive about "traversal changes."** A traversal reorder feeding a **fold** changes association → not bit-identical even in REPRO; that's a licensed schedule, not a storage rewrite (cf. windowing §9a:339–341). Families are individually safe (PM10/PM41/orbit preserve order), but the top-level definition invites a category error for PM25/28/29/41. Proposed clause: *"…only placement, lifetime, or traversal changes — where a traversal change is a storage rewrite only if it preserves every consuming fold's source order; a reorder ahead of a fold is a licensed schedule (§2.1)."*

**M2 — The allocator cache's 4 MB cap excludes the buffers that dominate peak.** (`nested_array_utilities.hpp:104–109`: `kMaxBlock=4 MB`, "a whole trajectory (48 MB) is never retained"; `round3:15–16`.) Two distinct retention stories are conflated (plan:33, 56–57, PM08, §5.12): (a) the per-thread ≤4 MB block cache — so DRAM-tier peaks are governed by *scope frees*, not the cache; (b) the proposed round-3 A4 "hold large pools to thread exit." PM08's retention arm can only exercise (a) with sub-4 MB churn. → State the 4 MB/8 MB boundary and split the two stories.

**M3 — "Measured DRAM bytes" is effectively unavailable on the recorded host.** (§3.2:189–191; record @402; PM49–53.) Windows + MSYS2 g++ on Zen 3 exposes no portable DRAM-traffic counter; the plan itself says faults/misses aren't DRAM bytes. → Add one sentence: on this host the bandwidth story is logical + modeled, and PM49–53 calibrate the *model*, not a measured roofline — so the campaign doesn't over-invest in that field.

### Low / optional
- **L1 — Print-replay timing artifact** (windowing §7:294–298): a windowed sole-consumer sweep runs *after* the `completed in` line. Latent for checksum fixtures, but §3.3 should explicitly forbid printed-trajectory fixtures in timed memory arms.
- **L2 — PM49 labels:** only copy=16N and triad=24N are given; scale is also 16N, add is 24N (four ops named, two counts).
- **L3 — M0 scope:** "modeled live payload"/"peak capacity" need a net-new per-allocation byte + high-water instrument; existing `blade_pool::Stats` (hpp:88–102) counts *events*, not bytes. The "optional diagnostics" phrasing understates this.

### Confirmed sound (no further challenge)
The `g=1e16,a=-1e16,b=1` parenthesization counterexample (§5.8); the emitter restrict/noalias reproof as a separate soundness obligation (§5.3); the partial-fold paired-failure decline (§5.4); capacity skip/timeout/alloc-failure distinctions and the upper-CI noninferiority margin (§3.3/§6.1); materialized-vs-terminal contracts backed by a machine-code store audit rather than a checksum; the five-party audit reuse; and the ban on subtracting input-only peaks. All hold.

---

## Scope after revision

No revised implementation has been benchmarked or proved by this review. The plan remains
a prioritized investigation backlog. The revised text incorporates the findings; it has
not been sent through a second Claude review.

