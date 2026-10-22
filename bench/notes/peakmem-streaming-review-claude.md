# Claude review of the streaming optimizer plan

Status: REVIEWED AND REVISED, 2026-10-21. Claude found no hard blocker and identified
four medium-priority prerequisites before their respective implementation stages, plus
four research refinements. The caller checked the material claims and revised the
[streaming plan](peakmem-streaming-optimizer-plan.md). This is design review, not
implementation or performance validation.

The user explicitly authorized sharing the plans and relevant repository files with Claude.
Source baseline: `667250cd2dd7c5212013f51353261d665b358890`.
Reviewed input SHA256 values, before the changes below:

- Streaming companion: `5FBF7EA61F131A16D5A520DA34B1B5694ADDB5FC696651751EE234367945A159`.
- Parent peakmem plan: `76EDC24D24DF76D0827A30CCD5B29AD17BA6E6E13CAAB6B0EAAE3C783DB12D08`.

The successful review used the configured Claude CLI through PAL in its read-only review
role. Initial continuation attempts failed: the local bridge's session reconstruction
requires an API-model fallback even though the CLI tool itself does not. Source inspection
identified that technical limitation; a fresh request with the relevant context succeeded.
No credentials, model settings, or bridge files were changed. Review line references below
refer to the input snapshot, not the revised document.

## Dispositions

| Finding | Disposition and verification |
|---|---|
| F1 Region/legacy planner ownership | Accepted. Section 2.1 now specifies one proposed ownership set plus storage-root/dependency exclusions, cross-plan assertions and per-compilation reset. S1 must be provably disjoint from all four planners; if no useful admission remains, defer integration to S3. The exact sequential planner calls are present in Lowering. |
| F2 Tests may pass without exercising spill | Accepted. S4/S5 now require emission assertions, runtime route evidence and fault injection, plus small materialized/reference results. `tests/OptDiff.fs:35–36` explicitly permits identical C++ as optimizer-inert success. The interpreter can validate a supported materialized reference; it does not validate C++ spill execution. |
| F3 Cleanup and concurrent recovery | Accepted with stronger ownership rules. Distinguish normal/quick-exit cleanup from abort, immediate termination, forced kill and host failure. Recovery requires exclusive ownership and a dead owner, identified by PID, process-start identity and a run nonce; PID or age alone is insufficient. Shared scratch roots require this even when the test harness uses private working directories. |
| F4 External-sort ordering prerequisite | Accepted. Pin the supported ordering/ties/NaN contract before ST14 admission. Source at `CodeGenExpr.fs:2504–2505` uses `std::stable_sort` with key comparison by `<`; this does not establish a total NaN order. Unspecified cases stay outside the route until a semantic decision and matching tests land. |
| F5 Progress-memory bound | Accepted as a requirement, not the report's suggested general min-cut characterization. Section 6 now gives a sufficient bound from the chosen schedule's allocation/release transitions, counts live roots and next-step allocations once, reserves before execution, and requires prompt refusal below the bound. Concurrent progress needs its own proof. |
| F6 Ledger lifetime | Accepted with scope clarification. The ledger belongs to a program execution context and must reset for repeated invocations within one process; current subprocess benchmark runs are already isolated. Add balanced-reservation, teardown and repeated-entry tests without claiming that the harness already embeds multiple programs in one process. |
| F7 Ordered-failure proof availability | Accepted. `IRTotality.fs` provides a closed purity/totality whitelist, not a general failure-order proof. S1–S4 interleaved compute kernels/seeds use concrete totality admission; provider I/O retains its validation/failure order. The more general proof alternative is deferred. |
| F8 Recurrence prerequisites | Accepted for dependent variants, narrowed for independent ones. Temporal/recursive ST10 and bounded recurrence-history ST17 wait for the shared PM33/N01 port to land. Pure spatial halos and non-recursive AD tape work do not automatically depend on that port. Recheck current ownership; the fact that this plan dispatched no implementation worker does not prove another session has no owner. |

