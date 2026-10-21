# Round 3 attack review — A-census

Read-only review, 2026-10-20. No compiler, build, test, timing, or C++ compiler invocation.
Source baseline: bench-fortran e64fef3b (BF); wave-3 magical-cannon-db55bf b80e29ad plus its current notes (W3); effect follow-up vigilant-hellman-c521dc (VH). Paths below are relative to those worktrees unless prefixed A-emit/research. Only this review was written.

## Verdicts

- **A1 — MEASURE FIRST.** Legal under the stated strict identity conditions; valid zero-code experiment; proposed cost rule is not established.
- **A2 — BUILD AFTER FIXES.** Build the narrow repro row-view admission; repair seed/empty/effect handling before the licensed helper arm, then measure it.
- **A4 — MEASURE FIRST.** Separate retention-policy arm, contingent on the user's footprint/protocol decision; no silent default change.
- **A5 — MEASURE FIRST.** In-team complete-row folds are plausible; locality and net saving are unproved.
- **A7c — BUILD AFTER FIXES.** Complete the totality, capture, seed-dominance and per-leg permission admissions; instrument before generalizing.
- **A8 — MEASURE FIRST.** Fix the runtime hook and prove the complete coordinate-demand census; consolidate with B/T4.
- **A7a — MEASURE FIRST.** Cross-function generator fusion lacks a concrete admission/implementation/knob design.
- **A7b — MEASURE FIRST.** Prove wrapping integer bounds and inspect the future generated instructions before promising vector multiplication.
- **A6 — DROP from this build round.** Keep as research: ordered-commit coordination does not remove the strict chain floor.
- A3 is not defined in the submitted brief. A6 is below the line; A7 has three distinct proposals and cannot receive one meaningful verdict.

## Findings (attack order)

1. **MEDIUM — A1 is legal, but the explanation conflates per-chain order with global execution order.**
   BF OrbitFold.fs:698-789 audits the summand, EVERY fold kernel (including the outer one), and seeds; rejects coordinates in those expressions, non-scalar captures, unsafe calls, mixed licensed/strict nests, antisymmetry, and excessive state. CodeGenBinding.fs:4235-4249 additionally requires the stored pool and dominating materialized scalar captures.
   
   Rank 2: define R[i] = fold(K1, s1, [f(S(i,j)) | j=0..n-1]). The row-slot emitter evaluates that entire expression once into slot[i], then folds slots in increasing i from s0 (CodeGenLoopNest.fs:2280-2284,2373-2380; A-emit/pairkernel_repro.cpp:298-319). The scatter initializes every R[i] with s1, feeds mirrored terms from preceding rows in ascending order, then its diagonal and following columns in ascending order; its outer fold also starts at s0 and visits increasing i (blade_orbit_scatter.hpp:41-50,67-128,133-142).
   
   Rank 3: H[i,j] is the leaf fold over k, from s2. Symmetry plus coordinate-free f/K2/s2 makes H[i,j] and H[j,i] identical chains, so sharing them is legal. Ascending slices plus the third event feed each unordered pair its ascending-k terms; scatter2 over retained H reconstructs the middle and outer chains from s1/s0 (header:145-162). There is no multiplicity-weighted reassociation.
   
   Thus both strict forms have identical results for identical stored inputs, seeds, kernels and FP environment with **REPRO=1 and CONTRACT=off**. REPRO alone does not disable contraction: moving/materializing f(x) can change whether a multiply contracts with a fold add. Coordinate-dependent seeds/terms, mutable captures, or a deferred pool invalidate this argument and must remain refused. The current gates cover them.
   
   **Fix:** replace “no reordering of kernel applications” (brief:189) with “interleaving changes, each chain's operand order does not.” Retain planStrict's outer-kernel audit; only the duplicate G35 admission becomes unnecessary on the selected scatter path.

