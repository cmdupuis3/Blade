# Codex adversarial review: round 2 D / E

Reviewed against master `4136f0b5a42f532b5d43c6e158f0961635d62b13`, 2026-10-19.
Read PREAMBLE first, both briefs, CLAUDE.md, the round-1 review, RESULTS 14.8,
gap-register, and microkernel negative evidence. No builds, test suites, or timings.
Fresh C++ emission: pairkernel and onepass licensed, recur repro; fresh dot plan.
Commands used the supplied Release binary, ucrt64 first on PATH, BLAS off, LLVM off,
contract off, OMP_NUM_THREADS=8, OMP_THREADS unset, and the existing private
`research/E-private-cwd`. Emission was inspected in memory; no emitted files were written.
Graft MCP was approval-blocked; its local index supplied no relevant symbol cards.
Repository status remained unchanged (the same three pre-existing untracked CSV fixtures).
Only this review file was written. Paths below are repository-relative unless marked D/E.
D/E mean the supplied research briefs; fresh-emission line numbers refer to stdout.

## Verdicts

| Item | Verdict | One-line reason |
|---|---|---|
| D1 | ACCEPT | Accept the conditional DROP: counts favor deferral, but modeled balance and guessed fork costs are not measured limits. |
| D2 | ACCEPT WITH CHANGES | Whole-call grouping preserves chains; byte counts are correct, but cache traffic and speed claims need qualification and both drivers need matching scratch/ABI changes. |
| D3a | ACCEPT | Accept DROP at the benchmark shape: the independent visit-count model reproduces ideal eight-worker balance. |
| D3b | ACCEPT WITH CHANGES | Dead-vector counts hold; the proposed L0/L1 dispatch is wrong for the live left-only head panel. |
| D4 | ACCEPT WITH CHANGES | Hand producer-local scheduling to B, but retract attribution of the entire gap and the unmeasured dismissal of team costs. |
| G7 | ACCEPT WITH CHANGES | Require an explicit request on the joined traversal plus independent licences on every fold; unchanged onepass does not meet that policy. |
| G5-admission | ACCEPT WITH CHANGES | Existing syntax is sufficient in principle, but current fusion rejects it; permission preservation and a fair fused baseline are prerequisites. |
| G5-persistent | ACCEPT WITH CHANGES | Same-x ownership is sound; preserve inner/outer checksum contracts separately and finalize/revalidate metadata after rewrites. |

## Numbered objections and required corrections

1. **D2: “bitwise for any G” needs the complete-call invariant, not identical global store order.**
   `src/cpp/blade_tiles.h:325-359` initializes separate P/Q states, advances pc and s
   ascending, evaluates P as X(i)*Y(j), Q as X(j)*Y(i), then calls the store once.
   `blade_tiles.h:283-298` subtracts P-Q once and applies the source scale once, writing
   only j>i. `src/cpp/blade_packed_gemm.hpp:417-451` supplies disjoint strict-packed
   row offsets and whole-n tile calls. Therefore q-then-g grouping is bitwise for any
   supported positive G if it visits exactly the same (i0,j0) tiles with the same
   parameters and does not interleave unfinished calls or alias output with inputs.
   Global store order DOES change; final pool bytes do not. There is no reduction
   between stores, no stored diagonal, and no scale pass whose order can change.
   Correction: state those invariants; test G tails, column-block boundaries, p<=1,
   empty n, both variants, both contraction modes, and forced G values. “Any G” is
   not permission for unchecked scratch arithmetic or an unbounded stack array.

2. **D2: B accounting is correct, but it counts a reuse model, not measured L3 bytes.**
   Both X and Y B panels are explicit at `blade_packed_gemm.hpp:423-432,449-451`.
   One full visit loads 2*8*n*8 bytes of B for 2*6*8*n issued scalar-lane FMAs:
   **1.333 B/FMA**, not twice that. Independent enumeration using the top-aligned
   panels (`blade_tiles.h:203-215`) gives 15,150 visits at p=1201:
   B=777.6192 MB at G=1 and 196.3296 MB at G=4, or **0.336634 B/issued FMA**.
   The denominator includes padded work (583,214,400 FMAs; useful work 577,921,200).
   A-panel rereads remain 583.2144 MB: modeled A+B traffic falls **42.72%**, not 75%.
   Correction: label these “B bytes if each distinct group/panel fetch misses L2.”
   Source alone cannot establish that every such fetch came from L3, or that reducing
   those bytes reduces wall time proportionally. D:13 currently states that assumption
   as fact; `src/microkernels/README.md:158-164` records exactly this cache-level trap.