An independent local capability audit added one further concrete correction: S2's direct
rank-1 streamed fold is not already connected to composed maps and joined reduction states.
The plan now names a new block-consumer path and a route-taken assertion. Verified anchors:
`CodeGenBinding.fs:3807/5961`, `CodeGenFusion.fs:405`,
`CodeGenLoopNest.fs:601`. Reusing the ordinary streamed-map wrapper unchanged would retain
full output/run metadata and fail the storage gate.

No compiler/runtime files were changed; no builds, tests or benchmarks were run. Document
validation checks links, ID coverage, whitespace and preservation of the source snapshot.
The [original parent review](peakmem-microkernel-review-claude.md) remains separately recorded.

## Original Claude report

I have verified the load-bearing anchors (all resolved accurately) and read both plans in full. This is a read-only review task; my deliverable is the findings returned to the caller below. I made no edits.

---

# Review: peakmem-streaming-optimizer-plan.md (+ integration into peakmem-microkernel-plan.md)

Baseline `667250cd`. Read-only: no edits, build, test, bench, git mutation, or contact with concurrent sessions.

## Verdict

**No hard blocker.** This is a planning artifact that adds no code and no Blade syntax, and it is unusually well-grounded: every source anchor I spot-checked resolves correctly (lowering order `Lowering.fs:3131–3185`; table reset `:2955–2960`; `bindingPayload`/`intervalPayloadOf` `Optimize.fs:1461/1478`; row-fold first-failure reorder `:4440–4472`; `fsproj:144–151` Optimize-before-ProviderRegistry; run-record size+mtime-not-content `blade_run_record.hpp:19`; best-effort tile store `blade_tilecache.hpp:224`; segment-stream provider-hook check `CodeGenCuda.fs:~4003`). The self-critical tone is correct — most proposed facilities (spools, external operators, budget ledger, ordered-failure proofs) do not exist and are honestly flagged as design sketches. My findings are refinements plus four items that should land before their respective stage (not before S0).

## Must-fix before the relevant stage

**F1 (MEDIUM, S1/S3; plan §2.1 70–83; `Lowering.fs:3167–3185`). The ownership handshake with the four existing side-table planners is under-specified and in tension with the stated ordering.** §2.1 says the region plan is selected "before allocation and producer/consumer side tables are populated," yet source shows `cseModule` → `planPoolReuse` → `planHotRowFolds` → `planCompactionFusion` → `planCompactRowClasses` run as four sequential statements, each populating its own table (`PoolReuseTable`, `HotRowTable`, `CompactionTable`, …). A region planner inserted *before* them means all four must learn to skip region-owned nodes; inserted *after* means it must read four tables. Either way this is a cross-cutting change to five passes, not the "narrow stable IR annotation" the plan proposes for v1. **Recommend:** (a) make the ownership mechanism concrete — a single `RegionOwned : Set<IRId>` checked at the top of each existing planner, reset alongside `:2955–2960`; (b) scope S1's ST01 region to be *disjoint* from pool-reuse/hot-row/compaction candidates so S1 touches none of the four, deferring real coordination to S3 where the plan already lists "mark those nodes owned."

**F2 (MEDIUM, S4/S5; plan §8 434–439). The spool/external-operator routes have no interpreter twin, and opt-diff cannot detect a silently-declined spool.** The differential oracle (`test interp`, `opt-diff`) is the campaign's miscompile catch, but §1 (`Interp/Run.fs:363/569`, `EmitLlvm.fs:7468`) confirms the interpreter and LLVM lane reject streamed/provider routes — so a spool is C++-lane-only with *no* second lane. Worse, a spool changes storage, not output; `opt-diff` compiles/runs only pairs whose C++ differs and compares normalized output, so a spool that is *silently declined* produces identical output and passes trivially (exactly the failure mode CLAUDE.md records for tiles: "a short scratch silently drops it to the scalar chain"). **Recommend:** add an explicit "spool route taken" / "external-operator taken" assertion to the S4/S5 harness (analogous to `llvm tiles` asserting the tile route), and state in §8 that for spool/external routes the oracle is the materialized `.read` reference plus route-taken + fault-injection assertions — the interpreter is not an oracle there.