2. **MEDIUM — A1's counts support an experiment, not a threaded cost model; its milliseconds mix baselines.**
   
   | Quantity | pairkernel n=2503 | comoment3 p=171 |
   |---|---:|---:|
   | Logical pool reads | 6,265,009 | 5,000,211 |
   | Canonical pool reads | 3,133,756 | 848,046 |
   | Nominal pool bytes: logical / canonical | 50.120 / 25.070 MB | 40.002 / 6.784 MB |
   | Scatter state | 2,503 rows = 20,024 bytes | 14,706 pairs = 117,648 bytes, plus row scratch |
   
   Pairkernel still performs 6,265,009 inner chain updates, not half as many. Rank 3 shares leaf chains: 14,706*171 = 2,514,726 leaf updates, plus the unchanged middle/outer work. These are useful savings in indexing, term evaluation and access locality, but eight row-slot workers versus one scatter worker is not settled by P < L/1.5.

   Using the **C++** medians behind RESULTS 15.3/15.3b (round2/w1-ab.md and w2-ab.md):
   
   | Arithmetic | pairkernel | comoment3 |
   |---|---:|---:|
   | Serial repro with scatter | 29.520 ms | 9.056 ms |
   | Serial licensed comparator | 28.140 ms (w1) | 8.643 ms (w2) |
   | Inferred excess, not a phase measurement | 1.380 ms | 0.413 ms |
   | mt8 repro / licensed | 9.354 / 6.157 ms | 4.449 / 3.118 ms |
   | Inferred mt8 strict excess | 3.197 ms | 1.331 ms |
   | Hypothetical licensed base + serial excess | 7.537 ms | 3.531 ms |
   | Hypothetical speedup / expert ratios gf,ifx | 1.241x / 0.852,0.824 | 1.260x / 1.382,1.165 |
   
   Comoment3's w2 expert times are **4.880 / 4.115 ms**, not the brief's w1-like 4.91 / 4.32; 4.449 is its w2 Blade time, not ~4.55. Pairkernel's licensed comparator is from another session/wave and uses producer-fused row classes; comoment's licensed orbit fold is also nonzero work. Neither subtraction isolates the strict consumer. Serial affinity, producer contraction, cache state, and teardown further prevent adding these deltas as measured phases.
   
   **Deciding experiment:** interleave unchanged mt8 repro with ROW_SLOTS=0, keeping ORBIT_SCATTER on and CONTRACT=off; assert the selected form and unchanged producer, compare raw output bits, total time and consumer phase separately. Preserve row slots where they win. Do not adopt the 1.5 threshold from these two shapes. Re-evaluate each kernel independently rather than dropping both if only one misses 10%.

3. **MEDIUM — A1's drop switch is valid; the LLVM and D conclusions need narrower wording.**
   rowSlotAdmission checks ROW_SLOTS=0 first (CodeGenBinding.fs:72); rowSlots then becomes None, and scatterTaken selects the existing eligible scatter (4450-4474). Producer team emission is independent. This is a valid no-source-change experiment **for these admitted stored-pool walks**; the switch does not guarantee scatter for arbitrary folds.
   
   LLVM takes its own stored-pool scatter at EmitLlvm.fs:6581-6618, with no row-slot precedence or OpenMP producer. An mt8 environment does not make LLVM threaded; w2-ab marks its mt8 arm “serial-only.” It is a serial reference, not evidence for the C++ hybrid.
   
   Brief:145's “D has no current-suite cell left” does not follow from pairkernel reaching 0.95: comoment3 remains separate, and D/P3 explicitly proposes a rank-3 pull consumer. Nor does a “2x reads” count refute pull: cache residency, canonicalization removed and independent chain throughput matter; W3's read probe measured 53 GB/s at T=4 versus 33 at T=8.
   
   **Fix/experiment:** retain A1 as D's measured baseline, not a proof that parallel strict consumers cannot help. Time D/P3 against the winning A1 form before discarding it.