3. **D2: the G=4 capacity calculation works; the performance conclusion is unproved.**
   At n=401, packed A for four panels is 153,984 B; the TWO B micro-panels are
   51,328 B. Total **205,312 B = 200.5 KiB**, leaving 56,832 B below 256 KiB.
   Add both P/Q spills (768 B), the source-level vector accumulator array (384 B),
   pointers and small bookkeeping; this still fits. This budget concerns the active
   B pair, NOT the entire packed B allocation (7,750,528 B). Account for transient
   output/cache-line traffic and packing as headroom, rather than claiming a cache
   residency guarantee. At n=1001, G=2 needs 320,320 B, while G=1 needs 224,224 B;
   the proposed gate's fallback is consistent.
   Short K does make one existing A+B visit fit L2 (89,824 B). It does NOT imply B
   survives until the next row-panel's use: that reuse follows a long suffix of the
   7.75 MB B stream (`blade_packed_gemm.hpp:434-452`). My simple capacity estimate
   leaves only the last ~76 columns' suffix fitting 512 KiB after one A pair, about
   **0.4%** of triangular work. Thus short K alone does not refute grouping.
   My speed estimate is conditional: G=4 removes ~74.75% of modeled B-fetch bytes;
   if exposed B-fetch stalls occupy 10-20% of tile time, savings would be ~7.5-15%
   of tile time, before grouping overhead, and less end-to-end. If execution or L2/L1
   supply already hides those stalls, the gain is zero. These are estimates, not
   timings. Serial gram's flat C++ and ~6% LLVM G1-to-G4 results
   (`bench/RESULTS.md:2223-2248`) favor a modest hypothesis, not a fourfold speedup.

4. **D2: anti_kc does not multiply spill state by G; do not accidentally redesign it.**
   `blade_tiles.h:311-315,330-357` balances the sample blocks and keeps **two**
   48-double partial arrays local to the current tile call, not per row-panel across
   visits. With n=401 and cap=200, blocks are 134/134/133, not 200/200/1.
   q-then-g calls finish all those blocks before moving to the next tile. G multiplies
   only packed A storage. A pc-outer rewrite would require per-unfinished-tile P/Q
   state and is outside this proof. Correct the stale “no K blocking/no partial is
   ever stored” comment at `blade_packed_gemm.hpp:373-375` when implementing.
   The live arithmetic budget stays 12 vector accumulators + two B vectors + a
   broadcast per pass (`blade_tiles.h:318-324`); G creates no simultaneous G-fold
   accumulator set. Actual compiler spills remain an assembly check, not something
   the source-level register count proves.

5. **D2: the LLVM change is a second driver change, and raw bits alone cannot prove it ran.**
   `src/cpp/blade_llvm_tiles.c:315-358` has the same jc/i0/q structure and calls the
   same antisym cores, so the whole-call grouping argument applies. Its scratch
   gate and layout are at :269-272,306-313; the emitter separately computes
   `2*n*w + 2*rm*n` at `src/EmitLlvm.fs:243-247`. Both must become G-aware, including
   the axp/ayp offsets and every declaration/caller of the changed ABI.
   `blade_llvm_tiles.c:367-381` silently executes scalar chains for insufficient
   scratch. Therefore `llvm tiles` could remain byte-green while losing the tile.
   Extend `src/cpp/llvm_tiles_rawbit.c:50-66,235-249` with G, matching scratch sizes,
   exact-size and deliberately short-scratch cases, and a positive route assertion.
   Preserve all raw-bit expectations: grouping changes no numerical contract.
   No optimizer gate is needed for these runtime/emitter-only changes.

