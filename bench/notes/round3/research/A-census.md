# A -- round-3 census of the planning directory, the remaining losses, and a ranked candidate list

Research only (PREAMBLE.md obeyed): no build, no test, no `blade run`, no g++. Four `blade emit` calls
(bench-fortran binary, ucrt64 first on PATH, private cwd `scratchpad\round3\A-emit\`): stencil licensed,
pairkernel / saxpy / band under `BLADE_FP_REPRO=1 BLADE_FP_REASSOC=0`; the files are kept there.
Source trees read: `worktree-bench-fortran` e64fef3b (round-2 tip; line numbers below are this tree's
unless marked w3) and the wave-3 worktree `magical-cannon-db55bf` (b80e29ad + uncommitted notes; marked w3).
Ratios: expert / Blade (> 1 Blade faster), gf / ifx. `ind` = harness-suppressed block.

**Caveats that change how numbers below read.** (1) The wave-3 harness A/B (`bench/notes/wave3/w3-scripts/tables.md`)
ran beside a stray single-core `find` (16:50-20:16); its figures are directional and are being re-run now
(RESULTS 16.3 is still empty). (2) Gap numbers COLLIDE: wave 3 opened G48 (row-slot row-view decline) and G49
(teardown inside the timer), while `vigilant-hellman-c521dc` (unmerged) used G48 / G49 / G50 for three
different, now FIXED effect defects. One of the two must renumber before either merges. (3) The gap-register
G37 row still says "fix in progress"; the fix landed (963b9aa1, an ancestor of 4136f0b5 and e64fef3b:
the split conversion only in closed-form domain cell code, `CodeGenExprSupport.fs:245-262, :355-359`).

## Part 1 -- census (planning proposal -> status after rounds 1-2 and wave 3)

Status words: BUILT (commit / RESULTS / knob), PARTIAL (what is missing), NOT BUILT, REFUTED (measured
negative), DROPPED (on an instrument or a count), SUPERSEDED (by what). Round-2 commits are bench-fortran's
linear ones (407c132d W2-A, 16849599 W1-B, fabcd924 W1-C, 906c91db W1-D, 065b430e W1-A, cac0b8a9 W2-D,
350aeb74 W2-E, b40defeb/e02ef3f1 W2-F); wave 3 = 3a676a24 (E-G5), b4a21003 (stencil attribution).

### 1.1 README.md "Recommended implementation priorities"

| # | proposal | status | where / evidence |
|---|---|---|---|
| 1 | ordered symmetric scatter folds (rank 2, rank 3 with shared inner chains) | BUILT, both lanes | W2-A; `BLADE_ORBIT_SCATTER`, TILE_TUNE `scatter_g=4 scatter_b=64 scatter_vrow=1`; `blade test orbit-scatter`; RESULTS 15.3b: serial repro pairkernel 0.929/0.859 -> 1.163/1.079, comoment3 0.770/0.775 -> 1.288/1.299 (knob-off restores). Not taken under an mt8 omp request (G35 precedence, Part 2 row 1) |
| 2 | fuse scalar consumers at output stores (cells, rank-1 saxpy, stable compaction) | BUILT (B1, B2r, B3, B4); B2l REFUTED | W1-B `BLADE_CELL_EPILOGUE` (P1 lane state, saxpy rank-1, recur/stencil chain interleave), W1-C `BLADE_COMPACTION_FUSION`, `BLADE_COMPACT_ROW_CLASSES`; B2l licensed-lanes arm: hand patch measured null (w3 second-look: 13.3 vs 13.0-14.0 ms, bits identical) |
| 3 | share traversals of sibling folds (dot, fusedchain, onepass) | BUILT; PARTIAL for producer legs | W1-D `shareSiblingFoldsModule` (`Optimize.fs:3906`; w3 :4196), `BLADE_SIBLING_FOLDS`; repro dot 1.213 -> 1.696 gf. Missing: legs riding a PRODUCER pass (saxpy input sums, excluded by the W1-B/W1-D surface rule) and the input generator (G36 fastmath half) |
| 4 | stable ownership and persistent tile teams | DROPPED (instrument) | D1: wave-1 count; w3 instrument 236-257 us/call vs persistent 299-376 (201-panel re-run 251-293 vs 284-335); D3a weighted simplex tasks dropped (LPT 1.000). Open card: `nowait` on the final loop, a real `dsyrk_tri` instrument |
| 5 | antisym panel reuse; selective tile variants | PARTIAL | W2-D `anti_g` (default 1; 0 = cache budget), simplex `tri_edge=1` (+2%, live); 6x4 / narrow / lower-register tiles NOT BUILT. anti_g auto: mt8 -4.6% (w2, ind) and -5.7% (w3 first run, ind), serial +2% (w3, live) |
| 6 | independent recurrence fibers; tuple reductions | BUILT (C++ lane) | E-G5 `BLADE_REC_STEP_OMP` (w3, `where omp(v: 1)` on the step's final map, one persistent team, no per-step sync); G7 `BLADE_JOIN_THREADS` (W2-E). Spatial blocks through time (serial) NOT BUILT; LLVM serial (G11) |
| 7 | reuse packs across unchanged consumers | NOT BUILT | no suite kernel has two compatible contraction consumers; needs a new workload (C) |
| 8 | matrix-free and multi-output regions | NOT BUILT | region track: items B, C |
| -- | S11 producer-orbit epilogue | PARTIAL | B3 = licensed rank-2 scalar pair map (row classes behind each packed row); contraction / rank-3 epilogues NOT BUILT |

### 1.2 existing-kernels-review.md section A (scatter) and section B (per kernel)

| kernel | proposal | status |
|---|---|---|
| (A) | rank-2 / rank-3 ordered scatter, retained-H first form, refusals (seeds, symmetry, antisym, effects, alias) | BUILT (W2-A: slices + same-Z third event + retained H). Antisym scatter NOT BUILT (`OrbitFold.planStrict:715` declines). Per-row seeds / coordinate-dependent summands refused. "v1.1 contraction invariance" REJECTED (RESULTS 15.5). Threaded scatter NOT BUILT |
| gram | persistent team + work-weighted row groups; repeat-call pack cache | DROPPED (D1, D1-0 `syrk_g` mt8 sweep null); pack cache NOT BUILT (one-shot bench) |
| comoment3 | scatter repro consumer; edge kernels; weighted prefix tasks | BUILT (W2-A rank 3); BUILT (`tri_edge`, 2%); DROPPED (D3a) |
| antisym | row-panel groups; paired 6x4 core | BUILT (`anti_g`, default 1); NOT BUILT (P3) |
| pairkernel | scatter repro checksum; licensed checksum epilogue in the producer; exp-call batching | BUILT; BUILT (B3: mt8 licensed 0.797/0.791 -> 1.020/0.967); NOT BUILT (P3) |
| recur | row-store + row-prodsum fusion at DAG level; independent-axis team; time x space blocks | BUILT (B2r repro interleave; B2l REFUTED); BUILT (E-G5, w3); NOT BUILT |
| stencil | cell checksum epilogue; 2-D strips | BUILT (B2r repro); licensed epilogue unbuilt (ceiling 0.02 ms/thread); strips NOT BUILT |
| saxpy | rank-1 epilogue on y2; fuse independent input sums | BUILT (B1); PARTIAL (W1-D joins the two input sums with each other, not with the y2 producer pass) |
| dot | dot + two input sums in one traversal; generator fused with consumers | BUILT (W1-D); NOT BUILT |
| fusedchain | share input read/checksum with the kernel fold; range-proved integer generator lowering | BUILT (W1-D); NOT BUILT |
| onepass | thread-private fold-state tuple; shared input-checksum chain; per-tuple lane budget | BUILT (G7, explicit-request policy); BUILT (W1-D); PARTIAL (`laneCountForStreams`, `CodeGenBinding.fs:4912`, for multi-stream folds; join teams use one simd reduction per leg) |
| maskagg | fused compaction payload + fold + count; no y*y pool; lazy positions | BUILT (B4: 6 passes -> 2); BUILT; NOT BUILT ("positions always built", B4d) |
| band | licensed 2-D strip schedule; team crossover from true cell count | NOT BUILT (P2); NOT BUILT |

### 1.3 Section C runtime design, optimization-architecture.md, review-decisions.md, benchmark-evidence.md

| proposal (source) | status |
|---|---|
| schedule certificate / one scheduling description (C; arch.) | PARTIAL: shared facts exist as `Optimize.scheduleMayWrite`/`scheduleMayWriteIn`/`callbackTable` (`Optimize.fs:1379/1343/1190`; W2-F), the `IRTotality` whitelist (`IRTotality.fs:101`, shared by scatter, G35, G7) and decision records; no unified plan object |
| persistent syrk team (C) | DROPPED (D1) |
| cache budget / target traits (C) | PARTIAL: `anti_g=0` compile-time 256 KiB budget (`CodeGenExprSupport.antisymRowGroup:2179`); L3 comment fixed to 16 MB (`blade_packed_gemm.hpp:208-209`); no traits table |
| restrict / noalias hints (C) | PRE-EXISTING (`BLADE_RESTRICT` on proven-fresh rows, e.g. emitted stencil `__orow___v39`); nothing new proposed |
| padded packed layout (C, P3); private blocked compact storage; shared immutable packs; layout x consumer co-selection; offline calibration profile (arch.) | NOT BUILT (all) |
| licensed epilogues for compact outputs (arch.) | PARTIAL (B3 scalar pair map; contraction epilogues unbuilt) |
| partition by canonical cell count (arch.) | PARTIAL (orbit pass row starts by `canon_count` + boustrophedon, round 1 G32); D3a DROPPED |
| recurrence as 2-D scheduling (arch.) | PARTIAL: threaded fixed-x team BUILT (E-G5); serial x-blocks through time NOT BUILT |
| tile family: narrow / lower-register / edge (arch.) | PARTIAL (edge kloops only) |
| implementation sequence 1-7 (arch.) | 1 DONE (baseline 6800/0); 2 BUILT; 3 BUILT (B3) / DROPPED (D3a); 4 SUPERSEDED for mt8 by E-G5, serial blocking untried; 5 NOT BUILT; 6 PARTIAL; 7 NOT BUILT (C) |
| first-round boundaries (review-decisions) | serial strict consumer of a stored pool DONE (W2-A); scalar-pair licensed epilogue DONE (B3); two-consumer pack reuse NOT BUILT; serial recurrence interchange NOT BUILT |
| orbit-review-decisions refinements (pair-wave ownership, 2T-1 phases, quotient joins, write-disjointness certificate, sign vs conjugation) | NOT BUILT; item D's input |
| freeze evidence; routing labels; current baselines (bench-evidence) | DONE (round-2 baseline; D47 native-preempt; RESULTS 14.8 / 15.3) |
| non-power-of-two shape sweep; 4093-4099 offset-table diagnostic; 2x2 attribution designs (bench-evidence) | NOT DONE (all A/Bs at the full bench shapes, one knob-off mirror per item) |
| phase/helper attribution; store audit after fusion; exclusive window (bench-evidence) | PARTIAL (w3 phase timers spliced into emitted C++); PARTIAL (gate blocks assert stores, no executable audit published); VIOLATED once (w3 stray process), re-run in progress |

### 1.4 whole-region-blueprints.md, deferred-region-planning.md, new-kernel-catalog.md (owners B / C / D)

| proposal | status | owner |
|---|---|---|
| blueprint ranks 1, 3, 4, 5, 7, 13 (membership build/probe, aggregate-only grouping, hash equijoin, let-spanning filter/projection fusion, group algorithm selection, join-graph search) | NOT BUILT (`contains` still linear) | B |
| rank 2 automatic sibling-fold discovery over a shared producer DAG | PARTIAL (W1-D: siblings over one STORED dense axis; no producer DAG, no generator, no cross-function producer) | A7 / B |
| rank 6 pair producer + several consumers; rank 12 symmetric endpoint / strict ordered compact regions | PARTIAL (scatter consumes a stored pool; endpoint consumption NOT BUILT) | D (+ C: N06) |
| rank 8 cross-output contraction / pack reuse | NOT BUILT | D / C |
| rank 9 recurrence demand / liveness, independent-fiber scheduling | PARTIAL (E-G5 team; terminal-state rolling NOT BUILT) | C (N01, N21) |
| rank 10 factor-consumer batching, cross-AD factor reuse | NOT BUILT (`m.lu` value exists) | C (N05) |
| rank 11 threaded multi-state regions | PARTIAL (G7 join team, E-G5 team) | -- |
| deferred-region families (normalized pairs, moments, factors, trajectories/RNG, segmented) | expressible today; automatic recognition NOT BUILT; `gram-apply-advisory` still advisory only (`Optimize.fs:2093`) | B / C / D |
| catalog top ten N01 stencil time tiles, N02 halo primal+VJP, N03 ragged online attention, N04 banded DP frontier, N05 factor-once sensitivities, N06 antisym pair force action, N07 cross-segment rolling filter, N08 addressed MC derivatives, N09 tensor-product action, N10 grouped NLLS | NOT BUILT (no spec, no expert, no kernel dir) | C |
| N11-N45 by name: N11 sym rank-3 tensor action, N12 equivariant irreps maps, N13 quotient-space action, N14 exterior products, N15 block Jacobian structural zeros, N16 cascaded filters one ring, N17 separable filter bank, N18 masked normalization, N19 typed-bin histogram, N20 rowwise top-k, N21 terminal independent dynamics, N22 checkpointed reverse AD, N23 integer segmented scan, N24 banded triangular solves, N25 monotone relaxation on a DAG, N26 sparse intersection dot, N27 sparse union axpby, N28 fixed-topology SpMM, N29 FE residual assembly, N30 resonance triads, N31 broadcast-reduce reverse, N32 Hessian-vector local energies, N33 spectral convolution derivative, N34 producer-fused AoSoA update, N35 transpose + nonlinear column filter, N36 regularized Gram actions shared panel, N37 Hermitian-aware spectral filter, N38 boundary-typed Poisson, N39 overlap-save FIR, N40 grouped online covariance, N41 compensated summaries, N42 polynomial jets, N43 rejection sampling identities, N44 bootstrap weights, N45 tensor quadrature | NOT BUILT, unscoped this round (N06/N11/N14 overlap D; N21 builds on E-G5; N36 on `gram_apply`) | -- |

### 1.5 Knobs that exist (both CLAUDE.md copies carry rows; w3 adds the last)

Optimizer gates (`Optimize.optimizerGates`, `Optimize.fs:123`; w3 :129): FUSION, FREEZE_IDIOM, CSE, POOL_REUSE, HOIST,
HOT_ROW, CELL_EPILOGUE, COMPACTION_FUSION, COMPACT_ROW_CLASSES, SIBLING_FOLDS, REC_STEP_OMP (w3). Cleared emission knobs
(`CliSelfTests.suiteClearedKnobs:83`) include FOLD_ROW_SLOTS, ORBIT_SCATTER, JOIN_THREADS, FOLD_LANES, TILE_TUNE. TILE_TUNE
keys and defaults (`CodeGenExprSupport.fs:2146-2256`): `syrk_g=4 anti_kc=0 anti_g=-1(=1) tri_edge=1 rowslots_min=262144
epi_blk=256 epi_blk_r=0 scatter_g=4 scatter_b=64 scatter_vrow=1`; `lanes` reserved (B2l).

## Part 2 -- the remaining losses (every cell < 1, or indicative near 1)

Latest measurement per cell: w1 = RESULTS 15.3 / `w1-ab.md`, w2 = 15.3b / `w2-ab.md`, w3 = wave-3 first run
(contaminated, re-run pending), 14.8 = round 3 (not re-measured since). Blade ms vs gf / ifx ms.

| # | cell | ratio [src, class] | ms | what the expert does / what Blade does | planning proposal | admission a fix needs | eff. | cand. |
|---|---|---|---|---|---|---|---|---|
| 1 | pairkernel mt8 repro | 0.687 / 0.664 [w2, ind] | 9.354 vs 6.423 / 6.213 | expert: one team, weighted packed checksum, `reduction(+:)` (not Blade's contract, Codex audit 3). Blade: G35 row slots thread the CANONICALIZING logical walk (6,265,009 `canon_fold<2>` reads, 3,131,253 strided mirror reads; emitted `pairkernel_repro.cpp:299-319`), ~3.2 ms over mt8 licensed (6.157). The serial ordered scatter (~1.2 ms serially: serial repro cpp 29.52 vs licensed ~28.3) is never taken: `scatterG35Precedence` (`OrbitFold.fs:865`, `CodeGenBinding.fs:4465-4475`) | README P1 (serial scatter BUILT), "retain a serial consumer after threaded production" (review A refusals) | none new: `planStrict` (IRTotality pure/total summand, exact seeds, stored pool in scope) | S | A1 |
| 2 | comoment3 mt8 repro | 1.097 / 0.925 [w2, ind] | ~4.55 (w1) vs 4.91 / 4.32 | same shape: G35 v1 threads 171 outer rows of a 5,000,211-read walk (~1.5 ms over mt8 licensed ~3.0); the serial rank-3 scatter reads 848,046 cells and costs ~0.3 ms serially (9.056 vs ~8.76) | G35 v2 leaf-chain slots (round-2 wave 3, conditional, NOT BUILT) | as row 1 | S | A1 |
| 3 | antisym mt8 repro | 0.993 / 1.000 [14.8, live] | 11.22 vs 11.15 / 11.22 | tile threads (one chain per cell, fine); checksum is the strict logical walk (no antisym scatter) | antisym scatter extension (secondary, NOT BUILT) | planStrict + sign/diagonal events | M | below line |
| 4 | recur mt8 licensed (new arm) | 0.747 / 0.803 [w3, ind] | 10.58 vs 7.90 / 8.50 | experts free the 48.19 MB trajectory after their stop line; Blade frees it inside (`blade_pool` keeps 16 KB..4 MB, `nested_array_utilities.hpp:104-115`): +3.4-5 ms after a threaded fill (G49). Region near parity (pre-touched 5.9-6.3 vs 5.38). Retention instrument (kMaxBlock 64 MB, kBudget 96 MB): 7.9-8.2 ms end-to-end | none (protocol asymmetry, bench README item 6) | none (runtime policy) | S + user decision | A4 |
| 5 | recur mt8 repro (new arm) | 0.599 / 0.630 [w3, live] | 13.72 vs 8.22 / 8.64 | G49 as row 4, plus the strict post-pass (w3 `CodeGenBinding.fs:6280-6305`): complete-row slots in parallel AFTER the team re-read 48 MB from DRAM (+3.1 ms over licensed 10.58). Expert reduces each row inside its persistent team (thread reduction, not Blade's contract) | arch.: "original-order checksum pass after production ... or a different block/time schedule" | E-G5's admission + G35's outer-kernel gate + row kernel reads only its own row | M | A5 (+A4) |
| 6 | stencil mt8 repro | 0.822 / 0.875 [w1, ind]; 0.754 / 0.784 [w3, live] | 9.524 vs 7.83 / 8.34 | input checksum serial 2.7-2.9 ms: `rowSlotAdmission` (`CodeGenBinding.fs:71`, `isCompleteFold` takes only `IRReduceCompute`) declines the row-view fold `reduce(u(i), (+))` (G48 of w3); `lap` teardown 1.0 ms inside the timer (G49) | w3 second-look card (Codex: ~60-150 lines) | rowSlotAdmission + `IRTotality.walk` IRReduce/row-view arm + base array not written (scheduleMayWriteIn) | S | A2 (+A4) |
| 7 | stencil mt8 licensed | 0.903 / 0.970 [w1, ind]; 0.849 / 0.889 [w3, ind] | 8.583 vs 7.75 / 8.33 | input checksum 1.5-1.9 ms vs a 0.7 ms stream read of the same 24 MB at T=8 (w3 `rd.cpp`). Emitted (`stencil_lic.cpp:305-333`): Path B puts `BLADE_OMP_SIMD_REDUCTION` on the ROW loop whose body is the expression-form 8-scalar-lane IIFE (`CodeGenExpr.fs:1559 laneForm`, `__wrap_70` per element through an `Array<double,1>{...}[k]` view) -- not `blade_fold::sum<double,8>` (`blade_fold_lanes.hpp:141`), which the kernel's own row fold gets as `sq<double,8>`; + G49 1.0 ms | w3 second-look follow-up ("the emitted per-row form, not the licence") | none new (licensed form change only) | S | A2 (+A4) |
| 8 | saxpy mt8 repro | 0.811 / 0.871 [w1, live] | 11.07 vs 8.97 / 9.65 | two serial chain passes after threaded producers (emitted `saxpy_repro.cpp`: joined x/y input sums, 2M iterations; then the y2 square chain, 2M); B1 declines a threaded producer. Experts thread `reduction(+:)` (different reproducibility contract) | "ordered commit buffers" (review A refusals) | producer kernel / fold kernel / seed via scheduleMayWrite + IRTotality (panic order) | M-L | A6 (below line) |
| 9 | saxpy mt8 licensed | 0.938 / 0.970 [w1, ind] | 9.413 vs 8.83 / 9.13 | input-sum pass (32 MB) separate from the y2 producer epilogue; y2 (16 MB) freed in the timer (G49 class, unmeasured) | as row 10 | as row 10 | M | A7c |
| 10 | saxpy serial vs ifx | licensed 1.377 / 0.972, repro 1.375 / 0.878 [w1, live] | 10.93 / 11.19 vs ifx 10.63 / ~9.8 | ifx updates kernel and input-checksum chains in ONE loop (G36, asm-confirmed). Blade repro net 3.30 vs ifx 1.97 ms: one extra pass over x, y. The W1-B/W1-D surface rule keeps the y2 producer's leg out of the sibling join ("C must NOT take a leg whose operand is a fresh `|> compute`d map ... while BLADE_CELL_EPILOGUE is on") | saxpy row "fuse independent input sums where legal" | scheduleMayWriteIn over producer, legs' kernels / seeds, intervening bindings; legs read producer INPUTS only; no writing leg (G43) | M | A7c |
| 11 | ifx fastmath dot / onepass / fusedchain | 0.782 / 0.820 / 0.941 (ind) [w1] | 11.03 vs 8.63; 10.06 vs 8.25; 3.89 vs 3.67 | Blade's net (total - own twin) is already ~0 (0.12 / 0.49 / 0.37 ms): the whole gap is the GENERATOR phase. ifx fastmath fuses the 5-round generator into the consumer loop (inputs still stored; G36). Blade: two stored generator passes, then one joined read pass; `gen` emits plain int64 `%`/`/`/`*` under `-fwrapv` (`saxpy_repro.cpp:189-196`, `Build.fs:107`), scalar on AVX2 (no packed int64 mul). Blade fastmath == licensed knobs by policy | dot row "compiler-fuse input generation with all consumers while retaining stores"; fusedchain row "range-proved integer generator lowering" (P2/L) | (a) as row 10 across two producers + function boundary; (b) none (exact integer values) | L / M | A7ab |
| 12 | maskagg serial vs ifx | licensed 1.062 / 0.788, repro 1.062 / 0.813 [w1, live] | 3.73 vs 3.96 / 2.94 | net 2.02 vs ifx 1.21 / gf 1.04 ms: B4 still builds `rank_to_tuple` positions (4 MB) and the compound index object; ifx packs | maskagg row "lazy key/rank metadata when all consumers are storage-order scans" (B4d) | consumer census of `y` / its index (no keyed, partial, wildcard read; no escape) | M | A8 |
| 13 | band mt8 repro | 0.302 / 0.520 [14.8, ind] | 4.678 vs 1.411 / 2.431 | ONE global lex chain (4,051,931 cells): strict floor ~2.8 ms at 3-cycle add latency, so the strict ceiling is ~0.51 / 0.88 even with free terms. Expert threads rows with `reduction(+:s)` | none | as row 8 | M-L | A6 (below line) |
| 14 | band serial repro vs gf | 0.844 / 1.008 [14.8, ind] | 3.685 vs 3.111 / 3.716 | same lex order and per-cell phi in the expert; Blade ~4.0 cycles/cell vs gf ~3.4 (floor 3). Hypothesis: per-row (203-cell) in-order-reduction epilogues; the C++ lane also emits two BL8006 `(i : Nx)` checks per cell (`band_repro.cpp:226-229`) although the struct bounds prove them (LLVM ref not inspected) | none | none | S | below line |
| 15 | band mt8 licensed vs gf; pairkernel mt8 licensed vs ifx; fusedchain mt8 licensed vs gf | 0.972 [14.8 ind]; 0.967 [w1 ind]; 0.997 [w2 live] | 1.07 vs 1.04; 6.157 vs 5.955; -- | sub-ms team entry (~40 us) on band; pairkernel producer bound by scalar platform `exp`/`sqrt` (Blade contract) vs a possible vector maths runtime in ifx; parity | band strips P2; exp batching P3 | -- | -- | below line |
| 16 | mt8 fastmath dot / fusedchain; onepass mt8 repro / fastmath | 0.911 / 0.992, 0.909 / 0.928 [14.8 ind]; unmeasured | -- | STALE: measured before the G7 join team, which moved mt8 licensed dot 0.867x and fusedchain 0.926x (w2); onepass's mt8 arm is new | -- | -- | re-measure | -- |

Closed since 14.8 (not losses now): serial repro pairkernel / comoment3 (W2-A), recur serial (G8 + B2r: 1.309 / 1.336
repro, 1.094 / 1.106 licensed in w3), dot / onepass / fusedchain serial repro (W1-D), mt8 repro dot (1.163 / 1.182),
comoment3 mt8 repro vs gf.

## Part 3 -- ranked round-3 candidates (A-D)

| rank | item | expected cells (estimate, not a forecast) | mechanism | effort | drop experiment | depends on |
|---|---|---|---|---|---|---|
| 1 | **A1** scatter-vs-row-slot precedence by cost under mt8 repro | pairkernel mt8 repro 0.687 / 0.664 -> ~0.89 / 0.86; comoment3 mt8 repro 1.097 / 0.925 -> ~1.45 / 1.27 | threaded producer, then the SERIAL ordered scatter (contiguous pool, half the reads) instead of the threaded canonicalizing walk | S | ZERO-CODE: `BLADE_FOLD_ROW_SLOTS=0` on both mt8 repro cells vs default (the decline record says it yields the scatter); drop if not >= 10% faster | before D (D's baseline) |
| 2 | **A2** row-view inner folds in the Path B / row-slot forms (G48 + the licensed form) | stencil mt8 repro -> ~0.93 / 0.97; mt8 licensed -> ~0.95 / 0.99 (each +~0.12 with A4) | repro: admit `reduce(u(i), op)` as a complete row fold; licensed: route it to `blade_fold::sum<T,K>`, no `omp simd` on a row loop whose body is a complete fold | S | zero-code for repro: the `stencil_d` spelling under mt8 repro (w3 plan: `1501 row folds into indexed slots`); licensed: hand-patched emission, phase timer; drop if the checksum phase stays > 1.2 ms at T=8 | -- |
| 3 | **A4** G49 bounded large-block retention -- USER DECISION | recur mt8 licensed 0.747 -> ~0.95 (measured instrument 7.9-8.2 ms); recur / stencil mt8 repro and licensed +1-5 ms each | release large pools at thread exit (after the stop line) as the experts do | S code | already instrumented; the remaining test is a harness A/B with the knob, and the user's footprint ruling | multiplies A2, A5 |
| 4 | **B** relational region wave A (membership build/probe, aggregate-only grouping) -- design: `round3/research/B-*.md` | new cells only (R1/R2 + SQL/DuckDB references); O(NM) -> O(N+M) lookups | region recognition over existing spellings | M per rule | B's own (tiny-M linear control, duplicate-key fixture) | none on A; shares provenance seam with A7a |
| 5 | **A5** E-G5 repro: lagged in-team complete-row folds | recur mt8 repro 0.599 / 0.630 -> ~0.77 / 0.81 (~1.05 with A4) | each worker folds the complete rows it owns as soon as a per-row completion counter reaches T; rows read from L3, not a 48 MB DRAM post-pass | M | hand-patched w3 emission with counters; drop if the post-pass cost (3.1 ms) does not fall below 1 ms | A4 for parity |
| 6 | **A7c** producer-epilogue sibling legs (saxpy) | saxpy serial repro ifx 0.878 -> ~1.0; licensed ifx 0.972 -> ~1.03; mt8 licensed 0.938 / 0.970 -> ~1.0 | the input-sum legs ride the y2 producer pass (B1 lane state per leg) | M | hand-patched emitted C++ (move the joined loop into the producer loop); drop if < 0.8 ms on serial repro | before A7a |
| 7 | **A8** maskagg position elision (B4d) | maskagg ifx 0.788 / 0.813 -> ~0.85-0.9 (net -0.3-0.4 ms) | do not build `rank_to_tuple` when every consumer of `y` is a storage-order scan | M | hand-patched C++ without positions; drop if net falls < 0.25 ms | overlaps B (compaction) |
| 8 | **C** N01-N10 workloads -- design: `round3/research/C-*.md` | new cells; README first set N01, N02, N04, N07, N10 | new benchmark specs + two experts each | S-M each | C's own | feeds D (N06) and A5-type schedules (N01) |
| 9 | **A7ab** generator phase: (a) generator -> consumer fusion, (b) range-proved integer lowering | ifx fastmath dot 0.782, onepass 0.820, fusedchain 0.941 -> ~0.9-1.0; (b) moves every kernel's input phase | (a) store inputs and feed the consumer chains in the generating loop; (b) operands proved in [0, 2^31): 32x32->64 unsigned multiplies (`vpmuludq`), `% 2^k` -> mask, `/ 2^k` -> shift | L / M | (b) hand-edit `gen` in the emitted dot twin, phase timer; drop (b) if < 10%; (a) hand-fused loops, drop if < 1 ms | (a) after A7c and B's provenance seam |
| 10 | **D** orbit / strict-symmetry region plan (pair waves, endpoint consumption, residual-symmetry towers) -- design: `round3/research/D-*.md` | current suite only through rows 1-3 (after A1); otherwise new workloads (N06, N11, S2) | D's | M-L | D's; re-run A1's cells with D's form vs A1's | after A1 and C (N06) |

Dependencies, plainly: A4 is a multiplier, not a peer -- A2 and A5 reach parity on the mt8 cells only with it,
so the user's retention ruling should come first. A1 is D's baseline: if A1's zero-code experiment brings pairkernel
mt8 repro to >= 0.95, D has no current-suite cell left and is justified by new workloads (C's N06 / N11) alone. A7c
precedes A7a (same epilogue machinery, smaller), and A7a waits for B's provenance seam (Lowering force points). A8
and B share the compaction / mask machinery; schedule them apart. B, C and A1-A5 are otherwise independent and can
run in parallel worktrees (file overlap in section 8).

Below the line (not ranked): **A6** ordered-commit pipeline (a producer team publishes chunk-ready flags, one thread
runs the untouched serial chain behind it; saxpy / band mt8 repro; bounded by the chain floor and a contract asymmetry;
spin-wait inside an OpenMP team is the risk); `anti_g` per-regime default (auto under threads only; flagged indicative
twice); band per-row overhead and the proven-cast checks (gf-only cell); antisym scatter; G35 v2 (SUPERSEDED by A1 if
A1's experiment passes); pairkernel exp batching (P3).

## 4. Hook verification

| claim (source) | current location (e64fef3b unless w3) | verdict |
|---|---|---|
| orbit recognition `OrbitFold.fs:312`, licensed veto `:423-426` (EKR) | `recogniseWalk:343`, `planLicensed:448` (repro decline `:483`), strict plan `planStrict:698` | changed (strict plan is a new sibling, as proposed) |
| `genOrbitFoldCpp:3562`, `emitOrbitFold:6002` (EKR) | `CodeGenBinding.fs:3915` + `genOrbitScatterCpp:4120`; `EmitLlvm.fs:6453` + `emitOrbitScatter:6621` | moved; siblings exist |
| G35 precedence over the scatter (RESULTS 15.2) | `CodeGenBinding.fs:4465-4475` (`scatterTaken`), `OrbitFold.scatterG35Precedence:865`, `recordScatterYieldedToRows:869` ("BLADE_OMP_THREADS=1 or BLADE_FOLD_ROW_SLOTS=0 gives the scatter") | holds |
| row-slot admission takes only `IRReduceCompute` (w3 second look) | `CodeGenBinding.fs:71 rowSlotAdmission`, `isCompleteFold` (~:88); w3 same line | holds |
| `IRTotality.walk` has no row-view / IRReduce arm | `IRTotality.fs:101`; arms IRIndex `:173`, IRReduceCompute `:195`, no IRReduce | holds |
| stencil input checksum = Path B + lane-unrolled `__wrap` chain (w3) | emitted `stencil_lic.cpp:305-333`; `CodeGenExpr.fs:1559-1575 laneForm`; helper `blade_fold::sum` at `blade_fold_lanes.hpp:141` unused there | holds (by emit) |
| E-G5 strict post-pass after the team (RESULTS 16.1) | w3 `CodeGenBinding.genRecStepTimeLoop:6108`, `parallelRows` branch `:6280-6305`; `RecStepTable` w3 `Optimize.fs:2362` | holds |
| pool caps 4 MB / 8 MB (G49) | `cpp/nested_array_utilities.hpp:104-115` (`kMaxBlock`, `kBudget`, constexpr) | holds |
| saxpy repro: producer team, then serial chains (brief B) | emitted `saxpy_repro.cpp`: `BLADE_OMP_PARALLEL_FOR_SIMD` y2, serial y2 chain, joined 2-leg input sums seeded `-(0.0)` | holds |
| generator integer ops | emitted `gen` (plain int64 `%`, `/`, `*`); `Build.fs:107` `-O3 ... -fwrapv` | holds |
| hot-row / slice-write / row-fold / pipeline (`Optimize.fs:1504/1547/1731/2004/2221`, arch.) | `HotRowPlan:2208`, `planHotRowFolds:2324`, `fuseSliceWritesModule:3203`, `fuseRowFoldsModule:3476`, `optimizeModule:4891` (w3: 2214/2417/3296/3717/5181) | drifted +700..+1500 |
| shared write analysis (preamble) | `scheduleMayWriteIn:1343`, `scheduleMayWrite:1379`, `callbackTable:1190` (w3 :1349/:1385/:1196) | holds |
| tile drivers (`blade_packed_gemm.hpp:263-360, 399-475, 504-575`, EKR) | `dsyrk_tri:268` (team `:349`), `dantisym_tri:429` (`:506`), `dsym3_tri:555` (`:612`) | drifted |
| `genLoopNestStreamed:1491`, `genDomainFoldCpp:140`, `emitDomainDiagonalNest:1779`, `domainFoldOrder` (EKR) | `:1510`, `:140`, `:1787`, `CodeGenExprSupport.fs:2038` | holds |
| `materializeMaskForm:1997`, compound `index_types.h:354` (EKR) | `CodeGenExpr.fs:2050`; `compound_index_t` / `rank_to_tuple` `index_types.h:95-119` | moved |
| `Effects.fs:37/79/85`, `IRStorage FoldChunkPlan:95`, `gram-apply-advisory` `Optimize.fs:1410` | 37 / 79 / 85; `:95` (+ `ChunkRowSlots:128`); `Optimize.fs:2093` (advisory) | holds |
| "32 MB L3" runtime comment (`blade_packed_gemm.hpp:208`) | now "16 MB on the bench box's 5800HS" (`:208-209`) | fixed |
| BLAS preempt `CodeGenLoopNest.fs:2885`; `forceCallableBody`; elementwise fusion `IRMono.fs:2377` | `:3230`; `Lowering.fs:474`; `fuseElementwiseChainsModule IRMono.fs:2579` | moved |

## 5. The transformations, precisely

**A1 (precedence).** Recognized shape: the `reduce` over a deferred SymIdx rank-2/3 logical walk for which BOTH
`planStrict` yields `StrictScatter sp` and `rowSlotAdmission` yields `Ok` (repro build, threads on, an omp request on
the outer fold kernel). Rule: take the scatter when the source pool is materialized in scope and `cells(pool) <
logical reads / 1.5` (rank 2: 3,133,756 vs 6,265,009; rank 3: 848,046 vs 5,000,211), else the row slots; a TILE_TUNE key
`scatter_mt=auto|rows|scatter` (default from the A/B) keeps both reachable. Legality: unchanged -- both forms are bitwise
the serial program under REPRO + CONTRACT=off (W2-A's 52 x 8 raw-bit block and G35's identity cases already prove each);
the threaded producer is untouched (its own map licence). Admission: `planStrict`'s (IRTotality whitelist for the summand
and every level's kernel, exact seeds, positive static extent, a materialized pool in scope -- the declines at
`CodeGenBinding.fs:4240-4246`); no reordering of kernel applications is introduced, so the row-slot outer-kernel audit
is no longer needed on that path. Refusals: as today. Lanes: C++ only (LLVM is serial and already takes the scatter).
Record: `fold-form ... ordered orbit scatter (repro; serial behind a threaded producer; preferred over threaded complete
rows: P pool cells vs L logical reads)`. A threaded scatter is NOT proposed here: a destination-block pull form is
strict but reads the pool twice (6.27 M contiguous reads, ~same bytes/s as the serial push at T=8 on this box: `rd.cpp`
24 MB at 33 GB/s), so a parallel strict form belongs to D's producer-side endpoint schedule.

**A2 (row-view folds).** Shape: `reduce(method_for(range<Y>) <@> lambda(i) -> reduce(u(i)[, op[, s]]), K_out, s_out)`
with `u` a stored, non-deferred dense rank-2 binding, `u(i)` an `IRIndex` row view subscripted by the level parameter of
exactly `Y`, and the inner `IRReduce` over that view. Repro: `isCompleteFold` also accepts `IRReduce (IRIndex (IRVar u,
[IRVar i]), k, s)`; `IRTotality.walk` gains an `IRReduce` arm (operand = such a row view of a non-deferred array, kernel
via `auditCallable`, seed total). Licensed: in the Path B chunk body, a complete inner fold over a contiguous row view
with builtin `(+)` renders as `blade_fold::sum<T,K>(row, n)` (the `sq`/`dot` family), and the chunk's row loop carries no
`omp simd` when its body is a complete fold (each thread's partial is then an ascending chain over its rows -- a
licensed association change, inside tolerance). Admission: `scheduleMayWriteIn` over the row kernel, the outer kernel and
seed (`u` must not be written anywhere in the nest; a view of a writing map is G41-class and declines), plus the existing
whitelist audit of `K_out`. Lanes: C++ (threads); LLVM unaffected; interpreter unaffected.

**A4 (retention).** Make `kMaxBlock` / `kBudget` build defines (`-DBLADE_POOL_MAX_BLOCK`, `-DBLADE_POOL_BUDGET`, as the
w3 instrument did; keep `kMaxBlock <= kBudget`) passed by `Build.fs` from `BLADE_POOL_RETAIN` (env + toolchain key);
retained blocks are released at thread exit. No value or schedule changes; both lanes share the allocator contract. The
harness's recorded peak working set changes only when later allocations reuse the pages. This is the user's call.

**A5 (lagged strict row folds).** In `genRecStepTimeLoop`'s strict branch with `parallelRows`: an array `done[t]`
(atomic counters, one per row). After writing its chunk of row t a worker does `fetch_add(done[t], 1, release)`; worker
w owns rows t with `t mod T == w`; at the top of each step it polls its oldest pending owned row t' (acquire): if
`done[t'] == T` it runs the row kernel K on the COMPLETE row into `slot[t']`, otherwise it continues producing; after the
time loop it drains its remaining rows (producers never wait on folds, so no deadlock). The time chain over the slots
stays after the team, ascending from its seed. Strict: every row chain is K's own text on final values, one thread --
bitwise the post-pass. Admission: E-G5's (fresh rank-2 buffer, same-x reads of earlier rows, `scheduleMayWrite` over
slice and time-loop body with the buffer bound locally, no `where repro` splice) + G35's gate (the OUTER fold kernel's
own `where omp`, `rowSlotWalk` on K and F) + new: K reads only row t' of the buffer. Lanes: C++ only.

**A7c (producer-epilogue legs).** Shape (after W1-D's one-level inlining): a rank-1 forced map `y2 = map(zip(x, y)) k |>
compute` whose checksum B1 already streams, and sibling folds `reduce(x, op1)`, `reduce(y, op2)` over the producer's
INPUT operands later in the region. Rewrite: the producer loop carries the extra legs (each its own chain or P1 lane
state, its exact seed -- W1-D's `-0.0` identity for unseeded legs), fed by the values it already loads; the legs' results
bind where the original folds were. Strict in repro (each chain ascending i, the producer's order), licensed with
per-leg licences (never unioned, G7 policy). Refusals: a leg over an array the producer writes; a writing leg (G43); a
threaded producer in repro (the legs would split chains); an intervening binding that writes x or y. Admission:
`scheduleMayWriteIn` over the producer kernel, each leg's kernel and seed, and every binding the legs move across.
Both lanes (serial reference is LLVM): the hot-row / flat-epilogue twins.

**A7ab (generator phase).** (b) An interval lattice over Int64 expressions in a pure scalar callee after inlining into
a map over `range<I>` with literal arguments; when both operands of `*` are proved in [0, 2^31) emit an unsigned
32x32->64 multiply, and `% 2^k` / `/ 2^k` of a proved-nonnegative operand as a mask / shift -- identical integers, so
no pin moves; C++ (`CodeGenExprSupport.renderContractBinOp`) and LLVM (`EmitLlvm` int ops; `blade_llvm_shim.c` untouched);
the formalism 2.4 contract is unchanged (no division by zero is possible: divisors are nonzero literals). (a) Two
co-indexed forced generator maps (function results, forced in the callee) and their consumer legs fused into one loop
that stores both inputs and feeds every leg; needs cross-function region provenance (item B's seam) -- L.

**A8 (positions elision).** When every read of `y = compound(x, keep)` and of its index type is a storage-order scan
(`reduce`, `prodsum`, `extents`, storage-order print), with no keyed / partial / wildcard read and no escape of the index
identity, B4's fused fill skips `rank_to_tuple`; `y`'s pool and `keep` stay stored. Both lanes
(`index_types.h` compound, `EmitLlvm` compound init / `blade_compact1_rank`).

## 6. Implementation design

| item | files / functions (current lines) | plan record | knob (registration) | tests (`blade test` key) | docs |
|---|---|---|---|---|---|
| A1 | `CodeGenBinding.fs:4440-4480` (dispatch), `OrbitFold.fs:865-875` (records) | `fold-form` scatter-preferred line | TILE_TUNE `scatter_mt` (suiteClearedKnobs via TILE_TUNE) | `orbit-scatter` + mt8 cases (T=1/3/8 raw bits vs serial; form asserted); `omp` row-slot cases on SymIdx sources re-pinned | CLAUDE.md TILE_TUNE row |
| A2 | `CodeGenBinding.fs:71-120` (`rowSlotAdmission`), `IRTotality.fs:101-245`, Path B body (`CodeGenBinding.fs:4900-4930` chunk emission, `CodeGenExpr.fs:1559`) | `threaded complete rows (repro): N row-view folds`; `fold form: threaded(Path B chunks, blade_fold::sum per row)` | rides `BLADE_FOLD_ROW_SLOTS` / `BLADE_FOLD_LANES` | `omp` (omp-reduce row-view identity T=1/4/7), `fold-lanes`; a pin on stencil's spelling | G48 row closed |
| A4 | `nested_array_utilities.hpp:104-115`, `Build.fs:107` | none (runtime) -- `blade plan` prints the retention in the build line | `BLADE_POOL_RETAIN` (suiteClearedKnobs; exe cache keys on flags) | a runtime case asserting no release before exit | env-table row |
| A5 | w3 `CodeGenBinding.fs:6262-6310`, `Optimize.RecStepTable` w3 :2362 | `rec-step-parallel ... strict in-team complete-row folds (lagged)` | TILE_TUNE `rec_rows=post|team` | `rec-step` (repro raw bits T=1/3/8 + thread limit), recursive-arrays/033 | E-G5 row |
| A7c | `Optimize.shareSiblingFoldsModule:3906`, `planHotRowFolds:2324` (B1 arm), `CodeGenLoopNest.tryGenFlatEpilogueNest:2577`, `EmitLlvm` flat epilogue | `sibling-folds ... legs on the producer epilogue of y2` | `BLADE_EPILOGUE_LEGS` (optimizerGates) | sibling-folds/019+, `cell-epilogue` raw bits, `opt-diff` | env-table row |
| A7b | `CodeGenExprSupport.renderContractBinOp`, `EmitLlvm` int binops, a small `IntRange` module before `CodeGenExprSupport` in the fsproj | `int-range-lowering v1 applied` per callee | `BLADE_INT_RANGE` (suiteClearedKnobs) | arithmetic corpus (wrap / panic pins unchanged), `llvm`, `interp` | formalism 2.4 note (implementation only) |
| A8 | `CodeGenBinding.genCompoundInitBinding:2133`, `CodeGenExpr.materializeMaskForm:2050`, `index_types.h:95-119`, `EmitLlvm` compound init | `compaction-fusion ... positions elided (no keyed consumer)` | rides `BLADE_COMPACTION_FUSION` + TILE_TUNE `compact_pos=auto|always` | index-types 522+ (a keyed consumer keeps positions) | -- |

Interpreter twins: none needed for A1, A2, A4, A5, A7b, A8 (values identical; repro forms bitwise the serial program);
A7c licensed legs are checked at 1e-12 vs gate-off, repro bitwise. Every optimizer-level change is in `opt-diff`.

## 7. Effort, risks, and the experiment that drops each

| item | effort | main risk | DROP if |
|---|---|---|---|
| A1 | S | the serial scatter's 25 MB read is slower at T=8 than measured serially (other cores idle) | `BLADE_FOLD_ROW_SLOTS=0` is not >= 10% faster on both mt8 repro cells |
| A2 | S | the licensed slowness is elsewhere (first touch, nested-pointer rows); `-fopt-info-vec` may show g++ ignored the outer `omp simd` | repro: the `stencil_d` spelling does not cut >= 1.5 ms; licensed: the patched checksum phase stays > 1.2 ms |
| A4 | S | footprint policy; hides a real teardown cost users pay | the user declines; or the harness A/B shows < 2 ms on recur mt8 |
| A5 | M | polling overhead in the step loop; rows evicted from L3 if a worker lags | patched post-pass cost does not fall below 1 ms |
| A7c | M | B1 lane state + three legs exceeds the register budget (spills) | hand-patched gain < 0.8 ms on serial repro saxpy |
| A8 | M | a consumer needing positions slips the census (print, `--print`, provider writes) | net gain < 0.25 ms |
| A7b | M | interval analysis soundness (wrap); perceived as benchmark-specific | patched `gen` gives < 10% on the dot input-only twin |
| A7a | L | cross-function fusion of two producers; print/demand provenance (blueprint section 7) | hand-fused dot gives < 1 ms |

## 8. File-overlap list (A vs B relational, C workloads, D orbit)

| file / surface | A items | B | C | D |
|---|---|---|---|---|
| `OrbitFold.fs`, `CodeGenBinding` orbit / scatter dispatch (:3915-4480), `tests` orbit-scatter | A1 | -- | -- | HIGH (land A1 first) |
| `IRTotality.fs` | A2 | -- | -- | medium (strict whitelist) |
| `Optimize.fs` sibling folds / hot-row / RecStepTable | A5, A7c | HIGH (region passes) | medium (N01, N21) | -- |
| `Lowering.fs` force points (`:474`), provenance | A7a | HIGH | -- | -- |
| compound / mask (`index_types.h`, `materializeMaskForm`, `EmitLlvm` compound) | A8 | HIGH (aggregate-only grouping, filter fusion) | low | -- |
| `CodeGenExprSupport` int ops, `EmitLlvm` int ops | A7b | low | low | -- |
| `nested_array_utilities.hpp` pool | A4 | -- | medium (stateful workloads' teardown) | -- |
| CLAUDE.md env table, `CliSelfTests.suiteClearedKnobs`, `optimizerGates` | all | all | all | all (textual merges) |
| `bench/kernels/*`, harness | none (no source changes; A2 keeps stencil's spelling) | new R1-R4 dirs | new N-dirs | new workloads |

## 9. Open questions for Codex

1. A1: is the cross-session estimate right that the serial scatter costs ~1.2 ms (pairkernel) / ~0.3 ms (comoment3)
   behind a threaded producer, against G35's ~3.2 / ~1.5 ms? The zero-code A/B decides; is there any program where the
   scatter is chosen but its pool is NOT materialized at the consumer (the C++ decline paths at `:4240-4246`)?
2. A1: the rejected destination-block pull scatter -- is the "2x reads, same bandwidth" argument enough to leave the
   strict parallel form to D, or is there a 1x-read strict parallel consumer of a stored pool we missed?
3. A2: is stencil's licensed checksum slow because g++ outer-loop-vectorizes the `omp simd` row loop (gathers) or
   because of the scalar 8-lane IIFE? Is changing that licensed tree (8 scalar lanes -> `sum<double,8>`, 32 lanes)
   acceptable without a new knob arm?
4. A5: memory ordering of the counters in a barrier-free team (release per chunk, acquire per fold) and whether a K
   that reads earlier rows (not only its own) can ever reach this branch.
5. A4: is a bounded retention ceiling a fair runtime allocator policy or a benchmark-shaped one, given the experts
   free after their stop line? Should the harness record it as a separate arm?
6. A7b: soundness of an Int64 interval lattice under `-fwrapv` semantics; does the rewrite need a formalism note?
   Is optimizing the shared input generator (identical in every arm, inside every timer) fair if totals and
   incrementals are both reported?
7. A7c: lifting the W1-B/W1-D surface rule -- which hazard did it guard (two passes claiming one leg), and does the
   B1 lane state plus extra legs keep W1-D's -0.0 seed semantics in the licensed P1 form?
8. A8: does the storage-order print of a compound (G22) or `--print` need `rank_to_tuple`?
9. Gap numbering: renumber wave 3's G48 / G49 or vigilant-hellman's G48-G50?
10. Rank 3 vs 4: should B (no current cells, asymptotic) outrank A4 (a user decision with measured 3-5 ms)?