4. **BLOCKER — A2's literal helper rewrite loses explicit seeds.**
   Brief:196 admits reduce(u(i), op, s), but :200-201 replaces its complete inner fold with sum(row,n). For row [1,2] and seed 7, the original result is 10; that helper returns 3. A reorder licence does not license removing a seed.
   
   **Fix:** evaluate the original seed exactly once in its original scope; combine it exactly once. For the unseeded spelling, preserve head peeling, n=1 behavior, and the empty-row BL8003 rule. Do not turn an unseeded empty fold into the helper's zero. Explicitly test nonzero seeds, empty/singleton rows and signed zero. Never call this reassociating helper on a strict inner fold merely because the outer fold requests omp.

5. **HIGH — A2's admission is incomplete; the emitted diagnosis itself is confirmed.**
   A-emit/stencil_lic.cpp:324-327 puts BLADE_OMP_SIMD_REDUCTION on the row loop around eight scalar accumulators; the IIFE repeatedly spells the row view and __wrap_70. It does **not** call blade_fold::sum. This proves source shape, not that g++ actually vectorizes across rows or that view construction survives optimization.
   
   The applicable vector helper is blade_fold_lanes.hpp:345-349; :141 is **model::sum**, its scalar reference. sum<double,8> has eight four-wide accumulators (32 chains), versus the emitted eight scalar chains. It is a sensible licensed candidate, not an identity-preserving replacement for that tree.
   
   **Required rowViewFoldAdmission matrix:**
   
   | Role | Required proof |
   |---|---|
   | Producer/base | Dominating, already materialized nonvirtual dense rank-2 array; row contiguous; correct Y index identity; reject deferred producers/aliases that would execute here. No producer evaluation moves; if a generalized version moves one, audit its full kernel with scheduleMayWriteIn and callbackTable. |
   | Consumer | Audit the entire row callable, including leading IRLets, and inner fold callable through shared transitive scheduleMayWriteIn plus IRTotality.auditCallable; only known-total operations/callees. |
   | Outer kernel | Same shared write audit and totality whitelist; its own explicit threading request. Preserve ascending outer combination for repro; no licence transfer. |
   | Seeds | Audit BOTH inner and outer seeds; preserve evaluation count, scope, captures and original value. Handle nonempty proof/BL8003 for unseeded folds. |
   | Intervening | Audit every statement whose relative order changes, including the row spine; reject writes through aliases to u or captured state. No cross-binding motion in the narrow v1; say so explicitly. |
   
   Extend both isCompleteFold and IRTotality's exact row-view IRReduce case; return the real row work count for rowslots_min. Require inner foldReorderLicensed, no repro veto, supported float type, and FOLD_LANES enabled for sum. Removing outer simd also needs an explicit licensed-only choice.
   
   **Deciding experiment:** separate repro admission from licensed lowering. Pin original spelling versus nested-range spelling bitwise in repro; compare helper versus old licensed form under its documented numerical tolerance and original seeds. Future assembly/vectorization report plus per-phase timing decides the licensed performance hypothesis. Existing FOLD_LANES on/off can serve the A/B, but it must actually gate this arm and appear in the plan.

6. **MEDIUM — A4 is flagged as a user decision, but its memory/lifetime claims are inaccurate.**
   bench/README.md:59-76 requires named stores and explicitly records destruction as asymmetric; it does **not** require all arms to deallocate inside their timers. recur/expert.f90 stops at :103 and deallocates at :115; stencil/expert.f90 stops at :79 and deallocates at :91. Their named output deallocation is excluded; worker panels/compiler temporaries may still die inside.
   
   Raising retention changes the runtime-policy arm and the disclosed timed-work/lifetime comparison; it does not violate materialization parity. Keep the user's decision. Do not present relocating destruction as less total application work.
   
   The retained recur payload is 301*20011*8 = **48,186,488 bytes**. The budget is **per thread**, not process-wide. Retention lasts until reuse, eviction, flush, or thread exit—not unconditionally until exit (nested_array_utilities.hpp:137-151,153-196). The harness reads process PeakWorkingSetSize after exit (run.py:143,166,250-257), including allocation overlap outside the reported kernel timer.
   
   Brief:210's “peak ... changes only when later allocations reuse the pages” is false: retaining A while unrelated B allocates can increase the high-water mark without reusing A. Conversely, merely keeping the last allocation alive longer need not change an already attained peak.
   
   **Fix/decision:** separate default and opt-in retention arms; record max-block, per-thread/process budget implications, kernel time, wall_s and peak_working_set. Preserve all stores. Apply/record the build defines in both lane runtimes. Ask the user to choose the footprint policy after these arms are concrete; this review does not authorize a default change.