6. **D3b: the 7.3% count holds, but the proposed specialization is incomplete and can skip live work.**
   I independently enumerated `blade_packed_gemm.hpp:528-553`: at p=171 there are
   19,568 visits, 234,816 vector-accumulator updates per sample, and 17,236 wholly
   dead updates (**7.3402%**). Useful/staircase/padded scalar lanes are
   **90.2884% / 9.3341% / 0.3775%**. Do not call 7.3% “padding”: it includes dead
   staircase halves. The stated issue model gives 4.0385%, not a wall-time result.
   D:200-203 sets L1=0 when nr<=4, assumes L0<=L1, and broadcasts only rows<L1.
   At p=171, i=0, j0=0, k0=0, mr=6, nr=3, the first panel has six real cells.
   The proposed formula gives L0=4, L1=0: the claimed domain excludes it and the
   broadcast rule skips its LIVE left half. There are three such left-only visits
   at this shape. This is a correctness blocker for that pseudocode.
   Correction: for half h, use zero when nr<=4h; otherwise the mathematically signed
   count `clamp(c0 + min(nr,4h+4) - r0 - S, 0, mr)`. Broadcast rows below
   max(L0,L1), and include the left-only (L0>0,L1=0) family. Avoid unsigned underflow.
   Pin nr=1..4 explicitly, not merely p mod 8 with loose numerical pins.

7. **D3b: preserve per-cell arithmetic; compare against the closest failed diagonal optimization.**
   The existing edge path runs the full kloop and masks only reload/store
   (`blade_tiles.h:164-198`). Omitting wholly dead vector accumulators or using a
   narrower column edge is bitwise if each live cell retains its ascending sample
   chain, exact partial reload, `(X(i)*X(j))*X(k)` factor order, and final scale.
   W formation and K-block order remain `blade_packed_gemm.hpp:540-553`.
   The closest refuted idea is **paired diagonal triangles**, which also attacked
   staircase waste: `src/microkernels/README.md:148-156` reports -4.9% at n=337
   despite bit-exactness. D3b differs by retaining panel layout and operands and
   deleting dead updates, rather than blending two operand panels. It also avoids
   the per-i repack failure (:140-146). Neither distinction proves a win: narrow
   variants lose independent chains and add dispatch/code size. Keep wave 2 and
   the measured DROP gate; the 4% issue model omits loads, stores and dispatch.

8. **D1/D3a: accept the drops as prioritization, not proofs that scheduling cannot help.**
   `blade_packed_gemm.hpp:285-350` gives one jc block and three pc blocks for
   p=1601,k=1001,KC=334. Each pc contains a parallel region, static packing loop,
   and dynamic(1) compute loop: **3 fork/join regions and 9 source-level implicit
   barriers** when joins are counted. Using that same convention, D's proposed
   team=1 has 6 loop barriers PLUS the final join = **7**, and team=2 has 3 PLUS
   join = **4**, not 6/3 (D:142-143). Runtime barrier coalescing/cost is unmeasured.
   Independent greedy equal-cost-visit scheduling reproduces **1.006906** for the
   67 syrk groups and **1.000000** for the 171 simplex i tasks (largest 330/19,568
   visits). This matches the dynamic ascending loops at :348-349 and :566-567.
   It is a cost model, not observed dynamic scheduling: packing, edge cost, cache
   misses, worker start times and runtime dispatch can differ. C exceeding L2 also
   does not prove that stable ownership saves literally no lines.
   Correction: retain D1-i and the existing mt8 G sweep; do not promote the assumed
   10-40 us into a result. Weighted simplex tasks have no demonstrated problem to
   solve at this shape. No new implementation of either dropped item is justified.

