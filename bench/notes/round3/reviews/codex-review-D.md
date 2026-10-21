P1 VERDICT: BUILD AFTER FIXES — order is defensible; build a costed prototype before the planner.
P2 VERDICT: BUILD AFTER FIXES — start with stored rank-2 actions; split out deduction and the tower.
P3 VERDICT: BUILD AFTER FIXES — ownership is sound for the stated stored, coordinate-free case; complete admission and validation.

Reviewed bench-fortran HEAD e64fef3ba1ee6c8082e4b1abe30b76245d587b2d. Files and git only: no builds, tests, emits, programs, or timings. Numerical examples below are deductions, not executed results. Paths/lines refer to that worktree unless prefixed D-work. The requested CLAUDE.md and PREAMBLE.md were read. Graft is absent in this worktree; no index-refresh command was run.

1. MEDIUM — P1's actual pseudocode preserves order; a ticket alone does not.

   In blade_orbit_scatter.hpp:61-117, group i0 touches only R[i0..n); its diagonal block supplies the intra-group prefixes, then its column sweep supplies the remaining terms. Earlier groups have already supplied columns 0..i0-1. At group completion, every R[i] in that group is finished and no later group touches it. Thus ascending complete-group tickets, with THIRD=false, reproduce every inner chain. Advancing ONE outer accumulator through those completed R[i] in increasing i reproduces scatter2:139-140, even though scatter2 currently waits until all groups finish. Cross-chain interleaving is immaterial only because kernels/seeds are pure and total. Do not compute a per-group outer subtotal or reseed out.

   There is no counterexample to the complete-group pseudocode as written. The smallest witness against completion-order commits is n=2: z01=1, z11=2, seed=0, k(acc,z)=10*acc+z. Row 1 must receive [1,2] -> 12; committing row 1 before row 0 gives [2,1] -> 21. This kernel is admissible strict arithmetic. For outer regrouping, row results [1e16,-1e16,1], outer seed 0: the serial result is 1; combining the last two as a subtotal gives 0.

   Fix: make the ticket unit an explicit canonical interval, not a thread ID or readiness ordinal. Production must finish ALL rows read by that interval before commit. At n=2503, G=4 gives 625 full groups plus a three-row tail. A normal eight-way contiguous row partition starts near row 312/313 and can split a four-row group; do not call the existing G=4 body on an unaligned producer chunk. Replace that partition with the proposed group claims, or implement and prove an independently sized boundary group. Different G values can preserve the same per-chain sequence; byte identity does not require identical global interleaving. Pin n=1,2,3,4,5,7,8,9 and ragged team/group boundaries, noncommutative kernels, and nonzero/signed-zero seeds.

   Acquire/release on the same ticket publishes R/out; the producing thread's own pool stores precede its reads. Relaxed unique claims suffice for allocation. Ascending claims avoid a queued-predecessor deadlock, but progress still assumes runnable workers eventually run. Oversubscription and long descheduling can stall everyone. Keep producer totality; preserve callable repro boundaries and producer stores.

2. MEDIUM — P1's ticket is a serial server, and the DRAM argument does not prove the advertised ceiling.

   Arithmetic from the brief: P=2503*2504/2=3,133,756 cells; 25,070,048 pool bytes; 6,265,009 inner plus 2,503 outer applications; 626 commits. Its assumed 0.25–0.4 ns/cell gives S=0.783–1.254 ms of serialized commit service, excluding handoffs, waits, state migration, and outer applications. Average service is 1.25–2.00 us/group. A 5.4 ms producer at eight fully occupied cores represents about 43.2 core-ms, or 69 us production/group; early groups are about twice that.

   With equal production times and cyclic worker reuse, completion obeys Cg=max(C[g-1], C[g-8]+Pg)+Sg. The max contains head-of-line waiting: seven ready workers may idle behind one slow producer. General lower bounds are max(S,(43.2+S)/8)=5.50–5.56 ms before startup/drain and overhead, assuming that 5.4 ms producer measurement applies. For a hypothetical 6.3 ms result, eight-core capacity leaves only 50.4-43.2-S=5.95–6.42 core-ms for ALL waiting and other overhead; at 6.8 ms it leaves 9.95–10.42 core-ms. Serialized service need not be wholly additive to producer wall time, but it is not free.

   The largest produced group is 80,048 bytes: not all L1-resident. The approximately 6.28 MB sum of touched R suffixes is useful state bytes, not measured coherence traffic. Each named pool still incurs its stores/writeback. Likewise n² logical reads do not prove n² DRAM misses; late producer rows may remain cached. Even accepting the quoted bandwidth, one cold 25.07 MB pass costs approximately 1.57–1.98 ms: combined with the separate 5.4 ms producer estimate, 6.97–7.38 ms gives gf ratios 0.87–0.92, not a proven universal 0.82 cap. These cross-session numbers cannot decide the schedule.

   Deciding experiment, AFTER the timing session: same emitted producer, producer-only control, G35, standalone scatter, and ticket prototype; interleaved end-to-end measurements and paired CIs. Record per-group production/commit times, total wait in core-time, longest predecessor stall, actual team size, and handoff counts. Sweep G=1,4,8 and T=1,3,8 plus a restricted actual team. Define the brief's “20% wait” denominator; summed worker waits divided by producer wall time is not an occupancy fraction. Default-on follows evidence, not the 0.25 ns assumption.