7. **HIGH — A5 omits parts of the new motion admission and defines T ambiguously.**
   W3 CodeGenBinding.fs:6280-6302 really folds complete rows AFTER the producer team, then combines slots in order. Moving K into that team needs the full shared audit for producer, K, outer F, both seeds and intervening statements; “E-G5 + G35” is not a complete written matrix for the new placement. K must read only its completed row plus immutable dominating captures, never unfinished trajectory state.
   
   **Fix:** T = actual omp_get_num_threads() for publication and ownership; every worker, including an empty-x worker, publishes once per produced row. Initialize counters and handle seed rows explicitly. Use atomic RMW release increments and an acquire observation of T; retain the RMW release sequence, single owner per slot, immutable completed rows, and a final team completion before the outer fold. No worker may wait for an unpublished row while it still owes production needed by another owner.
   
   The cache claim is speculative: a worker may lag far enough that its row is no longer in L3. The “3.1 ms post-pass” is a difference between two whole regimes (13.72-10.58), not a directly isolated cost; producer code and teardown may differ too.
   
   **Deciding experiment:** compare complete total times for identical strict producers with post/team folds, measure polling/publication/fold work inside the team, and check raw bits at limited actual teams. “Post-pass <1 ms” alone can pass merely by moving all its work into a slower producer region.

8. **BLOCKER — the census blanket “NOT BUILT” for blueprint rank 7 would rebuild existing grouping machinery.**
   The first row of §1.4 groups ranks 1/3/4/5/**7**/13 under NOT BUILT. External whole-region-blueprints.md:164 specifically includes direct-index bounded-key grouping.
   BF CodeGenBinding.fs:618-650,720-786 already selects positional Idx<N> buckets, EnumIdx lookup, or dynamic hash discovery; EmitLlvm.fs:3745-3757 mirrors the numeric static regimes.
   
   **Fix:** split rank 7 to **PARTIAL / PRE-EXISTING physical dispatch**. Missing work is wider fact-based region selection, aggregate-only execution, contiguous-segment recognition/sharing—not implementation of positional buckets or basic key-type dispatch again. Other members of the combined row retain their narrower missing-region status.

