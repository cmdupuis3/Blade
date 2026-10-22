# Round-3 wave-1 Codex diff reviews (condensed)

The independent Codex diff review of each wave-1 item (read-only; verdict, findings, hook table) and the merge-level review of the wave. The research attack reviews stay in `codex-review-A.md` .. `codex-review-D.md`.

Sections (each is one former file, verbatim): [`wave1-codex-reviews.md#codex-diff-w1-5`](#codex-diff-w1-5), [`wave1-codex-reviews.md#codex-diff-w1-3`](#codex-diff-w1-3), [`wave1-codex-reviews.md#codex-diff-w1-a2`](#codex-diff-w1-a2), [`wave1-codex-reviews.md#codex-diff-w1-t4`](#codex-diff-w1-t4), [`wave1-codex-reviews.md#codex-diff-w1-t4-2`](#codex-diff-w1-t4-2), [`wave1-codex-reviews.md#codex-wave1-merged`](#codex-wave1-merged)

---

## codex-diff-W1-5

_formerly `bench/notes/round3/reviews/codex-diff-W1-5.md`_

﻿VERDICT: MERGE AFTER FIXES

Reviewed `66e04875..9a017e12`, the required guidance, research N07/N09 and review C. Paths below are relative to commit `9a017e12`; `w15/` denotes the supplied reproducer directory. No builds, executions of benchmark programs, harness runs or compiler edits. All six added `.blade` files pass the permitted `blade check`; `git diff --check` passes. Oracle execution with `python -I -B` failed because NumPy is unavailable in isolated mode; pin values were inspected, not independently recomputed.

1. **HIGH — twofir's Fortran inputonly twins still allocate the outputs.** `bench/kernels/twofir/natural.f90:30,91` and `expert.f90:40,115` allocate/deallocate x, y1 and y2 unconditionally. `BENCH_INPUTONLY` removes the calculations but retains two output allocations inside the timer, unlike `inputonly.blade`. This contradicts §3.3's incremental-estimate definition (output allocation belongs in total-minus-twin) and `spec.md`'s x-only twin accounting. This establishes unwanted allocation overhead, not 32 MB of resident output pages: the outputs are untouched. **Fix:** allocate x unconditionally; guard y1/y2 allocation and deallocation with `#ifndef BENCH_INPUTONLY`, as tensorop already does. Keep the input fill and input sum identical between total and twin.

2. **HIGH — tensorop omits the required mt8 factor-checksum teams.** `bench/kernels/tensorop/expert.f90:181–201` gives sa/sb/sc only conditional SIMD directives; `natural.f90:134` uses serial `sum(af/bf/cf)`. Blade licenses all four input folds (`tensorop.blade:75–78`). Kernel-authoring §1/D44 requires every arm to thread preparation and checksum where supported; small factors are not an exception. This affects total and inputonly arms. **Fix:** use a threaded outer row fold for each factor, retaining the mathematical row grouping and sx+sa+sb+sc combination; expert may SIMD the licensed inner folds, natural keeps its ordinary sums. Expert's flat sx and factor sums also differ from the README §3 matched-loop grouping: restore row/plane grouping, including mathematical C rows through cct's transposed accessor. Inputs are exact dyadic sums here, so this is a protocol/work distinction, not evidence of wrong pins.

3. **MEDIUM — tensorop's baseline-selection justification misstates the contract.** `spec.md:83–86` says the brief chose textbook nests; N09 actually chooses **matmul per slice**, and kernel-authoring §1 says natural retains intrinsics. “Runtime calls and temporaries hide factor order” is not a justification: three explicit calls can preserve I→J→K. `spec.md:160` also incorrectly says the brief fixes the library spelling; review C requested existing library baselines, not exclusion of the stronger map spelling. The committed nests do match mode_product's arithmetic and are a credible **ordered textbook control**; this review does not establish that they are deliberately slowed. **Fix:** correct both attributions, explicitly record the deviation from intrinsic-natural, and require the Fortran tier-selection review to assess the slice-matmul candidate against the ascending-chain requirement before publication. Label mode_product as the library census baseline, not the best-performing existing Blade spelling. Retain the licensed outer-map/inner-fold spelling as the later recognizer comparison, with its different source permissions disclosed.

4. **MEDIUM — tensorop's metadata overstates repro parallelism.** `kernel.json:41` says “parallel preparation and checksum, serial kernel” for blade-cpp. `bench/harness/run.py:1843–1844` copies this into every mt8 regime, including repro, whereas `spec.md:140–142` says those checksum folds are serial. **Fix:** make the note explicit: “serial mode_product kernel; threaded preparation; checksum threaded in licensed/fastmath, serial in repro.”

Other attack results:

- **Arithmetic/twins:** twofir's expert covers y1 indices 1..5, 6..n−11, n−10..n−6 and all y2 cells. Serial repro preserves separate +0 s1/s2 chains, then s1+s2; licensed SIMD reductions match the source licences. Two separate FIR loops are a credible natural baseline. Tensorop's full and edge tiles initialize accumulators to +0, visit t=1..kd once per cell, and store after that chain. MC=240 blocks **rows**, never t; outer row/column partitions are disjoint. Factor order remains I→J→K. `MathDecls.fs:649–672` confirms the natural nests' arithmetic. No contraction reduction directive grants extra licensed reassociation.
- **Stores:** every required input/output is materialized in source and every cell contributes to its checksum. twofir expert consumes each just-produced scalar while storing it, which §1.5 permits; a separate reload is not required. T1/T2 are not mandatory outputs. Physical stores in every optimized timed arm remain **unverified**: dump builds cannot prove them. Retain the full-size store audit as the publication gate, particularly for twofir expert; no missing store is established by this review.
- **Style/sizes/storage:** no imperative Blade loops, int/float mixes or illegal omp placements found. Halo and annotated mode_product are valid idioms. Logical extents are non-power-of-two: twofir n/n−6/n−16 at both sizes; tensorop 11/13/17 and 89/97/101. There is no logical extent 16; halo span subtraction and register-tile dimensions are implementation constants. Payload estimates are approximately 48 MB twofir, tensorop 34.9 MB Blade versus 27.9 MB Fortran **plus factors/metadata**, not measured working sets. Both kernels provision twins; tensorop's later >5% pilot decision is correctly conditional. Regime lists match N07/N09.
- **Oracles/pins:** ref_twofir uses the exact taps and source expression order. ref_tensorop uses zero-filled accumulators, ascending Python t loops and separate NumPy multiplication/addition; moveaxis only rearranges views. No einsum/BLAS reassociation. `math.fsum` checksum pins are intentional tolerance-based reference sums, not ordered-fold bit pins. Oracle-only full pins are exactly the procedure in `make_pins_B.py` and §3.4; `validate_B.py` checks smoke cells. A full Blade run is not a prerequisite to generating pins. Published full results still require validated runs and store/working-set evidence.

Reported defects (pre-existing; do not fold compiler fixes into this kernel commit):

- **Capture leak — REAL; needs a compiler gap row.** `CodeGenLoopNest.fs:4389–4423` seeds captures from all body lets, even nonescaping scalar-fold lambdas. `w15/leak_repro.cpp:206,249–250` allocates y (`__v13`) but frees only t (`__v9`) before returning. Fix escape analysis to distinguish escaping closures from synchronous local consumers; retain lifetime protection until the fold/team completes. Record tensorop's omitted Y free as a timing/lifetime asymmetry too.
- **Own-let reduce declined — REAL; extend G24/G14 with this exact shape.** `CodeGen.fs:523–543` routes only array-valued IRReduce lets through statement emission; scalar lets reach `CodeGenExpr.fs:1534–1536`'s expression-only decline. `axes = 1` takes the other lowering route. Fix scalar function-body let dispatch to use the statement fold path and pin its team/census behavior; the suggestion to bind an already-bound fold is wrong.
- **smoke_B.sh digit replacement — REAL; tooling defect, no compiler gap row.** `bench/validation/tools/smoke_B.sh:40` replaces the first digit sequence anywhere on the SIZE line: n1 becomes n11 for smoke 11 while 89 remains. Current hard-coded inventory does not yet run tensorop, so this is latent there, not proof the reported run.py gates used wrong sizes. Fix by reusing the harness SIZE parser and kernel.json inventory instead of the sed substitution.
- **Halo cell epilogue — REAL limitation; needs a performance-gap entry.** `CodeGenLoopNest.fs:2473–2489` excludes halo tags from the flat path; `CodeGenCuda.fs:3791–3808` only attaches B1 through that path and records the decline. The saved twofir plan confirms two passes. Extend epilogue admission/emission to halo nests while preserving stores, failure order and repro chains; this is a safe missed optimization, not a value miscompile.

---

## codex-diff-W1-3

_formerly `bench/notes/round3/reviews/codex-diff-W1-3.md`_

VERDICT: MERGE AFTER FIXES

Read-only review of 66e04875..142ee9d7. Read bench-fortran/CLAUDE.md first. No builds, emission, tests, program execution, or compiler invocation. Counterexamples below are source traces, not executed reproductions. Existing w13 artifacts/logs were inspected.

1. BLOCKER — Tuple-wide positional forcing precedes the new member-order machinery.

   src/CodeGenBinding.fs:580 runs forceDeferredPositionalReads on the ENTIRE tuple before :601 invokes forceTupleArrayMembers. CodeGenFusion.fs:1637,1664 collects a later member's indexed producer; an earlier IRCompute(IRVar) does not get collected (:1678). Thus the newly supported tuple still reverses its first failure:

   ```blade
   type Row = Idx<1>
   let a: Array<Int64 like Row> = [1]
   let z: Array<Int64 like Row> = [0]
   let first = method_for(z) <@> lambda(v) -> 1 / v
   let later = method_for(a) <@> lambda(v) -> a((v : Row))
   let t = (first |> compute, later(0))
   ```

   Interpreter: Core.fs:642 evaluates member 1, Loops.fs:2348 forces it: BL8013. C++: the pre-pass materializes later first: BL8006. The base could not declare first for the tuple; this patch makes that shape executable with the wrong first failure. tuples/037 misses this because its scalar member reads only stored arrays.

   Fix: dispatch tuple evaluation before the whole-expression pre-pass; force dependencies and bind evaluated leaves at their own left-to-right positions. Cover the enclosing genLetChainBinding pre-pass (:6517) too. Do not stop sequencing at lastForced (:718): two evaluating scalar leaves AFTER the last forced member still reach std::make_tuple with C++17's unspecified argument order. Pin the probe above (BL8013), its reversed-member control (BL8006), and a safe computed first member followed by a failing cast and a failing integer division (cast first).

2. BLOCKER — A bare deferred tuple member is silently turned into compute; this changes first failure, not just printing.

   src/CodeGenBinding.fs:640-644 accepts IRVar without requiring IRCompute; :714 inserts IRCompute. The interpreter's IRTuple arm retains a bare VDeferred member (Core.fs:642-643); force on the enclosing VTuple does not recursively force its members (Loops.fs:274-299).

   ```blade
   type Row = Idx<1>
   let a: Array<Int64 like Row> = [1]
   let z: Array<Int64 like Row> = [0]
   let g = method_for(z) <@> lambda(v) -> 1 / v
   let held = (g, 1.0)
   let bad = a((a(0) : Row))
   ```

   Interpreter: held stores the deferred member; bad fails BL8006 before printing. Patched C++: held computes g and fails BL8013. No writing map, printing limitation, or ambiguous C++ argument order is needed. This C++ acceptance/divergence is INTRODUCED, despite the report grouping it with pre-existing defects. LLVM already doing the eager thing does not establish interpreter parity.

   Fix: restrict this correction to explicit compute members, retaining a clear unsupported path for bare deferred members until their semantics are implemented consistently; alternatively fix the representation/evaluation contract in all lanes. Add the probe with the interpreter's BL8006 and an explicit-compute control with BL8013. Qualify RESULTS.md:2686's bare "(g, 1.0)" closure claim.

3. HIGH — The new Sparse branch has no corpus pin.

   src/CodeGenExpr.fs:2277 deliberately admits Sparse as well as Compound. Sparse DOES have data (nested_array_types.hpp:178-181). Its pool is in supplied-key order, not hash iteration or sorted-key order (index_types.h:494-504); sparseToDense preserves that order (Interp/ArrayOps.fs:1869). The access is justified, but a scratch probe does not pin this newly claimed behavior.

   Fix: promote w13/z5_sparse_setops.blade to corpus and add union. With static keys [3,7,1,9] and values [5,2,5,2], pin unique(F)=[5,2], intersect(F,[2,8])=[2], union(F,[2,8])=[5,2,8], plus intersect([2,5],F)=[2,5]. Include an empty-input control. Check C++/interpreter parity; LLVM currently refuses these set-op nodes.

4. MEDIUM — The committed lane account conflates CLI fallback with differential skips.

   bench/notes/gap-register.md:171-173 says 014/015/009/012 "fall back to C++"; it also omits dense control 010. That describes CLI behavior (CliCommands.fs:405-414), NOT test llvm. LlvmTests.fs:2016-2017 returns SkippedCase on refusal; :2058-2060 writes/compiles the accepted LLVM text directly.

   Fix the note: sql-masks/014 and /015, sql-unique-contains/009 and /010, sql-set-ops/012 are LLVM SKIPS. sql-masks/016 and tuples/034-037 actually PASS LLVM in the saved gates-final logs. NONE of those passes is C++ fallback. No extra corpus lane pin is needed to prevent fallback in this harness; require explicit LLVM emission success if claiming enduring LLVM coverage rather than merely "0 failed". w13/lanes.ps1 uses BLADE_LLVM=1 blade run and CAN fall back: its results need backend verification before being called LLVM evidence.

Evaluation count / storage / compound order:

- No changed writing-producer count established for the three requested sharing patterns. Lowering.fs:2726-2785 forces recognized named writing maps and array choice/fallback values at their definitions (join-only readers are separately refused). Two tuple readers, a member's captured producer also appearing as a member, and compound data/mask sharing that writer therefore consume stored cells. forceDeferredArrayInput removes an actually forced id (:2535); the new compound helper retains both contexts (:2584-2586); tuple members are rechecked against the threaded context (:713).
- tuples/036's gw counter=6 is precisely G45: six canonical stored cells of the compact 3x3 symmetric array, evaluated once at gw's definition. It does NOT independently exercise G50 choice/fallback. Its tw tuple has an already stored gw, so it does not test the new unforced-member branch. Extend it with (gw |> compute, gw |> compute), and with a pure computed member capturing gw alongside gw; seen must remain 6.
- An explicit (g |> compute, ...) member gets a compiler temporary; pure g can remain deferred, matching forceExpr's non-memoizing explicit-compute path. A capture-forced g is stored under its own name and retained for later reads/printing. No named output is deliberately removed. Bare (g, ...) is the exception in finding 2: it adds a store/evaluation the interpreter does not perform there.
- Compound emits densePre THEN maskPre (CodeGenBinding.fs:2341-2342), matching materializeCompoundBinding (Interp/Loops.fs:1780-1781). For the requested competing-failure probe, use Row=Idx<1>, a=[1], and an empty Int64 array: data operand "method_for(a) <@> lambda(v) -> a((v : Row))"; mask operand "method_for(a) <@> lambda(v) -> reduce(empty, (+)) > v". Both source paths select data's BL8006 before mask's BL8003. No contrary compound-order defect found. Add this abort pin and a valid-data control selecting BL8003.
- Compound set-op order is compact buffer order: compoundToDense (:1713) copies the same cells. IndexTypes.Length=1 excludes trailing axes, so trailing_stride=1 and cardinality is the correct count. Rank-1 kept-position construction and fused fill preserve ascending kept order; the higher-rank compound index uses lexicographic order. The set-op walk never reads positions.
- T4b dependency: preserve idx->cardinality and the stable compact data[0..cardinality) sequence when omitting positions; never substitute dense extent or a zero position-table length for cardinality. At THIS commit normal rank-1 constructors build positions; the adopting constructor can structurally accept null, but that is not an implemented demand-certified no-position path. T4b must prohibit keyed/unhash/range consumers from reaching an omitted table and pin set-ops on an actually elided representation. This helper itself needs no positions.

Five hook checks (all repository lines at 142ee9d7):

| Claim | Checked hook | Result |
|---|---|---|
| Computed compound operands defer, then force with retained context | CodeGenBinding.fs:2570,2584-2586; forceDeferredArrayInput:2527-2542 | Confirmed; dense-before-mask emission at :2341-2342. |
| Both set-op passes use compact count/cells | CodeGenExpr.fs:2274-2279,2297-2315,2353-2380; Interp/ArrayOps.fs:1713,1869 | Confirmed; Sparse uses key order. No position-table dependency. |
| Fixture 02 and the residual explicit-compute tuple share the undeclared-name site | w13/base_034_tuple_of_computes_from_block.cpp:342; base_036_tuple_holding_unforced_map_over_compact_pool.cpp:297; CodeGenBinding.fs:594-603 | Both are make_tuple of undeclared producers. This proves the shared explicit-compute site, not bare-member semantic equivalence. |
| Tuple forcing preserves first-failure order | CodeGenBinding.fs:580,713-719 versus Interp/Core.fs:642-643 | Incomplete: finding 1 bypasses the helper; finding 2 inserts a new force. |
| Named writer is stored once; captured producer is forced under its name | Lowering.fs:2783-2784; CodeGenBinding.fs:1172-1174,2527-2535; tuples/036:47-58 | G45 control matches the contract. 04b00fe8 also contains loops/524's separate nested-capture correction. |

Reported defects — gap disposition (numbers left for merge):

- Module-level call argument: PARTLY REAL. f(m1 |> compute, 2.0) bypasses CodeGenFusion.note's direct-IRVar test and can name undeclared m1. Bare f(m1) is explicitly covered by :1659 and CodeGenBinding.fs:580; w13/z4_call_arg.blade's earlier failing wrapped call does not establish the later bare call fails. Gap row for wrapped arguments; isolate the bare spelling before claiming it.
- Deferred tuple member (p, 1.0), p=(m1,m2): REAL, pre-existing. p is deferred; unforcedTupleArrayMember only accepts array-typed members. Gap row.
- Tuple of computes inside if: REAL, pre-existing. The new branch requires top-level IRTuple; IRIf still goes through expression rendering. Gap row; LLVM refusal is not coverage.
- Bare deferred array member (g, 1.0): REAL disagreement, but newly accepted with wrong first-failure timing in C++; finding 2 is a merge fix, not merely a pre-existing gap row. Track shared bare-tuple semantics if left unsupported.
- Deferred alias let g2=g printing g only in C++: REAL, pre-existing for assignable array lets. genVarAliasBinding:5728-5731 forces the source before copying; Core.evalBinding:1385-1386 returns a deferred alias before its copy branch. Gap row; distinguish static/immutable aliases, which propagate deferral in C++ too.

Artifacts inspected: w13 under C:/Users/cdupu/AppData/Local/Temp/claude/C--Users-cdupu-Documents-GitHub-Blade/dbd3f5e5-01ed-41dd-bc92-4bc0ec85152b/scratchpad/. Existing gate totals are builder evidence only; this review reran none.

---

## codex-diff-W1-A2

_formerly `bench/notes/round3/reviews/codex-diff-W1-A2.md`_

﻿VERDICT: MERGE AFTER FIXES

Read-only review of 66e04875..cc5a646e (C++ lane). Paths/lines below refer to cc5a646e in agent-ac4cf3ba57f851a5c. Read bench-fortran/CLAUDE.md, wave1-builder-briefs.md#w1-a2-rowview, wave1-builder-briefs.md#builder-preamble and review-A findings 4-5. No builds, tests, compiler invocations or timings run. Reported footers are not independently verified. git diff --check is clean.

1. HIGH — The licensed arm does not establish its advertised outer-kernel totality.
   src/CodeGenBinding.fs:4751-4781 audits cg.KernelExpr (the row) only. licensedFold/chunkable (:4569-4578) establishes the outer kernel's licence and absence of writes, NOT totality; the new arm (:4869-4874) accepts a general comm-declared outer kernel. Yet rowViewFoldMatrix (:95-101, emitted at :4928) says "outer kernel = total".
   Probe: four singleton rows [1e19, -1e19, 1e19, -1e19], inner (+), outer lambda(p,q) where comm(p,q), omp -> Float64(Int64(floor(p + q))), outer seed 0.0, actual T=2. A serial chain can panic BL8014 on its first application; two head-seeded chunks can cancel before either cast. This is a source-derived probe, not executed. The general Path B weakness already exists at the parent; this commit nevertheless admits it into a NEW arm despite the brief's required outer totality proof. Do not present its matrix as closing that proof.
   Fix: apply the same parameter-bound IRTotality audit to callable.Body used by rowSlotAdmission:176-181 before selecting the helper arm; add a refusal needle for a panicking comm-declared outer kernel. A fallback into the already-unsafe general Path B route is not a first-failure fix: record that inherited issue separately or keep this case serial. Also correct the matrix's "its own explicit omp request" to "fold or outer map request"; both placements enter at :4572. The outer seed is write-audited, not totality-audited: it remains before the team, so that is safe, but gap-register.md:101 must not claim both seeds passed totality.

2. HIGH — Corpus 533's signed-zero pins deliberately hide a sign regression.
   tests/corpus/loops/533_row_view_fold_signed_zeros.blade:11-15,26-28 acknowledges that EXPECT neg = -0.0 and EXPECT neg_peeled = -0.0 also accept +0.0. tests/Expect.fs:645-653,671-674 confirms it. Thus "loops 297/0" is not evidence of these signs.
   Fix: give the corpus an explicitly strict sign-observing case, e.g. a where-repro outer fold and a pinned boolean 1.0 / result < 0.0, and keep mode-specific exact-text assertions for the threaded licensed/repro spellings in omp-reduce. Test the exact 533 source under both modes if retaining these numeric-only pins. Existing OmpTests.fs:3620-3629 and :3695 onward provide independent sign-sensitive coverage for analogous cases; this is a pin defect, not evidence that the implementation currently loses repro -0.

3. MEDIUM — Requested thread counts are not observed teams in the identity battery.
   OmpTests.fs:3600-3604 DOES assert the route (11 slot stores, none in the serial reference); it is not a route-less test. :3606 requests T=1/4/7/8, but runProgram:2439-2445 sets only OMP_NUM_THREADS, inherits OMP_DYNAMIC/OMP_THREAD_LIMIT, and reads no team observation. Separate coverage at :2056,2099-2131 observes a simpler quarter-integer program at T=4/7, demands only >1 workers and an upper bound, and has no T=8 run. It does not establish actual T=4/7/8 for the low-bit/sign/NaN battery.
   Fix: run that battery with dynamic teams disabled and the thread limit cleared, instrument its actual teams, and assert the intended sizes (or label/report reduced teams explicitly). Add T=8 to coverage. Printed-text equality plus residuals is stronger than tolerance, but is not an exhaustive raw-bit comparison of all finite results.

4. MEDIUM — The difficult scope/admission edges are reasoned about, not pinned.
   The new identity source (OmpTests.fs:3563-3584) uses bare row folds and fold-kernel requests. It has no leading-let row, no fold-valued outer seed, no runtime-extent refusal, and no bit-sensitive map-request twin. The 24 emission cases cover map placement, but not these combined scope/evaluation cases; the hand-built IR battery (:1761 onward) omits runtime extents.
   Fix/probes: add a row body "{ let s = 7.0 + Float64(i); reduce(u(i), (+), s) }"; an outer seed that is a nonzero pure fold; a map-request identity twin; and hand-built IR with an unevaluable column extent. Assert slot/decline needles plus exact output, and helper refusal for integer elements. A direct lambda(a,b) where omp -> a+b is NOT a negative builtin test: foldKernelBuiltinOp recognizes the body itself (CodeGenExprSupport.fs:1600-1616). Use a genuinely non-builtin comm-declared callable to test that refusal.

Disposition of the semantic attacks:
- REPRO: no new bit/order/evaluation-count defect found. The unchanged row IIFE evaluates an explicit seed once in its row scope, then applies the inner kernel ascending; unseeded rows peel cell 0 and start applications at 1. Leading IRLets are walked, then retained in the rendered row expression. One row evaluation fills each slot; the outer accumulator is initialized once before the team and combines slots ascending. These preserve -0 under REPRO + CONTRACT=off.
- Empty/runtime rows: requiring statically positive extent for unseeded admission is sufficient and conservative. Static empty unseeded source is BL3007 before this path; hand-built empty IR is refused for BL8003. Runtime column extent is refused ("no static extent") for seeded AND unseeded rows, retaining the ordinary path/guard.
- Five parties: the base's initializer is not moved. A previously stored rank-2 view or alias is not categorically excluded, nor must it be: only its reads move. The full row spine and inner/outer callees are write-audited; no separate top-level intervening statement crosses this fold. Both request placements share the gate. Codegen uses the shared Optimize write analyzer with program lets, alias/copy provenance, unknown-as-write and callbackTable, rather than literally calling scheduleMayWriteIn (whose callback policy is more conservative). No missing writer party demonstrated.
- LICENSED: seed + sum(row) retains the seed exactly once; unseeded head + sum(tail), singleton head alone, and the final outer-seeded wrapper combine are explicit. The per-thread row chain followed by ascending job partials is a licensed association, conditional on the outer legality issue in finding 1. Integer elements and where-repro inner kernels decline; a pure fold-valued OUTER seed is not categorically refused and stays before the team. Leading-let rows decline this helper.
- Signed zero: dropping the repair is allowed by G40's licensed contract; matching LLVM +0 is a compatibility choice, not a universal semantic requirement. FOLD_LANES=0 can preserve -0 where this helper yields +0. Exact text, raw bits, or a reciprocal-sign boolean catches it; numeric EXPECT does not. Singleton unseeded rows remain their original cell, so blanket "every all-minus-zero row becomes +0" wording needs that exception.

Five hook checks:

| Claim | Hook at cc5a646e | Result |
|---|---|---|
| Exact row-view proof, static extent, unseeded nonempty | src/IRTotality.fs:240-277 | Confirmed; runtime extent declined |
| Original inner chain/seed and ascending outer chain | src/CodeGenExpr.fs:1583-1600; src/CodeGenLoopNest.fs:2283-2284,2377-2380; src/CodeGenBinding.fs:5283 | Confirmed by source; unchanged renderer |
| Shared transitive audit including callbacks | src/CodeGenExprSupport.fs:1741-1768; src/CodeGenBinding.fs:178-199,4569-4573 | Confirmed; licensed outer totality is missing |
| Licensed helper, seed once, real-team partition/final combine | src/CodeGenBinding.fs:4751-4781,4896-4907,4939-4955 | Confirmed; own inner licence, float gate, actual __rnt partition |
| Route-sensitive identity plus team coverage | tests/OmpTests.fs:3600-3629,2056,2099-2131 | Route confirmed; observed teams are a separate T=4/7 probe |

Reported defects:
1. NOT a C++ REPRO/interpreter violation: the deferred spelling receives an explicit +0 in TypeCheckInfer.fs:2841-2867; Lowering.fs:980-982 and Interp/Loops.fs:2367-2369 preserve it. The identity-seeded deferred-fold rule is documented in docs/plans/structural/03-streaming-reductions.md:117-118. Real cross-spelling difference, no new W1-A2 correctness gap row; any unification of materialized/deferred seed semantics is separate design work.
2. REAL licensed signed-zero variation, already G40 (gap-register.md:90); not a new repro defect. Extend G40's examples with the FOLD_LANES on/off row-view probe, do not add a duplicate row.
3. MIXED: TEST-line removal is documented harness coordinates (Corpus.fs:47; Expect.fs:532), not a new defect. Short "inf" is not normalized by Expect.fs:32-35 (the stronger claim that no infinity pin is representable is unwarranted); zero-sign blindness is real (finding 2). Harness follow-up, no G51 row.
4. REAL stale comment: bench/kernels/stencil/stencil.blade:14 still says repro cannot recognize row-view folds. This change makes it stale; update the comment, no new gap row.

---

## codex-diff-W1-T4

_formerly `bench/notes/round3/reviews/codex-diff-W1-T4.md`_

﻿VERDICT: DO NOT MERGE

Reviewed 66e04875..9012128c. Read-only source/git review; no builds, emission, tests, executables, or timings run. Reproducers below are source-derived probes, not executed results. File:line references are in the builder worktree at 9012128c.

1. **BLOCKER — an imported lookup hidden in RandomInits gets a positions-free compound.** `src/Lowering.fs:3086-3094` collects foreign references from binding values, function bodies, CompoundInits and ProviderWrites only. RandomInits and SparseInits are absent. A random binding's executable key is moved into RandomInits and its binding value becomes IRUnit (`:2455-2474`). Importing a variable creates an environment mapping, not an alias binding (`:2267-2271`). The exporting module's census therefore sees no reference; its local RandomInits scan (`Optimize.fs:3195-3202`) cannot see the importing module.

   Minimal two-file probe:
   
   ```blade
   // Data.blade
   module Data
   let x = [12345]
   let keep = mask(x, lambda(v) -> v > 0)
   let y = compound(x, keep)
   ```

   ```blade
   // Main.blade
   module Main
   import Data
   import rand as r
   let u = r.uniform(Data.y(0), 1)
   ```

   B4 builds positions and supplies key 12345. V2 elides them; `CodeGenBinding.fs:2048` renders the key expression at the fill, and the keyed Compound read reaches `index_types.h:483-484` → `:472` → BL9002. This is a valid-program runtime ICE, not an unreachable assertion. This particular probe is C++: LLVM explicitly refuses RandomInits (`EmitLlvm.fs:7471`), so requesting LLVM can fall back to the same defective C++.

   **Fix/probe:** compute foreign demand from ALL executable side-table expressions and all relevant type/index expressions, using one complete module traversal shared with the local census. Include random key, parameters, weights, address, FillModulus, SparseInits and provider dependencies. Add this multifile regression and variants with a derived map exported as the key source. Pin b4/default output identity and a REQUIRED-positions decision; repeat with `sel_cap=0` to isolate T4b. Multifile/037 exercises an ordinary binding reference and misses this case.

2. **BLOCKER — the “between” party audits placeholders, allowing T4a across a real mutation.** `Optimize.fs:3442` calls W/F only on `b.Value`; the inherited B4 check at `:3265-3267` does the same. RandomInits and CompoundInits execute hidden operands at that binding's site, while their values are IRUnit (`Lowering.fs:2468,2488`). W is genuinely called, but on the wrong expression. `scheduleMayWriteIn`'s initializer map also contains only those placeholders (`Optimize.fs:1455-1456`).

   Probe:
   
   ```blade
   import rand as r
   type Ix = Idx<2>
   function bump(a: mut Array<Float64 like Ix>) -> Int64 = {
       a(0) = 9.0
       7
   }
   let mut x: Array<Float64 like Ix> = [1.0, 2.0]
   let keep = mask(x, lambda(v) -> v > 0.0)
   let noise = r.uniform(bump(x), 1)
   let y = compound(x, keep)
   let s = reduce(y, (+))
   ```

   Source-derived outcomes: off/B4 gather after `bump`, giving `y = [9.0, 2.0]`, `s = 11.0`; v2 gathers at keep, giving `y = [1.0, 2.0]`, `s = 3.0`. The mask remains all true, so this isolates the newly moved gather rather than a pre-existing stale-count problem. The named y store contains the wrong snapshot. The source initializer is a literal, the predicate is total, no fold is fed, and the intervening noise placeholder passes all recorded checks.

   **Fix/probe:** audit a binding's actual execution payload, including side tables, with W/F and mask-reference collection. Feed that payload to the shared initializer resolver too. For this case decline T4a to B4; do not disable otherwise legal B4. Add a second probe with `compound(bump_returning_array(x), other_keep)` between keep and y, using a DISTINCT mask so shared-mask refusal cannot hide the bug; this also targets the compound side table and supported LLVM paths. Add a provider-write/read barrier probe and an imported mutating helper variant.

3. **MEDIUM — the traffic claim conflates executed traffic with a cache model.** `CodeGenExpr.fs:2278-2287` emits two x reads per source cell: one in the predicate loop and another in the gather loop. It also rereads keep and performs a y store for EVERY source cell, including rejects. LLVM does the same (`EmitLlvm.fs:4394-4410`). “m+1 touched cells” does not mean m stores.

   For Float64, n source cells and m kept cells, counting one load per self-square operand value, B4's licensed loops execute approximately `34n + 8m` bytes (x twice, keep write/read, n y stores, n position stores, fold read); positions-elided v2 executes `26n + 8m`. At m≈n/2 these are about 38→30 MB, not 30→17 MB. The smaller published numbers model retained cache lines / traffic beyond L1 and omit repeated writes and cached reads. Repro's chain reads are likewise real operations even when cache-resident.

   **Fix/probe:** publish executed scalar-cell loads/stores separately from touched capacity, modeled cache/memory traffic and measured peak working set. Label 30→17 / 26→13 as cache-model estimates with assumptions; do not call them logical bytes counted from emitted loops.

**Demand audit (a).** “Required” below describes this census, not whether the primitive inherently needs positions.

| Consumer | Census / runtime consequence |
|---|---|
| Direct keyed read; keyed read through a tracked elementwise map | Required (`Optimize.fs:3136`); maps share the input index. |
| Alias, let-mut copy, assignment, tuple then projection, returned value, ordinary callable argument | Required at the alias/assignment/tuple/call/uncertified-variable occurrence (`:3137-3148,3179`). No escape analysis needs to follow the subsequent lookup. |
| Capture into a kernel, including one performing keyed reads | Required via capture lists or body references (`:3182-3189`), including captures that only query extents. |
| `<|:>` / present, a prefix/index consumer explicitly naming y | Required by the generic operand rule. Rank-1 compound partial/prefix indexing is not a supported surface read; the C++ prefix helper is nevertheless guarded. |
| Coordinate enumeration / unhash / halo over `CompoundIdx<keep>` | A range constructs its OWN index from keep (`CodeGenCuda.fs:3034-3069`, `CodeGenLoopNest.fs:259-290`); it does not borrow y's elided table. An explicit y operand is uncertified and requires positions. |
| Printing y or a tracked map | Correctly elided: `CodeGen.fs:3133-3136` prints `.size()` cells of `.data`; it does not unhash. `Compound::size()` uses cardinality × trailing_stride. |
| `extents(y)`; ordinary scalar folds | Correctly elided: rank-1 extent is m, not dense extent n; folds walk stored values. Both n and m remain in the C++ index. |
| unique / intersect / union, sort, group_by | Required conservatively: their y operand reaches the generic rule. Positions-free set walks added by W1-3 do not themselves authorize this census to elide; this commit is not that sibling implementation. |
| Provider output; local CompoundInits / SparseInits / RandomInits operands | Required when they mention a tracked value (`:3190-3204`). |
| Imported reference | Required for ordinary bodies/CompoundInits/provider writes; **wrongly elided for the hidden foreign random key in finding 1**. |
| Type extents / unknown uses | Binding and function-signature extent expressions are scanned. The blanket “all types/all side tables” claim is too broad: foreign types are absent from moduleRefs, and generic childrenOf deliberately excludes embedded index types (`IR.fs:2200-2202`). Add type-only and local-expression-type probes. |

Additional narrow census probe: `derivedMap` (`Optimize.fs:3118-3122`) checks output rank zero but never checks scalar INPUT ranks or that the output preserves the compound axis. Exercise `object_for(at0) <@> y |> compute` with a whole-array typed kernel performing `c(0)`. Require positions for any nonzero/unknown kernel input rank. I am not claiming a runnable B4→v2 regression for that probe: zero-iteration kernel emission itself needs checking; findings 1–2 do not depend on it.

**Guard and legality checks (b–f).**

- BL9002 is the diagnostic registry's “internal codegen invariant violated” (`Diagnostics.fs:536,597`), so it is a defensible defensive ICE code even though g++ rejection is its usual documented use. It is not an acceptable semantic fallback. `blade_runtime.hpp:397-403` preserves that code at runtime. Finding 1 disproves unreachability. Fix demand; retain the guards.
- In admitted predicates, a mid-block Blade panic is excluded by `totalScalar` (`Optimize.fs:3021-3038`): integer division/modulo/power, checked casts, calls and captures are rejected. Predicate-panic probes should assert DECLINED, including a failure after cell 256. Pin 528 alone does not establish that refusal.
- In the absence of finding 2, predicate and keep writes occur once in ascending cell order; branchless y writes occur n times, including slack overwrites, exactly as B4. The interleaving changes intentionally. Both strict-chain helpers use the same 256 SOURCE-cell blocks, first-kept-square peel, ascending completed-cell feed and product-rounding barrier; no chain reassociation was found. BL8003 remains at the fold.
- The runtime cap decision precedes the first block/allocation; fallback never resumes a partially fed chain. It uses untouched initialized accumulators and fresh m+1 buffers. Byte admission uses division; static admission is n < capCells, dynamic admission tests the same relation.
- Explicit seeds are excluded at `Optimize.fs:3330`; “seeds: none” is accurate for planned folds. Ordinary source aliases/views are covered by the shared transitive W analysis, but hidden initializer payloads are not (finding 2). A source-language read of y before its declaration is unavailable. The pass adds no parallel callback body; unresolved callback effects are conservative.
- Opt-diff's mirror really compares default vs default-with-b4 under REPRO=1 and CONTRACT=off (`tests/OptDiff.fs:142,180-188`); compilation remains inside that environment. Other optimizer gates have the same unset defaults. `suiteClearedKnobs` includes optimizerGates and clears them to NULL/unset, which selects v2, not off (`CliSelfTests.fs:96,118`). No mirror-knob mismatch found.
- `git ls-files --eol src/cpp/index_types.h` reports `i/crlf w/crlf`, not mixed. The constructor/tag/guards are unconditional host C++17 code, with <stdexcept> already included; they are not hidden behind GCC-only macros. They are parsed by MSVC and nvcc host compilation wherever the header is included. No MSVC/CUDA compile was performed or inferred successful.

**Five hook checks (h).**

| Claim | Hook at 9012128c | Finding |
|---|---|---|
| Complete positions demand / imported references | `Optimize.fs:3109`; `Lowering.fs:3086` | Local census present; foreign coverage incomplete, finding 1. |
| Mask-site gather, both named stores retained | `CodeGenExpr.fs:2278`; `CodeGenBinding.fs:2378` | Block predicate/gather emitted; compound adopts buffers. Motion barrier incomplete, finding 2. |
| LLVM twin / same strict-chain blocks | `EmitLlvm.fs:4255,4337,7868` | Shared block/chain helper and guarded fallback present; not executed. |
| Cardinality-only index and lookup guards | `src/cpp/index_types.h:443,468,471,483,491,498` | n/m preserved; all listed lookup entry points guard; reachable guard in finding 1. |
| b4 differential mirror | `Optimize.fs:174`; `tests/OptDiff.fs:180` | Registered and scheduled against defaults in identity regime. |

**T4a decision (g): DROP from the default for this round; retain corrected T4b.** The stated independent 0.18–0.36 ms gain meets the brief's explicit <0.4 ms drop condition. “v2 fastest overall” does not override that condition, particularly without allocation-inclusive runner results and peak working set. Reconsider only after the harness A/B supplies contrary evidence under an explicitly revised acceptance decision.

**Shared-mask defect (i): confirmed; b4 still records “applied” for the overwritten first plan.** `CompactionTable.record` keeps one plan per mask; `Optimize.fs:3290-3307` deliberately repairs the record only for v2. This is historical reporting compatibility, not an honest b4 plan; correcting its diagnostic would not alter round-2 emission.

---

## codex-diff-W1-T4-2

_formerly `bench/notes/round3/reviews/codex-diff-W1-T4-2.md`_

VERDICT: MERGE AFTER FIXES

Read-only review of 9012128c..df90b999, with 66e04875..df90b999 for provenance. References are at df90b999. No builds, tests, emission or executables run; probe outcomes below are source-derived.

1. NOT CLOSED in the literal full-traversal sense (new 8); the reported foreign-demand escapes ARE fixed. Both call sites use compactionRoots: moduleReferencedIds at src/Optimize.fs:3116 and positionsDemand at :3361; Lowering.fs:3092 and :3177 supply the other modules' union. This is one shared root enumerator, not one cached visit per module. RandomInits (including address/modulus), SparseInits values, sparse keys through RootType -> IRSparseKeys(SkRuntime ...), function bodies/captures, provider-write SourceId and the listed embedded types are covered. A sparse key reading y is demanded, not an escape. Provider MaskName is a STORE FIELD name (Lowering.fs:1988), not a reference to an in-memory compound: a load_compound read constructs its own index; ignoring RootNameRef in the foreign ID set does not create that proposed escape.
2. CLOSED for compaction admissions. bindingPayload reaches assignedAnywhere (:3155), payloadWrites/payloadRefs/readsMask (:3161-3172), B4's between check (:3452-3455), fold-host snapshot (:3527), in-pass interval (:3548), producer party (:3614), and T4a between party (:3635); predicate/fold kernels are checked directly and seeds are absent. scheduleMayWriteIn stores bindingPayloadExpr at :1489-1491. A random binding's alias reaches its IRSequence payload through OuterLet/chase (:1138-1164; IRSequence is deferred at :920), so that attempted escape declines. Provider writes explicitly bar T4a (:3636). The hidden x writer keeps legal B4 and declines gather; no remaining compaction wrong-value probe established.
3. CLOSED — default/1/on is T4b; only gather enables T4a (Optimize.fs:128,3136-3137,3652). Both b4 and gather are mirrors (:178); OptDiff.fs:142,180-186 compares each with defaults under REPRO=1/CONTRACT=off. No compound means no compaction plan and no foreign census. The shared resolver change CAN make other schedules more conservative in compound-free programs; identical emission is not promised, though this preserves their source semantics.
4. CLOSED — Optimize.fs:3678 states both x loads, keep store/load and the per-source-cell value store; gap-register.md:104 separates executed 34n+8m / 26n+8m / 26n+8m from the explicitly assumed cache model 30/26/17 and 26/22/13 MB.
5. CLOSED — sharedDecline now applies in every enabled mode, including b4 (Optimize.fs:3485-3501); the overwritten first plan records declined and keeps its previous unfused emission.
6. CLOSED — derivedMap requires nonempty, all-zero input ranks and rank-1 compound output (Optimize.fs:3294-3302); whole-array at0 is uncertified and requires positions. Its separate C++ failure does not invalidate this demand fix.

7. HIGH — the other scheduling intervals still audit placeholders: src/Optimize.fs:2636-2648 (hot-row), :2772-2780 (B1 cell epilogue), :3875-3877 (compact row classes); their module chains explicitly carry b.Value at :2835/:3921. Passing IRUnit to the improved resolver cannot reveal a RandomInits/CompoundInits operand. These are live source-derived wrong-answer candidates, not declines: the producer and consumer remain pure, while an intervening random key mutates the producer. No execution claim is made.

Smallest B1 probe (identity regime; gate on predicts s=13, BLADE_CELL_EPILOGUE=off predicts the correct s=90):
```blade
import rand as r
type I = Idx<2>
function bump(a: mut Array<Float64 like I>) -> Int64 = {
    a(0) = 9.0
    7
}
let x: Array<Float64 like I> = [1.0, 2.0]
let mut p = method_for(x) <@> lambda(v) -> v + 1.0 |> compute
let noise = r.uniform(bump(p), 1)
let s = reduce(method_for(p) <@> lambda(v) -> v * v, (+), 0.0)
```

Hot-row variant: add type J = Idx<2>; change bump's parameter to Array<Float64 like I, J> and assignment to a(0, 0) = 9.0; replace p with method_for(range<I, J>) <@> lambda(i, j) -> 1.0 |> compute and s with reduce(method_for(p) <@> lambda(row) -> prodsum(row, row), (+), 0.0). Predicted hot-row on/off: 4 versus correct 84 (disable CELL_EPILOGUE to isolate hot-row; enabling its row epilogue does not repair the interval).

Compact-row variant: keep x above, change bump's parameter to Array<Float64 like SymIdx<2, I>> and assignment to a(0, 1) = 9.0; replace p with method_for(x, x) <@> lambda(a, b) where comm(a, b) -> a + b |> compute; replace s with reduce(method_for(range<I>) <@> lambda(i) -> reduce(method_for(range<I>) <@> lambda(j) -> p(i, j), lambda(a, b) where omp -> a + b), lambda(a, b) where omp -> a + b, 0.0). In the licensed regime, predicted compact-row on/off: 12 versus correct 24. REPRO declines the licensed compact-row emission and therefore hides this probe.

A CompoundInits variant uses a distinct pre-existing all-true mask and compound(bumped(p), other), with bumped mutating p and returning a fresh dense array (the pattern already pinned by index-types/531). It has the same placeholder hole; do not insert an ordinary binding that reads p and accidentally triggers the existing barrier.

Exact fix: retain the original values for producer/consumer recognition, but resolve every intervening module binding by ID and apply read, repeatability and write checks to its complete bindingPayload (plus relevant provider effects); nested ordinary IRLet values keep their existing checks. Add these hidden-operand variants to the three planner decision/value regressions. Merely replacing the shared initializer map is insufficient.

8. MEDIUM — src/Optimize.fs:3049-3064 is not the claimed complete embedded-index traversal. IRUngroup, IRUngroupRows and IRUngroupGrid carry source index records, but neither this match nor childrenOf visits them (IR.fs:2304-2306). A nested ungroup whose source extent contains a y lookup is consequently invisible at this seam unless another root happens to expose it. No frontend-reachable positions-free lookup established. Exact fix: add those three source-index cases to embeddedExtentExprs and recursively visit embedded types within extracted extent expressions; pin local and foreign census coverage with an IR fixture whose only y reference is in that index extent.

(4) Other planners: bare values, not a repaired payload audit; new 7 constructs the missed hidden writers. Pre-existing, still live after this fix.
(5) D-W14-2: real, separate pre-existing zero-loop-level parameter-binding defect, consistent with the supplied baseline failure: CodeGenLoopNest.fs:1520 initializes an empty parameter map, fills it only while peeling levels (:2064-2095), then renders with it (:2099); a whole-array kernel has no such level. Those sites are unchanged from 66e04875. Add a distinct gap row with the at0 probe and baseline BL9002; fix by binding zero-S-dimension parameters to their whole operands. The exact '__v15' diagnostic was not independently reproduced here.

---

## codex-wave1-merged

_formerly `bench/notes/round3/reviews/codex-wave1-merged.md`_

VERDICT: FIX BEFORE A/B RESULTS ARE TRUSTED

Reviewed 66e04875..9da403a8 against item tips 2e6f5ea5, 88b9b0c5, cab20d31 and 82cc5e99. Files/git only; no builds, tests, emission, programs or timings run. Probe outcomes below are source-derived. Findings are record defects; no lost implementation hunk, duplicate-counted test or cross-item wrong value established.

1. **HIGH ? the merge renumber corrupted G51's FIXED evidence.** `bench/notes/gap-register.md:101` now says **`corpus loops/537-536`**; cab20d31 correctly says `loops/528-536`. A2's nine files remain 528?536; only W1-4's three files moved to 537?539. **Exact fix:** change this one G51 citation back to `loops/528-536`. Keep G63's `loops/537-539`, `CliSelfTests.fs:861` and `OptimizeTests.fs:1589` unchanged. All those files exist.

2. **HIGH ? stale merged records.** G58 (`bench/notes/gap-register.md:108`) attributes its refusal boundary to `unforcedTupleArrayMember`, which no longer exists after W1-3's fix round. **Exact fix:** replace that sentence with ?`explicitComputeTupleMember` and `bareDeferredTupleMember` accept array-typed leaves only; `tupleNeedsMemberOrder` does not descend into a named deferred tuple.? At `bench/RESULTS.md:2690`, replace ?the shared contract is a gap row at merge? with ?the shared contract remains OPEN as G61?; make the same G61 replacement at `gap-register.md:198`. The substantive 15.4 closure is correctly limited to explicit computes; `tuples/041` remains the BL7004 refusal, not a closed bare-member contract.

(a) **Cross-item probes.** These are review text only, not executed or added to the corpus.

A2 ? W1-4, smallest nontrivial row-view base followed by a hidden writer:

```blade
import rand as r
type I = Idx<2>
type J = Idx<2>
function bump(a: mut Array<Float64 like I, J>) -> Int64 = {
    a(0, 0) = 9.0
    7
}
let mut u = method_for(range<I, J>) <@> lambda(i, j) -> 1.0 |> compute
let noise = r.uniform(bump(u), 1)
let s = reduce(method_for(range<I>) <@> lambda(i) -> reduce(u(i), (+)), lambda(a, b) where omp -> a + b, 0.0)
let q = reduce(method_for(u) <@> lambda(row) -> prodsum(row, row), (+), 0.0)
```

With REPRO=1, CONTRACT=off and TILE_TUNE=rowslots_min=0, predicted `s = 12.0`, `q = 84.0`. A2 emits row slots **at s**, after noise has written u; it neither moves u's producer nor crosses noise. Its ?intervening = none? record is accurate (`CodeGenBinding.fs:74,134,4818`). The separate hot-row candidate q cannot move to u: `intervalPayloadOf` exposes noise's random-key call, and the interval refuses it (`Optimize.fs:1478,2666`). The licensed row helper likewise reads the post-write base. Neither path resurrects 4 instead of 84.

W1-3 ? W1-4, a compound held beside an explicitly computed derived member, then projected and keyed:

```blade
let x = [1.0, 2.0]
let keep = mask(x, lambda(v) -> v > 1.5)
let y = compound(x, keep)
let g = method_for(y) <@> lambda(v) -> v + 10.0
let held = (g |> compute, y |> compute)
let a = held[0]
let b = held[1]
let ga = a(1)
let yb = b(1)
```

Predicted `ga = 12.0`, `yb = 2.0` under default, b4 and gather. `positionsDemand` tracks g and rejects its uncertified tuple/IRCompute use, as well as y's; positions are **required**, never elided (`Optimize.fs:3329-3439`). The member-ordered emitter computes g before constructing held and carries its context forward (`CodeGenBinding.fs:815`). Replacing held with `(y, 0.0)` also demands positions immediately; projection need not be followed to establish demand. Replacing y's initializer with `compound(method_for(x) <@> lambda(v) -> v * 2.0, keep)` predicts 14.0/4.0: W1-3 forces the inline operand, while the named-source compaction admission declines. Literal `compound(...)` inside the tuple is outside the supported surface and is refused BL6002 (`Lowering.fs:909`). A bare deferred g member remains refused, per G61. No positions-free tuple escape found.

(b) **Shared text.** Actual function bodies differing from every touching item's tip: **`CodeGenBinding.genCompoundInitBinding` and `EmitLlvm.emitCompoundInit` only**. The first combines W1-3 operand materialization with W1-4 compaction; the second combines dense-before-mask order with W1-4's buffers. `materializeMaskFormCounted` itself equals W1-4; the apparent tail difference is W1-3's following `setOpOperandWalk` declaration/documentation. `Optimize.fs` and `CodeGenExprSupport.fs` equal 82cc5e99; the A2 row-slot/reduce bodies equal cab20d31; tuple helpers equal 88b9b0c5. All changed compiler/runtime files equal the union of the item edits, with no lost/duplicated tail. CLI/OptimizeTests integration differences are the intended reference renumbering. W1-5 workload/oracle/pin additions survive.

(c) **Collisions/counts.** No new numeric-prefix or corpus TEST-name collision; no duplicate new OmpTests/OptimizeTests case label or duplicate invocation in their result lists. OmpTests' repeated local label `path_b_reduce_over_computation` belongs to different prefixed test families, not one counted case. Corpus discovery collects each file once; opt-diff's OFF, b4 and gather comparisons are different comparisons with distinct artifact stems. Remaining duplicate prefixes are all baseline: `ad/007,008`; `functions/023,086`; `index-types/238`; `intrinsics/008-011`; `loops/214`; `multifile/011`; `mutability-errors/042`; `reynolds/011-015`; `symmetry/132`; `unit-errors/019-021`; `units/048,077`. They do not create duplicate-counted tests. No merge renumber is needed for these unrelated pairs.

(d) **Register.** G54?G64 each occur exactly once; no duplicate G-number anywhere. Newly cited corpus ranges exist: A2 loops/528?536; W1-3 sql-masks/014?018, sql-set-ops/012?013, sql-unique-contains/009?010, tuples/034?043; W1-4 index-types/522?531, multifile/037?040, loops/537?539, symmetry/137. G51's inverted range is finding 1. RESULTS 15.4 and G61 agree on the open bare-member behavior; their future-tense reference is finding 2.

(e) **Knobs.** All three have CLAUDE rows, plan evidence and suite clearing (`CliSelfTests.fs:83-96`). FOLD_LANES' row-view arm records K/seed/form (`CodeGenBinding.fs:5152`); its 0/4 mirrors are covered by the OmpTests cases, not optimizerGateMirrors. COMPACTION_FUSION is in optimizerGates and both `b4`/`gather` are in optimizerGateMirrors (`Optimize.fs:167,178`); OptDiff iterates those mirrors against defaults (`OptDiff.fs:180-190`), reached by both CLI opt-diff dispatches. TILE_TUNE's sel_cap is recorded by the selection admission (`Optimize.fs:3711-3715`) and cleared as part of TILE_TUNE; explicit cap/fallback cases are in OptimizeTests and CLI smoke. **Scope:** opt-diff pins repro, so it does not validate the licensed FOLD_LANES arm; it exercises gather at the default cap, not an independent sel_cap mirror. No missing applicable registration found.

(f) **Emission:** the three exclusions alone do NOT guarantee unchanged emission (W1-3 also changes pure dense tuples of computes and sparse set operands; A2 changes OMP test instrumentation); none of those new shapes occurs in the ten other existing kernel sources in this tree (14 total including the two new kernels). For those sources, no changed emission predicate was identified. `scheduleMayWriteIn` now resolves `bindingPayloadExpr` and interval checks use `intervalPayloadOf` plus provider effects; absent executable side tables/provider effects they receive exactly the old values. This is a source audit, not a byte-identity measurement.