9. **D4: the separate checksum pass is verified; “the gap IS that pass” is not.**
   Fresh pairkernel emission has the producer's dynamic team at stdout:290-301,
   followed by a distinct orbit team at :313-340 reading the stored pool at
   :324,332. The input fill and input fold provide two more teams. Source emitter:
   `src/CodeGenBinding.fs:3562-3599,3640-3652`. Pool size is exactly
   3,133,756*8 = **25,070,048 B**. The expert folds each completed column inside
   its producing task (`bench/kernels/pairkernel/expert.f90:48-71`); its largest
   column is ~20 KB. This proves the locality opportunity, not its entire cost.
   `bench/notes/gap-register.md:28,79` labels ~5.4 ms producer/~1.5 ms checksum as
   diagnostic single runs, not verdicts. `bench/RESULTS.md:2496` marks 7.541 versus
   6.032/6.014 ms indicative; :2397-2398 says threading the orbit pass measured no
   gain. D:237-245 conflates a phase time with the difference between whole programs
   and leaves ~0.6 ms unexplained. The phase includes arithmetic/synchronization,
   not only pool reads. L3 capacity alone also cannot establish a 9 MB DRAM minimum:
   account for private caches and actual residency, and distinguish MB from MiB.
   Correction: call the cold checksum pass the leading hypothesis. Measure a
   same-team/two-pass control against producer-local folding, with phase timing and
   total timing. The quoted 5-20 us/fork is an assumption; “immaterial” is unsettled.
   A row-local epilogue may still load every pool cell, but from hot cache; distinguish
   that from a cell-store epilogue that consumes the just-computed stored value.

10. **D4: row-indexed class slots are sufficient only with a fixed within-row rule.**
    A complete row's class sums, computed from zero in a fixed cell/SIMD scheme,
    stored once into [row,class], then combined in ascending row order, eliminate
    dependence on dynamic ownership and team size within one build. Initialize the
    empty off-diagonal class in the last row. Do not use per-thread accumulators
    carried across rows. Preserve the source term on the rounded stored cell and
    the original outer seed once. Do not elide the named K pool.
    Retain OrbitFold's independent licence checks on EVERY logical fold level
    (`src/OrbitFold.fs:7-36,406`), repro veto, and addition-only multiplicity tree
    (`src/CodeGenBinding.fs:3620-3636`). A producer's map omp does not license a
    checksum. This changes licensed bits relative to today's per-thread orbit tree;
    it is not a repro transform or a cross-compiler deterministic-SIMD guarantee.
    With those qualifications the proposed rule is sufficient; B owns the change.

11. **G7: reject the join-only automatic-thread policy; use request AND permissions.**
    A single materialized builtin sum needs `IsOmpParallel` today
    (`src/CodeGenBinding.fs:3087-3090`). A deferred fold needs a fold request OR
    a producer-map request covering the outer level, AND its own reorder licence
    (:3892-3905). Builtin (+) alone grants the latter, not the former
    (`src/CodeGenExprSupport.fs:1634-1648`). Fresh dot plan confirms the distinction:
    its explicitly licensed dot is threaded, while its builtin input checksums say
    `[fold form: K-lanes(8); reduce in expression position]`, with no team.
    E:259 explicitly proposes a policy extension, so it is not a mistaken claim
    about current code; it is nevertheless asymmetric. Item C could then change
    execution policy merely by discovering a join of formerly serial folds.
    Correction: **thread when ANY leg carries an explicit applicable omp request
    and ALL fold legs independently admit reassociation, plus shared-traversal
    purity/shape checks**. A producer-map request may select the traversal only
    where it covers that same outer level, matching existing Path B; it grants
    no other leg a licence. Never OR the leg licence/repro facts together.
    `bench/kernels/onepass/onepass.blade:30-32` has no such request. The omp at :26
    is on the completed input generator and cannot transfer. Thus drop the
    “no source change” promise, or obtain a separate language-policy decision
    covering single folds and joins uniformly. The knob is not source permission.

12. **G7: seed contract is compatible with ChunkSimd, not with arbitrary FoldChunkPlan kernels.**
    `src/IRStorage.fs:90-94` describes first-contributed-value seeding for the
    general identity-free path; :109-116 explicitly distinguishes ChunkSerial
    from identity-seeded ChunkSimd. Four zero-seeded builtin + partials, followed
    by a fixed chunk-order combine starting from each original seed ONCE, match
    the existing SIMD specialization. Nonzero caller seeds must never enter each
    partial. Preserve empty-input/no-seed refusal versus explicit-seed behavior,
    empty chunks, actual team size and per-leg seed evaluation order.
    Correction: describe this as the builtin + specialization of the contract;
    do not generalize the zero initialization to custom commutative kernels.
    The existing has-value/first-contribution machinery is required for those.