3. HIGH — The claimed validation is stronger than the existing tests; C++-only scheduling needs an explicit lane fallback.

   LLVM keeps its own serial scatter only when tryOrbitScatter admits an APool, supported types, and dominating captures; otherwise it keeps its logical walk. Ignoring RowCommit metadata must retain the original stored producer and consumer IR. CliCommands:991-1015 uses the LLVM emitter's own scatter decision; merely translating a C++ marker does not establish that LLVM scattered.

   “blade test llvm compares lane text” needs qualification: LlvmTests:100-128,141 compares runtime output tokens with relative 1e-9 / absolute 1e-12 tolerances; it does NOT require exact cross-lane floating results. Separate LLVM goldens compare emitted .ll against pins, not LLVM text against C++. OrbitScatterTests:12-27 explicitly says finite-value identity, signed-zero checks, NaN-equivalence, NOT raw bits. Its cross-build comparison is printed text.

   Fix: retain the existing tests but add a dedicated bit-pattern comparison across C++ commit-on/off, G35/scatter/walk, LLVM, and interpreter under REPRO=1/CONTRACT=off. State the NaN-payload contract instead of calling NaN-equivalence raw-bit equality. Assert routes as well as values, and test noncommutative outer kernels and nonzero seeds. Do not use a green general LLVM suite as the identity gate.

4. HIGH — The five-party admission contract is incomplete across the pieces.

   - P1 §3.4 explicitly covers producer, consumer, outer kernel, seed, and intervening bindings with W+F, plus totality and dominance. Keep that. Its new hand-written parallel header must also preserve the callback-aware producer veto currently reached through CodeGenLoopNest:1555 and CodeGenExprSupport:1707-1733; do not bypass it merely because lowering once accepted omp.
   - P2 §4.4 names comp, fold, seed, weights and between-bindings, but omits an explicit compact-source producer check and an explicit outer map/fold kernel check for every supported shape. “No writes S/weights/free scalars” is not the required conservative W check on EVERY intervening binding; narrower conflict analysis needs transitive alias evidence. Require actual stored sources/weights, not names that might execute deferred initializers. A historical producer already completed at a materialization boundary is not being rerun: record that boundary rather than treating its body as newly scheduled.
   - P3 §5 names walk/kernels/seeds, but does not specify source-producer and intervening-binding handling, nor the callback-table parallel admission. The closed whitelist supports a conservative refusal but does not replace the requested shared-analysis wiring. Record that the pool is stored and no producer/binding is moved; still explicitly audit the source use and the complete scheduled body.

   Fix: specify a per-pass matrix with exact expressions passed to scheduleMayWriteIn, source materialization/dominance, outer callable, every seed, and every crossed binding. Unknown callbacks decline; any parallel route uses the shared callbackTable-backed analysis. Add effect-refusal probes for each column, including imported callees, named deferred weights, aliases, outer-kernel writes, seed writes, and an intervening writer. For P3, retain the exact G35 request/depth gate; an omp on an unrelated producer is insufficient. Threading pure leaf computations under the authorized outer workload does not license reassociating any leaf or outer fold.