9. **MEDIUM — ten census spot checks; one incorrect status and one overstrong evidence label.**
   
   | Census item | Check against current code/record | Result |
   |---|---|---|
   | Ordered symmetric scatter, both lanes | OrbitFold.planStrict:698; C++ header:135/151; LLVM:6621; RESULTS 15.3b | BUILT correct; do not rebuild. |
   | B3 compact row classes | Optimize.planCompactRowClasses:3042, producer/write gates:3118-3125; CodeGenBinding:4254-4261; RESULTS 15.3 | BUILT/PARTIAL correct; rank-2 scalar producer route, not general contraction/rank-3 epilogues. |
   | G7 join team | CodeGenFusion.joinedFoldThreadPlan:590-627 independently licenses every leg; RESULTS 15.3b dot/onepass/fusedchain | BUILT correct; C++ only. |
   | W1-D sibling folds | Optimize.shareSiblingFoldsModule:3906, stored operands:4102+, epilogue reservation:4309/4555; RESULTS 15.3 | BUILT for stored-axis siblings; producer legs missing. |
   | Simplex live-edge loops | CodeGenExprSupport.triTileEdge:2198 default 1; blade_packed_gemm.hpp:555-604; RESULTS 15.3b | BUILT correct; ~2% serial gain, mt8 indicative. |
   | Independent recurrence team | W3 CodeGenBinding:6251-6260 and strict post-pass:6280; RESULTS 16.1 | BUILT C++ correct; no halo/time-block generalization implied. |
   | Stencil row-view G35 | CodeGenBinding:85-88 accepts only IRReduceCompute; IRTotality:173-195 has no partial row-view/IRReduce arm; W3 G48 | NOT BUILT correct. |
   | Maskagg position elision | CodeGenBinding:2295-2302 fills y and pos; index_types.h:357-410 owns/adopts positions | NOT BUILT correct; B4 itself is already built. |
   | Relational physical selection | contains remains linear (CodeGenExpr:572-588); grouping already has three regimes | Combined NOT BUILT row wrong for rank 7; see finding 8. |
   | N01-N10 benchmark workloads | BF bench/kernels contains only the existing 12 directories; RESULTS 15.5; C has checks/sketches, not benchmark experts/specs | NOT BUILT as suite workloads correct; existing AD/halo/solve primitives are not missing wholesale. |
   
   Additional label correction: §1.1 priority 2 and §1.2 recur call B2l “REFUTED.” W3 second-look-actions.md reports 13.3 versus 14.0/13.7/13.0 ms with overlapping ranges: **NOT BUILT; no detectable gain in the tested instrument**, not an established slowdown or general refutation. These and rank 7 are the census rows I would change; no other wrong status found in the spot checks.

10. **MEDIUM — record collisions and stale rows are real.**
    W3 gap-register.md:98/99 assigns G48=row-view admission, G49=teardown. VH :98-100 assigns G48=forcing named choice/fallback fold operands, G49=LLVM compact-source print-list forcing, G50=once-at-let writing wrappers; those are FIXED follow-ups.
    
    **Fix:** retain VH G48-G50; renumber W3's two descriptive/open cards to G51/G52 after confirming the integration allocator has not assigned them. Update all W3 notes, RESULTS, research briefs and later plan references; retain an old-to-new mapping. This preserves the contiguous fixed defect/test history.
    
    Both gap registers still say G37's fix is “in progress” (:87). Git confirms 963b9aa1 is an ancestor of BF e64fef3b; CodeGenExprSupport.fs:247-264 documents the scoped conversion, and A-emit/saxpy_repro.cpp:195 uses static_cast<double>. Close implementation status; separate any remaining measurement task.
    
    W3 RESULTS 16.3 at :2740-2741 is **empty at review time**. tables.md is not an accepted populated 16.3. Preserve contamination/suppression labels even where a contaminated table says “live.”

11. **HIGH — A7c/A7a need totality and seed/capture availability, not just write freedom.**
    A7c names scheduleMayWriteIn, but moving a pure throwing fold/seed ahead of later production can still change failure order; captured values defined between producer and consumer may not exist at the earlier site. Its input legs must also remain disjoint from the output leg B1 already owns.
    
    **Fix:** name a producerEpilogueLegAdmission using the shared transitive write/callback analysis plus closed totality, dominating captures/seeds, operand/index equality, alias checks, and a complete intervening-binding audit. Preserve each leg's own seed and per-leg licence, including the output leg and outer combine. Reject threaded strict legs; bind results at their original points. A7a must supply the equivalent proof across BOTH generator calls, plus its own knob, plan record and optimizerGates entry if IR changes. Do not treat “B's provenance seam” as already designed: B explicitly says its initial T1-T4 do not require that Stage-0 seam.