13. **G7: threading must enclose the shared traversal, not recreate one emitter per leg.**
    Fresh onepass stdout:219-225 has one x load feeding four accumulators; no
    four-pass inefficiency exists to remove. `l = method_for(x)` is a loop value,
    not itself an expensive map, but losing peel dedup would still reload x.
    The more general shared deferred producer uses ShareDecl at
    `src/CodeGenBinding.fs:4694-4703`, references that local at :4721-4730, and
    substitutes it into consumers at :4734-4748; :4763 puts shareCgs before legs.
    Correction: partition once around that whole ordered list, place shared
    locals inside each worker's iteration, and privatize only the accumulators.
    Pin one load and one shared-producer evaluation per coordinate in BOTH entry
    paths (:4465-4510 and :4763-4786). Do not replace the join with four chunked
    helper calls or reassociate v*v*v*v. M effort is credible for this bounded
    design; policy expansion is a separate decision. Wave 2 is appropriate.

14. **G5-admission: current fusion rejects the proposed licence; baseline pollution is real.**
    `src/IRMono.fs:2523-2527` rejects Parallelism and IsOmpParallel in tilePlainKernel.
    `src/Optimize.fs:1826-1843` applies it to maps; :1845-1856 also rejects parallel
    callees. The successful branch creates plain IRForRange at :1928. Thus the
    licensed final-map spelling loses this slice-write fusion TODAY, rather than
    silently losing its request. Fresh recur stdout:453-480 confirms the original
    source already allocates the full trajectory, writes each fused scalar step
    directly into its row, and folds that row immediately. Adding omp alone can
    restore temporaries/copies and be slower; a timing regression is not proved
    without running the proposed source, which was not done here.
    Correction: keep candidate (a), change only this pass's admission, preserve
    the final spatial request and independent effect/dependence proof, and retain
    exact scalarization under OMP_THREADS=1. Compare OLD/original, OLD/licensed,
    NEW/licensed fused-serial, NEW/step and NEW/persistent. E's off gate restores
    the old unfused licensed path (E:230-233), so on/off ALONE cannot measure
    threading. M for admission/per-step remains reasonable; do not relax
    tilePlainKernel globally or turn the permission into a time-axis licence.

15. **G5-persistent: the licensed schedule is sound; “deterministic” must not substitute for repro.**
    `bench/kernels/recur/recur.blade:28-45` is pointwise RK4 with only the same-x
    preceding-row dependency. The expert uses contiguous x ownership with private
    time loops (`expert.f90:52-89`). Each worker can compute all times for its block
    without per-step barriers, while storing EVERY trajectory cell. Row0 must be
    ready before readers start and its checksum must be included exactly once.
    `part[row,tid]` and ascending block combines reassociate the INNER row chain.
    Here the row kernel is prodsum(row,row), whose builtin + licence permits that
    in licensed mode. The outer time (+) licence is not the justification for
    splitting that inner chain; the spatial map's omp is not either. Preserve
    each row's seed once, then the original ascending time fold and its seed.
    Under repro/where repro, block partials are forbidden even with fixed order.
    Use E's barrier, single complete ascending row fold, and completion barrier;
    or prove a different schedule preserves both complete chains. S13's explicit
    update is `F(acc,K(BUF(row)))` (`src/Optimize.fs:2072-2095`). The expert's
    thread-local cs followed by reduction is NOT Blade's repro oracle
    (`bench/RESULTS.md:2403-2414`). L effort and wave 3 after B are appropriate.