5. HIGH — P2 must distinguish invariant reads from invariant consumers and preserve the ordered fold tree.

   Precise read rule: after swapping the candidate free parameters, the array denotes the SAME stable stored object/version; within each affected SymSymmetric group the full logical subscript tuple changes only by a permitted permutation; all other groups/indices are unchanged. Preserve nominal index identities, move whole compound coordinates, and prove index evaluation pure/total. Equal extents, equal-looking arrays, or a permutation crossing independent groups do not suffice. A symmetric read has identity transport. An antisymmetric read has its actual permutation-sign transport, not PInv; Hermitian transport is conjugation, not identity.

   Counterexamples to lifting that fact too far:
   - S01=2 and V=[3,5]: S(i,j)*V(i) is 6 at (0,1), 10 at (1,0). A packed read is invariant; the complete cell term is not.
   - Antisymmetric A01=2 gives A10=-2. Treating all compact permutations as PInv corrupts the value.
   - A strict outer fold over a licensed inner fold remains a mixed nest. For inner terms [1e16,-1e16,1], the ascending chain gives 1, while 1e16+(-1e16+1) gives 0. An invariant compact read permits neither flattening the nest nor substituting that inner result under an untouched strict outer chain.

   The stated one-axis H(i,j)=fold_k(T(i,j,k)*c(k)) can share H(i,j)/H(j,i) when corresponding terms, kernel, seed, k order and expression trees are identical. Symmetry alone does not prove those conditions. Keep separate row/column/third ROLE terms; evaluate g with swapped own/partner weights, not reuse one weighted term at both endpoints. Preserve repeated-coordinate event deduplication.

   Fix: implement the symmetric-read certificate separately from whole-body deduction and scheduling. Pin the three counterexamples, cross-group/compound cases, same-extent different identities, and mixed licences. The “20-line” claim also leaves a traversal gap: parityOf:807 asks par arr; its closed cases end at :851 without a mapped-computation/lambda rule. Show how the fixture's nested map body reaches the new index rule, then prove an undeclared H actually packs in a plan test. Do not infer success from adding only TExprIndex/mirrorEq arms.

6. MEDIUM — Eight channels is a bound, not a cache/register policy; P2/P3 knob contracts are unfinished.

   At n=2503, eight double row-state arrays occupy 160,192 bytes. With G=4/B=64, eight channels' Zrow+Zcol blocks occupy 32,768 bytes; adding Zthird makes 49,152 bytes, before weights, pool lines and other scratch. The 32 live row accumulators need eight 256-bit vector registers when vectorized, before column state, addresses, weights and arbitrary g temporaries. Spills are plausible, not proved. At p=171, eight packed H states need 941,184 bytes. A cap of 2^20 chains PER channel admits 64 MiB of double state across eight channels.

   Fix: bound aggregate state/scratch bytes, select channel blocking by element size/G/B/body, and preserve an alternative of several passes with fewer channels. Count rereads when blocking. Later compare K=1,2,3,4,8, inspect spills, and test K=8; the proposed correctness block covers only 1–3. A universal “joint pass must beat three scatters by 2x” is not a suitable gate for compute-heavy g.

   P2 names an optimizer gate but not a complete default/off/on/strict parser, env-table row or plan policy. P3 names suiteClearedKnobs but omits an explicit default/off contract and docs/plan details. Complete PREAMBLE constraint 5; prove optimizer opt-diff actually changes P1/P2 emission rather than comparing two inactive forms.

7. MEDIUM — P3's ownership works, but its proof and cost claim must state what is shared and what is duplicated.

   Fix a<=b. Owner a seeds H[a,b] once, pulls T(c,a,b) for c=0..a-1 in ascending c, then runs THIRD=false on slice a. Within that triangle R[b-a] receives local coordinates 0..p-a-1 in order, exactly the remaining omitted coordinates c=a..p-1. For a=b, the pull includes (c,a,a) once, and the local diagonal is fed once. Thus one stored H[a,b] represents both logical leaf chains (a,b)/(b,a); it is not split across owners. After a mandatory team-completion barrier, scatter2 over retained H preserves the middle/outer chains and their original seeds.

   The same-Z third-event reuse does NOT survive: for a<b<c, slice a evaluates f(Tabc) for H[a,b]/H[a,c], while owner b pulls and evaluates it again for H[b,c]. Pure deterministic coordinate-free f makes that legal, but potentially expensive. The stated reads are correct: 848,046 slice cells + C(172,3)=833,340 pulls = 1,681,386, with 2,514,726 leaf applications. No cross-row accumulator merge is needed.

   Fix: document this per-pair sequence proof, require the existing strict/pure/total admission, initialize all H chains once, prohibit THIRD=true in workers, and retain the completion barrier before the tail. Include all-equal/two-equal/distinct triples, group tails, arbitrary fold kernels and seeds in the identity tests. Include expensive f in the later drop experiment: the gain is fewer reads/address calculations than G35, not one f evaluation per canonical cell.