12. **MEDIUM — A7b and A8 benefits remain hypotheses; A8's hook points at the wrong representation.**
    The generator emits the claimed Int64 arithmetic (A-emit/saxpy_repro.cpp:189-195). A range proof must model every wrapping intermediate, not assume mathematical nonnegative arithmetic. Widen before multiplying narrowed operands; uint32*uint32 followed by widening is wrong. Cover k+seed and all additions/divisions, negative inputs and overflow fallbacks. A C++ spelling change does not establish that vpmuludq is selected.
    
    A8 targets compound_index_t<1> at index_types.h:**357**, whose positions member is :384 and adopting constructor :407-410. Brief :95-119 instead names abstract_hashed_idx_t, a different representation.
    
    **Fix/experiment:** measure A7b's input-only twin in both lanes, retaining stored inputs; report total and twin separately. For A8, use a demand certificate through aliases, returned values, callable captures, unhash/coordinate enumeration, present/keyed/prefix access, printing and provider output. Reject unknown escape; preserve n/cardinality and all named stores. Do not leave rank_to_tuple.n=0 pretending that it is cardinality. Compare a positions-free instrument against default and merge the implementation scope with B/T4.

13. **MEDIUM — ranking gives estimates more authority than they have.**
    A1/A2's projected ratios rank above B; A5/A7c/A8 rank above C and D without a measured new implementation. A4 has a measured instrument, but it relocates work and needs a policy decision. B's “new cells only” is also too broad: B/T4 explicitly targets current maskagg, overlapping A8.
    
    The strongest below-line measurement lead is **anti_g under threads**: w2 auto/default 0.954 [0.904,0.996], plus the flagged W3 ~5.7% signal. Still not a default-change result, but a cheaper, better-evidenced remeasurement than A5 or A7ab implementation. Band's proven-check/per-row-overhead idea is cheap to inspect, not yet stronger performance evidence. Antisym scatter and exp batching are not supported by a measured win.
    
    **Fix:** rank experiments separately from build commitments; do not sum A2/A5/A4 cross-regime estimated savings into a parity claim. Use the quiet W3 rerun when published.

## Hook verification (five claims)

| Brief claim | Actual BF hook | Verdict |
|---|---|---|
| G35 wins over scatter, :4465-4475 | CodeGenBinding.fs:4469-4474; rowSlotAdmission:72; OrbitFold.fs:865/869 | Holds; disabling slots yields scatter only when scatterPlan exists. |
| Row-view fold rejected by isCompleteFold | CodeGenBinding.fs:85-88; IRTotality.fs:173-195 | Holds; two separate admission barriers. |
| blade_fold::sum at header:141 | model::sum:141; vector blade_fold::sum:345-349 | Wrong namespace/site; fix to :345. |
| Pool caps at header:104-115 | nested_array_utilities.hpp:106-109, release:180-196 | Holds: 4 MiB max, 8 MiB per-thread budget; eviction can precede exit. |
| compound_index_t / positions at index_types.h:95-119 | abstract_hashed_idx_t:107 there; compound_index_t<1>:357, positions:384, adopt:407 | Wrong type/site; fix A8 hooks. |

## Re-ranked work queue

1. **A1 zero-code experiment**, then a measured precedence choice; retain both strict forms.
2. **A2 narrow repro fix**, with corrected admission; independently instrument licensed sum.
3. **B T1/T3**, with pre-existing grouping dispatch acknowledged; consolidate B/T4 and A8 planning.
4. **C's selected initial workloads and expert/store contracts**, before spending on general D schedules.
5. **anti_g quiet threaded remeasurement**; no default flip from flagged sessions.
6. **A7c admitted producer-input legs**, instrumented before implementation expansion.
7. **A8/B-T4 positions-elision instrument**, then a demand-certified implementation if worthwhile.
8. **D**, first the rank-3 pull comparison against A1 and new action fixtures; no blanket rejection from byte counts.
9. **A5 strict in-team instrument**, judged on full strict-run time, not disappearance of a post-pass.
10. **A7b input-generator instrument; A7a later**, only after an explicit cross-function design.

**A4 is a separate user policy decision, not a kernel-speed rank.** If accepted, measure it as its own recorded runtime arm and rebase affected comparisons. A6 stays outside this build round.