16. **G5: an IRId-keyed RecStepPlan is not self-maintaining. Name the actual rewrite seams.**
    After slice-write admission, `Optimize.optimizeModule` still runs row-fold
    fusion and invariant hoisting (`src/Optimize.fs:2231-2233`). Row-fold fusion
    freshens/inlines K/F lets and replaces the time-loop tail (:2075-2095,2168);
    invariant hoisting can clone kernels with new callable IDs and captures
    (:1374-1409). Those are immediate proof/owner audit points.
    AFTER optimizeModule, the pipeline is (`src/Lowering.fs:2870-2925`):
    - IRLift.liftInlineFormsModule: inserts/relocates lets and wraps bodies;
      loop binder IDs are retained by `src/IRLift.fs:1227-1228`.
    - forceCallableBody on functions: changes deferred/compute structure.
    - Optimize.cseModule: can remove lets and substitute references/captures
      (`src/Optimize.fs:884-923`). It currently skips callable bodies containing
      IRForRange/assign (:801-808), so do not falsely claim it renumbers this
      benchmark's recurrence today.
    - planPoolReuse and planHotRowFolds: planning side tables, not general ID
      renumbering passes; their storage/scheduling decisions still need consistency.
    - eliminateDeadPolymorphs, then qualifyModuleNames: remove unreachable
      callables / rename members; prune dead plans and preserve provenance.
    C++ assembly also disambiguates names and merges module metadata explicitly
    (`src/CodeGen.fs:3656-3672,3839-3859`); the new table needs an explicit union.
    Arity/HM/shape monomorphization occurs BEFORE Optimize
    (`src/Lowering.fs:2778,2797,2815`), not afterward. There is no evidence of a
    universal post-Optimize renumbering pass; the danger is stale proof/body/owner
    references and incomplete copying, not an assertion that every ID changes.
    Correction: preserve source permission during admission, finalize/revalidate
    dependence and row-fold plans after the last structural rewrite, key ownership
    unambiguously (enclosing callable plus loop binding/iterator), and fail closed
    on missing/mismatched bodies. Map all referenced IDs when cloning; do not just
    copy the map key. Cover two instances, multiple modules, hoisting, fusion/CSE
    off/on, and deleted/unreachable owners. E leaves this as an audit; make it a
    prerequisite to shipping, not a follow-up.

17. **Hard constraints and evidence labels: retain the checks, correct the claims.**
    No accepted schedule may delete named pools: D2/D3b still store every canonical
    cell, D4 keeps K, G5 keeps the 301*20011 trajectory. G7 keeps all scalar outputs.
    Strict chains, product order, subtraction and scale are covered above; map
    permission never licenses a reduction, and one leg never licenses another.
    D's BLADE_TILE_TUNE registration and both-lane ABI/testing plan are appropriate;
    G7's emission-only knob belongs in suiteClearedKnobs; G5's pre-split admission
    gate belongs in optimizerGates. Keep the proposed env-table rows and applied/
    declined plan records. LLVM must retain truthful serial census after G5
    inlining; interpreter and LLVM execute the sequential fallback, and G5 needs
    opt-diff in addition to interp. Raw-bit repro+contract-off checks cannot be
    replaced by integer-valued decimal EXPECT fixtures.
    D:7 says every number is a count or recorded measurement, yet :154 and :241
    supply assumed fork/barrier times; :13 labels a cache model as observed traffic.
    Correct these labels and D4's causal wording. RESULTS 14.8 is the historical
    d3d5722b tree (`bench/RESULTS.md:2369`), not a new 4136f0b5 measurement. E states
    this correctly. E's costs/waves are forecasts explicitly labeled as such.

## Disagreements I could not settle from source

1. **Thread-request policy:** should builtin + automatically open teams for BOTH
   single folds and joins, or should existing explicit-request policy remain?
   Source settles current behavior, not the intended future policy. My recommendation
   is request-on-any/applicable-leg plus independent licences on all legs. A third
   reviewer must resolve this before accepting E's unchanged-source onepass promise.
2. **D2 performance:** actual L2/L3 misses, shared-L3 contention, generated spills,
   and anti_g x anti_kc interaction need the runner's interleaved OLD/NEW and G sweep.
   Source and my counts establish legal reuse, not a default or a speedup.
3. **D4 attribution:** the cold-pass share, team-entry share and unexplained residual
   need same-session phase and whole-program controls. The diagnostic split does not
   settle whether fusion recovers 1.51 ms. Third review should keep this open.
4. **D1/D3b/G5 timing:** source-level barriers are not runtime barrier timings;
   fewer edge updates need not beat full-width ILP; persistent repro synchronization
   may lose to fused serial execution. The proposed runner experiments decide these.
   The D3b left-only bug and current G5 admission refusal are settled from source,
   not questions to defer.