8. HIGH — The proposed benchmarks have credible expert algorithms, but not yet a fair executable contract.

   pairaction is a standard symmetric matrix/multiple-RHS action plus normalization; momenttower is a staged symmetric tensor contraction. Neither inherently requires Blade's scatter form. However “dspmv-style” and “canonical-triple updates” are algorithm sketches, not completed expert twins, especially for strict mt8. A canonical-triple loop parallelized with unsynchronized shared updates races; atomics/private partial merges do not preserve strict chains.

   Fix: provide independently implemented expert serial and mt8 schedules with the same producer evaluation contract, packed named W/T/H stores, z/u/y outputs, seeds, FP policy and input generation. Offer output-owned gathers and suitable blocked actions; licensed builds may use independently licensed private reductions. Preserve the source-stated H stage; do not compare it with an algebraically reassociated direct contraction. Separate dense-natural results from the competitive expert.

   Sizes 2503 and 171/201 meet the non-power-of-two rule. The observability contract does not: “checksum” does not specify reading every named output. D-work emit02:358 prints H only as "<rank-2 array>"; fixture 02 returns h/y, and fixture 01 has deferred intermediate consumers. Neither fixture establishes the benchmark's stored-output/checksum requirement. Require explicit materialization and independent read-back of every stored W/T/H/z/u/y element, consistently either packed physical or logical weighted reads in every arm. A checksum of y alone cannot witness every named upstream output. Pin small cases against an independent reference and retain knob-off mirrors, interleaving, medians and CIs.

9. BLOCKER — Fixture 02's saved C++ is invalid; the evidence establishes the failure family, not identical root cause.

   D-work/emit02.txt:345-347 contains only deferred comments for __v45 (t), __v53 (h), __v60 (y). Line 348 passes __v53/__v60 to std::make_tuple without declarations. Searching the whole saved emission finds no definitions for those names. This is a missing materialization at a tuple use, before any generated scatter can run; it is not numerical evidence against P2.

   RESULTS:2684-2685 calls the older defect “unforced map over a compact pool fails BL9002”; gap-register G41 records an undeclared 'g' for a writing virtual source. Fixture 02 is pure and explicitly requests (h |> compute, y |> compute), so it adds a tuple-of-computes trigger. Classify it as the same documented deferred-materialization/undeclared-name family, with root-cause equivalence UNCONFIRMED. Emitted C++ alone cannot prove both frontend paths lose forcing at the same site; do not close the old card as a duplicate without that evidence.

   Fix: ensure each tuple member's compute is materialized and bound in the emitting context, recursively forcing required compact sources while retaining required stores. After the timing session, distinguish direct compute, tuple-of-computes, precomputed tuple members, dense/compact, and pure/writing-source cases in both lanes. Keep this reproducer on the existing card as a separate acceptance case until root cause is traced.

Hook verification (five spot-checks):

| Brief claim | Current source evidence | Result / correction |
|---|---|---|
| planStrict at OrbitFold:698; antisym/rank/mixed/state refusals :715/:717/:721-729/:774 | Those branches and locations match; :787-788 read group/block tuning | Holds. These are existing strict-walk gates, not new commit/action admission. |
| tri :57, scatter2 :135, scatter3 :151; rank-3 same-Z third events and retained H | Header :57 is the template, :58 the function; third writes :72/:113/:128, retained-H scatter :161 | Holds, with one-line declaration convention. THIRD=false is essential to P1/P3. |
| genOrbitScatterCpp :4120; deferred refusal :4237-4239; compactRows :4255 | Emitter starts :4120. :4237-4238 checks undominated captures; actual deferred-source refusal is :4239-4241. compactRows binding is :4254, match :4255 | Main hook holds; deferred-refusal span is inaccurate. |
| LLVM tryOrbitScatter :6581 / emitOrbitScatter :6621; §3.5 marker rewrite ensures serial scatter | Functions match; :6611 requires APool. CliCommands:991-1015 merges/preserves the LLVM emitter's own record and explicitly allows the serial walk | Function hooks hold; unconditional “LLVM keeps the serial scatter”/marker-only translation is overstated. |
| Deduce :818 uses allInv(arr,indices), mirrorEq :91 lacks compact permutation handling | Exactly :818-819 and :91-94; parityOf's closed fallback is :851 | Holds as a gap. These two local edits alone do not demonstrate deduction through the fixture's nested map/fold. |

Ranked build order:

1. Fix/track fixture 02 materialization and establish the stored-output benchmark contracts; do not bury that defect in a new optimization.
2. P3, after admission and identity gates: smallest change to an existing measured workload; retain its serial LLVM fallback.
3. P1 standalone ticket prototype and wait accounting; build planner/emitter integration only if paired end-to-end evidence survives.
4. P2 rank-2 single-channel actions, then bounded multi-channel sharing with fair pairaction experts.
5. P2 rank-3 one-axis tower with declared H symmetry; land automatic deduction separately after read/whole-body proofs and negative pins.