**F3 (MEDIUM, S4; plan §5.2 268–273). The scratch-cleanup design must name the exit paths it cannot intercept and lock concurrent runs out of the recovery sweep.** The plan correctly notes provider paths call `std::exit` (destructors won't run) and proposes an atexit-style cleanup plus a cross-run "recover only this facility's stale owned directories" sweep. Two gaps: (a) `std::_exit`/`abort`/panic paths bypass `atexit` too — the plan should state which paths it intercepts (atexit/at_quick_exit) and which it cannot, so an un-cleaned dir after `abort` is expected, not a bug; (b) CLAUDE.md is explicit that the scratch dir is cwd-relative and "concurrent sessions must use private working directories" — a recovery sweep that deletes a *live concurrent run's* spool is data corruption. **Recommend:** per-run-dir lockfile with owning pid; the sweep deletes only dirs whose owner pid is provably dead; never a time-based or name-pattern sweep of arbitrary temporaries (the plan already forbids the latter — make the pid-liveness rule explicit).

**F4 (MEDIUM, S5; plan §5.3 287/296–299, ST14). "Establish the current sort's NaN/tie/stability contract" must be an explicit S5 precondition, not a caveat.** External run/merge sort must reproduce bit-for-bit the in-memory sort's observable order including NaN placement and stability, but the plan cites only `CodeGenExpr.fs:2502–2513` (permutation+output) — not where the comparator/NaN/tie semantics are *defined*. If the current order is implementation-defined for NaN, an external merge with a different comparison count/order can diverge while "passing" a materialized reference that also has undefined NaN order. **Recommend:** promote "pin the current sort's NaN/tie/stability contract with corpus tests" from the §5.3 caveat into an S5 completion gate that *precedes* any external-sort admission.

## Optional research refinements

**F5 (S3/S6; §4.2 190–191, §6 352–354). Forward-progress reservation needs a formula, not "reserve minimum buffers."** A synchronized pull pipeline with a hard byte budget and shared-block fanout can deadlock: the block needed to release others can't be allocated because the budget is full. The plan names this hazard twice but gives no minimum. The safe reserve is the max over the dependency DAG of simultaneously-live blocks on any forward-progress path (a min-cut), not a constant. Without it, the §8 "deterministic small allowance" boundary tests may deadlock rather than cleanly refuse. Recommend a concrete progress-reservation rule as an S3 deliverable, and a test asserting *refusal* (not hang) at a sub-minimum budget.

**F6 (S4+; §6 350–351, §2.1). The runtime reservation ledger is global mutable state and needs a per-run lifetime.** The compile-time tables reset at `:2955–2960`, but the §6 "execution-wide reservation ledger" lives in the compiled program. The harness runs many programs; the ledger must init/teardown per *program run*, not per process, or reservations leak across runs. It also cannot be differentially tested (C++-only). State the lifetime explicitly and add a leak/reset test.

**F7 (S1/S2/S4; §4.3 219–223, §5.2 248–251). Demote "specific ordered-failure proof" to deferred — there is no current foothold.** Totality is available (`IRTotality.fs`, the closed whitelist, `fsproj:144`), but a *failure-ordering* analysis (proving interleaved producer/statistic evaluation preserves first-diagnostic order) has no cited analysis and `IRTotality` does not provide it. In practice S1–S4 can admit only the total-kernel case (S1's brief already says "pure/total region" — good). Recommend stating in §4.3/§5.2 that the ordered-failure alternative is a deferred facility with no v1 foothold, so no region claims it before S5+.

**F8 (S3/S5; §3 158, §5.3 291, ST10/ST17). ST10 and ST17 inherit the parent's unresolved H1 (unowned recurrence-windowing/PM33 port).** The prior Claude review's H1 disposition records "No worker has been dispatched." The streaming plan adds two new dependents on that same port ("temporal dependence may need a ring/slab and PM33 ownership"; "rings/slabs plus explicit tape spool"). The §3 S3 gate ("coordinate … PM33 owners") covers this, but ST10/ST17 should be explicitly gated on the PM33/N01 port *landing*, mirroring the parent's H1, so the companion does not silently schedule work on an unbuilt, unowned prerequisite.

## Positives worth preserving

- The two-seam framing (discover while high-level forms are recognizable, re-decide on actual IR after CSE) is the correct response to the real `Lowering.fs` order, and the "early discovery is advisory, cannot authorize codegen to skip a binding" rule is exactly right.
- Provider scoping is honest and matches source: S2→Zarr only (fiber/block/window hooks exist; NetCDF block/window, CSV, Icechunk absent), ST11 gated on capability facts, LLVM/interp refusals surfaced up front.
- "Computation reduction beats spilling" (§3 162–167): deforest/strided-consume before inventing out-of-core machinery — the right priority, and it keeps the disk facilities genuinely last (S4/S5/S6).
- The resource model (§6) refuses to conflate tracked allocations with RSS/page-cache, bounds *capacity* not occupancy, and demands checked/saturating shape arithmetic — all consistent with the parent campaign's discipline.
- Coordination with the speed campaign is explicit (S0–S2 early; S3 consumes landed W2/W3 owners, does not create competing passes), and the plan correctly reuses `bindingPayload`/compaction demand roots rather than a naive tree walk (§2.2 110–113), which is the right foothold given hidden provider/mask/RNG consumers.

## Checks performed / limitations

Verified against source: lowering order and the exact four-planner sequence; per-compile table reset set; `bindingPayload`/`intervalPayloadOf`; row-fold fusion's explicit first-failure reorder (confirms §2.2's claim that current predicates do *not* establish ordered-failure); `fsproj` Optimize-before-ProviderRegistry (confirms the "cannot call the registry directly" constraint); run-record size+mtime semantics; best-effort tile store; the CUDA-file C++ segment-stream provider-hook check. I did **not** exercise the runtime (read-only), did not verify every provider-capability line (ZarrProvider/NetcdfProvider/Csv/Icechunk hook line numbers accepted as the plan states them — the plan itself asks that anchors be refreshed before work), and did not re-review the 60 PM families. graft does not index the F#/C++ here, so these were targeted reads. This is a planning review, not implementation validation; findings are about design completeness and feasibility, not working code.

<SUMMARY>
No hard blocker; the streaming extension is a code-free, unusually well-grounded plan (all spot-checked anchors accurate). Four items to resolve before their stage, not before S0: (F1) specify the region-ownership handshake with the four existing side-table planners and keep S1 disjoint from them — the "annotate narrow IR" v1 is in tension with the stated "before side tables" ordering; (F2) spool/external routes have no interpreter twin and opt-diff can't catch a silently-declined spool — add a "route-taken" assertion plus materialized `.read` oracle for S4/S5; (F3) scratch cleanup must name exit paths it cannot intercept (`_exit`/`abort`) and must pid-lock concurrent runs out of the recovery sweep (cwd-relative scratch + concurrent sessions = corruption risk); (F4) make "pin the current sort NaN/tie/stability contract" an explicit S5 precondition, not a caveat. Optional: a concrete forward-progress reservation formula to prevent budget deadlock (F5); per-run (not per-process) lifetime for the runtime ledger (F6); demote "ordered-failure proof" to deferred since IRTotality gives only totality, so S1–S4 are total-only (F7); gate ST10/ST17 on the still-unowned PM33/N01 windowing port that the parent review's H1 left undispatched (F8). Preserve: the two-seam design, honest provider scoping (Zarr-only S2), computation-reduction-before-spilling priority, capacity-not-occupancy resource model, and reuse of existing demand-root analysis. Checks: verified lowering order, table resets, payload helpers, row-fold reorder, fsproj order, run-record and tilecache semantics against source; did not run anything, did not re-verify every provider hook line, did not re-review the 60 families.
</SUMMARY>

